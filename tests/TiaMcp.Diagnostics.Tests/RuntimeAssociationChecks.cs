using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeAssociationChecks
{
    internal static void Run()
    {
        using var f=new Fixture();var diagnostic=f.Plan();string caseId=diagnostic["id"]!.GetValue<string>();
        string attempt=Guid.NewGuid().ToString("N"),selectionId=Guid.NewGuid().ToString("N");
        var context=WebRuntimeProtocol.Context("https://plc.example",attempt,Guid.NewGuid().ToString("N"),new string('a',64));
        var metadata=JsonSerializer.SerializeToElement(new{name="Enable",datatype="bool"});
        RuntimeCaptureSignal[] signals=[new(selectionId,WebRuntimeProtocol.Symbol(context,"\"DB_Line\".Conv2.Enable","simple",metadata),metadata)];
        var mapping=new RuntimeCaptureAssociationRequest(caseId,[new(selectionId,"enable","Explicitly selected conveyor command")],"Caller supplied program label");
        var request=new RuntimeCaptureRequest(attempt,[selectionId],100,1,1,1<<20,f.ReviewId,Association:mapping);
        RuntimeCaptureAssociation Resolve(RuntimeCaptureRequest? input=null)=>RuntimeCaptureAssociations.Resolve(f.Root,input??request,signals)!;
        void Check(bool value){if(!value)throw new Exception("Runtime association expectation failed");}
        void Fails(Action action,string code="RuntimeCaptureAssociationMismatch"){try{action();}catch(BridgeException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var association=Resolve();
        Check(association.Signals[0].Declaration.GetProperty("storagePath").GetString()==Fixture.Block("DB_Line") &&
            association.SelectedInstance.GetProperty("memberPath")[0].GetString()=="Conv2" &&
            !association.AddressMatchVerified && !association.ProgramMatchVerified && !association.SourceAuthenticityVerified);
        Fails(()=>Resolve(request with{ReviewId=null}));
        Fails(()=>Resolve(request with{ReviewId=new string('0',64)}));
        Fails(()=>Resolve(request with{Association=mapping with{Mappings=[new(selectionId,"missing","Explicit") ]}}));
        Fails(()=>Resolve(request with{Association=mapping with{Mappings=[new(Guid.NewGuid().ToString("N"),"enable","Wrong selection")]}}));
        Fails(()=>Resolve(request with{Association=mapping with{Mappings=[new(selectionId,"step","Wrong observed datatype")]}}));
        var original=(JsonObject)diagnostic["plan"]!.DeepClone();
        foreach(string field in new[]{"datatype","storagePath","symbolComponents","evidence","namespace","scopePath"})
        {
            var changed=(JsonObject)original.DeepClone();var row=changed["signals"]!.AsArray().Single(s=>s!["id"]!.GetValue<string>()=="enable")!;
            row[field]=field switch {"datatype"=>JsonValue.Create("UInt"),"storagePath"=>JsonValue.Create(Fixture.Block("DB_IO")),"symbolComponents"=>new JsonArray("Wrong", "Enable"),"evidence"=>new JsonObject{["liveVerified"]=true},_=>JsonValue.Create("wrong scope")};
            var forged=new DiagnosticStore(f.Root).Save(changed)["id"]!.GetValue<string>();
            Fails(()=>Resolve(request with{Association=mapping with{CaseId=forged}}),field is "datatype" or "storagePath" ? "MeasurementLineageMismatch" : "RuntimeCaptureAssociationMismatch");
        }
        var otherInstance=(JsonObject)original.DeepClone();otherInstance["selectedInstance"]!["memberPath"]=new JsonArray("Conv1");
        string otherId=new DiagnosticStore(f.Root).Save(otherInstance)["id"]!.GetValue<string>();
        Fails(()=>Resolve(request with{Association=mapping with{CaseId=otherId}}));
        var unavailable=(JsonObject)original.DeepClone();unavailable["signals"]![1]!["resolution"]="unavailable";
        string unavailableId=new DiagnosticStore(f.Root).Save(unavailable)["id"]!.GetValue<string>();
        Fails(()=>Resolve(request with{Association=mapping with{CaseId=unavailableId}}));
        string reviewPath=Path.Combine(f.Root,"project-reviews",f.ReviewId+".json");byte[] reviewBytes=File.ReadAllBytes(reviewPath);
        File.AppendAllText(reviewPath," ");Fails(()=>Resolve(),"ArtifactChanged");File.WriteAllBytes(reviewPath,reviewBytes);
        string id=Guid.NewGuid().ToString("N"),reference;
        var plan=new RuntimeCapturePlan(id,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,request,signals,association);
        using(var store=new RuntimeCaptureStore(f.Root))
        {
            Fails(()=>store.Create(plan with{Association=association with{ProgramMatchVerified=true}}));
            var writer=store.Create(plan);writer.Finish("stopped",0);reference=writer.Reference;
            File.Delete(reviewPath);File.Delete(Path.Combine(f.Root,"trace-diagnostics",caseId+".json"));
            Check(JsonSerializer.Serialize(store.Status(id,false),BridgeProtocol.Json).Contains("capturedDeclaration"));
        }
        using(var reopened=new RuntimeCaptureStore(f.Root))
        {
            var exported=JsonSerializer.SerializeToElement(reopened.Export(reference,"json",false),BridgeProtocol.Json);
            using var data=JsonDocument.Parse(File.ReadAllText(exported.GetProperty("artifacts")[0].GetProperty("path").GetString()!));
            Check(data.RootElement.GetProperty("capture").GetProperty("plan").GetProperty("association").GetProperty("caseId").GetString()==caseId);
        }
    }
}
