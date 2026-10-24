using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

if (args.Length > 0 && args[0] == "--multiuser-only") { MultiuserReviewChecks.Run(); return; }
if (args.Length > 0 && args[0] == "--teamcenter-only") { TeamcenterReviewChecks.Run(); return; }
if (args.Length == 1 && args[0] == "--watch-authoring-only") { await WatchAuthoringChecks.Run(); return; }
if (args.Length > 0 && args[0] == "--operation-text-only") { OperationTextChecks.Run(args.Length > 1 ? args[1] : null); return; }
if (args.Length > 0 && args[0] == "--resource-path-only") { EngineeringOperationChecks.LongPaths(); return; }

if (args.Length > 0 && args[0] == "--admission-probe")
{
    MutationAdmissionChecks.Probe(args); return;
}

if (args.Length > 0 && args[0] == "--watch-matrix-child")
{
    WatchInterruptionMatrixChecks.Child(args); return;
}

if (args.Length > 0 && args[0] == "--operation-probe")
{
    EngineeringOperationChecks.Probe(args); return;
}

if (args.Length > 0 && args[0] == "--external-process") { ExternalResourceChecks.Process(args); return; }
if (args.Length > 0 && args[0] == "--external-only") { ExternalResourceChecks.Run(args.Length > 1 ? args[1] : null); return; }

int passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; }
void Reject(Action action, string code)
{
    try { action(); } catch (BridgeException e) when (e.Code == code) { passed++; return; }
    throw new Exception("Expected refusal: " + code);
}
var baseline = new EngineeringObjectReference(new("P/Ω", "PLC/1", "Root", "DB/MotorA", "block"), new("capture-1", new('a', 64)), new('b', 64));
EngineeringIdentityGuard.Validate(baseline); passed++;
EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { Object = baseline.Object with { } }); passed++;
Reject(() => EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { Object = baseline.Object with { ObjectPath = "DB/MotorB" } }), "IdentityMismatch");
Reject(() => EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { Object = baseline.Object with { ProjectPath = "P/Other" } }), "IdentityMismatch");
Reject(() => EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { Object = baseline.Object with { ScopePath = "Unit" } }), "IdentityMismatch");
Reject(() => EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { Snapshot = baseline.Snapshot with { ObservationId = "capture-2" } }), "StaleObservation");
Reject(() => EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { Snapshot = baseline.Snapshot with { SourceSha256 = new('c', 64) } }), "StaleObservation");
Reject(() => EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { Snapshot = baseline.Snapshot with { TiaVersion = 20 } }), "StaleObservation");
Reject(() => EngineeringIdentityGuard.EnsureFresh(baseline, baseline with { ContentSha256 = null }), "StaleContent");
Reject(() => EngineeringIdentityGuard.EnsureSameObject(baseline, baseline with { Object = baseline.Object with { ObjectPath = "db/MotorA" } }), "IdentityMismatch");
EngineeringIdentityGuard.EnsureSameSnapshot(baseline, baseline with { Object = baseline.Object with { ObjectPath = "DB/MotorB" } }); passed++;
foreach (var text in new[] { "", " ", "a\nb", new string('a', 8193), "bad\ud800" })
    Reject(() => EngineeringIdentityGuard.Validate(baseline with { Object = baseline.Object with { ProjectPath = text } }), "InvalidIdentity");
foreach (var digest in new[] { "bad", new string('A', 64), new string('a', 63), new string('a', 64) + "\n" })
    Reject(() => EngineeringIdentityGuard.Validate(baseline with { ContentSha256 = digest }), "InvalidIdentity");
Check(CanonicalJson.Serialize(JsonNode.Parse("{\"z\":1,\"a\":{\"y\":2,\"b\":null}}")!) == "{\"a\":{\"b\":null,\"y\":2},\"z\":1}", "Independent sorted JSON oracle");
Check(CanonicalJson.Sha256(JsonNode.Parse("{\"z\":1,\"a\":2}")!) == CanonicalJson.Sha256(JsonNode.Parse("{\"a\":2,\"z\":1}")!), "Property order does not change identity");
Check(CanonicalJson.Sha256(JsonNode.Parse("[1,2]")!) != CanonicalJson.Sha256(JsonNode.Parse("[2,1]")!), "Row order changes identity");
Check(CanonicalJson.Sha256(JsonNode.Parse("{\"a\":null}")!) != CanonicalJson.Sha256(JsonNode.Parse("{}")!), "Unknown versus absent stays distinct");
Check(CanonicalJson.Sha256(JsonNode.Parse("[1]")!) != CanonicalJson.Sha256(JsonNode.Parse("[1.0]")!), "Numeric source representation preserved");
Console.WriteLine($"PASS Engineering identity and canonical capture contracts: {passed} checks; no native execution.");
EngineeringOperationChecks.Run();
MultiuserReviewChecks.Run();
TeamcenterReviewChecks.Run();
OperationTextChecks.Run();
ExternalResourceChecks.Run(args.Length == 2 && args[0] == "--external-output" ? args[1] : null);
ProductionRecoveryChecks.Run();
await WatchInterruptionMatrixChecks.Run();
await WatchExpandedEditChecks.Run();
await MutationAdmissionChecks.Run();
await ProvisionReconciliationChecks.Run();
await ProductionAdapterChecks.Run();
await WatchAuthoringChecks.Run();
