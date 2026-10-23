using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.AddIns;

namespace TiaMcp.Tests;

internal static class AddInSelectionTests
{
    internal static Task Run()
    {
        var model=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures/project-explorer/representative.project-model.json")))!.AsObject();
        string root=Path.Combine(Path.GetTempPath(),"tia-addin-"+Guid.NewGuid().ToString("N"));
        var store=new ProjectReviewStore(root);var saved=store.Save(model,[],new());string reviewId=(string)saved["reviewId"]!;
        var review=store.Read(reviewId);var nodes=model["nodes"]!.AsArray().OfType<JsonObject>().Where(n=>(string?)n["origin"]=="native" && !string.IsNullOrEmpty((string?)n["objectPath"])).Take(2).ToArray();
        var references=new JsonArray(nodes.Select(n=>(JsonNode)new JsonObject{["schemaVersion"]="project-model.v1",["snapshotId"]=model["snapshotId"]!.DeepClone(),["sessionGeneration"]=model["sessionGeneration"]!.DeepClone(),["projectPath"]=model["project"]!["projectPath"]!.DeepClone(),["nodeId"]=n["nodeId"]!.DeepClone(),["objectPath"]=n["objectPath"]!.DeepClone()}).ToArray());
        var input=new JsonObject{["schemaVersion"]="addin-selection.v1",["reviewId"]=reviewId,["selectionId"]=new string('a',32),["selectedAtUtc"]="2026-10-18T00:00:00.0000000+00:00",["surface"]="projectTree",["projectHost"]=model["project"]!["host"]!.DeepClone(),["producer"]=new JsonObject{["productId"]="synthetic-companion",["version"]="0.0.0",["tiaVersion"]=21,["assemblySha256"]=new string('b',64),["configurationSha256"]=new string('c',64)},["references"]=references};
        int checks=0;
        void Assert(bool value){if(!value)throw new Exception("Add-In selection assertion failed");checks++;}
        void Refuse(Action action){try{action();}catch(Exception e) when(e is BridgeException or JsonException or DecoderFallbackException or ArgumentException){checks++;return;}throw new Exception("Expected selection refusal");}
        JsonObject Copy()=>input.DeepClone().AsObject();
        var result=AddInSelection.Build(reviewId,review,input);
        Assert(result["selections"]!.AsArray().Count==2 && (bool)result["producerAuthenticated"]! ==false && (bool)result["nativeExecutable"]! ==false);
        foreach(string key in new[]{"schemaVersion","snapshotId","sessionGeneration","projectPath","nodeId","objectPath"})
        {var bad=Copy();bad["references"]![0]![key]="changed";Refuse(()=>AddInSelection.Build(reviewId,review,bad));}
        foreach(string key in new[]{"reviewId","selectionId","selectedAtUtc","surface","projectHost"})
        {var bad=Copy();bad[key]="changed";Refuse(()=>AddInSelection.Build(reviewId,review,bad));}
        foreach(string key in new[]{"assemblySha256","configurationSha256"})
        {var bad=Copy();bad["producer"]![key]="invalid";Refuse(()=>AddInSelection.Build(reviewId,review,bad));}
        foreach(int version in new[]{20,22}){var bad=Copy();bad["producer"]!["tiaVersion"]=version;Refuse(()=>AddInSelection.Build(reviewId,review,bad));}
        foreach(string scope in new[]{"root","producer","reference"})
        {var bad=Copy();(scope=="root"?bad:scope=="producer"?bad["producer"]!:bad["references"]![0]!).AsObject()["nativeQualified"]=true;Refuse(()=>AddInSelection.Build(reviewId,review,bad));}
        var duplicate=Copy();duplicate["references"]!.AsArray().Add(references[0]!.DeepClone());Refuse(()=>AddInSelection.Build(reviewId,review,duplicate));
        foreach(int count in new[]{0,65}){var bad=Copy();bad["references"]=new JsonArray(Enumerable.Range(0,count).Select(_=>references[0]!.DeepClone()).ToArray());Refuse(()=>AddInSelection.Build(reviewId,review,bad));}
        var virtualReview=review.DeepClone().AsObject();virtualReview["model"]!["nodes"]!.AsArray().OfType<JsonObject>().Single(n=>(string?)n["nodeId"]==(string?)nodes[0]["nodeId"])["origin"]="virtual";
        Refuse(()=>AddInSelection.Build(reviewId,virtualReview,input));
        var ambiguous=review.DeepClone().AsObject();var alias=nodes[0].DeepClone().AsObject();alias["nodeId"]="different-id";ambiguous["model"]!["nodes"]!.AsArray().Add(alias);Refuse(()=>AddInSelection.Build(reviewId,ambiguous,input));
        Refuse(()=>AddInSelection.Parse(Encoding.UTF8.GetBytes("{\"references\":[],\"references\":[]}")));
        Refuse(()=>AddInSelection.Parse(new byte[]{0xff}));Refuse(()=>AddInSelection.Parse(new byte[AddInSelection.InputLimit+1]));
        byte[] bytes=Encoding.UTF8.GetBytes(input.ToJsonString()+"\n");
        var uploads=new ArtifactUploadStore(root);var begun=uploads.Begin("selection.json",bytes.Length,Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        uploads.Write(begun.UploadId,0,Convert.ToBase64String(bytes));var committed=uploads.Commit(begun.UploadId);
        var workflow=new AddInSelectionStore(root);var receipt=workflow.Review(reviewId,committed.ArtifactRef!);
        Assert(JsonNode.DeepEquals(receipt,workflow.Review(reviewId,committed.ArtifactRef!)));
        string recordId=(string)receipt["recordId"]!;var page=new AddInSelectionStore(root).Page(recordId,"selections",0,1);
        Assert((int)page["nextOffset"]! ==1 && JsonNode.DeepEquals(page["rows"]![0]!["value"]!["reference"],references[0]));
        Assert(ProjectReviewStore.ReadArtifact(root,receipt["input"]!.AsObject()).SequenceEqual(bytes));
        foreach(string section in new[]{"execute","resume"})Refuse(()=>workflow.Page(recordId,section));
        Refuse(()=>workflow.Page(recordId,"selections",3));
        string recordPath=Path.Combine(root,(string)receipt["reportArtifact"]!["path"]!);byte[] original=File.ReadAllBytes(recordPath);File.AppendAllText(recordPath," ");Refuse(()=>workflow.Page(recordId));File.WriteAllBytes(recordPath,original);
        string reviewPath=Path.Combine(root,"project-reviews",reviewId+".json");byte[] source=File.ReadAllBytes(reviewPath);File.AppendAllText(reviewPath," ");Refuse(()=>workflow.Page(recordId));File.WriteAllBytes(reviewPath,source);
        Assert(JsonNode.DeepEquals(receipt,new AddInSelectionStore(root).Page(recordId)));
        Console.WriteLine($"PASS Add-In detached selections: {checks} checks; no native companion or live selection.");return Task.CompletedTask;
    }
}
