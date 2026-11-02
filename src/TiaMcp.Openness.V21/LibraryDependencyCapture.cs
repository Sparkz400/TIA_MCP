using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.Library.Types;

namespace TiaMcp.Openness.V21
{
    internal sealed class LibraryDependencyCapture
    {
        internal LibraryTypeVersion[] Versions { get; }
        internal string[] VersionGuids { get; }

        private LibraryDependencyCapture(LibraryTypeVersion[] versions, string[] guids)
        {
            Versions = versions;
            VersionGuids = guids;
        }

        internal static LibraryDependencyCapture Read(LibraryTypeVersion owner, Func<bool> admitRead = null)
        {
            if (owner == null) throw new BridgeException("DependencyUnresolved", "A native library version is required.");
            void Admit()
            {
                if (admitRead != null && !admitRead()) throw new BridgeException("TraversalLimit", "The shared library dependency read budget is exhausted.");
            }
            Admit();
            var source = owner.Dependencies;
            if (source == null) throw new BridgeException("DependencyUnresolved", "Native library dependencies are unavailable.");
            var captured = new List<LibraryTypeVersion>();
            using (var iterator = source.GetEnumerator())
                while (captured.Count <= 1000)
                {
                    Admit();
                    if (!iterator.MoveNext()) break;
                    captured.Add(iterator.Current);
                }
            var versions = captured.ToArray();
            if (versions.Length > 1000 || versions.Any(v => v == null))
                throw new BridgeException("DependencyUnresolved", "Native library dependencies exceed 1000 entries or contain an unresolved version.");
            // Freeze this observation; any later freshness check must compare rather than replace it.
            var guids = versions.Select(v => v.Guid).ToArray();
            if (guids.Any(g => g == Guid.Empty)) throw new BridgeException("DependencyUnresolved", "Native dependency version identity is unavailable.");
            return new LibraryDependencyCapture(versions, guids.Select(g => g.ToString("D")).ToArray());
        }

        internal static void RequireUnchanged(LibraryTypeVersion owner, string[] expected)
        {
            var current = Read(owner);
            if (expected == null || !expected.SequenceEqual(current.VersionGuids, StringComparer.Ordinal))
                throw new BridgeException("DependencyConflict", "Native library dependencies changed during export; prepare a fresh plan.");
        }

        internal static string[] InstantiationOrder(IEnumerable<string> selected, JArray closures)
        {
            var remaining = selected.ToList();
            var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string id in remaining)
            {
                var rows = closures.FirstOrDefault(x => x.Value<string>("rootVersionGuid") == id)?["versions"] as JArray;
                var ids = rows?.Select(x => x.Value<string>("versionGuid")).ToHashSet(StringComparer.Ordinal);
                if (ids == null || !ids.Contains(id) || ids.Any(string.IsNullOrEmpty))
                    throw new BridgeException("DependencyUnresolved", "Every selected version needs its complete previewed dependency closure.");
                ids.Remove(id); dependencies.Add(id, ids);
            }
            var ordered = new List<string>();
            while (remaining.Count != 0)
            {
                string next = remaining.FirstOrDefault(id => !dependencies[id].Overlaps(remaining));
                if (next == null) throw new BridgeException("DependencyConflict", "Selected library roots have a dependency cycle; no safe instantiation order is available.");
                ordered.Add(next); remaining.Remove(next);
            }
            return ordered.ToArray();
        }
    }
}
