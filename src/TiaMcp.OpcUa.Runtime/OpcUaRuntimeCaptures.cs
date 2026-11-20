using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Diagnostics;

namespace TiaMcp.OpcUa.Runtime;

/// <summary>One bounded polling worker per selected session. Restart reopens evidence, never acquisition.</summary>
public sealed class OpcUaRuntimeCaptures(OpcUaRuntimeSessionStore sessions, string artifactsRoot, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private sealed class Running(string attemptId)
    {
        internal string AttemptId = attemptId;
        internal CancellationTokenSource Stop = new();
        internal string Reason = "stopped";
        internal string? Failure;
        internal Task Work = Task.CompletedTask;
    }
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private readonly RuntimeCaptureStore store = new(Path.Combine(artifactsRoot,"runtime-ua-acquisition"),timeProvider);
    private readonly Dictionary<string,Running> running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;

    public async Task<object> Start(string captureId, OpcUaCaptureRequest request, CancellationToken cancellationToken)
    {
        _ = OpcUaCaptureContracts.StorageRequest(request);
        request = request with { Nodes = request.Nodes.ToArray() };
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disposed) throw Error("UaCaptureHostStopping");
            if (store.Exists(captureId))
            {
                var original = store.Plan(captureId).UaContext!.Request;
                if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original,BridgeProtocol.Json),JsonSerializer.SerializeToElement(request,BridgeProtocol.Json)))
                    throw Error("UaCaptureRequestChanged");
                return Observe(captureId);
            }
            // Finished workers leave the dictionary; one that recorded a local failure stays so Status keeps reporting it.
            foreach(var (id,done) in running.Where(x=>x.Value.Work.IsCompleted && x.Value.Failure is null).ToArray()){running.Remove(id);done.Stop.Dispose();}
            if (running.Values.Count(x=>!x.Work.IsCompleted)>=4 || running.Values.Any(x=>!x.Work.IsCompleted && x.AttemptId==request.AttemptId))
                throw Error("UaCaptureSessionBusy");
            if (request.DiagnosticBindingId is { } bindingId)
                _ = new DiagnosticUaBindings(artifactsRoot).ValidateForCapture(bindingId, request.AttemptId, request.Nodes);
            var session = await sessions.Acquire(request.AttemptId,captureId,cancellationToken);
            if(session.State != "connected" || !session.PeerVerified) throw Error("UaSessionUnavailable");
            var plan = new RuntimeCapturePlan(captureId,session.HostId,clock.GetUtcNow(),
                OpcUaCaptureContracts.StorageRequest(request),[],UaContext:new(request,session));
            RuntimeCaptureStore.Writer writer;
            try {writer=store.Create(plan);} catch {await sessions.Release(request.AttemptId,captureId);throw;}
            var entry = new Running(request.AttemptId); running.Add(captureId,entry);
            entry.Work = Task.Run(()=>Poll(plan,writer,entry));
            return Observe(captureId);
        }
        finally { gate.Release(); }
    }
    public async Task<object> Status(string captureId,CancellationToken cancellationToken)
    { await gate.WaitAsync(cancellationToken); try { return Observe(captureId); } finally { gate.Release(); } }
    public async Task<object> Stop(string captureId,CancellationToken cancellationToken)
    {
        Task? worker = null;
        await gate.WaitAsync(cancellationToken);
        try { if(running.TryGetValue(captureId,out var entry)){entry.Stop.Cancel();worker=entry.Work;} }
        finally { gate.Release(); }
        if(worker is not null)await worker;
        return await Status(captureId,cancellationToken);
    }
    public object Page(string snapshotRef,int offset,int count) => store.Page(snapshotRef,offset,count);
    public System.Text.Json.Nodes.JsonObject DiagnosticSource(string snapshotRef)
        => DiagnosticUaCaptureSource.ReadPolling(artifactsRoot, store, snapshotRef);
    public async Task<object> Export(string snapshotRef,string format,CancellationToken cancellationToken)
    {
        bool live=false;await gate.WaitAsync(cancellationToken);
        try { var id=snapshotRef.Split(':').ElementAtOrDefault(1);live=id is not null && running.TryGetValue(id,out var entry) && !entry.Work.IsCompleted; }
        finally {gate.Release();}
        return store.Export(snapshotRef,format,live);
    }
    private object Observe(string id)
    {
        running.TryGetValue(id,out var entry);
        return store.Status(id,entry is not null && !entry.Work.IsCompleted,entry?.Failure);
    }
    private async Task Poll(RuntimeCapturePlan plan,RuntimeCaptureStore.Writer writer,Running entry)
    {
        long started=clock.GetTimestamp(), cycle=0, lastReadCycle=-1,accountedThrough=-1; string reason="durationLimit";int reconnects=0;bool perNode=false;
        using var duration=new CancellationTokenSource(TimeSpan.FromSeconds(plan.Request.DurationSeconds),clock);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token,duration.Token);
        double durationMilliseconds=plan.Request.DurationSeconds*1000d;
        double Elapsed()=>clock.GetElapsedTime(started).TotalMilliseconds;
        double stoppedElapsed=double.PositiveInfinity;
        using var stopObservation=entry.Stop.Token.Register(()=>Volatile.Write(ref stoppedElapsed,Elapsed()));
        // Timer callbacks can run late; elapsed time must independently prevent new reads.
        bool Expired()=>deadline.IsCancellationRequested || Elapsed()>=durationMilliseconds;
        void Recovery(OpcUaReconnectEvent e)
        {
            writer.Append(new(writer.Rows,"gap",null,cycle,cycle*(double)plan.Request.IntervalMilliseconds,Elapsed(),clock.GetUtcNow(),null,null,0,e));
            writer.Flush(Elapsed());
        }
        async Task<bool> Recover(string error)
        {
            while(!Expired() && OpcUaReconnectHistory.Retryable(error) && reconnects<plan.UaContext!.Request.MaximumReconnectAttempts)
            {
                int attempt=++reconnects;Recovery(new(attempt,"starting",error));
                string? failure=null;
                try {await Task.Delay(TimeSpan.FromMilliseconds(500),clock,deadline.Token);await sessions.ReconnectAcquisition(plan.Request.AttemptId,plan.CaptureId,deadline.Token);}
                catch(Exception e) {failure=e is BridgeException b && b.Code.StartsWith("Ua",StringComparison.Ordinal)?b.Code:deadline.IsCancellationRequested?"UaCancelled":"UaReconnectFailed";}
                Recovery(new(attempt,failure is null?"succeeded":"failed",failure));
                if(failure is null)return true;
                error=failure;
            }
            return false;
        }
        void RecordElapsedGaps()
        {
            if(lastReadCycle<0)return;
            double elapsed=Elapsed();
            long through=RuntimeCaptureStore.NextCycle(lastReadCycle,Math.Min(Math.Min(elapsed,Volatile.Read(ref stoppedElapsed)),durationMilliseconds-0.000001),plan.Request.IntervalMilliseconds)-1;
            if(through<=accountedThrough)return;
            writer.Append(new(writer.Rows,"gap",null,lastReadCycle,lastReadCycle*(double)plan.Request.IntervalMilliseconds,elapsed,clock.GetUtcNow(),null,null,through-accountedThrough));
            accountedThrough=through;
        }
        try
        {
            while(!Expired())
            {
                double scheduled=cycle*(double)plan.Request.IntervalMilliseconds;
                double due=Math.Min(scheduled,durationMilliseconds);
                // Timer completion is only a wake-up: it may occur before the
                // requested slot. Recheck monotonic time and keep waits positive.
                while(!Expired())
                {
                    double remaining=due-Elapsed();
                    if(remaining<=0)break;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,Math.Ceiling(remaining))),clock,deadline.Token);
                }
                if(scheduled>=durationMilliseconds || Expired())break;
                bool finish=false;
                var nodes=plan.UaContext!.Request.Nodes;
                // One Read request per cycle for every node still inside the sample budget; the rows stay one per node.
                int budget=(int)Math.Min(nodes.Length,plan.Request.MaximumSamples-writer.ReadAttempts);
                if(budget<=0){reason="sampleLimit";finish=true;}
                else if(!Expired())
                {
                    lastReadCycle=cycle;accountedThrough=cycle;
                    string?[] results=new string?[budget],errors=new string?[budget];int rows=budget;
                    async Task<string> ReadSerialized(OpcUaRuntimeNode[] subset,int index)
                    {
                        var read=await sessions.ReadAcquisition(plan.Request.AttemptId,plan.CaptureId,subset,deadline.Token);
                        if(read.Items.Count!=subset.Length)throw new BridgeException("UaResponseInvalid","The batched read did not answer every node.");
                        return JsonSerializer.Serialize(read with {Items=[read.Items[index]]},BridgeProtocol.Json);
                    }
                    if(!perNode)
                        try
                        {
                            var read=await sessions.ReadAcquisition(plan.Request.AttemptId,plan.CaptureId,nodes.Take(budget).ToArray(),deadline.Token);
                            if(read.Items.Count!=budget)throw new BridgeException("UaResponseInvalid","The batched read did not answer every node.");
                            for(int i=0;i<budget;i++)results[i]=JsonSerializer.Serialize(read with {Items=[read.Items[i]]},BridgeProtocol.Json);
                        }
                        // The cycle's answers together passed the output limit that each node had to itself: this capture reads node by node from now on.
                        catch(BridgeException tooLarge) when(tooLarge.Code=="UaResponseLimit" && budget>1) { perNode=true; }
                        catch(OperationCanceledException) { errors[0]="UaCancelled";rows=1; }
                        catch(Exception failure) { errors[0]=OpcUaCaptureContracts.ReadError(failure);rows=1; }
                    if(perNode)
                        for(int i=0;i<budget;i++)
                        {
                            try { results[i]=await ReadSerialized([nodes[i]],0); }
                            catch(OperationCanceledException) { errors[i]="UaCancelled";rows=i+1;break; }
                            catch(Exception failure) { errors[i]=OpcUaCaptureContracts.ReadError(failure);rows=i+1;break; }
                        }
                    // A failed request leaves one failed row (for the first node it failed on) and ends the cycle, as a failed read always did.
                    for(int i=0;i<rows;i++)
                    {
                        string selectionId=OpcUaCaptureContracts.SelectionId(nodes[i]);
                        try { writer.Append(new(writer.Rows,"read",selectionId,cycle,scheduled,Elapsed(),clock.GetUtcNow(),results[i],errors[i],0)); }
                        catch(BridgeException failure) when(failure.Code is "RuntimeCaptureByteLimit" or "RuntimeCaptureValueLimit") {writer.RecordDroppedResult();throw;}
                        if(errors[i] is not null)
                        {
                            if(writer.ReadAttempts<plan.Request.MaximumSamples && await Recover(errors[i]!))break; // Continue at the next scheduled cycle; the failed read is never replaced.
                            reason=deadline.IsCancellationRequested?"durationLimit":"sessionUnavailable";finish=true;break;
                        }
                        if(writer.ReadAttempts>=plan.Request.MaximumSamples){reason="sampleLimit";finish=true;break;}
                    }
                }
                RecordElapsedGaps();
                cycle=Math.Max(cycle+1,accountedThrough+1);
                if(finish)break;
            }
        }
        catch(OperationCanceledException) when(deadline.IsCancellationRequested) { }
        catch(BridgeException error) when(error.Code is "RuntimeCaptureByteLimit" or "RuntimeCaptureValueLimit") {reason=error.Code=="RuntimeCaptureByteLimit"?"byteLimit":"valueLimit";}
        catch(Exception) {entry.Failure="RuntimeCaptureStorageFailure";reason="storageFailure";}
        finally
        {
            try
            {
                if(entry.Failure is null)
                    try { RecordElapsedGaps(); }
                    catch(BridgeException error) when(error.Code is "RuntimeCaptureByteLimit" or "RuntimeCaptureValueLimit") {reason=error.Code=="RuntimeCaptureByteLimit"?"byteLimit":"valueLimit";}
                    catch {entry.Failure="RuntimeCaptureStorageFailure";reason="storageFailure";}
                if(entry.Stop.IsCancellationRequested)reason=entry.Reason;
                writer.Finish(reason,Elapsed());
            }
            catch {entry.Failure="RuntimeCaptureStorageFailure";}
            finally {await sessions.Release(plan.Request.AttemptId,plan.CaptureId);}
        }
    }
    private static BridgeException Error(string code) => new(code,"Capture request refused. Retained data is not resumed and no PLC values are written.");
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync(); Task[] workers;
        try { disposed=true;foreach(var entry in running.Values){entry.Reason="hostShutdown";entry.Stop.Cancel();} workers=running.Values.Select(x=>x.Work).ToArray(); }
        finally { gate.Release(); }
        await Task.WhenAll(workers);
        foreach(var entry in running.Values)entry.Stop.Dispose();store.Dispose();gate.Dispose();
    }
}
