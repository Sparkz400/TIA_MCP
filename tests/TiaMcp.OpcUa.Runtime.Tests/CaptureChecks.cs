using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal static class CaptureChecks
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
    private static string Id() => Guid.NewGuid().ToString("N");
    internal static async Task Run(FixtureServer server, string output, Action<bool,string> check)
    {
        string root = Path.Combine(output,"capture-checks");
        await using var sessions = new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
        var captures = new OpcUaRuntimeCaptures(sessions,root);
        var boundaries = new List<object>();
        async Task CompleteOriginal(FixtureServer.ReadBoundary boundary, string scenario, string originalAttempt)
        {
            check(!boundary.Completed.Task.IsCompleted,"Original "+scenario+" server read remains pending after local cancellation");
            int before=server.Audit.Count;
            boundary.Release.TrySetResult();
            await boundary.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var delta=server.Audit.ToArray().Skip(before).Select(Json).ToArray();
            check(delta.Length==1 && delta[0].GetProperty("operation").GetString()=="Read" &&
                delta[0].GetProperty("requestHandle").GetUInt32()==boundary.RequestHandle,
                "Exact original "+scenario+" read completion explains one late audit event");
            check(server.ActiveReads==0,"Original "+scenario+" request completion reached before next scenario");
            boundaries.Add(new {scenario,originalAttempt,boundary.RequestHandle,boundary.EnteredAt,boundary.CompletedAt,
                before,after=server.Audit.Count,delta,oldUnsettledOracleWouldFail=before!=server.Audit.Count});
        }
        try
        {
            string attempt = Id(); await sessions.Connect(attempt,server.Selection);
            string capture = Id();
            string[] names = ["int64","uint64","double","bool","text","bad","uncertain","missingTimes","array","denied","structure","byteArray","byteString","minimumDate"];
            var request = new OpcUaCaptureRequest(attempt,names.Select(FixtureServer.Node).ToArray(),100,10,names.Length);
            await captures.Start(capture,request,default);
            var status = await End(captures,capture);
            check(status.GetProperty("schemaVersion").GetString()=="opcua-runtime-capture-status.v1","UA capture distinct status schema");
            check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit","UA capture sample limit");
            var rows = Json(captures.Page(status.GetProperty("snapshotRef").GetString()!,0,200)).GetProperty("rows").EnumerateArray().Where(x=>x.GetProperty("kind").GetString()=="read").ToArray();
            check(rows.Length==names.Length,"UA captures every original selected node");
            var items = rows.Select(row=>JsonDocument.Parse(row.GetProperty("readResultJson").GetString()!).RootElement.GetProperty("items")[0].Clone()).ToArray();
            check(items.Select(x=>x.GetProperty("node").GetProperty("identifier").GetString()).SequenceEqual(names),"UA captured selection order preserved");
            check(items[0].GetProperty("value").GetString()=="-9223372036854775808" && items[1].GetProperty("value").GetString()=="18446744073709551615","UA capture exact integer extrema");
            check(items[5].GetProperty("quality").GetString()=="Bad" && items[5].GetProperty("value").ValueKind==JsonValueKind.Null,"UA capture bad value remains suppressed");
            check(items[6].GetProperty("quality").GetString()=="Uncertain" && items[6].GetProperty("statusCode").GetString()=="0x40920000","UA capture uncertain status remains exact");
            check(items[7].GetProperty("sourceTimestamp").ValueKind==JsonValueKind.Null && items[7].GetProperty("serverTimestamp").ValueKind==JsonValueKind.Null,"UA capture missing clocks remain null");
            check(items[0].GetProperty("sourcePicoseconds").GetInt32()==12 && items[0].GetProperty("serverPicoseconds").GetInt32()==34,"UA capture picoseconds preserved");
            check(items[8].GetProperty("encoding").GetString()=="array","UA capture array retained");
            string reference=status.GetProperty("snapshotRef").GetString()!;
            var exported=Json(await captures.Export(reference,"csv",default));
            foreach(var artifact in exported.GetProperty("artifacts").EnumerateArray())
            {
                var bytes=File.ReadAllBytes(artifact.GetProperty("path").GetString()!);
                check(Convert.ToHexStringLower(SHA256.HashData(bytes))==artifact.GetProperty("sha256").GetString(),"UA export exact artifact SHA");
            }
            var jsonExport=JsonDocument.Parse(File.ReadAllBytes(exported.GetProperty("artifacts")[0].GetProperty("path").GetString()!)).RootElement;
            check(jsonExport.GetProperty("schemaVersion").GetString()=="opcua-runtime-capture-export.v1","UA capture distinct export schema");
            check(JsonElement.DeepEquals(jsonExport.GetProperty("rows"),Json(captures.Page(reference,0,200)).GetProperty("rows")),"UA export exact original row payloads");
            File.WriteAllText(Path.Combine(root,"original-export.json"),jsonExport.GetRawText());
            var retainedPlan=jsonExport.GetProperty("capture").GetProperty("plan").Deserialize<RuntimeCapturePlan>(BridgeProtocol.Json)!;
            foreach (var mutation in new (int Index,string Name,Action<JsonObject> Change)[] {
                (0,"nondecimal integer",v=>v["value"]="not an integer"),
                (0,"integer with Boolean encoding",v=>{v["encoding"]="boolean";v["value"]=true;}),
                (1,"UInt64 overflow",v=>v["value"]="18446744073709551616"),
                (0,"Int64 overflow",v=>v["value"]="-9223372036854775809"),
                (0,"integer leading zero",v=>v["value"]="01"),
                (2,"nonfinite double",v=>v["value"]="NaN"),
                (2,"noncanonical double",v=>v["value"]="0.1250"),
                (12,"noncanonical base64",v=>v["value"]="AQID\n"),
                (13,"noncanonical scalar date",v=>v["value"]="0001-01-01T00:00:00Z"),
                (0,"malformed DataType identity",v=>v["dataType"]!["identifier"]="+8") })
            {
                var row=rows[mutation.Index].Deserialize<RuntimeCaptureRow>(BridgeProtocol.Json)!;
                var packet=JsonNode.Parse(row.ReadResultJson!)!.AsObject();mutation.Change(packet["items"]![0]!.AsObject());
                string? rejected=null;
                try{OpcUaCaptureContracts.ValidateRead(retainedPlan,row with {ReadResultJson=packet.ToJsonString()});}
                catch(BridgeException e){rejected=e.Code;}
                check(rejected=="UaCaptureInvalid","UA retained scalar mutation refused: "+mutation.Name);
            }
            var customRow=rows[0].Deserialize<RuntimeCaptureRow>(BridgeProtocol.Json)!;
            var custom=JsonNode.Parse(customRow.ReadResultJson!)!.AsObject();
            custom["items"]![0]!["dataType"]=JsonSerializer.SerializeToNode(new OpcUaRuntimeNode("urn:custom:types","string","CounterType"),BridgeProtocol.Json);
            OpcUaCaptureContracts.ValidateRead(retainedPlan,customRow with {ReadResultJson=custom.ToJsonString()});
            check(true,"UA original custom declared DataType not equated with decoded builtin type");
            int services=server.Audit.Count;
            await captures.Start(capture,request,default); await captures.Stop(capture,default);
            check(server.Audit.Count==services,"UA capture repeat start and stop never replay");
            await Refuse(()=>captures.Start(capture,request with {DurationSeconds=11},default),"UaCaptureRequestChanged",check);
            await Refuse(()=>captures.Start(Id(),request with {Nodes=[request.Nodes[0],request.Nodes[0]]},default),"UaCaptureInvalid",check);
            string running=Id();
            await captures.Start(running,new(attempt,[FixtureServer.Node("bool")],1000,10),default);
            await Refuse(()=>captures.Start(Id(),request,default),"UaCaptureSessionBusy",check);
            await Task.Delay(150); var stopped=Json(await captures.Stop(running,default));
            check(stopped.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="stopped","UA explicit stop terminal reason");
            services=server.Audit.Count; await Task.Delay(200);
            check(server.Audit.Count==services,"UA no requests after stop completion");
            if((await sessions.Status(attempt)).State!="connected") { await sessions.Disconnect(attempt); attempt=Id(); await sessions.Connect(attempt,server.Selection); }
            server.ReadDelayMs=250;
            string slow=Id(); await captures.Start(slow,new(attempt,[FixtureServer.Node("bool")],100,1),default);
            var delayed=await End(captures,slow);
            check(delayed.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()>0,"UA slow reads retain skipped polling slots");
            check(delayed.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="durationLimit","UA long read crossing deadline preserves duration reason");
            server.ReadDelayMs=0;
            var settling=Stopwatch.StartNew();
            while(server.ActiveReads>0 && settling.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(10);
            check(server.ActiveReads==0,"Prior cancelled server read reaches its own completion boundary");
            await sessions.Disconnect(attempt); attempt=Id(); await sessions.Connect(attempt,server.Selection);
            string failed=Id(); server.Mode="stale";
            await captures.Start(failed,new(attempt,[FixtureServer.Node("bool")],100,3),default);
            var failure=await End(captures,failed);
            check(failure.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sessionUnavailable","UA invalid session ends capture");
            services=server.Audit.Count; await Task.Delay(200);
            check(server.Audit.Count==services,"UA failure does not reconnect or retry");
            server.Mode="normal"; await sessions.Disconnect(attempt);

            // Fail actual publication with an owned directory collision; the committed prefix remains immutable.
            attempt=Id(); await sessions.Connect(attempt,server.Selection);
            string fault=Id(); var faultBoundary=server.HoldNextValueRead();
            var prefix=Json(await captures.Start(fault,new(attempt,[FixtureServer.Node("bool")],1000,5),default));
            await faultBoundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string directory=Path.Combine(root,"runtime-ua-acquisition","runtime-captures",fault);
            Directory.CreateDirectory(Path.Combine(directory,"000001.segment"));
            var faulty=Json(await captures.Stop(fault,default));
            check(faulty.GetProperty("localFailure").GetString()=="RuntimeCaptureStorageFailure","UA actual publication failure visible");
            check(faulty.GetProperty("snapshotRef").GetString()==prefix.GetProperty("snapshotRef").GetString(),"UA failed publication preserves committed head");
            await sessions.Disconnect(attempt);
            await CompleteOriginal(faultBoundary,"publication-failure",attempt);

            attempt=Id(); await sessions.Connect(attempt,server.Selection);
            var shutdownBoundary=server.HoldNextValueRead();
            string shutdown=Id(); await captures.Start(shutdown,new(attempt,[FixtureServer.Node("bool")],1000,30),default);
            await shutdownBoundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await captures.DisposeAsync();
            await CompleteOriginal(shutdownBoundary,"host-shutdown",attempt);
            captures=new(sessions,root);
            services=server.Audit.Count;
            check(Json(await captures.Status(shutdown,default)).GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="hostShutdown","UA disposal finalizes owned running capture");
            var reopened=Json(await captures.Status(capture,default));
            check(reopened.GetProperty("snapshotRef").GetString()==reference,"UA capture reopen exact original snapshot");
            check(Json(await captures.Status(fault,default)).GetProperty("state").GetString()=="interrupted","UA incomplete journal reopens interrupted");
            check(server.Audit.Count==services,"UA reopen makes no network request");
            await sessions.Disconnect(attempt);
            attempt=Id(); await sessions.Connect(attempt,server.Selection);
            services=server.Audit.Count;
            await sessions.Read(attempt,[FixtureServer.Node("bool")]);
            check(server.Audit.Count!=services,"No-network oracle rejects an unrelated actual service request");
            // Rehash a zero-segment plan: computed session flags must not be silently normalized into authority.
            string faultPlan=Path.Combine(directory,"plan.json"), faultHead=Path.Combine(directory,"head.json");
            byte[] planOriginal=File.ReadAllBytes(faultPlan), headOriginal=File.ReadAllBytes(faultHead);
            var altered=JsonNode.Parse(planOriginal)!.AsObject();
            altered["uaContext"]!["session"]!["physicalQualified"]=true;
            byte[] alteredBytes=JsonSerializer.SerializeToUtf8Bytes(altered,BridgeProtocol.Json);
            File.WriteAllBytes(faultPlan,alteredBytes);
            File.WriteAllText(faultHead,JsonSerializer.Serialize($"capture:{fault}:0:{Convert.ToHexStringLower(SHA256.HashData(alteredBytes))}"));
            await Refuse(()=>captures.Status(fault,default),"RuntimeCaptureIntegrityFailure",check);
            File.WriteAllBytes(faultPlan,planOriginal);File.WriteAllBytes(faultHead,headOriginal);
            var tamper=Path.Combine(root,"runtime-ua-acquisition","runtime-captures",capture,"000001.segment");
            byte[] original=File.ReadAllBytes(tamper);
            File.WriteAllText(tamper,"{}");
            await Refuse(()=>captures.Status(capture,default),"RuntimeCaptureIntegrityFailure",check);
            File.WriteAllBytes(tamper,original);
            check(Json(await captures.Status(capture,default)).GetProperty("snapshotRef").GetString()==reference,"UA original restored evidence verifies");
        }
        finally
        {
            File.WriteAllText(Path.Combine(root,"original-read-boundaries.json"),JsonSerializer.Serialize(boundaries,BridgeProtocol.Json));
            server.ReadDelayMs=0;server.Mode="normal";await captures.DisposeAsync();
        }
    }
    private static async Task<JsonElement> End(OpcUaRuntimeCaptures captures,string id)
    {
        var clock=Stopwatch.StartNew();
        while(clock.Elapsed<TimeSpan.FromSeconds(20))
        {
            var status=Json(await captures.Status(id,default));
            if(status.GetProperty("state").GetString()!="running")return status;
            await Task.Delay(20);
        }
        throw new Exception("UA capture did not terminate within its bounded qualification window.");
    }
    private static async Task Refuse(Func<Task<object>> action,string expected,Action<bool,string> check)
    {
        string? code=null;try{await action();}catch(BridgeException e){code=e.Code;}
        check(code==expected,"UA capture refused "+expected+" actual="+code);
    }
}
