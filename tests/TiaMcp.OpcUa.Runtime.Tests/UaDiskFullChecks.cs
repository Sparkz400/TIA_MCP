using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

/// <summary>Production subscription acquisition on an exhausted owned volume; the session store and PKI stay outside it.</summary>
internal static class UaDiskFullChecks
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
        using(var marker=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(volume,"runtime-pressure-owner.json"))))
            if(marker.RootElement.GetProperty("token").GetString()!=token || marker.RootElement.GetProperty("sizeBytes").GetInt64()!=64L<<20)
                throw new Exception("Owned volume marker does not match.");
        string evidence=Path.GetFullPath(evidenceRoot);
        if(evidence.StartsWith(volume,StringComparison.OrdinalIgnoreCase) || Directory.Exists(evidence))
            throw new Exception("Evidence must be new and retained outside the volume being exhausted.");
        Directory.CreateDirectory(evidence);
        int checks=0;var passed=new List<string>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks++;passed.Add(label);}
        static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,BridgeProtocol.Json);
        string root=Path.Combine(volume,"ua-subscription-pressure-"+token),fill=Path.Combine(volume,"ua-fill-"+token+".bin");
        if(Directory.Exists(root) || File.Exists(fill))throw new Exception("Pressure paths must be new.");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var server=new FixtureServer();await server.StartFixture(port,Path.Combine(evidence,"pki"));
        try
        {
            await using var sessions=new OpcUaRuntimeSessionStore(Path.Combine(evidence,"sessions"));
            string attempt=Guid.NewGuid().ToString("N");
            Check((await sessions.Connect(attempt,server.Selection)).State=="connected","Owned encrypted fixture session outside the bounded volume");
            var subscriptions=new OpcUaRuntimeSubscriptions(sessions,root);
            string capture=Guid.NewGuid().ToString("N");
            // Keep-alive Publish responses arrive every 100 ms, so publication is attempted at least once per second.
            await subscriptions.Start(capture,new(attempt,[FixtureServer.Node("bool")],DurationSeconds:60,KeepAliveCount:1),default);
            var clock=Stopwatch.StartNew();JsonElement before;
            do {before=Json(await subscriptions.Status(capture,default));await Task.Delay(50);}
            while(before.GetProperty("recordedProgress").GetProperty("rows").GetInt64()<2 && clock.Elapsed<TimeSpan.FromSeconds(20));
            Check(before.GetProperty("state").GetString()=="running" && before.GetProperty("recordedProgress").GetProperty("rows").GetInt64()>=2,"Committed prefix exists before exhaustion");
            string capturePath=Path.Combine(root,"runtime-ua-subscriptions",capture);
            var committed=Directory.GetFiles(capturePath,"*.segment").ToDictionary(p=>p,File.ReadAllBytes);
            int diskFullCode=0;long fillBytes=0;JsonElement failed;
            try
            {
                using(var output=new FileStream(fill,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                {
                    var buffer=new byte[1<<20];RandomNumberGenerator.Fill(buffer);
                    foreach(int chunk in new[]{buffer.Length,4096})
                        while(output.Position+chunk<=64L<<20)
                        {
                            try{output.Write(buffer,0,chunk);output.Flush(true);}
                            catch(IOException e)when((e.HResult&0xffff) is 112 or 39){diskFullCode=e.HResult;break;}
                        }
                    fillBytes=output.Length;
                }
                Check(diskFullCode!=0 && drive.AvailableFreeSpace<4096,"Actual OS disk-full write failure on bounded owned volume");
                clock.Restart();
                do {failed=Json(await subscriptions.Status(capture,default));await Task.Delay(50);}
                while(failed.GetProperty("state").GetString()=="running" && clock.Elapsed<TimeSpan.FromSeconds(30));
                // Stop awaits the worker, including its finally that releases the session acquisition.
                failed=Json(await subscriptions.Stop(capture,default));
                Check(failed.GetProperty("state").GetString()=="interrupted" && failed.GetProperty("localFailure").GetString()=="UaSubscriptionStorageFailure","Exhausted volume interrupts subscription publication");
                Check(committed.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) &&
                    failed.GetProperty("recordedProgress").GetProperty("rows").GetInt64()>=before.GetProperty("recordedProgress").GetProperty("rows").GetInt64(),"Full-volume failure preserves every committed segment byte");
                Check(Json(subscriptions.Page(failed.GetProperty("snapshotRef").GetString()!,0,1)).GetProperty("totalRows").GetInt64()==failed.GetProperty("recordedProgress").GetProperty("rows").GetInt64(),"Uncommitted envelopes are not adopted");
                File.WriteAllText(Path.Combine(evidence,"disk-full-status.json"),failed.GetRawText());
            }
            finally
            {
                // The sole filler path was constructed above on the validated owned bounded volume.
                if(File.Exists(fill))File.Delete(fill);
            }
            Check((await sessions.Status(attempt)).State=="connected","Storage failure leaves the owned session connected");
            string? busy=null;try{await sessions.Read(attempt,[FixtureServer.Node("bool")]);}catch(BridgeException e){busy=e.Code;}
            Check(busy is null,"Storage failure released the session acquisition (actual "+busy+")");
            string reference=failed.GetProperty("snapshotRef").GetString()!;
            await subscriptions.DisposeAsync();
            subscriptions=new OpcUaRuntimeSubscriptions(sessions,root);
            var reopened=Json(await subscriptions.Status(capture,default));
            Check(reopened.GetProperty("snapshotRef").GetString()==reference && reopened.GetProperty("state").GetString()=="interrupted" &&
                reopened.GetProperty("localFailure").ValueKind==JsonValueKind.Null,"Fresh owner keeps the exact head without adopting the tail or inventing failure memory");
            string healthy=Guid.NewGuid().ToString("N");
            await subscriptions.Start(healthy,new(attempt,[FixtureServer.Node("bool")],DurationSeconds:1,KeepAliveCount:1),default);
            clock.Restart();JsonElement next;
            do {next=Json(await subscriptions.Status(healthy,default));await Task.Delay(50);}
            while(next.GetProperty("state").GetString()=="running" && clock.Elapsed<TimeSpan.FromSeconds(20));
            Check(next.GetProperty("recordedProgress").GetProperty("terminalReason").GetString()=="durationLimit","Next acquisition on the same session succeeds after space is released");
            await subscriptions.DisposeAsync();
            await sessions.Disconnect(attempt);
            string retained=Path.Combine(evidence,"retained");
            foreach(string source in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
            {
                string target=Path.Combine(retained,Path.GetRelativePath(root,source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(source,target,false);
            }
            File.WriteAllText(Path.Combine(evidence,"disk-full-checks.json"),JsonSerializer.Serialize(new{checks,passed,volumeSizeBytes=drive.TotalSize,fillBytes,diskFullHResult=$"0x{diskFullCode:x8}",snapshotRef=reference,captureRoot=retained,actualDiskFull=true,productionAcquisition="opcUaSubscription",server="loopbackFixture",nativeExecuted=false},BridgeProtocol.Json));
            Console.WriteLine($"PASS actual bounded UA subscription disk-full: {checks} checks.");
        }
        finally {await server.StopFixture();}
    }
}
