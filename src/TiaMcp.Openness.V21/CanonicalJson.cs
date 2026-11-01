using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Openness.V21
{
    // RFC 8785 (JCS) form of a Newtonsoft token, byte-identical to the Host's TiaMcp.Core.Engineering.CanonicalJson;
    // fixtures/canonical-json/vectors.v1.json pins both. Integers keep their exact digits beyond 2^53.
    internal static class CanonicalJson
    {
        internal static string Serialize(JToken token)
        {
            var output = new StringBuilder();
            Write(output, token, 0);
            return output.ToString();
        }

        internal static string Sha256(JToken token)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Serialize(token)))).Replace("-", "").ToLowerInvariant(); }

        private static void Write(StringBuilder output, JToken token, int depth)
        {
            if (depth > 128) throw new BridgeException("TraversalLimit", "Canonical JSON exceeds its depth limit.");
            switch (token == null ? JTokenType.Null : token.Type)
            {
                case JTokenType.Object:
                    output.Append('{'); bool first = true;
                    foreach (var property in ((JObject)token).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!first) output.Append(','); first = false;
                        AppendString(output, property.Name); output.Append(':'); Write(output, property.Value, depth + 1);
                    }
                    output.Append('}'); break;
                case JTokenType.Array:
                    output.Append('[');
                    var array = (JArray)token;
                    for (int i = 0; i < array.Count; i++) { if (i > 0) output.Append(','); Write(output, array[i], depth + 1); }
                    output.Append(']'); break;
                case JTokenType.Null: output.Append("null"); break;
                case JTokenType.Boolean: output.Append((bool)token ? "true" : "false"); break;
                case JTokenType.String: AppendString(output, (string)token); break;
                case JTokenType.Integer: output.Append(Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture)); break;
                case JTokenType.Float:
                    output.Append(CanonicalJsonText.Number(Convert.ToDouble(((JValue)token).Value, CultureInfo.InvariantCulture))
                        ?? throw Invalid("Canonical JSON refuses a number outside the double range.")); break;
                // Hashed as the string Newtonsoft writes for them, as before.
                case JTokenType.Date: case JTokenType.Guid: case JTokenType.Uri: case JTokenType.TimeSpan: case JTokenType.Bytes:
                    AppendString(output, JsonConvert.DeserializeObject<string>(token.ToString(Formatting.None), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })); break;
                default: throw Invalid("Canonical JSON accepts only JSON values.");
            }
        }

        private static void AppendString(StringBuilder output, string value)
        { if (!CanonicalJsonText.TryAppendString(output, value)) throw Invalid("Canonical JSON refuses a lone surrogate."); }

        private static BridgeException Invalid(string message) => new BridgeException("InvalidRequest", message);
    }
}
