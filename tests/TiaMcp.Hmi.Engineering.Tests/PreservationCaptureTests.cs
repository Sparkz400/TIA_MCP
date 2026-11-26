using Newtonsoft.Json.Linq;
using TiaMcp.Model;
using TiaMcp.Openness.V21;

static class PreservationCaptureTests
{
    const string HmiPath = "/projects/fixture/hmi/Runtime";
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static JObject Artifacts() => new JObject { [HmiPath + "/screens/Main"] = new JObject { ["sha256"] = new string('a', 64), ["bytes"] = 12 } };
    static JArray Targets(string family = "classic") => new JArray(new JObject { ["family"] = family });
    static JObject Manifest(HmiPreservationCapture capture) => capture.Manifest("hmi_classic_import", Artifacts(),
        new JArray(HmiPreservationRules.ClassicExportNote), Targets());
    static void InDirectory(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "tia-hmi-capture-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, true); }
    }
    static HmiPreservationCapture Capture(string directory, Action<JObject> writer = null) =>
        new HmiPreservationCapture(directory, "C:/fixture.ap21", "classic", HmiPath, "before", writer);

    internal static void Run(Action<string, Action> check)
    {
        check("Graphic-list containment refuses the native route regardless of project/list name", () => {
            foreach (string name in new[] { "3DLights_Status_Green", "Renamed", "A/B", "" })
            {
                int exports = 0;
                try
                {
                    HmiNativeExportSafety.RequireAllowed(HmiNativeExportSafety.ClassicGraphicList, HmiPath + "/graphicLists/" + name);
                    exports++;
                    throw new Exception("Expected containment refusal");
                }
                catch (BridgeException ex)
                {
                    Assert(ex.Code == "HmiNativeExportBlocked", "specific refusal code");
                    Assert(ex.Details.Value<string>("nativeOutcome") == "notDispatched", "no native dispatch");
                    Assert(!ex.Details.Value<bool>("automaticRetryAllowed") && exports == 0, "no retry or export");
                }
            }
            foreach (string type in new[] { "Siemens.Engineering.Hmi.Screen.Screen", "Siemens.Engineering.Hmi.Tag.TagTable",
                "Siemens.Engineering.Hmi.TextGraphicList.TextList", "Siemens.Engineering.HmiUnified.TextGraphicList.HmiGraphicList" })
                HmiNativeExportSafety.RequireAllowed(type, HmiPath);
        });

        check("A blocked preflight never runs subsequent exports or grants a preservation hash", () => InDirectory(directory => {
            var capture = Capture(directory); int exports = 0;
            capture.Capture(() => {
                capture.Run("graphic-list-admission", HmiPath + "/graphicLists", null,
                    () => HmiNativeExportSafety.RequireAllowed(HmiNativeExportSafety.ClassicGraphicList, HmiPath + "/graphicLists/Existing"));
                capture.Run("classic-export", HmiPath + "/screens/Main", "native-0.xml", () => exports++);
            }, () => 0);
            var manifest = Manifest(capture);
            Assert(exports == 0 && manifest.Value<string>("status") == "failed", "stop before exports");
            Assert(manifest["contentHash"].Type == JTokenType.Null && !manifest.Value<bool>("complete"), "no authorization");
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", manifest), "standing note cannot excuse a blocked export");
            Assert(manifest["nativeFailure"].Value<string>("code") == "HmiNativeExportBlocked", "original refusal retained");
        }));

        check("Native failure is retained after the project getter becomes unusable", () => InDirectory(directory => {
            bool disconnected = false; int reads = 0;
            string ProjectPath() { reads++; if (disconnected) throw new ObjectDisposedException("Project"); return "C:/fixture.ap21"; }
            var capture = new HmiPreservationCapture(directory, ProjectPath(), "classic", HmiPath, "before");
            var nativeFailure = new InvalidCastException("System.String cannot be converted to HmiOpenLink", new ArgumentException("bad reference"));
            capture.Capture(() => capture.Run("classic-export", HmiPath + "/screens/Main", "native-1.xml", () => {
                disconnected = true; throw nativeFailure;
            }), () => 1);
            var manifest = Manifest(capture);
            var saved = JObject.Parse(File.ReadAllText(capture.SaveManifest(manifest)));
            Assert(reads == 1 && saved.Value<string>("projectPath") == "C:/fixture.ap21", "report uses cached identity");
            Assert(saved["nativeFailure"].Value<string>("type") == typeof(InvalidCastException).FullName, "original type survives");
            Assert(saved["nativeFailure"].Value<string>("message") == nativeFailure.Message, "original message survives");
            Assert(saved["nativeFailure"].Value<string>("exception").Contains("bad reference"), "inner failure retained");
            Assert(saved["artifacts"].Count() == 1 && saved["nativeFailure"].Value<int>("completedArtifactCount") == 1, "partial artifacts retained");
            Assert(saved["contentHash"].Type == JTokenType.Null && !HmiEngineeringRules.ScopeAccepted("hmi_classic_import", saved), "partial data cannot authorize");
        }));

        check("Export intent is readable on disk before dispatch; failure leaves no completion", () => InDirectory(directory => {
            var capture = Capture(directory);
            string identity = HmiPath + "/screens/Über\nquoted\"";
            capture.Capture(() => capture.Run("classic-export", identity, "native-1.xml", () => {
                var last = JObject.Parse(File.ReadAllLines(capture.ProgressPath).Last());
                Assert(last.Value<string>("state") == "started", "start persisted before dispatch");
                Assert(last["activeOperation"].Value<string>("objectPath") == identity, "JSON escaping retains exact identity");
                throw new InvalidOperationException("lost session");
            }), () => 1);
            var records = File.ReadAllLines(capture.ProgressPath).Select(JObject.Parse).ToArray();
            Assert(records.Select(r => r.Value<string>("state")).SequenceEqual(new[] { "captureStarted", "started", "captureFailed" }), "no false completed record");
            Assert(records.Select(r => r.Value<int>("sequence")).SequenceEqual(new[] { 1, 2, 3 }), "ordered evidence");
        }));

        check("Progress write failure prevents dispatch", () => InDirectory(directory => {
            int dispatched = 0;
            var capture = Capture(directory, record => { if (record.Value<string>("state") == "started") throw new IOException("disk full before dispatch"); });
            capture.Capture(() => capture.Run("classic-export", HmiPath, null, () => dispatched++), () => 0);
            Assert(dispatched == 0 && capture.Failure.Value<string>("type") == typeof(IOException).FullName, "no unjournaled call");
            Assert(Manifest(capture)["contentHash"].Type == JTokenType.Null, "no partial authorization");
        }));

        check("Failure to append diagnostics does not replace the native exception", () => InDirectory(directory => {
            var capture = Capture(directory, record => { if (record.Value<string>("state") == "captureFailed") throw new IOException("disk full after failure"); });
            capture.Capture(() => capture.Run("classic-export", HmiPath, null, () => { throw new InvalidCastException("original native error"); }), () => 0);
            var manifest = Manifest(capture);
            Assert(manifest["nativeFailure"].Value<string>("message") == "original native error", "primary failure unchanged");
            Assert(manifest["nativeFailure"]["progressWriteFailure"].Value<string>("message") == "disk full after failure", "secondary failure retained separately");
            Assert(File.Exists(capture.SaveManifest(manifest)), "manifest can still be retained");
        }));

        check("Manifest publication failure returns original native details", () => InDirectory(directory => {
            var capture = Capture(directory);
            capture.Capture(() => { throw new InvalidCastException("original native error"); }, () => 0);
            Directory.CreateDirectory(Path.Combine(directory, "manifest.json"));
            try { capture.SaveManifest(Manifest(capture)); throw new Exception("Expected publication failure"); }
            catch (BridgeException ex)
            {
                Assert(ex.Code == "HmiPreservationUnavailable" && ex.Message.Contains("original native error"), "original failure is visible");
                Assert(ex.Details["nativeFailure"].Value<string>("type") == typeof(InvalidCastException).FullName, "native type retained");
                Assert(ex.Details["manifestWriteFailure"] is JObject, "publication failure separate");
            }
        }));

        check("Successful captures retain exact hash and standing-note rules", () => InDirectory(directory => {
            var capture = Capture(directory);
            capture.Capture(() => capture.Run("classic-export", HmiPath + "/screens/Main", null, () => { }), () => 1);
            var manifest = Manifest(capture);
            var expected = HmiPreservationRules.ScopeHash("classic", new Dictionary<string, string> { [HmiPath + "/screens/Main"] = new string('a', 64) }, true);
            Assert(manifest.Value<string>("contentHash") == expected && manifest.Value<string>("observedArtifactsHash") == expected, "existing exact hash semantics");
            Assert(HmiEngineeringRules.ScopeAccepted("hmi_classic_import", manifest), "unchanged standing note accepted");
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_engineering", manifest), "closed kind remains closed");
            var extraGap = capture.Manifest("hmi_classic_import", Artifacts(), new JArray(HmiPreservationRules.ClassicExportNote, "unobserved content"), Targets());
            Assert(extraGap["contentHash"].Type == JTokenType.Null, "additional coverage gap stays closed");
            var mixed = capture.Manifest("hmi_classic_import", Artifacts(), new JArray(HmiPreservationRules.ClassicExportNote),
                new JArray(new JObject { ["family"] = "classic" }, new JObject { ["family"] = "unified" }));
            Assert(mixed["contentHash"].Type == JTokenType.Null, "other-family target remains unqualified");
        }));

        check("A Classic tag-table import scope excludes graphic lists only with its own note and policy", () => InDirectory(directory => {
            var capture = Capture(directory);
            capture.Capture(() => capture.Run("classic-export", HmiPath + "/screens/Main", null, () => { }), () => 1);
            string standing = HmiPreservationRules.ClassicExportNote, excluded = HmiPreservationRules.ClassicGraphicListExcludedNote;
            string policy = HmiPreservationRules.ClassicTagTablePolicy;
            var scoped = capture.Manifest("hmi_classic_import", Artifacts(), new JArray(standing, excluded), Targets(), policy);
            var expected = HmiPreservationRules.ScopeHash("classic", new Dictionary<string, string> { [HmiPath + "/screens/Main"] = new string('a', 64) }, true, policy);
            Assert(scoped.Value<string>("scopePolicy") == policy && !scoped.Value<bool>("complete"), "declared, incomplete scope");
            Assert(scoped.Value<string>("contentHash") == expected && HmiEngineeringRules.ScopeAccepted("hmi_classic_import", scoped), "accepted with its own hash");
            Assert(expected != Manifest(capture).Value<string>("contentHash"), "the policy is part of the content hash");
            var unscoped = capture.Manifest("hmi_classic_import", Artifacts(), new JArray(standing, excluded), Targets());
            Assert(unscoped["contentHash"].Type == JTokenType.Null, "the excluded note needs the tag-table policy");
            var noNote = capture.Manifest("hmi_classic_import", Artifacts(), new JArray(standing), Targets(), policy);
            Assert(noNote["contentHash"].Type == JTokenType.Null, "the policy needs the excluded note");
            var twoTargets = capture.Manifest("hmi_classic_import", Artifacts(), new JArray(standing, excluded, standing),
                new JArray(new JObject { ["family"] = "classic" }, new JObject { ["family"] = "classic" }), policy);
            Assert(twoTargets["contentHash"].Type == JTokenType.Null, "every Classic target records the excluded note");
            var withList = new JObject { [HmiPath + "/graphicLists/Status"] = new JObject { ["sha256"] = new string('b', 64), ["bytes"] = 3 } };
            var listed = capture.Manifest("hmi_classic_import", withList, new JArray(standing, excluded), Targets(), policy);
            Assert(listed["contentHash"].Type == JTokenType.Null, "a scope that claims to exclude graphic lists cannot contain one");
            var extra = capture.Manifest("hmi_classic_import", Artifacts(), new JArray(standing, excluded, "unobserved content"), Targets(), policy);
            Assert(extra["contentHash"].Type == JTokenType.Null, "another gap stays closed");
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_screen_edit", scoped) && !HmiEngineeringRules.ScopeAccepted("hmi_engineering", scoped), "other kinds refuse the scope");
            try { capture.Manifest("hmi_classic_screen_edit", Artifacts(), new JArray(standing, excluded), Targets(), policy); throw new Exception("Expected policy refusal"); }
            catch (ArgumentException) { }
        }));

        check("Only the screen edit and the Classic tag-table import scope accept resolved compile findings", () => {
            var tagTable = new JObject { ["scopePolicy"] = HmiPreservationRules.ClassicTagTablePolicy };
            var whole = new JObject { ["scopePolicy"] = null };
            Assert(HmiEngineeringRules.AllowsResolvedCompileProblems("hmi_classic_screen_edit", null), "screen edit");
            Assert(HmiEngineeringRules.AllowsResolvedCompileProblems("hmi_classic_import", tagTable), "tag-table import scope");
            Assert(!HmiEngineeringRules.AllowsResolvedCompileProblems("hmi_classic_import", whole), "whole-HMI Classic import stays exact");
            Assert(!HmiEngineeringRules.AllowsResolvedCompileProblems("hmi_classic_import", null), "no manifest stays exact");
            foreach (string kind in new[] { "hmi_text_list_import", "hmi_external_tag_create", "hmi_engineering" })
                Assert(!HmiEngineeringRules.AllowsResolvedCompileProblems(kind, tagTable), kind + " stays exact");
        });

        check("Nested capture phases identify the failing export, not its enclosing target", () => InDirectory(directory => {
            var capture = Capture(directory);
            capture.Capture(() => capture.Run("capture-target", HmiPath, null, () => {
                capture.Run("classic-export", HmiPath + "/screens/First", "native-0.xml", () => { });
                capture.Run("classic-export", HmiPath + "/screens/Second", "native-1.xml", () => { throw new InvalidOperationException("lost session"); });
            }), () => 1);
            Assert(capture.Failure["activeOperation"].Value<string>("objectPath") == HmiPath + "/screens/Second", "precise failing identity");
        }));
    }
}
