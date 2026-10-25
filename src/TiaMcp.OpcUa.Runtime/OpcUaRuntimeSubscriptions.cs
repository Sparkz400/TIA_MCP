using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.OpcUa.Runtime;

/// <summary>Explicit bounded Publish/Republish acquisition. Recovery never transfers or recreates subscriptions.</summary>
public sealed class OpcUaRuntimeSubscriptions(OpcUaRuntimeSessionStore sessions,string artifactsRoot) : IAsyncDisposable
{
    private sealed class Running(string attemptId)
    {
        internal readonly string AttemptId=attemptId;
        internal readonly CancellationTokenSource Stop=new();
        internal string Reason="stopped";
        internal string? Failure;
        internal Task Work=Task.CompletedTask;
    }
    private readonly OpcUaSubscriptionStore store=new(artifactsRoot);
    private readonly Dictionary<string,Running> running=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate=new(1);
    private bool disposed;
    public async Task<object> Start(string captureId,OpcUaSubscriptionRequest request,CancellationToken ct)
    {
        OpcUaSubscriptionContracts.Validate(request);request=request with {Nodes=request.Nodes.ToArray()};
        await gate.WaitAsync(ct);
        try
        {
            if(disposed)throw OpcUaRuntimeClient.Error("UaSubscriptionHostStopping");
            if(store.Exists(captureId))
            {
                if(!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(store.Plan(captureId).Request,BridgeProtocol.Json),JsonSerializer.SerializeToElement(request,BridgeProtocol.Json)))
                    throw OpcUaRuntimeClient.Error("UaSubscriptionRequestChanged");
                return Observe(captureId);
            }
            if(running.Values.Count(x=>!x.Work.IsCompleted)>=4)throw OpcUaRuntimeClient.Error("UaSubscriptionCapacity");
            if (request.DiagnosticBindingId is { } bindingId)
                _ = new DiagnosticUaBindings(artifactsRoot).ValidateForCapture(bindingId, request.AttemptId, request.Nodes);
            var session=await sessions.Acquire(request.AttemptId,captureId,ct);
            var plan=new OpcUaSubscriptionPlan(captureId,session.HostId,DateTimeOffset.UtcNow,request,session);
            OpcUaSubscriptionStore.Writer writer;
            try {writer=store.Create(plan);}catch{await sessions.Release(request.AttemptId,captureId);throw;}
            var entry=new Running(request.AttemptId);running.Add(captureId,entry);
            entry.Work=Task.Run(()=>Acquire(plan,writer,entry));return Observe(captureId);
        }
        finally {gate.Release();}
    }
    public async Task<object> Status(string captureId,CancellationToken ct)
    {await gate.WaitAsync(ct);try{return Observe(captureId);}finally{gate.Release();}}
    public async Task<object> Stop(string captureId,CancellationToken ct)
    {
        Task? work=null;await gate.WaitAsync(ct);
        try {if(running.TryGetValue(captureId,out var entry)){entry.Stop.Cancel();work=entry.Work;}}finally{gate.Release();}
        if(work is not null)await work;return await Status(captureId,ct);
    }
    public object Page(string snapshotRef,int offset,int count)=>store.Page(snapshotRef,offset,count);
    public System.Text.Json.Nodes.JsonObject DiagnosticSource(string snapshotRef)
    {
        var snapshot = store.ReadSnapshot(snapshotRef);
        return DiagnosticUaCaptureSource.Retain(artifactsRoot, "subscription", snapshotRef, snapshot.Plan, snapshot.Progress, snapshot.Rows);
    }
    public async Task<object> Export(string snapshotRef,string format,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try{var id=snapshotRef.Split(':').ElementAtOrDefault(1);return store.Export(snapshotRef,format,id is not null && running.TryGetValue(id,out var entry)&&!entry.Work.IsCompleted);}
        finally{gate.Release();}
    }
    private object Observe(string id)=>store.Status(id,running.TryGetValue(id,out var entry)&&!entry.Work.IsCompleted,entry?.Failure);
    private async Task Acquire(OpcUaSubscriptionPlan plan,OpcUaSubscriptionStore.Writer writer,Running entry)
    {
        long started=System.Diagnostics.Stopwatch.GetTimestamp();string reason="durationLimit";uint subscription=0;
        double Elapsed()=>System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        bool Expired()=>entry.Stop.IsCancellationRequested || Elapsed()>=plan.Request.DurationSeconds*1000d;
        void Append(OpcUaSubscriptionEvent e)=>writer.Append(new(writer.Rows,Elapsed(),DateTimeOffset.UtcNow,e));
        var seen=new HashSet<uint>();var missingRanges=new List<(uint From,long Count)>(); var acknowledgements=new HashSet<uint>(); uint expected=1;int republished=0;
        int reconnects=0;bool recovering=false;
        using var duration=new CancellationTokenSource(TimeSpan.FromSeconds(plan.Request.DurationSeconds));
        using var recoveryStop=CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token,duration.Token);
        void Recovery(OpcUaReconnectEvent e) {Append(new("reconnect",subscription,Reconnect:e));writer.Flush(Elapsed());}
        async Task<bool> Recover(string error)
        {
            while(!Expired() && OpcUaReconnectHistory.Retryable(error) && reconnects<plan.Request.MaximumReconnectAttempts)
            {
                int attempt=++reconnects;Recovery(new(attempt,"starting",error));string? failure=null;
                try {await Task.Delay(500,recoveryStop.Token);await sessions.ReconnectAcquisition(plan.Request.AttemptId,plan.CaptureId,recoveryStop.Token);}
                catch(Exception e) {failure=e is BridgeException b && b.Code.StartsWith("Ua",StringComparison.Ordinal)?b.Code:recoveryStop.IsCancellationRequested?"UaCancelled":"UaReconnectFailed";}
                Recovery(new(attempt,failure is null?"succeeded":"failed",failure));
                if(failure is null){recovering=true;return true;}
                error=failure;
            }
            return false;
        }
        try
        {
            // Stop is observed between bounded service calls; cancellation does not silently destroy
            // ownership of an in-flight Publish. A service still has the session's ten-second deadline.
            if(Expired())return;
            var created=await sessions.CreateSubscription(plan.CaptureId,plan.Request,CancellationToken.None);subscription=created.SubscriptionId;
            Append(new("created",subscription,Created:created));
            if(created.Items.All(x=>(OpcUaSubscriptionContracts.Status(x.StatusCode)&0x80000000)!=0)){reason="allItemsFailed";return;}
            while(!Expired())
            {
                try
                {
                if(recovering)
                {
                    // Drain potentially lost responses before issuing another Publish; each envelope stays original.
                    if(republished>=plan.Request.MaximumRepublish)throw OpcUaRuntimeClient.Error("UaReconnectRepublishLimit");
                    var recovered=await sessions.Republish(plan.Request.AttemptId,plan.CaptureId,expected,CancellationToken.None);republished++;
                    if(recovered.Kind=="republish")
                    {
                        missingRanges.Add((expected,1));Append(new("gap",subscription,GapFrom:expected,GapTo:expected,MissingCount:1));
                        Append(recovered with {Duplicate=!seen.Add(expected)});acknowledgements.Add(expected);expected=Next(expected);
                        if(recovered.StatusChanges is {Length:>0}){reason="serverStatusChanged";break;}
                        if(writer.Notifications>=plan.Request.MaximumNotifications){reason="notificationLimit";break;}
                    }
                    else {Append(recovered);recovering=false;}
                    writer.Flush(Elapsed());continue;
                }
                var message=await sessions.Publish(plan.Request.AttemptId,plan.CaptureId,acknowledgements.ToArray(),CancellationToken.None);
                acknowledgements.Clear();uint sequence=message.SequenceNumber!.Value;
                ulong gap=((ulong)sequence+uint.MaxValue-expected)%uint.MaxValue;
                bool ahead=gap<0x80000000UL;
                if(!ahead && (message.KeepAlive || !seen.Contains(sequence) && !missingRanges.Any(g=>((ulong)sequence+uint.MaxValue-g.From)%uint.MaxValue<(ulong)g.Count)))
                    throw OpcUaRuntimeClient.Error("UaSubscriptionSequenceInvalid");
                bool duplicate=!message.KeepAlive && !seen.Add(sequence);
                if(ahead && gap>0){missingRanges.Add((expected,(long)gap));Append(new("gap",subscription,GapFrom:expected,GapTo:Previous(sequence),MissingCount:(long)gap));}
                Append(message with {Duplicate=duplicate});
                if(message.StatusChanges is {Length:>0}){reason="serverStatusChanged";writer.Flush(Elapsed());break;}
                if(!message.KeepAlive)acknowledgements.Add(sequence);
                bool limit=writer.Notifications>=plan.Request.MaximumNotifications;
                uint firstMissing=expected;
                if(ahead)expected=message.KeepAlive?sequence:Next(sequence);
                if(ahead && gap>0 && !limit)
                {
                    uint missing=firstMissing;ulong attempted=0;
                    for(ulong n=0;n<gap && !Expired() && republished<plan.Request.MaximumRepublish;n++,missing=Next(missing))
                    {
                        var recovered=await sessions.Republish(plan.Request.AttemptId,plan.CaptureId,missing,CancellationToken.None);republished++;attempted++;
                        if(recovered.Kind=="republish")
                        {
                            bool repeated=!seen.Add(missing);Append(recovered with {Duplicate=repeated});acknowledgements.Add(missing);
                            if(recovered.StatusChanges is {Length:>0}){reason="serverStatusChanged";limit=true;break;}
                            if(writer.Notifications>=plan.Request.MaximumNotifications){limit=true;break;}
                        }
                        else Append(recovered);
                    }
                    if(attempted<gap && !Expired() && !limit && republished>=plan.Request.MaximumRepublish){reason="sequenceGapLimit";limit=true;}
                }
                // Acknowledge only after the complete original received/recovered envelope is committed.
                writer.Flush(Elapsed());
                if(limit){if(reason is not ("sequenceGapLimit" or "serverStatusChanged"))reason="notificationLimit";break;}
                }
                catch(BridgeException e) when(OpcUaReconnectHistory.Retryable(e.Code))
                { if(!await Recover(e.Code))throw; }
            }
        }
        catch(BridgeException e) when(e.Code is "UaSubscriptionByteLimit" or "UaSubscriptionValueLimit") {reason=e.Code=="UaSubscriptionByteLimit"?"byteLimit":"valueLimit";}
        catch(Exception e)
        {
            reason="sessionUnavailable";
            try {Append(new("failure",subscription,ErrorCode:e is BridgeException b && b.Code.StartsWith("Ua",StringComparison.Ordinal)?b.Code:"UaSubscriptionAcquisitionFailed"));}
            catch {entry.Failure="UaSubscriptionStorageFailure";reason="storageFailure";}
        }
        finally
        {
            try
            {
                var cleanup=await sessions.DeleteSubscription(plan.Request.AttemptId,plan.CaptureId);
                if(cleanup is not null)Append(cleanup);
            }
            catch {entry.Failure="UaSubscriptionCleanupPublicationFailed";}
            finally
            {
                try {if(entry.Stop.IsCancellationRequested)reason=entry.Reason;writer.Finish(reason,Elapsed());}
                catch {entry.Failure="UaSubscriptionStorageFailure";}
                finally {await sessions.Release(plan.Request.AttemptId,plan.CaptureId);}
            }
        }
    }
    internal static uint Next(uint sequence)=>sequence==uint.MaxValue?1:sequence+1;
    internal static uint Previous(uint sequence)=>sequence==1?uint.MaxValue:sequence-1;
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();Task[] work;
        try {disposed=true;foreach(var entry in running.Values){entry.Reason="hostShutdown";entry.Stop.Cancel();}work=running.Values.Select(x=>x.Work).ToArray();}
        finally {gate.Release();}
        try {await Task.WhenAll(work);}finally {foreach(var entry in running.Values)entry.Stop.Dispose();store.Dispose();gate.Dispose();}
    }
}
