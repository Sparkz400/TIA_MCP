using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

// Bounded load against the linked production poller/store. Session and clock are explicit test doubles.
internal static class RuntimeCapturePressureChecks
{
    internal static async Task<int> Run(string root)
    {
        Directory.CreateDirectory(root);
        int checks=0;var passed=new List<string>();var cases=new List<object>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;passed.Add(label);}
        var elapsed=Stopwatch.StartNew();
        await using(var f=new RuntimePollingChecks.PollFixture(root,64))
        {
            const int attempts=32768;
            f.Clock.AdvanceShortDelays=true;
            var observations=new List<object>();
            f.Sessions.BeforeRead=(number,_)=>{
                long started=f.Clock.GetTimestamp();int delay=number%997==0?351:1;
                f.Clock.Advance(delay);
                string? error=number%17==0?"RuntimeTimeout":number%29==0?"RuntimeTransportFailure":null;
                observations.Add(new{number,started,completed=f.Clock.GetTimestamp(),error,responseMember=error is null?Response(number):null});
                if(error is not null)throw new BridgeException(error,"Explicit test-session pressure.");
                return Task.CompletedTask;
            };
            f.Sessions.ResponseMember=Response;
            var request=new RuntimeCaptureRequest(f.Attempt,f.Sessions.Signals.Select(s=>s.SelectionId).ToArray(),100,3600,attempts,64L<<20);
            await f.Captures.Start(f.Id,request,CancellationToken.None);
            var status=await f.Ended(90);var progress=status.GetProperty("recordedProgress");
            Check(progress.GetProperty("terminalReason").GetString()=="sampleLimit" && progress.GetProperty("readAttempts").GetInt64()==attempts,"Mixed 64-signal load retains all 32768 attempted reads");
            Check(progress.GetProperty("skippedPolls").GetInt64()>90 && progress.GetProperty("droppedReadResults").GetInt64()==0,"Repeated slow reads retain skipped slots without dropping accepted results");
            Check(f.Clock.ActiveTimers==0 && status.GetProperty("localFailure").ValueKind==JsonValueKind.Null,"Sustained completion releases timers without a storage error");
            string reference=status.GetProperty("snapshotRef").GetString()!;
            var exported=JsonSerializer.SerializeToElement(await f.Captures.Export(reference,"json",CancellationToken.None),BridgeProtocol.Json);
            string path=exported.GetProperty("artifacts")[0].GetProperty("path").GetString()!;
            using var document=JsonDocument.Parse(File.ReadAllBytes(path));
            var rows=document.RootElement.GetProperty("rows").EnumerateArray().Where(r=>r.GetProperty("kind").GetString()=="read").ToArray();
            Check(rows.Length==attempts && rows.Select(r=>r.GetProperty("selectionId").GetString()).SequenceEqual(f.Sessions.Reads.Select(r=>r.SelectionId)),"All selected signals retain acquisition order through export");
            Check(rows.Count(r=>r.GetProperty("errorCode").GetString()=="RuntimeTimeout")==Enumerable.Range(1,attempts).Count(n=>n%17==0),"Timeouts retain explicit error rows");
            Check(rows.Count(r=>r.GetProperty("errorCode").GetString()=="RuntimeTransportFailure")==Enumerable.Range(1,attempts).Count(n=>n%17!=0 && n%29==0),"Transport failures retain distinct error rows");
            Check(rows.Where(r=>r.GetProperty("readResultJson").ValueKind!=JsonValueKind.Null).Any(r=>r.GetProperty("readResultJson").GetString()!.Contains("18446744073709551615")),"Large exact integers survive mixed load");
            var before=Directory.GetFiles(Path.Combine(f.Root,"runtime-captures",f.Id),"*.segment").ToDictionary(p=>p,File.ReadAllBytes);
            _=await f.Captures.Start(f.Id,request,CancellationToken.None);
            Check(f.Sessions.Reads.Count==attempts && before.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Repeated completed intent neither replays acquisition nor rewrites segments");
            var ledgerPath=Path.Combine(root,"mixed-observations.json");
            File.WriteAllText(ledgerPath,JsonSerializer.Serialize(observations,BridgeProtocol.Json));
            File.WriteAllText(Path.Combine(root,"mixed-status.json"),status.GetRawText());
            cases.Add(new{kind="mixed",captureRoot=f.Root,captureId=f.Id,snapshotRef=reference,exportPath=path,ledgerPath,attempts,signalCount=64});
            f.Id=Guid.NewGuid().ToString("N");
            await f.Start(maximumSamples:1);
            var healthy=await f.Ended();
            Check(healthy.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit" && f.Clock.ActiveTimers==0,"A fresh capture succeeds after sustained mixed load");
        }
        // Exercise actual store admission, which counts both reservations and retained bytes.
        using(var store=new RuntimeCaptureStore(Path.Combine(root,"reservations")))
        {
            var context=WebRuntimeProtocol.Context("https://pressure.example",Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new string('a',64));
            var metadata=JsonSerializer.SerializeToElement(new{name="Value",datatype="bool"});
            var signal=new RuntimeCaptureSignal(Guid.NewGuid().ToString("N"),WebRuntimeProtocol.Symbol(context,"Value","simple",metadata),metadata);
            RuntimeCapturePlan Plan()=>new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,new(context.ConnectionId,[signal.SelectionId],100,1,1,64L<<20),[signal]);
            var writers=Enumerable.Range(0,7).Select(_=>store.Create(Plan())).ToArray();
            var refused=Plan();
            try{store.Create(refused);throw new Exception("Expected reservation capacity refusal");}
            catch(BridgeException e)when(e.Code=="RuntimeCaptureCapacity"){checks++;passed.Add("Seven 64 MiB reservations plus stored bytes refuse the eighth before intent");}
            Check(!store.Exists(refused.CaptureId),"Reservation refusal creates no retained intent");
            writers[0].Finish("stopped",0);
            var replacement=Plan();var recovered=store.Create(replacement);
            recovered.Finish("stopped",0);
            foreach(var writer in writers)writer.Finish("stopped",0);
            Check(JsonSerializer.SerializeToElement(store.Status(replacement.CaptureId,false),BridgeProtocol.Json).GetProperty("state").GetString()=="ended","Finishing a reserved writer immediately admits a replacement without restarting the store");
            cases.Add(new{kind="reservationCapacity",captureRoot=Path.Combine(root,"reservations"),reservedWriters=7,recoveredCaptureId=replacement.CaptureId});
        }
        // Saturate the four-worker boundary repeatedly, with real task cancellation and one shared store.
        await using(var f=new RuntimePollingChecks.PollFixture(root))
        {
            var entered=Channel.CreateUnbounded<int>();
            f.Sessions.BeforeRead=async(number,token)=>{entered.Writer.TryWrite(number);await Task.Delay(Timeout.Infinite,token);};
            var original=f.Sessions.Signals[0];
            f.Sessions.Signals=Enumerable.Range(0,5).Select(_=>new RuntimeCaptureSignal(Guid.NewGuid().ToString("N"),
                WebRuntimeProtocol.Symbol(original.Selection!.Context with{ConnectionId=Guid.NewGuid().ToString("N"),Epoch=Guid.NewGuid().ToString("N")},original.Selection.Symbol,"simple",original.Metadata),original.Metadata)).ToArray();
            RuntimeCaptureRequest Request(int index)=>new(f.Sessions.Signals[index].Selection!.Context.ConnectionId,[f.Sessions.Signals[index].SelectionId],100,3600,10,64L<<20);
            var retained=new List<string>();
            for(int wave=0;wave<8;wave++)
            {
                var active=new List<string>();
                for(int i=0;i<4;i++)
                {
                    string id=Guid.NewGuid().ToString("N");active.Add(id);retained.Add(id);
                    await f.Captures.Start(id,Request(i),CancellationToken.None);
                    _=await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                }
                string refused=Guid.NewGuid().ToString("N");
                try{await f.Captures.Start(refused,Request(4),CancellationToken.None);throw new Exception("Expected active worker refusal");}
                catch(BridgeException e)when(e.Code=="RuntimeCaptureSessionBusy"){checks++;passed.Add("Worker capacity refusal in wave "+wave);}
                Check(!Directory.Exists(Path.Combine(f.Root,"runtime-captures",refused)),"Worker exhaustion creates no capture intent in wave "+wave);
                await Task.WhenAll(active.Select(id=>f.Captures.Stop(id,CancellationToken.None)));
                foreach(string id in active)
                {
                    var status=JsonSerializer.SerializeToElement(await f.Captures.Status(id,CancellationToken.None),BridgeProtocol.Json);
                    var row=JsonSerializer.SerializeToElement(f.Captures.Page(status.GetProperty("snapshotRef").GetString()!,0,1),BridgeProtocol.Json).GetProperty("rows")[0];
                    Check(status.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="stopped" && row.GetProperty("errorCode").GetString()=="RuntimeCancelled","Concurrent stop retains cancellation for "+id);
                }
                Check(f.Clock.ActiveTimers==0,"All worker timers released after wave "+wave);
            }
            Check(f.Sessions.Reads.Count==32,"Worker churn performs exactly one in-flight read for each admitted capture");
            f.Sessions.BeforeRead=null;f.Id=Guid.NewGuid().ToString("N");
            await f.Captures.Start(f.Id,Request(4) with{MaximumSamples=1},CancellationToken.None);
            var healthy=await f.Ended();
            Check(healthy.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit" && f.Sessions.Reads.Count==33,"Healthy acquisition succeeds after eight saturation/cancellation waves");
            await f.DisposeAsync();
            using var reopened=new RuntimeCaptureStore(f.Root);
            Check(retained.All(id=>JsonSerializer.SerializeToElement(reopened.Status(id,false),BridgeProtocol.Json).GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="stopped"),"All 32 cancelled captures reopen with their committed terminal state");
            cases.Add(new{kind="workerChurn",captureRoot=f.Root,captureIds=retained,healthyCaptureId=f.Id,waves=8,reads=33});
        }
        File.WriteAllText(Path.Combine(root,"pressure-checks.json"),JsonSerializer.Serialize(new{schemaVersion="runtime-pressure-checks.v1",checks,passed,cases,elapsedSeconds=elapsed.Elapsed.TotalSeconds,productionPoller=true,sessionBoundary="testDouble",clockBoundary="manual",actualDiskFull=false,nativeExecuted=false},BridgeProtocol.Json));
        Console.WriteLine($"PASS runtime pressure: {checks} checks; 32768 mixed reads, eight four-worker saturation waves, reservation exhaustion; no native execution.");
        return checks;
    }

    private static string Response(int number)=>number%31==0?"\"error\":{\"code\":201,\"message\":\"not retained\"}":
        "\"result\":"+((number%6) switch{0=>"18446744073709551615",1=>"-9223372036854775808",2=>"1.234567890123456789e-100",3=>"{\"nested\":[true,null,\"text\",42]}",4=>"\""+new string('x',1024)+"\"",_=>"false"});
}
