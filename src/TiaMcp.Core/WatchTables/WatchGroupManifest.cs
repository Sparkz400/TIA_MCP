#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TiaMcp.Core.WatchTables
{
    // Shared with the net48 native adapter. Canonical subtree of one watch/force table group, one sorted line per descendant:
    // "watch\t<rel>\t<semanticSha256>\t<rows>", "force\t<rel>\t<semanticSha256>", "group\t<rel>"; rel joins escaped name segments with '/'.
    public static class WatchGroupManifest
    {
        public const string Profile = "watch-group.v1";
        public static string Segment(string value) { return Uri.EscapeDataString(value ?? ""); }
        public static byte[] Build(IEnumerable<string> lines)
        { return Encoding.UTF8.GetBytes(Profile + "\n" + string.Concat(lines.OrderBy(l => l, StringComparer.Ordinal).Select(l => l + "\n"))); }
        public static string[] Lines(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes ?? new byte[0]);
            if (!text.StartsWith(Profile + "\n", StringComparison.Ordinal)) throw new FormatException("Not a watch group manifest.");
            return text.Substring(Profile.Length + 1).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // Exact subtree after one lifecycle operation on a direct child, or null with a refusal code.
        // "*" stands for the export hash of a natively created table, which only readback can reveal.
        public static string[] Expect(byte[] before, string operation, string name, string newName, string renamedSha256, out string refusal)
        {
            var lines = Lines(before).ToList(); string n = Segment(name), m = Segment(newName); refusal = null;
            Func<string, string> rel = l => l.Split('\t')[1];
            Func<string, string, bool> taken = (kinds, path) => lines.Any(l => kinds.Contains(l.Split('\t')[0]) && string.Equals(rel(l), path, StringComparison.OrdinalIgnoreCase));
            string table = lines.FirstOrDefault(l => l.StartsWith("watch\t" + n + "\t", StringComparison.Ordinal));
            switch (operation)
            {
                case "createTable":
                    if (taken("watch force", n)) refusal = "WatchTableCollision"; else lines.Add("watch\t" + n + "\t*\t0"); break;
                case "renameTable": case "deleteTable":
                    if (table == null) refusal = "WatchTableNotFound";
                    else if (operation == "deleteTable") lines.Remove(table);
                    else if (renamedSha256 == null) refusal = "InvalidRequest";
                    else if (taken("watch force", m)) refusal = "WatchTableCollision";
                    else { lines.Remove(table); lines.Add("watch\t" + m + "\t" + renamedSha256 + "\t" + table.Split('\t')[3]); }
                    break;
                case "createGroup":
                    if (taken("group", n)) refusal = "WatchGroupCollision"; else lines.Add("group\t" + n); break;
                case "renameGroup": case "deleteGroup":
                    if (!lines.Contains("group\t" + n)) refusal = "WatchGroupNotFound";
                    else if (operation == "deleteGroup")
                    { if (lines.Any(l => rel(l).StartsWith(n + "/", StringComparison.Ordinal))) refusal = "WatchGroupNotEmpty"; else lines.Remove("group\t" + n); }
                    else if (taken("group", m)) refusal = "WatchGroupCollision";
                    else lines = lines.Select(l => rel(l) == n || rel(l).StartsWith(n + "/", StringComparison.Ordinal) ? Rekey(l, n.Length, m) : l).ToList();
                    break;
                default: refusal = "UnsupportedProfile"; break;
            }
            return refusal == null ? lines.OrderBy(l => l, StringComparer.Ordinal).ToArray() : null;
        }
        public static bool Matches(string[] expected, byte[] after)
        {
            var actual = Lines(after);
            return expected != null && expected.Length == actual.Length && expected.Zip(actual, Same).All(x => x);
        }
        private static bool Same(string expected, string actual)
        {
            string[] e = expected.Split('\t'), a = actual.Split('\t');
            return e.Length == a.Length && e.Zip(a, (x, y) => x == "*" || x == y).All(x => x);
        }
        private static string Rekey(string line, int length, string name)
        { var parts = line.Split('\t'); parts[1] = name + parts[1].Substring(length); return string.Join("\t", parts); }
    }
}
