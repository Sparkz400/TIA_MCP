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
        long started=clock.GetTimestamp(), cycle=0, lastReadCycle=-1,accountedThrough=-1; string reason="durationLimit";
        using var duration=new CancellationTokenSource(TimeSpan.FromSeconds(plan.Request.DurationSeconds),clock);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token,duration.Token);
        double durationMilliseconds=plan.Request.DurationSeconds*1000d;
        double Elapsed()=>clock.GetElapsedTime(started).TotalMilliseconds;
        double stoppedElapsed=double.PositiveInfinity;
        using var stopObservation=entry.Stop.Token.Register(()=>Volatile.Write(ref stoppedElapsed,Elapsed()));
        // Timer callbacks can run late; elapsed time must independently prevent new reads.
        bool Expired()=>deadline.IsCancellationRequested || Elapsed()>=durationMilliseconds;
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
                foreach(var node in plan.UaContext!.Request.Nodes)
                {
                    if(Expired())break;
                    string selectionId=OpcUaCaptureContracts.SelectionId(node);
                    lastReadCycle=cycle;accountedThrough=cycle;
                    string? result=null,error=null;
                    try { result=JsonSerializer.Serialize(await sessions.ReadAcquisition(plan.Request.AttemptId,plan.CaptureId,[node],deadline.Token),BridgeProtocol.Json); }
                    catch(OperationCanceledException) { error="UaCancelled"; }
                    catch(Exception failure) { error=OpcUaCaptureContracts.ReadError(failure); }
                    try { writer.Append(new(writer.Rows,"read",selectionId,cycle,scheduled,Elapsed(),clock.GetUtcNow(),result,error,0)); }
                    catch(BridgeException failure) when(failure.Code is "RuntimeCaptureByteLimit" or "RuntimeCaptureValueLimit") {writer.RecordDroppedResult();throw;}
                    if(error is not null) {reason=deadline.IsCancellationRequested?"durationLimit":"sessionUnavailable";finish=true;break;}
                    if(writer.ReadAttempts>=plan.Request.MaximumSamples){reason="sampleLimit";finish=true;break;}
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
