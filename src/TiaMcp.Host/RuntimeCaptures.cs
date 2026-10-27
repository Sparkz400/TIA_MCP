using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Host;

/// <summary>One bounded polling worker per selected session or owned PLCSIM instance. Restart reopens evidence, never acquisition.</summary>
public sealed class RuntimeCaptures(PhysicalRuntimeSessions sessions, HostOptions options,TimeProvider? timeProvider,IBridgeClient? simulator=null) : IAsyncDisposable
{
    public RuntimeCaptures(PhysicalRuntimeSessions sessions,HostOptions options):this(sessions,options,TimeProvider.System){}
    private sealed class Running(string attemptId)
    {
        internal string AttemptId = attemptId;
        internal CancellationTokenSource Stop = new();
        internal string Reason = "stopped";
        internal string? Failure;
        internal Task Work = Task.CompletedTask;
    }
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private readonly RuntimeCaptureStore store = new(options.ArtifactsDirectory,timeProvider);
    private readonly Dictionary<string,Running> running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    private readonly string hostId = Guid.NewGuid().ToString("N");
    private bool disposed;

    public async Task<object> Start(string captureId, RuntimeCaptureRequest request, CancellationToken cancellationToken)
    {
        RuntimeCaptureStore.Validate(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (disposed) throw Error("RuntimeCaptureHostStopping");
            if (store.Exists(captureId))
            {
                var original = store.Plan(captureId).Request;
                if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original,BridgeProtocol.Json),JsonSerializer.SerializeToElement(request,BridgeProtocol.Json)))
                    throw Error("RuntimeCaptureRequestChanged");
                return Observe(captureId);
            }
            if (running.Values.Count(x=>!x.Work.IsCompleted)>=4 || running.Values.Any(x=>!x.Work.IsCompleted && x.AttemptId==request.AttemptId))
                throw Error("RuntimeCaptureSessionBusy");
            bool plcsim = request.Simulator is not null;
            if (plcsim && simulator is null) throw Error("RuntimeCaptureSourceUnavailable");
            var context = plcsim ? default : await sessions.CaptureContext(request.AttemptId,request.SelectionIds,request.Representation,cancellationToken);
            var signals = plcsim ? SimulatorCaptureContracts.Signals(request) : context.Signals;
            if (request.ReviewId is not null) _ = new ProjectReviewStore(options.ArtifactsDirectory).Read(request.ReviewId);
            var association = RuntimeCaptureAssociations.Resolve(options.ArtifactsDirectory,request,signals);
            var handoff = plcsim ? null : RuntimeCaptureHandoffs.Resolve(options.ArtifactsDirectory, request, context.Session, signals);
            var plan = new RuntimeCapturePlan(captureId,hostId,clock.GetUtcNow(),request,signals,association,handoff);
            // The worker refuses anything but the exact owned instance and enters sync-point mode before durable intent exists.
            if (plcsim) await simulator!.CallAsync("sim_capture_begin",SimulatorCaptureContracts.Arguments(request),CancellationToken.None);
            RuntimeCaptureStore.Writer writer;
            try { writer = store.Create(plan); } catch { await EndSimulator(request); throw; }
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
    public object Analyze(string snapshotRef,JsonObject units)=>new RuntimeCaptureAnalysis(options.ArtifactsDirectory).Analyze(store,snapshotRef,units);
    public object AcquireDiagnostic(string experimentId,string snapshotRef,JsonObject request)
        =>new DiagnosticRuntimeAcquisitions(options.ArtifactsDirectory).Acquire(store,experimentId,snapshotRef,request);
    public object ExportDiagnosticAcquisition(string acquisitionId,string format)
        =>new DiagnosticRuntimeAcquisitions(options.ArtifactsDirectory).Export(store,acquisitionId,format);
    public object CompareDiagnosticAcquisitions(string beforeAcquisitionId,string afterAcquisitionId,JsonObject request)
        =>new DiagnosticRuntimeAcquisitions(options.ArtifactsDirectory).Compare(store,beforeAcquisitionId,afterAcquisitionId,request);
    /// <summary>Diagnostic-case signal writes on the exact owned PLCSIM instance; refused while a capture holds that instance.</summary>
    public async Task<object> WriteDiagnosticPlcsim(string caseId,int instanceId,string instanceName,string creationId,DiagnosticPlcsimWrite[] writes,CancellationToken cancellationToken)
    {
        var (arguments,association)=DiagnosticPlcsimWrites.Admit(options.ArtifactsDirectory,caseId,instanceId,instanceName,creationId,writes);
        if(simulator is null)throw Error("RuntimeCaptureSourceUnavailable");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if(disposed)throw Error("RuntimeCaptureHostStopping");
            if(running.Values.Any(x=>!x.Work.IsCompleted && x.AttemptId==creationId))throw Error("RuntimeCaptureSessionBusy");
            // Native calls are never cancelled: a cancelled bridge call faults the simulator worker for every later tool.
            return DiagnosticPlcsimWrites.Receipt(arguments,association,await simulator.CallAsync("sim_write_owned",arguments,CancellationToken.None));
        }
        finally{gate.Release();}
    }
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
                // PLCSIM: one worker call per cycle reads every signal (after stepping one sync point in cycle-synchronous mode).
                var sample=plan.Request.Simulator is null?null:await SimulatorSample(plan);
                for(int index=0;index<plan.Signals.Length;index++)
                {
                    var signal=plan.Signals[index];
                    if(sample is null && Expired())break;
                    lastReadCycle=cycle;accountedThrough=cycle;
                    string? result=null,error=null;
                    if(sample is not null)(result,error)=sample[index];
                    else try { result=JsonSerializer.Serialize(await sessions.ReadValue(plan.Request.AttemptId,signal.SelectionId,signal.Selection!.Representation,deadline.Token,plan.Handoff),BridgeProtocol.Json); }
                    catch(OperationCanceledException) { error="RuntimeCancelled"; }
                    catch(Exception failure) { error=ReadFailure(failure); }
                    try { writer.Append(new(writer.Rows,"read",signal.SelectionId,cycle,scheduled,Elapsed(),clock.GetUtcNow(),result,error,0)); }
                    catch(BridgeException failure) when(failure.Code is "RuntimeCaptureByteLimit" or "RuntimeCaptureValueLimit") {writer.RecordDroppedResult();throw;}
                    if(writer.ReadAttempts>=plan.Request.MaximumSamples){reason="sampleLimit";finish=true;break;}
                    if(error is not (null or "RuntimeTimeout" or "RuntimeTransportFailure" or "RuntimeCancelled")) {reason="sessionUnavailable";finish=true;break;}
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
            if(!await EndSimulator(plan.Request))entry.Failure??="PlcsimOperatingModeRestoreFailed";
        }
    }
    // Native calls are never cancelled: a cancelled bridge call faults the simulator worker for every later tool.
    private async Task<(string? Result,string? Error)[]> SimulatorSample(RuntimeCapturePlan plan)
    {
        try { return SimulatorCaptureContracts.Reads(plan.Request,await simulator!.CallAsync("sim_capture_sample",SimulatorCaptureContracts.Arguments(plan.Request),CancellationToken.None)); }
        catch(Exception failure) { string code=SimulatorCaptureContracts.ReadError(failure);return plan.Signals.Select(_=>((string?)null,(string?)code)).ToArray(); }
    }
    /// <summary>Restores the operating mode recorded at begin; free-running captures never changed it.</summary>
    private async Task<bool> EndSimulator(RuntimeCaptureRequest request)
    {
        if(request.Simulator is not { CycleSynchronous: true })return true;
        try { await simulator!.CallAsync("sim_capture_end",SimulatorCaptureContracts.Arguments(request),CancellationToken.None);return true; }
        catch { return false; }
    }
    private static string ReadFailure(Exception failure) => failure is BridgeException error && error.Code is
        "RuntimeTimeout" or "RuntimeTransportFailure" or "RuntimeCancelled" or "RuntimePermissionDenied" or "RuntimeSessionUnavailable" or
        "RuntimeSessionExpired" or "RuntimeSessionIdentityMismatch" or "RuntimeSymbolMetadataChanged" or "RuntimeRecordChanged" or "CredentialRetired" or "RuntimeCaptureHandoffMismatch" or "RuntimeCaptureHandoffUnavailable"
        ? error.Code : "RuntimeCaptureReadFailed";
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
