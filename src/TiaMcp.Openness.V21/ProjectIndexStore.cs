#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Indexing
{
    // No Siemens objects survive capture. A cursor only addresses one immutable
    // snapshot, one query and this worker lifetime; it is never a live-object handle.
    internal sealed class ProjectIndexStore
    {
        internal const int MaximumRows = 50000;
        internal const long MaximumBytes = 32L * 1024 * 1024;
        private readonly Dictionary<string, Snapshot> _snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, Row[]>> _views = new Dictionary<string, Dictionary<string, Row[]>>(StringComparer.Ordinal);
        private readonly Func<DateTime> _clock;
        private readonly byte[] _key = new byte[32];
        internal ProjectIndexStore(Func<DateTime> clock = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(_key);
        }

        internal Snapshot Capture(IEnumerable<Row> source, string projectPath, string revision, bool scopeComplete, string[] warnings, int lifetimeSeconds = 300)
        {
            if (lifetimeSeconds < 30 || lifetimeSeconds > 900) throw Error("InvalidRequest", "Snapshot lifetime must be 30..900 seconds.");
            var rows = new List<Row>();
            long bytes = 0;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in source)
            {
                if (row == null || string.IsNullOrEmpty(row.Path) || row.Json == null) throw Error("InvalidIndexRow", "Every row requires a canonical path and payload.");
                if (!paths.Add(row.Path)) throw Error("AmbiguousIndexIdentity", "Duplicate canonical index path: " + row.Path);
                int size = Encoding.UTF8.GetByteCount(row.Json);
                if (size > 65536) throw Error("IndexCapacity", "One index row exceeds 64 KiB.");
                bytes += size + Encoding.UTF8.GetByteCount(row.Path + row.Name + row.Kind + row.PlcPath + row.ScopePath);
                if (rows.Count >= MaximumRows || bytes > MaximumBytes) throw Error("IndexCapacity", "Index capacity exceeded; select a smaller capture scope.");
                rows.Add(new Row(row.Path, row.Kind, row.Name, row.PlcPath, row.ScopePath, row.Json));
            }
            var now = _clock();
            foreach (var id in _snapshots.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray()) Release(id);
            while (_snapshots.Count >= 2) Release(_snapshots.Values.OrderBy(snapshot => snapshot.CreatedAtUtc).First().Id);
            var result = new Snapshot(Guid.NewGuid().ToString("N"), projectPath, revision, rows.OrderBy(row => row.Path, StringComparer.Ordinal).ToArray(),
                scopeComplete, (string[])warnings.Clone(), now, now.AddSeconds(lifetimeSeconds), bytes);
            _snapshots.Add(result.Id, result);
            _views.Add(result.Id, new Dictionary<string, Row[]>());
            return result;
        }

        internal Snapshot Get(string id)
        {
            Snapshot snapshot;
            if (id == null || !_snapshots.TryGetValue(id, out snapshot)) throw Error("IndexNotFound", "Index is absent or evicted. Capture a new snapshot in this worker session.");
            if (snapshot.ExpiresAtUtc <= _clock()) { Release(id); throw Error("IndexExpired", "Index expired; capture a fresh snapshot."); }
            if (snapshot.InvalidatedBy != null) throw Error("IndexInvalidated", "Index invalidated before operation: " + snapshot.InvalidatedBy + ". Capture again.");
            return snapshot;
        }

        internal void Invalidate(string reason) { foreach (var snapshot in _snapshots.Values) snapshot.InvalidatedBy = reason; }
        internal bool Release(string id) { if (id == null) return false; _views.Remove(id); return _snapshots.Remove(id); }

        internal Page Query(string id, string text, string kind, string plcPath, string scopePath, int maxItems, string cursor)
        {
            if (maxItems < 1 || maxItems > 200) throw Error("InvalidRequest", "Page size must be 1..200.");
            if ((text ?? "").Length > 256 || (kind ?? "").Length > 80 || (plcPath ?? "").Length > 4096 || (scopePath ?? "").Length > 4096)
                throw Error("InvalidRequest", "Index query exceeds its bounded selector lengths.");
            var snapshot = Get(id);
            string query = Hash(Part(text) + Part(kind) + Part(plcPath) + Part(scopePath));
            int offset = cursor == null ? 0 : Decode(cursor, snapshot, query);
            Row[] matches;
            var views = _views[snapshot.Id];
            if (!views.TryGetValue(query, out matches))
            {
                matches = snapshot.Rows.Where(row => (string.IsNullOrEmpty(kind) || row.Kind == kind)
                    && (string.IsNullOrEmpty(plcPath) || row.PlcPath == plcPath)
                    && (string.IsNullOrEmpty(scopePath) || row.ScopePath == scopePath)
                    && (string.IsNullOrEmpty(text) || row.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || row.Path.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                if (views.Count >= 16) views.Clear();
                views.Add(query, matches);
            }
            if (offset < 0 || offset > matches.Length) throw Error("InvalidCursor", "Cursor position is outside this query.");
            var rows = new List<Row>();
            int bytes = 0;
            for (int i = offset; i < matches.Length && rows.Count < maxItems; i++)
            {
                int rowBytes = Encoding.UTF8.GetByteCount(matches[i].Json);
                if (rows.Count > 0 && bytes + rowBytes > 512 * 1024) break;
                rows.Add(matches[i]); bytes += rowBytes;
            }
            int next = offset + rows.Count;
            return new Page { Snapshot = snapshot, Rows = rows.ToArray(), Offset = offset, Total = matches.Length,
                NextCursor = next < matches.Length ? Encode(snapshot, query, next) : null, PayloadBytes = bytes };
        }

        private string Encode(Snapshot snapshot, string query, int offset)
        {
            string payload = snapshot.Id + ":" + query + ":" + offset.ToString(CultureInfo.InvariantCulture);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload + ":" + Sign(payload)));
        }
        private int Decode(string cursor, Snapshot snapshot, string query)
        {
            try
            {
                if (cursor.Length > 512) throw new FormatException();
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
                if (parts.Length != 4 || parts[0] != snapshot.Id || parts[1] != query) throw new FormatException();
                string expected = Sign(string.Join(":", parts.Take(3)));
                int difference = expected.Length ^ parts[3].Length;
                for (int i = 0; i < Math.Min(expected.Length, parts[3].Length); i++) difference |= expected[i] ^ parts[3][i];
                int offset;
                if (difference != 0 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset)) throw new FormatException();
                return offset;
            }
            catch (Exception error) when (error is FormatException || error is ArgumentException)
            { throw Error("InvalidCursor", "Cursor does not belong to this snapshot, query or worker. Repeat the original filters with the returned cursor."); }
        }
        private string Sign(string text) { using (var hmac = new HMACSHA256(_key)) return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        internal static string Hash(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static string Part(string value) => value == null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        private static IndexException Error(string code, string message) => new IndexException(code, message);

        internal sealed class Row
        {
            internal readonly string Path, Kind, Name, PlcPath, ScopePath, Json;
            internal Row(string path, string kind, string name, string plcPath, string scopePath, string json)
            { Path = path; Kind = kind; Name = name ?? ""; PlcPath = plcPath; ScopePath = scopePath; Json = json; }
        }
        internal sealed class Snapshot
        {
            internal readonly string Id, ProjectPath, Revision;
            internal readonly ReadOnlyCollection<Row> Rows;
            internal readonly bool ScopeComplete;
            internal readonly ReadOnlyCollection<string> Warnings;
            internal readonly DateTime CreatedAtUtc, ExpiresAtUtc;
            internal readonly long PayloadBytes;
            internal string InvalidatedBy;
            internal Snapshot(string id, string project, string revision, Row[] rows, bool complete, string[] warnings, DateTime created, DateTime expires, long bytes)
            { Id = id; ProjectPath = project; Revision = revision; Rows = Array.AsReadOnly(rows); ScopeComplete = complete; Warnings = Array.AsReadOnly(warnings); CreatedAtUtc = created; ExpiresAtUtc = expires; PayloadBytes = bytes; }
        }
        internal sealed class Page { internal Snapshot Snapshot; internal Row[] Rows; internal int Offset, Total, PayloadBytes; internal string NextCursor; }
        internal sealed class IndexException : Exception { internal readonly string Code; internal IndexException(string code, string message) : base(message) { Code = code; } }
    }
}
