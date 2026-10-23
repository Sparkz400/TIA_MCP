using System.Collections;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class ClassicScreenInventoryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var all = new Fixture(); var scopes = all.Capture();
        check(all.Rows.Count == 6 && scopes.Count == 6 && scopes.All(s => (string)s["status"] == "complete"), "All six Classic scopes are observed independently.");
        check(all.Rows.Select(r => (string)r!["screenRole"]).SequenceEqual(new[] { "regular", "template", "popup", "slideIn", "globalElements", "overview" }), "Screen roles preserve exact native composition distinctions.");
        check(all.Rows.Select(r => (string)r!["identity"]!["objectPath"]).Distinct().Count() == 6, "Repeated names in distinct native scopes remain separate identities.");
        check((string)all.Rows[4]!["identity"]!["objectPath"] == "/hmi/screenGlobalElements" && (string)all.Rows[5]!["identity"]!["objectPath"] == "/hmi/screenOverview", "Singleton paths use native owner properties, not their display names.");
        var nested = new Fixture(); var folder = new Node("A/B"); folder.Values["Screens"] = new[] { new Node("X Y") }; nested.Root("ScreenFolder").Values["Folders"] = new[] { folder };
        nested.Capture(); check(nested.Rows.Any(r => (string)r!["identity"]!["objectPath"] == "/hmi/screens/folders/A%2FB/X%20Y"), "Nested folders and special names use the existing escaped locator grammar.");
        var absent = new Fixture(); absent.Roots["ScreenOverview"] = null; var absentScopes = absent.Capture();
        check((string)absentScopes[5]!["status"] == "absent" && absent.Errors.Count == 0, "Successful null singleton observation records explicit absence.");
        var missing = new Fixture(); missing.Roots["ScreenTemplateFolder"] = null; var missingScopes = missing.Capture();
        check((string)missingScopes[1]!["status"] == "unavailable" && missing.Rows.Count == 5 && missing.Errors.Count > 0, "Missing folder is unavailable, and other roots still retain rows.");
        var broken = new Fixture { BrokenRoot = "ScreenFolder" }; var brokenScopes = broken.Capture();
        check((string)brokenScopes[0]!["status"] == "unavailable" && broken.Rows.Count == 5 && broken.RootReads == 6, "One failing root getter does not hide other screen scopes.");
        var selected = new Fixture { BrokenRoot = "ScreenTemplateFolder" }; var selectedScopes = selected.Capture(selectedPath: "/hmi/screens/Repeated");
        check(selected.RootReads == 1 && selected.Rows.Count == 1 && selected.Errors.Count == 0 && selectedScopes.Count == 1, "Exact regular screen lookup does not depend on unrelated unavailable scopes.");
        var selectedTemplate = new Fixture(); var templateScopes = selectedTemplate.Capture(selectedPath: "/hmi/screenTemplates/Repeated");
        check(selectedTemplate.RootReads == 1 && (string)templateScopes[0]!["role"] == "template", "Exact template lookup selects its original composition.");
        foreach (string invalid in new[] { "/hmi/screensOther/Repeated", "/other/screens/Repeated" })
        {
            var foreign = new Fixture(); check(foreign.Capture(selectedPath: invalid).Count == 0 && foreign.RootReads == 0, "Selection requires an exact owner and complete path prefix: " + invalid);
        }
        var wholeTarget = new Fixture(); check(wholeTarget.Capture(selectedPath: "/hmi").Count == 6, "Selecting the HMI owner still inventories every declared scope.");
        var unnamed = new Fixture(); unnamed.Root("ScreenFolder").Values["Screens"] = new[] { new Node(null) }; var unnamedScopes = unnamed.Capture();
        check((string)unnamedScopes[0]!["status"] == "unavailable" && unnamed.Rows.Count == 5, "Unnamed composition members cannot receive invented identities.");
        var unnamedSingleton = new Fixture(); unnamedSingleton.Roots["ScreenOverview"] = new Node(null); unnamedSingleton.Capture();
        check((string)unnamedSingleton.Rows.Last!["name"] == null && (string)unnamedSingleton.Rows.Last!["identity"]!["objectPath"] == "/hmi/screenOverview", "A singleton keeps its stable property path without inventing a native name.");
        var duplicate = new Fixture(); duplicate.Root("ScreenFolder").Values["Screens"] = new[] { new Node("Repeated"), new Node("Repeated") }; var duplicateScopes = duplicate.Capture();
        check((string)duplicateScopes[0]!["status"] == "unavailable" && (int)duplicateScopes[0]!["observed"] == 1 && duplicate.Rows.Count == 6, "Duplicate native locators retain the first observation and invalidate uniqueness.");
        var iterator = new BrokenSequence(new Node("First")); var partial = new Fixture(); partial.Root("ScreenFolder").Values["Screens"] = iterator; var partialScopes = partial.Capture();
        check(iterator.Disposed && (string)partialScopes[0]!["status"] == "unavailable" && partial.Rows.Count == 6, "Enumerator failure preserves earlier rows, disposes the iterator and continues other roots.");
        var nullComposition = new Fixture(); nullComposition.Root("ScreenFolder").Values["Screens"] = null; var nullScopes = nullComposition.Capture();
        check((string)nullScopes[0]!["status"] == "unavailable", "Unavailable composition never becomes an observed empty list.");
        var cycle = new Fixture(); var self = cycle.Root("ScreenFolder"); self.Values["Screens"] = Array.Empty<Node>(); self.Values["Folders"] = new[] { self }; var cycleScopes = cycle.Capture(depth: 2);
        check((string)cycleScopes[0]!["status"] == "truncated" && cycle.Rows.Count == 5 && cycle.Used < 20, "Cyclic folders stop at the depth bound while independent roots remain observable.");
        for (int limit = 0; limit <= 10; limit++)
        {
            var bounded = new Fixture { Maximum = limit }; var boundedScopes = bounded.Capture();
            check(bounded.Used <= limit && boundedScopes.Count == 6 && (limit == 10 || boundedScopes.Any(s => (string)s["status"] == "truncated")), "Shared visit budget and skipped-scope coverage hold at " + limit);
            if (limit == 0) check(bounded.RootReads == 0, "No native getter runs without a visit budget.");
        }
        var rejected = new Fixture { MaximumRows = 0 }; var rejectedScopes = rejected.Capture();
        check(rejected.Rows.Count == 0 && rejected.RootReads == 1 && rejectedScopes.All(s => (string)s["status"] == "truncated"), "Metadata retention refusal stops subsequent native getters.");
        var path = Environment.GetEnvironmentVariable("TIA_HMI_CLASSIC_SAMPLE_PATH");
        if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, new JObject { ["authority"] = "synthetic", ["rows"] = all.Rows, ["screenScopes"] = scopes }.ToString());
    }
    private sealed class Node(string name)
    {
        internal string Name = name;
        internal Dictionary<string, IEnumerable> Values = new() { ["Folders"] = Array.Empty<Node>() };
    }
    private sealed class Fixture
    {
        internal Dictionary<string, Node> Roots = new();
        internal JArray Rows = new(); internal List<string> Errors = new();
        internal int Used, RootReads, Maximum = 1000, MaximumRows = 1000;
        internal string BrokenRoot;
        internal Fixture()
        {
            foreach (var pair in new[] { ("ScreenFolder", "Screens"), ("ScreenTemplateFolder", "ScreenTemplates"), ("ScreenPopupFolder", "ScreenPopups"), ("ScreenSlideinFolder", "ScreenSlideins") })
            { var node = new Node("Root"); node.Values[pair.Item2] = new[] { new Node("Repeated") }; Roots[pair.Item1] = node; }
            Roots["ScreenGlobalElements"] = new Node("Repeated"); Roots["ScreenOverview"] = new Node("Repeated");
        }
        internal Node Root(string property) => Roots[property];
        internal JArray Capture(int depth = 32, string selectedPath = null) => HmiClassicScreenInventory.Capture("/hmi", property => {
            RootReads++; if (property == BrokenRoot) throw new IOException("Synthetic getter failure."); return Roots[property];
        }, (owner, collection) => ((Node)owner).Values[collection], native => ((Node)native).Name,
            () => { if (Used >= Maximum) return false; Used++; return true; },
            (native, path, parent, role, name) => {
                if (Rows.Count >= MaximumRows) return false;
                Rows.Add(new JObject { ["kind"] = "hmiScreen", ["name"] = name, ["screenRole"] = role, ["parentPath"] = parent,
                    ["source"] = new JObject { ["kind"] = "synthetic", ["scriptsExecuted"] = false },
                    ["identity"] = new JObject { ["schemaVersion"] = 2, ["projectPath"] = "C:\\fixture\\HMI.ap21", ["projectCopyId"] = "fixture", ["sessionGeneration"] = "fixture",
                        ["family"] = "classic", ["devicePath"] = "/device", ["hmiPath"] = "/hmi", ["kind"] = "hmiScreen", ["objectPath"] = path, ["name"] = name } }); return true;
            }, (scope, error) => Errors.Add(scope + ": " + error.Message), depth, selectedPath);
    }
    private sealed class BrokenSequence(object first) : IEnumerable, IEnumerator, IDisposable
    {
        private int reads; internal bool Disposed;
        public object Current => first;
        public IEnumerator GetEnumerator() => this;
        public bool MoveNext() { if (reads++ == 0) return true; throw new IOException("Synthetic iterator failure."); }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
