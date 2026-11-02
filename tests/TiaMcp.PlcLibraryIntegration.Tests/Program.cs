using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TiaMcp.Openness.V21;

if (args.Length == 2 && args[0] == "--scale-qualification")
    return ScaleQualification.Run(args[1]);

int passed = 0, failed = 0;
void Test(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void Reject(string code, Action<Fixture> change) { var f = new Fixture(); change(f); try { f.Build(); } catch (PlcIntegrationPlanException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }

Test("FBD motor plan emits independently preparable pinned import and typed instance/call steps", () => {
    var f = new Fixture(); var p = f.Build();
    Equal(false, p.Value<bool>("applySupported")); Equal(false, p.Value<bool>("nativeMutationDispatched"));
    Equal("planned_only", p.Value<string>("status")); Equal(2, ((JArray)p["steps"]).Count);
    Equal("library_instantiate", p["steps"][0].Value<string>("kind"));
    Equal("graphical_typed_edit", p["steps"][1].Value<string>("kind"));
    var second = (JObject)p["steps"][1]["prepareArguments"]["payload"];
    TypedGraphicalPayload.Validate(second);
    Equal("createGlobal", second["instances"][0].Value<string>("kind"));
    Equal("/plc/blocks/Motor", second["model"]["nodes"][0].Value<string>("calleePath"));
    Equal("/plc/tags/IO", second["model"]["operands"][0].Value<string>("ownerPath"));
    Equal("call", second["model"]["connections"][1]["from"].Value<string>("node"));
    Equal("Active", second["model"]["connections"][1]["from"].Value<string>("port"));
    Equal(2, p["instanceDb"].Value<int>("provisionalNumber")); Equal(false, p["recoveryPlan"].Value<bool>("separateStepsAreAtomic"));
});
Test("LAD explicit unconditional rail and InOut wiring validate against current typed payload", () => {
    var f = new Fixture(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = "LAD"); f.Inventory["objects"][0]["language"] = "LAD";
    f.Fb.Descendants().Single(e => e.Name.LocalName == "Section" && (string)e.Attribute("Name") == "Output").SetAttributeValue("Name", "InOut");
    f.Payload["integration"]["mappings"][1]["direction"] = "InOut";
    var p = f.Build(); var typed = (JObject)p["steps"][1]["prepareArguments"]["payload"]; TypedGraphicalPayload.Validate(typed);
    Equal("powerrail", typed["model"]["nodes"][0].Value<string>("kind")); Equal("call", typed["model"]["connections"][2]["to"].Value<string>("node"));
});
Test("native following IDs are explicit blockers, not a false executable plan", () => {
    var f = new Fixture(); f.Caller.Root.Element("SW.Blocks.FC").Element("ObjectList").Add(new XElement("MultilingualText", new XAttribute("ID", "9")));
    Equal("NativeIdPreservationUnsupported", f.Build()["blockers"][0].Value<string>("code"));
});
Test("predictable following ID shift no longer blocks existing-caller insertion", () => {
    var f = new Fixture(); f.Caller = XDocument.Parse("<Document><SW.Blocks.FC ID=\"0\"><AttributeList><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID=\"1\"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit><MultilingualText ID=\"2\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"3\" /></ObjectList></MultilingualText></ObjectList></SW.Blocks.FC></Document>");
    f.Payload["integration"]["caller"]["insertAfterNetworkId"] = "1"; Equal(0, ((JArray)f.Build()["blockers"]).Count);
});
Test("OB caller exported as another block kind rejects", () => Reject("IntegrationCallerMismatch", f => f.Inventory["objects"][0]["kind"] = "OB"));
Test("plan does not mutate payload or captured evidence", () => { var f = new Fixture(); string before = f.Payload.ToString() + f.Inventory.ToString() + f.Versions.ToString() + f.Fb.ToString() + f.Caller.ToString(); f.Build(); Equal(before, f.Payload.ToString() + f.Inventory.ToString() + f.Versions.ToString() + f.Fb.ToString() + f.Caller.ToString()); });
Test("dependency versions all pinned", () => { var f = new Fixture(); f.AddDependency(); Equal(2, ((JArray)f.Build()["versions"]).Count); });
Test("case variants of valid GUID pins match native identities", () => { var f = new Fixture(); f.Payload["selection"][0]["typeGuid"] = Fixture.Type.ToUpperInvariant(); Equal("planned_only", f.Build().Value<string>("status")); });
Test("indexed dependency closure preserves type-version pairing", () => Reject("DependencyPinMismatch", f => {
    f.AddDependency(); var root = f.Payload["selection"][0]; var dependency = f.Payload["integration"]["dependencyPins"][0];
    var rootVersion = root["versionGuid"].DeepClone(); root["versionGuid"] = dependency["versionGuid"].DeepClone(); dependency["versionGuid"] = rootVersion;
}));
Test("indexed closure preserves case-insensitive native edges and GUID identities", () => {
    var f = new Fixture(); f.AddDependency();
    foreach (JObject version in f.Versions) { version["typeGuid"] = version.Value<string>("typeGuid").ToUpperInvariant(); version["versionGuid"] = version.Value<string>("versionGuid").ToUpperInvariant(); }
    Equal(2, ((JArray)f.Build()["versions"]).Count);
});
Test("unmatched native type does not inspect its malformed version before pin mismatch", () => Reject("DependencyPinMismatch", f => {
    f.Versions[0]["typeGuid"] = "cccccccc-cccc-cccc-cccc-cccccccccccc"; f.Versions[0]["versionGuid"] = "malformed";
}));
Test("matching native type still rejects a malformed version", () => Reject("InvalidIntegrationRecipe", f => f.Versions[0]["versionGuid"] = "malformed"));
Test("indexed dependency collision remains case insensitive", () => Reject("LibraryConflict", f => {
    f.AddDependency(); ((JArray)f.Inventory["objects"]).Add(new JObject { ["name"] = f.Versions[1].Value<string>("typeName").ToUpperInvariant() });
}));
Test("missing dependency pin rejects", () => Reject("DependencyPinMismatch", f => { f.AddDependency(); ((JArray)f.Payload["integration"]["dependencyPins"]).Clear(); }));
Test("wrong version rejects", () => Reject("DependencyPinMismatch", f => f.Payload["selection"][0]["versionGuid"] = Guid.NewGuid().ToString()));
Test("duplicate root pin rejects", () => Reject("DependencyConflict", f => ((JArray)f.Payload["integration"]["dependencyPins"]).Add(f.Payload["selection"][0].DeepClone())));
Test("two versions of one dependency type rejects", () => Reject("DependencyConflict", f => { f.AddDependency(); f.Versions[1]["typeGuid"] = Fixture.Type; }));
Test("missing dependency edge target rejects", () => Reject("DependencyUnresolved", f => ((JArray)f.Versions[0]["requiredVersionGuids"]).Add(Guid.NewGuid().ToString())));
Test("missing native content proof rejects", () => Reject("DependencyUnresolved", f => ((JObject)f.Versions[0]).Remove("contentSha256")));
Test("in-work dependencies reject", () => Reject("DependencyConflict", f => f.Versions[0]["state"] = "InWork"));
Test("DoNotUse dependencies reject", () => Reject("DependencyConflict", f => f.Versions[0]["doNotUse"] = true));
Test("same-name different-GUID dependency rejects", () => Reject("LibraryConflict", f => { f.AddDependency(); f.Versions[1]["typeName"] = "motor"; }));
Test("same-name installed content is never adopted", () => Reject("LibraryConflict", f => ((JArray)f.Inventory["objects"]).Add(new JObject { ["name"] = "MOTOR" })));
Test("planned DB and imported FB cannot collide", () => Reject("LibraryConflict", f => f.Payload["integration"]["instanceDb"]["name"] = "Motor"));
Test("existing DB collision rejects", () => Reject("LibraryConflict", f => f.Payload["integration"]["instanceDb"]["name"] = "ExistingDb"));
Test("incomplete inventory rejects", () => Reject("IntegrationInventoryIncomplete", f => f.Inventory["complete"] = false));
Test("missing parameter rejects", () => Reject("IntegrationMappingMismatch", f => ((JArray)f.Payload["integration"]["mappings"]).RemoveAt(1)));
Test("duplicate parameter rejects", () => Reject("IntegrationMappingMismatch", f => f.Payload["integration"]["mappings"][1] = f.Payload["integration"]["mappings"][0].DeepClone()));
Test("wrong parameter direction rejects", () => Reject("IntegrationMappingMismatch", f => f.Payload["integration"]["mappings"][1]["direction"] = "Input"));
Test("tag type differs from native rejects", () => Reject("OperandTypeMismatch", f => f.Inventory["tags"][1]["dataType"] = "Int"));
Test("missing exact tag path rejects", () => Reject("IntegrationTagMissing", f => f.Payload["integration"]["mappings"][0]["tagPath"] = "/wrong"));
Test("duplicate emitted tag name rejects", () => Reject("AmbiguousOperand", f => { var tag = f.Inventory["tags"][0].DeepClone(); tag["path"] = "/other"; ((JArray)f.Inventory["tags"]).Add(tag); }));
Test("safety tags reject", () => Reject("SafetyBoundary", f => f.Inventory["tags"][0]["isSafety"] = true));
Test("output cannot write physical input tags", () => Reject("OperandReadOnly", f => f.Inventory["tags"][1]["logicalAddress"] = "%I0.1"));
Test("InOut cannot write localized physical input address", () => Reject("OperandReadOnly", f => { f.Fb.Descendants().Single(e => e.Name.LocalName == "Section" && (string)e.Attribute("Name") == "Output").SetAttributeValue("Name", "InOut"); f.Payload["integration"]["mappings"][1]["direction"] = "InOut"; f.Inventory["tags"][1]["logicalAddress"] = "E0.1"; }));
Test("array parameter rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants().First(e => e.Name.LocalName == "Member").SetAttributeValue("Datatype", "Array[0..1] of Bool")));
Test("structured parameter rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants().First(e => e.Name.LocalName == "Member").Add(new XElement("Sections"))));
Test("FC roots reject", () => Reject("UnsupportedIntegrationType", f => f.Fb.Root.Element("SW.Blocks.FB").Name = "SW.Blocks.FC"));
Test("wrong root native name rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants("Name").First().Value = "Other"));
Test("safety FB rejects", () => Reject("UnsupportedIntegrationType", f => f.Fb.Descendants("ProgrammingLanguage").First().Value = "F_FBD"));
Test("missing caller exact path rejects", () => Reject("IntegrationCallerMismatch", f => f.Payload["integration"]["caller"]["blockPath"] = "/wrong"));
Test("protected caller rejects", () => Reject("UnsupportedIntegrationCaller", f => f.Inventory["objects"][0]["protected"] = true));
Test("namespace caller rejects", () => Reject("UnsupportedIntegrationCaller", f => f.Inventory["objects"][0]["namespace"] = "Motor"));
Test("caller source-language conversion rejects", () => Reject("UnsupportedIntegrationCaller", f => f.Inventory["objects"][0]["language"] = "SCL"));
Test("stale caller export rejects", () => Reject("IntegrationCallerMismatch", f => f.Caller.Descendants("Name").First().Value = "Other"));
Test("missing network ID rejects", () => Reject("NetworkNotFound", f => f.Payload["integration"]["caller"]["insertAfterNetworkId"] = "FF"));
Test("unknown recipe fields reject", () => Reject("InvalidIntegrationRecipe", f => f.Payload["overwrite"] = true));
Test("coerced bool rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["planOnly"] = "true"));
Test("coerced schema version rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["schemaVersion"] = "1"));
Test("implicit or enabled execution rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["planOnly"] = false));
Test("safety-unit scope rejects", () => Reject("UnsupportedIntegrationScope", f => f.Payload["target"]["scopePath"] = "/plc/safetyUnits/Unit1"));
Test("nested unit scope rejects", () => Reject("UnsupportedIntegrationScope", f => f.Payload["target"]["scopePath"] = "/plc/units/Unit1/blocks"));
Test("number override rejects", () => Reject("InvalidIntegrationRecipe", f => f.Payload["integration"]["instanceDb"]["number"] = 123));
Test("Date is unavailable in the typed call route", () => Reject("UnsupportedIntegrationType", f => f.Payload["integration"]["mappings"][0]["dataType"] = "Date"));
Test("Time_Of_Day is unavailable in the typed call route", () => Reject("UnsupportedIntegrationType", f => f.Payload["integration"]["mappings"][0]["dataType"] = "Time_Of_Day"));
Test("master copy cannot become a version pin", () => Reject("InvalidIntegrationRecipe", f => f.Payload["selection"][0] = new JObject { ["masterCopyPath"] = "/masterCopies/Motor" }));
Test("supported scalar set equals production compiler scalar set", () => {
    var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
    var planner = (HashSet<string>)typeof(PlcLibraryIntegrationPlanner).GetField("Scalars", flags).GetValue(null);
    var compiler = (HashSet<string>)typeof(TypedGraphical).GetField("ScalarTypes", flags).GetValue(null);
    Equal(true, planner.SetEquals(compiler));
});
foreach (string language in new[] { "LAD", "FBD" })
    Test(language + " emitted call passes actual production compiler and retained native profiles", () => {
        var f = new Fixture(); f.Caller.Descendants("ProgrammingLanguage").ToList().ForEach(e => e.Value = language); f.Inventory["objects"][0]["language"] = language;
        var plan = f.Build(); var payload = (JObject)plan["steps"][1]["prepareArguments"]["payload"];
        var model = payload["model"].ToObject<TypedGraphicalModel>();
        string repository = RepositoryFiles.Root();
        var profiles = JObject.Parse(File.ReadAllText(Path.Combine(repository, "fixtures/typed-graphical/profiles.v1.json")))["profiles"].ToObject<TypedGraphicalProfile[]>();
        var context = new TypedGraphicalContext { CpuFamily = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1", Profiles = profiles };
        context.Callees["/plc/blocks/Motor"] = new TypedGraphicalCallee { Path = "/plc/blocks/Motor", Name = "Motor", BlockType = "FB", InterfaceHash = "fixture", Parameters = [new() { Name = "Enable", Section = "Input", DataType = "Bool" }, new() { Name = "Active", Section = "Output", DataType = "Bool" }] };
        context.Instances["instance"] = new TypedGraphicalInstance { Identity = "/plc/blocks/Motor_1", Scope = "GlobalVariable", Components = ["Motor_1"], CalleePath = "/plc/blocks/Motor", DataType = "\"Motor\"", DeclarationHash = "fixture" };
        foreach (JObject operand in (JArray)payload["model"]["operands"])
            context.Operands[operand.Value<string>("key")] = new TypedGraphicalOperand { Key = operand.Value<string>("key"), Identity = operand.Value<string>("key"), Scope = operand.Value<string>("scope") == "constant" ? "LiteralConstant" : "GlobalVariable", DataType = "Bool", Components = operand["components"]?.ToObject<string[]>() ?? [], Value = operand.Value<string>("value"), Writable = operand.Value<string>("key") == "p1", DeclarationHash = "fixture" };
        var built = TypedGraphical.Build(f.Caller, model, context);
        Equal(1, built.Document.Descendants(TypedGraphical.Net + "Call").Count());
        Equal(true, built.Document.Descendants(TypedGraphical.Net + "NameCon").Any(e => (string)e.Attribute("Name") == "en"));
    });

// The native guard must remain before durable claims/checkpoints. This is a
// source-boundary check, not a substitute for the explicitly unrun native harness.
Test("native apply gate is before idempotency claims and checkpoints", () => {
    string path = Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.LibraryChanges.cs");
    string source = File.ReadAllText(path); int apply = source.IndexOf("private JObject LibraryChangeApply", StringComparison.Ordinal);
    // Flag off: the unchanged plan-only refusal (shared constant, not a duplicated literal). Flag on: the combined preconditions.
    int flag = source.IndexOf("if (component && !_libraryIntegrateApplyEnabled)", apply, StringComparison.Ordinal);
    int gate = source.IndexOf("PlcLibraryIntegrationPlanner.ApplyError, \"This PLC integration is a plan only.", apply, StringComparison.Ordinal);
    int refusal = source.IndexOf("PlcCombinedApplyRefusal(change) is JObject refusal", apply, StringComparison.Ordinal);
    int combined = source.IndexOf("PlcComponentApplyCoordinator(change)", apply, StringComparison.Ordinal);
    int claim = source.IndexOf("LibraryJournalStore.Claim", apply, StringComparison.Ordinal);
    if (apply < 0 || flag < apply || gate <= flag || refusal <= gate || combined <= refusal || claim <= combined) throw new Exception("Guards must precede durable claim.");
    // A same-key retry after a successful or uncertain combined apply must reach the idempotent replay or OutcomeUnknown,
    // not a fresh target refusal (the installed FB occupies its own number): the refusal runs only for an unclaimed change.
    const string unclaimed = "if (component && change.IdempotencyKey == null && ";
    if (source.IndexOf(unclaimed + "PlcCombinedApplyRefusal(change) is JObject refusal)", apply, StringComparison.Ordinal) != refusal - unclaimed.Length)
        throw new Exception("The combined-apply refusal must be limited to unclaimed changes.");
    int replay = source.IndexOf("if (change.State == \"applied\") return LibraryChangeView(change);", refusal, StringComparison.Ordinal);
    int unknown = source.IndexOf("throw new BridgeException(\"OutcomeUnknown\", \"This operation was already dispatched and cannot be replayed.\"", refusal, StringComparison.Ordinal);
    if (replay < 0 || unknown < replay || unknown > claim) throw new Exception("The applied replay and OutcomeUnknown retry paths must stay unchanged before the claim.");
    // The target checks (languages, instance profile) are part of that pre-claim refusal, so a refused plan imports and saves nothing.
    string integration = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.PlcLibraryIntegration.cs"));
    int shape = integration.IndexOf("private JObject PlcCombinedApplyRefusal(LibraryChange change)", StringComparison.Ordinal);
    int target = integration.IndexOf("PlcCombinedApplyTargetRefusal(change, dependents, directInstanceDbs);", shape, StringComparison.Ordinal);
    int end = integration.IndexOf("private JObject PlcCombinedApplyTargetRefusal(LibraryChange change, string[] dependents, string[] directInstanceDbs)", shape, StringComparison.Ordinal);
    if (shape < 0 || target < shape || end < target) throw new Exception("The pre-claim refusal must include the target checks.");
    // The block-number and archive-path checks are pre-claim target checks too, and apply throws each refusal's own code.
    if (source.IndexOf("throw new BridgeException(refusal.Value<string>(\"code\")", apply, StringComparison.Ordinal) < 0) throw new Exception("Apply must throw the refusal's own code.");
    foreach (string check in new[] { "CombinedApplyLanguageRefusal(", "CombinedApplyInstanceRefusal(", "CapturedStorageContext(change, true)", "CombinedApplyNumberRefusal(", "CombinedApplyPathRefusal(" })
        if (integration.IndexOf(check, end, StringComparison.Ordinal) < 0) throw new Exception("Target check missing: " + check);
});
// A refused combined apply (a multi-instance caller with dependents included) is refused while the worker resolves the
// shared-mutation scope (mutation_begin), so the Host writes no admission record; the dependents are the typed route's own closure.
Test("combined-apply refusals precede the admission record, a stale preview still gets PreviewStale, and multi-instance dependents are refused", () => {
    string admission = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.SharedMutationAdmission.cs")).Replace("\r\n", "\n");
    int resolve = admission.IndexOf("private JObject SharedMutationResolve()", StringComparison.Ordinal);
    int library = admission.IndexOf("if (!_libraryChanges.TryGetValue(id, out var change))", resolve, StringComparison.Ordinal);
    int end = admission.IndexOf("var change = GetEngineeringChange(args);", library, StringComparison.Ordinal);
    // The scope's own refusals keep their codes: an online project, a stale preview and a changed source are refused first.
    int offline = admission.IndexOf("EnsureProjectOffline(change.Project);", library, StringComparison.Ordinal);
    int stale = admission.IndexOf("throw new BridgeException(\"PreviewStale\", \"Library resources changed since preview.\");", library, StringComparison.Ordinal);
    int sources = admission.IndexOf("VerifyLibrarySources(change);", library, StringComparison.Ordinal);
    // Only at mutation_begin (no scope yet): dispatch re-resolves the scope, and change_apply rechecks under the lease.
    int refusal = admission.IndexOf("if (_sharedMutationScope == null && change.Kind == \"library_integrate\" && !IsHmiLibraryTarget(change.Payload) && _libraryIntegrateApplyEnabled\n                            && change.IdempotencyKey == null && PlcCombinedApplyRefusal(change) is JObject combinedRefusal)\n                            throw new BridgeException(combinedRefusal.Value<string>(\"code\")", library, StringComparison.Ordinal);
    if (resolve < 0 || library < resolve || offline < library || stale < offline || sources < stale || refusal < sources || end < refusal)
        throw new Exception("The combined-apply refusal must follow the scope's own offline, PreviewStale and source checks and run at mutation_begin only.");
    if (admission.Split(new[] { "PlcCombinedApplyRefusal(change)" }, StringSplitOptions.None).Length != 2) throw new Exception("The admission scope evaluates the combined-apply refusal once.");
    string integration = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.PlcLibraryIntegration.cs"));
    if (integration.IndexOf("dependents = TypedDependentPaths(plc, callerPath).ToArray();", StringComparison.Ordinal) < 0 ||
        integration.IndexOf("PlcComponentQualificationGate.CombinedApplyDependentsRefusal(plan, dependents, directInstanceDbs)", StringComparison.Ordinal) < 0)
        throw new Exception("The reused-FB multi-instance target check must refuse the caller's dependents other than its direct instance DBs.");
    string typed = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.TypedInstances.cs"));
    if (typed.IndexOf("foreach (string path in TypedDependentPaths(capture.Plc, capture.CallerPath))", StringComparison.Ordinal) < 0)
        throw new Exception("The typed step and the pre-claim check must share one dependent closure.");
});
// Candidate shapes stay out of production: only the exact shapes with passed native evidence are verified, and candidates
// are admitted only on a --component-qualification worker in its own single owned scratch. Change these pins only together with native evidence.
Test("candidate shapes run only on a qualification Host's owned scratch, and only natively passed shapes are verified", () => {
    Equal("dependentInstanceDb+earlierPlacement+fbCaller+ladCaller+multiInstance+reusedComponent,dependentInstanceDb+earlierPlacement+fbCaller+multiInstance+reusedComponent,dependentInstanceDb+fbCaller+ladCaller+multiInstance+reusedComponent,dependentInstanceDb+fbCaller+multiInstance+reusedComponent,earlierPlacement,earlierPlacement+fbCaller,earlierPlacement+fbCaller+ladCaller,earlierPlacement+fbCaller+ladCaller+multiInstance+reusedComponent,earlierPlacement+fbCaller+ladCaller+reusedComponent,earlierPlacement+fbCaller+multiInstance+reusedComponent,earlierPlacement+fbCaller+reusedComponent,earlierPlacement+ladCaller,earlierPlacement+ladCaller+reusedComponent,earlierPlacement+reusedComponent,fbCaller,fbCaller+ladCaller,fbCaller+ladCaller+multiInstance+reusedComponent,fbCaller+ladCaller+reusedComponent,fbCaller+multiInstance+reusedComponent,fbCaller+reusedComponent,ladCaller,ladCaller+reusedComponent,obCaller,reusedComponent", string.Join(",", PlcComponentQualificationGate.VerifiedShapes));
    string integration = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.PlcLibraryIntegration.cs"));
    foreach (string pinned in new[] {
        "CombinedApplyRefusal(change.Preview, export, PlcComponentCandidatesAdmitted(change), dependentInstanceDb)",
        "private bool PlcComponentCandidatesAdmitted(LibraryChange change) =>\n            _componentQualificationEnabled && _ownsPortal && _scratchProject != null && change.Project != null && change.Project.Equals(_scratchProject) && _portal.Projects.Count == 1;" })
        if (integration.Replace("\r\n", "\n").IndexOf(pinned, StringComparison.Ordinal) < 0) throw new Exception("Candidate admission changed: " + pinned);
    if (integration.Split(new[] { "PlcComponentCandidatesAdmitted(" }, StringSplitOptions.None).Length != 3) throw new Exception("Candidate admission must have exactly one use.");
    string gate = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/PlcComponentQualificationGate.cs"));
    if (gate.IndexOf("internal static readonly string[] VerifiedShapes = { " + string.Join(", ", PlcComponentQualificationGate.VerifiedShapes.Select(s => "\"" + s + "\"")) + " };", StringComparison.Ordinal) < 0 ||
        gate.IndexOf("local.library_integrate.candidate_shapes.rerun.20261031", StringComparison.Ordinal) < 0 ||
        gate.IndexOf("local.library_integrate.candidate_shapes.multi.20261101", StringComparison.Ordinal) < 0 ||
        gate.IndexOf("local.library_integrate.candidate_shapes.combinations.20261102", StringComparison.Ordinal) < 0 ||
        gate.IndexOf("local.library_integrate.candidate_shapes.dependent_db.20261102", StringComparison.Ordinal) < 0)
        throw new Exception("VerifiedShapes changes only together with its cited native evidence.");
    // A failed combined apply carries the coordinator's initial checkpoint, the typed one of a reused FB included, into the change journal.
    if (integration.IndexOf("if (record?[\"initialCheckpoint\"] is JObject checkpoint && ((JArray)change.Journal[\"checkpoints\"]).Count == 0) ((JArray)change.Journal[\"checkpoints\"]).Add(checkpoint.DeepClone());", StringComparison.Ordinal) < 0)
        throw new Exception("The failure path no longer records the initial checkpoint in the change journal.");
});
// The admitted step workspace of a combined apply needs its own reconcile rule, or a crash leaves the admission unreleasable.
Test("the combined apply's admitted step workspace has a reconcile rule", () => {
    string admission = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.SharedMutationAdmission.cs"));
    string reconcile = File.ReadAllText(Path.Combine(RepositoryFiles.Root(), "src/TiaMcp.Openness.V21/OpennessSession.AdmissionReconcileLibrary.cs"));
    if (!admission.Contains("PlcComponentQualificationGate.CombinedApplyWorkspace(change.Directory, change.Id), \"combinedApplyStepWorkspace\")") ||
        !reconcile.Contains("check.Rows(\"combinedApplyStepWorkspace\")") || !reconcile.Contains("PlcComponentCoordinator.ReconcileWorkspace(row.Value<string>(\"path\")"))
        throw new Exception("The admitted combinedApplyStepWorkspace row must be proven by PlcComponentCoordinator.ReconcileWorkspace.");
});
TextBoundaryTests.Run(Test);
CoordinatorTests.Run(Test);
StandaloneTests.Run(Test);
CallerInsertionTests.Run(Test);
QualificationTests.Run(Test);
ContentContractTests.Run(Test);
ContentArtifactTests.Run(Test);
PlanArtifactTests.Run(Test);
LibraryDependencyCaptureTests.Run(Test);
PlcHmiTests.Run(Test);
BroadenedPlanTests.Run(Test);
if (failed == 0 && args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--observation-fixtures") throw new ArgumentException("Expected --observation-fixtures <fresh-directory>.");
    ObservationFixtures.Write(args[1]);
}
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class Fixture
{
    internal const string Type = "aaaaaaaa-1111-1111-1111-111111111111", Version = "bbbbbbbb-2222-2222-2222-222222222222";
    internal JObject Payload = JObject.Parse("""
    {"schemaVersion":1,"action":"plc_component_plan","planOnly":true,"source":{"libraryId":"lib-source","revision":"revision"},"selection":[{"typeGuid":"aaaaaaaa-1111-1111-1111-111111111111","versionGuid":"bbbbbbbb-2222-2222-2222-222222222222"}],"target":{"projectName":"Fixture","plcPath":"/plc"},"integration":{"dependencyPins":[],"instanceDb":{"name":"Motor_1","numberPolicy":"firstAvailable"},"caller":{"blockPath":"/plc/blocks/Main","insertAfterNetworkId":"3"},"mappings":[{"parameter":"Enable","direction":"Input","dataType":"Bool","tagPath":"/plc/tags/IO/tags/EnableMotor"},{"parameter":"Active","direction":"Output","dataType":"Bool","tagPath":"/plc/tags/IO/tags/MotorRunning"}]}}
    """);
    internal JArray Versions = new(new JObject { ["typeGuid"] = Type, ["versionGuid"] = Version, ["typeName"] = "Motor", ["state"] = "Committed", ["doNotUse"] = false, ["contentSha256"] = new string('a', 64), ["requiredVersionGuids"] = new JArray() });
    internal XDocument Fb = XDocument.Parse("""
    <Document><SW.Blocks.FB ID="0"><AttributeList><Name>Motor</Name><Namespace/><ProgrammingLanguage>SCL</ProgrammingLanguage><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Input"><Member Name="Enable" Datatype="Bool"/></Section><Section Name="Output"><Member Name="Active" Datatype="Bool"/></Section><Section Name="InOut"/><Section Name="Static"/></Sections></Interface></AttributeList></SW.Blocks.FB></Document>
    """);
    internal XDocument Caller = XDocument.Parse("""
    <Document><SW.Blocks.FC ID="0"><AttributeList><Name>Main</Name><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="3"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>
    """);
    internal JObject Inventory = JObject.Parse("""
    {"complete":true,"objects":[{"name":"Main","path":"/plc/blocks/Main","kind":"FC","number":1,"language":"FBD","protected":false,"namespace":""},{"name":"ExistingDb","path":"/plc/blocks/ExistingDb","kind":"DB","number":1}],"tags":[{"name":"EnableMotor","path":"/plc/tags/IO/tags/EnableMotor","tablePath":"/plc/tags/IO","dataType":"Bool","logicalAddress":"%I0.0","isSafety":false},{"name":"MotorRunning","path":"/plc/tags/IO/tags/MotorRunning","tablePath":"/plc/tags/IO","dataType":"Bool","logicalAddress":"%Q0.0","isSafety":false}]}
    """);
    internal JObject Build() => PlcLibraryIntegrationPlanner.Build(Payload, Versions, Fb, Caller, Inventory);
    internal void Standalone(string language = "FBD") {
        Payload["integration"]["caller"] = new JObject { ["mode"] = "createStandaloneFc", ["name"] = "MotorCaller", ["language"] = language, ["numberPolicy"] = "firstAvailable" };
        Caller = null;
    }
    internal void AddDependency() {
        var pin = new JObject { ["typeGuid"] = "cccccccc-3333-3333-3333-333333333333", ["versionGuid"] = "dddddddd-4444-4444-4444-444444444444" };
        ((JArray)Payload["integration"]["dependencyPins"]).Add(pin.DeepClone());
        ((JArray)Versions[0]["requiredVersionGuids"]).Add(pin["versionGuid"].DeepClone());
        pin["typeName"] = "MotorState"; pin["state"] = "Committed"; pin["doNotUse"] = false; pin["contentSha256"] = new string('b', 64); pin["requiredVersionGuids"] = new JArray(); Versions.Add(pin);
    }
}

