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
            JToken input;
            if (vector["utf16"] is JArray units)
            {
                string text = new string(units.Select(u => (char)(int)u).ToArray());
                input = (string)vector["as"] == "key" ? new JObject { [text] = 1 } : (JToken)new JValue(text);
            }
            else // Read as the worker reads requests (Program.cs), with a deeper bound so the depth cases reach the canonical limit.
                using (var reader = new JsonTextReader(new StringReader((string)vector["input"])) { MaxDepth = 256, DateParseHandling = DateParseHandling.None })
                    input = JToken.ReadFrom(reader);
            try
            {
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
        Console.WriteLine("PASS worker RFC 8785 canonical JSON: " + passed + " shared vectors");
        return passed;
    }
}
