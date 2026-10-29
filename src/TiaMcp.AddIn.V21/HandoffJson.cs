using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TiaMcp.AddIn
{
    /// <summary>The handoff's JSON: TIA's Add-In sandbox refuses JavaScriptSerializer as security-critical.</summary>
    /// <remarks>Objects read as Dictionary&lt;string, object&gt;, arrays as List&lt;object&gt;, integers as long and other numbers as double.</remarks>
    internal sealed class HandoffJson
    {
        // A stack overflow would end TIA Portal, not just this request.
        private const int DepthLimit = 128;
        private readonly string text;
        private int at;

        private HandoffJson(string text) { this.text = text ?? throw new InvalidDataException("Expected JSON text."); }

        internal static object Parse(string text)
        {
            var reader = new HandoffJson(text);
            object value = reader.Value(0);
            reader.Space();
            if (reader.at != text.Length) throw reader.Error("trailing data");
            return value;
        }

        internal static string Write(object value) { var output = new StringBuilder(); Write(output, value); return output.ToString(); }

        private object Value(int depth)
        {
            if (depth > DepthLimit) throw Error("nesting too deep");
            Space();
            if (at >= text.Length) throw Error("unexpected end");
            switch (text[at])
            {
                case '{':
                    var map = new Dictionary<string, object>(StringComparer.Ordinal); at++;
                    if (Next('}')) return map;
                    do
                    {
                        Space(); string key = String(); Expect(':');
                        if (map.ContainsKey(key)) throw Error("duplicate key " + key);
                        map[key] = Value(depth + 1);
                    } while (Next(','));
                    Expect('}'); return map;
                case '[':
                    var list = new List<object>(); at++;
                    if (Next(']')) return list;
                    do list.Add(Value(depth + 1)); while (Next(','));
                    Expect(']'); return list;
                case '"': return String();
                case 't': return Word("true", true);
                case 'f': return Word("false", false);
                case 'n': return Word("null", null);
                default: return Number();
            }
        }

        private string String()
        {
            if (at >= text.Length || text[at] != '"') throw Error("expected a string");
            var value = new StringBuilder(); at++;
            while (true)
            {
                if (at >= text.Length) throw Error("unterminated string");
                char c = text[at++];
                if (c == '"') return value.ToString();
                if (c < ' ') throw Error("control character in a string");
                if (c != '\\') { value.Append(c); continue; }
                if (at >= text.Length) throw Error("unterminated string");
                switch (c = text[at++])
                {
                    case '"': case '\\': case '/': value.Append(c); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                        if (at + 4 > text.Length || !ushort.TryParse(text.Substring(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
                            throw Error("bad unicode escape");
                        value.Append((char)code); at += 4; break;
                    default: throw Error("bad escape");
                }
            }
        }

        private object Number()
        {
            int start = at;
            if (at < text.Length && text[at] == '-') at++;
            if (at >= text.Length || text[at] < '0' || text[at] > '9') throw Error("unexpected token");
            while (at < text.Length && "0123456789.eE+-".IndexOf(text[at]) >= 0) at++;
            string literal = text.Substring(start, at - start);
            if (long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)) return whole;
            if (double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double real)) return real;
            throw Error("bad number");
        }

        private object Word(string word, object value)
        {
            if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0) throw Error("unexpected token");
            at += word.Length; return value;
        }

        private void Space() { while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\n' || text[at] == '\r')) at++; }
        private bool Next(char c) { Space(); if (at < text.Length && text[at] == c) { at++; return true; } return false; }
        private void Expect(char c) { if (!Next(c)) throw Error("expected '" + c + "'"); }
        private Exception Error(string reason) => new InvalidDataException("Invalid JSON at offset " + at + ": " + reason + ".");

        private static void Write(StringBuilder output, object value)
        {
            switch (value)
            {
                case null: output.Append("null"); break;
                case bool flag: output.Append(flag ? "true" : "false"); break;
                case string s: Quote(output, s); break;
                case int _: case long _: case uint _: output.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                case double real when !double.IsNaN(real) && !double.IsInfinity(real): output.Append(real.ToString("R", CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> map:
                    output.Append('{'); bool more = false;
                    foreach (var pair in map) { if (more) output.Append(','); more = true; Quote(output, pair.Key); output.Append(':'); Write(output, pair.Value); }
                    output.Append('}'); break;
                case IEnumerable list:
                    output.Append('['); bool next = false;
                    foreach (object item in list) { if (next) output.Append(','); next = true; Write(output, item); }
                    output.Append(']'); break;
                default: throw new NotSupportedException("JSON cannot hold " + value.GetType().Name + " " + value + ".");
            }
        }

        private static void Quote(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char c in value)
                if (c == '"' || c == '\\') output.Append('\\').Append(c);
                else if (c < ' ') output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else output.Append(c);
            output.Append('"');
        }
    }
}
