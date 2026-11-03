using Newtonsoft.Json.Linq;
using TiaMcp.Model;
using TiaMcp.Openness.V21;

static class ClassicScreenRecoveryTests
{
    const string Hmi = "/projects/Test/devices/HMI/hmi/Runtime";
    static JObject Manifest(char selected = 'a', char peer = 'b')
    {
        var capture = new HmiPreservationCapture("unused", "p.ap21", "classic", Hmi, "test", _ => { });
        JObject Artifact(char hash) => new() { ["sha256"] = new string(hash, 64), ["contentSha256"] = new string(hash, 64) };
        return capture.Manifest("hmi_classic_screen_edit", new JObject { [Hmi + "/screens/Main"] = Artifact(selected), [Hmi + "/screens/Peer"] = Artifact(peer) },
            new JArray(HmiPreservationRules.ClassicScreenExportNote), new JArray(new JObject { ["family"] = "classic", ["hmiPath"] = Hmi }));
    }
    static JObject Compile() => new() { ["errorCount"] = 0, ["warningCount"] = 0, ["targets"] = new JArray(new JObject {
        ["target"] = Hmi, ["errorCount"] = 0, ["warningCount"] = 0, ["messagesTruncated"] = false,
        ["messages"] = new JArray(new JObject { ["description"] = "Complete", ["path"] = Hmi, ["state"] = "Information", ["depth"] = 0 }) }) };
    static JObject Journal(bool rollback = false) => new() {
        ["kind"] = "hmi_classic_screen_edit", ["state"] = rollback ? "rolled_back" : "applied", ["afterFingerprint"] = new string('a', 64),
        ["preview"] = new JObject { ["payload"] = new JObject { ["operation"] = "classic_xml_import" } },
        ["compile"] = Compile(),
        ["stages"] = rollback ? new JArray("recovery_archive_created", "hmi_rollback_imported", "hmi_rollback_compiled", "hmi_rollback_saved", "hmi_rollback_verified")
            : new JArray("recovery_archive_created", "hmi_precise_readback_verified", "compile_finished", "hmi_unrelated_content_preserved", "changed_project_saved", "hmi_saved_screen_content_verified"),
        ["hmiEvidence"] = new JObject { ["baselineCompile"] = Compile(), ["rollbackCompile"] = Compile(), ["after-save"] = Manifest(), ["rollback-after-save"] = Manifest() } };

    public static void Run(Action<string, Action> check)
    {
        void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected recovery refusal"); }
        check("Classic recovery accepts only complete saved terminal outcomes, including verified rollback", () => {
            foreach (bool rollback in new[] { false, true }) {
                var journal = Journal(rollback);
                ClassicScreenRecoveryRules.TerminalManifest(journal);
                foreach (var stage in ((JArray)journal["stages"]).Values<string>()) {
                    var broken = (JObject)journal.DeepClone();
                    ((JArray)broken["stages"]).First(n => n.Value<string>() == stage).Remove();
                    Reject(() => ClassicScreenRecoveryRules.TerminalManifest(broken));
                }
            }
            foreach (string state in new[] { "applying", "checkpointing", "recovery_required", "restoring", "prepared", "not_applied" }) {
                var journal = Journal(); journal["state"] = state; Reject(() => ClassicScreenRecoveryRules.TerminalManifest(journal));
            }
            var other = Journal(); other["kind"] = "hmi_classic_import"; Reject(() => ClassicScreenRecoveryRules.TerminalManifest(other));
            other = Journal(); other["preview"]["payload"]["operation"] = "scalar_property_set"; Reject(() => ClassicScreenRecoveryRules.TerminalManifest(other));
            other = Journal(); other.Remove("afterFingerprint"); Reject(() => ClassicScreenRecoveryRules.TerminalManifest(other));
        });
        check("Classic recovery refuses compiler errors, changed warnings and incomplete diagnostics", () => {
            foreach (string failure in new[] { "error", "warning", "truncated", "missingBaseline", "missingTarget" }) {
                var journal = Journal(); var compile = (JObject)journal["compile"];
                if (failure == "error") compile["errorCount"] = 1;
                if (failure == "warning") compile["warningCount"] = 1;
                if (failure == "truncated") compile["targets"][0]["messagesTruncated"] = true;
                if (failure == "missingBaseline") ((JObject)journal["hmiEvidence"]).Remove("baselineCompile");
                if (failure == "missingTarget") compile["targets"] = new JArray();
                Reject(() => ClassicScreenRecoveryRules.TerminalManifest(journal));
            }
        });
        check("Rollback cannot erase changed peers and completion requires exact original content", () => {
            var before = Manifest(); var failed = Manifest('c');
            ClassicScreenRecoveryRules.RequireRollbackScope(before, failed, Hmi + "/screens/Main");
            Reject(() => ClassicScreenRecoveryRules.RequireRollbackScope(before, Manifest('c', 'd'), Hmi + "/screens/Main"));
            Reject(() => ClassicScreenRecoveryRules.RequireRestoredScope(before, failed));
            ClassicScreenRecoveryRules.RequireRestoredScope(before, Manifest());
            foreach (string damage in new[] { "hash", "observedHash", "artifact", "gap", "scope", "empty" }) {
                var bad = Manifest();
                if (damage == "hash") bad["contentHash"] = new string('f', 64);
                if (damage == "observedHash") bad["observedArtifactsHash"] = new string('f', 64);
                if (damage == "artifact") bad["artifacts"][Hmi + "/screens/Peer"]["contentSha256"] = new string('f', 64);
                if (damage == "gap") ((JArray)bad["missingCoverage"]).Add("screen export missing");
                if (damage == "scope") bad["scopePolicy"] = "other";
                if (damage == "empty") bad["artifacts"] = new JObject();
                Reject(() => ClassicScreenRecoveryRules.RequireRestoredScope(before, bad));
            }
        });
    }
}
