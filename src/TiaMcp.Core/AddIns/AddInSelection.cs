using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.AddIns;

/// <summary>Detached selection evidence only; never admits a native target or executes an intent.</summary>
public static class AddInSelection
{
    public const int InputLimit=1024*1024;
    public static JsonObject Parse(byte[] bytes)
    {
        if(bytes.Length>InputLimit)throw Error("Selection exceeds one MiB.");
        string text=new UTF8Encoding(false,true).GetString(bytes);
        using var document=JsonDocument.Parse(text,new(){MaxDepth=24});
        void Unique(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var field in value.EnumerateObject())
                {if(!names.Add(field.Name))throw Error("Duplicate JSON field.");Unique(field.Value);}
            }
            else if(value.ValueKind==JsonValueKind.Array)foreach(var row in value.EnumerateArray())Unique(row);
        }
        Unique(document.RootElement);
        return JsonNode.Parse(text) as JsonObject??throw Error("Selection must be an object.");
    }
    public static JsonObject Build(string reviewId,JsonObject review,JsonObject request)
    {
        Fields(request,"schemaVersion reviewId selectionId selectedAtUtc surface projectHost producer references");
        if(Text(request,"schemaVersion")!="addin-selection.v1" || Text(request,"reviewId")!=reviewId)throw Error("Selection must name the exact saved review.");
        EngineeringIdentityGuard.Sha256(reviewId,"reviewId");
        string selectionId=Text(request,"selectionId");
        if(selectionId.Length!=32 || selectionId.Any(c=>!char.IsAsciiHexDigitLower(c)))throw Error("selectionId must be 32 lowercase hexadecimal characters.");
        if(!DateTimeOffset.TryParseExact(Text(request,"selectedAtUtc"),"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time) || time.Offset!=TimeSpan.Zero)
            throw Error("selectedAtUtc must be an exact UTC round-trip timestamp; it is not freshness proof.");
        if(Text(request,"surface") is not ("projectTree" or "devicesAndNetworks"))throw Error("Only supplied project-tree and hardware/network selections are supported.");
        var producer=Object(request["producer"]);
        Fields(producer,"productId version tiaVersion assemblySha256 configurationSha256");
        Text(producer,"productId",256);Text(producer,"version",64);
        if(producer["tiaVersion"] is not JsonValue version || !version.TryGetValue<int>(out int number) || number!=21)throw Error("Only the V21 handoff contract is defined.");
        EngineeringIdentityGuard.Sha256(Text(producer,"assemblySha256"),"assemblySha256");
        EngineeringIdentityGuard.Sha256(Text(producer,"configurationSha256"),"configurationSha256");
        var model=Object(review["model"]);var project=Object(model["project"]);
        if(Text(review,"schemaVersion")!="project-review.v1" || Text(model,"schemaVersion")!="project-model.v1" || Text(project,"host")!=Text(request,"projectHost"))throw Error("Saved project host/schema differs.");
        if(request["references"] is not JsonArray references || references.Count is <1 or >64)throw Error("Select 1..64 exact references.");
        if(model["nodes"] is not JsonArray nodes)throw Error("Saved model has no nodes.");
        var selected=new HashSet<string>(StringComparer.Ordinal);var rows=new JsonArray();
        foreach(var item in references)
        {
            var reference=Object(item);Fields(reference,"schemaVersion snapshotId sessionGeneration projectPath nodeId objectPath");
            foreach(string key in new[]{"schemaVersion","snapshotId","sessionGeneration"})
                if(Text(reference,key)!=Text(model,key))throw Error("Selection snapshot or session differs; recapture explicitly.");
            if(Text(reference,"projectPath")!=Text(project,"projectPath"))throw Error("Selection belongs to another project copy.");
            string nodeId=Text(reference,"nodeId"),path=Text(reference,"objectPath");
            var matches=nodes.OfType<JsonObject>().Where(n=>(string?)n["nodeId"]==nodeId).ToArray();
            if(matches.Length!=1 || Text(matches[0],"objectPath")!=path || Text(matches[0],"origin")!="native" || !selected.Add(path))
                throw Error("Selection is absent, duplicated, virtual or changed; names never repair identity.");
            var node=matches[0];
            if(nodes.OfType<JsonObject>().Count(n=>(string?)n["objectPath"]==path)!=1)throw Error("Saved object path is ambiguous.");
            rows.Add(new JsonObject { ["reference"]=reference.DeepClone(),["kind"]=Text(node,"kind",64),["name"]=Text(node,"name",512),
                ["binding"]="exactSavedReference",["contentVerified"]=false,["liveSelectionVerified"]=false });
        }
        return new JsonObject { ["schemaVersion"]="addin-selection-report.v1",["reviewId"]=reviewId,["selectionId"]=selectionId,
            ["selectedAtUtc"]=request["selectedAtUtc"]!.DeepClone(),["surface"]=request["surface"]!.DeepClone(),["projectHost"]=request["projectHost"]!.DeepClone(),
            ["producer"]=producer.DeepClone(),["selections"]=rows,["producerAuthenticated"]=false,["liveSelectionVerified"]=false,["nativeExecutable"]=false,["nativeQualified"]=false,
            ["summary"]=new JsonObject{["selectionCount"]=rows.Count,["scope"]="savedReviewOnly",["sourceEvidencePolicy"]=ProjectReviewStore.SourceEvidencePolicy} };
    }
    private static JsonObject Object(JsonNode? node)=>node as JsonObject??throw Error("Expected an object.");
    private static void Fields(JsonObject value,string fields)
    {var expected=fields.Split(' ');if(value.Count!=expected.Length || expected.Any(k=>!value.ContainsKey(k)))throw Error("Missing or unknown selection fields.");}
    private static string Text(JsonObject node,string key,int max=8192)
    {
        if(node[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length>max || text.Any(char.IsControl))throw Error("Invalid exact text: "+key);
        _=new UTF8Encoding(false,true).GetByteCount(text);return text;
    }
    private static BridgeException Error(string message)=>new("InvalidAddInSelection",message);
}
