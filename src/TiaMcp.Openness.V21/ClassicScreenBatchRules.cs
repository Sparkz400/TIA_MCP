using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static class ClassicScreenBatchRules
    {
        internal const int MaxScreens = 8;
        internal const int MaxXmlLength = 2 * 1024 * 1024;
        internal static JObject[] Members(JObject payload)
        {
            if (payload == null) throw new ArgumentException("A screen payload is required.");
            var result = new List<JObject> { new JObject { ["target"] = payload["target"]?.DeepClone(), ["xml"] = payload["xml"]?.DeepClone() } };
            if (payload["additionalScreens"] != null)
            {
                var extra = payload["additionalScreens"] as JArray;
                if (extra == null || extra.Count < 1 || extra.Count >= MaxScreens || extra.Any(x => !(x is JObject)))
                    throw new ArgumentException("additionalScreens must contain 1 to 7 exact target/xml pairs.");
                result.AddRange(extra.OfType<JObject>());
            }
            var first = payload["target"] as JObject ?? throw new ArgumentException("An exact first screen target is required.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var member in result)
            {
                if (member.Count != 2 || member["target"] == null || member["xml"]?.Type != JTokenType.String)
                    throw new ArgumentException("Each screen must contain exactly target and xml.");
                var target = member["target"] as JObject ?? throw new ArgumentException("An exact screen identity is required.");
                foreach (var key in new[] { "projectCopyId", "sessionGeneration", "hmiPath", "family" })
                    if (string.IsNullOrEmpty(target.Value<string>(key)) || target.Value<string>(key) != first.Value<string>(key))
                        throw new ArgumentException("All screens must belong to the same HMI, project copy and attachment generation.");
                string path = target.Value<string>("objectPath");
                if (target.Value<string>("family") != "classic" || string.IsNullOrEmpty(path)
                    || !path.StartsWith(first.Value<string>("hmiPath") + "/screens/", StringComparison.Ordinal) || !paths.Add(path))
                    throw new ArgumentException("Only distinct existing regular Classic screen paths are allowed.");
                string xml = member.Value<string>("xml");
                total += xml.Length;
                if (xml.Length == 0 || xml.Length > MaxXmlLength || total > 8 * 1024 * 1024)
                    throw new ArgumentException("Screen XML is bounded to 2 MiB characters per screen and 8 MiB per batch.");
                if (HmiPreservationRules.ClassicImportRoot(HmiPreservationRules.ReadXml(xml)).Name.LocalName != "Hmi.Screen.Screen")
                    throw new ArgumentException("Only native regular-screen exports are accepted.");
            }
            return result.ToArray();
        }
    }
}
