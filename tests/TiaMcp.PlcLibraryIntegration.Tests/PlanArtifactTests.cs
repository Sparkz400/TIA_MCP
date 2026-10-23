using System.Security.Cryptography;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class PlanArtifactTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (bool standalone in new[] { false, true })
            test("component pinned artifact plan matches pure XML plan, standalone=" + standalone, () => {
                using var f = new Files(standalone); string payload = f.Input.Payload.ToString(), versions = f.Input.Versions.ToString();
                var expected = f.Input.Build(); if (!standalone) expected["callerArtifact"] = f.Caller.DeepClone();
                Equal(expected, f.Build());
                if (payload != f.Input.Payload.ToString() || versions != f.Input.Versions.ToString()) throw new Exception("Planning changed its original inputs.");
            });
        test("component root mutation cannot change the mapped interface behind its original hash", () => {
            using var f = new Files();
            f.Input.Payload["integration"]["mappings"][0]["parameter"] = "SwitchOn";
            var changed = XDocument.Load(f.RootPath);
            changed.Descendants().First(e => e.Name.LocalName == "Member").SetAttributeValue("Name", "SwitchOn");
            File.WriteAllText(f.RootPath, changed.ToString());
            // Positive control for the former adapter: a separately loaded XML document
            // can build a plan carrying an unrelated earlier raw-content hash.
            var unbound = PlcLibraryIntegrationPlanner.Build(f.Input.Payload, f.Input.Versions, changed, f.Input.Caller, f.Input.Inventory);
            if ((string)unbound["versions"][0]["contentSha256"] != (string)f.Input.Versions[0]["contentSha256"])
                throw new Exception("The unbound control did not retain the original hash.");
            Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component caller mutation cannot change insertion behind its original hash", () => {
            using var f = new Files(); File.AppendAllText(f.CallerPath, "\n");
            Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component missing root bytes refuse", () => {
            using var f = new Files(); File.Delete(f.RootPath); Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component missing root descriptor refuses", () => {
            using var f = new Files(); ((JObject)f.Input.Versions[0]).Remove("artifact"); Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component contradictory root descriptor refuses", () => {
            using var f = new Files(); f.Input.Versions[0]["artifact"]["sha256"] = new string('b', 64); Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component original byte count must match even when its hash is correct", () => {
            using var f = new Files(); f.Input.Versions[0]["artifact"]["bytes"] = new FileInfo(f.RootPath).Length + 1;
            Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component byte counts reject coercion negatives and overflow", () => {
            foreach (JToken length in new JToken[] { new JValue("1"), new JValue(-1), new JValue(1.5), JValue.CreateNull(),
                JToken.Parse("9223372036854775808"), new JValue(4 * 1024 * 1024 + 1) })
            {
                using var f = new Files(); f.Input.Versions[0]["artifact"]["bytes"] = length.DeepClone();
                Reject("ComponentReadbackMismatch", () => f.Build());
            }
        });
        test("component contradictory caller descriptor refuses", () => {
            using var f = new Files(); f.Caller["sha256"] = new string('b', 64); Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component duplicate selected root refuses before artifact access", () => {
            using var f = new Files(); f.Input.Versions.Add(f.Input.Versions[0].DeepClone()); File.Delete(f.RootPath);
            Reject("DependencyPinMismatch", () => f.Build());
        });
        test("component unmatched selected root refuses before artifact access", () => {
            using var f = new Files(); f.Input.Payload["selection"][0]["versionGuid"] = Guid.NewGuid().ToString("D"); File.Delete(f.RootPath);
            Reject("DependencyPinMismatch", () => f.Build());
        });
        test("component existing caller cannot omit its artifact", () => {
            using var f = new Files(); f.Caller = null; Reject("IntegrationCallerMismatch", () => f.Build());
        });
        test("component standalone caller cannot adopt an artifact", () => {
            using var f = new Files(true); f.Caller = (JObject)f.Input.Versions[0]["artifact"].DeepClone(); Reject("IntegrationCallerMismatch", () => f.Build());
        });
        test("component oversized root refuses with honestly updated hashes", () => {
            using var f = new Files(); File.WriteAllBytes(f.RootPath, new byte[4 * 1024 * 1024 + 1]); f.PinRoot();
            Reject("ComponentReadbackMismatch", () => f.Build());
        });
        test("component malformed recipe refuses before missing artifact reads", () => {
            using var f = new Files(); f.Input.Payload["planOnly"] = false; File.Delete(f.RootPath); Reject("InvalidIntegrationRecipe", () => f.Build());
        });
        foreach (bool caller in new[] { false, true })
            test("component pinned planner refuses malformed original text, caller=" + caller, () => {
                using var f = new Files(); string path = caller ? f.CallerPath : f.RootPath;
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes("<!--").Concat(new byte[] { 0x80 })
                    .Concat(System.Text.Encoding.UTF8.GetBytes("-->")).Concat(File.ReadAllBytes(path)).ToArray();
                File.WriteAllBytes(path, bytes);
                if (caller)
                {
                    f.Caller["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    f.Caller["bytes"] = bytes.Length;
                }
                else f.PinRoot();
                Reject("ComponentReadbackMismatch", () => f.Build());
            });
        test("component pinned plan retains exact caller descriptor independently of later input edits", () => {
            using var f = new Files(); JObject expected = (JObject)f.Caller.DeepClone(); var plan = f.Build(); f.Caller["path"] = "changed";
            Equal(expected, plan["callerArtifact"]);
        });
        test("component pinned plan consumes retained original native FB export", () => {
            using var f = new Files();
            string original = Path.Combine(RepositoryFiles.Root(), "fixtures/libraries/component-qualification/oracle-xp01/source-43b5c338-b589-4ab5-8c82-cae3e08e9d06-options1.xml");
            byte[] bytes = File.ReadAllBytes(original); File.WriteAllBytes(f.RootPath, bytes); f.PinRoot();
            f.Input.Fb = EngineeringSources.ReadXml(File.ReadAllText(f.RootPath));
            f.Input.Versions[0]["typeName"] = "TiaMcpComponentGate";
            var expected = f.Input.Build(); expected["callerArtifact"] = f.Caller.DeepClone(); Equal(expected, f.Build());
            if (!bytes.SequenceEqual(File.ReadAllBytes(original))) throw new Exception("Original fixture changed.");
        });
    }
    private static void Equal(JToken expected, JToken actual) { if (!JToken.DeepEquals(expected, actual)) throw new Exception("Plan differs from its original XML/descriptor inputs."); }
    private static void Reject(string code, Action action)
    {
        try { action(); }
        catch (PlcComponentException error) when (error.Code == code) { return; }
        catch (PlcIntegrationPlanException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
    private sealed class Files : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "tia-plan-artifacts-" + Guid.NewGuid().ToString("N"));
        internal readonly Fixture Input = new();
        internal readonly string RootPath, CallerPath;
        internal JObject Caller;
        internal Files(bool standalone = false)
        {
            Directory.CreateDirectory(directory); RootPath = Path.Combine(directory, "root.xml"); CallerPath = Path.Combine(directory, "caller.xml");
            if (standalone) Input.Standalone();
            File.WriteAllText(RootPath, Input.Fb.ToString()); PinRoot();
            if (!standalone) { File.WriteAllText(CallerPath, Input.Caller.ToString()); Caller = Descriptor(CallerPath); }
        }
        internal void PinRoot()
        {
            var descriptor = Descriptor(RootPath); Input.Versions[0]["contentSha256"] = descriptor["sha256"].DeepClone(); Input.Versions[0]["artifact"] = descriptor;
        }
        internal JObject Build() => PlcLibraryIntegrationPlanner.BuildFromArtifacts(Input.Payload, Input.Versions, Caller, Input.Inventory);
        private static JObject Descriptor(string path) => new() { ["path"] = path, ["bytes"] = new FileInfo(path).Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) };
        public void Dispose() => Directory.Delete(directory, true);
    }
}
