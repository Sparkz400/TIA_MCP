using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal static class SubscriptionChecks
{
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,BridgeProtocol.Json);
    private static string Id()=>Guid.NewGuid().ToString("N");
    internal static async Task Run(FixtureServer server,string output,Action<bool,string> check)
    {
        string root=Path.Combine(output,"subscription-checks");
        await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
        var subscriptions=new OpcUaRuntimeSubscriptions(sessions,root);
        await using var polling=new OpcUaRuntimeCaptures(sessions,root);
        string attempt=Id();check((await sessions.Connect(attempt,server.Selection)).State=="connected","Subscription owned encrypted session");
        try
        {
            string[] names=["int64","uint64","double","bool","text","bad","uncertain","missingTimes","array","denied","structure","byteArray","byteString","minimumDate","absent"];
            var request=new OpcUaSubscriptionRequest(attempt,names.Select(FixtureServer.Node).ToArray(),DurationSeconds:10,MaximumNotifications:14);
            string capture=Id();await subscriptions.Start(capture,request,default);
            var status=await End(subscriptions,capture);var events=Events(subscriptions,status);
            File.WriteAllText(Path.Combine(root,"healthy-status.json"),status.GetRawText());
            File.WriteAllText(Path.Combine(root,"healthy-events.json"),Json(events).GetRawText());
            var created=events.Single(x=>x.Kind=="created").Created!;
            check(created.RevisedPublishingInterval==200 && created.RevisedLifetimeCount>=60 && created.RevisedKeepAliveCount==10,"Original revised subscription parameters");
            check(created.Items.All(x=>x.Node.Identifier=="absent" || x.RevisedSamplingInterval==200 && x.RevisedQueueSize==4),"Original revised monitored-item parameters");
            check(created.Items[^1].StatusCode=="0x80340000" && created.Items[^1].MonitoredItemId==0,"Missing monitored item explicit partial failure");
            var values=events.SelectMany(x=>x.Notifications??[]).ToDictionary(x=>x.Value.Node.Identifier,x=>x.Value);
            check(values.Count==14 && !values.ContainsKey("absent"),"Successful neighboring monitored items survive missing-node failure");
            check((string?)((JsonElement)values["int64"].Value!).GetString()=="-9223372036854775808" && ((JsonElement)values["uint64"].Value!).GetString()=="18446744073709551615","Subscription exact integer extrema");
            check(values["bad"].Outcome=="itemError" && values["bad"].Value is null,"Subscription suppresses bad values");
            check(values["uncertain"].Quality=="Uncertain" && values["uncertain"].StatusCode=="0x40920000","Subscription preserves uncertain raw status");
            check(values["int64"].SourceTimestamp==new DateTimeOffset(FixtureServer.SourceTime) && values["int64"].ServerTimestamp==new DateTimeOffset(FixtureServer.ServerTime) && values["int64"].SourcePicoseconds==12 && values["int64"].ServerPicoseconds==34,"Subscription preserves original timestamps and picoseconds");
            check(values["missingTimes"].SourceTimestamp is null && values["missingTimes"].ServerTimestamp is null,"Subscription absent clocks remain null");
            check(values["array"].Encoding=="array" && values["byteArray"].Encoding=="array" && values["byteString"].Outcome=="value","Subscription arrays and ByteString distinction");
            check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="notificationLimit" && status.GetProperty("recordedProgress").GetProperty("remoteCleanupConfirmed").GetBoolean(),"Notification bound and confirmed remote deletion retained");
            var actual=events.Where(x=>x.Kind=="publish").ToArray();
            check(actual.Select(x=>x.SequenceNumber).SequenceEqual(Enumerable.Range(1,14).Select(x=>(uint?)x)),"Original actual Publish sequence numbers");
            check(actual.Skip(1).All(x=>x.AcknowledgedSequenceNumbers!.Length==1 && x.AcknowledgementResults!.SequenceEqual(["0x00000000"])),"Original acknowledgement results retained after commit");
            var export=Json(await subscriptions.Export(status.GetProperty("snapshotRef").GetString()!,"csv",default));
            foreach(var artifact in export.GetProperty("artifacts").EnumerateArray())check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(artifact.GetProperty("path").GetString()!)))==artifact.GetProperty("sha256").GetString(),"Subscription exact export artifact hash");
            var exported=JsonDocument.Parse(File.ReadAllBytes(export.GetProperty("artifacts")[0].GetProperty("path").GetString()!)).RootElement;
            check(JsonElement.DeepEquals(exported.GetProperty("rows"),Json(subscriptions.Page(status.GetProperty("snapshotRef").GetString()!,0,200)).GetProperty("rows")),"Subscription page and exact JSON export agree");
            int audit=server.Audit.Count;await subscriptions.Start(capture,request,default);await subscriptions.Stop(capture,default);
            check(server.Audit.Count==audit,"Repeated subscription start and stop do not replay");
            await Refuse(()=>subscriptions.Start(capture,request with {DurationSeconds=11},default),"UaSubscriptionRequestChanged",check);
            await Refuse(()=>subscriptions.Start(Id(),request with {Nodes=[request.Nodes[0],request.Nodes[0]]},default),"UaSubscriptionInvalid",check);
            await Refuse(()=>subscriptions.Start(Id(),request with {MaximumRepublish=65},default),"UaSubscriptionInvalid",check);
            check(server.Audit.Count==audit,"Invalid subscription admission sends no services");
            string active=Id();await subscriptions.Start(active,new(attempt,[FixtureServer.Node("bool")],DurationSeconds:30,KeepAliveCount:1),default);
            await Refuse(()=>sessions.Read(attempt,[FixtureServer.Node("bool")]),"UaSessionAcquisitionBusy",check);
            await Refuse(()=>sessions.Browse(attempt,FixtureServer.Node("fixture")),"UaSessionAcquisitionBusy",check);
            await Refuse(()=>sessions.Disconnect(attempt),"UaSessionAcquisitionBusy",check);
            await Refuse(()=>polling.Start(Id(),new(attempt,[FixtureServer.Node("bool")],100,1),default),"UaSessionAcquisitionBusy",check);
            await Refuse(()=>subscriptions.Start(Id(),request,default),"UaSessionAcquisitionBusy",check);
            await Task.Delay(600);var stopped=Json(await subscriptions.Stop(active,default));
            check(stopped.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="stopped" && stopped.GetProperty("recordedProgress").GetProperty("remoteCleanupConfirmed").GetBoolean(),"Stop waits for bounded Publish and deletes exact subscription");
            check(Events(subscriptions,stopped).Any(x=>x.KeepAlive),"Keepalive retained separately without sample or duplicate");
            check((await sessions.Status(attempt)).State=="connected","Clean subscription stop leaves original session usable");
            audit=server.Audit.Count;await Task.Delay(250);check(server.Audit.Count==audit,"No background Publish after stop");
            foreach(string mode in new[]{"subscriptionGap","subscriptionDuplicate","subscriptionOverflow","subscriptionRepublishUnavailable","subscriptionRepublishStatus","subscriptionStatus","subscriptionBadHandle","subscriptionOversize","subscriptionCreateItemsDenied","subscriptionDeleteDenied"})
            {
                server.Mode=mode;string id=Id();
                await subscriptions.Start(id,new(attempt,[FixtureServer.Node("int64"),FixtureServer.Node("bool"),FixtureServer.Node("uncertain")],DurationSeconds:2,KeepAliveCount:1,MaximumNotifications:3),default);
                var outcome=await End(subscriptions,id);var rows=Events(subscriptions,outcome);
                File.WriteAllText(Path.Combine(root,mode+".json"),Json(new{outcome,events=rows}).GetRawText());
                if(mode=="subscriptionGap")check(rows.Any(x=>x.Kind=="gap" && x.GapFrom==1 && x.GapTo==1 && x.MissingCount==1) && rows.Any(x=>x.Kind=="republish" && x.SequenceNumber==1),"Real Republish repairs injected missing original sequence");
                if(mode=="subscriptionDuplicate")check(rows.Count(x=>x.Duplicate)==1 && rows.Any(x=>x.Kind=="republish" && x.SequenceNumber==2) && outcome.GetProperty("recordedProgress").GetProperty("notifications").GetInt32()==3,"Duplicate retains original envelope without double counting; subsequent gap repaired");
                if(mode=="subscriptionOverflow")check(rows.SelectMany(x=>x.Notifications??[]).All(x=>x.Overflow && (uint.Parse(x.Value.StatusCode[2..],System.Globalization.NumberStyles.HexNumber)&0x480)==0x480),"Original DataValue overflow information bits retained");
                if(mode=="subscriptionRepublishUnavailable")check(rows.Any(x=>x.Kind=="failure" && x.ErrorCode=="UaRepublishUnavailable" && x.StatusCode=="0x807b0000"),"Unavailable retransmission remains explicit gap evidence");
                if(mode=="subscriptionRepublishStatus")check(rows.Any(x=>x.Kind=="republish" && x.StatusChanges?.Contains("0x800a0000")==true) && outcome.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="serverStatusChanged","Retransmitted StatusChangeNotification also terminates acquisition");
                if(mode=="subscriptionStatus")check(rows.Any(x=>x.StatusChanges?.Contains("0x800a0000")==true) && outcome.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="serverStatusChanged","Server StatusChangeNotification retained and terminates acquisition");
                if(mode=="subscriptionBadHandle")check(rows.Any(x=>x.ErrorCode=="UaResponseInvalid") && outcome.GetProperty("recordedProgress").GetProperty("notifications").GetInt32()==0,"Unselected server client handle refuses fabricated notification association");
                if(mode=="subscriptionOversize")check(rows.Any(x=>x.ErrorCode=="UaResponseLimit") && outcome.GetProperty("recordedProgress").GetProperty("notifications").GetInt32()==0,"Hostile two-notification response refuses without splitting exact bounded envelope");
                if(mode=="subscriptionCreateItemsDenied")check(!rows.Any(x=>x.Kind=="created") && rows.Any(x=>x.Kind=="failure") && rows.Any(x=>x.Kind=="deleted") && outcome.GetProperty("recordedProgress").GetProperty("remoteCleanupConfirmed").GetBoolean(),"Partial CreateSubscription success then monitored-service failure deletes original remote subscription");
                if(mode=="subscriptionDeleteDenied")check(!outcome.GetProperty("recordedProgress").GetProperty("remoteCleanupConfirmed").GetBoolean() && (await sessions.Status(attempt)).State=="remoteSessionUnknown","Denied deletion retains unknown remote subscription/session state");
                server.Mode="normal";
                if((await sessions.Status(attempt)).State!="connected"){await sessions.Disconnect(attempt);attempt=Id();await sessions.Connect(attempt,server.Selection);}
            }
            foreach(int budget in new[]{0,1})
            {
                server.Mode="subscriptionGap";string id=Id();
                await subscriptions.Start(id,new(attempt,[FixtureServer.Node("int64"),FixtureServer.Node("bool"),FixtureServer.Node("uncertain")],KeepAliveCount:1,DurationSeconds:2,MaximumNotifications:3,MaximumRepublish:budget),default);
                var bound=await End(subscriptions,id);
                check(bound.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()==(budget==0?"sequenceGapLimit":"notificationLimit"),"Exact retransmission allowance "+budget+" distinguishes exhausted pending gap from complete recovery");
            }
            server.Mode="normal";
            await RetainedControls(root,sessions,capture,status,check);
            var next=typeof(OpcUaRuntimeSubscriptions).GetMethod("Next",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            var previous=typeof(OpcUaRuntimeSubscriptions).GetMethod("Previous",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            check((uint)next.Invoke(null,[uint.MaxValue])! == 1 && (uint)next.Invoke(null,[1u])! == 2 && (uint)previous.Invoke(null,[1u])! == uint.MaxValue,"Sequence counter wrap excludes zero with independent boundary constants");
            string empty=Id();await subscriptions.Start(empty,new(attempt,[FixtureServer.Node("absent")]),default);
            check((await End(subscriptions,empty)).GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="allItemsFailed","All monitored-item failures remain terminal with no polling fallback");
            string shutdown=Id();await subscriptions.Start(shutdown,new(attempt,[FixtureServer.Node("bool")],KeepAliveCount:1,DurationSeconds:30),default);await Task.Delay(100);
            await subscriptions.DisposeAsync();subscriptions=new(sessions,root);audit=server.Audit.Count;
            check(Json(await subscriptions.Status(shutdown,default)).GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="hostShutdown","Host shutdown finalizes and deletes original subscription");
            check(Json(await subscriptions.Status(capture,default)).GetProperty("snapshotRef").GetString()==status.GetProperty("snapshotRef").GetString(),"Fresh journal owner preserves exact committed snapshot");
            await subscriptions.Start(capture,request,default);await subscriptions.Stop(capture,default);
            check(server.Audit.Count==audit,"Reopen status/repeated start/stop never create or replay subscription");
            await sessions.Read(attempt,[FixtureServer.Node("bool")]);check(server.Audit.Count>audit,"Independent real Read defeats no-network comparator");
            await sessions.Disconnect(attempt);
        }
        finally{server.Mode="normal";await subscriptions.DisposeAsync();}
    }
    private static async Task RetainedControls(string root,OpcUaRuntimeSessionStore sessions,string capture,JsonElement status,Action<bool,string> check)
    {
        string original=Path.Combine(root,"runtime-ua-subscriptions",capture);
        var originalHashes=Directory.GetFiles(original).ToDictionary(x=>x,x=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(x))));
        var mutations=new (string Name,Action<JsonObject[]> Change)[] {
            ("unselected item id",segments=>First(segments)["notifications"]![0]!["monitoredItemId"]=4294967295u),
            ("messages after terminal status",segments=>First(segments)["statusChanges"]=new JsonArray("0x800a0000")),
            ("oversized status list",segments=>First(segments)["statusChanges"]=new JsonArray(Enumerable.Range(0,5000).Select(_=>(JsonNode?)JsonValue.Create("0x00000000")).ToArray())),
            ("invalid scalar",segments=>First(segments)["notifications"]![0]!["value"]!["value"]="-9223372036854775809"),
            ("invented overflow",segments=>First(segments)["notifications"]![0]!["overflow"]=true),
            ("sequence without gap",segments=>First(segments)["sequenceNumber"]=3),
            ("ack before commit",segments=>{var e=First(segments);e["acknowledgedSequenceNumbers"]=new JsonArray(1);e["acknowledgementResults"]=new JsonArray("0x00000000");}),
            ("impossible all items failed",segments=>segments[^1]["progress"]!["terminalReason"]="allItemsFailed"),
            ("impossible status termination",segments=>segments[^1]["progress"]!["terminalReason"]="serverStatusChanged"),
            ("impossible gap termination",segments=>segments[^1]["progress"]!["terminalReason"]="sequenceGapLimit"),
            ("impossible duration termination",segments=>segments[^1]["progress"]!["terminalReason"]="durationLimit"),
            ("intermediate counter",segments=>segments[0]["progress"]!["notifications"]=1),
            ("created envelope with hidden data",segments=>segments[0]["rows"]![0]!["event"]!["notifications"]=First(segments)["notifications"]!.DeepClone()),
            ("nonmonotonic elapsed",segments=>segments[^1]["rows"]![0]!["elapsedMilliseconds"]=0)
        };
        static JsonObject First(JsonObject[] segments)=>segments.SelectMany(s=>s["rows"]!.AsArray()).Select(r=>r!["event"]!.AsObject()).First(e=>e["kind"]!.GetValue<string>()=="publish");
        foreach(var mutation in mutations)
        {
            string directory=Path.Combine(root,"tamper-"+mutation.Name.Replace(' ','-'));
            string destination=Path.Combine(directory,"runtime-ua-subscriptions",capture);Directory.CreateDirectory(destination);
            foreach(string path in Directory.GetFiles(original))File.Copy(path,Path.Combine(destination,Path.GetFileName(path)));
            var paths=Directory.GetFiles(destination,"*.segment").Order().ToArray();var segments=paths.Select(p=>JsonNode.Parse(File.ReadAllBytes(p))!.AsObject()).ToArray();
            mutation.Change(segments);string hash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination,"plan.json"))));
            for(int i=0;i<paths.Length;i++) {segments[i]["previousSha256"]=hash;byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(segments[i],BridgeProtocol.Json);File.WriteAllBytes(paths[i],bytes);hash=Convert.ToHexStringLower(SHA256.HashData(bytes));}
            File.WriteAllText(Path.Combine(destination,"head.json"),JsonSerializer.Serialize($"ua-subscription:{capture}:{paths.Length}:{hash}"));
            await using var altered=new OpcUaRuntimeSubscriptions(sessions,directory);bool refused=false;
            try {await altered.Status(capture,default);}catch(BridgeException){refused=true;}
            check(refused,"Rehashed retained subscription contradiction refused: "+mutation.Name);
        }
        check(originalHashes.All(x=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(x.Key)))==x.Value),"Every original subscription byte unchanged by rehashed controls");
    }
    private static OpcUaSubscriptionEvent[] Events(OpcUaRuntimeSubscriptions subscriptions,JsonElement status)=>
        Json(subscriptions.Page(status.GetProperty("snapshotRef").GetString()!,0,200)).GetProperty("rows").EnumerateArray().Select(x=>x.GetProperty("event").Deserialize<OpcUaSubscriptionEvent>(BridgeProtocol.Json)!).ToArray();
    private static async Task<JsonElement> End(OpcUaRuntimeSubscriptions subscriptions,string id)
    {var timer=Stopwatch.StartNew();while(timer.Elapsed<TimeSpan.FromSeconds(30)){var s=Json(await subscriptions.Status(id,default));if(s.GetProperty("state").GetString()!="running")return s;await Task.Delay(25);}throw new Exception("Subscription termination deadline");}
    private static async Task Refuse(Func<Task> action,string expected,Action<bool,string> check)
    {string? code=null;try{await action();}catch(BridgeException e){code=e.Code;}check(code==expected,"Subscription refusal "+expected+" (actual "+code+")");}
}
