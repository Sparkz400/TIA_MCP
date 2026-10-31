using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Implementations must bind these methods to the existing prepared-change
    // operations. This coordinator does not create an alternate native write path.
    internal interface IPlcComponentOperations
    {
        string SessionToken { get; }
        JObject Prepare(JObject arguments);
        JObject Apply(JObject arguments);
        JObject Status(string changeId);
        JObject VerifyImport(JObject contract);
        JObject VerifyInstallation(JObject contract, JObject typedPreparation);
    }

    /// <summary>Internal qualification coordinator. No production tool dispatches it.</summary>
    internal sealed class PlcComponentCoordinator
    {
        private readonly string _directory, _journalPath, _resourceLock;
        private readonly IPlcComponentOperations _operations;
        private readonly string _id;

        private PlcComponentCoordinator(string directory, string id, IPlcComponentOperations operations, string projectPath, bool admittedWindow = false)
        {
            _directory = directory; _id = id; _operations = operations;
            _journalPath = Path.Combine(directory, "component-journal.json");
            _resourceLock = admittedWindow ? Path.Combine(directory, "component.lock") : Path.Combine(Path.GetDirectoryName(directory), "component-project-" + LibraryJournalStore.Hash(projectPath.ToUpperInvariant()) + ".lock");
        }

        internal static PlcComponentCoordinator Create(string root, JObject preparedPlan, IPlcComponentOperations operations, string admittedId = null, string admittedDirectory = null)
        {
            if (operations == null || string.IsNullOrWhiteSpace(operations.SessionToken)) Fail("ComponentSessionRequired", "A current native session identity is required.");
            var contract = ValidateContract(preparedPlan);
            if (!Path.IsPathRooted(root)) Fail("InvalidComponentDirectory", "Coordinator storage must be an absolute owned directory.");
            root = Path.GetFullPath(root); Directory.CreateDirectory(root);
            string id = admittedId ?? "component-" + Guid.NewGuid().ToString("N");
            string directory = admittedDirectory ?? Path.Combine(root, id); Directory.CreateDirectory(directory);
            if (File.Exists(Path.Combine(directory, "component-journal.json"))) Fail("ComponentOutcomeUnknown", "An existing coordinator is inspected, never recreated.");
            var coordinator = new PlcComponentCoordinator(directory, id, operations, contract.Value<string>("projectPath"), admittedDirectory != null);
            using (coordinator.Lock())
            {
                coordinator.Write(new JObject {
                    ["schemaVersion"] = 1, ["operationId"] = id, ["state"] = "created", ["sessionToken"] = operations.SessionToken,
                    ["contract"] = contract, ["contractSha256"] = LibraryJournalStore.Hash(contract.ToString(Formatting.None)),
                    ["library"] = new JObject(), ["graphical"] = new JObject(), ["events"] = new JArray(),
                    ["automaticReplayAllowed"] = false, ["atomic"] = false, ["productionApplySupported"] = false, ["admittedWindow"] = admittedDirectory != null,
                    ["freshReopenVerified"] = false, ["recoveryVerified"] = false, ["runtimeVerified"] = false
                }, "created");
            }
            return coordinator;
        }

        internal static PlcComponentCoordinator Open(string directory, IPlcComponentOperations operations)
        {
            directory = Path.GetFullPath(directory);
            var record = ReadChecked(Path.Combine(directory, "component-journal.json"));
            return new PlcComponentCoordinator(directory, record.Value<string>("operationId"), operations, record["contract"].Value<string>("projectPath"), record.Value<bool?>("admittedWindow") == true);
        }

        internal JObject Status() { using (Lock()) return Read(); }

        // Derive a recovery-only view from the exact durable first inner receipt. This never advances installation or rewrites an uncertain coordinator outcome.
        internal JObject RecoveryAnchor(JObject firstInnerStatus)
        {
            using (Lock())
            {
                var record = Read(); var prepared = record["library"]?["prepared"] as JObject;
                if (prepared == null) Fail("ComponentCheckpointMissing", "No exact first inner import exists.");
                ValidateReceiptIdentity(prepared, firstInnerStatus); CaptureInitialCheckpoint(record, firstInnerStatus, true);
                record["firstInnerRecoveryStatus"] = firstInnerStatus.DeepClone(); record["recoveryOnly"] = true; return record;
            }
        }

        // Each invocation crosses at most one native boundary. In particular,
        // preparation returns for review; applying requires its exact preview hash.
        internal JObject Advance(string approvedStepPreviewHash = null)
        {
            using (Lock())
            {
                var record = Read(); string state = record.Value<string>("state");
                if (state == "installation_verified") return record;
                if (record.Value<string>("sessionToken") != _operations.SessionToken) Fail("ComponentFreshSessionRequired", "Prepared native handles expired. Reconcile dispatched outcomes read-only and retain the initial checkpoint; automatic continuation in a new session is unavailable.");
                if (state == "dispatching_library" || state == "dispatching_graphical" || state == "outcome_unknown") Fail("ComponentOutcomeUnknown", "A dispatched operation must be reconciled, never replayed.");
                if (state == "created") return Reused(record) ? Prepare(record, "graphical") : Prepare(record, "library");
                if (state == "library_prepared") return Apply(record, "library", approvedStepPreviewHash);
                if (state == "library_applied") return Verify(record, false);
                if (state == "import_verified") return Prepare(record, "graphical");
                if (state == "graphical_prepared") return Apply(record, "graphical", approvedStepPreviewHash);
                if (state == "graphical_applied") return Verify(record, true);
                Fail("ComponentRecoveryRequired", "The coordinator stopped at " + state + ". Inspect its durable evidence and initial checkpoint. It cannot repeat failed stages.");
                return null;
            }
        }

        // A reused FB has no import step: its one typed step is index 0 and its own checkpoint anchors recovery.
        private static bool Reused(JObject record) => record["contract"]["plan"]["fb"]?.Value<bool?>("reused") == true;
        private static int StepIndex(JObject record, string step) => step == "library" ? 0 : ((JArray)record["contract"]["plan"]["steps"]).Count - 1;

        private JObject Prepare(JObject record, string step)
        {
            try
            {
                // Source/caller/tag/dependency checks are repeated immediately
                // before typed preparation, not reused from the earlier gate.
                if (step == "graphical")
                {
                    var latest = _operations.VerifyImport((JObject)record["contract"].DeepClone());
                    record["preTypedEvidenceAttempt"] = latest.DeepClone();
                    Write(record, "pre_typed_readback_captured_unverified");
                    ValidateImportEvidence(record, latest); ValidateOriginalCaller(record, latest);
                    record["preTypedEvidence"] = latest.DeepClone();
                }
                var arguments = (JObject)record["contract"]["plan"]["steps"][StepIndex(record, step)]["prepareArguments"].DeepClone();
                var prepared = _operations.Prepare(arguments);
                ValidatePrepared(record, step, prepared);
                record[step]["prepared"] = prepared.DeepClone(); record[step]["prepareArguments"] = arguments;
                record["state"] = step + "_prepared";
                return Write(record, step + "_prepared");
            }
            catch (Exception error)
            {
                // Before the first dispatch nothing changed, so a failed preparation needs no recovery.
                record["state"] = record["library"]["dispatch"] == null ? "preparation_failed" : "recovery_required";
                record["failure"] = Failure(error); Write(record, step + "_prepare_failed"); throw;
            }
        }

        private JObject Apply(JObject record, string step, string approval)
        {
            var row = (JObject)record[step]; var prepared = (JObject)row["prepared"];
            string previewHash = prepared.Value<string>("previewHash");
            if (approval != previewHash) Fail("ComponentStepReviewRequired", "Applying this step requires its exact freshly prepared previewHash.");
            if (row["dispatch"] != null) Fail("ComponentOutcomeUnknown", "A component step is dispatched at most once.");
            foreach (JObject version in (JArray)record["contract"]["plan"]["versions"]) PlcComponentContentContract.ValidatePin(version);
            var request = new JObject { ["changeId"] = prepared["changeId"].DeepClone(), ["previewHash"] = previewHash,
                ["idempotencyKey"] = _id + ":" + step, ["checkpointSave"] = true, ["compile"] = true, ["save"] = true };
            row["dispatch"] = request; record["state"] = "dispatching_" + step;
            // Durable intent always precedes handing control to a mutating API.
            Write(record, step + "_dispatching");
            try
            {
                var result = _operations.Apply((JObject)request.DeepClone());
                row["result"] = result.DeepClone();
                AcceptApplied(record, step, result);
                record["state"] = step + "_applied";
                return Write(record, step + "_applied");
            }
            catch (Exception error)
            {
                // Includes a lost response, validation failure or failed final
                // journal write. Even a reported preflight failure is not replayed.
                record["state"] = "outcome_unknown"; record["uncertainStep"] = step; record["failure"] = Failure(error);
                Write(record, step + "_outcome_unknown"); throw;
            }
        }

        internal JObject Reconcile()
        {
            using (Lock())
            {
                var record = Read(); string state = record.Value<string>("state");
                string step = state == "dispatching_library" ? "library" : state == "dispatching_graphical" ? "graphical" : record.Value<string>("uncertainStep");
                if (state != "outcome_unknown" && state != "dispatching_library" && state != "dispatching_graphical") return record;
                if (step != "library" && step != "graphical") Fail("ComponentJournalInvalid", "Uncertain state has no exact dispatched step.");
                var prepared = (JObject)record[step]["prepared"];
                var observed = _operations.Status(prepared.Value<string>("changeId"));
                record[step]["reconciliation"] = observed.DeepClone();
                Write(record, step + "_status_observed");
                ValidateReceiptIdentity(prepared, observed);
                if (step == "library") CaptureInitialCheckpoint(record, observed, false);
                else if (Reused(record)) CaptureTypedCheckpoint(record, observed["journal"] as JObject ?? observed, false);
                string nativeState = (observed.Value<string>("state") ?? observed["journal"]?.Value<string>("state"));
                if (nativeState == "applied")
                {
                    AcceptApplied(record, step, observed); record["state"] = step + "_applied";
                    record.Remove("uncertainStep");
                }
                else
                {
                    record["state"] = nativeState == "recovery_required" || nativeState == "restored" || nativeState == "restored_to_new_locations" ? "recovery_required" : "outcome_unknown";
                    record["uncertainStep"] = step;
                }
                return Write(record, step + "_reconciled_read_only");
            }
        }

        private JObject Verify(JObject record, bool installed)
        {
            try
            {
                var contract = (JObject)record["contract"].DeepClone();
                var evidence = installed ? _operations.VerifyInstallation(contract, (JObject)record["graphical"]["prepared"].DeepClone()) : _operations.VerifyImport(contract);
                // Preserve observations even when strict validation rejects them.
                // Attempt evidence is never a verified state or permission to advance.
                record[installed ? "installationEvidenceAttempt" : "importEvidenceAttempt"] = evidence.DeepClone();
                Write(record, installed ? "installation_readback_captured_unverified" : "import_readback_captured_unverified");
                ValidateImportEvidence(record, evidence);
                if (installed)
                {
                    if (contract["plan"]["multiInstance"] is JObject multi)
                    {
                        var member = evidence["multiInstance"] as JObject;
                        if (member == null || member.Value<string>("callerPath") != multi.Value<string>("callerPath") || member.Value<string>("name") != multi.Value<string>("name") || member.Value<string>("dataType") != multi.Value<string>("dataType") || evidence.Value<bool?>("callerMatchesPrepared") != true || evidence["instance"] != null)
                            Fail("ComponentReadbackMismatch", "Caller or its exact new multi-instance member differs from the freshly prepared typed result.");
                    }
                    else
                    {
                        var effect = ((JArray)record["graphical"]["prepared"]["preview"]["effects"]).Single(e => e.Value<string>("action") == "createInstanceDB");
                        var instance = evidence["instance"] as JObject;
                        if (instance == null || instance.Value<string>("path") != effect.Value<string>("path") || instance.Value<int?>("number") != effect.Value<int?>("number") || instance.Value<string>("instanceOfName") != effect.Value<string>("instanceOfName") || evidence.Value<bool?>("callerMatchesPrepared") != true || evidence.Value<bool?>("instanceMatchesPrepared") != true)
                            Fail("ComponentReadbackMismatch", "Caller or exact instance identity/content differs from the freshly prepared typed result.");
                        RequireHash(instance.Value<string>("contentSha256"));
                    }
                    RequireHash(evidence.Value<string>("callerSha256"));
                    if (PlcLibraryIntegrationPlanner.CreatesStandaloneCaller((JObject)contract["payload"]) &&
                        (evidence.Value<bool?>("callerExists") != true || evidence.Value<string>("callerPath") != PlcLibraryIntegrationPlanner.CallerPath((JObject)contract["payload"]) ||
                         evidence["caller"]?.Value<string>("kind") != "FC" || evidence["caller"]?.Value<string>("language") != contract["payload"]["integration"]["caller"].Value<string>("language")))
                        Fail("ComponentReadbackMismatch", "Standalone caller must resolve as the exact newly created FC in the requested language.");
                    if (!PlcLibraryIntegrationPlanner.CreatesStandaloneCaller((JObject)contract["payload"]) && evidence["callerInsertion"]?.Value<bool?>("verified") != true)
                        Fail("ComponentReadbackMismatch", "Existing caller lacks the original-vs-installed insertion preservation proof.");
                }
                else ValidateOriginalCaller(record, evidence);
                record[installed ? "installationEvidence" : "importEvidence"] = evidence.DeepClone();
                record["state"] = installed ? "installation_verified" : "import_verified";
                return Write(record, installed ? "installation_readback_verified" : "import_readback_verified");
            }
            catch (Exception error) { record["state"] = "recovery_required"; record["failure"] = Failure(error); Write(record, "readback_failed"); throw; }
        }

        private static void ValidateImportEvidence(JObject record, JObject evidence)
        {
            var contract = (JObject)record["contract"]; var target = contract["payload"]["target"];
            if (evidence?.Value<string>("projectPath") != contract.Value<string>("projectPath") || evidence.Value<string>("plcPath") != target.Value<string>("plcPath") || evidence.Value<bool?>("scopeComplete") != true)
                Fail("ComponentReadbackMismatch", "Readback must cover the exact project, PLC and complete native version-use scope.");
            var expected = (JArray)contract["plan"]["versions"]; var actual = evidence["versions"] as JArray;
            if (actual == null || actual.Count != expected.Count || actual.Select(v => v.Value<string>("versionGuid")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != actual.Count)
                Fail("ComponentReadbackMismatch", "Readback must contain the complete exact dependency closure once.");
            foreach (var pin in expected)
            {
                var matches = actual.Where(v => v.Value<string>("typeGuid") == pin.Value<string>("typeGuid") && v.Value<string>("versionGuid") == pin.Value<string>("versionGuid")).ToArray();
                if (matches.Length != 1 || matches[0].Value<string>("sourceContentSha256") != pin.Value<string>("contentSha256") || !(matches[0]["objectPaths"] is JArray paths) || paths.Count == 0 || paths.Any(p => p.Type != JTokenType.String || !((string)p).StartsWith(target.Value<string>("plcPath") + "/", StringComparison.Ordinal)))
                    Fail("ComponentReadbackMismatch", "An exact dependency identity, source/installed content hash or native PLC use link differs.");
                if (pin["contentPolicy"] != null)
                    ((JObject)matches[0])["contentVerification"] = PlcComponentContentContract.Validate((JObject)pin, (JObject)matches[0]);
                else if (matches[0].Value<string>("contentSha256") != pin.Value<string>("contentSha256"))
                    Fail("ComponentReadbackMismatch", "Strict dependency raw content differs; no content policy applies.");
            }
            if (!JToken.DeepEquals(evidence["mappings"], contract["plan"]["mappings"])) Fail("ComponentReadbackMismatch", "Mapped native tag paths, names, types or addresses changed.");
            // The root FB has exactly one use, the planned block, and keeps the block number of its first readback.
            var fb = (JObject)contract["plan"]["fb"]; var root = actual.Single(v => v.Value<string>("typeGuid") == expected.Single(p => p.Value<string>("typeName") == fb.Value<string>("name")).Value<string>("typeGuid"));
            if (!(root["objectPaths"] is JArray rootPaths) || rootPaths.Count != 1 || (string)rootPaths[0] != fb.Value<string>("path") ||
                evidence["fb"]?.Value<string>("path") != fb.Value<string>("path") || evidence["fb"]?["number"]?.Type != JTokenType.Integer)
                Fail("ComponentReadbackMismatch", "The root FB must have exactly one native use, the planned block " + fb.Value<string>("path") + ".");
            if (record["fbNumber"] != null && record.Value<int>("fbNumber") != evidence["fb"].Value<int>("number")) Fail("ComponentReadbackMismatch", "The root FB's block number changed between readbacks.");
            record["fbNumber"] = evidence["fb"].Value<int>("number");
        }

        private static void ValidateOriginalCaller(JObject record, JObject evidence)
        {
            var payload = (JObject)record["contract"]["payload"];
            if (PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(payload))
            {
                if (evidence.Value<bool?>("callerExists") != false || evidence.Value<string>("callerPath") != PlcLibraryIntegrationPlanner.CallerPath(payload) || evidence["callerSha256"] != null)
                    Fail("ComponentReadbackMismatch", "The standalone caller must remain absent until freshly prepared typed creation.");
                return;
            }
            if (evidence.Value<string>("callerSha256") != record["contract"]["plan"]["callerArtifact"].Value<string>("sha256"))
                Fail("ComponentReadbackMismatch", "The exact caller payload changed before typed preparation.");
        }

        private static void ValidatePrepared(JObject record, string step, JObject prepared)
        {
            if (prepared?.Value<string>("state") != "prepared" || string.IsNullOrWhiteSpace(prepared.Value<string>("changeId"))) Fail("ComponentPreparationMismatch", "Existing operation did not produce a prepared native change.");
            RequireHash(prepared.Value<string>("previewHash"));
            var preview = prepared["preview"] as JObject;
            if (preview == null || LibraryJournalStore.Hash(preview.ToString(Formatting.None)) != prepared.Value<string>("previewHash")) Fail("ComponentPreparationMismatch", "Prepared preview hash does not bind the returned preview.");
            var arguments = record["contract"]["plan"]["steps"][StepIndex(record, step)]["prepareArguments"];
            if (preview.Value<string>("kind") != arguments.Value<string>("kind") || !JToken.DeepEquals(preview["payload"], arguments["payload"])) Fail("ComponentPreparationMismatch", "Preparation changed the exact reviewed step kind or payload.");
            string path = preview.Value<string>("projectPath") ?? preview["project"]?.Value<string>("path");
            if (path != record["contract"].Value<string>("projectPath")) Fail("ComponentPreparationMismatch", "Prepared step belongs to a different project.");
            if (step == "graphical")
            {
                var effects = preview["effects"] as JArray ?? new JArray();
                var instances = effects.Where(e => e.Value<string>("action") == "createInstanceDB").ToArray();
                var members = effects.Where(e => e.Value<string>("action") == "createMultiInstance").ToArray();
                if (record["contract"]["plan"]["multiInstance"] is JObject multi)
                {
                    if (instances.Length != 0 || members.Length != 1 || members[0].Value<string>("callerPath") != multi.Value<string>("callerPath") || members[0].Value<string>("member") != multi.Value<string>("name") || members[0].Value<string>("dataType") != multi.Value<string>("dataType"))
                        Fail("ComponentPreparationMismatch", "Fresh typed preparation must create exactly the reviewed multi-instance in the caller FB and no instance DB.");
                }
                else if (members.Length != 0 || instances.Length != 1 || instances[0].Value<string>("name") != record["contract"]["plan"]["instanceDb"].Value<string>("name") || instances[0].Value<string>("path") != record["contract"]["plan"]["instanceDb"].Value<string>("path") || instances[0].Value<int?>("number") == null || instances[0].Value<int?>("number") < 1 || instances[0].Value<int?>("number") > 65535)
                    Fail("ComponentPreparationMismatch", "Fresh typed preparation must create exactly the reviewed instance DB; its native number is separately reviewed in this preview.");
                var payload = (JObject)record["contract"]["payload"];
                if (PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(payload))
                {
                    var creations = effects.Where(e => e.Value<string>("action") == "create").ToArray();
                    if (creations.Length != 1 || creations[0].Value<string>("blockName") != payload["integration"]["caller"].Value<string>("name") ||
                        preview["typed"]?.Value<string>("operation") != "createBlock" || preview["typed"]?.Value<string>("blockPath") != PlcLibraryIntegrationPlanner.CallerPath(payload))
                        Fail("ComponentPreparationMismatch", "Fresh typed preparation must create the exact reviewed standalone caller without editing an existing caller.");
                }
            }
        }

        private static void AcceptApplied(JObject record, string step, JObject observed)
        {
            ValidateReceiptIdentity((JObject)record[step]["prepared"], observed);
            var journal = observed["journal"] as JObject ?? observed;
            if ((observed.Value<string>("state") ?? journal.Value<string>("state")) != "applied" || !CompilationEvidenceRules.IsSuccessful(journal["compile"] as JObject))
                Fail("ComponentOutcomeUnknown", "A matching completed native receipt with successful compilation is required.");
            string keyHash = journal.Value<string>("idempotencyKeyHash") ?? observed.Value<string>("componentIdempotencyKeyHash");
            if (keyHash == null && journal["idempotencyKey"]?.Type == JTokenType.String) keyHash = LibraryJournalStore.Hash(journal.Value<string>("idempotencyKey"));
            if (keyHash != LibraryJournalStore.Hash(record[step]["dispatch"].Value<string>("idempotencyKey"))) Fail("ComponentReceiptMismatch", "Applied receipt belongs to a different dispatch idempotency key.");
            if (step == "library")
            {
                var events = journal["events"] as JArray ?? new JArray();
                int archive = EventIndex(events, "checkpoint_project_archive", "completed"), mutation = EventIndex(events, "native_mutation", "dispatching"), compile = EventIndex(events, "compile", "completed"), save = EventIndex(events, "save_project", "completed");
                if (archive < 0 || mutation <= archive || compile <= mutation || save <= compile) Fail("ComponentCheckpointOrderInvalid", "Native library receipt must show initial checkpoint completion before mutation, then compilation and project persistence.");
                CaptureInitialCheckpoint(record, observed, true);
            }
            else
            {
                var qualification = journal["typedQualification"];
                if (qualification?.Value<bool?>("engineeringVerified") != true || qualification.Value<bool?>("preservationVerified") != true || qualification.Value<bool?>("saved") != true)
                    Fail("ComponentReadbackMismatch", "Typed step lacks compiler, preservation, instance-readback or save evidence.");
                if (Reused(record)) CaptureTypedCheckpoint(record, journal);
            }
        }

        private static int EventIndex(JArray events, string stage, string state)
        {
            for (int i = 0; i < events.Count; i++) if (events[i].Value<string>("stage") == stage && events[i].Value<string>("state") == state) return i;
            return -1;
        }

        private static void ValidateReceiptIdentity(JObject prepared, JObject observed)
        {
            var journal = observed?["journal"] as JObject ?? observed;
            if (journal == null || (observed.Value<string>("changeId") ?? journal.Value<string>("changeId")) != prepared.Value<string>("changeId") || (observed.Value<string>("previewHash") ?? journal.Value<string>("previewHash")) != prepared.Value<string>("previewHash"))
                Fail("ComponentReceiptMismatch", "Native result/status does not match the exact dispatched change and preview hash.");
        }

        private static void CaptureInitialCheckpoint(JObject record, JObject observed, bool required)
        {
            var journal = observed["journal"] as JObject ?? observed;
            var checkpoints = journal["checkpoints"] as JArray;
            var matches = checkpoints?.Where(c => c.Value<string>("kind") == "project" && c.Value<string>("originalPath") == record["contract"].Value<string>("projectPath")).ToArray();
            if (matches == null || matches.Length != 1) { if (required) Fail("ComponentCheckpointMissing", "First native library step must retain one initial checkpoint of the complete target project."); return; }
            var checkpoint = (JObject)matches[0].DeepClone(); RequireHash(checkpoint.Value<string>("sha256"));
            var events = journal["events"] as JArray ?? new JArray();
            int archive = EventIndex(events, "checkpoint_project_archive", "completed"), mutation = EventIndex(events, "native_mutation", "dispatching");
            if (archive < 0 || mutation >= 0 && archive >= mutation) Fail("ComponentCheckpointOrderInvalid", "The alleged initial project checkpoint is not recorded before mutation dispatch.");
            string path = checkpoint.Value<string>("archivePath");
            if (!Path.IsPathRooted(path ?? "") || !File.Exists(path) || FileHash(path) != checkpoint.Value<string>("sha256")) Fail("ComponentCheckpointInvalid", "Initial native checkpoint is missing or its bytes changed.");
            checkpoint["changeId"] = record["library"]["prepared"]["changeId"].DeepClone(); checkpoint["previewHash"] = record["library"]["prepared"]["previewHash"].DeepClone();
            if (record["initialCheckpoint"] != null && !JToken.DeepEquals(checkpoint, record["initialCheckpoint"])) Fail("ComponentCheckpointInvalid", "Initial checkpoint cannot be replaced by the later typed-step checkpoint.");
            record["initialCheckpoint"] = checkpoint;
            record["recovery"] = new JObject { ["initialLibraryChangeId"] = checkpoint["changeId"].DeepClone(), ["initialPreviewHash"] = checkpoint["previewHash"].DeepClone(),
                ["archivePath"] = checkpoint["archivePath"].DeepClone(), ["archiveSha256"] = checkpoint["sha256"].DeepClone(),
                ["requiresEmptyOwnedPortal"] = true, ["retrievesToNewLocation"] = true, ["automaticRecovery"] = false,
                ["instructions"] = "Preserve current projects and all journals. Use the first library change's existing change_restore in a new empty owned Portal; never substitute the later typed checkpoint, close user projects, or replay uncertain mutations. Verify fresh-reopen native content independently." };
        }

        // A reused FB's only step is the typed edit, so the archive it takes before its mutation is the initial checkpoint.
        private static void CaptureTypedCheckpoint(JObject record, JObject journal, bool required = true)
        {
            string path = journal.Value<string>("recoveryArchive"), hash = journal.Value<string>("recoveryArchiveSha256");
            if (path == null && !required) return;
            var stages = (journal["stages"] as JArray ?? new JArray()).Select(s => (string)s).ToList();
            int archive = stages.IndexOf("recovery_archive_created"), mutation = stages.IndexOf("mutation_applied");
            if (archive < 0 || mutation >= 0 && mutation <= archive || required && mutation < 0) Fail("ComponentCheckpointOrderInvalid", "The typed receipt must record its project archive before its mutation.");
            RequireHash(hash);
            if (!Path.IsPathRooted(path ?? "") || !File.Exists(path) || FileHash(path) != hash) Fail("ComponentCheckpointInvalid", "The typed step's checkpoint is missing or its bytes changed.");
            var prepared = record["graphical"]["prepared"];
            var checkpoint = new JObject { ["kind"] = "project", ["originalPath"] = record["contract"]["projectPath"].DeepClone(), ["archivePath"] = path, ["sha256"] = hash,
                ["changeId"] = prepared["changeId"].DeepClone(), ["previewHash"] = prepared["previewHash"].DeepClone() };
            if (record["initialCheckpoint"] != null && !JToken.DeepEquals(checkpoint, record["initialCheckpoint"])) Fail("ComponentCheckpointInvalid", "The initial checkpoint cannot be replaced.");
            record["initialCheckpoint"] = checkpoint;
            record["recovery"] = new JObject { ["initialTypedChangeId"] = checkpoint["changeId"].DeepClone(), ["initialPreviewHash"] = checkpoint["previewHash"].DeepClone(),
                ["archivePath"] = path, ["archiveSha256"] = hash, ["requiresEmptyOwnedPortal"] = true, ["retrievesToNewLocation"] = true, ["automaticRecovery"] = false,
                ["instructions"] = "Preserve current projects and all journals. The reused FB was not imported, so the typed step's own pre-mutation archive is the initial checkpoint; retrieve it into a new location in an empty owned Portal and never replay uncertain mutations." };
        }

        private static JObject ValidateContract(JObject prepared)
        {
            var preview = prepared?["preview"] as JObject; var plan = preview?["integrationPlan"] as JObject;
            if (prepared?.Value<string>("state") != "prepared" || preview?.Value<string>("kind") != "library_integrate" || plan?.Value<bool?>("applySupported") != false || prepared.Value<string>("previewHash") != LibraryJournalStore.Hash(preview.ToString(Formatting.None)))
                Fail("InvalidComponentContract", "Coordinator requires the exact hashed plan-only prepared library integration response.");
            if (plan["blockers"] is not JArray blockers || blockers.Count != 0) Fail("ComponentPlanBlocked", "Resolve every planner blocker before preparing a qualification sequence.");
            if (plan["steps"] is JArray proposed && proposed.Any(s => s.Value<bool?>("planOnly") != null)) Fail("ComponentPlanBlocked", "The plan has planOnly steps (see planOnlyReasons); the coordinator cannot dispatch or read them back.");
            string[] kinds = plan["fb"]?.Value<bool?>("reused") == true ? new[] { "graphical_typed_edit" } : new[] { "library_instantiate", "graphical_typed_edit" };
            if (plan["steps"] is not JArray steps || !steps.Select(s => s.Value<string>("kind")).SequenceEqual(kinds)) Fail("InvalidComponentContract", "Exactly the existing library import and fresh typed edit are allowed; a fully reused FB has the typed edit only.");
            if (!Path.IsPathRooted(preview.Value<string>("projectPath") ?? "")) Fail("InvalidComponentContract", "An exact native target project path is required.");
            PlcLibraryIntegrationPlanner.ValidatePayload((JObject)preview["payload"]);
            var payload = (JObject)preview["payload"];
            if (PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(payload))
            {
                if (plan["caller"]?.Value<bool?>("initiallyAbsent") != true || plan["caller"]?.Value<string>("blockPath") != PlcLibraryIntegrationPlanner.CallerPath(payload) || plan["callerArtifact"] != null ||
                    plan["steps"][1]["prepareArguments"]["payload"]["model"]?.Value<string>("operation") != "createBlock")
                    Fail("InvalidComponentContract", "Standalone caller contract requires exact initial absence and typed creation.");
            }
            else RequireHash(plan["callerArtifact"]?.Value<string>("sha256"));
            foreach (JObject version in (JArray)plan["versions"]) PlcComponentContentContract.ValidatePin(version);
            return new JObject { ["planChangeId"] = prepared["changeId"].DeepClone(), ["planPreviewHash"] = prepared["previewHash"].DeepClone(), ["projectPath"] = preview["projectPath"].DeepClone(), ["payload"] = preview["payload"].DeepClone(), ["plan"] = plan.DeepClone() };
        }

        /// <summary>Admission reconciliation of a production combined apply's step workspace, worker-owned scratch of its steps: every
        /// coordinator journal must verify its own hash, and every checkpoint archive must be recorded with its hash by the step journal
        /// beside it, so the steps' own records account for what was written. "unchanged", "verifiedApplied" or null (held, basis is why).</summary>
        internal static string ReconcileWorkspace(string workspace, JObject evidence, out string basis)
        {
            if (File.Exists(workspace)) { basis = "The step workspace path is a file."; return null; }
            if (!Directory.Exists(workspace) || !Directory.EnumerateFileSystemEntries(workspace).Any())
            { basis = "The step workspace was never written; no component step ran."; return "unchanged"; }
            var states = new JArray(); evidence["coordinatorStates"] = states;
            foreach (string journal in Directory.GetFiles(workspace, "component-journal.json", SearchOption.AllDirectories))
            {
                try { states.Add(ReadChecked(journal).Value<string>("state")); }
                catch (Exception error) when (error is PlcComponentException || error is IOException || error is JsonException)
                { basis = "Coordinator journal " + journal + " is unreadable or its hash differs: " + error.Message; return null; }
            }
            var archives = new JArray(); evidence["checkpointArchives"] = archives;
            foreach (string archive in Directory.GetFiles(workspace, "*.za?21", SearchOption.AllDirectories))
            {
                string directory = Path.GetDirectoryName(archive), name = Path.GetFileName(directory), path = Path.Combine(directory, "journal.json"), hash = FileHash(archive);
                bool recorded = false;
                try
                {
                    if (name.StartsWith("change-lib-", StringComparison.Ordinal))
                        recorded = (LibraryJournalStore.Read(path, name.Substring("change-".Length))["checkpoints"] as JArray ?? new JArray()).OfType<JObject>()
                            .Any(c => SamePath(c.Value<string>("archivePath"), archive) && c.Value<string>("sha256") == hash);
                    else if (name.StartsWith("change-", StringComparison.Ordinal) && File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024)
                    {
                        var step = JObject.Parse(File.ReadAllText(path));
                        recorded = "change-" + step.Value<string>("changeId") == name && SamePath(step.Value<string>("recoveryArchive"), archive) && step.Value<string>("recoveryArchiveSha256") == hash;
                    }
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is JsonException) { recorded = false; }
                if (!recorded) { basis = "Checkpoint archive " + archive + " is missing from, or differs from, its step journal."; return null; }
                archives.Add(new JObject { ["path"] = archive, ["sha256"] = hash });
            }
            basis = "Worker-owned step scratch of the combined apply: every coordinator journal verifies its hash and every checkpoint archive is recorded with its hash by its step journal.";
            return "verifiedApplied";
        }
        private static bool SamePath(string left, string right) =>
            !string.IsNullOrEmpty(left) && Path.IsPathRooted(left) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

        private IDisposable Lock()
        {
            try { return new FileStream(_resourceLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new PlcComponentException("ComponentBusy", "Another coordinator holds the project gate: " + error.Message); }
        }
        private JObject Read() { var record = ReadChecked(_journalPath); if (record.Value<string>("operationId") != _id) Fail("ComponentJournalInvalid", "Journal operation identity changed."); return record; }
        private static JObject ReadChecked(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) Fail("ComponentJournalInvalid", "Journal is missing or exceeds 8 MiB.");
            // Keep date-looking strings verbatim: a reparsed DateTime reserializes "...8245720Z" as "...824572Z" and breaks the hash.
            JObject envelope; using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None }) envelope = JObject.Load(reader);
            var record = envelope["record"] as JObject;
            if (record == null || envelope.Value<string>("sha256") != LibraryJournalStore.Hash(record.ToString(Formatting.None)) || record.Value<int?>("schemaVersion") != 1 || record["contract"] == null || record.Value<string>("contractSha256") != LibraryJournalStore.Hash(record["contract"].ToString(Formatting.None))) Fail("ComponentJournalInvalid", "Journal or immutable contract hash does not match.");
            return record;
        }
        private JObject Write(JObject record, string stage)
        {
            ((JArray)record["events"]).Add(new JObject { ["stage"] = stage, ["state"] = record["state"].DeepClone(), ["utc"] = DateTime.UtcNow });
            LibraryJournalStore.Write(_journalPath, new JObject { ["record"] = record, ["sha256"] = LibraryJournalStore.Hash(record.ToString(Formatting.None)) });
            return (JObject)record.DeepClone();
        }
        private static JObject Failure(Exception error) => new JObject { ["code"] = (error as PlcComponentException)?.Code ?? "ComponentStageFailed", ["message"] = error.Message };
        private static void RequireHash(string value) { if (value == null || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{64}$")) Fail("ComponentEvidenceInvalid", "Expected a SHA-256 evidence hash."); }
        private static string FileHash(string path) { using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static void Fail(string code, string message) => throw new PlcComponentException(code, message);
    }

    internal sealed class PlcComponentException : Exception
    {
        internal string Code { get; }
        internal PlcComponentException(string code, string message) : base(message) { Code = code; }
    }
}

