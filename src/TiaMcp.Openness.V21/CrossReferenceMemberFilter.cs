#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TiaMcp.Openness.V21
{
    /// <summary>Member-level filter for native cross-reference locations, e.g. memberPath "Real[200..220]" on a global DB.
    /// Pure text matching of TIA's referenced-as names and source member chains; it never resolves runtime indices.
    /// Match kinds: "exact" (constant index inside the range, or the member itself), "indirect" (a variable or expression index
    /// that may hit the range), "wholeArray" (the array is accessed without an index, e.g. passed as a parameter) and
    /// "container" (a containing structure is accessed as a whole). Only "exact" proves a use; the others are possible uses.</summary>
    internal sealed class CrossReferenceMemberFilter
    {
        internal sealed class Segment
        {
            internal string Name;
            internal string[] Indices; // raw index texts; null when the segment has no index
        }

        private sealed class Range
        {
            internal long Low, High;
        }

        private readonly string[] _names;
        private readonly Range[][] _ranges; // per segment: null, or one range per dimension

        internal string MemberPath { get; }

        internal CrossReferenceMemberFilter(string memberPath)
        {
            if (string.IsNullOrWhiteSpace(memberPath) || memberPath.Length > 512) throw new BridgeException("InvalidRequest", "memberPath must be 1-512 characters, e.g. Real[210], Real[200..220] or Station.Downholder.Enable.");
            var segments = Parse(memberPath.Trim());
            if (segments == null || segments.Length == 0 || segments.Any(s => s.Name.Length == 0))
                throw new BridgeException("InvalidRequest", "memberPath must be dot-separated member names with optional constant [index] or [low..high] ranges.");
            _names = segments.Select(s => s.Name).ToArray();
            _ranges = segments.Select(s => s.Indices?.Select(ParseRange).ToArray()).ToArray();
            MemberPath = memberPath.Trim();
        }

        private static Range ParseRange(string text)
        {
            var parts = text.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length > 2 || !long.TryParse(parts[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long low))
                throw new BridgeException("InvalidRequest", "memberPath indices must be constant integers or low..high ranges: " + text);
            long high = low;
            if (parts.Length == 2 && !long.TryParse(parts[1].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out high))
                throw new BridgeException("InvalidRequest", "memberPath indices must be constant integers or low..high ranges: " + text);
            if (high < low) throw new BridgeException("InvalidRequest", "memberPath range low must not exceed high: " + text);
            return new Range { Low = low, High = high };
        }

        /// <summary>Best match kind among the candidate texts, or null. "exact" beats every possible-use kind.</summary>
        internal string Match(IEnumerable<string> candidates)
        {
            string best = null;
            foreach (string candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                string kind = MatchOne(candidate);
                if (kind == "exact") return kind;
                if (kind != null && (best == null || Rank(kind) < Rank(best))) best = kind;
            }
            return best;
        }

        private static int Rank(string kind) => kind == "indirect" ? 1 : kind == "wholeArray" ? 2 : 3;

        private string MatchOne(string text)
        {
            var segments = Parse(text.Trim());
            if (segments == null) return null;
            string best = null;
            // The pattern is relative to the queried object, so it may start at any segment (TIA texts can carry the DB name first).
            for (int start = 0; start < segments.Length; start++)
            {
                string kind = MatchAt(segments, start);
                if (kind == "exact") return kind;
                if (kind != null && (best == null || Rank(kind) < Rank(best))) best = kind;
            }
            return best;
        }

        private string MatchAt(Segment[] candidate, int start)
        {
            string result = "exact";
            for (int i = 0; i < _names.Length; i++)
            {
                if (start + i >= candidate.Length) return i == 0 ? null : "container";
                var segment = candidate[start + i];
                if (!string.Equals(segment.Name, _names[i], StringComparison.OrdinalIgnoreCase)) return null;
                var ranges = _ranges[i];
                if (ranges == null) continue;
                if (segment.Indices == null)
                {
                    // The array itself is accessed; any deeper pattern segment cannot be checked either.
                    if (start + i == candidate.Length - 1) return "wholeArray";
                    return null;
                }
                if (segment.Indices.Length != ranges.Length) return null;
                for (int d = 0; d < ranges.Length; d++)
                {
                    string index = segment.Indices[d].Trim();
                    if (long.TryParse(index, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                    {
                        if (value < ranges[d].Low || value > ranges[d].High) return null;
                    }
                    else result = "indirect";
                }
            }
            return result;
        }

        /// <summary>Splits on '.' outside brackets and quotes; strips quotes and the local '#' prefix. Returns null for unbalanced text.</summary>
        internal static Segment[] Parse(string text)
        {
            var segments = new List<Segment>();
            var name = new StringBuilder();
            List<string> indices = null;
            var index = new StringBuilder();
            int depth = 0; bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (depth == 0)
                {
                    if (c == '"') { quoted = !quoted; continue; }
                    if (quoted) { name.Append(c); continue; }
                    // A bit/byte slice (.%X3) addresses part of the member; the member path ends there.
                    if (c == '.' && i + 1 < text.Length && text[i + 1] == '%') { Flush(); return segments.ToArray(); }
                    if (c == '.') { Flush(); continue; }
                    if (c == '[') { depth = 1; indices = indices ?? new List<string>(); index.Clear(); continue; }
                    if (c == ']') return null;
                    if (c == '#' && name.Length == 0) continue;
                    name.Append(c);
                }
                else
                {
                    if (c == '[') depth++;
                    else if (c == ']' && --depth == 0) { indices.Add(index.ToString()); continue; }
                    if (c == ',' && depth == 1) { indices.Add(index.ToString()); index.Clear(); continue; }
                    index.Append(c);
                }
            }
            if (depth != 0 || quoted) return null;
            Flush();
            return segments.ToArray();

            void Flush()
            {
                if (name.Length == 0 && indices == null) return;
                segments.Add(new Segment { Name = name.ToString().Trim(), Indices = indices?.ToArray() });
                name.Clear(); indices = null;
            }
        }
    }
}
