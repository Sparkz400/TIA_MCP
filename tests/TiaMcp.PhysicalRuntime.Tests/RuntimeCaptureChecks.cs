using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeCaptureChecks
{
    internal static int Run(string root, WebRuntimeReadResult result, JsonElement metadata)
    {
        int checks=0;
        void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
        void Fails(Action action,string code){try{action();}catch(BridgeException e)when(e.Code==code){checks++;return;}throw new Exception("Expected "+code);}
        JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,BridgeProtocol.Json);
        string id=Guid.NewGuid().ToString("N"),sid=Guid.NewGuid().ToString("N");
        var request=new RuntimeCaptureRequest(result.Selection.Context.ConnectionId,[sid],100,10,20,1<<20);
        var plan=new RuntimeCapturePlan(id,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,request,[new(sid,result.Selection,metadata)]);
        string exact=JsonSerializer.Serialize(result,BridgeProtocol.Json),oldRef,finalRef;
        RuntimeCaptureRow Row(long n,double elapsed)=>new(n,"read",sid,0,0,elapsed,DateTimeOffset.UtcNow,exact,null,0);
        using(var store=new RuntimeCaptureStore(root))
        {
            Fails(()=>RuntimeCaptureStore.Validate(request with{SelectionIds=[sid,sid]}),"RuntimeCaptureSelectionInvalid");
            Fails(()=>RuntimeCaptureStore.Validate(request with{IntervalMilliseconds=99}),"RuntimeCaptureLimit");
            Fails(()=>store.Create(plan with{Signals=[new(sid,result.Selection,Json(new{name="changed",datatype="ulint"}))]}),"RuntimeCaptureSelectionInvalid");
            var writer=store.Create(plan);oldRef=writer.Reference;
            Check(Json(store.Status(id,true)).GetProperty("state").GetString()=="running","Durable intent before first sample");
            Fails(()=>writer.Append(Row(0,1) with{ScheduledMilliseconds=1}),"RuntimeCaptureIntegrityFailure");
            Fails(()=>writer.Append(Row(0,1) with{ReadResultJson=exact.Replace("\"nativeQualified\":false","\"nativeQualified\":true")}),"RuntimeCaptureIntegrityFailure");
            writer.Append(Row(0,1001));oldRef=writer.Reference;
            writer.Append(new(1,"gap",null,0,0,1002,DateTimeOffset.UtcNow,null,null,9));
            writer.Append(Row(2,1003) with{ReadResultJson=null,ErrorCode="RuntimeTimeout"});writer.Finish("stopped",1004);finalRef=writer.Reference;
            Check(Json(store.Page(oldRef)).GetProperty("totalRows").GetInt64()==1,"Pinned prefix stays immutable");
            var page=Json(store.Page(finalRef,0,2));Check(page.GetProperty("nextOffset").GetInt32()==2,"Stable page cursor");
            Check(page.GetProperty("rows")[0].GetProperty("readResultJson").GetString()==exact,"Exact UInt64 JSON survives storage");
            var status=Json(store.Status(id,false));Check(status.GetProperty("state").GetString()=="ended" && status.GetProperty("recordedProgress").GetProperty("skippedPolls").GetInt64()==9,"Final progress retains gaps");
            var export=Json(store.Export(finalRef,"csv",false));Check(export.GetProperty("artifacts").GetArrayLength()==2,"CSV always has full JSON companion");
            string jsonPath=export.GetProperty("artifacts")[0].GetProperty("path").GetString()!;
            using(var doc=JsonDocument.Parse(File.ReadAllText(jsonPath)))Check(doc.RootElement.GetProperty("rows")[0].GetProperty("readResultJson").GetString()==exact,"Export preserves exact JSON text");
            string crashId=Guid.NewGuid().ToString("N");store.Create(plan with{CaptureId=crashId});
            Check(Json(store.Status(crashId,false)).GetProperty("state").GetString()=="interrupted","Orphaned acquisition is never resumed");
            string segment=Path.Combine(root,"runtime-captures",id,"000001.segment");byte[] original=File.ReadAllBytes(segment);
            File.WriteAllText(segment,"{}");Fails(()=>store.Page(finalRef),"RuntimeCaptureIntegrityFailure");File.WriteAllBytes(segment,original);
            File.WriteAllText(Path.Combine(root,"runtime-captures",id,"000003.segment"),"orphan");
            Check(Json(store.Status(id,false)).GetProperty("uncommittedTailPresent").GetBoolean(),"Orphan tail reported without adoption");
            Fails(()=>store.Page(finalRef,0,201),"RuntimeCapturePageLimit");
            Fails(()=>store.Page("capture:../bad:0:"+new string('0',64)),"RuntimeCaptureIdentityInvalid");
            string blockedId=Guid.NewGuid().ToString("N");var blocked=store.Create(plan with{CaptureId=blockedId});
            File.WriteAllText(Path.Combine(root,"runtime-captures",blockedId,"000001.segment"),"occupied");
            blocked.Append(Row(0,1));
            try{blocked.Finish("stopped",2);throw new Exception("Expected publication failure");}catch(IOException){checks++;}
            var partial=Json(store.Status(blockedId,false));
            Check(partial.GetProperty("state").GetString()=="interrupted" && partial.GetProperty("recordedProgress").GetProperty("rows").GetInt32()==0 && partial.GetProperty("uncommittedTailPresent").GetBoolean(),"Failed publication retains old prefix and reports unknown tail");
            if (OperatingSystem.IsWindows())
            {
                string lockedId=Guid.NewGuid().ToString("N");var locked=store.Create(plan with{CaptureId=lockedId});
                locked.Append(Row(0,1001));string committed=locked.Reference;
                string headPath=Path.Combine(root,"runtime-captures",lockedId,"head.json");
                byte[] headBefore=File.ReadAllBytes(headPath);
                locked.Append(Row(1,1002));
                using(var hold=new FileStream(headPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    try{locked.Finish("stopped",1003);throw new Exception("Expected head replacement sharing failure");}catch(IOException){checks++;}
                    Check(File.ReadAllBytes(headPath).SequenceEqual(headBefore),"Sharing failure preserves committed head bytes");
                    var retained=Json(store.Status(lockedId,false));
                    Check(retained.GetProperty("snapshotRef").GetString()==committed && retained.GetProperty("recordedProgress").GetProperty("rows").GetInt32()==1 && retained.GetProperty("uncommittedTailPresent").GetBoolean(),"Head replacement failure does not adopt the already-written second segment");
                }
                locked.Finish("stopped",1004);
                Check(store.Head(lockedId)==committed && Json(store.Page(committed)).GetProperty("totalRows").GetInt32()==1,"Releasing the lock does not replay failed publication");
                Check(File.Exists(Path.Combine(root,"runtime-captures",lockedId,"000002.segment")),"Failed publication retains the orphan segment for explicit recovery");
            }
            string changedId=Guid.NewGuid().ToString("N");var changed=store.Create(plan with{CaptureId=changedId});string initial=changed.Reference;
            changed.Append(Row(0,1001));string published=changed.Reference;
            string changedHead=Path.Combine(root,"runtime-captures",changedId,"head.json");File.WriteAllText(changedHead,JsonSerializer.Serialize(initial));
            changed.Append(Row(1,1002));Fails(()=>changed.Finish("stopped",1003),"RuntimeCaptureRecordChanged");
            Check(store.Head(changedId)==initial && Json(store.Page(published)).GetProperty("totalRows").GetInt32()==1,"Changed head is not overwritten and the caller-pinned prefix remains readable");
            string concurrentId=Guid.NewGuid().ToString("N");var concurrent=store.Create(plan with{CaptureId=concurrentId,Request=request with{MaximumSamples=150,MaximumBytes=4<<20}});
            var writing=Task.Run(()=>{for(int i=0;i<150;i++)concurrent.Append(Row(i,1001*(i+1)));concurrent.Finish("sampleLimit",151000);});
            while(!writing.IsCompleted){var current=Json(store.Status(concurrentId,true));Check(current.GetProperty("state").GetString() is "running" or "ended","Concurrent status never becomes historical or reads partial head");}
            writing.GetAwaiter().GetResult();Check(Json(store.Page(concurrent.Reference)).GetProperty("totalRows").GetInt32()==150,"Concurrent publication preserves all committed rows");
            string boundedId=Guid.NewGuid().ToString("N");var bounded=store.Create(plan with{CaptureId=boundedId});
            string large=JsonSerializer.Serialize(result with{Value=JsonSerializer.SerializeToElement(new string('x',190000))},BridgeProtocol.Json);
            bool limited=false;
            for(int i=0;i<20;i++)try{bounded.Append(Row(i,i+1) with{ReadResultJson=large});}catch(BridgeException e)when(e.Code=="RuntimeCaptureByteLimit"){bounded.RecordDroppedResult();limited=true;break;}
            Check(limited,"Capture byte budget stops acquisition before sample count");bounded.Finish("byteLimit",30);
            var boundedStatus=Json(store.Status(boundedId,false));Check(boundedStatus.GetProperty("recordedProgress").GetProperty("droppedReadResults").GetInt32()==1,"Discarded read result is counted, not silently lost");
            var finalGap=store.Create(plan with{CaptureId=Guid.NewGuid().ToString("N"),Request=request with{MaximumSamples=1}});
            finalGap.Append(Row(0,350));finalGap.Append(new(1,"gap",null,0,0,351,DateTimeOffset.UtcNow,null,null,3));finalGap.Finish("sampleLimit",352);
            Check(Json(store.Page(finalGap.Reference)).GetProperty("totalRows").GetInt32()==2,"Final gap does not consume another read-sample slot");
            for(int i=0;i<7;i++)store.Create(plan with{CaptureId=Guid.NewGuid().ToString("N"),Request=request with{MaximumBytes=64L<<20}});
            Fails(()=>store.Create(plan with{CaptureId=Guid.NewGuid().ToString("N"),Request=request with{MaximumBytes=64L<<20}}),"RuntimeCaptureCapacity");
        }
        using(var reopened=new RuntimeCaptureStore(root))Check(Json(reopened.Page(finalRef)).GetProperty("totalRows").GetInt32()==3,"Restart reads retained prefix without session");
        Check(RuntimeCaptureStore.NextCycle(0,350,100)==4,"Slow poll skips three slots without burst");
        Check(RuntimeCaptureStore.NextCycle(0,99,100)==1,"Fast poll waits for next scheduled slot");
        Fails(()=>RuntimeCaptureStore.NextCycle(0,double.NaN,100),"RuntimeCaptureTimingInvalid");
        foreach(var timing in new[]{(0L,0d,100,1L),(0L,99.999d,100,1L),(0L,100d,100,2L),(10L,1000d,100,11L),(0L,3600000d,100,36001L),(0L,60000d,60000,2L),(0L,3599999.9d,60000,60L)})
            Check(RuntimeCaptureStore.NextCycle(timing.Item1,timing.Item2,timing.Item3)==timing.Item4,"Scheduling selects the first future slot at exact interval/hour boundaries");
        Fails(()=>RuntimeCaptureStore.NextCycle(long.MaxValue,0,100),"RuntimeCaptureTimingInvalid");
        return checks;
    }
}
