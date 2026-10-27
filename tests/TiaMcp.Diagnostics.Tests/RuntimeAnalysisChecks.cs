using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeAnalysisChecks
{
    internal static void Run()
    {
        using var f=new CaptureFixture();var writer=f.Store.Create(f.Plan);string empty=writer.Reference;
        f.Append(writer,0,true,"65535");f.Append(writer,1,false,"65534",backwardsClock:true);
        f.Append(writer,2,null,null);writer.Append(new(writer.Rows,"gap",null,2,200,203,f.Start,null,null,2));
        f.Append(writer,5,true,"\"65535\"");f.Append(writer,6,true,"70000");f.Append(writer,7,true,"1");f.Append(writer,8,true,"1e-100");
        writer.Finish("stopped",804);
        var receipt=f.Analyze(writer.Reference);var report=receipt["report"]!;
        Equal(7,report["qualityAssessment"]!["cyclesObserved"]!.GetValue<int>());
        Equal(68,report["qualityAssessment"]!["missingValues"]!.GetValue<int>());
        Equal(1,report["qualityAssessment"]!["hostReadErrors"]!.GetValue<int>());
        Equal(1,report["qualityAssessment"]!["providerFailures"]!.GetValue<int>());
        Equal(3,report["qualityAssessment"]!["unsupportedValues"]!.GetValue<int>());
        Equal(2L,report["qualityAssessment"]!["skippedPolls"]!.GetValue<long>());
        Equal(2,report["observedChangeCount"]!.GetValue<int>());
        var conditions=report["hypotheses"]!.AsArray().ToDictionary(h=>h!["id"]!.GetValue<string>());
        Equal(1,conditions["pattern"]!["matchedCycles"]!.GetValue<int>());Equal(1,conditions["pattern"]!["contradictedCycles"]!.GetValue<int>());Equal(4,conditions["pattern"]!["unknownCycles"]!.GetValue<int>());
        Equal(2,conditions["numeric"]!["matchedCycles"]!.GetValue<int>());Equal(4,conditions["numeric"]!["unknownCycles"]!.GetValue<int>());
        Equal(true,conditions["numeric"]!["examples"]![1]!["reads"]!["step"]!["hostClockWentBackwards"]!.GetValue<bool>());
        foreach(var finding in conditions.Values)Equal("unresolved",finding!["verdict"]!.GetValue<string>());
        Equal(receipt["id"]!.GetValue<string>(),f.Analyze(writer.Reference)["id"]!.GetValue<string>());
        Equal(0,f.Analyze(empty)["report"]!["qualityAssessment"]!["cyclesObserved"]!.GetValue<int>());
        Equal(true,f.Analyze(empty)["report"]!["capture"]!["uncommittedDataUnknown"]!.GetValue<bool>());
        Fails(()=>f.Analyzer.Analyze(f.Store,writer.Reference,new JsonObject{["enable"]="1"}),"RuntimeCaptureAnalysisMismatch");
        Fails(()=>f.Analyzer.Analyze(f.Store,writer.Reference,new JsonObject{["enable"]="1",["step"]="ms"}),"RuntimeCaptureAnalysisMismatch");
        var duplicate=f.Store.Create(f.Plan with{CaptureId=Guid.NewGuid().ToString("N")});
        duplicate.Append(f.Row(0,0,0,"true"));duplicate.Append(f.Row(1,0,0,"true"));duplicate.Finish("stopped",3);
        Fails(()=>f.Analyze(duplicate.Reference),"RuntimeCaptureAnalysisOrderInvalid");
        var reverse=f.Store.Create(f.Plan with{CaptureId=Guid.NewGuid().ToString("N")});
        reverse.Append(f.Row(0,1,0,"true"));reverse.Append(f.Row(1,0,1,"1") with{ElapsedMilliseconds=102});reverse.Finish("stopped",103);
        Fails(()=>f.Analyze(reverse.Reference),"RuntimeCaptureAnalysisOrderInvalid");
        var plain=f.Store.Create(f.Plan with{CaptureId=Guid.NewGuid().ToString("N"),Request=f.Plan.Request with{Association=null},Association=null});
        Fails(()=>f.Analyze(plain.Reference),"RuntimeCaptureAssociationRequired");plain.Finish("stopped",0);
        File.Delete(Path.Combine(f.Source.Root,"project-reviews",f.Source.ReviewId+".json"));
        Equal(receipt["id"]!.GetValue<string>(),f.Analyze(writer.Reference)["id"]!.GetValue<string>());
        File.Delete(Path.Combine(f.Source.Root,"trace-diagnostics",f.Plan.Association!.CaseId+".json"));
        Fails(()=>f.Analyze(writer.Reference),"ArtifactUnavailable");
        foreach(string format in new[]{"json","markdown","html"})
        {
            var exported=new DiagnosticReportExport(f.Source.Root).Export(receipt["id"]!.GetValue<string>(),format);
            string contents=File.ReadAllText(exported["artifact"]!["path"]!.GetValue<string>());
            Equal(true,contents.Contains("unresolved"));
            if(format=="html"){Equal(false,contents.Contains("<script>"));Equal(true,contents.Contains("sequential reads"));}
        }
        var forged=report.DeepClone().AsObject();forged["hypotheses"]![0]!["verdict"]="supportedPrediction";
        string forgedId=new DiagnosticStore(f.Source.Root).Save(forged)["id"]!.GetValue<string>();
        Fails(()=>new DiagnosticReportExport(f.Source.Root).Export(forgedId,"html"),"InvalidDiagnosticReport");
        HandoffExports(f, report.AsObject());
        using var raw=new CaptureFixture(raw:true);var rawWriter=raw.Store.Create(raw.Plan);raw.Append(rawWriter,0,true,"[0,255]");rawWriter.Finish("stopped",3);
        Equal(2,raw.Analyze(rawWriter.Reference)["report"]!["qualityAssessment"]!["unsupportedValues"]!.GetValue<int>());
        using var wide=new CaptureFixture(wide:true);var wideWriter=wide.Store.Create(wide.Plan);wide.Append(wideWriter,0,true,"18446744073709551615");wideWriter.Finish("stopped",3);
        var wideReport=wide.Analyze(wideWriter.Reference)["report"]!;
        Equal(1,wideReport["hypotheses"]![1]!["matchedCycles"]!.GetValue<int>());
        Equal("18446744073709551615",wideReport["hypotheses"]![1]!["examples"]![0]!["values"]!["step"]!.ToJsonString());
        using var bounded=new CaptureFixture();
        var longPlan=bounded.Plan with{Request=bounded.Plan.Request with{DurationSeconds=3600,MaximumSamples=13000,MaximumBytes=32<<20}};
        var longWriter=bounded.Store.Create(longPlan);
        for(int cycle=0;cycle<6001;cycle++)bounded.Append(longWriter,cycle,true,cycle%2==0?"65535":"1");
        longWriter.Finish("stopped",600003);
        var longReport=bounded.Analyze(longWriter.Reference)["report"]!;
        Equal(6001,longReport["qualityAssessment"]!["cyclesObserved"]!.GetValue<int>());
        Equal(3001,longReport["hypotheses"]![1]!["matchedCycles"]!.GetValue<int>());
        Equal(3000,longReport["hypotheses"]![1]!["contradictedCycles"]!.GetValue<int>());
        Equal(20,longReport["hypotheses"]![1]!["examples"]!.AsArray().Count);
        Equal(6000,longReport["observedChangeCount"]!.GetValue<int>());
        Equal(200,longReport["observedChanges"]!.AsArray().Count);
        Equal(true,longReport["observedChangesTruncated"]!.GetValue<bool>());
    }
    private static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}");}
    private static void HandoffExports(CaptureFixture f,JsonObject source)
    {
        var report=source.DeepClone().AsObject();
        var handoff=new RuntimeCaptureHandoff("runtime-capture-handoff.v1","runtime-handoff:"+new string('b',32)+":"+new string('c',64),f.Source.ReviewId,
            new(new string('d',32),f.Plan.Signals[0].Selection!.Context,"<img src=x onerror=alert(1)>","local",
                new("protected-credential-reference.v1",new string('e',32),new string('f',32),new string('a',64),new string('b',64)),true),
            "consistentDeclarations",false,false,false,false,false,false);
        report["handoff"]=JsonSerializer.SerializeToNode(handoff,BridgeProtocol.Json);
        report["capture"]!["request"]!["handoffRef"]=handoff.RecordRef;
        var store=new DiagnosticStore(f.Source.Root);var exporter=new DiagnosticReportExport(f.Source.Root);
        string Save(JsonObject value)=>store.Save(value)["id"]!.GetValue<string>();
        foreach(string format in new[]{"json","markdown","html"})
        {
            var receipt=exporter.Export(Save(report),format);string text=File.ReadAllText(receipt["artifact"]!["path"]!.GetValue<string>());
            Equal(true,text.Contains(handoff.RecordRef));
            if(format=="html"){Equal(false,text.Contains("<img"));Equal(true,text.Contains("Export does not refresh this evidence"));}
        }
        void Reject(Action<JsonObject> mutate)
        {
            var bad=report.DeepClone().AsObject();mutate(bad);
            Fails(()=>exporter.Export(Save(bad),"html"),"InvalidDiagnosticReport");
        }
        foreach(string flag in new[]{"deviceIdentityVerified","programMatchVerified","effectiveRightsVerified","sourceAuthenticityVerified","operationAdmitted","assertionsAdmitted"})
        { Reject(b=>b["handoff"]![flag]=true);Reject(b=>b["handoff"]!.AsObject().Remove(flag)); }
        Reject(b=>b.Remove("handoff"));Reject(b=>b["capture"]!["request"]!.AsObject().Remove("handoffRef"));
        Reject(b=>b["handoff"]!["reviewId"]=new string('a',64));
        Reject(b=>b["handoff"]!["session"]!["context"]!["epoch"]=new string('a',32));
        Reject(b=>b["capture"]!["signals"]![1]!["selection"]!["context"]!["epoch"]=new string('a',32));
        Reject(b=>b["handoff"]!["session"]=null);
        Reject(b=>b["handoff"]!["extra"]=true);
    }
    private static void Fails(Action action,string code){try{action();}catch(BridgeException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}

    internal sealed class CaptureFixture : IDisposable
    {
        internal Fixture Source=new();internal RuntimeCaptureStore Store;internal RuntimeCapturePlan Plan;
        internal RuntimeCaptureAnalysis Analyzer=>new(Source.Root);
        internal DateTimeOffset Start=new(2026,10,18,1,0,0,TimeSpan.Zero);
        internal CaptureFixture(bool raw=false,bool wide=false)
        {
            if(wide)foreach(string block in new[]{"DB_Line","FB_Conveyor"})Source.ReplaceXml(block,s=>s.Replace("Name=\"Step\" Datatype=\"UInt\"","Name=\"Step\" Datatype=\"ULInt\""));
            var input=Source.Request();string maximum=wide?"18446744073709551615":"65535";
            input["hypotheses"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"pattern\",\"statement\":\"<script>never execute</script> sequential pattern\",\"prediction\":{\"when\":[{\"signalId\":\"enable\",\"op\":\"eq\",\"value\":true}],\"expect\":[{\"signalId\":\"step\",\"op\":\"eq\",\"value\":"+maximum+",\"unit\":\"count\"}]}}"));
            input["hypotheses"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"numeric\",\"statement\":\"Bounded integer observation\",\"prediction\":{\"when\":[{\"signalId\":\"step\",\"op\":\"ge\",\"value\":65000,\"unit\":\"count\"}],\"expect\":[{\"signalId\":\"step\",\"op\":\"le\",\"value\":"+maximum+",\"unit\":\"count\"}]}}"));
            string caseId=Source.Planner.Plan(input)["id"]!.GetValue<string>();string attempt=Guid.NewGuid().ToString("N");
            var context=WebRuntimeProtocol.Context("https://plc.example",attempt,Guid.NewGuid().ToString("N"),new string('a',64));
            RuntimeCaptureSignal Signal(string name,string type){var metadata=JsonSerializer.SerializeToElement(new{name,datatype=type});return new(Guid.NewGuid().ToString("N"),WebRuntimeProtocol.Symbol(context,"\"DB_Line\".Conv2."+name,raw?"raw":"simple",metadata),metadata);}
            RuntimeCaptureSignal[] signals=[Signal("Enable","bool"),Signal("Step",wide?"ulint":"uint")];
            var association=new RuntimeCaptureAssociationRequest(caseId,[new(signals[0].SelectionId,"enable","Explicit"),new(signals[1].SelectionId,"step","Explicit")]);
            var request=new RuntimeCaptureRequest(attempt,signals.Select(s=>s.SelectionId).ToArray(),100,10,100,1<<20,Source.ReviewId,raw?"raw":"simple",association);
            Plan=new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Start,request,signals,RuntimeCaptureAssociations.Resolve(Source.Root,request,signals));
            Store=new(Source.Root);
        }
        internal RuntimeCaptureRow Row(long sequence,long cycle,int signal,string? value,bool backwardsClock=false)
        {
            string? result=null,error=null;
            if(signal==0 && value is null)error="RuntimeTimeout";
            else
            {
                string id=Guid.NewGuid().ToString("N");var selection=Plan.Signals[signal].Selection!;
                var request=WebRuntimeProtocol.Read(selection.Context,selection,id);
                string payload=value is null?"\"error\":{\"code\":2}":"\"result\":"+(selection.Representation=="raw"?"[0,255]":value);
                var read=WebRuntimeProtocol.ReadResult(selection.Context,request,Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\""+id+"\","+payload+"}"),Start,Start.AddMilliseconds(backwardsClock?-1:1),TimeSpan.FromMilliseconds(1));
                result=JsonSerializer.Serialize(read,BridgeProtocol.Json);
            }
            return new(sequence,"read",Plan.Signals[signal].SelectionId,cycle,cycle*100,cycle*100+signal+1,Start,result,error,0);
        }
        internal void Append(RuntimeCaptureStore.Writer writer,long cycle,bool? enabled,string? step,bool backwardsClock=false)
        {writer.Append(Row(writer.Rows,cycle,0,enabled?.ToString().ToLowerInvariant()));writer.Append(Row(writer.Rows,cycle,1,step,backwardsClock));}
        internal JsonObject Analyze(string reference)=>Analyzer.Analyze(Store,reference,new JsonObject{["enable"]="1",["step"]="count"});
        public void Dispose(){Store.Dispose();Source.Dispose();}
    }
}
