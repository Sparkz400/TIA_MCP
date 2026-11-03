using Newtonsoft.Json.Linq;
using TiaMcp.Model;
using TiaMcp.Openness.V21;

static class ClassicScreenEditTests
{
    public static void Run(Action<string, Action> check)
    {
        const string kind = "hmi_classic_screen_edit", hmi = "/projects/Test/devices/HMI/hmi/Runtime";
        void Assert(bool value) { if (!value) throw new Exception("Screen scope assertion failed"); }
        check("Screen edit admits only existing regular RT Advanced screens and XML import", () => {
            foreach (string family in new[] { "classic", "unified" })
            foreach (string profile in new[] { "classic-rt-advanced", "classic-basic", "classic-comfort", "unified-pc" })
            foreach (string operation in new[] { "classic_xml_import", "scalar_property_set", "screen_create" })
            foreach (string type in new[] { "Siemens.Engineering.Hmi.Screen.Screen", "Siemens.Engineering.Hmi.Screen.ScreenTemplate", "Siemens.Engineering.Hmi.Tag.TagTable", "Siemens.Engineering.Hmi.TextGraphicList.GraphicList" }) {
                var gate = HmiEngineeringRules.KindGate(kind, family, profile, operation, type);
                Assert(gate.Value<bool>("available") == (family == "classic" && profile == "classic-rt-advanced" && operation == "classic_xml_import" && type == "Siemens.Engineering.Hmi.Screen.Screen"));
                Assert(gate.Value<string>("nativeQualification") == "unverified");
            }
        });
        check("Screen scope is explicit and cannot authorize whole-HMI import or vice versa", () => {
            var capture = new HmiPreservationCapture("unused", "copy.ap21", "classic", hmi, "test", _ => { });
            var artifacts = new JObject { [hmi + "/screens/Main"] = new JObject { ["sha256"] = new string('a', 64) } };
            var targets = new JArray(new JObject { ["family"] = "classic", ["hmiPath"] = hmi });
            var bounded = capture.Manifest(kind, artifacts, new JArray(HmiPreservationRules.ClassicScreenExportNote), targets);
            Assert(!bounded.Value<bool>("complete") && HmiEngineeringRules.ScopeAccepted(kind, bounded));
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", bounded));
            var full = capture.Manifest("hmi_classic_import", artifacts, new JArray(HmiPreservationRules.ClassicExportNote), targets);
            Assert(!HmiEngineeringRules.ScopeAccepted(kind, full));
            Assert(bounded.Value<string>("contentHash") != full.Value<string>("contentHash"));
            foreach (string damage in new[] { "scope", "target", "extraTarget", "extraGap", "artifact", "empty", "failure" }) {
                var bad = (JObject)bounded.DeepClone();
                if (damage == "scope") bad.Remove("scopePolicy");
                if (damage == "target") bad["targets"][0]["hmiPath"] = hmi + "Other";
                if (damage == "extraTarget") ((JArray)bad["targets"]).Add(targets[0].DeepClone());
                if (damage == "extraGap") ((JArray)bad["missingCoverage"]).Add("unexported screen");
                if (damage == "artifact") bad["artifacts"][hmi + "/tagTables/Other"] = artifacts.Properties().First().Value.DeepClone();
                if (damage == "empty") bad["artifacts"] = new JObject();
                if (damage == "failure") bad["nativeFailure"] = new JObject { ["message"] = "export failed" };
                Assert(!HmiEngineeringRules.ScopeAccepted(kind, bad));
            }
        });
        check("Screen preservation refuses changed, added and removed peers", () => {
            var before = new Dictionary<string, string> { ["screen"] = "a", ["peer"] = "b" };
            var after = new Dictionary<string, string>(before) { ["screen"] = "changed" };
            HmiPreservationRules.RequireUnrelatedPreserved(before, after, new[] { "screen" }, Array.Empty<string>());
            foreach (string mutation in new[] { "changed", "added", "removed" }) {
                var bad = new Dictionary<string, string>(after);
                if (mutation == "changed") bad["peer"] = "changed";
                if (mutation == "added") bad["new"] = "added";
                if (mutation == "removed") bad.Remove("peer");
                bool rejected = false;
                try { HmiPreservationRules.RequireUnrelatedPreserved(before, bad, new[] { "screen" }, Array.Empty<string>()); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected);
            }
        });
        check("Screen fingerprint binds project state and content without requiring PLC payloads", () => {
            string Hash(string path, string saved, bool dirty, char content, bool independent = false)
                => HmiEngineeringRules.ClassicScreenFingerprint(path, saved, dirty, new string(content, 64), independent);
            string original = Hash("copy.ap21", "save1", false, 'a');
            Assert(original != Hash("other.ap21", "save1", false, 'a'));
            Assert(original != Hash("copy.ap21", "save2", false, 'a'));
            Assert(original != Hash("copy.ap21", "save1", true, 'a'));
            Assert(original != Hash("copy.ap21", "save1", false, 'b'));
            Assert(Hash("copy.ap21", "save1", false, 'a', true) == Hash("recovered.ap21", "save2", true, 'a', true));
            Assert(Hash("copy.ap21", "save1", false, 'a', true) != Hash("recovered.ap21", "save2", true, 'b', true));
        });
    }
}
