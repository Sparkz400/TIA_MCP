#nullable disable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed class TechnologyBlockMembership
    {
        private readonly Dictionary<object, string> _technology = new Dictionary<object, string>();
        private readonly HashSet<string> _technologyPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<object> _blocks = new HashSet<object>();
        private readonly HashSet<string> _blockPaths = new HashSet<string>(StringComparer.Ordinal);
        private int _mapped;

        internal void AddTechnology(object item, string path)
        {
            if (item == null || string.IsNullOrWhiteSpace(path) || _technology.Count >= 50000 ||
                _technology.ContainsKey(item) || !_technologyPaths.Add(path))
                throw new BridgeException("TraversalIncomplete", "Technology membership is missing, ambiguous or exceeds its bound.");
            _technology.Add(item, path);
        }

        internal string AddBlock(object item, string path)
        {
            if (item == null || string.IsNullOrWhiteSpace(path) || _blocks.Count >= 50000 ||
                !_blocks.Add(item) || !_blockPaths.Add(path))
                throw new BridgeException("TraversalIncomplete", "Block membership is missing, ambiguous or exceeds its bound.");
            if (!_technology.TryGetValue(item, out var technologyPath)) return null;
            _mapped++;
            return technologyPath;
        }

        internal JObject Coverage(bool truncated) => new JObject {
            ["basis"] = "nativeObjectEquality", ["scopeComplete"] = !truncated, ["atomicSnapshot"] = false,
            ["technologyObjects"] = _technology.Count, ["blockObjects"] = _blocks.Count, ["mappedBlocks"] = _mapped };
    }
}
