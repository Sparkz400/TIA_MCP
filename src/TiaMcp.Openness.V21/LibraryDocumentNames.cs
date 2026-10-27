using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Core;

namespace TiaMcp.Openness.V21
{
    internal static class LibraryDocumentNames
    {
        // Exactly one of documentName or documentNames (1..100 distinct names, the library selection limit); one type each.
        internal static string[] Read(JObject payload)
        {
            var single = payload["documentName"];
            var list = payload["documentNames"] as JArray;
            if (payload["documentNames"] == null && single?.Type == JTokenType.String && Valid((string)single)) return new[] { (string)single };
            if (single != null || list == null || list.Count < 1 || list.Count > 100 || list.Any(x => x.Type != JTokenType.String || !Valid((string)x)))
                throw new BridgeException("InvalidRequest", "Specify documentName or 1..100 non-empty documentNames of at most 256 characters, not both.");
            var names = list.Select(x => (string)x).ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new BridgeException("InvalidRequest", "documentNames must be distinct.");
            return names;
        }

        private static bool Valid(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 256;
    }
}
