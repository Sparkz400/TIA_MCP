using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.VersionControl;

if (GitRunnerChecks.RunProcessFixture()) return 0;
if (args.Length == 3 && args[0] == "--git-selection-only")
{
    GitRepositorySelectionChecks.Run(args[1], args[2]).GetAwaiter().GetResult();
    return 0;
}
if (args.Length == 3 && args[0] == "--git-long-path-only")
{
    GitLongPathChecks.Run(args[1], args[2]).GetAwaiter().GetResult();
    return 0;
}
if (args.Length == 3 && args[0] == "--git-durable-reader")
{
    DurableGitChecks.ReadInFreshProcess(args[1], args[2]);
    return 0;
}
if (args.Length == 4 && args[0] == "--retained-native-evidence" && args[2] == "--review-output")
{
    PreservationChecks.ReviewRetained(args[1], args[3]);
    return 0;
}
var tests = new List<(string, Action)>();
void Test(string name, Action run) => tests.Add((name, run));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
void True(bool value) { if (!value) throw new Exception("Expected true."); }
void Reject(string code, Action run)
{
    try { run(); } catch (BridgeException e) { Equal(code, e.Code); return; }
    throw new Exception("Expected " + code);
}
string H(char c) => new(c, 64);
EngineeringSnapshotIdentity Snapshot(string id) => new(id, H('a'));
EngineeringObjectReference Obj(string id, string path = "/PLC/Blocks/Motor") => new(new("D:/Fixture/P.ap21", "/PLC", null, path, "block"), Snapshot(id));
VciProfileInput Profile() => new("authored-sd", "plcSimaticSd", "block", "ObservedFixtureFormat", H('b'), "authoredFixture",
    new[] { new VciExpectedFile("PLC/Motor.s7dcl", "source"), new VciExpectedFile("PLC/Motor.s7res", "resources") });
VciFileInput FileInput(string path, string value) => new(path, Encoding.UTF8.GetBytes(value));
VciManifest Manifest(string id, string? source, bool complete = true, string? status = null,
    string objectPath = "/PLC/Blocks/Motor", string? nativeIdentity = null)
{
    var packages = source is null ? Array.Empty<VciPackageInput>() : new[] { new VciPackageInput("motor", Obj(id, objectPath), Profile(), status, nativeIdentity) };
    var files = source is null ? Array.Empty<VciFileInput>() : new[] { FileInput("PLC/Motor.s7dcl", source), FileInput("PLC/Motor.s7res", "comment") };
    return VciManifest.Capture("workspace-fixture", Snapshot(id), packages, files, complete);
}
VciConflictReport Compare(VciManifest b, VciManifest p, VciManifest w) => VciComparison.Compare(b, p, w, new(b.Sha256, p.Sha256, w.Sha256));
var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/conflict-matrix.json")))!["cases"]!.AsArray();
foreach (var row in matrix)
{
    var r = row!;
    Test("three-way " + r["state"], () =>
    {
        var report = Compare(Manifest("b", (string?)r["baseline"]), Manifest("p", (string?)r["project"]), Manifest("w", (string?)r["workspace"]));
        Equal((string)r["state"]!, report.Rows.Single().State); Equal((bool)r["conflict"]!, report.Rows.Single().Conflict);
        Equal(false, (bool)report.ToJson()["canSynchronize"]!); Equal(false, (bool)report.ToJson()["nativeQualified"]!);
    });
}
Test("empty complete inventories contain no invented deletion", () => Equal(0, Compare(Manifest("b", null), Manifest("p", null), Manifest("w", null)).Rows.Count));
Test("native green is not content equality", () =>
{
    var report = Compare(Manifest("b", "A", status:"Equal"), Manifest("p", "B", status:"Equal"), Manifest("w", "C", status:"Equal"));
    Equal("divergentChange", report.Rows[0].State); Equal("Equal", report.Rows[0].ProjectNativeStatus);
});
Test("different native status does not change identical content", () => Equal("unchanged", Compare(Manifest("b", "A", status:"Equal"), Manifest("p", "A", status:"Unknown"), Manifest("w", "A", status:"Unequal")).Rows[0].State));
Test("expected manifest digest binds all inputs", () =>
{
    var b = Manifest("b", "A"); var p = Manifest("p", "B"); var w = Manifest("w", "A");
    var result = VciComparison.Compare(b, p, w, new(b.Sha256, H('c'), w.Sha256));
    Equal("stale", result.Rows[0].State); Equal("unknown", result.Rows[0].ProjectChange);
});
Test("incomplete inventory never implies deletion", () => Equal("unknown", Compare(Manifest("b", "A"), Manifest("p", null, false), Manifest("w", "A")).Rows[0].State));
Test("same package different exact target collision", () => Equal("identityCollision", Compare(Manifest("b", "A"), Manifest("p", "A", objectPath:"/OtherPLC/Blocks/Motor"), Manifest("w", "A")).Rows[0].State));
Test("same name different native identity collision", () => Equal("identityCollision", Compare(Manifest("b", "A", nativeIdentity:"guid-A"), Manifest("p", "A", nativeIdentity:"guid-B"), Manifest("w", "A", nativeIdentity:"guid-A")).Rows[0].State));
Test("missing native identity observation is unknown", () => Equal("unknown", Compare(Manifest("b", "A", nativeIdentity:"guid-A"), Manifest("p", "A"), Manifest("w", "A", nativeIdentity:"guid-A")).Rows[0].State));
Test("missing companion is incomplete not deletion", () =>
{
    var missing = VciManifest.Capture("workspace-fixture", Snapshot("w"), new[] { new VciPackageInput("motor", Obj("w"), Profile()) }, new[] { FileInput("PLC/Motor.s7dcl", "A") }, true);
    var r = Compare(Manifest("b", "A"), Manifest("p", "A"), missing).Rows[0];
    Equal("incompletePackage", r.State); True(r.Reasons.Contains("workspace:missingCompanion:PLC/Motor.s7res"));
});
Test("unmanaged file prevents adopting missing mapping", () =>
{
    var unmanaged = VciManifest.Capture("workspace-fixture", Snapshot("w"), [], new[] { FileInput("plc/motor.s7dcl", "unowned") }, true);
    var r = Compare(Manifest("b", "A"), Manifest("p", "A"), unmanaged).Rows[0];
    Equal("ownershipConflict", r.State); True(r.Reasons.Any(x => x.Contains("unmanagedFileCollision")));
});
Test("multiply mapped file cannot acquire two writers", () =>
{
    var m = VciManifest.Capture("workspace-fixture", Snapshot("p"), new[] {
        new VciPackageInput("motor", Obj("p"), Profile()), new VciPackageInput("other", Obj("p", "/Other/Motor"), Profile()) },
        new[] { FileInput("PLC/Motor.s7dcl", "A"), FileInput("PLC/Motor.s7res", "comment") }, true);
    True(m.Packages.All(p => p.Issues.Contains("mappingCollision"))); Equal("ownershipConflict", Compare(Manifest("b", "A"), m, Manifest("w", "A")).Rows[0].State);
});
Test("duplicate exact object even at different file paths is ambiguous", () =>
{
    var second = Profile() with { ExpectedFiles = new[] { new VciExpectedFile("other.s7dcl", "source"), new VciExpectedFile("other.s7res", "resources") } };
    var m = VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), Profile()), new VciPackageInput("b", Obj("p"), second) }, [], true);
    True(m.Packages.All(p => p.Issues.Contains("mappingCollision")));
});
Test("mixed capture snapshots refused", () => Reject("VciSnapshotMismatch", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("old"), Profile()) }, [], true)));
Test("profile cannot guess support from extension", () => Reject("VciInvalidProfile", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), Profile() with { Family = "guessed" }) }, [], true)));
Test("profile object family binding", () => Reject("VciInvalidProfile", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), Profile() with { ObjectKind = "hmi" }) }, [], true)));
Test("incomplete profile does not assert completeness", () =>
{
    var p = VciManifest.Capture("workspace-fixture", Snapshot("p"), new[] { new VciPackageInput("motor", Obj("p"), Profile() with { Complete = false }) }, new[] { FileInput("PLC/Motor.s7dcl", "A"), FileInput("PLC/Motor.s7res", "comment") }, true);
    Equal("unknown", Compare(Manifest("b", "A"), p, Manifest("w", "A")).Rows[0].State);
});
foreach (var path in new[] { "../a", "a/../b", "/root/a", "C:/a", "\\\\server\\share", "a\\b", "a//b", "a:stream", "NUL.txt", "x/COM1.log", "x/LPT².txt", "CON .txt", "a.", "a ", "a?b", "a/./b", "a\nb", "a\ud800" })
    Test("unsafe path " + path, () => Reject("VciUnsafePath", () => VciManifest.Capture("x", Snapshot("p"), [], new[] { FileInput(path, "data") }, true)));
Test("links refused", () => Reject("VciUnsafePath", () => VciManifest.Capture("x", Snapshot("p"), [], new[] { FileInput("a", "data") with { IsLink = true } }, true)));
foreach (var pair in new[] { new[] { "a", "A" }, new[] { "café", "cafe\u0301" }, new[] { "a", "a/b" } })
    Test("path collision " + pair[0] + " " + pair[1], () => Reject("VciPathCollision", () => VciManifest.Capture("x", Snapshot("p"), [], pair.Select(p => FileInput(p, "data")), true)));
Test("unknown auxiliary companion retained and hashed", () =>
{
    var profile = Profile() with { ExpectedFiles = Profile().ExpectedFiles.Concat(new[] { new VciExpectedFile("unknown.bin", "auxiliary") }).ToArray() };
    var m = VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), profile) }, new[] { FileInput("PLC/Motor.s7dcl", "A"), FileInput("PLC/Motor.s7res", "c"), FileInput("unknown.bin", "payload") }, true);
    Equal(3, m.Files.Count); Equal(0, m.Packages[0].Issues.Count);
});
Test("unified scripts require explicit paired roles", () =>
{
    var profile = Profile() with { Family = "unifiedScript", ExpectedFiles = new[] { new VciExpectedFile("script.js", "script") } };
    Reject("VciInvalidProfile", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), profile) }, [], true));
    profile = profile with { ExpectedFiles = new[] { new VciExpectedFile("script.js", "script"), new VciExpectedFile("script.yml", "metadata") } };
    var m = VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), profile) }, new[] { FileInput("script.js", "js"), FileInput("script.yml", "metadata") }, true);
    Equal(0, m.Packages[0].Issues.Count);
});
Test("library GUID and version companion required", () =>
{
    var profile = Profile() with { Family = "libraryDocument", ExpectedFiles = new[] { new VciExpectedFile("type.xml", "document"), new VciExpectedFile("version.xml", "libraryVersion") } };
    Reject("VciInvalidProfile", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), profile) }, [], true));
    profile = profile with { LibraryTypeGuid = "11111111-1111-1111-1111-111111111111", LibraryVersionGuid = "22222222-2222-2222-2222-222222222222" };
    var m = VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), profile) }, new[] { FileInput("type.xml", "data") }, true);
    True(m.Packages[0].Issues.Contains("missingCompanion:version.xml"));
});
Test("library SD does not drop resources", () =>
{
    var profile = Profile() with { Family = "libraryDocument", LibraryTypeGuid = "11111111-1111-1111-1111-111111111111", LibraryVersionGuid = "22222222-2222-2222-2222-222222222222", ExpectedFiles = new[] { new VciExpectedFile("type.s7dcl", "source"), new VciExpectedFile("version.xml", "libraryVersion") } };
    Reject("VciInvalidProfile", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), profile) }, [], true));
});
Test("deterministic order and cloned outputs", () =>
{
    var bytes = Encoding.UTF8.GetBytes("A"); var expected = Profile().ExpectedFiles.ToList();
    var package = new VciPackageInput("motor", Obj("p"), Profile() with { ExpectedFiles = expected });
    var a = VciManifest.Capture("x", Snapshot("p"), new[] { package }, new[] { new VciFileInput("PLC/Motor.s7dcl", bytes), FileInput("PLC/Motor.s7res", "comment") }, true);
    var b = VciManifest.Capture("x", Snapshot("p"), new[] { package }, new[] { FileInput("PLC/Motor.s7res", "comment"), FileInput("PLC/Motor.s7dcl", "A") }, true);
    Equal(a.Sha256, b.Sha256); bytes[0] = 0; expected.Clear(); a.ToJson()["workspaceId"] = "tampered";
    Equal(a.Sha256, CanonicalJson.Sha256(a.ToJson())); Equal(2, a.Packages[0].ExpectedFiles.Count);
});
Test("byte file package and path budgets", () =>
{
    Reject("VciLimitExceeded", () => VciManifest.Capture("x", Snapshot("p"), [], new[] { FileInput("a", "12") }, true, new(MaxTotalBytes:1)));
    Reject("VciLimitExceeded", () => VciManifest.Capture("x", Snapshot("p"), [], new[] { FileInput("a", "1"), FileInput("b", "2") }, true, new(MaxFiles:1)));
    Reject("VciUnsafePath", () => VciManifest.Capture("x", Snapshot("p"), [], new[] { FileInput(new string('a', 256), "1") }, true));
    Reject("VciInvalidLimits", () => VciManifest.Capture("x", Snapshot("p"), [], [], true, new(MaxPackages:0)));
});
Test("read-only result snapshots", () =>
{
    var r = Compare(Manifest("b", "A"), Manifest("p", "B"), Manifest("w", "C"));
    r.ToJson()["rows"]!.AsArray().Clear(); Equal(1, r.Rows.Count); Equal(r.Sha256, CanonicalJson.Sha256(r.ToJson()));
});
Test("project ownership cannot be mixed", () =>
{
    var other = Obj("p") with { Object = Obj("p").Object with { ProjectPath = "D:/Other/P.ap21" } };
    Reject("VciProjectMismatch", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), Profile()), new VciPackageInput("b", other, Profile()) }, [], true));
});
Test("object content observation cannot silently disappear", () =>
{
    var m = VciManifest.Capture("workspace-fixture", Snapshot("b"), new[] { new VciPackageInput("motor", Obj("b") with { ContentSha256 = H('d') }, Profile()) }, new[] { FileInput("PLC/Motor.s7dcl", "A"), FileInput("PLC/Motor.s7res", "comment") }, true);
    Equal("unknown", Compare(m, Manifest("p", "A"), Manifest("w", "A")).Rows[0].State);
});
Test("1000 object deterministic bounded comparison", () =>
{
    VciManifest Large(string id) => VciManifest.Capture("scale", Snapshot(id), Enumerable.Range(0, 1000).Select(i =>
        new VciPackageInput("p" + i, Obj(id, "/PLC/B" + i), Profile() with { Family = "nativeDocument", ExpectedFiles = new[] { new VciExpectedFile("b" + i + ".xml", "document") } })),
        Enumerable.Range(0, 1000).Select(i => FileInput("b" + i + ".xml", "fixed")), true);
    var r = Compare(Large("b"), Large("p"), Large("w")); Equal(1000, r.Rows.Count); True(r.Rows.All(row => row.State == "unchanged"));
});
Test("opaque native status must be bounded observable text", () => Reject("VciInvalidManifest", () => VciManifest.Capture("x", Snapshot("p"), new[] { new VciPackageInput("a", Obj("p"), Profile(), NativeStatus: "") }, [], true)));
PreservationChecks.Register(tests);
var pureCount = tests.Count;
if (args.Length == 4 && args[0] is "--git-executable" or "--durable-git-executable" && args[2] == "--git-fixtures")
{
    if (args[0] == "--git-executable") GitRunnerChecks.Register(tests, args[1], args[3]);
    DurableGitChecks.Register(tests, args[1], args[3]);
}
else if (args.Length != 0) throw new ArgumentException("Use --git-executable <absolute Git path> --git-fixtures <new isolated directory>.");
int failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"VCI checks: {tests.Count - failed}/{tests.Count}; {pureCount} pure checks, {tests.Count - pureCount} explicitly selected isolated Git/process checks; no native operations.");
if (failed == 0)
{
    var baseline = Manifest("b", "A");
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "vci-manifest.sample.json"), CanonicalJson.Serialize(baseline.ToJson()));
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "vci-conflict-report.sample.json"), CanonicalJson.Serialize(Compare(baseline, Manifest("p", "B"), Manifest("w", "C")).ToJson()));
}
return failed == 0 ? 0 : 1;
