using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeAlarmBindingChecks
{
    internal static JsonObject? Exported;
    internal static void Run()
    {
        int checks=0;
        void Check(bool ok){if(!ok)throw new Exception("Runtime alarm binding check "+(checks+1));checks++;}
        void Reject(Action action,string code="RuntimeCaptureAssociationMismatch")
        {try{action();}catch(BridgeException e)when(e.Code==code){checks++;return;}throw new Exception("Expected "+code);}
        using var f=new Fixture();AlarmBindingChecks.AddEvidence(f);
        var receipt=f.Planner.Plan(AlarmBindingChecks.Request(f));string caseId=receipt["id"]!.GetValue<string>();
        var original=receipt["plan"]!.AsObject();var alarm=original["alarmBindings"]!.AsObject();
        string attempt=Guid.NewGuid().ToString("N"),selectionId=Guid.NewGuid().ToString("N");
        var context=WebRuntimeProtocol.Context("https://plc.example",attempt,Guid.NewGuid().ToString("N"),new string('a',64));
        var metadata=JsonSerializer.SerializeToElement(new{name="Enable",datatype="bool"});
        RuntimeCaptureSignal[] signals=[new(selectionId,WebRuntimeProtocol.Symbol(context,"\"DB_Line\".Conv2.Enable","simple",metadata),metadata)];
        var request=new RuntimeCaptureRequest(attempt,[selectionId],100,1,10,1<<20,f.ReviewId,
            Association:new(caseId,[new(selectionId,"enable","Explicit saved alarm operand") ]));
        var association=RuntimeCaptureAssociations.Resolve(f.Root,request,signals)!;
        Check(JsonNode.DeepEquals(alarm,JsonNode.Parse(association.AlarmBindings!.Value.GetRawText())));
        Check(association.AlarmBindings.Value.GetProperty("bindings").GetArrayLength()==3 && association.Signals.Length==1);
        var plan=new RuntimeCapturePlan(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,request,signals,association);
        using var store=new RuntimeCaptureStore(f.Root);
        var diagnosticStore=new DiagnosticStore(f.Root);var exporter=new DiagnosticReportExport(f.Root);
        string Save(JsonObject value)=>diagnosticStore.Save(value)["id"]!.GetValue<string>();
        JsonElement Json(JsonNode value)=>JsonSerializer.SerializeToElement(value,BridgeProtocol.Json);
        foreach(Action<JsonObject> change in new Action<JsonObject>[] {
            a=>a["nativeQualified"]=true,a=>a["sourceAuthenticityVerified"]=true,
            a=>a["runtimeAlarmAssociationVerified"]=true,a=>a["coverageComplete"]=true,
            a=>a["reviewId"]=new string('b',64),a=>a["allSelectedMatched"]=false,
            a=>a["bindings"]![0]!["storage"]!["runtimeCallContextVerified"]=true,
            a=>a["bindings"]![0]!["declaration"]!["liveVerified"]=true,
            a=>a["bindings"]![0]!["signal"]!["datatype"]="UInt",
            a=>a["bindings"]![1]!["signal"]!["evidence"]!["liveVerified"]=true,
            a=>a["bindings"]![1]!["storage"]!["gaps"]!.AsArray().Add("missing call"),
            a=>a["bindings"]![0]!["status"]="relationshipUnresolved",
            a=>a["selectors"]![0]!["signalId"]="final",
            a=>a["bindings"]=new JsonArray(),a=>a.Remove("nativeQualified") })
        {
            var changed=alarm.DeepClone().AsObject();change(changed);
            Reject(()=>store.Create(plan with{Association=association with{AlarmBindings=Json(changed)}}));
            Check(!store.Exists(plan.CaptureId));
        }
        // Rehashed source alterations must also fail before a capture can be admitted.
        foreach(Action<JsonObject> change in new Action<JsonObject>[] {
            p=>p["alarmBindings"]!["bindings"]![0]!["declaration"]!["datatype"]="UInt",
            p=>p["alarmBindings"]!["bindings"]![1]!["relationships"]![0]!["edge"]!["kind"]="writes",
            p=>p["alarmBindings"]!["runtimeAlarmAssociationVerified"]=true })
        {
            var changed=original.DeepClone().AsObject();change(changed);string bad=Save(changed);
            Reject(()=>RuntimeCaptureAssociations.Resolve(f.Root,request with{Association=request.Association! with{CaseId=bad}},signals));
        }
        var oversized=alarm.DeepClone().AsObject();oversized["policy"]=new string('x',128<<10);
        Reject(()=>store.Create(plan with{Association=association with{AlarmBindings=Json(oversized)}}),"RuntimeCaptureLimit");
        Check(!store.Exists(plan.CaptureId));
        var writer=store.Create(plan);writer.Append(new(0,"read",selectionId,0,0,1,plan.CreatedUtc,null,"RuntimeTimeout",0));writer.Finish("stopped",2);
        var analysis=new RuntimeCaptureAnalysis(f.Root);var analyzed=analysis.Analyze(store,writer.Reference,new JsonObject{["enable"]="1"});
        string reportId=analyzed["id"]!.GetValue<string>();var report=analyzed["report"]!.AsObject();
        Check(JsonNode.DeepEquals(alarm,report["association"]!["alarmBindings"]));
        Check(report["qualityAssessment"]!["uncapturedSignalIds"]!.AsArray().Any(n=>(string?)n=="enableSource"));
        Check(report["hypotheses"]!.AsArray().All(h=>(string?)h!["verdict"]=="unresolved"));
        var altered=alarm.DeepClone().AsObject();altered["bindings"]![0]!["operand"]!["metadata"]!["rawLabel"]="Changed retained label";
        var changedWriter=store.Create(plan with{CaptureId=Guid.NewGuid().ToString("N"),Association=association with{AlarmBindings=Json(altered)}});
        changedWriter.Finish("stopped",0);
        Reject(()=>analysis.Analyze(store,changedWriter.Reference,new JsonObject{["enable"]="1"}),"RuntimeCaptureAnalysisMismatch");
        foreach(Action<JsonObject> change in new Action<JsonObject>[] {
            r=>r["association"]!["alarmBindings"]!["nativeQualified"]=true,
            r=>r["association"]!["alarmBindings"]!["bindings"]![2]!["storage"]!["runtimeCallContextVerified"]=true,
            r=>r["association"]!["alarmBindings"]!["bindings"]![0]!["signal"]!["storagePath"]="other",
            r=>r["association"]!["alarmBindings"]=null,
            r=>r["capture"]!["signals"]=new JsonArray(),
            r=>r["association"]!["caseId"]=new string('b',64) })
        {
            var changed=report.DeepClone().AsObject();change(changed);
            Reject(()=>exporter.Export(Save(changed),"html"),"InvalidDiagnosticReport");
        }
        string reviewPath=Path.Combine(f.Root,"project-reviews",f.ReviewId+".json");
        File.Delete(reviewPath);
        Reject(()=>analysis.Analyze(store,writer.Reference,new JsonObject{["enable"]="1"}),"ArtifactUnavailable");
        File.Delete(Path.Combine(f.Root,"trace-diagnostics",caseId+".json"));
        foreach(string format in new[]{"json","markdown","html"})
        {
            var exported=exporter.Export(reportId,format);string text=File.ReadAllText(exported["artifact"]!["path"]!.GetValue<string>());
            Check(text.Contains("diagnostic-alarm-bindings.v1"));
            if(format!="json"){Check(text.Contains("Selected for capture"));Check(text.Contains("Not selected in this capture"));}
        }
        string reference=writer.Reference;store.Dispose();
        using(var reopened=new RuntimeCaptureStore(f.Root))
        {
            Check(JsonNode.DeepEquals(alarm,JsonNode.Parse(reopened.Plan(plan.CaptureId).Association!.AlarmBindings!.Value.GetRawText())));
            var exported=JsonSerializer.SerializeToElement(reopened.Export(reference,"json",false),BridgeProtocol.Json);
            var contents=JsonNode.Parse(File.ReadAllText(exported.GetProperty("artifacts")[0].GetProperty("path").GetString()!));
            Check(JsonNode.DeepEquals(alarm,contents!["capture"]!["plan"]!["association"]!["alarmBindings"]));
            Exported=contents.AsObject();
        }
        using var unresolved=new Fixture();AlarmBindingChecks.AddEvidence(unresolved);
        unresolved.Model["nodes"]!.AsArray().Single(n=>(string?)n!["nodeId"]=="condition")!["metadata"]!.AsObject().Remove("alarmOperand");
        unresolved.SaveReview();string unresolvedId=unresolved.Planner.Plan(AlarmBindingChecks.Request(unresolved))["id"]!.GetValue<string>();
        Reject(()=>RuntimeCaptureAssociations.Resolve(unresolved.Root,request with{ReviewId=unresolved.ReviewId,Association=request.Association! with{CaseId=unresolvedId}},signals));
        Console.WriteLine($"PASS {checks} runtime alarm binding checks; no native alarm or online program qualification.");
    }
}
