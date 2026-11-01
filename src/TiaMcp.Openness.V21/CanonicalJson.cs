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

        internal static string Sha256(JToken token) => Hex(Serialize(token));

        // Workers before RFC 8785 (1 November 2026) hashed ordinal-sorted compact Newtonsoft JSON; values they
        // stored (admission observations, reserved manifests, source snapshots) still verify against it.
        internal static string LegacySha256(JToken token)
        {
            JToken Sort(JToken value)
            {
                if (value is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sort(p.Value))));
                if (value is JArray array) return new JArray(array.Select(Sort));
                return value.DeepClone();
            }
            return Hex(Sort(token).ToString(Formatting.None));
        }

        internal static bool Matches(string stored, JToken token) => stored != null && (stored == Sha256(token) || stored == LegacySha256(token));

        private static string Hex(string text)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }

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

    // Reads one request line. .NET Framework's double.Parse misrounds some literals (2.2250738585072012e-308, long
    // halfway mantissas), so each float is re-read exactly from its text, as the Host reads the same literal.
    internal sealed class ExactNumberReader : JsonTextReader
    {
        private readonly string _line;
        internal ExactNumberReader(string line) : base(new System.IO.StringReader(line)) { _line = line; }

        public override bool Read()
        {
            bool read = base.Read();
            if (read && TokenType == JsonToken.Float)
            {
                int end = LinePosition, start = end; // one line: the position just after the literal
                while (start > 0 && "0123456789+-.eE".IndexOf(_line[start - 1]) >= 0) start--;
                double? exact = CanonicalJsonText.ParseNumber(_line.Substring(start, end - start));
                if (exact.HasValue) SetToken(JsonToken.Float, exact.Value, false);
            }
            return read;
        }
    }
}
