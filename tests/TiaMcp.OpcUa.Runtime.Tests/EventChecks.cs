using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal static class EventChecks
{
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,BridgeProtocol.Json);
    private static string Id()=>Guid.NewGuid().ToString("N");
    internal static async Task Run(FixtureServer server,string output,Action<bool,string> check)
    {
        string root=Path.Combine(output,"event-checks");
        await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
        await using var captures=new OpcUaRuntimeSubscriptions(sessions,root);
        string attempt=Id();await sessions.Connect(attempt,server.Selection);
        string capture=Id();
        await captures.Start(capture,new(attempt,[FixtureServer.Node("int64")],DurationSeconds:8,MaximumNotifications:3,Events:new(FixtureServer.Node("fixture"),500)),default);
        await Wait(async()=>Json(await captures.Status(capture,default)).GetProperty("recordedProgress").GetProperty("rows").GetInt64()>0);
        server.EmitEvent(1,100);server.EmitEvent(2,700);server.EmitEvent(3,900);
        await Wait(async()=>Json(await captures.Status(capture,default)).GetProperty("state").GetString()!="running");
        var status=Json(await captures.Status(capture,default));
        var rows=Json(captures.Page(status.GetProperty("snapshotRef").GetString()!,0,200)).GetProperty("rows").EnumerateArray().Select(x=>x.Deserialize<OpcUaSubscriptionRow>(BridgeProtocol.Json)!).ToArray();
        File.WriteAllText(Path.Combine(root,"observed.json"),Json(new{status,rows}).GetRawText());
        var events=rows.SelectMany(r=>r.Event.Events??[]).ToArray();
        check(events.Length==2,"Actual event filter selects two high-severity events");
        check(events.Select(e=>e.Fields[0].Text).SequenceEqual(new[]{Convert.ToBase64String("fixture-event-2"u8),Convert.ToBase64String("fixture-event-3"u8)}),"Exact original event identities; low severity excluded");
        check(events.All(e=>e.Fields[2].Node==FixtureServer.Node("fixture") && e.Fields[6].Locale=="de-DE" && e.Fields[6].Text!.Contains("<script>")),"Event source and localized hostile message retained exactly");
        check(rows.SelectMany(r=>r.Event.Notifications??[]).Single().Value.Node==FixtureServer.Node("int64"),"Selected signal remains alongside events in the original subscription");
        check(status.GetProperty("recordedProgress").GetProperty("notifications").GetInt64()==3,"Shared notification bound covers signals and events");
        check(status.GetProperty("recordedProgress").GetProperty("remoteCleanupConfirmed").GetBoolean(),"Original event subscription cleanup confirmed");
        check(events[0].Fields[4].Text==FixtureServer.SourceTime.AddMilliseconds(2).ToString("O") && events[0].Fields[5].Text==FixtureServer.ServerTime.AddMilliseconds(2).ToString("O"),"Original event and server receive clocks remain distinct");
        var reference=status.GetProperty("snapshotRef").GetString()!;
        foreach(string format in new[]{"html","markdown","csv"})
        {
            var export=Json(await captures.Export(reference,format,default));
            var paths=export.GetProperty("artifacts").EnumerateArray().Select(a=>a.GetProperty("path").GetString()!).ToArray();
            check(paths.Length==2,"Event export retains exact companion JSON: "+format);
            string text=File.ReadAllText(paths[1]);
            check(text.Contains(format=="html"?"&lt;script&gt;":format=="markdown"?"&lt;script&gt;":"\\u003Cscript\\u003E"),"Hostile event message preserved and safely rendered: "+format);
        }
        await SubscriptionChecks.RetainedControls(root,sessions,capture,status,check,[
            ("event association",s=>FirstEvent(s)["monitoredItemId"]=uint.MaxValue),
            ("invented event overflow",s=>FirstEvent(s)["overflow"]=true),
            ("missing event identity",s=>FirstEvent(s)["fields"]![0]!["kind"]="null"),
            ("wrong event type",s=>FirstEvent(s)["fields"]![7]!["text"]="700.5"),
            ("misordered event fields",s=>FirstEvent(s)["fields"]![6]!["name"]="Severity")]);
        await sessions.Disconnect(attempt);
        foreach(string mode in new[]{"eventNullMessage","eventUnavailable","eventBadHandle","eventBadCount","eventBadType","eventBadIdentity","subscriptionCreateItemsDenied","subscriptionDuplicate","subscriptionGap","overflow","recovery","repeatedId"})
        {
            server.Mode=mode is "overflow" or "recovery" or "repeatedId"?"normal":mode;
            attempt=Id();await sessions.Connect(attempt,server.Selection);capture=Id();
            bool several=mode is "subscriptionDuplicate" or "subscriptionGap" or "overflow" or "recovery" or "repeatedId";
            await captures.Start(capture,new(attempt,[],DurationSeconds:3,KeepAliveCount:1,MaximumNotifications:several?100:1,
                MaximumReconnectAttempts:mode=="recovery"?1:0,Events:new(FixtureServer.Node("fixture"),mode=="overflow"?(ushort)1000:(ushort)0)),default);
            await Wait(async()=>Json(await captures.Status(capture,default)).GetProperty("recordedProgress").GetProperty("rows").GetInt64()>0);
            if(mode=="recovery")server.DropPublishResponses=1;
            int count=mode=="overflow"?100:several?3:1;
            for(int i=0;i<count;i++)server.EmitEvent(mode=="repeatedId"?10:10+i,mode=="overflow"?(ushort)1000:(ushort)700);
            await Wait(async()=>Json(await captures.Status(capture,default)).GetProperty("state").GetString()!="running");
            var result=Json(await captures.Status(capture,default));
            var actual=ReadRows(captures,result); var notifications=actual.SelectMany(r=>r.Event.Events??[]).ToArray();
            File.WriteAllText(Path.Combine(root,mode+".json"),Json(new{result,rows=actual}).GetRawText());
            if(mode.StartsWith("eventBad",StringComparison.Ordinal))check(notifications.Length==0 && actual.Any(r=>r.Event.Kind=="failure"),"Malformed event refuses without inventing fields: "+mode);
            if(mode=="eventNullMessage")check(notifications.Single().Fields[6] is {Kind:"value",Text:null,Locale:"de-DE"},"Localized message preserves null text separately from null field and empty string");
            if(mode=="eventUnavailable")check(notifications.Single().Fields[6].StatusCode=="0x801f0000" && notifications.Single().Fields[4].Kind=="null","Unavailable message and missing Time retained without invented data");
            if(mode=="subscriptionCreateItemsDenied")check(notifications.Length==0 && actual.Any(r=>r.Event.Kind=="failure") && result.GetProperty("recordedProgress").GetProperty("remoteCleanupConfirmed").GetBoolean(),"Denied event monitoring cleans original subscription");
            if(mode=="subscriptionDuplicate")check(actual.Any(r=>r.Event.Duplicate) && actual.Any(r=>r.Event.Kind=="republish"),"Duplicate event envelope remains visible with original retransmission");
            if(mode=="subscriptionGap")check(actual.Any(r=>r.Event.Kind=="gap") && actual.Any(r=>r.Event.Kind=="republish" && r.Event.Events?.Length==1),"Original missing event retransmitted through Republish");
            if(mode=="overflow")check(notifications.Any(n=>n.Overflow) && notifications.Length<100,"Actual bounded server event queue retains overflow rather than claiming complete delivery");
            if(mode=="recovery")check(actual.Any(r=>r.Event.Reconnect?.State=="succeeded") && actual.Any(r=>r.Event.Kind=="republish" && r.Event.Events?.Length==1),"Lost event Publish recovered on same session with original event Republish");
            if(mode=="repeatedId")
            {
                check(notifications.Length==3 && notifications.Select(n=>n.Fields[0].Text).Distinct().Count()==1,"Repeated EventIds retained as three original notifications");
                var export=Json(await captures.Export(result.GetProperty("snapshotRef").GetString()!,"html",default));
                check(File.ReadAllText(export.GetProperty("artifacts")[1].GetProperty("path").GetString()!).Contains("repeated EventId"),"Timeline labels repeated EventIds without dropping original records");
            }
            server.Mode="normal";server.DropPublishResponses=0;await sessions.Disconnect(attempt);
        }
        // Admission and capability failures must not manufacture a remote event subscription.
        attempt=Id();await sessions.Connect(attempt,server.Selection);
        int audit=server.Audit.Count;bool refused=false;
        try{await captures.Start(Id(),new(attempt,[],Events:new(FixtureServer.Node("fixture"),1001)),default);}catch(BridgeException){refused=true;}
        check(refused && server.Audit.Count==audit,"Invalid severity rejected before remote services");
        capture=Id();await captures.Start(capture,new(attempt,[],Events:new(FixtureServer.Node("int64"))),default);
        await Wait(async()=>Json(await captures.Status(capture,default)).GetProperty("state").GetString()!="running");
        var unsupported=ReadRows(captures,Json(await captures.Status(capture,default)));
        check(unsupported.Any(r=>r.Event.ErrorCode=="UaEventNotifierUnavailable") && !unsupported.Any(r=>r.Event.Kind=="created"),"Non-event source refused before subscription creation");
        await sessions.Disconnect(attempt);
    }
    private static JsonNode FirstEvent(JsonObject[] segments)=>segments.SelectMany(s=>s["rows"]!.AsArray()).Select(r=>r!["event"]!).First(e=>e["events"] is JsonArray a && a.Count>0)["events"]![0]!;
    private static OpcUaSubscriptionRow[] ReadRows(OpcUaRuntimeSubscriptions captures,JsonElement status)=>
        Json(captures.Page(status.GetProperty("snapshotRef").GetString()!,0,200)).GetProperty("rows").EnumerateArray().Select(x=>x.Deserialize<OpcUaSubscriptionRow>(BridgeProtocol.Json)!).ToArray();
    private static async Task Wait(Func<Task<bool>> predicate)
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(!await predicate())await Task.Delay(20,limit.Token);
    }
}
