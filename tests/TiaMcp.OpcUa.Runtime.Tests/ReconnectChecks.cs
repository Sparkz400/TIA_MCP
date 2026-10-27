using System.Text.Json;
using System.Text.Json.Nodes;
using Opc.Ua;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal static class ReconnectChecks
{
    private static string Id()=>Guid.NewGuid().ToString("N");
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,BridgeProtocol.Json);
    internal static async Task Run(FixtureServer server,string output,Action<bool,string> check)
    {
        int Count(string operation)=>operation=="FixtureActivateSession"?server.ActivationRequests:server.Audit.Select(Json).Count(e=>e.GetProperty("operation").GetString()==operation);
        await using(var relay=new LoopbackRelay(new Uri(server.Endpoint).Port))
        {
            server.AdvertisedEndpoint=relay.Endpoint;
            string root=Path.Combine(output,"reconnect-tcp"),attempt=Id(),capture=Id();
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
            await using var polling=new OpcUaRuntimeCaptures(sessions,root);
            try
            {
                check((await sessions.Connect(attempt,server.Selection)).State=="connected","Real opaque TCP relay connects with exact endpoint and certificate");
                var boundary=server.HoldNextValueRead();
                await polling.Start(capture,new(attempt,[FixtureServer.Node("bool")],100,15,2,MaximumReconnectAttempts:2),default);
                try {await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));relay.DropActive();}
                finally {boundary.Release.TrySetResult();}
                await Wait(async()=>Json(await polling.Status(capture,default)).GetProperty("state").GetString()!="running");
                var status=Json(await polling.Status(capture,default));
                var rows=Json(polling.Page(status.GetProperty("snapshotRef").GetString()!,0,200)).GetProperty("rows").EnumerateArray().ToArray();
                File.WriteAllText(Path.Combine(root,"observed.json"),JsonSerializer.Serialize(new{status,rows,dropped=relay.Dropped},BridgeProtocol.Json));
                check(relay.Dropped>0,"Actual owned TCP connection was dropped during the original read");
                check(rows.Any(r=>r.TryGetProperty("reconnect",out var e) && e.GetProperty("state").GetString()=="succeeded"),"Actual TCP loss reactivates original session");
                check(rows.Any(r=>r.GetProperty("readResultJson").ValueKind==JsonValueKind.String),"Values continue after actual TCP loss");
                check((await sessions.Disconnect(attempt)).State=="closed","Reactivated TCP session cleanup confirmed");
            }
            finally {server.AdvertisedEndpoint=null;}
        }
        {
            string root=Path.Combine(output,"reconnect-server-restart"),attempt=Id(),capture=Id();
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
            await using var polling=new OpcUaRuntimeCaptures(sessions,root);
            await sessions.Connect(attempt,server.Selection);var selection=server.Selection;
            await server.RestartFixture();int created=Count("CreateSession");
            check(server.Selection==selection,"Real server restart retains its original certificate and endpoint");
            await polling.Start(capture,new(attempt,[FixtureServer.Node("bool")],100,12,MaximumReconnectAttempts:2),default);
            await Wait(async()=>Json(await polling.Status(capture,default)).GetProperty("state").GetString()!="running");
            var status=Json(await polling.Status(capture,default));
            var rows=Json(polling.Page(status.GetProperty("snapshotRef").GetString()!,0,200)).GetProperty("rows").EnumerateArray().ToArray();
            check(rows.All(r=>r.GetProperty("readResultJson").ValueKind==JsonValueKind.Null) && Count("CreateSession")==created,"Server restart cannot silently replace the original session");
            check((await sessions.Status(attempt)).State=="remoteSessionUnknown","Restart leaves lost session ownership unresolved");
        }
        foreach(string mode in new[]{"poll","disabled","exhausted","changedNamespace","changedStart","expiredSession","denied","stop"})
        {
            string root=Path.Combine(output,"reconnect-"+mode),attempt=Id(),capture=Id();
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
            await using var polling=new OpcUaRuntimeCaptures(sessions,root);
            check((await sessions.Connect(attempt,server.Selection)).State=="connected","Reconnect fixture connects: "+mode);
            int activates=Count("FixtureActivateSession"),creates=Count("CreateSession");
            server.FailValueReads=1;
            if(mode=="exhausted")server.ReactivationFailure=StatusCodes.BadTimeout;
            if(mode=="expiredSession")server.ReactivationFailure=StatusCodes.BadSessionIdInvalid;
            if(mode=="denied")server.ReactivationFailure=StatusCodes.BadUserAccessDenied;
            if(mode is "changedNamespace" or "changedStart")server.ModeAfterReactivation=mode;
            var request=new OpcUaCaptureRequest(attempt,[FixtureServer.Node("int64")],100,8,3,MaximumReconnectAttempts:mode=="disabled"?0:2);
            try
            {
                await polling.Start(capture,request,default);
                if(mode=="stop")
                {
                    await Wait(async()=>Json(await polling.Status(capture,default)).GetProperty("recordedProgress").GetProperty("rows").GetInt64()>=2);
                    await polling.Stop(capture,default);
                }
                await Wait(async()=>Json(await polling.Status(capture,default)).GetProperty("state").GetString()!="running");
                var status=Json(await polling.Status(capture,default));string snapshot=status.GetProperty("snapshotRef").GetString()!;
                var rows=Json(polling.Page(snapshot,0,200)).GetProperty("rows").EnumerateArray().ToArray();
                File.WriteAllText(Path.Combine(root,"observed.json"),JsonSerializer.Serialize(new{status,rows},BridgeProtocol.Json));
                var recovery=rows.Where(r=>r.TryGetProperty("reconnect",out _)).Select(r=>r.GetProperty("reconnect")).ToArray();
                check(rows.Count(r=>r.GetProperty("errorCode").ValueKind==JsonValueKind.String)==1,"Original failed read retained: "+mode);
                check(Count("CreateSession")==creates,"No replacement session created: "+mode);
                if(mode=="poll")
                {
                    check(recovery.Length==2 && recovery[1].GetProperty("state").GetString()=="succeeded","Polling reactivated same session");
                    check(rows.Count(r=>r.GetProperty("readResultJson").ValueKind==JsonValueKind.String)==2,"Polling continues without replacing failed read");
                    check(Count("FixtureActivateSession")==activates+1 && server.SessionCount==1,"Exactly one reactivation and one remote session");
                    check(status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()>0,"Polling downtime has explicit skipped slots");
                    await polling.Export(snapshot,"csv",default);
                    var plan=status.GetProperty("plan").Deserialize<RuntimeCapturePlan>(BridgeProtocol.Json)!;
                    var typed=rows.Select(r=>r.Deserialize<RuntimeCaptureRow>(BridgeProtocol.Json)!).ToArray();
                    int success=Array.FindIndex(typed,r=>r.Reconnect?.State=="succeeded");
                    foreach(var forged in new[]{new OpcUaReconnectEvent(2,"succeeded",null),new(1,"failed","UaCertificateRejected")})
                    {
                        var altered=typed.ToArray();altered[success]=altered[success] with {Reconnect=forged};bool refused=false;
                        try {OpcUaCaptureContracts.ValidateReconnectRows(plan,altered);}catch(BridgeException){refused=true;}
                        check(refused,"Retained polling cannot invent recovery or read after failed recovery");
                    }
                }
                else
                {
                    check(rows.All(r=>r.GetProperty("readResultJson").ValueKind==JsonValueKind.Null),"No unsafe data after recovery refusal: "+mode);
                    check(recovery.Count(r=>r.GetProperty("state").GetString()=="starting")== (mode=="disabled"?0:mode=="exhausted"?2:1),"Bounded recovery attempts: "+mode);
                    check((await sessions.Status(attempt)).State=="remoteSessionUnknown","Remote uncertainty remains: "+mode);
                }
                int services=server.Audit.Count;
                await polling.Start(capture,request,default);await polling.Stop(capture,default);
                check(server.Audit.Count==services,"Repeated terminal start/stop remains local: "+mode);
            }
            finally {server.FailValueReads=0;server.ReactivationFailure=0;server.ModeAfterReactivation=null;server.Mode="normal";await sessions.Disconnect(attempt);}
        }
        foreach(bool enabled in new[]{false,true})
        {
            string root=Path.Combine(output,"reconnect-sub-"+enabled),attempt=Id(),capture=Id();
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
            await using var subscriptions=new OpcUaRuntimeSubscriptions(sessions,root);
            await sessions.Connect(attempt,server.Selection);server.DropPublishResponses=1;
            int created=Count("SubscriptionCreated"),activated=Count("FixtureActivateSession");
            try
            {
                await subscriptions.Start(capture,new(attempt,[FixtureServer.Node("int64"),FixtureServer.Node("bool")],DurationSeconds:5,MaximumNotifications:2,MaximumReconnectAttempts:enabled?2:0),default);
                await Wait(async()=>Json(await subscriptions.Status(capture,default)).GetProperty("state").GetString()!="running");
                var status=Json(await subscriptions.Status(capture,default));string snapshot=status.GetProperty("snapshotRef").GetString()!;
                var rows=Json(subscriptions.Page(snapshot,0,200)).GetProperty("rows").EnumerateArray().ToArray();
                File.WriteAllText(Path.Combine(root,"observed.json"),JsonSerializer.Serialize(new{status,rows},BridgeProtocol.Json));
                check(Count("SubscriptionCreated")==created+1,"Recovery never recreates subscription: "+enabled);
                check(Count("FixtureActivateSession")==activated+(enabled?1:0),"Subscription opt-in reactivation: "+enabled);
                check(status.GetProperty("recordedProgress").GetProperty("notifications").GetInt64()==(enabled?2:0),"Lost Publish recovered exactly once: "+enabled);
                if(enabled)
                {
                    check(rows.Any(r=>r.GetProperty("event").GetProperty("kind").GetString()=="republish"),"Original lost response retained as Republish");
                    check(rows.Count(r=>r.GetProperty("event").GetProperty("kind").GetString()=="reconnect")==2,"Subscription downtime boundaries retained");
                    await subscriptions.Export(snapshot,"csv",default);
                    static IEnumerable<JsonNode> Events(JsonObject[] segments)=>segments.SelectMany(s=>s["rows"]!.AsArray()).Select(r=>r!["event"]!);
                    await SubscriptionChecks.RetainedControls(root,sessions,capture,status,check,[
                        ("invented reconnect attempt",s=>Events(s).First(e=>e["reconnect"] is not null)["reconnect"]!["attempt"]=2),
                        ("data after failed reconnect",s=>{var e=Events(s).First(e=>e["reconnect"]?["state"]?.GetValue<string>()=="succeeded")["reconnect"]!;e["state"]="failed";e["errorCode"]="UaTimeout";}),
                        ("publish before recovery drain",s=>{var e=Events(s).First(e=>e["kind"]!.GetValue<string>()=="republish");e["kind"]="publish";e["requestedSequenceNumber"]=null;e["availableSequenceNumbers"]=new JsonArray();e["acknowledgedSequenceNumbers"]=new JsonArray();e["acknowledgementResults"]=new JsonArray();})
                    ]);
                }
            }
            finally {server.DropPublishResponses=0;await sessions.Disconnect(attempt);}
        }
        {
            string root=Path.Combine(output,"reconnect-retired-credential"),attempt=Id(),capture=Id();bool authorized=true;
            var selected=server.Selection with {Authentication="username",User="reader",VaultName="default"};
            selected=selected with {Credential=new("protected-credential-reference.v1",new('a',32),new('b',32),ProtectedCredentialGuard.BindingHash(OpcUaRuntimeSessionStore.Binding(selected)),new('c',64))};
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"),(_,consume)=>{
                if(!authorized)throw new BridgeException("CredentialRetired","Fixture credential was retired.");
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes("fixture-only-secret");
                try {consume(bytes);}finally {System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);}
            });
            await using var polling=new OpcUaRuntimeCaptures(sessions,root);
            await sessions.Connect(attempt,selected);int activated=server.ActivationRequests;server.FailValueReads=1;
            await polling.Start(capture,new(attempt,[FixtureServer.Node("bool")],100,8,MaximumReconnectAttempts:2),default);
            await Wait(async()=>Json(await polling.Status(capture,default)).GetProperty("recordedProgress").GetProperty("rows").GetInt64()>=2);
            authorized=false;
            await Wait(async()=>Json(await polling.Status(capture,default)).GetProperty("state").GetString()!="running");
            check(server.ActivationRequests==activated && (await sessions.Status(attempt)).ErrorCode=="UaCredentialUnavailable","Retired credential blocks reactivation before network");
            await sessions.Disconnect(attempt);
        }
        foreach(int maximum in new[]{-1,4})
        {
            bool refused=false;
            try {OpcUaCaptureContracts.StorageRequest(new(Id(),[FixtureServer.Node("bool")],100,1,MaximumReconnectAttempts:maximum));}catch(BridgeException){refused=true;}
            check(refused,"Out-of-bound polling retry count refused");
        }
        {
            string root=Path.Combine(output,"reconnect-republish-budget"),attempt=Id(),capture=Id();
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
            await using var subscriptions=new OpcUaRuntimeSubscriptions(sessions,root);
            await sessions.Connect(attempt,server.Selection);server.DropPublishResponses=1;server.Mode="subscriptionGapAfterRecovery";
            try
            {
                await subscriptions.Start(capture,new(attempt,[FixtureServer.Node("int64"),FixtureServer.Node("bool"),FixtureServer.Node("text")],DurationSeconds:8,MaximumRepublish:2,MaximumReconnectAttempts:2),default);
                await Wait(async()=>Json(await subscriptions.Status(capture,default)).GetProperty("state").GetString()!="running");
                var status=Json(await subscriptions.Status(capture,default));
                check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sequenceGapLimit","Unavailable recovery probe consumes budget without inventing lost messages");
                check(status.GetProperty("recordedProgress").GetProperty("missingSequences").GetInt64()==2,"Only actually detected missing sequences are counted across recovery");
            }
            finally {server.Mode="normal";server.DropPublishResponses=0;await sessions.Disconnect(attempt);}
        }
        // Session loss reported by the server: one replacement session, TransferSubscriptions, and on refusal a new subscription.
        foreach(string mode in new[]{"transferred","transferRefused"})
        {
            string root=Path.Combine(output,"reconnect-"+mode),attempt=Id(),capture=Id();
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(root,"sessions"));
            await using var subscriptions=new OpcUaRuntimeSubscriptions(sessions,root);
            check((await sessions.Connect(attempt,server.Selection)).State=="connected","Transfer fixture connects: "+mode);
            int creates=Count("CreateSession"),subscribed=Count("SubscriptionCreated"),transfers=Count("TransferRequested");
            server.DropPublishResponses=1;server.ReactivationFailure=StatusCodes.BadSessionIdInvalid;server.ReactivationFailureOnce=true;
            if(mode=="transferRefused")server.Mode="transferRefused";
            try
            {
                await subscriptions.Start(capture,new(attempt,[FixtureServer.Node("int64"),FixtureServer.Node("bool")],DurationSeconds:5,MaximumNotifications:2,MaximumReconnectAttempts:2),default);
                await Wait(async()=>Json(await subscriptions.Status(capture,default)).GetProperty("state").GetString()!="running");
                var status=Json(await subscriptions.Status(capture,default));string snapshot=status.GetProperty("snapshotRef").GetString()!;
                var rows=Json(subscriptions.Page(snapshot,0,200)).GetProperty("rows").EnumerateArray().Select(r=>r.GetProperty("event")).ToArray();
                File.WriteAllText(Path.Combine(root,"observed.json"),JsonSerializer.Serialize(new{status,rows},BridgeProtocol.Json));
                string Kind(JsonElement e)=>e.GetProperty("kind").GetString()!;
                var succeeded=rows.Where(e=>Kind(e)=="reconnect" && e.GetProperty("reconnect").GetProperty("state").GetString()=="succeeded").Select(e=>e.GetProperty("reconnect")).ToArray();
                check(succeeded.Length==1 && Count("CreateSession")==creates+1 && Count("TransferRequested")==transfers+1,"Lost session opens one replacement session and one transfer: "+mode);
                check(status.GetProperty("transferPolicy").GetString()=="transferThenRecreateAfterSessionLoss","Status reports opt-in transfer policy: "+mode);
                check(status.GetProperty("recordedProgress").GetProperty("notifications").GetInt64()==2,"Notifications continue after session loss: "+mode);
                var created=rows.Where(e=>Kind(e)=="created").ToArray();
                if(mode=="transferred")
                {
                    check(succeeded[0].GetProperty("transfer").GetString()=="transferred" && !succeeded[0].TryGetProperty("transferStatusCode",out _),"Transfer success reported");
                    check(Count("SubscriptionCreated")==subscribed+1 && created.Length==1,"Transferred subscription keeps its original identity");
                    check(rows.Any(e=>Kind(e)=="republish"),"Lost response recovered by Republish on the replacement session");
                }
                else
                {
                    check(succeeded[0].GetProperty("transfer").GetString()=="refused" && succeeded[0].GetProperty("transferStatusCode").GetString()=="0x800b0000","Server refusal reported with its status code");
                    check(Count("SubscriptionCreated")==subscribed+2 && created.Length==2 &&
                        created[0].GetProperty("subscriptionId").GetUInt32()!=created[1].GetProperty("subscriptionId").GetUInt32(),"Refused transfer falls back to a new subscription");
                    check(!rows.Any(e=>Kind(e) is "republish" or "gap"),"New subscription never republishes or counts the old sequence");
                }
                // Reopening retained evidence reruns the independent timeline validation; forged transfer evidence refuses.
                await subscriptions.Export(snapshot,"csv",default);
                static JsonNode Transfer(JsonObject[] s)=>s.SelectMany(x=>x["rows"]!.AsArray()).Select(r=>r!["event"]!).First(e=>e["reconnect"]?["transfer"] is not null)["reconnect"]!;
                await SubscriptionChecks.RetainedControls(root,sessions,capture,status,check,mode=="transferred" ? [
                    ("transfer claimed refused without recreate",s=>{var r=Transfer(s);r["transfer"]="refused";r["transferStatusCode"]="0x800b0000";}),
                    ("transfer status on success",s=>Transfer(s)["transferStatusCode"]="0x800b0000")
                ] : [
                    ("recreate without refused transfer",s=>{var r=Transfer(s).AsObject();r["transfer"]="transferred";r.Remove("transferStatusCode");}),
                    ("refusal with good status",s=>Transfer(s)["transferStatusCode"]="0x00000000")
                ]);
            }
            finally {server.Mode="normal";server.DropPublishResponses=0;server.ReactivationFailure=0;server.ReactivationFailureOnce=false;await sessions.Disconnect(attempt);}
        }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(!await condition())await Task.Delay(20,limit.Token);
    }
}
