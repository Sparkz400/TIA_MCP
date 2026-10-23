#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Model
{
    // Canonical inventory paths are identities within one project revision, not durable GUIDs.
    internal static class ObjectIdentity
    {
        internal static bool Matches(string path, string name, string scopePath, string wantedPath, string wantedName, string wantedScope) =>
            (string.IsNullOrWhiteSpace(wantedPath) || string.Equals(path, wantedPath, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(wantedName) || string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(wantedScope) || string.Equals(scopePath, wantedScope, StringComparison.Ordinal));

        internal static string PlcPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            // Parse structural positions. A device, item or user group may itself be
            // named "plc"; searching for a marker substring would select the wrong PLC.
            string[] parts = path.Split('/');
            if (parts.Length < 7 || parts[0] != "" || parts[1] != "projects" || parts[2].Length == 0) return null;
            int position = 3;
            while (position + 1 < parts.Length && parts[position] == "groups") position += 2;
            if (position + 1 >= parts.Length || (parts[position] != "devices" && parts[position] != "ungrouped")) return null;
            position += 2;
            while (position + 1 < parts.Length && parts[position] == "items") position += 2;
            if (position + 1 >= parts.Length || parts[position] != "plc" || parts[position + 1].Length == 0) return null;
            return string.Join("/", parts.Take(position + 2));
        }

        internal static string ScopePath(string plcPath, string objectPath)
        {
            if (string.IsNullOrWhiteSpace(plcPath) || string.IsNullOrWhiteSpace(objectPath)) return null;
            if (objectPath == plcPath) return plcPath;
            if (!objectPath.StartsWith(plcPath + "/", StringComparison.Ordinal)) return null;
            foreach (string marker in new[] { "/units/", "/safetyUnits/" })
            {
                string prefix = plcPath + marker;
                if (!objectPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int end = objectPath.IndexOf('/', prefix.Length);
                return end < 0 ? objectPath : objectPath.Substring(0, end);
            }
            return plcPath;
        }

        internal static string ParentPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var parts = path.Split('/');
            int namespaceIndex = NamespaceIndex(path, parts);
            if (namespaceIndex >= 0) return string.Join("/", parts.Take(namespaceIndex));
            int split = path.LastIndexOf('/');
            return split > 0 ? path.Substring(0, split) : null;
        }

        internal static string ObjectPath(string parent, string name, string nameSpace) => parent +
            (string.IsNullOrEmpty(nameSpace) ? "" : "/namespaces/" + Uri.EscapeDataString(nameSpace)) + "/" + Uri.EscapeDataString(name ?? "");

        internal static string NamespaceFromPath(string path)
        {
            var parts = (path ?? "").Split('/');
            int namespaceIndex = NamespaceIndex(path, parts);
            return namespaceIndex >= 0 ? Uri.UnescapeDataString(parts[namespaceIndex + 1]) : "";
        }

        internal static bool NamespaceCaseAlias(string existing, string requested) =>
            !string.Equals(existing ?? "", requested ?? "", StringComparison.Ordinal) &&
            string.Equals(existing ?? "", requested ?? "", StringComparison.OrdinalIgnoreCase);

        private static int NamespaceIndex(string path, string[] parts)
        {
            string plc = PlcPath(path), scope = ScopePath(plc, path);
            if (scope == null) return -1;
            int position = scope.Split('/').Length;
            if (position >= parts.Length || (parts[position] != "blocks" && parts[position] != "types")) return -1;
            position++;
            while (position + 1 < parts.Length && (parts[position] == "groups" || parts[position] == "systemGroups")) position += 2;
            return position + 2 == parts.Length - 1 && parts[position] == "namespaces" ? position : -1;
        }

        internal static bool IsGeneratedPath(string path) => path != null &&
            (path.IndexOf("/blocks/systemGroups/", StringComparison.Ordinal) >= 0 || path.IndexOf("/types/systemGroups/", StringComparison.Ordinal) >= 0);

        internal static string KindFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string plc = PlcPath(path);
            if (plc == null) return path.StartsWith("/projects/", StringComparison.Ordinal) && path.IndexOf('/', 10) < 0 ? "project" : "hardware";
            if (path == plc) return "plc";
            string scope = ScopePath(plc, path);
            if (scope == path) return scope.StartsWith(plc + "/safetyUnits/", StringComparison.Ordinal) ? "safetyUnit" : "softwareUnit";
            string tail = path.Substring(scope.Length);
            if (tail.StartsWith("/blocks/", StringComparison.Ordinal)) return "block";
            if (tail.StartsWith("/types/", StringComparison.Ordinal)) return "type";
            if (tail.StartsWith("/tags/", StringComparison.Ordinal)) return "tagTable";
            if (tail.StartsWith("/technology/", StringComparison.Ordinal)) return "technology";
            return null;
        }

        internal static string SingleScope(IEnumerable<string> scopes, string explicitScope, string fallback)
        {
            var distinct = scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (distinct.Length > 1) throw new InvalidOperationException("Targets span multiple software scopes; split the change and specify scopePath.");
            if (!string.IsNullOrWhiteSpace(explicitScope))
            {
                if (distinct.Length == 1 && distinct[0] != explicitScope) throw new InvalidOperationException("The target is outside the selected software scope.");
                return explicitScope;
            }
            return distinct.FirstOrDefault() ?? fallback;
        }
    }
}
