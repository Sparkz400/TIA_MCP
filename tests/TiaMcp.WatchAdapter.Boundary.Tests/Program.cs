using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

// Loads the production worker's pure guard. Never constructs or attaches a TiaPortal.
internal static class Program
{
    private static int Main()
    {
        string api = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            string path = Path.Combine(api, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var type = Assembly.Load("TiaMcp.Openness.V21").GetType("TiaMcp.Openness.V21.OpennessSession", true);
        var guard = type.GetMethod("EnsureWatchTargetFresh", BindingFlags.Static | BindingFlags.NonPublic);
        var before = JObject.Parse(@"{processId:41,projectModified:false,peerSha256:'peers',
            target:{isConsistent:true,semanticSha256:'content'},observation:{
            reference:{object:{projectPath:'D:/fixture/project.ap21',plcPath:'/PLC',scopePath:'/PLC',objectPath:'/watch/Fixture',kind:'watchTable'},
            snapshot:{observationId:'capture-before',sourceSha256:'source',tiaVersion:21},contentSha256:'content'},
            projectFilePath:'D:/fixture/project.ap21',sessionEpoch:'epoch',adapterSha256:'adapter',profileSha256:'profile',
            hardwareIdentity:'hardware',nativeIdentity:null,offline:true,probes:[{name:'offline',state:'observed',evidenceSha256:'source'}]}}");
        var fresh = (JObject)before.DeepClone(); fresh.SelectToken("observation.reference.snapshot.observationId").Replace("new-observation");
        guard.Invoke(null, new object[] { before, fresh }); int passed = 1;
        foreach (string path in new[] { "processId", "projectModified", "target.isConsistent", "peerSha256", "target.semanticSha256",
            "observation.reference.object.projectPath", "observation.reference.object.plcPath", "observation.reference.object.scopePath",
            "observation.reference.object.objectPath", "observation.reference.object.kind", "observation.reference.snapshot.tiaVersion",
            "observation.reference.snapshot.sourceSha256", "observation.reference.contentSha256", "observation.projectFilePath",
            "observation.sessionEpoch", "observation.adapterSha256", "observation.profileSha256", "observation.hardwareIdentity",
            "observation.nativeIdentity", "observation.offline", "observation.probes[0].state", "observation.probes[0].evidenceSha256" })
        {
            var changed = (JObject)fresh.DeepClone(); var value = changed.SelectToken(path);
            value.Replace(value.Type == JTokenType.Integer ? new JValue(99) : value.Type == JTokenType.Boolean ? new JValue(!value.Value<bool>()) : new JValue("different"));
            bool refused = false;
            try { guard.Invoke(null, new object[] { before, changed }); }
            catch (TargetInvocationException error)
            {
                string code = (string)error.InnerException.GetType().GetProperty("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(error.InnerException);
                if (code == null) throw new Exception("Unexpected guard error for " + path, error.InnerException);
                refused = code == "StaleNativeTarget" || code == "StaleContent";
            }
            if (!refused) throw new Exception("Worker accepted changed native boundary: " + path);
            passed++;
        }
        if (!JToken.DeepEquals(before["observation"]["reference"]["snapshot"]["observationId"], new JValue("capture-before")))
            throw new Exception("Guard mutated retained observation.");
        var queryFactory = type.GetMethod("WatchInstanceAnalysisQuery", BindingFlags.Static | BindingFlags.NonPublic);
        var query = (JObject)queryFactory.Invoke(null, new object[] { "Project", "/PLC", "/PLC/blocks/Instance" });
        var limit = type.GetMethod("Limit", BindingFlags.Static | BindingFlags.NonPublic);
        int admitted = (int)limit.Invoke(null, new object[] { query, 500 });
        if (admitted != 2000) throw new Exception("Watch generation query violates shared analysis budget.");
        passed++;
        if (query.Value<string>("projectName") != "Project" || query.Value<string>("plcPath") != "/PLC" ||
            query.Value<string>("blockPath") != "/PLC/blocks/Instance") throw new Exception("Watch analysis query lost exact source scope.");
        passed++;
        bool invalidBudgetRefused = false;
        try { limit.Invoke(null, new object[] { new JObject { ["maxItems"] = 10000 }, 500 }); }
        catch (TargetInvocationException error) { invalidBudgetRefused = error.InnerException.Message.Contains("2000"); }
        if (!invalidBudgetRefused) throw new Exception("Shared analysis bound unexpectedly widened.");
        passed++;
        var journal = type.GetMethod("TranslationCheckRecord", BindingFlags.Static | BindingFlags.NonPublic);
        string root = Path.Combine(Path.GetTempPath(), "translation-worker-journal-" + Guid.NewGuid().ToString("N"));
        string id = new string('e', 32), directory = Path.Combine(root, "engineering-operations", id);
        Directory.CreateDirectory(directory);
        string original = "{\"id\":\"" + id + "\",\"revision\":1,\"state\":\"acquired\",\"plan\":{\"operation\":\"translation.import\"},\"recordedAt\":\"2026-10-16T07:27:19.062622+00:00\"}";
        File.WriteAllText(Path.Combine(directory, "000001.json"), original);
        JObject wire;
        using (var reader = new JsonTextReader(new StringReader(original)) { DateParseHandling = DateParseHandling.None }) wire = JObject.Load(reader);
        var context = new JObject { ["artifactRoot"] = root };
        journal.Invoke(null, new object[] { context, wire }); passed++;
        foreach (string field in new[] { "recordedAt", "revision", "state" })
        {
            var changed = (JObject)wire.DeepClone(); changed[field] = "changed"; bool refused = false;
            try { journal.Invoke(null, new object[] { context, changed }); }
            catch (TargetInvocationException error) { refused = error.InnerException.Message.Contains("exact latest"); }
            if (!refused) throw new Exception("Worker accepted changed journal field " + field);
            passed++;
        }
        Console.WriteLine("PASS Production worker watch target/query and translation journal guards: " + (passed + 1) + " checks; offline reflection only, no native calls.");
        return 0;
    }
}
