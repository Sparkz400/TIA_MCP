using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Diagnostics;

namespace TiaMcp.Core.PhysicalRuntime;

/// <summary>Sequential cycle observations, never atomic samples or qualified behavioral assertions.</summary>
public sealed class RuntimeCaptureAnalysis(string artifactsRoot)
{
    private sealed class Prediction(JsonObject source,JsonObject[]? when,JsonObject[]? expect)
    {
        internal JsonObject Source=source;
        internal JsonObject[]? When=when,Expect=expect;
        internal int Matched,Contradicted,Unknown;
        internal JsonArray Examples=new();
    }

    public JsonObject Analyze(RuntimeCaptureStore captures,string snapshotRef,JsonObject units)
    {
        var report=Build(captures,snapshotRef,units);
        var receipt=new DiagnosticStore(artifactsRoot).Save(report);receipt["report"]=report;return receipt;
    }

    internal JsonObject Build(RuntimeCaptureStore captures,string snapshotRef,JsonObject units)
    {
        D.Bound(units);
        var snapshot=captures.ReadSnapshot(snapshotRef);var plan=snapshot.Plan;
        if (plan.Handoff is not null) RuntimeCaptureHandoffs.ValidateEvidence(artifactsRoot, plan.Handoff);
        var association=plan.Association??throw Error("RuntimeCaptureAssociationRequired");
        var store=new DiagnosticStore(artifactsRoot);
        var diagnostic=store.Read(association.CaseId,"diagnostic-case.v1");
        Require(D.Text(diagnostic,"reviewId")==association.ReviewId &&
            Same(diagnostic["selectedInstance"],association.SelectedInstance) && Same(diagnostic["sourceRevision"],association.SourceRevision));
        if(association.AlarmBindings is { } alarm)
        {
            RuntimeCaptureAssociations.RevalidateAlarmCase(diagnostic,new ProjectReviewStore(artifactsRoot).Read(association.ReviewId));
            Require(Same(diagnostic["alarmBindings"],alarm));
        }
        string symptom=D.Required(diagnostic,"report",8000);
        var sourceSignals=D.Rows(diagnostic["signals"]);Require(sourceSignals.Count is >=1 and <=64);
        var signals=sourceSignals.OfType<JsonObject>().ToDictionary(s=>D.Required(s,"id",64),StringComparer.Ordinal);
        Require(signals.Count==sourceSignals.Count);
        var mapped=association.Signals.ToDictionary(x=>x.SelectionId,StringComparer.Ordinal);
        Require(units.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal).SetEquals(mapped.Values.Select(x=>x.SignalId)));
        foreach(var row in mapped.Values)
        {
            Require(signals.TryGetValue(row.SignalId,out var source));
            foreach(string key in new[]{"storagePath","memberPath","datatype","symbolComponents","namespace","scopePath","resolution","traceability","evidence"})
                Require(Same(source![key],row.Declaration.GetProperty(key)));
            string unit=D.Required(units,row.SignalId,64);
            if(D.Text(source,"datatype").Equals("Bool",StringComparison.OrdinalIgnoreCase))Require(unit=="1");
        }
        var hypotheses=D.Rows(diagnostic["hypotheses"]);Require(hypotheses.Count is >=1 and <=12);
        TraceMeasurements.ValidatePredictions(hypotheses,sourceSignals);
        var predictions=new List<Prediction>();var hypothesisIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var item in hypotheses)
        {
            Require(item is JsonObject);var h=item!.AsObject();Require(hypothesisIds.Add(D.Required(h,"id",64)));D.Required(h,"statement",2000);
            var rule=h["prediction"] as JsonObject;
            var when=rule is null?null:TraceMeasurements.Conditions(rule["when"],signals);
            var expect=rule is null?null:TraceMeasurements.Conditions(rule["expect"],signals);
            foreach(var condition in (when??[]).Concat(expect??[]))
            {
                string id=D.Text(condition,"signalId");
                if(units.ContainsKey(id) && !D.Text(signals[id],"datatype").Equals("Bool",StringComparison.OrdinalIgnoreCase))
                    Require(D.Text(units,id)==D.Text(condition,"unit"));
            }
            predictions.Add(new(h,when,expect));
        }
        var values=EmptyValues(signals);var observations=new JsonObject();var seen=new HashSet<string>(StringComparer.Ordinal);
        JsonObject? previousValues=null,previousObservations=null;long previousCycle=-2,cycle=-1;
        int cycles=0,missing=0,hostErrors=0,providerFailures=0,unsupportedValues=0,changes=0;
        double previousElapsed=-1;bool cycleHasGap=false;var observedChanges=new JsonArray();var unavailableReasons=new JsonObject();
        void Unavailable(string reason){unavailableReasons[reason]=(unavailableReasons[reason]?.GetValue<int>()??0)+1;}
        void FinishCycle()
        {
            if(cycle<0)return;
            cycles++;missing+=values.Count(x=>x.Value is null);
            foreach(var prediction in predictions)
            {
                if(prediction.When is null)continue;
                bool? applies=TraceMeasurements.All(prediction.When,values);
                if(applies==false)continue;
                bool? matched=applies is null?null:TraceMeasurements.All(prediction.Expect!,values);
                if(matched is null)prediction.Unknown++;else if(matched.Value)prediction.Matched++;else prediction.Contradicted++;
                if(prediction.Examples.Count<20)prediction.Examples.Add(new JsonObject { ["cycle"]=cycle,["patternMatched"]=matched,
                    ["values"]=values.DeepClone(),["reads"]=observations.DeepClone() });
            }
            if(previousValues is not null && previousCycle+1==cycle)
                foreach(string id in signals.Keys)
                {
                    var before=previousValues[id];var after=values[id];
                    if(before is null || after is null || TraceMeasurements.Equal(before,after))continue;
                    changes++;
                    if(observedChanges.Count<200)observedChanges.Add(new JsonObject{["signalId"]=id,["before"]=before.DeepClone(),["after"]=after.DeepClone(),
                        ["beforeRead"]=previousObservations![id]!.DeepClone(),["afterRead"]=observations[id]!.DeepClone()});
                }
            previousCycle=cycleHasGap?-2:cycle;previousValues=values;previousObservations=observations;
            values=EmptyValues(signals);observations=new JsonObject();seen.Clear();cycleHasGap=false;
        }
        foreach(var row in snapshot.Rows)
        {
            if(row.Cycle<cycle || row.ElapsedMilliseconds<previousElapsed)throw Error("RuntimeCaptureAnalysisOrderInvalid");
            previousElapsed=row.ElapsedMilliseconds;
            if(row.Cycle!=cycle){FinishCycle();cycle=row.Cycle;}
            if(row.Kind=="gap"){cycleHasGap=true;continue;}
            if(!seen.Add(row.SelectionId!))throw Error("RuntimeCaptureAnalysisOrderInvalid");
            string id=mapped[row.SelectionId!].SignalId;
            var observation=new JsonObject{["sequence"]=row.Sequence,["cycle"]=row.Cycle,["selectionId"]=row.SelectionId,
                ["scheduledMilliseconds"]=row.ScheduledMilliseconds,["elapsedMilliseconds"]=row.ElapsedMilliseconds,["observedUtc"]=row.ObservedUtc};
            observations[id]=observation;
            string? unavailable=null;
            if(row.ErrorCode is not null){hostErrors++;unavailable=row.ErrorCode;}
            else
            {
                var read=JsonNode.Parse(row.ReadResultJson!)!.AsObject();
                foreach(string key in new[]{"status","requestStartedUtc","responseReceivedUtc","hostClockWentBackwards","providerErrorCode"})observation[key]=read[key]?.DeepClone();
                string status=D.Text(read,"status");
                if(status!="read"){providerFailures++;unavailable=status;}
                else if(plan.Request.Representation!="simple"){unsupportedValues++;unavailable="rawRepresentation";}
                else
                {
                    var value=read["value"];string datatype=D.Text(signals[id],"datatype");
                    try
                    {
                        if(datatype.Equals("Bool",StringComparison.OrdinalIgnoreCase))
                        { if(value is not JsonValue boolean || !boolean.TryGetValue<bool>(out _))throw Error("RuntimeCaptureValueUnsupported"); }
                        else TraceMeasurements.ValidateNumericSample(TraceMeasurements.Number(value),datatype);
                        values[id]=value!.DeepClone();
                    }
                    catch(BridgeException){unsupportedValues++;unavailable="valueNotRepresentableAsDeclaredScalar";}
                }
            }
            observation["unavailableReason"]=unavailable;
            if(unavailable is not null)Unavailable(unavailable);
        }
        FinishCycle();
        var findings=new JsonArray(predictions.Select(p=>(JsonNode?)new JsonObject{["id"]=p.Source["id"]!.DeepClone(),["statement"]=p.Source["statement"]!.DeepClone(),
            ["prediction"]=p.Source["prediction"]?.DeepClone(),["verdict"]="unresolved",["causationProven"]=false,
            ["matchedCycles"]=p.Matched,["contradictedCycles"]=p.Contradicted,["unknownCycles"]=p.Unknown,["examples"]=p.Examples,
            ["reason"]=p.When is null?"No bounded declarative prediction supplied.":"Sequential polling cycles and unverified address/program identity cannot establish a passing or failing behavioral assertion. Counts describe observed patterns only."}).ToArray());
        var report=new JsonObject{["schemaVersion"]="runtime-capture-analysis.v1",["snapshotRef"]=snapshotRef,["caseId"]=association.CaseId,["reviewId"]=association.ReviewId,
            ["symptom"]=symptom,["hypotheses"]=findings,["units"]=units.DeepClone(),["unitsOrigin"]="callerSuppliedNoConversion",
            ["association"]=JsonSerializer.SerializeToNode(association,BridgeProtocol.Json),
            ["capture"]=new JsonObject{["captureId"]=plan.CaptureId,["recordedProgress"]=JsonSerializer.SerializeToNode(snapshot.Progress,BridgeProtocol.Json),
                ["request"]=JsonSerializer.SerializeToNode(plan.Request,BridgeProtocol.Json),["signals"]=JsonSerializer.SerializeToNode(plan.Signals,BridgeProtocol.Json),
                ["context"]=JsonSerializer.SerializeToNode(plan.Signals[0].Selection.Context,BridgeProtocol.Json),["uncommittedDataUnknown"]=snapshot.Progress.State=="running"},
            ["qualityAssessment"]=new JsonObject{["cyclesObserved"]=cycles,["missingValues"]=missing,["hostReadErrors"]=hostErrors,["providerFailures"]=providerFailures,
                ["unsupportedValues"]=unsupportedValues,["unavailableReasons"]=unavailableReasons,["skippedPolls"]=snapshot.Progress.SkippedPolls,["droppedReadResults"]=snapshot.Progress.DroppedReadResults,
                ["uncapturedSignalIds"]=D.Strings(signals.Keys.Except(mapped.Values.Select(x=>x.SignalId),StringComparer.Ordinal))},
            ["observedChanges"]=observedChanges,["observedChangeCount"]=changes,["observedChangesTruncated"]=changes>200,
            ["atomicAcrossSignals"]=false,["scanSynchronized"]=false,["programMatchVerified"]=false,["addressMatchVerified"]=false,
            ["sourceAuthenticityVerified"]=false,["clockSynchronizationVerified"]=false,["nativeQualification"]=false,["causationProven"]=false,
            ["plcChangesBetweenPollsUnknown"]=true,["coverageGaps"]=diagnostic["coverageGaps"]?.DeepClone(),
            ["nextCapture"]="Retain exact source and independently establish address/program identity and acquisition suitability. Missing, bad, raw or nonrepresentable values stay unknown; skipped cycles are not fabricated. Observed changes compare consecutive cycles only and do not timestamp PLC transitions."};
        if (plan.Handoff is not null) report["handoff"] = JsonSerializer.SerializeToNode(plan.Handoff, BridgeProtocol.Json);
        return report;
    }
    private static JsonObject EmptyValues(Dictionary<string,JsonObject> signals)=>new(signals.Keys.Select(k=>new KeyValuePair<string,JsonNode?>(k,null)));
    private static bool Same(JsonNode? node,JsonElement value)=>JsonNode.DeepEquals(node,JsonNode.Parse(value.GetRawText()));
    private static void Require(bool condition){if(!condition)throw Error("RuntimeCaptureAnalysisMismatch");}
    private static BridgeException Error(string code)=>new(code,"Capture analysis needs an exact retained case, matching source mappings and explicit units. No native assertion or acquisition is performed.");
}
