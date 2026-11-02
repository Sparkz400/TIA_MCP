using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    // halfway mantissas) and accepts some beyond the double range, so it never sees a float literal: each one is read
    // exactly from its text first, as the Host reads it, and Newtonsoft parses a placeholder in its place.
    internal sealed class ExactNumberReader : JsonTextReader
    {
        private readonly Queue<double> _floats;
        internal ExactNumberReader(string line) : this(Scan(line)) { }
        private ExactNumberReader(Tuple<string, Queue<double>, bool> scanned) : base(new StringReader(scanned.Item1)) { _floats = scanned.Item2; OutOfRange = scanned.Item3; }

        // A literal beyond the double range, read as 0. The request loop refuses the request once its id is read, so the
        // error response keeps the id; refused here, it went back without one and the Host faulted the bridge.
        internal bool OutOfRange { get; }

        public override bool Read()
        {
            bool read = base.Read();
            // Every finite float token is a placeholder; NaN and Infinity literals keep Newtonsoft's value.
            if (read && TokenType == JsonToken.Float && Value is double value && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                if (_floats.Count == 0) throw new BridgeException("InvalidRequest", "A request number could not be read exactly.");
                SetToken(JsonToken.Float, _floats.Dequeue(), false);
            }
            return read;
        }

        private static Tuple<string, Queue<double>, bool> Scan(string line)
        {
            var text = new StringBuilder(line.Length); var floats = new Queue<double>();
            bool inString = false, outOfRange = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inString)
                {
                    text.Append(c);
                    if (c == '\\' && i + 1 < line.Length) text.Append(line[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; text.Append(c); continue; }
                if (c != '-' && (c < '0' || c > '9')) { text.Append(c); continue; }
                int start = i;
                while (i + 1 < line.Length && "0123456789+-.eE".IndexOf(line[i + 1]) >= 0) i++;
                string literal = line.Substring(start, i - start + 1);
                if (literal.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 || literal == "-") { text.Append(literal); continue; }
                double? exact;
                try { exact = CanonicalJsonText.ParseNumber(literal); }
                catch (FormatException) { text.Append(literal); continue; } // not a number; Newtonsoft refuses it
                outOfRange |= exact == null;
                floats.Enqueue(exact ?? 0);
                text.Append("0.0");
            }
            return Tuple.Create(text.ToString(), floats, outOfRange);
        }
    }
}
