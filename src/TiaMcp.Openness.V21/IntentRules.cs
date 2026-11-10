using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // #220: a change can carry the task's confirmed machine intent. It is recorded and hash-bound in the preview, compared with
    // decisions of earlier applied changes in the same project, and never evaluated: the tool does not infer machine behavior.
    internal static class IntentRules
    {
        internal static readonly JObject VerificationScope = new JObject {
            ["compileCorrectness"] = "Reported by compile.compilerSucceeded and compile.baselineComparison.",
            ["implementationAgainstIntent"] = "Recorded for review with the change; the tool does not check the implementation against it.",
            ["machineSafetyBehavior"] = "Not verified. Commissioning, risk assessment and runtime acceptance are outside this change." };

        internal static JObject Normalize(JObject intent)
        {
            if (intent.Properties().Any(p => p.Name != "decisions" && p.Name != "observedFacts" && p.Name != "assumptions"))
                throw new BridgeException("InvalidRequest", "intent accepts decisions, observedFacts and assumptions only.");
            var decisions = new JArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in intent["decisions"] as JArray ?? new JArray())
            {
                if (!(token is JObject d)) throw new BridgeException("InvalidRequest", "Each decision must be an object.");
                string id = Text(d, "id", 64, true), source = Text(d, "source", 200, true), statement = Text(d, "statement", 2000, true);
                if (!ids.Add(id)) throw new BridgeException("InvalidRequest", "Decision ids must be unique: " + id);
                var symbols = new JArray((d["symbols"] as JArray ?? new JArray()).Select(s => (JToken)(s.Type == JTokenType.String ? s.Value<string>() : throw new BridgeException("InvalidRequest", "symbols must be strings."))));
                decisions.Add(new JObject { ["id"] = id, ["source"] = source, ["target"] = Text(d, "target", 1000, false), ["symbols"] = symbols,
                    ["statement"] = statement, ["meaning"] = Text(d, "meaning", 2000, false) });
            }
            if (intent["decisions"] != null && decisions.Count == 0) throw new BridgeException("InvalidRequest", "decisions must not be empty when given.");
            return new JObject { ["decisions"] = decisions, ["observedFacts"] = Strings(intent, "observedFacts"), ["assumptions"] = Strings(intent, "assumptions"),
                ["verificationScope"] = VerificationScope.DeepClone() };
        }

        /// <summary>Sets previouslyConfirmed and conflicts from earlier applied decisions of the same project, newest last.</summary>
        internal static void Compare(JObject intent, IEnumerable<JObject> earlier)
        {
            var latest = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var d in earlier) latest[d.Value<string>("id")] = d;
            var confirmed = new JArray(); var conflicts = new JArray();
            foreach (var d in intent["decisions"].OfType<JObject>())
            {
                if (!latest.TryGetValue(d.Value<string>("id"), out var prior)) continue;
                bool same = prior.Value<string>("statement") == d.Value<string>("statement") && prior.Value<string>("target") == d.Value<string>("target");
                (same ? confirmed : conflicts).Add(new JObject { ["id"] = d["id"], ["earlierChangeId"] = prior["changeId"],
                    ["earlierStatement"] = prior["statement"], ["earlierTarget"] = prior["target"] });
            }
            intent["previouslyConfirmed"] = confirmed; intent["conflicts"] = conflicts;
        }

        private static JArray Strings(JObject intent, string key) =>
            new JArray((intent[key] as JArray ?? new JArray()).Select(s => s.Type == JTokenType.String && s.Value<string>().Length <= 2000 ? (JToken)s.Value<string>()
                : throw new BridgeException("InvalidRequest", key + " must list strings of at most 2000 characters.")));

        private static string Text(JObject d, string key, int max, bool required)
        {
            var value = d[key];
            if (value == null || value.Type == JTokenType.Null) return required ? throw new BridgeException("InvalidRequest", "Each decision needs " + key + ".") : null;
            string text = value.Type == JTokenType.String ? value.Value<string>() : throw new BridgeException("InvalidRequest", key + " must be a string.");
            if (string.IsNullOrWhiteSpace(text) || text.Length > max) throw new BridgeException("InvalidRequest", key + " must be 1.." + max + " characters.");
            return text;
        }

        /// <summary>Decisions of one project from the decision log. A line a crash left half-written is skipped, not fatal.</summary>
        internal static List<JObject> DecisionsOf(IEnumerable<string> lines, string projectPath)
        {
            var decisions = new List<JObject>();
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                JObject decision;
                try { decision = JObject.Parse(line); } catch (JsonException) { continue; }
                if (string.Equals(decision.Value<string>("projectPath"), projectPath, StringComparison.OrdinalIgnoreCase)) decisions.Add(decision);
            }
            return decisions;
        }

        internal static string Line(string projectPath, string changeId, JObject decision) =>
            new JObject { ["projectPath"] = projectPath, ["changeId"] = changeId, ["id"] = decision["id"], ["statement"] = decision["statement"], ["target"] = decision["target"] }
                .ToString(Formatting.None);
    }
}
