using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    /// <summary>Rules of change kind hmi_text_list_import. The export is not the authoring format (V21 renames entries, pools
    /// every text into one library and omits defaults), so lists are compared by meaning, as the native qualification
    /// runner compares them (scripts/qualify_hmi_text_list_import.py resolve): each list becomes a sorted multiset of entries
    /// with defaults applied and every text reference replaced by its per-language texts. Every stated value stays significant.</summary>
    internal static class HmiTextListImportRules
    {
        internal const string Operation = "text_list_import";
        // Profiles whose native qualification passed (local.hmi.text-list-import.route.20261031 and the interruption records).
        internal static readonly string[] Profiles = { "unified-comfort", "unified-pc" };
        // Profiles a qualification-only Host (--hmi-text-list-qualification <profile>) may admit in a project its own worker retrieved.
        internal static readonly string[] QualificationProfiles = { "unified-comfort", "unified-pc" };
        private static readonly Dictionary<string, JToken> EntryDefaults = new Dictionary<string, JToken>(StringComparer.Ordinal)
            { ["Type"] = "SingleValue", ["Value"] = 0L, ["FromValue"] = 0L, ["ToValue"] = 0L, ["IsDefaultEntry"] = false };

        internal static void RequireRequest(JObject payload)
        {
            if (payload == null || payload.Value<int?>("schemaVersion") != 1) throw new ArgumentException("hmi_text_list_import requires payload.schemaVersion=1.");
            foreach (var property in payload.Properties())
                if (!new[] { "schemaVersion", "target", "expectedContentHash", "candidate" }.Contains(property.Name)) throw new ArgumentException("Unsupported field: " + property.Name);
            var target = payload["target"] as JObject ?? throw new ArgumentException("An exact Unified hmiDevice identity is required.");
            foreach (string key in new[] { "projectCopyId", "sessionGeneration", "hmiPath", "objectPath" }) HmiEngineeringRules.Text(target, key, 4096);
            if (HmiEngineeringRules.Text(target, "family") != "unified" || HmiEngineeringRules.Text(target, "kind") != "hmiDevice" || target.Value<string>("objectPath") != target.Value<string>("hmiPath"))
                throw new ArgumentException("target must be the exact Unified HMI software identity (kind hmiDevice from hmi_inspect section=devices).");
            string hash = HmiEngineeringRules.Text(payload, "expectedContentHash");
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("expectedContentHash must be the full native-content SHA-256 of the HMI capture.");
            var candidate = payload["candidate"] as JObject ?? throw new ArgumentException("The Host-resolved candidate is missing; use change_prepare through the Host.");
            var files = candidate["files"] as JArray;
            if (files == null || files.Count != 2) throw new ArgumentException("A candidate is one configuration file and its TextLibrary file.");
            for (int i = 0; i < 2; i++)
            {
                var file = files[i] as JObject ?? throw new ArgumentException("A candidate file must be an object.");
                string name = HmiEngineeringRules.Text(file, "fileName");
                if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.StartsWith(".", StringComparison.Ordinal) || !name.EndsWith(".hmi.yml", StringComparison.Ordinal)
                    || name.EndsWith(".TextLibrary.hmi.yml", StringComparison.Ordinal) != (i == 1))
                    throw new ArgumentException("Candidate files are <base>.hmi.yml and <base>.TextLibrary.hmi.yml.");
                string sha = HmiEngineeringRules.Text(file, "sha256");
                if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c)) || file["bytes"]?.Type != JTokenType.Integer || (long)file["bytes"] < 1 || (long)file["bytes"] > 8 * 1024 * 1024)
                    throw new ArgumentException("Candidate files need their SHA-256 and a size of at most 8 MiB.");
                HmiEngineeringRules.Text(file, "path", 4096);
            }
            if ((string)files[1]["fileName"] != ((string)files[0]["fileName"]).Substring(0, ((string)files[0]["fileName"]).Length - ".hmi.yml".Length) + ".TextLibrary.hmi.yml")
                throw new ArgumentException("The TextLibrary file must pair with the configuration file.");
            var lists = candidate["lists"] as JArray;
            var languages = candidate["languages"] as JArray;
            if (lists == null || lists.Count < 1 || lists.Count > 128 || lists.Any(l => l.Type != JTokenType.String) || languages == null || languages.Count < 1 || languages.Any(l => l.Type != JTokenType.String))
                throw new ArgumentException("The candidate names its lists and languages.");
        }

        /// <summary>qualificationProfile is the profile a qualification-only Host admits, passed only for a project the worker retrieved itself.</summary>
        internal static JObject Gate(string family, string profile, string qualificationProfile = null)
        {
            bool production = family == "unified" && Profiles.Contains(profile, StringComparer.Ordinal);
            bool qualification = !production && family == "unified" && profile != null && profile == qualificationProfile && QualificationProfiles.Contains(profile, StringComparer.Ordinal);
            bool available = production || qualification;
            return new JObject { ["family"] = family, ["targetProfile"] = profile ?? "unknown", ["operation"] = Operation, ["available"] = available,
                ["admission"] = production ? "production" : qualification ? "qualification" : null,
                ["apiAvailability"] = "available", ["bridgeImplementation"] = "guarded_body", ["nativeQualification"] = production ? "verified" : "unverified",
                ["reasonCode"] = available ? null : "HmiPreservationUnavailable",
                ["reason"] = production
                    ? "Opened by the 31 October maintainer decision for this kind only, on a natively qualified profile: HmiTextListComposition.Import with exact export-compare readback, unchanged other text lists and whole-HMI capture, unchanged compiler errors/warnings, save and checkpoint restore. Screens and every other HMI write stay behind the closed preservation gate."
                    : qualification ? "Qualification-only admission of profile " + profile + " in a project this worker retrieved; no production profile is qualified yet."
                    : "No profile has passed native qualification of Unified text-list import (this target's profile is " + (profile ?? "unknown") + "); only a qualification Host admits unified-comfort or unified-pc in its own retrieved project.",
                ["requiredGates"] = new JArray("completePreservationScope", "candidateRefusals", "activeLanguagesEqual", "exactImportReadback", "otherTextListsUnchanged",
                    "unrelatedContentPreserved", "compilerProblemsUnchanged", "saveReopen", "archiveRecoveryEquality", "interruptedWriteRecovery"),
                ["runtimeVerified"] = false };
        }

        /// <summary>The Unified capture always records its two standing notes (tag-table export stability; no Screen.Export), so it is
        /// never "complete". This kind accepts exactly those notes and nothing else: no typed read error, no native capture failure,
        /// no Classic gap, no other note. Its content hash is then the observed-artifacts hash.</summary>
        internal static bool ScopeAccepted(JObject manifest) => HmiEngineeringRules.ScopeAccepted("hmi_text_list_import", manifest);

        /// <summary>The text-list export skips lists with formatted text (Siemens), so every typed text-list node of the target must
        /// be a list the export holds; otherwise an unexported peer could change unseen.</summary>
        internal static bool ExportCoversTypedLists(IEnumerable<string> artifactKeys, string hmiPath, IEnumerable<string> exportedNames)
        {
            string prefix = hmiPath + "/textLists/";
            var typed = new HashSet<string>(artifactKeys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) < 0)
                .Select(k => k.Substring(prefix.Length)), StringComparer.Ordinal);
            return typed.SetEquals(exportedNames.Select(Uri.EscapeDataString));
        }

        /// <summary>Text lists by name, each as canonical JSON of its meaning.</summary>
        internal static SortedDictionary<string, string> Resolve(string configuration, string library)
        {
            var config = AsMapping(Read(configuration), "configuration");
            var libraries = AsMapping(Field(AsMapping(Read(library), "library"), "TextLibraries"), "TextLibraries");
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var container in AsMapping(Field(config, "TextListContainers"), "TextListContainers"))
                foreach (var list in AsMapping(Field(AsMapping(container.Value, "container"), "ResourceLists"), "ResourceLists"))
                {
                    if (result.ContainsKey(list.Key)) throw new FormatException("Duplicate text-list name across containers: " + list.Key);
                    var row = AsMapping(list.Value, "list");
                    var entries = new List<JObject>();
                    foreach (var entry in AsMapping(Field(row, "Entries"), "Entries"))
                    {
                        var value = new SortedDictionary<string, JToken>(EntryDefaults, StringComparer.Ordinal);
                        foreach (var field in AsMapping(entry.Value, "entry")) value[field.Key] = EntryValue(field.Key, field.Value);
                        JToken reference = value.TryGetValue("Text", out var text) ? text : JValue.CreateNull();
                        value["Text"] = Texts(libraries, reference);
                        entries.Add(Canonical(value));
                    }
                    var canonical = new SortedDictionary<string, JToken>(StringComparer.Ordinal) { ["RangeType"] = "Decimal" };
                    foreach (var field in row.Where(f => f.Key != "Entries")) canonical[field.Key] = Generic(field.Value);
                    canonical["Entries"] = new JArray(entries.OrderBy(e => e.ToString(Formatting.None), StringComparer.Ordinal).ToArray<object>());
                    result.Add(list.Key, Canonical(canonical).ToString(Formatting.None));
                }
            return result;
        }

        /// <summary>Candidate lists must read back exactly; every other list must be unchanged and no other list may appear.</summary>
        internal static JObject Evaluate(IDictionary<string, string> before, IDictionary<string, string> after, IDictionary<string, string> candidate)
        {
            var lists = new JObject();
            foreach (var pair in candidate) lists[pair.Key] = !after.TryGetValue(pair.Key, out var actual) ? "absent" : actual == pair.Value ? "exact" : "changed";
            var peers = before.Keys.Where(k => !candidate.ContainsKey(k) && (!after.TryGetValue(k, out var value) || value != before[k])).ToArray();
            var unexpected = after.Keys.Where(k => !before.ContainsKey(k) && !candidate.ContainsKey(k)).ToArray();
            bool exact = lists.Properties().All(p => (string)p.Value == "exact") && peers.Length == 0 && unexpected.Length == 0;
            return new JObject { ["exact"] = exact, ["lists"] = lists, ["replaced"] = new JArray(candidate.Keys.Where(before.ContainsKey).ToArray()),
                ["created"] = new JArray(candidate.Keys.Where(k => !before.ContainsKey(k)).ToArray()), ["peersChanged"] = new JArray(peers),
                ["unexpectedLists"] = new JArray(unexpected), ["peersCompared"] = before.Keys.Count(k => !candidate.ContainsKey(k)) };
        }

        /// <summary>V21 fixtures keep encrypted-transfer password errors (no public setter), so the import must leave the HMI device's
        /// complete Error/Warning diagnostics exactly as the compile before it found them; Information rows depend on the previous compile.
        /// The tree's shape changes with the compile (run artifacts/tx2: the first compile of a retrieved fixture lists the two password
        /// errors flat under a Success summary, the compile after the import nests them under "Runtime settings" under an Error summary).
        /// So problem rows count by state, text and path only; which container (object) a problem sits under is not compared. An
        /// empty-text row is skipped only as a container (the next row is deeper); the closing summary row (last root row) must match
        /// with every digit run replaced by '#', so a trailing root error cannot pass as the summary. Anything else refuses.</summary>
        internal static bool CompileProblemsUnchanged(JObject before, JObject after)
        {
            var b = Problems(before); var a = Problems(after);
            return b != null && a != null && b.SequenceEqual(a, StringComparer.Ordinal);
        }

        // Sorted problem rows plus one normalized summary row per target; null for an incomplete or unknown tree.
        private static string[] Problems(JObject compilation)
        {
            var targets = compilation?["targets"] as JArray;
            if (compilation?["error"] != null || targets == null || targets.Count == 0) return null;
            var rows = new List<string>(); var summaries = new List<string>();
            foreach (var target in targets.OfType<JObject>())
            {
                if (target["error"] != null || target["messagesTruncated"]?.Type != JTokenType.Boolean || target.Value<bool>("messagesTruncated") || !(target["messages"] is JArray messages)) return null;
                var list = messages.OfType<JObject>().ToList();
                int summary = list.FindLastIndex(m => m.Value<int?>("depth") == 0);
                if (summary < 0 || summary != list.Count - 1 || string.IsNullOrEmpty(list[summary].Value<string>("description"))) return null;
                summaries.Add(new JArray(target.Value<string>("target"), Regex.Replace(list[summary].Value<string>("description"), "[0-9]+", "#")).ToString(Formatting.None));
                for (int i = 0; i < summary; i++)
                {
                    var message = list[i];
                    if (message.Value<string>("state") != "Error" && message.Value<string>("state") != "Warning") continue;
                    if (string.IsNullOrEmpty(message.Value<string>("description")))
                    {
                        if (list[i + 1].Value<int?>("depth") > message.Value<int?>("depth")) continue; // A container: its state mirrors its children.
                        return null;
                    }
                    rows.Add(new JArray(target.Value<string>("target"), message.Value<string>("state"), message.Value<string>("description"), message.Value<string>("path")).ToString(Formatting.None));
                }
            }
            rows.Sort(StringComparer.Ordinal);
            return rows.Concat(summaries).ToArray();
        }

        private static JToken EntryValue(string field, object node)
        {
            var scalar = node as Scalar;
            if (field == "Value" || field == "FromValue" || field == "ToValue")
                return scalar != null && scalar.Plain && Regex.IsMatch(scalar.Text, @"\A-?(0|[1-9][0-9]{0,18})\z") && long.TryParse(scalar.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) ? (JToken)number : Generic(node);
            if (field == "IsDefaultEntry" && scalar != null && scalar.Plain)
            {
                if (new[] { "true", "True", "TRUE" }.Contains(scalar.Text)) return true;
                if (new[] { "false", "False", "FALSE" }.Contains(scalar.Text)) return false;
            }
            return Generic(node);
        }

        // An entry without a reference keeps null; any other reference must resolve to exactly one library entry with one text per language.
        private static JToken Texts(Dictionary<string, object> libraries, JToken reference)
        {
            if (reference.Type == JTokenType.Null) return JValue.CreateNull();
            string text = reference.Type == JTokenType.String ? (string)reference : null;
            var owners = text == null ? new KeyValuePair<string, object>[0] : libraries.Where(l => text.StartsWith(l.Key + ".", StringComparison.Ordinal)).ToArray();
            if (owners.Length == 1)
            {
                var owner = AsMapping(owners[0].Value, "library");
                var languages = Field(owner, "Languages") as List<object>;
                var entry = Field(AsMapping(Field(owner, "Entries"), "Entries"), text.Substring(owners[0].Key.Length + 1)) as Dictionary<string, object>;
                var texts = entry == null ? null : Field(entry, "Text") as List<object>;
                if (languages != null && texts != null && languages.Count == texts.Count && languages.All(l => l is Scalar) && texts.All(t => t is Scalar))
                {
                    var map = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
                    for (int i = 0; i < languages.Count; i++)
                    {
                        if (map.ContainsKey(((Scalar)languages[i]).Text)) throw new FormatException("Duplicate library language.");
                        map[((Scalar)languages[i]).Text] = ((Scalar)texts[i]).Text;
                    }
                    return Canonical(map);
                }
            }
            throw new FormatException("Unresolved or ambiguous text reference: " + Shorten(reference.ToString(Formatting.None)));
        }

        private static JToken Generic(object node)
        {
            if (node == null) return JValue.CreateNull();
            if (node is Scalar scalar) return scalar.Text;
            if (node is List<object> sequence) return new JArray(sequence.Select(n => (object)Generic(n)).ToArray());
            var map = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var pair in (Dictionary<string, object>)node) map[pair.Key] = Generic(pair.Value);
            return Canonical(map);
        }

        // Built directly, never re-parsed: JSON parsing would turn date-like texts into dates.
        private static JObject Canonical(SortedDictionary<string, JToken> value)
        {
            var result = new JObject();
            foreach (var pair in value) result[pair.Key] = pair.Value?.DeepClone() ?? JValue.CreateNull();
            return result;
        }

        private static object Field(Dictionary<string, object> mapping, string key) => mapping.TryGetValue(key, out var value) ? value : null;
        private static Dictionary<string, object> AsMapping(object node, string role)
            => node == null ? new Dictionary<string, object>(StringComparer.Ordinal) : node as Dictionary<string, object> ?? throw new FormatException(role + " is not a mapping.");

        internal sealed class Scalar { internal string Text; internal bool Plain; }

        /// <summary>Reads the YAML subset of V21 text-list exports and hmi_text_list_author candidates: block mappings and
        /// (indentless) block sequences of scalars, plain/single/double-quoted single-line scalars, literal and folded block
        /// scalars without an indentation indicator, comments and empty [] / {}. Block scalars follow PyYAML (YAML 1.1),
        /// whose reading of these exports passed natively, including U+2028/U+2029 as preserved line breaks. Anything else
        /// (anchors, tags, flow content, multi-line plain or quoted scalars, duplicate keys, tabs in indentation) is refused.</summary>
        internal static object Read(string text)
        {
            if (text == null || text.Length > 32 * 1024 * 1024) throw new FormatException("Text-list YAML exceeds 32 MiB.");
            if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text.Substring(1);
            var lines = text.Split('\n').Select(l => l.EndsWith("\r", StringComparison.Ordinal) ? l.Substring(0, l.Length - 1) : l).ToArray();
            if (lines.Any(l => l.IndexOf('\r') >= 0 || l.IndexOf('\u0085') >= 0)) throw new FormatException("Only LF or CRLF line breaks are supported.");
            int index = 0;
            Skip(lines, ref index);
            if (index == lines.Length) return null;
            if (Indent(lines[index]) != 0) throw new FormatException("The document must start at column 0.");
            var result = Node(lines, ref index, 0, 0);
            Skip(lines, ref index);
            if (index != lines.Length) throw new FormatException("Unexpected content at line " + (index + 1) + ".");
            return result;
        }

        private static void Skip(string[] lines, ref int index)
        {
            while (index < lines.Length && (lines[index].Trim(' ').Length == 0 || lines[index].TrimStart(' ').StartsWith("#", StringComparison.Ordinal))) index++;
        }

        private static int Spaces(string line) { int count = 0; while (count < line.Length && line[count] == ' ') count++; return count; }
        private static int Indent(string line)
        {
            int count = Spaces(line);
            if (count < line.Length && line[count] == '\t') throw new FormatException("Tabs cannot indent YAML structure.");
            return count;
        }

        private static bool IsItem(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

        private const int MaxDepth = 32;

        private static object Node(string[] lines, ref int index, int indent, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("Text-list YAML nests deeper than " + MaxDepth + " levels.");
            string content = lines[index].Substring(indent);
            if (IsItem(content))
            {
                var sequence = new List<object>();
                while (index < lines.Length && Indent(lines[index]) == indent && IsItem(lines[index].Substring(indent)))
                {
                    string value = lines[index].Substring(indent) == "-" ? "" : lines[index].Substring(indent + 2);
                    if (value.Length == 0 || value.TrimStart(' ').Length != value.Length) throw new FormatException("Sequence items must be inline scalars.");
                    sequence.Add(Value(lines, ref index, indent, value, true));
                    Skip(lines, ref index);
                }
                return sequence;
            }
            var mapping = new Dictionary<string, object>(StringComparer.Ordinal);
            while (index < lines.Length && Indent(lines[index]) == indent && !IsItem(lines[index].Substring(indent)))
            {
                string line = lines[index].Substring(indent);
                int end; string key = Key(line, out end);
                if (mapping.ContainsKey(key)) throw new FormatException("Duplicate mapping key: " + key);
                string rest = line.Substring(end);
                object value;
                if (rest.Trim(' ').Length == 0 || rest.TrimStart(' ').StartsWith("#", StringComparison.Ordinal))
                {
                    index++; Skip(lines, ref index);
                    if (index < lines.Length && Indent(lines[index]) > indent) value = Node(lines, ref index, Indent(lines[index]), depth + 1);
                    else if (index < lines.Length && Indent(lines[index]) == indent && IsItem(lines[index].Substring(indent))) value = Node(lines, ref index, indent, depth + 1);
                    else value = null;
                }
                else
                {
                    if (!rest.StartsWith(" ", StringComparison.Ordinal)) throw new FormatException("A mapping value needs ': '.");
                    value = Value(lines, ref index, indent, rest.TrimStart(' '), false);
                    Skip(lines, ref index);
                }
                mapping.Add(key, value);
            }
            if (index < lines.Length && Indent(lines[index]) > indent) throw new FormatException("Unexpected indentation at line " + (index + 1) + ".");
            return mapping;
        }

        // A key ends at its ':'; returns the index after the colon.
        private static string Key(string line, out int end)
        {
            if (line.StartsWith("\"", StringComparison.Ordinal) || line.StartsWith("'", StringComparison.Ordinal))
            {
                string key = Quoted(line, out int close);
                if (close >= line.Length || line[close] != ':') throw new FormatException("A quoted key must be followed by ':'.");
                end = close + 1; return key;
            }
            int colon = -1;
            for (int i = 0; i < line.Length; i++)
                if (line[i] == ':' && (i + 1 == line.Length || line[i + 1] == ' ')) { colon = i; break; }
            if (colon <= 0) throw new FormatException("Expected a mapping key: " + Shorten(line));
            string plain = line.Substring(0, colon);
            RequirePlain(plain);
            end = colon + 1; return plain;
        }

        private static object Value(string[] lines, ref int index, int indent, string value, bool item)
        {
            if (value.StartsWith("|", StringComparison.Ordinal) || value.StartsWith(">", StringComparison.Ordinal)) return Block(lines, ref index, indent, value);
            index++;
            if (value == "[]" ) return new List<object>();
            if (value == "{}") return new Dictionary<string, object>(StringComparer.Ordinal);
            if (value.StartsWith("\"", StringComparison.Ordinal) || value.StartsWith("'", StringComparison.Ordinal))
            {
                string text = Quoted(value, out int close);
                string tail = value.Substring(close).TrimStart(' ');
                if (tail.Length != 0 && !(tail.StartsWith("#", StringComparison.Ordinal) && value[close] == ' ')) throw new FormatException("Unexpected text after a quoted scalar.");
                return new Scalar { Text = text, Plain = false };
            }
            string plain = value.TrimEnd(' ');
            RequirePlain(plain);
            if (item && (plain.EndsWith(":", StringComparison.Ordinal) || plain.Contains(": "))) throw new FormatException("Mappings inside sequences are not supported.");
            // A more indented line would continue a multi-line plain scalar.
            int next = index; Skip(lines, ref next);
            if (next < lines.Length && Spaces(lines[next]) > indent) throw new FormatException("Multi-line plain scalars are not supported.");
            return new Scalar { Text = plain, Plain = true };
        }

        private static void RequirePlain(string text)
        {
            if (text.Length == 0 || "-?:,[]{}#&*!|>'\"%@`".IndexOf(text[0]) >= 0 && !(text[0] == '-' && text.Length > 1 && text[1] != ' ') || text.Contains(" #") || text.Contains(": ")
                || text.IndexOf('\u2028') >= 0 || text.IndexOf('\u2029') >= 0 || text.Trim(' ', '\t').Length != text.Length)
                throw new FormatException("Unsupported plain scalar: " + Shorten(text));
        }

        // One-line single- or double-quoted scalar starting at text[0]; close is the index after the closing quote.
        private static string Quoted(string text, out int close)
        {
            char quote = text[0]; var result = new StringBuilder();
            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\u2028' || c == '\u2029') break;
                if (quote == '\'')
                {
                    if (c != '\'') { result.Append(c); continue; }
                    if (i + 1 < text.Length && text[i + 1] == '\'') { result.Append('\''); i++; continue; }
                    close = i + 1; return result.ToString();
                }
                if (c == '"') { close = i + 1; return result.ToString(); }
                if (c != '\\') { result.Append(c); continue; }
                if (++i >= text.Length) break;
                switch (text[i])
                {
                    case '0': result.Append('\0'); break; case 'a': result.Append('\a'); break; case 'b': result.Append('\b'); break;
                    case 't': case '\t': result.Append('\t'); break; case 'n': result.Append('\n'); break; case 'v': result.Append('\v'); break;
                    case 'f': result.Append('\f'); break; case 'r': result.Append('\r'); break; case 'e': result.Append('\u001b'); break;
                    case ' ': result.Append(' '); break; case '"': result.Append('"'); break; case '/': result.Append('/'); break;
                    case '\\': result.Append('\\'); break; case 'N': result.Append('\u0085'); break; case '_': result.Append('\u00A0'); break;
                    case 'L': result.Append('\u2028'); break; case 'P': result.Append('\u2029'); break;
                    case 'x': case 'u': case 'U':
                        int digits = text[i] == 'x' ? 2 : text[i] == 'u' ? 4 : 8;
                        if (i + digits >= text.Length || !int.TryParse(text.Substring(i + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)
                            || code < 0 || code > 0x10FFFF || code >= 0xD800 && code <= 0xDFFF)
                            throw new FormatException("Invalid or surrogate escape in a double-quoted scalar.");
                        result.Append(char.ConvertFromUtf32(code)); i += digits;
                        break;
                    default: throw new FormatException("Unknown escape in a double-quoted scalar.");
                }
            }
            throw new FormatException("Quoted scalars must close on their line.");
        }

        // Literal or folded block scalar: PyYAML's scan_block_scalar over the more-indented lines that follow the header.
        private static Scalar Block(string[] lines, ref int index, int indent, string header)
        {
            bool folded = header[0] == '>'; bool? chomping = null; int at = 1;
            if (at < header.Length && (header[at] == '-' || header[at] == '+')) { chomping = header[at] == '+'; at++; }
            if (at < header.Length && char.IsDigit(header[at])) throw new FormatException("Block scalar indentation indicators are not supported.");
            string rest = header.Substring(at);
            if (rest.Trim(' ').Length != 0 && !(rest.StartsWith(" ", StringComparison.Ordinal) && rest.TrimStart(' ').StartsWith("#", StringComparison.Ordinal)))
                throw new FormatException("Unexpected text after a block scalar header.");
            int first = ++index;
            while (index < lines.Length && (lines[index].Trim(' ').Length == 0 || Spaces(lines[index]) > indent)) index++;
            // Trailing empty lines before a less indented line belong to the scalar, as in PyYAML.
            string s = string.Join("\n", lines.Skip(first).Take(index - first)) + (index > first ? "\n" : "");
            int p = 0, column = 0;
            char Peek(int k = 0) => p + k < s.Length ? s[p + k] : '\0';
            void Forward(int count = 1)
            {
                for (int k = 0; k < count; k++) { char c = s[p++]; if (c == '\n' || c == '\u2028' || c == '\u2029') column = 0; else if (c != '\uFEFF') column++; }
            }
            bool Break(char c) => c == '\n' || c == '\u2028' || c == '\u2029';
            string LineBreak() { char c = Peek(); if (!Break(c)) return ""; Forward(); return c == '\n' ? "\n" : c.ToString(); }
            List<string> Breaks(int level)
            {
                var found = new List<string>();
                while (column < level && Peek() == ' ') Forward();
                while (Break(Peek())) { found.Add(LineBreak()); while (column < level && Peek() == ' ') Forward(); }
                return found;
            }
            var chunks = new StringBuilder(); var breaks = new List<string>(); int max = 0;
            while (Peek() == ' ' || Break(Peek()))
            {
                if (Peek() != ' ') breaks.Add(LineBreak());
                else { Forward(); if (column > max) max = column; }
            }
            int level = Math.Max(indent + 1, max);
            string lineBreak = "";
            while (column == level && Peek() != '\0')
            {
                foreach (string b in breaks) chunks.Append(b);
                bool leadingNonSpace = Peek() != ' ' && Peek() != '\t';
                int length = 0; while (Peek(length) != '\0' && !Break(Peek(length))) length++;
                chunks.Append(s, p, length); Forward(length);
                lineBreak = LineBreak();
                breaks = Breaks(level);
                if (column == level && Peek() != '\0')
                {
                    if (folded && lineBreak == "\n" && leadingNonSpace && Peek() != ' ' && Peek() != '\t') { if (breaks.Count == 0) chunks.Append(' '); }
                    else chunks.Append(lineBreak);
                }
                else break;
            }
            if (p < s.Length) throw new FormatException("A block scalar line is less indented than its content.");
            if (chomping != false) chunks.Append(lineBreak);
            if (chomping == true) foreach (string b in breaks) chunks.Append(b);
            return new Scalar { Text = chunks.ToString(), Plain = false };
        }

        private static string Shorten(string text) => text.Length > 80 ? text.Substring(0, 80) + "..." : text;
    }
}
