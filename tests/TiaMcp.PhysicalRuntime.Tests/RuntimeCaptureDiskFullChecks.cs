using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeCaptureDiskFullChecks
{
    // Invoked only by Test-RuntimeCaptureDiskFull.ps1 after it creates and validates its own 64 MiB VHD.
    internal static async Task Run(string volumeRoot,string token,string evidenceRoot)
    {
        if(!OperatingSystem.IsWindows() || !Guid.TryParseExact(token,"N",out _))throw new Exception("Bounded Windows volume and owner token required.");
        string volume=Path.GetFullPath(volumeRoot);
        if(!string.Equals(volume,Path.GetPathRoot(volume),StringComparison.OrdinalIgnoreCase) ||
            string.Equals(volume,Path.GetPathRoot(Environment.SystemDirectory),StringComparison.OrdinalIgnoreCase))
            throw new Exception("Only a dedicated non-system drive root is accepted.");
        var drive=new DriveInfo(volume);
        if(drive.TotalSize>64L<<20 || drive.TotalSize<32L<<20 || drive.VolumeLabel!="TIA_"+token[..20])
            throw new Exception("Volume label or bounded capacity does not match the owned fixture.");
        using var marker=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(volume,"runtime-pressure-owner.json")));
        if(marker.RootElement.GetProperty("token").GetString()!=token || marker.RootElement.GetProperty("sizeBytes").GetInt64()!=64L<<20)
            throw new Exception("Owned volume marker does not match.");
        if(Path.GetFullPath(evidenceRoot).StartsWith(volume,StringComparison.OrdinalIgnoreCase))
            throw new Exception("Evidence must be retained outside the volume being exhausted.");
        Directory.CreateDirectory(evidenceRoot);
        int checks=0;var passed=new List<string>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;passed.Add(label);}
        string fixtureRoot=Path.Combine(volume,"capture-pressure-"+token),fill=Path.Combine(volume,"fill-"+token+".bin");
        if(Directory.Exists(fixtureRoot) || File.Exists(fill))throw new Exception("Pressure paths must be new.");
        await using var f=new RuntimePollingChecks.PollFixture(fixtureRoot);
        f.Sessions.BeforeRead=(_,_)=>{f.Clock.Advance(1001);return Task.CompletedTask;};
        await f.Start(duration:30);
        _=await f.Clock.NextTimer();var delay=await f.Clock.NextTimer();
        var before=JsonSerializer.SerializeToElement(await f.Captures.Status(f.Id,CancellationToken.None),BridgeProtocol.Json);
        string reference=before.GetProperty("snapshotRef").GetString()!;
        string capturePath=Path.Combine(f.Root,"runtime-captures",f.Id);
        var committed=Directory.GetFiles(capturePath).ToDictionary(p=>Path.GetFileName(p)!,p=>File.ReadAllBytes(p));
        int diskFullCode=0;long fillBytes=0;
        try
        {
            using(var output=new FileStream(fill,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {
                var buffer=new byte[1<<20];RandomNumberGenerator.Fill(buffer);
                // A second bound prevents accidental expansion even if a caller supplied a wrong filesystem.
                foreach(int chunk in new[]{buffer.Length,4096})
                {
                    while(output.Position+chunk<=64L<<20)
                    {
                        try{output.Write(buffer,0,chunk);output.Flush(true);}
                        catch(IOException e)when((e.HResult&0xffff) is 112 or 39){diskFullCode=e.HResult;break;}
                    }
                }
                fillBytes=output.Length;
            }
            Check(diskFullCode!=0 && drive.AvailableFreeSpace<4096,"Actual OS disk-full write failure on bounded owned volume");
            f.Clock.Advance((int)(delay.Due-f.Clock.GetTimestamp()));
            var failed=await f.Ended();
            Check(failed.GetProperty("state").GetString()=="interrupted" && failed.GetProperty("localFailure").GetString()=="RuntimeCaptureStorageFailure","Actual exhausted volume interrupts production publication");
            Check(failed.GetProperty("snapshotRef").GetString()==reference && committed.All(p=>File.ReadAllBytes(Path.Combine(capturePath,p.Key!)).SequenceEqual(p.Value)),"Full-volume failure preserves every committed byte and exact head");
            Check(failed.GetProperty("uncommittedDataUnknown").GetBoolean() && f.Page(failed).GetProperty("totalRows").GetInt32()==before.GetProperty("recordedProgress").GetProperty("rows").GetInt32(),"Uncommitted full-volume reads and gaps are not adopted");
            Check(f.Clock.ActiveTimers==0,"Disk-full completion releases all polling timers");
            File.WriteAllText(Path.Combine(evidenceRoot,"disk-full-status.json"),failed.GetRawText());
        }
        finally
        {
            // The sole filler path was constructed above on the validated owned bounded volume.
            if(File.Exists(fill))File.Delete(fill);
        }
        int calls=f.Sessions.Reads.Count;
        _=await f.Start(duration:30);
        Check(f.Sessions.Reads.Count==calls,"Freeing space and repeating intent does not resume capture");
        var exported=JsonSerializer.SerializeToElement(await f.Captures.Export(reference,"json",CancellationToken.None),BridgeProtocol.Json);
        File.Copy(exported.GetProperty("artifacts")[0].GetProperty("path").GetString()!,Path.Combine(evidenceRoot,"disk-full-prefix-export.json"),false);
        Check(f.Sessions.Reads.Count==calls,"Export after freeing space uses only committed retained data");
        f.Id=Guid.NewGuid().ToString("N");await f.Start(maximumSamples:1,duration:30);
        var healthy=await f.Ended();
        Check(healthy.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="sampleLimit","Healthy capture succeeds on the same volume after capacity is released");
        await f.DisposeAsync();
        using(var reopened=new RuntimeCaptureStore(f.Root))
        {
            string id=before.GetProperty("captureId").GetString()!;
            var status=JsonSerializer.SerializeToElement(reopened.Status(id,false),BridgeProtocol.Json);
            Check(status.GetProperty("snapshotRef").GetString()==reference && status.GetProperty("state").GetString()=="interrupted" && status.GetProperty("localFailure").ValueKind==JsonValueKind.Null,"Fresh store retains original prefix without inventing previous process failure memory");
        }
        string retained=Path.Combine(evidenceRoot,"retained");
        foreach(string source in Directory.EnumerateFiles(f.Root,"*",SearchOption.AllDirectories))
        {
            string target=Path.Combine(retained,Path.GetRelativePath(f.Root,source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(source,target,false);
        }
        File.WriteAllText(Path.Combine(evidenceRoot,"disk-full-checks.json"),JsonSerializer.Serialize(new{checks,passed,volumeSizeBytes=drive.TotalSize,fillBytes,diskFullHResult=$"0x{diskFullCode:x8}",snapshotRef=reference,captureRoot=retained,actualDiskFull=true,productionPoller=true,sessionBoundary="testDouble",nativeExecuted=false},BridgeProtocol.Json));
        Console.WriteLine($"PASS actual bounded disk-full: {checks} checks.");
    }
}
