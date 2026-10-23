using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.Core.Diagnostics;

namespace TiaMcp.Core.PhysicalRuntime;

public sealed record RuntimeCaptureMapping(string SelectionId, string SignalId, string Reason);
public sealed record RuntimeCaptureAssociationRequest(string CaseId, RuntimeCaptureMapping[] Mappings, string? AssertedProgramIdentity = null);
public sealed record RuntimeCaptureMappedSignal(string SelectionId, string SignalId, JsonElement Declaration);
public sealed record RuntimeCaptureAssociation(string CaseId, string ReviewId, JsonElement Project, string BlockPath,
    JsonElement SelectedInstance, JsonElement SourceRevision, RuntimeCaptureMappedSignal[] Signals,
    bool AddressMatchVerified, bool ProgramMatchVerified, bool SourceAuthenticityVerified, bool NativeQualified,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? AlarmBindings = null);

/// <summary>Explicit source-to-observation declarations. Neither matching types nor saved XML proves the running program.</summary>
public static class RuntimeCaptureAssociations
{
    public static void ValidateRequest(RuntimeCaptureRequest request)
    {
        if(request.Association is not { } association)return;
        Require(request.ReviewId is not null && association.CaseId is {Length:64} && association.CaseId.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Require(association.Mappings is {Length:>=1 and <=64} && association.Mappings.Length==request.SelectionIds.Length);
        Require(association.Mappings.Select(x=>x.SelectionId).SequenceEqual(request.SelectionIds) &&
            association.Mappings.Select(x=>x.SignalId).Distinct(StringComparer.Ordinal).Count()==association.Mappings.Length);
        foreach(var mapping in association.Mappings) { Text(mapping.SignalId,64);Text(mapping.Reason,2000); }
        if(association.AssertedProgramIdentity is not null)Text(association.AssertedProgramIdentity,2000);
    }

    public static RuntimeCaptureAssociation? Resolve(string artifactsRoot,RuntimeCaptureRequest request,RuntimeCaptureSignal[] signals)
    {
        ValidateRequest(request);
        if(request.Association is not { } association)return null;
        var diagnostic=new DiagnosticStore(artifactsRoot).Read(association.CaseId,"diagnostic-case.v1");
        Require(D.Text(diagnostic,"reviewId")==request.ReviewId);
        var review=new ProjectReviewStore(artifactsRoot).Read(request.ReviewId!);
        var source=diagnostic.ContainsKey("alarmBindings")?RevalidateAlarmCase(diagnostic,review):new DiagnosticEvidence(review);
        var documents=new JsonArray(source.Documents.Values.Select(d=>(JsonNode?)new JsonObject {
            ["objectPath"]=d["objectPath"]?.DeepClone(),["status"]=d["status"]?.DeepClone(),["utf8Sha256"]=d["utf8Sha256"]?.DeepClone() }).ToArray());
        Require(JsonNode.DeepEquals(diagnostic["project"],review["model"]?["project"]) &&
            D.Text(diagnostic["sourceRevision"],"reviewId")==request.ReviewId &&
            JsonNode.DeepEquals(diagnostic["sourceRevision"]?["documents"],documents) &&
            diagnostic["sourceRevision"]?["onlineVerified"]?.GetValue<bool>()==false);
        string block=D.Required(diagnostic,"blockPath");
        Require(diagnostic["selectedInstance"] is JsonObject);
        var instance=diagnostic["selectedInstance"]!.AsObject();
        Require(source.Instances(block).Count(x=>JsonNode.DeepEquals(x,instance))==1);
        var selected=new JsonArray();
        foreach(var mapping in association.Mappings)
        {
            var matches=D.Rows(diagnostic["signals"]).OfType<JsonObject>().Where(x=>D.Text(x,"id")==mapping.SignalId).ToArray();
            Require(matches.Length==1 && D.Text(matches[0],"resolution")=="capturedDeclaration");
            var row=(JsonObject)matches[0].DeepClone();
            row["expectedDatatype"]=row["datatype"]?.DeepClone();
            selected.Add(row);
        }
        var hypotheses=D.Rows(diagnostic["hypotheses"]).Select(x=>D.Required(x!,"id",64)).ToHashSet(StringComparer.Ordinal);
        var resolved=DiagnosticPlanner.ResolveSignals(source,request.ReviewId!,instance,selected,hypotheses);
        // Re-resolve selectors; a content-addressed case is still supplied data, not trusted declaration evidence.
        string[] fields=["storagePath","memberPath","datatype","symbolComponents","namespace","scopePath","resolution","unavailableReason","traceability","evidence"];
        for(int i=0;i<resolved.Count;i++)foreach(string field in fields)
            Require(JsonNode.DeepEquals(selected[i]?[field],resolved[i]?[field]));
        var retained=new RuntimeCaptureAssociation(association.CaseId,request.ReviewId!,Json(diagnostic["project"]!),block,
            Json(instance),Json(diagnostic["sourceRevision"]!),association.Mappings.Select((m,i)=>new RuntimeCaptureMappedSignal(m.SelectionId,m.SignalId,Json(resolved[i]!))).ToArray(),false,false,false,false,
            diagnostic["alarmBindings"] is JsonObject alarm?Json(alarm):null);
        ValidateRetained(request,signals,retained);
        return retained;
    }

    public static void ValidateRetained(RuntimeCapturePlan plan)=>ValidateRetained(plan.Request,plan.Signals,plan.Association);
    internal static DiagnosticEvidence RevalidateAlarmCase(JsonObject diagnostic,JsonObject review)
    {
        try
        {
            var source=DiagnosticPlanner.Revalidate(diagnostic,review);
            DiagnosticAlarmBindings.RequireMatched(diagnostic);
            return source;
        }
        catch(BridgeException e) when(e.Code is "CaptureMismatch" or "AlarmBindingUnresolved")
        { throw Error(); }
    }
    internal static void ValidateRetained(RuntimeCaptureRequest capture,RuntimeCaptureSignal[] signals,RuntimeCaptureAssociation? retained)
    {
        if(capture.Association is not { } request){Require(retained is null);return;}
        Require(retained is not null);
        Require(retained!.CaseId==request.CaseId && retained.ReviewId==capture.ReviewId &&
            !retained.AddressMatchVerified && !retained.ProgramMatchVerified && !retained.SourceAuthenticityVerified && !retained.NativeQualified &&
            retained.Signals.Length==request.Mappings.Length && retained.Project.ValueKind==JsonValueKind.Object &&
            retained.SelectedInstance.ValueKind==JsonValueKind.Object && retained.SourceRevision.ValueKind==JsonValueKind.Object);
        Text(retained.BlockPath,4096);
        Require(retained.SourceRevision.GetProperty("reviewId").GetString()==retained.ReviewId && !retained.SourceRevision.GetProperty("onlineVerified").GetBoolean());
        for(int i=0;i<retained.Signals.Length;i++)
        {
            var row=retained.Signals[i];var declaration=row.Declaration;
            Require(row.SelectionId==request.Mappings[i].SelectionId && row.SignalId==request.Mappings[i].SignalId &&
                row.SelectionId==signals[i].SelectionId && declaration.GetProperty("id").GetString()==row.SignalId &&
                declaration.GetProperty("resolution").GetString()=="capturedDeclaration" &&
                declaration.GetProperty("traceability").GetString()=="requiresTargetValidation" &&
                !declaration.GetProperty("evidence").GetProperty("liveVerified").GetBoolean());
            Require(string.Equals(declaration.GetProperty("datatype").GetString(),signals[i].Metadata.GetProperty("datatype").GetString(),StringComparison.OrdinalIgnoreCase));
        }
        if(retained.AlarmBindings is { } alarm) ValidateAlarmBindings(retained,alarm);
    }
    private static void ValidateAlarmBindings(RuntimeCaptureAssociation association,JsonElement value)
    {
        try
        {
            var alarm=JsonNode.Parse(value.GetRawText())!.AsObject();
            Require(D.Text(alarm,"schemaVersion")=="diagnostic-alarm-bindings.v1" && D.Text(alarm,"reviewId")==association.ReviewId &&
                D.Text(alarm["alarmSelector"],"reviewId")==association.ReviewId && alarm["allSelectedMatched"]?.GetValue<bool>()==true);
            foreach(string flag in new[]{"sourceAuthenticityVerified","nativeQualified","runtimeAlarmAssociationVerified","coverageComplete"})
                Require(alarm[flag]?.GetValue<bool>()==false);
            var rows=D.Rows(alarm["bindings"]);var selectors=D.Rows(alarm["selectors"]);var ids=new HashSet<string>(StringComparer.Ordinal);
            Require(rows.Count is >=1 and <=16 && rows.Count==selectors.Count);
            for(int i=0;i<rows.Count;i++)
            {
                var row=rows[i]!;string id=D.Required(row,"id",64),signalId=D.Required(row,"signalId",64);
                Require(ids.Add(id) && D.Text(selectors[i],"id")==id && D.Text(selectors[i],"signalId")==signalId &&
                    D.Text(row,"status")=="capturedDeclarationMatch" && D.Text(row["signal"],"id")==signalId &&
                    D.Text(row["signal"],"resolution")=="capturedDeclaration" && row["signal"]?["evidence"]?["liveVerified"]?.GetValue<bool>()==false &&
                    D.Text(row["declaration"],"objectPath")==association.BlockPath && row["declaration"]?["liveVerified"]?.GetValue<bool>()==false &&
                    row["storage"]?["truncated"]?.GetValue<bool>()==false && row["storage"]?["runtimeCallContextVerified"]?.GetValue<bool>()==false &&
                    row["storage"]?["gaps"] is JsonArray gaps && gaps.Count==0);
                var mapped=association.Signals.SingleOrDefault(s=>s.SignalId==signalId);
                if(mapped is not null)
                    foreach(string field in new[]{"storagePath","memberPath","datatype","symbolComponents","namespace","scopePath","resolution","unavailableReason","traceability","evidence"})
                        Require(JsonNode.DeepEquals(row["signal"]?[field],JsonNode.Parse(mapped.Declaration.GetProperty(field).GetRawText())));
            }
        }
        catch(Exception e) when(e is BridgeException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException or FormatException)
        { throw Error(); }
    }
    private static JsonElement Json(JsonNode node)=>JsonSerializer.SerializeToElement(node,BridgeProtocol.Json);
    private static void Text(string value,int maximum)=>Require(!string.IsNullOrWhiteSpace(value) && value.Length<=maximum);
    private static void Require(bool condition){if(!condition)throw Error();}
    private static BridgeException Error()=>new("RuntimeCaptureAssociationMismatch","The explicit saved-case mapping, declaration, type or retained context does not match. No address or running-program equivalence is inferred.");
}
