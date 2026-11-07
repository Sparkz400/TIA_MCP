using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // #215: names the compiler reports as undefined, with where it reported them. Nothing is resolved or guessed here;
    // a replacement needs an explicit, user-confirmed mapping. The message text is TIA's English UI text.
    internal static class UnresolvedSymbolRules
    {
        private static readonly Regex Undefined = new Regex(@"^(Tag|Block|Data type|Constant) ""(?<name>[^""]+)"" (is )?not defined\.?$", RegexOptions.Compiled);

        internal static JArray From(JObject compilation)
        {
            var result = new JArray();
            foreach (var target in (compilation?["targets"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var rows = target["messages"] as JArray ?? new JArray();
                var parents = new List<string>();
                foreach (var row in rows.OfType<JObject>())
                {
                    int depth = row.Value<int?>("depth") ?? 0;
                    if (parents.Count > depth) parents.RemoveRange(depth, parents.Count - depth);
                    parents.Add(row.Value<string>("path"));
                    var match = Undefined.Match(row.Value<string>("description") ?? "");
                    if (row.Value<string>("state") != "Error" || !match.Success) continue;
                    result.Add(new JObject { ["kind"] = match.Groups[1].Value, ["name"] = match.Groups["name"].Value,
                        ["target"] = target.Value<string>("target"), ["location"] = new JArray(parents.Where(p => !string.IsNullOrEmpty(p))) });
                }
            }
            return result;
        }
    }
}
