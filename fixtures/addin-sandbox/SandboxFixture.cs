using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using TiaMcp.AddIn;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: SecurityRules(SecurityRuleSet.Level1)]

// Runs the companion's JSON and pipe code in a partial-trust AppDomain granted only what AddIn.Publisher.xml declares, as TIA does.
public sealed class SandboxProbe : MarshalByRefObject
{
    public string Run(string pipeName, uint processId, string startedAtUtc, string reviewDirectory)
    {
        try
        {
            // The sandbox must be real: TIA refuses the Framework pipe client in it.
            try { new NamedPipeClientStream(".", pipeName, PipeDirection.InOut).Dispose(); return "FAIL the AppDomain is not partially trusted"; }
            catch (SecurityException) { }
            const string sample = "{\"a\":[1,-2,3.5,true,false,null,\"x\\\"\\\\\\u0001\\u00e9\\ud83d\\ude00\"],\"b\":{},\"c\":[],\"d\":9007199254740993}";
            var parsed = (Dictionary<string, object>)HandoffJson.Parse(sample);
            string text = (string)((List<object>)parsed["a"])[6];
            Require(text == "x\"\\\u0001é😀" && (long)parsed["d"] == 9007199254740993 && (double)((List<object>)parsed["a"])[2] == 3.5, "values");
            Require(HandoffJson.Write(HandoffJson.Parse(HandoffJson.Write(parsed))) == HandoffJson.Write(parsed), "round trip");
            foreach (string bad in new[] { "{\"a\":1,\"a\":2}", "[1] x", "\"a\u0001\"", "[01x]", "{\"a\" 1}", "tru", new string('[', 200) + new string(']', 200), "" })
                Require(Refused(() => HandoffJson.Parse(bad)), "refused " + bad.Substring(0, Math.Min(bad.Length, 12)));
            string reply = HostPipe.Exchange(pipeName, processId, startedAtUtc, HandoffJson.Write(new Dictionary<string, object> { ["action"] = "begin", ["protocolVersion"] = 1 }), 2 << 20);
            var answer = (Dictionary<string, object>)HandoffJson.Parse(reply);
            Require(answer["success"] is true && (string)((Dictionary<string, object>)answer["result"])["echo"] == "begin", "exchange");
            Require(Refused(() => HostPipe.Exchange(pipeName, processId + 1, startedAtUtc, "{}", 1024)), "other server process");
            Require(Refused(() => HostPipe.Exchange(@"TiaMcp-AddIn-..\..\x", processId, startedAtUtc, "{}", 1024)), "path pipe name");
            // Everything else SelectionHandoff.Send touches.
            Require(!string.IsNullOrEmpty(Environment.MachineName) && Environment.GetEnvironmentVariable("SystemRoot") != null, "environment");
            var reviews = new DirectoryInfo(reviewDirectory).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            Require(reviews.Count == 1 && HandoffJson.Parse(File.ReadAllText(reviews[0].FullName, new UTF8Encoding(false, true))) is Dictionary<string, object>, "review file");
            using (var sha = SHA256.Create()) Require(sha.ComputeHash(typeof(SandboxProbe).Module.ModuleVersionId.ToByteArray()).Length == 32, "module hash");
            Require(typeof(SandboxProbe).Assembly.GetManifestResourceStream("absent") == null && Convert.ToBase64String(Encoding.UTF8.GetBytes(reply)).Length > 0, "resources");
            return "ok";
        }
        catch (Exception error) { return "FAIL " + error.GetType().Name + ": " + error.Message; }
    }

    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
    private static void Require(bool value, string check) { if (!value) throw new InvalidOperationException("check failed: " + check); }
}

internal static class SandboxFixture
{
    private static void Main(string[] args)
    {
        if (args.Length != 1 || File.Exists(args[0])) throw new ArgumentException("Select a new receipt path.");
        string pipeName = "TiaMcp-AddIn-" + Guid.NewGuid().ToString("N"), reviews = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "reviews");
        Directory.CreateDirectory(reviews);
        File.WriteAllText(Path.Combine(reviews, "review.json"), "{\"model\":{\"nodes\":[]}}", new UTF8Encoding(false));
        var server = new Thread(() =>
        {
            while (true)
                using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1))
                {
                    pipe.WaitForConnection();
                    var request = new MemoryStream(); int next;
                    while ((next = pipe.ReadByte()) >= 0 && next != '\n') request.WriteByte((byte)next);
                    if (next < 0) continue;
                    var action = ((Dictionary<string, object>)HandoffJson.Parse(Encoding.UTF8.GetString(request.ToArray())))["action"];
                    byte[] reply = Encoding.UTF8.GetBytes(HandoffJson.Write(new Dictionary<string, object> { ["protocolVersion"] = 1, ["success"] = true,
                        ["result"] = new Dictionary<string, object> { ["echo"] = action } }) + "\n");
                    pipe.Write(reply, 0, reply.Length); pipe.WaitForPipeDrain();
                }
        }) { IsBackground = true };
        server.Start();
        // AddIn.Publisher.xml: EnvironmentPermission, FileIOPermission and SecurityPermission.UnmanagedCode, nothing else.
        var grant = new PermissionSet(PermissionState.None);
        grant.AddPermission(new SecurityPermission(SecurityPermissionFlag.Execution | SecurityPermissionFlag.UnmanagedCode));
        grant.AddPermission(new EnvironmentPermission(PermissionState.Unrestricted));
        grant.AddPermission(new FileIOPermission(PermissionState.Unrestricted));
        var domain = AppDomain.CreateDomain("addin-sandbox", null, new AppDomainSetup { ApplicationBase = AppDomain.CurrentDomain.BaseDirectory }, grant);
        var probe = (SandboxProbe)domain.CreateInstanceAndUnwrap(typeof(SandboxProbe).Assembly.FullName, typeof(SandboxProbe).FullName);
        string result;
        using (var self = Process.GetCurrentProcess())
            result = probe.Run(pipeName, (uint)self.Id, self.StartTime.ToUniversalTime().ToString("O"), reviews);
        File.WriteAllText(args[0], HandoffJson.Write(new Dictionary<string, object> { ["completed"] = result == "ok", ["result"] = result,
            ["clrVersion"] = Environment.Version.ToString(), ["grant"] = "Execution, UnmanagedCode, Environment, FileIO" }) + "\n");
        if (result != "ok") throw new InvalidOperationException("Add-In sandbox fixture: " + result);
        Console.WriteLine("PASS Add-In partial trust: JSON and verified Host pipe exchange under the companion's declared permissions only.");
    }
}
