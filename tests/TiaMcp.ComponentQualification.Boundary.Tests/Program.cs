using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Offline: no TiaPortal constructor, start, attach, discover or native method.
internal static class Program
{
    private static int Main()
    {
        // The same override the builds take from MSBuild's TiaApiDirectory property.
        string api = Environment.GetEnvironmentVariable("TiaApiDirectory") ?? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            string path = Path.Combine(api, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        int passed = CanonicalVectors(Assembly.Load("TiaMcp.Openness.V21"));
        var type = Assembly.Load("TiaMcp.Openness.V21").GetType("TiaMcp.Openness.V21.OpennessSession", true);
        var execute = type.GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        passed += SourceDirectoryForms(type);
        void Reject(bool enabled, string operation, JObject arguments, string expected)
        {
            var session = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { api, enabled }, null);
            try
            {
                execute.Invoke(session, new object[] { operation, arguments });
                throw new Exception("Expected " + expected);
            }
            catch (TargetInvocationException error)
            {
                var inner = error.InnerException;
                string code = (string)inner.GetType().GetProperty("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(inner);
                if (code != expected) throw new Exception("Expected " + expected + ", got " + inner, inner);
                Console.WriteLine("PASS " + operation + " enabled=" + enabled + " -> " + code); passed++;
            }
            finally { ((IDisposable)session).Dispose(); }
        }
        var start = JObject.Parse("{kind:'library_component_qualification',payload:{schemaVersion:1,action:'start',planChangeId:'lib-fixture',planPreviewHash:'unused'}}");
        var id = JObject.Parse("{changeId:'lib-component-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'}");
        Reject(false, "change_prepare", start, "ComponentQualificationDisabled");
        Reject(false, "change_apply", id, "MutationAdmissionRequired");
        Reject(false, "change_status", id, "ComponentQualificationDisabled");
        Reject(true, "change_prepare", start, "OwnedScratchRequired");
        Reject(true, "change_apply", id, "MutationAdmissionRequired");
        Reject(false, "component_window_plan", new JObject(), "ComponentQualificationDisabled");
        Reject(true, "component_window_step", new JObject(), "MutationAdmissionRequired");
        Reject(true, "component_window_read", new JObject(), "ComponentReservationRequired");
        Reject(true, "component_window_source", new JObject(), "ComponentReservationRequired");
        // A durable captured-storage row cannot reconstruct private live
        // authority after restart or enter an ordinary typed operation.
        var changeType = type.GetNestedType("EngineeringChange", BindingFlags.NonPublic);
        var validate = type.GetMethod("ValidateCapturedStorageBindings", BindingFlags.Instance | BindingFlags.NonPublic);
        void StorageBoundary(bool receipt, bool context, string expected)
        {
            var session = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { api, false }, null);
            var change = Activator.CreateInstance(changeType, true);
            if (receipt) ((JArray)changeType.GetField("TypedDependencies", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(change)).Add(new JObject { ["kind"] = "capturedStorageQualification" });
            if (context)
            {
                var contextType = type.GetNestedType("CapturedStorageQualification", BindingFlags.NonPublic);
                changeType.GetField("CapturedStorageQualification", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(change, Activator.CreateInstance(contextType, true));
            }
            try
            {
                validate.Invoke(session, new[] { change });
                if (expected != null) throw new Exception("Expected captured-storage rejection " + expected);
            }
            catch (TargetInvocationException error)
            {
                var inner = error.InnerException;
                string code = (string)inner.GetType().GetProperty("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(inner);
                if (code != expected) throw new Exception("Expected " + expected + ", got " + inner, inner);
            }
            finally { ((IDisposable)session).Dispose(); }
            Console.WriteLine("PASS captured storage boundary receipt=" + receipt + " context=" + context + " -> " + (expected ?? "ordinary route unchanged")); passed++;
        }
        StorageBoundary(false, false, null);
        StorageBoundary(true, false, "StaleDeclaration");
        StorageBoundary(false, true, "StaleDeclaration");
        Console.WriteLine(passed + " offline production-routing boundary checks passed; no native session started.");
        return 0;
    }

    // A source directory hashes to the same two forms the Host's ComponentWindowEvidence.SourceHashes expects (TiaMcp.ComponentWindow.Tests
    // pins the same digests): RFC 8785, then the earlier worker's compact Newtonsoft JSON, which escapes U+2028.
    private static int SourceDirectoryForms(Type session)
    {
        string directory = Path.Combine(Path.GetTempPath(), "canonical-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "a\u2028b.txt"), "x");
            var hashes = (string[])session.GetMethod("LibraryDirectoryHashes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { directory });
            if (hashes.Length != 2 || hashes[0] != "e4f64a84bafa84cdeb6ec6cd81717ac1eeac986031c74385de61526088fa5e8f" ||
                hashes[1] != "978da03ddf2a095d1b12614996a298a621b0d5c27fb2ea3cbc69ab41c47d3a63")
                throw new Exception("Worker source directory hashes differ from the Host's pinned forms: " + string.Join(",", hashes));
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS worker source directory hashes match the Host's two forms");
        return 1;
    }

    // The worker's component manifest and request hashes must equal the Host's byte for byte (shared RFC 8785 vectors).
    private static int CanonicalVectors(Assembly worker)
    {
        var canonical = worker.GetType("TiaMcp.Openness.V21.CanonicalJson", true);
        var serialize = canonical.GetMethod("Serialize", BindingFlags.Static | BindingFlags.NonPublic);
        var sha256 = canonical.GetMethod("Sha256", BindingFlags.Static | BindingFlags.NonPublic);
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "fixtures", "canonical-json", "vectors.v1.json"))) directory = directory.Parent;
        JObject vectors;
        using (var file = new JsonTextReader(new StringReader(File.ReadAllText(Path.Combine(directory.FullName, "fixtures", "canonical-json", "vectors.v1.json")))) { DateParseHandling = DateParseHandling.None })
            vectors = JObject.Load(file);
        int passed = 0;
        foreach (JObject vector in vectors["cases"])
        {
            string name = (string)vector["name"];
            try
            {
                JToken input;
                if (vector["utf16"] is JArray units)
                {
                    string text16 = new string(units.Select(u => (char)(int)u).ToArray());
                    input = (string)vector["as"] == "key" ? new JObject { [text16] = 1 } : (JToken)new JValue(text16);
                }
                else // The worker's own request reader (Program.cs), with a deeper bound so the depth cases reach the canonical limit.
                    using (var reader = (JsonTextReader)Activator.CreateInstance(worker.GetType("TiaMcp.Openness.V21.ExactNumberReader", true),
                        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { (string)vector["input"] }, null))
                    {
                        reader.MaxDepth = 256; reader.DateParseHandling = DateParseHandling.None;
                        input = JToken.ReadFrom(reader);
                        // Program.cs refuses such a request with InvalidRequest after reading its id.
                        if ((bool)reader.GetType().GetProperty("OutOfRange", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reader))
                        {
                            if ((string)vector["refusal"] != "InvalidRequest") throw new Exception("Unexpected range refusal for vector: " + name);
                            passed++; continue;
                        }
                    }
                string text = (string)serialize.Invoke(null, new object[] { input }), digest = (string)sha256.Invoke(null, new object[] { input });
                if (vector["refusal"] != null) throw new Exception("Expected " + vector["refusal"] + " for vector: " + name);
                if (text != (string)vector["canonical"] || digest != (string)vector["sha256"]) throw new Exception("Worker canonical JSON differs from the shared vector: " + name);
            }
            catch (TargetInvocationException error)
            {
                string code = (string)error.InnerException.GetType().GetProperty("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(error.InnerException);
                if (code == null || code != (string)vector["refusal"]) throw new Exception("Unexpected refusal for vector: " + name, error.InnerException);
            }
            passed++;
        }
        // Values an earlier worker stored (admission observations, reserved manifests, source snapshots) used ordinal-sorted
        // compact Newtonsoft JSON, which escapes U+2028 and writes 1.0 for a double; they still verify.
        var legacy = canonical.GetMethod("LegacySha256", BindingFlags.Static | BindingFlags.NonPublic);
        var matches = canonical.GetMethod("Matches", BindingFlags.Static | BindingFlags.NonPublic);
        string Digest(string text) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        var observed = new JObject { ["b"] = 1.0, ["a"] = "\u2028" };
        string earlier = Digest("{\"a\":\"\\u2028\",\"b\":1.0}"), current = Digest("{\"a\":\"\u2028\",\"b\":1}");
        if ((string)legacy.Invoke(null, new object[] { observed }) != earlier || (string)sha256.Invoke(null, new object[] { observed }) != current ||
            !(bool)matches.Invoke(null, new object[] { earlier, observed }) || !(bool)matches.Invoke(null, new object[] { current, observed }) ||
            (bool)matches.Invoke(null, new object[] { Digest("{}"), observed }))
            throw new Exception("Worker hashes stored before RFC 8785 must still verify, and only those.");
        passed++;
        Console.WriteLine("PASS worker RFC 8785 canonical JSON: " + (passed - 1) + " shared vectors and the earlier stored form");
        return passed;
    }
}
