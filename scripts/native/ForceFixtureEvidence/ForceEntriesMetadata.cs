using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.WatchAndForceTables;

// This public-metadata collector never calls Create, Invoke or a setter.
internal static class ForceEntriesMetadata
{
    internal static JObject Capture(PlcForceTable table)
    {
        var obj = (IEngineeringObject)table;
        var result = new JObject { ["tableName"] = table.Name, ["runtimeType"] = table.GetType().FullName,
            ["nativeAuthoringInvoked"] = false, ["nativeSignalCreationQualified"] = false,
            ["tableInvocations"] = Observe(() => Invocations(obj.GetInvocationInfos())) };
        var names = obj.GetCompositionInfos().Select(i => i.Name).ToArray();
        if (names.Length > 128) throw new InvalidOperationException("Force composition metadata bound exceeded.");
        result["advertisedCompositions"] = new JArray(names);
        if (!names.Contains("Entries", StringComparer.Ordinal))
        {
            result["entriesState"] = "notAdvertised"; return result;
        }
        result["objectCreationInfos"] = Observe(() => Creation(obj.GetCreationInfos("Entries")));
        try
        {
            var native = obj.GetComposition("Entries");
            var composition = native as IEngineeringComposition;
            result["entriesRuntimeType"] = native?.GetType().FullName;
            result["entriesState"] = composition == null ? "notEngineeringComposition" : "observed";
            if (composition != null)
            {
                result["compositionCreationInfos"] = Observe(() => Creation(composition.GetCreationInfos()));
                result["compositionInvocations"] = Observe(() => Invocations(composition.GetInvocationInfos()));
            }
        }
        catch (Exception e) { result["entriesState"] = "failed"; result["entriesFailure"] = Failure(e); }
        return result;
    }

    private static JArray Creation(IList<EngineeringCreationInfo> source)
    {
        if (source.Count > 128) throw new InvalidOperationException("Creation metadata bound exceeded.");
        return new JArray(source.Select(c => new JObject { ["type"] = c.Type.FullName, ["assembly"] = c.Type.Assembly.FullName,
            ["isExactSignalEntryType"] = c.Type == typeof(PlcForceTableEntry), ["isExactCommentEntryType"] = c.Type == typeof(PlcTableCommentEntry),
            ["parameters"] = new JArray(c.ParameterInfos.Select(p => new JObject { ["name"] = p.Name, ["mandatory"] = p.IsMandatory,
                ["supportedTypes"] = new JArray(p.SupportedTypes.Select(t => t.FullName)) })) }));
    }
    private static JArray Invocations(IList<EngineeringInvocationInfo> source)
    {
        if (source.Count > 128) throw new InvalidOperationException("Invocation metadata bound exceeded.");
        return new JArray(source.Select(i => new JObject { ["name"] = i.Name, ["type"] = i.Type.FullName,
            ["parameters"] = new JArray(i.ParameterInfos.Select(p => new JObject { ["name"] = p.Name, ["type"] = p.Type.FullName })) }));
    }
    private static JObject Observe(Func<JArray> read)
    {
        try { return new JObject { ["state"] = "returned", ["items"] = read() }; }
        catch (Exception e) { return new JObject { ["state"] = "failed", ["failure"] = Failure(e) }; }
    }
    private static JObject Failure(Exception e) => new JObject { ["type"] = e.GetType().FullName, ["message"] = e.Message };
}
