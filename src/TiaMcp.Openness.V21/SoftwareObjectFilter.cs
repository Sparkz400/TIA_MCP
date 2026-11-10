#nullable disable
using System;
using System.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Name and group filters for project_inventory and software_inspect, applied to object paths before result bounding.
    /// groupPath is either a group path as returned by software_inspect (it contains "/plc/") or a '/'-separated chain of group
    /// names from the top level, e.g. "Station 3000/Downholder", matched below blocks, tags, types and technology of any scope.
    /// namePrefix matches object names case-insensitively.</summary>
    internal sealed class SoftwareObjectFilter
    {
        private static readonly string[] Areas = { "blocks", "tags", "types", "technology" };
        private readonly string _pathPrefix, _chain, _namePrefix;

        internal bool Active => _pathPrefix != null || _chain != null || _namePrefix != null;
        internal string GroupPath { get; }
        internal string NamePrefix => _namePrefix;

        internal SoftwareObjectFilter(string groupPath, string namePrefix)
        {
            if (groupPath != null && (groupPath.Trim().Trim('/').Length == 0 || groupPath.Length > 1024)) throw new BridgeException("InvalidRequest", "groupPath must be 1-1024 characters.");
            if (namePrefix != null && (namePrefix.Length == 0 || namePrefix.Length > 256)) throw new BridgeException("InvalidRequest", "namePrefix must be 1-256 characters.");
            GroupPath = groupPath;
            _namePrefix = namePrefix;
            if (groupPath == null) return;
            if (groupPath.Contains("/plc/")) _pathPrefix = groupPath.TrimEnd('/');
            else
            {
                var names = groupPath.Split('/').Select(n => n.Trim()).ToArray();
                if (names.Any(n => n.Length == 0)) throw new BridgeException("InvalidRequest", "groupPath group names must not be empty.");
                _chain = string.Concat(names.Select(n => "/groups/" + Uri.EscapeDataString(n)));
            }
        }

        internal bool Matches(string path, string name)
        {
            if (_namePrefix != null && (name == null || !name.StartsWith(_namePrefix, StringComparison.OrdinalIgnoreCase))) return false;
            if (_pathPrefix != null) return path != null && path.StartsWith(_pathPrefix + "/", StringComparison.Ordinal);
            if (_chain != null) return path != null && Areas.Any(area => path.IndexOf("/" + area + _chain + "/", StringComparison.Ordinal) >= 0);
            return true;
        }

        /// <summary>Whether a group path lies inside the selected group (namePrefix does not apply to groups).</summary>
        internal bool ContainsGroup(string groupPath)
        {
            if (_pathPrefix != null) return groupPath == _pathPrefix || groupPath.StartsWith(_pathPrefix + "/", StringComparison.Ordinal);
            if (_chain != null) return Areas.Any(area => (groupPath + "/").IndexOf("/" + area + _chain + "/", StringComparison.Ordinal) >= 0);
            return true;
        }
    }

    /// <summary>The user-group paths software_inspect lists per scope. They have their own bound (the value of maxItems, counted
    /// apart from it), so a project with many groups cannot use up the item budget before a requested section is read. With
    /// include the caller named the sections to read; group paths are not one of them and are left out.</summary>
    internal sealed class SoftwareGroupListing
    {
        private readonly SoftwareObjectFilter _filter;
        private readonly int _maximum;

        internal bool Listed { get; }
        internal int Count { get; private set; }
        internal bool Truncated { get; private set; }

        internal SoftwareGroupListing(SoftwareObjectFilter filter, bool includeGiven, int maximum)
        { _filter = filter; _maximum = maximum; Listed = !includeGiven; }

        /// <summary>The paths of one scope that are listed; stops reading the source at the bound.</summary>
        internal System.Collections.Generic.List<string> Take(System.Collections.Generic.IEnumerable<string> groupPaths, out bool truncated)
        {
            var listed = new System.Collections.Generic.List<string>();
            truncated = false;
            if (!Listed) return listed;
            foreach (string path in groupPaths)
            {
                if (!_filter.ContainsGroup(path)) continue;
                if (Count >= _maximum) { truncated = Truncated = true; break; }
                Count++;
                listed.Add(path);
            }
            return listed;
        }

        internal string Coverage => !Listed ? "Group paths are not listed when include is given; read without include to list them."
            : "Bounded user-group paths, including empty groups, counted apart from maxItems" + (Truncated ? "; the list is incomplete, see groupTruncation" : "") +
              "; native system-group objects are included in their object inventories.";
    }
}
