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
}
