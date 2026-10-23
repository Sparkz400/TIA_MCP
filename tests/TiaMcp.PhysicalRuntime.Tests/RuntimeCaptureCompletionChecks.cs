using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeCaptureCompletionChecks
{
    internal static async Task<int> Run(string root)
    {
        int checks=0;var cases=new List<object>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
        await using var f=new RuntimePollingChecks.PollFixture(root);
        f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(10);return Task.CompletedTask;};
        for(int i=0;i<8;i++)
        {
            f.Id=Guid.NewGuid().ToString("N");
            var request=new RuntimeCaptureRequest(f.Attempt,f.Sessions.Signals.Select(s=>s.SelectionId).ToArray(),100,1,100,64L<<20);
            await f.Captures.Start(f.Id,request,CancellationToken.None);
            var deadline=await f.Clock.NextTimer();_=await f.Clock.NextTimer();
            string directory=Path.Combine(f.Root,"runtime-captures",f.Id),head=Path.Combine(directory,"head.json");
            byte[] original=File.ReadAllBytes(head);
            // Collide only with the next segment; the committed head must remain readable.
            Directory.CreateDirectory(Path.Combine(directory,"000001.segment"));
            f.Clock.Advance(1190,fire:false);deadline.Fire();
            var status=await f.Ended();
            Check(status.GetProperty("state").GetString()=="interrupted" && status.GetProperty("localFailure").GetString()=="RuntimeCaptureStorageFailure","Final-gap publication failure stays explicit");
            Check(File.ReadAllBytes(head).SequenceEqual(original),"Failed final-gap publication preserves exact committed head");
            Check(status.GetProperty("uncommittedDataUnknown").GetBoolean() && f.Page(status).GetProperty("totalRows").GetInt32()==0,"Unpublished final reads and gaps cannot become committed evidence");
            Check(f.Clock.ActiveTimers==0,"Failed completion disposes deadline and delay timers");
            _=await f.Captures.Start(f.Id,request,CancellationToken.None);
            Check(f.Sessions.Reads.Count==i+1,"Reopening failed capture never replays acquisition");
            cases.Add(new{captureId=f.Id,snapshotRef=status.GetProperty("snapshotRef").GetString(),failure="finalGapSegmentCollision"});
        }
        f.Id=Guid.NewGuid().ToString("N");await f.Start(maximumSamples:1);
        var healthy=await f.Ended();
        Check(healthy.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit" && f.Page(healthy).GetProperty("totalRows").GetInt32()==1,"A new capture succeeds after eight failed publishers release capacity");
        Check(f.Sessions.Reads.Count==9 && f.Clock.ActiveTimers==0,"Fresh capture performs exactly its requested read and disposes timers");
        long bytes=Directory.EnumerateFiles(f.Root,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length);
        Check(bytes<1<<20,"Repeated tiny failures do not require a full disk or consume the reserved store capacity");
        File.WriteAllText(Path.Combine(root,"completion-failure-checks.json"),JsonSerializer.Serialize(new{checks,cases,actualBytes=bytes,productionPoller=true,sessionBoundary="testDouble",actualFilesystemCollision=true,nativeExecuted=false},BridgeProtocol.Json));
        Console.WriteLine($"PASS runtime completion failures: {checks} checks; eight actual segment collisions, no native execution.");
        return checks;
    }
}
