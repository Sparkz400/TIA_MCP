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

    static JObject BatchJournal(bool rollback = false, int attempted = 2)
    {
        var journal = Journal(rollback);
        var requests = new JArray();
        var receipts = new JArray();
        for (int i = 0; i < 3; i++)
        {
            var target = new JObject { ["projectCopyId"] = "copy", ["sessionGeneration"] = "session", ["family"] = "classic",
                ["hmiPath"] = Hmi, ["objectPath"] = Hmi + "/screens/Screen" + i, ["kind"] = "hmiScreen" };
            requests.Add(new JObject { ["target"] = target, ["xml"] = "<Document><Hmi.Screen.Screen><AttributeList><Name>Screen" + i + "</Name></AttributeList></Hmi.Screen.Screen></Document>" });
            string state = rollback ? i < attempted ? "rollback_readback_verified" : "prepared" : "readback_verified";
            receipts.Add(new JObject { ["target"] = target.DeepClone(), ["state"] = state });
            if (state != "prepared")
            {
                ((JArray)journal["stages"]).Add("hmi_screen_" + i + "_import_started");
                ((JArray)journal["stages"]).Add("hmi_screen_" + i + "_" + state);
            }
        }
        var payload = (JObject)journal["preview"]["payload"];
        payload["target"] = requests[0]["target"].DeepClone(); payload["xml"] = requests[0]["xml"].DeepClone();
        payload["additionalScreens"] = new JArray(requests.Skip(1));
        journal["hmiEvidence"]["screens"] = receipts;
        journal["hmiEvidence"]["proposedEffect"] = new JObject { ["screenCount"] = 3 };
        return journal;
    }

    public static void Run(Action<string, Action> check)
    {
        void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected recovery refusal"); }
        check("Screen recovery and composition reject file links and linked ancestors", () => {
            string root = Path.Combine(Path.GetTempPath(), "screen-links-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string outside = Path.Combine(root, "outside"), link = Path.Combine(root, "linked");
            Directory.CreateDirectory(outside);
            string file = Path.Combine(outside, "native.xml"); File.WriteAllText(file, "retained");
            try
            {
                ClassicScreenRecoveryRules.RequireUnlinkedArtifact(file);
                if (OperatingSystem.IsWindows())
                {
                    var start = new System.Diagnostics.ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
                    foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path $env:TIA_TEST_LINK -Target $env:TIA_TEST_TARGET | Out-Null" }) start.ArgumentList.Add(argument);
                    start.Environment["TIA_TEST_LINK"] = link; start.Environment["TIA_TEST_TARGET"] = outside;
                    using var process = System.Diagnostics.Process.Start(start);
                    if (!process.WaitForExit(30000) || process.ExitCode != 0) throw new Exception("Junction fixture failed");
                }
                else Directory.CreateSymbolicLink(link, outside);
                Reject(() => ClassicScreenRecoveryRules.RequireUnlinkedArtifact(Path.Combine(link, "native.xml")));
                if (!OperatingSystem.IsWindows())
                {
                    string fileLink = Path.Combine(root, "file.xml"); File.CreateSymbolicLink(fileLink, file);
                    Reject(() => ClassicScreenRecoveryRules.RequireUnlinkedArtifact(fileLink));
                }
                Directory.Delete(link);
                if (File.ReadAllText(file) != "retained") throw new Exception("Link target changed");
            }
            finally { Directory.Delete(root, true); }
        });
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
        check("Batch reconciliation requires every member receipt even when the list is removed or malformed", () => {
            foreach (bool rollback in new[] { false, true })
            {
                ClassicScreenRecoveryRules.TerminalManifest(BatchJournal(rollback));
                foreach (JToken damage in new JToken[] { null, JValue.CreateNull(), new JObject(), new JArray() })
                {
                    var journal = BatchJournal(rollback);
                    if (damage == null) ((JObject)journal["hmiEvidence"]).Remove("screens");
                    else journal["hmiEvidence"]["screens"] = damage.DeepClone();
                    Reject(() => ClassicScreenRecoveryRules.TerminalManifest(journal));
                }
            }
        });
        check("Batch reconciliation binds receipt order, identity and each native dispatch/readback stage", () => {
            foreach (bool rollback in new[] { false, true })
                foreach (string damage in new[] { "identity", "state", "dispatch", "readback", "untouchedDispatch", "reordered", "scalar" })
                {
                    var journal = BatchJournal(rollback);
                    var members = (JArray)journal["hmiEvidence"]["screens"];
                    var stages = (JArray)journal["stages"];
                    if (damage == "identity") members[0]["target"]["objectPath"] = Hmi + "/screens/Foreign";
                    if (damage == "state") members[0]["state"] = "import_started";
                    if (damage == "dispatch") stages.First(x => (string)x == "hmi_screen_0_import_started").Remove();
                    if (damage == "readback") stages.First(x => (string)x == "hmi_screen_0_" + (rollback ? "rollback_" : "") + "readback_verified").Remove();
                    if (damage == "untouchedDispatch") { members[2]["state"] = "prepared"; stages.Add("hmi_screen_2_import_started"); }
                    if (damage == "reordered") members[0] = members[1].DeepClone();
                    if (damage == "scalar") members[0] = "invalid";
                    Reject(() => ClassicScreenRecoveryRules.TerminalManifest(journal));
                }
            Reject(() => ClassicScreenRecoveryRules.TerminalManifest(BatchJournal(true, 0)));
            ClassicScreenRecoveryRules.TerminalManifest(BatchJournal(true, 3));
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
