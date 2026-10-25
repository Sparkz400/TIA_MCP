using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TiaMcp.Openness.V21;

internal static class CoordinatorTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("coordinator oracle path persists verified hashes through both stages with gates closed", () => {
            using var oracle = new ContentContractTests.OracleFixture(); var s = OracleScenario(oracle);
            s.ToLibraryApplied(); s.Coordinator = PlcComponentCoordinator.Open(s.Directory, s.Ops);
            Equal("library_applied", s.Coordinator.Status().Value<string>("state"));
            var imported = s.Next(); Equal("import_verified", imported.Value<string>("state"));
            Equal(PlcComponentContentContract.PolicyId, imported["importEvidence"]["versions"][0]["contentVerification"].Value<string>("policyId"));
            Equal(null, imported["importEvidenceAttempt"]["versions"][0]["contentVerification"]);
            Equal("graphical_prepared", s.Next().Value<string>("state")); s.Apply("graphical");
            var installed = s.Next(); Equal("installation_verified", installed.Value<string>("state"));
            Equal(false, installed.Value<bool>("productionApplySupported")); Equal(false, installed.Value<bool>("runtimeVerified"));
            Equal(true, installed["installationEvidence"]["versions"][0]["contentVerification"].Value<bool>("oracleRemainingTreeMatches"));
            Equal(s.Ops.InitialCheckpoint, installed["initialCheckpoint"].Value<string>("archivePath")); Equal(2, s.Ops.Applies);
        });
        test("oracle drift at immediate pre-typed recheck retains failed evidence and prevents typed preparation", () => {
            using var oracle = new ContentContractTests.OracleFixture(); var s = OracleScenario(oracle);
            s.ToLibraryApplied(); s.Next();
            oracle.Rewrite("installedOracle", d => d.Descendants("SW.Blocks.FB").Single().SetAttributeValue("ID", "changed"));
            Reject("ComponentReadbackMismatch", () => s.Next()); var stopped = s.Coordinator.Status();
            Equal("recovery_required", stopped.Value<string>("state")); Equal(0, s.Ops.TypedPreparations); Equal(1, s.Ops.Applies);
            Equal(oracle.Evidence.Value<string>("installedOracleSha256"), stopped["preTypedEvidenceAttempt"]["versions"][0].Value<string>("installedOracleSha256"));
            Equal(null, stopped["preTypedEvidence"]); Reject("ComponentRecoveryRequired", () => s.Next()); Equal(1, s.Ops.Applies);
        });
        test("same source base but changed native full oracle refuses import mutation and never replays", () => {
            using var oracle = new ContentContractTests.OracleFixture(); var s = OracleScenario(oracle); s.Next();
            // Native adapter invokes this same source-observation validator from
            // the existing library fingerprint inside pre-mutation exclusive access.
            s.Ops.BeforeApply = () => PlcComponentContentContract.ValidateSourceObservation(oracle.Pin,
                oracle.Pin.Value<string>("contentSha256"), new string('0', 64), "ComponentQualification");
            Reject("ComponentReadbackMismatch", () => s.Apply("library")); Equal(0, s.Ops.Applies); Equal(0, s.Ops.TypedPreparations);
            Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state")); Reject("ComponentOutcomeUnknown", () => s.Next());
        });
        test("changed retained oracle artifact before dispatch cannot enter native operation", () => {
            using var oracle = new ContentContractTests.OracleFixture(); var s = OracleScenario(oracle); s.Next();
            oracle.Rewrite("sourceOracle", d => d.Root.Add(new XElement("Unknown")));
            Reject("ComponentReadbackMismatch", () => s.Apply("library")); Equal(0, s.Ops.Applies);
            Equal(null, s.Coordinator.Status()["library"]["dispatch"]);
        });
        test("coordinator stages existing import then freshly prepared typed change with two reviews", () => {
            var s = new Scenario(); Equal("library_prepared", s.Next().Value<string>("state"));
            Reject("ComponentStepReviewRequired", () => s.Coordinator.Advance()); Equal(0, s.Ops.Applies);
            Equal("library_applied", s.Apply("library").Value<string>("state")); Equal(0, s.Ops.TypedPreparations);
            Equal("import_verified", s.Next().Value<string>("state"));
            Equal("graphical_prepared", s.Next().Value<string>("state")); Equal(2, s.Ops.ImportReads);
            Reject("ComponentStepReviewRequired", () => s.Coordinator.Advance(s.Coordinator.Status()["library"]["prepared"].Value<string>("previewHash")));
            Equal("graphical_applied", s.Apply("graphical").Value<string>("state")); Equal("installation_verified", s.Next().Value<string>("state"));
            Equal(2, s.Ops.Applies); Equal(1, s.Ops.TypedPreparations); Equal(1, s.Ops.InstallationReads);
            var status = s.Coordinator.Status(); Equal(false, status.Value<bool>("productionApplySupported")); Equal(false, status.Value<bool>("runtimeVerified")); Equal(false, status.Value<bool>("freshReopenVerified"));
            Equal(s.Ops.InitialCheckpoint, status["initialCheckpoint"].Value<string>("archivePath"));
            s.Next(); Equal(2, s.Ops.Applies);
        });
        test("standalone absent caller installs through two reviewed stages and retains first checkpoint", () => {
            var s = new Scenario(standalone: true); s.ToTypedPrepared(); s.Apply("graphical");
            var status = s.Next(); Equal("installation_verified", status.Value<string>("state")); Equal(2, s.Ops.Applies);
            Equal(false, status.Value<bool>("productionApplySupported")); Equal(false, status.Value<bool>("runtimeVerified"));
            Equal(s.Ops.InitialCheckpoint, status["initialCheckpoint"].Value<string>("archivePath"));
        });
        test("standalone caller appeared after import blocks typed preparation", () => {
            var s = new Scenario(standalone: true); s.ToLibraryApplied(); s.Ops.ImportMutation = r => r["callerExists"] = true;
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal(0, s.Ops.TypedPreparations); Equal(1, s.Ops.Applies);
        });
        test("standalone absence is rechecked immediately before typed preparation", () => {
            var s = new Scenario(standalone: true); s.ToLibraryApplied(); s.Next(); s.Ops.ImportMutation = r => r["callerPath"] = "/other";
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal(0, s.Ops.TypedPreparations);
        });
        test("standalone fresh caller creation effect cannot name a different FC", () => {
            var s = new Scenario(standalone: true); s.ToLibraryApplied(); s.Next();
            s.Ops.PreparedMutation = r => r["preview"]["effects"][1]["blockName"] = "OtherCaller";
            Reject("ComponentPreparationMismatch", () => s.Next()); Equal(1, s.Ops.Applies);
        });
        test("standalone readback must prove the FC identity after creation", () => {
            var s = new Scenario(standalone: true); s.ToTypedPrepared(); s.Apply("graphical"); s.Ops.InstallationMutation = r => r["caller"]["kind"] = "FB";
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal("recovery_required", s.Coordinator.Status().Value<string>("state"));
        });
        test("standalone lost typed response is never replayed after restart and retains initial recovery", () => {
            var s = new Scenario(standalone: true); s.ToTypedPrepared(); s.Ops.LoseResponse = true;
            Throws<IOException>(() => s.Apply("graphical")); s.Ops.Token = "new-session";
            var reopened = PlcComponentCoordinator.Open(s.Directory, s.Ops); Reject("ComponentFreshSessionRequired", () => reopened.Advance());
            Equal("graphical_applied", reopened.Reconcile().Value<string>("state")); Equal(2, s.Ops.Applies);
            Equal(s.Ops.InitialCheckpoint, reopened.Status()["initialCheckpoint"].Value<string>("archivePath"));
        });
        test("standalone contract cannot carry existing caller artifact", () => {
            var s = new Scenario(create: false, standalone: true);
            s.PreparedPlan["preview"]["integrationPlan"]["callerArtifact"] = new JObject { ["sha256"] = new string('c', 64) }; s.RehashPlan();
            Reject("InvalidComponentContract", () => PlcComponentCoordinator.Create(s.Root, s.PreparedPlan, s.Ops)); Equal(0, s.Ops.Applies);
        });
        test("coordinator native apply observes durable dispatch before entering mutation", () => {
            var s = new Scenario(); s.Next(); s.Ops.BeforeApply = () => {
                var stored = JObject.Parse(File.ReadAllText(s.Journal))["record"];
                Equal("dispatching_library", stored.Value<string>("state")); Equal(true, stored["library"]["dispatch"] != null);
            }; s.Apply("library");
        });
        test("lost library apply response is never replayed and reconciles exact completed receipt", () => {
            var s = new Scenario(); s.Next(); s.Ops.LoseResponse = true;
            Throws<IOException>(() => s.Apply("library")); Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state"));
            Reject("ComponentOutcomeUnknown", () => s.Next()); Equal(1, s.Ops.Applies);
            var restarted = PlcComponentCoordinator.Open(s.Directory, s.Ops);
            Equal("library_applied", restarted.Reconcile().Value<string>("state")); Equal(1, s.Ops.Applies);
            Equal("import_verified", restarted.Advance().Value<string>("state"));
        });
        test("lost typed apply response is status-reconciled without replay or replacing initial checkpoint", () => {
            var s = new Scenario(); s.ToTypedPrepared(); s.Ops.LoseResponse = true;
            Throws<IOException>(() => s.Apply("graphical")); Equal(2, s.Ops.Applies);
            var status = s.Coordinator.Reconcile(); Equal("graphical_applied", status.Value<string>("state"));
            Equal(s.Ops.InitialCheckpoint, status["initialCheckpoint"].Value<string>("archivePath"));
            Equal("installation_verified", s.Next().Value<string>("state")); Equal(2, s.Ops.Applies);
        });
        test("restart reconciles a durable receipt but refuses implicit native continuation", () => {
            var s = new Scenario(); s.Next(); s.Ops.LoseResponse = true; Throws<IOException>(() => s.Apply("library")); s.Ops.Token = "new-session";
            var reopened = PlcComponentCoordinator.Open(s.Directory, s.Ops);
            Equal("library_applied", reopened.Reconcile().Value<string>("state"));
            Reject("ComponentFreshSessionRequired", () => reopened.Advance()); Equal(1, s.Ops.Applies);
        });
        test("prepared handles from another session never dispatch", () => {
            var s = new Scenario(); s.Next(); s.Ops.Token = "new-session";
            Reject("ComponentFreshSessionRequired", () => s.Apply("library")); Equal(0, s.Ops.Applies);
        });
        test("nonterminal native status retains unknown outcome and never dispatches next step", () => {
            var s = new Scenario(); s.Next(); s.Ops.LoseResponse = true; Throws<IOException>(() => s.Apply("library"));
            s.Ops.StatusMutation = r => r["state"] = "applying";
            Equal("outcome_unknown", s.Coordinator.Reconcile().Value<string>("state")); Reject("ComponentOutcomeUnknown", () => s.Next()); Equal(0, s.Ops.TypedPreparations);
        });
        test("reported native recovery-required stops sequence but retains initial checkpoint", () => {
            var s = new Scenario(); s.Next(); s.Ops.LoseResponse = true; Throws<IOException>(() => s.Apply("library"));
            s.Ops.StatusMutation = r => { r["state"] = "recovery_required"; r["journal"]["state"] = "recovery_required"; };
            Equal("recovery_required", s.Coordinator.Reconcile().Value<string>("state"));
            Equal(s.Ops.InitialCheckpoint, s.Coordinator.Status()["recovery"]["archivePath"].Value<string>());
            Reject("ComponentRecoveryRequired", () => s.Next());
        });
        test("unrelated native receipt cannot reconcile a dispatched step", () => {
            var s = new Scenario(); s.Next(); s.Ops.LoseResponse = true; Throws<IOException>(() => s.Apply("library"));
            s.Ops.StatusMutation = r => r["changeId"] = "other";
            Reject("ComponentReceiptMismatch", () => s.Coordinator.Reconcile()); Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state"));
            Equal("other", s.Coordinator.Status()["library"]["reconciliation"].Value<string>("changeId"));
        });
        test("same preview applied under another dispatch key cannot reconcile", () => {
            var s = new Scenario(); s.Next(); s.Ops.LoseResponse = true; Throws<IOException>(() => s.Apply("library"));
            s.Ops.StatusMutation = r => r["journal"]["idempotencyKeyHash"] = LibraryJournalStore.Hash("different-owner-key");
            Reject("ComponentReceiptMismatch", () => s.Coordinator.Reconcile()); Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state")); Equal(1, s.Ops.Applies);
        });
        test("disk-form typed journal reconciles only the matching raw dispatch key", () => {
            var s = new Scenario(); s.ToTypedPrepared(); s.Ops.LoseResponse = true; Throws<IOException>(() => s.Apply("graphical"));
            string key = s.Coordinator.Status()["graphical"]["dispatch"].Value<string>("idempotencyKey");
            s.Ops.StatusMutation = r => { var native = (JObject)r.DeepClone(); native.Remove("componentIdempotencyKeyHash"); native["idempotencyKey"] = key; r.RemoveAll(); r["changeId"] = native["changeId"].DeepClone(); r["journal"] = native; r["recoveredFromDisk"] = true; };
            Equal("graphical_applied", s.Coordinator.Reconcile().Value<string>("state")); Equal(2, s.Ops.Applies);
        });
        test("initial checkpoint must exist and match recorded bytes before import is accepted", () => {
            var s = new Scenario(); s.Next(); s.Ops.ResultMutation = r => File.WriteAllText(s.Ops.InitialCheckpoint, "changed");
            Reject("ComponentCheckpointInvalid", () => s.Apply("library")); Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state")); Equal(0, s.Ops.TypedPreparations);
        });
        test("initial checkpoint cannot be absent from completed library receipt", () => {
            var s = new Scenario(); s.Next(); s.Ops.ResultMutation = r => ((JArray)r["journal"]["checkpoints"]).Clear();
            Reject("ComponentCheckpointMissing", () => s.Apply("library")); Equal(0, s.Ops.TypedPreparations);
        });
        test("checkpoint ordered after native mutation does not establish initial recovery", () => {
            var s = new Scenario(); s.Next(); s.Ops.ResultMutation = r => {
                var events = (JArray)r["journal"]["events"]; var first = events[0].DeepClone(); events[0] = events[1].DeepClone(); events[1] = first;
            }; Reject("ComponentCheckpointOrderInvalid", () => s.Apply("library")); Equal(0, s.Ops.TypedPreparations); Equal(true, s.Coordinator.Status()["initialCheckpoint"] == null);
        });
        test("library persistence evidence is mandatory before fresh typed preparation", () => {
            var s = new Scenario(); s.Next(); s.Ops.ResultMutation = r => ((JArray)r["journal"]["events"]).RemoveAt(3);
            Reject("ComponentCheckpointOrderInvalid", () => s.Apply("library")); Equal(0, s.Ops.TypedPreparations);
        });
        test("compiler errors are never accepted as successful import", () => {
            var s = new Scenario(); s.Next(); s.Ops.ResultMutation = r => r["journal"]["compile"]["errorCount"] = 1;
            Reject("ComponentOutcomeUnknown", () => s.Apply("library")); Equal(0, s.Ops.TypedPreparations);
        });
        test("zero-error summary with native Error diagnostics cannot advance or replay import", () => {
            var s = new Scenario(); s.Next(); s.Ops.ResultMutation = r => ((JArray)r["journal"]["compile"]["targets"][0]["messages"]).Add(
                new JObject { ["state"] = "Error", ["description"] = "The entered password confirmation does not match the password.", ["depth"] = 1 });
            Reject("ComponentOutcomeUnknown", () => s.Apply("library")); Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state"));
            Equal(0, s.Ops.TypedPreparations); Equal(1, s.Ops.Applies);
            Reject("ComponentOutcomeUnknown", () => s.Coordinator.Reconcile()); Equal(1, s.Ops.Applies);
        });
        test("typed receipt with incomplete compile diagnostics cannot claim completion", () => {
            var s = new Scenario(); s.ToTypedPrepared(); s.Ops.ResultMutation = r => r["compile"]["targets"][0]["messagesTruncated"] = true;
            Reject("ComponentOutcomeUnknown", () => s.Apply("graphical")); Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state"));
            Equal(2, s.Ops.Applies); Equal(0, s.Ops.InstallationReads);
        });
        test("exact dependency content drift blocks typed stage", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Ops.ImportMutation = r => r["versions"][0]["contentSha256"] = new string('f', 64);
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal("recovery_required", s.Coordinator.Status().Value<string>("state")); Equal(0, s.Ops.TypedPreparations);
        });
        test("rejected import retains unverified exact readback without advancing or replay", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Ops.ImportMutation = r => r["versions"][0]["contentSha256"] = new string('f', 64);
            Reject("ComponentReadbackMismatch", () => s.Next());
            var status = s.Coordinator.Status(); Equal("recovery_required", status.Value<string>("state"));
            Equal(new string('f', 64), status["importEvidenceAttempt"]["versions"][0].Value<string>("contentSha256"));
            Equal(true, status["importEvidence"] == null); Equal(0, s.Ops.TypedPreparations); Equal(1, s.Ops.Applies);
            Reject("ComponentRecoveryRequired", () => s.Next()); Equal(1, s.Ops.Applies);
        });
        test("rejected pre-typed recheck retains separate attempted evidence", () => {
            var s = new Scenario(standalone: true); s.ToLibraryApplied(); s.Next(); s.Ops.ImportMutation = r => r["callerExists"] = true;
            Reject("ComponentReadbackMismatch", () => s.Next()); var status = s.Coordinator.Status();
            Equal(true, status["preTypedEvidenceAttempt"].Value<bool>("callerExists")); Equal(true, status["preTypedEvidence"] == null);
            Equal("recovery_required", status.Value<string>("state")); Equal(0, s.Ops.TypedPreparations);
        });
        test("rejected installation readback retains evidence without verified claim", () => {
            var s = new Scenario(standalone: true); s.ToTypedPrepared(); s.Apply("graphical"); s.Ops.InstallationMutation = r => r["caller"]["kind"] = "FB";
            Reject("ComponentReadbackMismatch", () => s.Next()); var status = s.Coordinator.Status();
            Equal("FB", status["installationEvidenceAttempt"]["caller"].Value<string>("kind")); Equal(true, status["installationEvidence"] == null);
            Equal("recovery_required", status.Value<string>("state")); Equal(2, s.Ops.Applies);
        });
        test("same-GUID changed source content blocks typed stage", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Ops.ImportMutation = r => r["versions"][0]["sourceContentSha256"] = new string('f', 64);
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal(0, s.Ops.TypedPreparations);
        });
        test("unresolved exact native dependency use blocks typed stage", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Ops.ImportMutation = r => ((JArray)r["versions"][0]["objectPaths"]).Clear();
            Reject("ComponentReadbackMismatch", () => s.Next());
        });
        test("changed mapped tag type/address blocks fresh preparation", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Ops.ImportMutation = r => r["mappings"][0]["nativeTag"]["logicalAddress"] = "%I20.0";
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal(0, s.Ops.TypedPreparations);
        });
        test("caller edit after import verification is caught by immediate second readback", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Next(); s.Ops.ImportMutation = r => r["callerSha256"] = new string('f', 64);
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal(0, s.Ops.TypedPreparations);
        });
        test("typed prepare failure stops without repeat and keeps initial checkpoint", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Next(); s.Ops.FailTypedPrepare = true;
            Throws<IOException>(() => s.Next()); Equal("recovery_required", s.Coordinator.Status().Value<string>("state"));
            Reject("ComponentRecoveryRequired", () => s.Next()); Equal(1, s.Ops.TypedPreparations); Equal(1, s.Ops.Applies);
        });
        test("fresh typed preparation cannot rename planned DB or change supplied model", () => {
            var s = new Scenario(); s.ToLibraryApplied(); s.Next(); s.Ops.PreparedMutation = p => p["preview"]["effects"][0]["name"] = "OtherDb";
            Reject("ComponentPreparationMismatch", () => s.Next()); Equal(1, s.Ops.Applies);
        });
        test("typed successful return still requires native preservation and saved qualification", () => {
            var s = new Scenario(); s.ToTypedPrepared(); s.Ops.ResultMutation = r => r["typedQualification"]["preservationVerified"] = false;
            Reject("ComponentReadbackMismatch", () => s.Apply("graphical")); Equal("outcome_unknown", s.Coordinator.Status().Value<string>("state"));
        });
        test("instance number readback must equal fresh preparation rather than provisional plan", () => {
            var s = new Scenario(); s.ToTypedPrepared(); s.Apply("graphical"); s.Ops.InstallationMutation = r => r["instance"]["number"] = 2;
            Reject("ComponentReadbackMismatch", () => s.Next()); Equal("recovery_required", s.Coordinator.Status().Value<string>("state"));
        });
        test("whole caller native readback difference stops completion", () => {
            var s = new Scenario(); s.ToTypedPrepared(); s.Apply("graphical"); s.Ops.InstallationMutation = r => r["callerMatchesPrepared"] = false;
            Reject("ComponentReadbackMismatch", () => s.Next());
        });
        test("existing caller without the insertion preservation proof stops completion", () => {
            var s = new Scenario(); s.ToTypedPrepared(); s.Apply("graphical"); s.Ops.InstallationMutation = r => r.Remove("callerInsertion");
            Reject("ComponentReadbackMismatch", () => s.Next());
        });
        test("journal tampering fails closed on reopen", () => {
            var s = new Scenario(); var record = JObject.Parse(File.ReadAllText(s.Journal)); record["record"]["state"] = "installation_verified"; File.WriteAllText(s.Journal, record.ToString());
            Reject("ComponentJournalInvalid", () => PlcComponentCoordinator.Open(s.Directory, s.Ops));
        });
        test("project gate excludes simultaneous coordinator mutations", () => {
            var s = new Scenario(); string lockFile = System.IO.Directory.GetFiles(s.Root, "*.lock").Single();
            using (var held = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Reject("ComponentBusy", () => s.Next());
            Equal(0, s.Ops.Applies); Equal(0, s.Ops.LibraryPreparations);
        });
        test("write failure after native completion leaves durable dispatch and never replays", () => {
            var s = new Scenario(); s.Next(); FileStream held = null;
            s.Ops.AfterApply = () => held = new FileStream(s.Journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { Throws<IOException>(() => s.Apply("library")); } finally { held?.Dispose(); }
            var reopened = PlcComponentCoordinator.Open(s.Directory, s.Ops); Equal("dispatching_library", reopened.Status().Value<string>("state"));
            Reject("ComponentOutcomeUnknown", () => reopened.Advance()); Equal(1, s.Ops.Applies);
            Equal("library_applied", reopened.Reconcile().Value<string>("state")); Equal(1, s.Ops.Applies);
        });
        test("planner blockers cannot be ignored by coordinator", () => {
            var s = new Scenario(create: false); ((JArray)s.PreparedPlan["preview"]["integrationPlan"]["blockers"]).Add(new JObject { ["code"] = "NativeIdPreservationUnsupported" }); s.RehashPlan();
            Reject("ComponentPlanBlocked", () => PlcComponentCoordinator.Create(s.Root, s.PreparedPlan, s.Ops)); Equal(0, s.Ops.Applies);
        });
    }

    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(string code, Action action) { try { action(); } catch (PlcComponentException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    private static Scenario OracleScenario(ContentContractTests.OracleFixture oracle)
    {
        oracle.Bind(Fixture.Type, Fixture.Version, "Motor");
        var s = new Scenario(create: false, standalone: true);
        var pin = (JObject)s.PreparedPlan["preview"]["integrationPlan"]["versions"][0];
        foreach (var property in oracle.Pin.Properties()) pin[property.Name] = property.Value.DeepClone();
        s.RehashPlan(); s.Coordinator = PlcComponentCoordinator.Create(s.Root, s.PreparedPlan, s.Ops);
        s.Directory = Path.Combine(s.Root, s.Coordinator.Status().Value<string>("operationId"));
        Action<JObject> attach = result => {
            var evidence = (JObject)oracle.Evidence.DeepClone(); evidence["typeGuid"] = Fixture.Type; evidence["versionGuid"] = Fixture.Version;
            evidence["objectPaths"] = new JArray("/plc/blocks/Motor"); result["versions"][0] = evidence;
        };
        s.Ops.ImportMutation = attach; s.Ops.InstallationMutation = attach;
        return s;
    }

    private sealed class Scenario
    {
        internal string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/component-tests", Guid.NewGuid().ToString("N")));
        internal string Directory;
        internal string Journal => Path.Combine(Directory, "component-journal.json");
        internal PlcComponentCoordinator Coordinator;
        internal FakeOperations Ops;
        internal JObject PreparedPlan;
        internal Scenario(bool create = true, bool standalone = false)
        {
            System.IO.Directory.CreateDirectory(Root); var fixture = new Fixture(); if (standalone) fixture.Standalone(); var plan = fixture.Build(); if (!standalone) plan["callerArtifact"] = new JObject { ["sha256"] = new string('c', 64) };
            var preview = new JObject { ["kind"] = "library_integrate", ["projectPath"] = Path.Combine(Root, "Scratch.ap21"), ["payload"] = fixture.Payload, ["integrationPlan"] = plan };
            PreparedPlan = new JObject { ["changeId"] = "lib-plan", ["state"] = "prepared", ["preview"] = preview }; RehashPlan();
            Ops = new FakeOperations(Root, PreparedPlan); if (create) { Coordinator = PlcComponentCoordinator.Create(Root, PreparedPlan, Ops); Directory = Path.Combine(Root, Coordinator.Status().Value<string>("operationId")); }
        }
        internal void RehashPlan() => PreparedPlan["previewHash"] = LibraryJournalStore.Hash(PreparedPlan["preview"].ToString(Formatting.None));
        internal JObject Next() => Coordinator.Advance();
        internal JObject Apply(string step) => Coordinator.Advance(Coordinator.Status()[step]["prepared"].Value<string>("previewHash"));
        internal void ToLibraryApplied() { Next(); Apply("library"); }
        internal void ToTypedPrepared() { ToLibraryApplied(); Next(); Next(); }
    }

    private sealed class FakeOperations : IPlcComponentOperations
    {
        private readonly JObject _plan;
        private readonly Dictionary<string, JObject> _prepared = new();
        private readonly Dictionary<string, JObject> _results = new();
        internal string Token = "original-session";
        public string SessionToken => Token;
        internal string InitialCheckpoint;
        internal int Applies, TypedPreparations, LibraryPreparations, ImportReads, InstallationReads;
        internal bool LoseResponse, FailTypedPrepare;
        internal Action BeforeApply, AfterApply;
        internal Action<JObject> ResultMutation, StatusMutation, ImportMutation, InstallationMutation, PreparedMutation;
        internal FakeOperations(string root, JObject plan) { _plan = plan; InitialCheckpoint = Path.Combine(root, "initial.zap21"); File.WriteAllText(InitialCheckpoint, "original-complete-project"); }
        public JObject Prepare(JObject arguments)
        {
            bool library = arguments.Value<string>("kind") == "library_instantiate";
            if (library) LibraryPreparations++; else { TypedPreparations++; if (FailTypedPrepare) throw new IOException("typed preparation rejected"); }
            string id = library ? "lib-step" : "typed-step";
            var preview = new JObject { ["kind"] = arguments["kind"].DeepClone(), ["payload"] = arguments["payload"].DeepClone(), ["projectPath"] = _plan["preview"]["projectPath"].DeepClone() };
            if (!library) preview["effects"] = new JArray(new JObject { ["action"] = "createInstanceDB", ["name"] = "Motor_1", ["path"] = "/plc/blocks/Motor_1", ["number"] = 3, ["instanceOfName"] = "Motor" });
            if (!library && PlcLibraryIntegrationPlanner.CreatesStandaloneCaller((JObject)_plan["preview"]["payload"]))
            {
                ((JArray)preview["effects"]).Add(new JObject { ["action"] = "create", ["blockName"] = "MotorCaller" });
                preview["typed"] = new JObject { ["operation"] = "createBlock", ["blockPath"] = "/plc/blocks/MotorCaller" };
            }
            var p = new JObject { ["changeId"] = id, ["state"] = "prepared", ["preview"] = preview };
            PreparedMutation?.Invoke(p); p["previewHash"] = LibraryJournalStore.Hash(preview.ToString(Formatting.None)); _prepared[id] = (JObject)p.DeepClone(); return p;
        }
        public JObject Apply(JObject arguments)
        {
            BeforeApply?.Invoke(); Applies++; string id = arguments.Value<string>("changeId"); var p = _prepared[id]; bool library = id.StartsWith("lib-");
            var result = new JObject { ["changeId"] = id, ["previewHash"] = p["previewHash"].DeepClone(), ["state"] = "applied" };
            result["componentIdempotencyKeyHash"] = LibraryJournalStore.Hash(arguments.Value<string>("idempotencyKey"));
            if (library) result["journal"] = new JObject { ["changeId"] = id, ["previewHash"] = p["previewHash"].DeepClone(), ["state"] = "applied", ["idempotencyKeyHash"] = result["componentIdempotencyKeyHash"].DeepClone(), ["compile"] = SuccessfulCompilation(),
                ["checkpoints"] = new JArray(new JObject { ["kind"] = "project", ["originalPath"] = _plan["preview"]["projectPath"].DeepClone(), ["archivePath"] = InitialCheckpoint, ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(InitialCheckpoint))) }),
                ["events"] = new JArray(new JObject { ["stage"] = "checkpoint_project_archive", ["state"] = "completed" }, new JObject { ["stage"] = "native_mutation", ["state"] = "dispatching" }, new JObject { ["stage"] = "compile", ["state"] = "completed" }, new JObject { ["stage"] = "save_project", ["state"] = "completed" }) };
            else { result["compile"] = SuccessfulCompilation(); result["typedQualification"] = new JObject { ["engineeringVerified"] = true, ["preservationVerified"] = true, ["saved"] = true }; result["recoveryArchive"] = "later-typed-checkpoint"; }
            ResultMutation?.Invoke(result); _results[id] = (JObject)result.DeepClone(); AfterApply?.Invoke();
            if (LoseResponse) throw new IOException("native operation response lost"); return result;
        }
        public JObject Status(string id) { var result = (JObject)_results[id].DeepClone(); StatusMutation?.Invoke(result); return result; }
        private static JObject SuccessfulCompilation() => new JObject { ["errorCount"] = 0, ["targets"] = new JArray(
            new JObject { ["target"] = "/plc", ["state"] = "Success", ["errorCount"] = 0, ["messages"] = new JArray(), ["messagesTruncated"] = false }) };
        public JObject VerifyImport(JObject contract)
        {
            ImportReads++; var result = Evidence(contract); ImportMutation?.Invoke(result); return result;
        }
        public JObject VerifyInstallation(JObject contract, JObject typedPreparation)
        {
            InstallationReads++; var result = Evidence(contract);
            result["callerExists"] = true; result["caller"] = new JObject { ["kind"] = "FC", ["language"] = "FBD" };
            result["callerSha256"] = new string('d', 64); result["callerMatchesPrepared"] = true; result["instanceMatchesPrepared"] = true; result["callerInsertion"] = new JObject { ["verified"] = true };
            result["instance"] = new JObject { ["path"] = "/plc/blocks/Motor_1", ["number"] = 3, ["instanceOfName"] = "Motor", ["contentSha256"] = new string('e', 64) };
            InstallationMutation?.Invoke(result); return result;
        }
        private static JObject Evidence(JObject contract) {
            var result = new JObject { ["projectPath"] = contract["projectPath"].DeepClone(), ["plcPath"] = "/plc", ["scopeComplete"] = true,
            ["versions"] = new JArray(((JArray)contract["plan"]["versions"]).Select(v => new JObject { ["typeGuid"] = v["typeGuid"].DeepClone(), ["versionGuid"] = v["versionGuid"].DeepClone(), ["contentSha256"] = v["contentSha256"].DeepClone(), ["sourceContentSha256"] = v["contentSha256"].DeepClone(), ["objectPaths"] = new JArray("/plc/blocks/Motor") })),
            ["mappings"] = contract["plan"]["mappings"].DeepClone(), ["callerSha256"] = contract["plan"]["callerArtifact"]?["sha256"]?.DeepClone() };
            if (PlcLibraryIntegrationPlanner.CreatesStandaloneCaller((JObject)contract["payload"])) {
                result.Remove("callerSha256"); result["callerExists"] = false; result["callerPath"] = "/plc/blocks/MotorCaller";
            }
            return result;
        }
    }
}
