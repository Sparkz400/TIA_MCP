using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Host;

// Production Host poller and store; only the simulator worker boundary is a fake. No PLCSIM API is loaded.
internal static class RuntimeCapturePlcsimChecks
{
    internal static async Task<int> Run(string root)
    {
        int checks=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
        async Task Fails(Func<Task> action,string code){try{await action();}catch(BridgeException e)when(e.Code==code){checks++;return;}throw new Exception("Expected "+code);}
        string creation=Guid.NewGuid().ToString("N");
        RuntimeCaptureRequest Request(bool sync,int samples=4,int duration=10)=>new(creation,[Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")],100,duration,samples,1<<20,
            Source:"plcsim",Simulator:new(7,"TiaMcp_owned",[new("\"DB\".Flag","bool","Bool"),new("\"DB\".Counter","uint64","ULInt")],sync));
        string Read(JsonElement row)=>row.GetProperty("readResultJson").GetString()!;

        await using(var f=new Fixture(root))
        {
            var request=Request(false);await f.Captures.Start(f.Id,request,CancellationToken.None);var status=await f.Ended();var rows=f.Rows(status);
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit" && rows.GetArrayLength()==4,"Free-running PLCSIM polling uses the shared bounds and terminal reasons");
            Check(f.Simulator.Calls.SequenceEqual(["sim_capture_begin","sim_capture_sample","sim_capture_sample"]),"One owned-instance check, then one worker sample per cycle; no mode change to restore");
            Check(Read(rows[1]).Contains("18446744073709551615") && Read(rows[1]).Contains("\"syncPoint\":null") && rows[1].GetProperty("cycle").GetInt64()==0,"Exact 64-bit value and explicit absent sync point are retained per signal row");
            var signal=status.GetProperty("plan").GetProperty("signals")[0];
            Check(signal.GetProperty("selection").ValueKind==JsonValueKind.Null && signal.GetProperty("metadata").GetProperty("datatype").GetString()=="Bool","Plan retains declared datatype metadata without inventing a Web selection");
            await f.Captures.Start(f.Id,request,CancellationToken.None);
            Check(f.Simulator.Calls.Count==3,"Repeating the same capture ID observes intent without touching the simulator");
            await Fails(()=>f.Captures.Start(f.Id,request with{IntervalMilliseconds=200},CancellationToken.None),"RuntimeCaptureRequestChanged");
        }
        await using(var f=new Fixture(root))
        {
            await f.Captures.Start(f.Id,Request(true),CancellationToken.None);var status=await f.Ended();var rows=f.Rows(status);
            Check(f.Simulator.Calls.SequenceEqual(["sim_capture_begin:sync","sim_capture_sample:sync","sim_capture_sample:sync","sim_capture_end:sync"]),"Cycle-synchronous capture enters the mode, steps once per sample and restores at its sample limit");
            Check(Read(rows[0]).Contains("\"syncPoint\":{\"count\":1,") && Read(rows[1]).Contains("\"syncPoint\":{\"count\":1,") && Read(rows[2]).Contains("\"syncPoint\":{\"count\":2,"),"Every signal of one cycle carries the same reached sync point");
            Check(status.GetProperty("localFailure").ValueKind==JsonValueKind.Null,"Successful restore leaves no local failure");
        }
        await using(var f=new Fixture(root))
        {
            var sampled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Simulator.Before=op=>{if(op=="sim_capture_sample")sampled.TrySetResult();return Task.CompletedTask;};
            await f.Captures.Start(f.Id,Request(true,100000,60),CancellationToken.None);await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var status=JsonSerializer.SerializeToElement(await f.Captures.Stop(f.Id,CancellationToken.None),BridgeProtocol.Json);
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="stopped" && f.Simulator.Calls[^1]=="sim_capture_end:sync","Stop restores the recorded operating mode");
        }
        foreach(var (code,retained) in new[]{("SimulationSyncPointTimeout","SimulationSyncPointTimeout"),("Timeout","PlcsimCaptureReadFailed")})
        {
            await using var f=new Fixture(root);
            f.Simulator.Before=op=>op=="sim_capture_sample"?throw new BridgeOperationException(code,"remote text"):Task.CompletedTask;
            await f.Captures.Start(f.Id,Request(true),CancellationToken.None);var status=await f.Ended();var rows=f.Rows(status);
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sessionUnavailable" && rows.GetArrayLength()==1 &&
                rows[0].GetProperty("errorCode").GetString()==retained && !rows[0].ToString().Contains("remote text"),"Worker failure ends the capture with an explicit bounded error row");
            Check(f.Simulator.Calls[^1]=="sim_capture_end:sync","Failure restores the recorded operating mode");
        }
        await using(var f=new Fixture(root))
        {
            f.Simulator.BadCounter=true;
            await f.Captures.Start(f.Id,Request(false),CancellationToken.None);var status=await f.Ended();var rows=f.Rows(status);
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit" && Read(rows[1]).Contains("\"status\":\"readFailed\"") &&
                Read(rows[1]).Contains("\"providerErrorCode\":\"SignalNameDoesNotExist\"") && Read(rows[1]).Contains("\"value\":null"),"A bad-quality signal is a retained provider failure, not a fabricated value");
        }
        await using(var f=new Fixture(root))
        {
            f.Simulator.Symbol="\"DB\".Other";
            await f.Captures.Start(f.Id,Request(false),CancellationToken.None);var status=await f.Ended();var rows=f.Rows(status);
            Check(rows.GetArrayLength()==1 && rows[0].GetProperty("errorCode").GetString()=="PlcsimResponseInvalid" &&
                status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sessionUnavailable","A response for another symbol is refused, never stored as a value");
        }
        await using(var f=new Fixture(root))
        {
            f.Simulator.Before=op=>op=="sim_capture_end"?throw new BridgeOperationException("WorkerDisconnected","lost"):Task.CompletedTask;
            await f.Captures.Start(f.Id,Request(true),CancellationToken.None);var status=await f.Ended();
            for(int wait=0;wait<2000 && status.GetProperty("localFailure").ValueKind==JsonValueKind.Null;wait++){await Task.Delay(5);status=JsonSerializer.SerializeToElement(await f.Captures.Status(f.Id,CancellationToken.None),BridgeProtocol.Json);}
            Check(status.GetProperty("localFailure").GetString()=="PlcsimOperatingModeRestoreFailed","An unconfirmed restore stays visible");
        }
        string shutdownRoot=Path.Combine(root,Guid.NewGuid().ToString("N")),shutdownId;
        await using(var f=new Fixture(root,shutdownRoot))
        {
            var sampled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Simulator.Before=op=>{if(op=="sim_capture_sample")sampled.TrySetResult();return Task.CompletedTask;};
            await f.Captures.Start(f.Id,Request(true,100000,60),CancellationToken.None);await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.DisposeAsync();shutdownId=f.Id;
            Check(f.Simulator.Calls[^1]=="sim_capture_end:sync","Host shutdown restores the recorded operating mode");
        }
        await using(var reopened=new Fixture(root,shutdownRoot))
        {
            var status=JsonSerializer.SerializeToElement(await reopened.Captures.Status(shutdownId,CancellationToken.None),BridgeProtocol.Json);
            Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="hostShutdown" && reopened.Simulator.Calls.Count==0,"Restart reopens the retained capture without resuming acquisition");
        }
        await using(var f=new Fixture(root))
        {
            f.Simulator.Before=op=>op=="sim_capture_begin"?throw new BridgeOperationException("SimulationOwnershipUnverified","not owned"):Task.CompletedTask;
            await Fails(()=>f.Captures.Start(f.Id,Request(true),CancellationToken.None),"SimulationOwnershipUnverified");
            Check(!Directory.Exists(Path.Combine(f.Root,"runtime-captures",f.Id)),"An unowned or mismatched instance is refused before capture intent exists");
            var plain=Request(false);
            await Fails(()=>f.Captures.Start(f.Id,plain with{Representation="raw"},CancellationToken.None),"PlcsimCaptureInvalid");
            await Fails(()=>f.Captures.Start(f.Id,plain with{Simulator=null},CancellationToken.None),"PlcsimCaptureInvalid");
            await Fails(()=>f.Captures.Start(f.Id,plain with{Source=null},CancellationToken.None),"PlcsimCaptureInvalid");
            await Fails(()=>f.Captures.Start(f.Id,plain with{Simulator=plain.Simulator! with{Signals=[new("\"DB\".Flag","bool","Int"),new("\"DB\".Counter","uint64","ULInt")]}},CancellationToken.None),"PlcsimCaptureInvalid");
            await Fails(()=>f.Captures.Start(f.Id,plain with{ReviewId=new string('a',64),HandoffRef="runtime-handoff:"+new string('b',32)+":"+new string('c',64)},CancellationToken.None),"PlcsimCaptureInvalid");
            await using var unwired=new RuntimeCaptures(new PhysicalRuntimeSessions(TimeProvider.System,[]),new("",Path.Combine(root,Guid.NewGuid().ToString("N")),null,5,false,"",null),TimeProvider.System);
            await Fails(()=>unwired.Start(Guid.NewGuid().ToString("N"),plain,CancellationToken.None),"RuntimeCaptureSourceUnavailable");
        }
        Console.WriteLine($"PASS PLCSIM capture source: {checks} checks; fake simulator worker, no native execution.");
        return checks;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly FakeSimulator Simulator=new();
        internal readonly RuntimeCaptures Captures;
        internal readonly string Id=Guid.NewGuid().ToString("N"),Root;
        private bool disposed;
        internal Fixture(string root,string? exact=null)
        {
            Root=exact??Path.Combine(root,Guid.NewGuid().ToString("N"));
            Captures=new(new PhysicalRuntimeSessions(TimeProvider.System,[]),new("",Root,null,5,false,"",null),TimeProvider.System,Simulator);
        }
        internal async Task<JsonElement> Ended()
        {
            async Task<JsonElement> Wait()
            {
                while(true)
                {
                    var status=JsonSerializer.SerializeToElement(await Captures.Status(Id,CancellationToken.None),BridgeProtocol.Json);
                    if(status.GetProperty("state").GetString()!="running")return status;
                    await Task.Delay(5);
                }
            }
            return await Wait().WaitAsync(TimeSpan.FromSeconds(10));
        }
        internal JsonElement Rows(JsonElement status)=>JsonSerializer.SerializeToElement(Captures.Page(status.GetProperty("snapshotRef").GetString()!,0,200),BridgeProtocol.Json).GetProperty("rows");
        public async ValueTask DisposeAsync(){if(!disposed){disposed=true;await Captures.DisposeAsync();}}
    }

    /// <summary>Answers like the simulator worker's sim_capture_* operations.</summary>
    internal sealed class FakeSimulator : IBridgeClient
    {
        internal readonly List<string> Calls=[];
        internal Func<string,Task>? Before;
        internal string? Symbol;
        internal bool BadCounter;
        private long samples;
        public async Task<JsonElement> CallAsync(string operation,object? arguments=null,CancellationToken cancellationToken=default)
        {
            var args=JsonSerializer.SerializeToElement(arguments,BridgeProtocol.Json);bool sync=args.GetProperty("cycleSynchronous").GetBoolean();
            lock(Calls)Calls.Add(operation+(sync?":sync":""));
            if(Before is not null)await Before(operation);
            if(operation!="sim_capture_sample")return JsonSerializer.SerializeToElement(new{instanceId=7},BridgeProtocol.Json);
            long n=Interlocked.Increment(ref samples);string now=DateTime.UtcNow.ToString("O");var values=new JsonArray();
            foreach(var signal in args.GetProperty("signals").EnumerateArray())
            {
                string type=signal.GetProperty("type").GetString()!;
                var value=new JsonObject{["symbol"]=Symbol??signal.GetProperty("symbol").GetString(),["type"]=type,["observedAtUtc"]=now,["completedAtUtc"]=now};
                if(type=="uint64" && BadCounter){value["quality"]="bad";value["error"]="remote text";value["errorCode"]="SignalNameDoesNotExist";}
                else{value["quality"]="good";value["value"]=type=="bool"?JsonValue.Create(n%2==0):JsonValue.Create(ulong.MaxValue);}
                values.Add(value);
            }
            return JsonSerializer.SerializeToElement(new JsonObject{["instanceId"]=args.GetProperty("instanceId").GetInt32(),["instanceName"]=args.GetProperty("instanceName").GetString(),
                ["syncPoint"]=sync?new JsonObject{["count"]=n,["timeSinceSameSyncPointNanoseconds"]=1000000,["timeSinceAnySyncPointNanoseconds"]=1000000}:null,["values"]=values},BridgeProtocol.Json);
        }
    }
}
