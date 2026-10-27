using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Host;

// Compiles the production Host poller; only its authenticated-session boundary is replaced.
internal static class RuntimePollingChecks
{
    internal static async Task<int> Run(string root)
    {
        int checks=0;var passed=new List<string>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;passed.Add(label);}
        await using(var f=new PollFixture(root,2))
        {
            f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(1500,fire:false);return Task.CompletedTask;};
            await f.Start(maximumSamples:4);
            var status=await f.Ended();
            Check(f.Sessions.Reads.Count==1,"Delayed deadline callback must not start a second read after monotonic duration");
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="durationLimit","Delayed callback retains duration-limit reason");
            Check(status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()==9,"Slow final read counts missed slots only inside requested duration");
        }
        foreach(bool slow in new[]{false,true})
        {
            await using var f=new PollFixture(root);
            f.Sessions.BeforeRead=(number,_)=>{f.Clock.Advance(slow && number==1?350:10);return Task.CompletedTask;};
            await f.Start(maximumSamples:3);
            _=await f.Clock.NextTimer();
            var first=await f.Clock.NextTimer();f.Clock.Advance((int)(first.Due-f.Clock.GetTimestamp()));
            var second=await f.Clock.NextTimer();f.Clock.Advance((int)(second.Due-f.Clock.GetTimestamp()));
            var status=await f.Ended();var rows=f.Page(status).GetProperty("rows").EnumerateArray().Where(r=>r.GetProperty("kind").GetString()=="read").ToArray();
            Check(f.Sessions.Reads.Select(r=>r.Started).SequenceEqual(slow?new long[]{0,400,500}:[0,100,200]),"Fast/slow polling uses scheduled slots without catch-up burst");
            Check(rows.Select(r=>r.GetProperty("cycle").GetInt64()).SequenceEqual(slow?new long[]{0,4,5}:[0,1,2]),"Read cycle numbers do not reuse slots reported skipped");
            Check(status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()==(slow?3:0),"Only missed scheduled slots are counted");
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit" && f.Clock.ActiveTimers==0,"Sample completion disposes deadline and wait timers");
        }
        await using(var f=new PollFixture(root))
        {
            f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(10,fire:false);return Task.CompletedTask;};
            await f.Start(maximumSamples:2);_=await f.Clock.NextTimer();var delay=await f.Clock.NextTimer();
            f.Clock.Advance(89,fire:false);delay.FireEarly();
            var rewait=await f.Clock.NextTimer();
            Check(f.Sessions.Reads.Count==1 && rewait.Due==100,"Early interior wake waits for the original scheduled slot");
            rewait.FireEarly();var repeatedWait=await f.Clock.NextTimer();
            Check(f.Sessions.Reads.Count==1 && repeatedWait.Due==100,"Repeated early wake cannot dispatch or create a zero-delay spin");
            f.Clock.Advance(1);var status=await f.Ended();var rows=f.Page(status).GetProperty("rows");
            Check(f.Sessions.Reads.Select(r=>r.Started).SequenceEqual(new long[]{0,100}) && rows[1].GetProperty("scheduledMilliseconds").GetDouble()==100,"Interior read starts only when its scheduled monotonic time is reached");
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit" && f.Clock.ActiveTimers==0,"Early-wake sample completion releases every timer");
        }
        foreach(int interval in new[]{100,2000})
        {
            await using var f=new PollFixture(root);
            f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(interval==100?950:10,fire:false);return Task.CompletedTask;};
            await f.Start(interval:interval);_=await f.Clock.NextTimer();var delay=await f.Clock.NextTimer();
            Check(delay.Due==1000,"A slot at or beyond duration waits only until the duration boundary");
            f.Clock.Advance(999-(int)f.Clock.GetTimestamp(),fire:false);delay.FireEarly();var rewait=await f.Clock.NextTimer();
            Check(f.Sessions.Reads.Count==1 && rewait.Due==1000,"Early duration wake cannot dispatch an exact or outside-duration slot");
            f.Clock.Advance(1);var status=await f.Ended();var rows=f.Page(status).GetProperty("rows").EnumerateArray().Where(r=>r.GetProperty("kind").GetString()=="read").ToArray();
            Check(rows.Length==1 && rows.All(r=>r.GetProperty("scheduledMilliseconds").GetDouble()<1000) &&
                status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="durationLimit","No requested read slot at or beyond duration is acquired");
            Check(status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()==(interval==100?9:0) && f.Clock.ActiveTimers==0,"Duration rewait preserves bounded gaps and disposes every timer");
        }
        foreach(bool stopDuringRewait in new[]{true,false})
        {
            await using var f=new PollFixture(root);
            f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(10,fire:false);return Task.CompletedTask;};
            await f.Start(maximumSamples:4);var deadline=await f.Clock.NextTimer();var delay=await f.Clock.NextTimer();
            f.Clock.Advance(89,fire:false);delay.FireEarly();_=await f.Clock.NextTimer();
            if(stopDuringRewait)await f.Captures.Stop(f.Id,CancellationToken.None);
            else {f.Clock.Advance(901,fire:false);deadline.Fire();}
            var status=await f.Ended();var progress=status.GetProperty("recordedProgress");
            Check(f.Sessions.Reads.Count==1,"Stop or deadline during an early-wake rewait cannot start another read");
            Check(progress.GetProperty("terminalReason").GetString()==(stopDuringRewait?"stopped":"durationLimit") &&
                progress.GetProperty("skippedPolls").GetInt64()==(stopDuringRewait?0:9),"Rewait cancellation retains its exact reason and elapsed gap boundary");
            Check(f.Clock.ActiveTimers==0,"Rewait cancellation releases all duration and delay timers");
        }
        foreach(string ending in new[]{"lateWake","deadline","stop"})
        {
            await using var f=new PollFixture(root);
            f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(10);return Task.CompletedTask;};
            await f.Start(maximumSamples:4);var deadline=await f.Clock.NextTimer();var delay=await f.Clock.NextTimer();
            f.Clock.Advance(ending=="stop"?340:1190,fire:false);
            if(ending=="lateWake")delay.Fire();else if(ending=="deadline")deadline.Fire();else await f.Captures.Stop(f.Id,CancellationToken.None);
            var status=await f.Ended();
            Check(f.Sessions.Reads.Count==1,"Late wake/cancellation cannot manufacture another read");
            Check(status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()==(ending=="stop"?3:9),"Cancellation while waiting retains all elapsed missed slots");
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()==(ending=="stop"?"stopped":"durationLimit"),"Explicit stop and deadline remain distinct");
            Check(f.Clock.ActiveTimers==0,"Terminal polling disposes every timer");
        }
        await using(var f=new PollFixture(root))
        {
            f.Sessions.BeforeRead=(number,_)=>{f.Clock.ShiftUtc(number==1?-3600000:7200000);f.Clock.Advance(10);return Task.CompletedTask;};
            await f.Start(maximumSamples:2);_=await f.Clock.NextTimer();var delay=await f.Clock.NextTimer();f.Clock.Advance((int)(delay.Due-f.Clock.GetTimestamp()));
            var status=await f.Ended();var rows=f.Page(status).GetProperty("rows");
            using var first=JsonDocument.Parse(rows[0].GetProperty("readResultJson").GetString()!);
            Check(first.RootElement.GetProperty("hostClockWentBackwards").GetBoolean(),"Backward UTC evidence remains explicit");
            Check(f.Sessions.Reads.Select(r=>r.Started).SequenceEqual(new long[]{0,100}) && rows[1].GetProperty("elapsedMilliseconds").GetDouble()==110,"UTC jumps do not change monotonic schedule or durations");
        }
        await using(var f=new PollFixture(root,2))
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Sessions.BeforeRead=async(_,token)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);};
            await f.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));f.Clock.Advance(1000);
            var status=await f.Ended();var rows=f.Page(status).GetProperty("rows");
            Check(f.Sessions.Reads.Count==1 && rows[0].GetProperty("errorCode").GetString()=="RuntimeCancelled","In-flight deadline retains cancellation and never starts the next selected signal");
            Check(status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()==9,"Cancelled in-flight read retains elapsed missed slots");
        }
        await using(var f=new PollFixture(root))
        {
            var entered=Channel.CreateUnbounded<int>();
            f.Sessions.BeforeRead=async(number,token)=>{entered.Writer.TryWrite(number);await Task.Delay(Timeout.Infinite,token);};
            var original=f.Sessions.Signals[0];
            f.Sessions.Signals=Enumerable.Range(0,5).Select(i=>i==0?original:new RuntimeCaptureSignal(Guid.NewGuid().ToString("N"),
                WebRuntimeProtocol.Symbol(original.Selection!.Context with{ConnectionId=Guid.NewGuid().ToString("N"),Epoch=Guid.NewGuid().ToString("N")},original.Selection.Symbol,"simple",original.Metadata),original.Metadata)).ToArray();
            var ids=new List<string>();
            RuntimeCaptureRequest Request(int i)=>new(f.Sessions.Signals[i].Selection!.Context.ConnectionId,[f.Sessions.Signals[i].SelectionId],100,1,100,1<<20);
            for(int i=0;i<4;i++)
            {
                string id=i==0?f.Id:Guid.NewGuid().ToString("N");ids.Add(id);
                await f.Captures.Start(id,Request(i),CancellationToken.None);
                _=await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            async Task Refuse(RuntimeCaptureRequest request)
            {
                try{await f.Captures.Start(Guid.NewGuid().ToString("N"),request,CancellationToken.None);throw new Exception("Expected capture worker capacity refusal");}
                catch(BridgeException e)when(e.Code=="RuntimeCaptureSessionBusy"){Check(true,"Capacity refuses another active worker without capture intent");}
            }
            await Refuse(Request(4));await Refuse(Request(0));
            _=await f.Captures.Start(f.Id,Request(0),CancellationToken.None);
            Check(f.Sessions.Reads.Count==4,"Four active sessions stay bounded and repeated capture intent starts no worker");
            await f.DisposeAsync();
            using var reopened=new RuntimeCaptureStore(f.Root);
            foreach(string id in ids)
            {
                var status=JsonSerializer.SerializeToElement(reopened.Status(id,false),BridgeProtocol.Json);
                Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="hostShutdown","Shutdown drains every owned worker and persists its partial capture");
            }
            Check(f.Clock.ActiveTimers==0,"Shutdown disposes all four worker deadline timers");
        }
        await using(var f=new PollFixture(root))
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Sessions.BeforeRead=async(_,_)=>{entered.TrySetResult();await release.Task;};
            await f.Start();await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));f.Clock.Advance(350,fire:false);
            var stopping=f.Captures.Stop(f.Id,CancellationToken.None);
            f.Clock.Advance(500,fire:false);release.TrySetResult();await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            var status=await f.Ended();
            Check(status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()==3,"Late in-flight completion cannot count planned slots after explicit stop");
            Check(f.Page(status).GetProperty("rows")[0].GetProperty("elapsedMilliseconds").GetDouble()==850,"Late result timing is retained without extending the requested capture window");
        }
        await using(var f=new PollFixture(root,64))
        {
            f.Clock.AdvanceShortDelays=true;
            f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(1);throw new BridgeException("RuntimeTimeout","Synthetic bounded load");};
            await f.Start(maximumSamples:100000,duration:3600);var status=await f.Ended();var progress=status.GetProperty("recordedProgress");
            Check(progress.GetProperty("terminalReason").GetString()=="byteLimit" && progress.GetProperty("readAttempts").GetInt64()>512,"Sustained 64-signal timeout load crosses multiple buffers and stops at byte budget");
            Check(progress.GetProperty("droppedReadResults").GetInt64()==1 && status.GetProperty("localFailure").ValueKind==JsonValueKind.Null,"Byte exhaustion counts its rejected result and publishes a normal final prefix");
            long bytes=Directory.EnumerateFiles(Path.Combine(f.Root,"runtime-captures",f.Id)).Sum(p=>new FileInfo(p).Length);
            Check(bytes<=1<<20 && f.Clock.ActiveTimers==0,"Bounded load stays within its retained byte budget and releases timers");
        }
        int completionChecks=await RuntimeCaptureCompletionChecks.Run(root);checks+=completionChecks;
        int aclChecks=OperatingSystem.IsWindows()?await RuntimeCaptureAclChecks.Run(root):0;checks+=aclChecks;
        int pressureChecks=await RuntimeCapturePressureChecks.Run(Path.Combine(root,"pressure"));checks+=pressureChecks;
        File.WriteAllText(Path.Combine(root,"polling-checks.json"),JsonSerializer.Serialize(new{productionSourceLinked=true,sessionBoundary="testDouble",nativeExecuted=false,checks=passed,completionFailureChecks=completionChecks,windowsAclChecks=aclChecks,pressureChecks},BridgeProtocol.Json));
        return checks;
    }

    internal sealed class PollFixture : IAsyncDisposable
    {
        internal ManualCaptureClock Clock=new();
        internal PhysicalRuntimeSessions Sessions;
        internal RuntimeCaptures Captures;
        internal string Id=Guid.NewGuid().ToString("N"),Attempt=Guid.NewGuid().ToString("N"),Root;
        private bool disposed;
        internal PollFixture(string root,int signals=1)
        {
            Root=Path.Combine(root,Id);
            var context=WebRuntimeProtocol.Context("https://polling.example",Attempt,Guid.NewGuid().ToString("N"),new string('a',64));
            var selected=Enumerable.Range(0,signals).Select(i=>{
                var metadata=JsonSerializer.SerializeToElement(new{name="Flag"+i,datatype="bool"});
                return new RuntimeCaptureSignal(Guid.NewGuid().ToString("N"),WebRuntimeProtocol.Symbol(context,"\"DB\".Flag"+i,"simple",metadata),metadata);
            }).ToArray();
            Sessions=new(Clock,selected);
            Captures=new(Sessions,new("",Root,null,5,false,"",null),Clock);
        }
        internal Task<object> Start(int maximumSamples=100,int interval=100,int duration=1)=>Captures.Start(Id,new(Attempt,Sessions.Signals.Select(s=>s.SelectionId).ToArray(),interval,duration,maximumSamples,1<<20),CancellationToken.None);
        internal async Task<JsonElement> Ended(int timeoutSeconds=10)
        {
            async Task<JsonElement> Wait()
            {
                while(true)
                {
                    var status=JsonSerializer.SerializeToElement(await Captures.Status(Id,CancellationToken.None),BridgeProtocol.Json);
                    if(status.GetProperty("state").GetString()!="running")return status;
                    await Task.Delay(1);
                }
            }
            return await Wait().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        }
        internal JsonElement Page(JsonElement status)=>JsonSerializer.SerializeToElement(Captures.Page(status.GetProperty("snapshotRef").GetString()!,0,200),BridgeProtocol.Json);
        public async ValueTask DisposeAsync(){if(!disposed){disposed=true;await Captures.DisposeAsync();}}
    }
}

internal sealed class ManualCaptureClock : TimeProvider
{
    private readonly object gate=new();
    private long elapsed;
    private DateTimeOffset utc=new(2026,10,18,5,0,0,TimeSpan.Zero);
    private readonly List<ManualTimer> timers=[];
    private readonly Channel<ManualTimer> created=Channel.CreateUnbounded<ManualTimer>();
    internal bool AdvanceShortDelays;
    public override long TimestampFrequency=>1000;
    public override long GetTimestamp(){lock(gate)return elapsed;}
    public override DateTimeOffset GetUtcNow(){lock(gate)return utc;}
    internal void ShiftUtc(int milliseconds){lock(gate)utc=utc.AddMilliseconds(milliseconds);}
    internal void Advance(int milliseconds,bool fire=true)
    {
        ManualTimer[] ready;
        lock(gate){elapsed+=milliseconds;utc=utc.AddMilliseconds(milliseconds);ready=timers.Where(t=>t.Due<=elapsed).ToArray();}
        if(fire)foreach(var timer in ready)timer.Fire();
    }
    internal Task<ManualTimer> NextTimer()=>created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    internal int ActiveTimers{get{lock(gate)return timers.Count(t=>!t.Disposed);}}
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
    {
        var timer=new ManualTimer(this,callback,state);timer.Change(dueTime,period);
        lock(gate)timers.Add(timer);
        created.Writer.TryWrite(timer);
        if(AdvanceShortDelays && dueTime>=TimeSpan.Zero && dueTime<TimeSpan.FromSeconds(1))Advance((int)dueTime.TotalMilliseconds);
        return timer;
    }
    internal sealed class ManualTimer(ManualCaptureClock clock,TimerCallback callback,object? state) : ITimer
    {
        internal long Due=long.MaxValue;
        internal bool Disposed;
        public bool Change(TimeSpan dueTime,TimeSpan period)
        {
            if(period!=Timeout.InfiniteTimeSpan)throw new Exception("Capture test clock expects one-shot timers");
            lock(clock.gate){if(Disposed)return false;Due=dueTime==Timeout.InfiniteTimeSpan?long.MaxValue:clock.elapsed+(long)dueTime.TotalMilliseconds;return true;}
        }
        internal void Fire()
        {lock(clock.gate){if(Disposed || Due>clock.elapsed)return;Due=long.MaxValue;}callback(state);}
        internal void FireEarly()
        {
            lock(clock.gate)
            {
                if(Disposed || Due<=clock.elapsed || Due==long.MaxValue)throw new Exception("Expected a pending future timer");
                Due=long.MaxValue;
            }
            callback(state);
        }
        public void Dispose(){lock(clock.gate){Disposed=true;Due=long.MaxValue;}}
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
}

namespace TiaMcp.Host
{
    // A test double, not an alternate production session implementation.
    public sealed class PhysicalRuntimeSessions
    {
        private readonly TimeProvider clock;
        internal RuntimeCaptureSignal[] Signals;
        internal List<(string SelectionId,long Started)> Reads=[];
        internal Func<int,CancellationToken,Task>? BeforeRead;
        internal Func<int,string>? ResponseMember;
        internal PhysicalRuntimeSessions(TimeProvider clock,RuntimeCaptureSignal[] signals){this.clock=clock;Signals=signals;}
        public Task<(RuntimeCaptureSession Session, RuntimeCaptureSignal[] Signals)> CaptureContext(string attemptId,string[] ids,string representation,CancellationToken cancellationToken)=>Task.FromResult<(RuntimeCaptureSession, RuntimeCaptureSignal[])>((null!, ids.Select(id=>Signals.Single(s=>s.SelectionId==id)).ToArray()));
        public async Task<WebRuntimeReadResult> ReadValue(string attemptId,string selectionId,string representation,CancellationToken token,RuntimeCaptureHandoff? handoff = null)
        {
            int number;lock(Reads){Reads.Add((selectionId,clock.GetTimestamp()));number=Reads.Count;}
            var signal=Signals.Single(s=>s.SelectionId==selectionId);var start=clock.GetUtcNow();long timestamp=clock.GetTimestamp();
            if(BeforeRead is not null)await BeforeRead(number,token);
            string id=Guid.NewGuid().ToString("N");var request=WebRuntimeProtocol.Read(signal.Selection!.Context,signal.Selection,id);
            return WebRuntimeProtocol.ReadResult(signal.Selection.Context,request,Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\""+id+"\","+(ResponseMember?.Invoke(number)??"\"result\":true")+"}"),start,clock.GetUtcNow(),clock.GetElapsedTime(timestamp));
        }
    }
}
