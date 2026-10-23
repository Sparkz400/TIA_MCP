using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

internal static class ConfiguredVerifyChecks
{
    private static JObject Ref(string path) => new JObject { ["path"] = path, ["sha256"] = Program.Hash(path) };
    private static JObject Write(string path, JToken value) { File.WriteAllText(path, value.ToString()); return Ref(path); }
    private static void Rewrite(JObject admission, string field, Action<JObject> action)
    {
        string path = admission[field].Value<string>("path"); var data = JObject.Parse(File.ReadAllText(path)); action(data);
        admission[field] = Write(path, data);
    }
    private static JObject Terminal(string root, out JObject proof)
    {
        var request = Write(Path.Combine(root, "admission.json"), new JObject { ["testOnly"] = true });
        proof = Write(Path.Combine(root, "job.json"), new JObject { ["schemaVersion"] = "owned-job-terminal.v1", ["jobName"] = "Local\\TiaMcp-Owned-test",
            ["createdAtomicallyInJob"] = true, ["helperHandleSignaled"] = true, ["activeProcesses"] = 0, ["totalProcesses"] = 2,
            ["limitTerminatedProcesses"] = 0, ["limitFlags"] = 0, ["automaticTermination"] = false });
        var intent = Write(Path.Combine(root, "intent.json"), new JObject { ["schemaVersion"] = "native-dispatch-intent.v1", ["jobName"] = "Local\\TiaMcp-Owned-test",
            ["containmentRequiredFromCreation"] = true, ["source"] = request });
        return Write(Path.Combine(root, "terminal.json"), new JObject { ["schemaVersion"] = "native-dispatch-terminal.v1", ["intent"] = intent,
            ["jobEvidence"] = proof, ["originalAttemptPromoted"] = false });
    }
    private static JObject Fixture(string root, string helper, bool second)
    {
        Directory.CreateDirectory(root); string sourceRoot = Path.Combine(root, "configured"), nativeRoot = Path.Combine(sourceRoot, "native");
        Directory.CreateDirectory(nativeRoot);
        string project = Path.Combine(nativeRoot, "TiaMcpHmi_test.ap21"), archive = Path.Combine(nativeRoot, "configured.zap21");
        File.WriteAllText(project, "synthetic project"); File.WriteAllText(archive, "synthetic configured archive");
        var native = new JObject { ["completed"] = true, ["savedConfiguredArchiveCaptured"] = true, ["explicitOwnedSdkDispose"] = true,
            ["retainedHandleExitVerified"] = true, ["sourceArchiveUnchanged"] = true, ["projectPath"] = project, ["archive"] = Ref(archive),
            ["after"] = new JArray(new JObject { ["name"] = "OwnerA" }, new JObject { ["name"] = "OwnerB" }) };
        Write(Path.Combine(nativeRoot, "result.json"), native);
        var terminal = Terminal(sourceRoot, out var proof);
        var source = Write(Path.Combine(sourceRoot, "result.json"), new JObject { ["completed"] = true, ["helperExitCode"] = 0,
            ["processProof"] = new JObject { ["completed"] = true, ["jobProof"] = proof }, ["nativeResult"] = native });
        string outputRoot = Path.Combine(root, "new"); Directory.CreateDirectory(outputRoot);
        string output = Path.Combine(outputRoot, "native");
        var admission = new JObject { ["ownedFixture"] = true, ["sequentialNativeAdmission"] = true, ["automaticReplay"] = false,
            ["runtimeAllowed"] = false, ["helperSha256"] = Program.Hash(helper), ["configuredResult"] = source, ["archive"] = Ref(archive),
            ["configuredTerminal"] = terminal,
            ["phase"] = second ? "recovered-reopen" : "reopen-retrieve", ["outputDirectory"] = output, ["artifactRoot"] = outputRoot };
        if (second)
        {
            string priorRoot = Path.Combine(root, "prior"), priorNative = Path.Combine(priorRoot, "native");
            Directory.CreateDirectory(Path.Combine(priorNative, "recovered"));
            project = Path.Combine(priorNative, "recovered", "TiaMcpHmi_recovered.ap21"); File.WriteAllText(project, "synthetic recovered project");
            var recovered = new JObject { ["completed"] = true, ["phase"] = "reopen-retrieve", ["configuredResult"] = source,
                ["recoveredProjectPath"] = project };
            Write(Path.Combine(priorNative, "result.json"), recovered);
            admission["priorTerminal"] = Terminal(priorRoot, out var priorProof);
            admission["priorResult"] = Write(Path.Combine(priorRoot, "result.json"), new JObject { ["completed"] = true,
                ["helperExitCode"] = 0, ["processProof"] = new JObject { ["completed"] = true, ["jobProof"] = priorProof }, ["nativeResult"] = recovered });
        }
        admission["projectPath"] = project;
        admission["storeWindowIntent"] = Write(Path.Combine(root, "window.json"), new JObject {
            ["kind"] = "held-coordinator-store-window", ["mode"] = "isolatedQualificationMutation", ["readOnly"] = false,
            ["sameHandlesHeldThroughDispatch"] = true, ["automaticReplay"] = false,
            ["resources"] = new JArray(new JObject { ["kind"] = "project", ["path"] = project }, new JObject { ["kind"] = "workspace", ["path"] = output }) });
        return admission;
    }
    internal static List<object> Run(string root, string helper)
    {
        var results = new List<object>();
        void Check(string label, Action<JObject> change, bool second = false, bool reject = true)
        {
            var admission = Fixture(Path.Combine(root, "verify-" + label), helper, second); change(admission);
            bool refused = false;
            try { Program.ValidateConfiguredVerifyAdmission(admission, helper); }
            catch (Exception) { refused = true; }
            if (refused != reject) throw new Exception("Unexpected configured verification admission: " + label);
            results.Add(new { name = "configured-" + label, passed = true, rejected = refused, nativeInvoked = false });
        }
        Check("first-valid", _ => { }, reject: false);
        Check("second-valid", _ => { }, second: true, reject: false);
        Check("bad-helper", a => a["helperSha256"] = new string('0', 64));
        Check("runtime", a => a["runtimeAllowed"] = true);
        Check("replay", a => a["automaticReplay"] = true);
        Check("wrong-phase", a => a["phase"] = "open-anything");
        Check("failed-source", a => Rewrite(a, "configuredResult", v => v["completed"] = false));
        Check("failed-cleanup", a => Rewrite(a, "configuredResult", v => v["processProof"]["completed"] = false));
        Check("missing-terminal", a => a.Remove("configuredTerminal"));
        Check("unbound-job", a => Rewrite(a, "configuredTerminal", t => t["jobEvidence"]["sha256"] = new string('2',64)));
        Check("promoted-terminal", a => Rewrite(a, "configuredTerminal", t => t["originalAttemptPromoted"] = true));
        Check("raw-source-mismatch", a => Rewrite(a, "configuredResult", v => v["nativeResult"]["extra"] = true));
        Check("archive-changed", a => File.AppendAllText(a["archive"].Value<string>("path"), "different"));
        Check("wrong-project", a => a["projectPath"] = a["archive"].Value<string>("path"));
        Check("existing-output", a => Directory.CreateDirectory(a.Value<string>("outputDirectory")));
        Check("escaped-output", a => a["outputDirectory"] = Path.Combine(root, "outside"));
        Check("unheld-window", a => Rewrite(a, "storeWindowIntent", w => w["sameHandlesHeldThroughDispatch"] = false));
        Check("missing-project-resource", a => Rewrite(a, "storeWindowIntent", w => ((JArray)w["resources"]).RemoveAt(0)));
        Check("missing-output-resource", a => Rewrite(a, "storeWindowIntent", w => ((JArray)w["resources"]).RemoveAt(1)));
        Check("contradictory-read-only", a => Rewrite(a, "storeWindowIntent", w => w["readOnly"] = true));
        Check("failed-prior", a => Rewrite(a, "priorResult", p => p["completed"] = false), second: true);
        Check("failed-prior-native", a => Rewrite(a, "priorResult", p => p["nativeResult"]["completed"] = false), second: true);
        Check("failed-prior-cleanup", a => Rewrite(a, "priorResult", p => p["processProof"]["completed"] = false), second: true);
        Check("wrong-prior-configured", a => Rewrite(a, "priorResult", p => p["nativeResult"]["configuredResult"]["sha256"] = new string('1',64)), second: true);
        Check("raw-prior-mismatch", a => Rewrite(a, "priorResult", p => p["nativeResult"]["extra"] = true), second: true);
        return results;
    }
}
