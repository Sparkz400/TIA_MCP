using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

internal static class AdmissionChecks
{
    private static string Root, Helper;
    private static readonly List<object> Results = new List<object>();
    private static JObject Reference(string path) => new JObject { ["path"] = path, ["sha256"] = Program.Hash(path) };
    private static JObject Write(string path, JToken value) { File.WriteAllText(path, value.ToString()); return Reference(path); }
    private static void ChangeReference(JObject admission, string key, Action<JObject> change)
    {
        string path = admission[key].Value<string>("path");
        var value = JObject.Parse(File.ReadAllText(path)); change(value);
        admission[key] = Write(path, value);
    }
    private static JObject Fixture(string label)
    {
        string folder = Path.Combine(Root, label); Directory.CreateDirectory(folder);
        string archive = Path.Combine(folder, "baseline.zap21"); File.WriteAllText(archive, "pure test archive bytes");
        string output = Path.Combine(folder, "new-output");
        var source = new JObject { ["completed"] = true, ["profiles"] = new JArray(new JObject {
            ["profile"] = "unified-comfort", ["fixtureMode"] = "positive", ["deliberateInvalidReferences"] = false, ["archiveRetrievalVerified"] = true,
            ["projectPath"] = Path.Combine(folder, "TiaMcpHmi_test.ap21"), ["archive"] = Reference(archive),
            ["nativeBefore"] = new JArray(new JObject { ["name"] = "A" }, new JObject { ["name"] = "B" }) }) };
        var review = new JObject { ["approved"] = true, ["sourceArchiveSha256"] = Program.Hash(archive), ["owners"] = new JArray("A", "B"),
            ["allowedEdit"] = "configure-encrypted-transfer-password-and-confirmation", ["disableSecurityAllowed"] = false, ["executeScriptsAllowed"] = false };
        var window = new JObject { ["kind"] = "held-coordinator-store-window", ["mode"] = "isolatedQualificationMutation",
            ["readOnly"] = false, ["sameHandlesHeldThroughDispatch"] = true, ["automaticReplay"] = false,
            ["resources"] = new JArray(new JObject { ["kind"] = "workspace", ["path"] = output }) };
        return new JObject { ["ownedFixture"] = true, ["sequentialNativeAdmission"] = true, ["checkpointRetrievalGatePassed"] = true,
            ["automaticReplay"] = false, ["runtimeAllowed"] = false, ["helperSha256"] = Program.Hash(Helper),
            ["archive"] = Reference(archive), ["sourceReport"] = Write(Path.Combine(folder, "source.json"), source),
            ["review"] = Write(Path.Combine(folder, "review.json"), review), ["storeWindowIntent"] = Write(Path.Combine(folder, "window.json"), window),
            ["recoveryGate"] = Write(Path.Combine(folder, "gate.json"), new JObject { ["pureTestOnly"] = true, ["completed"] = true,
                ["preservation"] = new JObject { ["checkpointRecovery"] = true, ["recoveredFreshReopen"] = true },
                ["sessions"] = new JArray(new JObject { ["cleanup"] = new JObject { ["completed"] = true } }) }),
            ["outputDirectory"] = output, ["artifactRoot"] = folder };
    }
    private static void Check(string name, Action<JObject> mutate, bool reject = true)
    {
        var input = Fixture(name); mutate(input);
        bool rejected = false; string reason = null;
        try { Program.ValidateRepairUiAdmission(input, Helper); }
        catch (Exception error) { rejected = true; reason = error.Message; }
        if (rejected != reject) throw new Exception("Unexpected admission: " + name + ": " + reason);
        Results.Add(new { name, passed = true, rejected, reason, nativeInvoked = false });
    }
    private static int Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("New test output directory required.");
        Root = Path.GetFullPath(args[0]); Directory.CreateDirectory(Root);
        Helper = Path.Combine(Root, "helper.bin"); File.WriteAllText(Helper, "pure helper pin");
        Check("valid-exact-reviewed-owners", _ => { }, false);
        Check("valid-classic-start-screen", a => {
            ChangeReference(a, "sourceReport", s => s["profiles"][0]["profile"] = "classic-comfort");
            ChangeReference(a, "review", r => r["allowedEdit"] = "configure-existing-start-screen");
        }, false);
        Check("missing-sequential", a => a.Remove("sequentialNativeAdmission"));
        Check("runtime-denied", a => a["runtimeAllowed"] = true);
        Check("replay-denied", a => a["automaticReplay"] = true);
        Check("helper-pin", a => a["helperSha256"] = new string('0', 64));
        Check("archive-pin", a => File.AppendAllText(a["archive"].Value<string>("path"), "changed"));
        Check("source-incomplete", a => ChangeReference(a, "sourceReport", s => s["completed"] = false));
        Check("unknown-profile", a => ChangeReference(a, "sourceReport", s => s["profiles"][0]["profile"] = "unknown-classic"));
        Check("archive-retrieval-unverified", a => ChangeReference(a, "sourceReport", s => s["profiles"][0]["archiveRetrievalVerified"] = false));
        Check("recovery-incomplete", a => ChangeReference(a, "recoveryGate", r => r["completed"] = false));
        Check("recovery-checkpoint-failed", a => ChangeReference(a, "recoveryGate", r => r["preservation"]["checkpointRecovery"] = false));
        Check("recovery-reopen-failed", a => ChangeReference(a, "recoveryGate", r => r["preservation"]["recoveredFreshReopen"] = false));
        Check("recovery-cleanup-failed", a => ChangeReference(a, "recoveryGate", r => r["sessions"][0]["cleanup"]["completed"] = false));
        Check("recovery-cleanup-empty", a => ChangeReference(a, "recoveryGate", r => r["sessions"] = new JArray()));
        Check("negative-fixture", a => ChangeReference(a, "sourceReport", s => s["profiles"][0]["deliberateInvalidReferences"] = true));
        Check("unreviewed-owner", a => ChangeReference(a, "review", r => r["owners"] = new JArray("A", "C")));
        Check("security-disable-denied", a => ChangeReference(a, "review", r => r["disableSecurityAllowed"] = true));
        Check("script-execution-denied", a => ChangeReference(a, "review", r => r["executeScriptsAllowed"] = true));
        Check("wrong-prerequisite", a => ChangeReference(a, "review", r => r["allowedEdit"] = "configure-existing-start-screen"));
        Check("missing-output-resource", a => ChangeReference(a, "storeWindowIntent", w => w["resources"] = new JArray()));
        Check("window-read-only", a => ChangeReference(a, "storeWindowIntent", w => w["mode"] = "readOnlyProbe"));
        Check("window-legacy-unheld", a => ChangeReference(a, "storeWindowIntent", w => w["mode"] = "fixtureMutation"));
        Check("window-unheld", a => ChangeReference(a, "storeWindowIntent", w => w["sameHandlesHeldThroughDispatch"] = false));
        Check("window-contradictory-read-only", a => ChangeReference(a, "storeWindowIntent", w => w["readOnly"] = true));
        Check("window-wrong-kind", a => ChangeReference(a, "storeWindowIntent", w => w["kind"] = "unheld-review"));
        Check("window-replay", a => ChangeReference(a, "storeWindowIntent", w => w["automaticReplay"] = true));
        Check("output-escape", a => a["outputDirectory"] = Path.Combine(Root, "escape"));
        Check("output-reuse", a => Directory.CreateDirectory(a.Value<string>("outputDirectory")));
        Check("unbounded-wait", a => a["uiTimeoutMinutes"] = 61);
        Check("duplicate-owners", a => {
            ChangeReference(a, "sourceReport", s => s["profiles"][0]["nativeBefore"] = new JArray(new JObject { ["name"] = "A" }, new JObject { ["name"] = "A" }));
            ChangeReference(a, "review", r => r["owners"] = new JArray("A", "A"));
        });
        foreach (string action in new[] { "capture-saved-close", "abort-close", "unknown" })
        {
            var command = new JObject { ["command"] = action, ["reviewSha256"] = new string('a', 64),
                ["processId"] = 42, ["projectPath"] = "owned.ap21" };
            bool refused = false, capture = false;
            try { capture = Program.RepairUiCaptureRequested(command, new string('a', 64), 42, "owned.ap21"); }
            catch (InvalidOperationException) { refused = true; }
            if (refused != (action == "unknown") || capture != (action == "capture-saved-close"))
                throw new Exception("Command dispatch mismatch: " + action);
            Results.Add(new { name = "command-" + action, passed = true, rejected = refused, nativeInvoked = false });
        }
        foreach (string field in new[] { "reviewSha256", "processId", "projectPath" })
        {
            var command = new JObject { ["command"] = "abort-close", ["reviewSha256"] = new string('a', 64),
                ["processId"] = 42, ["projectPath"] = "owned.ap21" };
            command.Remove(field);
            bool refused = false;
            try { Program.RepairUiCaptureRequested(command, new string('a', 64), 42, "owned.ap21"); }
            catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new Exception("Unbound abort accepted: " + field);
            Results.Add(new { name = "abort-missing-" + field, passed = true, rejected = true, nativeInvoked = false });
        }
        Results.AddRange(ConfiguredVerifyChecks.Run(Root, Helper));
        File.WriteAllText(Path.Combine(Root, "result.json"), JsonConvert.SerializeObject(new { completed = true, nativeInvoked = false, count = Results.Count, checks = Results }, Formatting.Indented));
        Console.WriteLine(Results.Count + " file-only HMI repair admission checks passed; nativeInvoked=false");
        return 0;
    }
}
