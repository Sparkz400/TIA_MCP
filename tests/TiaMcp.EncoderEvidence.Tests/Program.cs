using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

string root = Path.GetFullPath(args.Length == 1 ? args[0] : Path.Combine(Path.GetTempPath(), "encoder-checkpoint-" + Guid.NewGuid().ToString("N")));
if (Directory.Exists(root)) throw new IOException("Choose a fresh checkpoint test directory.");
Directory.CreateDirectory(root);
int passed = 0;
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
JsonObject Artifact(string path) => new() { ["path"] = path, ["bytes"] = new FileInfo(path).Length, ["sha256"] = Hash(File.ReadAllBytes(path)) };
void Assert(bool value) { if (!value) throw new Exception("Checkpoint evidence assertion failed."); }
void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action) { try { action(); } catch (Exception error) when (error is IOException || error is InvalidOperationException) { return; } throw new Exception("Expected checkpoint refusal."); }
(string archive, string auth, string project) Inputs(string name)
{
    string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
    string auth = Path.Combine(folder, "connect.authorization.json"), archive = Path.Combine(folder, "checkpoint.zap21"), project = Path.Combine(folder, "TiaMcpScratch_Test.ap21");
    File.WriteAllText(auth, new JsonObject { ["processId"] = 123, ["projectPath"] = project }.ToJsonString(), new UTF8Encoding(false));
    File.WriteAllBytes(archive, Encoding.UTF8.GetBytes("original pre-change archive"));
    return (archive, auth, project);
}
try
{
    Check("pre-mutation receipt captures actual archive and authorization bytes", () => {
        var input = Inputs("baseline"); var descriptor = CheckpointEvidence.Capture(input.archive, input.auth, input.project, 123);
        var reference = JsonNode.Parse(descriptor.ToString()); string path = reference["path"].GetValue<string>();
        Assert(JsonNode.DeepEquals(reference, Artifact(path)));
        var receipt = JsonNode.Parse(File.ReadAllBytes(path));
        Assert(receipt["schemaVersion"].GetValue<int>() == 1 && receipt["phase"].GetValue<string>() == "beforeEncoderMutation");
        Assert(receipt["processId"].GetValue<int>() == 123 && receipt["projectPath"].GetValue<string>() == input.project);
        Assert(receipt["archive"]["sha256"].GetValue<string>() == "49b3248b8bb630dcbc1fae36aab3a481b0622a77ec7415d6ae53c7f17f26d503");
        Assert(JsonNode.DeepEquals(receipt["authorization"], Artifact(input.auth)));
        var fixture = new JsonObject { ["synthetic"] = true, ["nativeExecuted"] = false, ["runDirectory"] = Path.GetDirectoryName(input.auth),
            ["projectPath"] = input.project, ["sessions"] = new JsonArray(new JsonObject { ["processId"] = 123 }),
            ["seederConnect"] = new JsonObject { ["completed"] = false, ["checkpointArchive"] = input.archive, ["checkpointReceipt"] = reference.DeepClone() },
            ["seederLifetimes"] = new JsonArray(new JsonObject { ["mode"] = "connect", ["authorization"] = input.auth,
                ["authorizationSha256"] = Artifact(input.auth)["sha256"].GetValue<string>(), ["authorizationProcessId"] = 123,
                ["authorizationProjectPath"] = input.project, ["terminal"] = new JsonObject { ["completed"] = true, ["exitCode"] = 0 },
                ["unknown"] = new JsonArray(), ["outcomeCaptured"] = true }) };
        File.WriteAllText(Path.Combine(root, "fixture.json"), fixture.ToJsonString(), new UTF8Encoding(false));
    });
    Check("later archive replacement never changes the retained expected digest", () => {
        var input = Inputs("replacement"); var descriptor = CheckpointEvidence.Capture(input.archive, input.auth, input.project, 123);
        string path = descriptor.Value<string>("path"); byte[] original = File.ReadAllBytes(path);
        File.WriteAllText(input.archive, "replacement bytes");
        Assert(original.SequenceEqual(File.ReadAllBytes(path)));
        Assert(JsonNode.Parse(original)["archive"]["sha256"].GetValue<string>() != Hash(File.ReadAllBytes(input.archive)));
    });
    Check("an existing receipt cannot be overwritten by recapture", () => {
        var input = Inputs("immutable"); string path = input.auth + ".checkpoint.json"; File.WriteAllText(path, "original receipt");
        Reject(() => CheckpointEvidence.Capture(input.archive, input.auth, input.project, 123));
        Assert(File.ReadAllText(path) == "original receipt");
    });
    Check("missing or empty checkpoint fails before receipt publication", () => {
        foreach (bool missing in new[] { false, true }) {
            var input = Inputs("missing-" + missing);
            if (missing) File.Delete(input.archive); else File.WriteAllBytes(input.archive, Array.Empty<byte>());
            Reject(() => CheckpointEvidence.Capture(input.archive, input.auth, input.project, 123));
            Assert(!File.Exists(input.auth + ".checkpoint.json"));
        }
    });
    Check("wrong owner context and unrelated archive location refuse", () => {
        var input = Inputs("context"); var other = Inputs("other");
        Reject(() => CheckpointEvidence.Capture(input.archive, input.auth, input.project, 0));
        Reject(() => CheckpointEvidence.Capture(input.archive, input.auth, "relative.ap21", 123));
        Reject(() => CheckpointEvidence.Capture(other.archive, input.auth, input.project, 123));
        Assert(!File.Exists(input.auth + ".checkpoint.json"));
    });
    Check("missing authorization cannot leave an admissible receipt", () => {
        var input = Inputs("authorization"); File.Delete(input.auth);
        Reject(() => CheckpointEvidence.Capture(input.archive, input.auth, input.project, 123));
        Assert(!File.Exists(input.auth + ".checkpoint.json"));
    });
    Check("directory links cannot substitute another checkpoint location", () => {
        var input = Inputs("links"); string link = Path.Combine(root, "alias"), target = Path.GetDirectoryName(input.auth);
        if (OperatingSystem.IsWindows()) {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
            using var child = Process.Start(start); child.WaitForExit(); Assert(child.ExitCode == 0);
        } else Directory.CreateSymbolicLink(link, target);
        try {
            Reject(() => CheckpointEvidence.Capture(Path.Combine(link, Path.GetFileName(input.archive)), Path.Combine(link, Path.GetFileName(input.auth)), input.project, 123));
            Assert(!File.Exists(input.auth + ".checkpoint.json"));
        } finally { Directory.Delete(link); }
        Assert(File.Exists(input.archive));
    });
    Console.WriteLine($"Encoder checkpoint evidence: {passed} passed; actual file I/O, synthetic archives, no Siemens execution.");
}
finally
{
    if (args.Length == 0 && Path.GetDirectoryName(root) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(root).StartsWith("encoder-checkpoint-", StringComparison.Ordinal)) Directory.Delete(root, true);
}
