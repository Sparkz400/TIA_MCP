using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Independent fixture evidence capture. Bounds must never silently turn an
// incomplete native diagnostic tree into a successful compile prerequisite.
internal static class HmiCompilerDiagnostics
{
    internal static JArray Capture<T>(IEnumerable<T> messages, Func<T, string> state,
        Func<T, string> description, Func<T, string> path, Func<T, IEnumerable<T>> children,
        int maximumMessages = 500, int maximumDepth = 32)
    {
        if (maximumMessages < 1 || maximumDepth < 1) throw new ArgumentOutOfRangeException();
        int remaining = maximumMessages;
        return CaptureLevel(messages, state, description, path, children, ref remaining, maximumDepth);
    }

    private static JArray CaptureLevel<T>(IEnumerable<T> messages, Func<T, string> state,
        Func<T, string> description, Func<T, string> path, Func<T, IEnumerable<T>> children,
        ref int remaining, int depth)
    {
        var result = new JArray();
        if (messages == null) { result.Add(Incomplete("Native diagnostic collection is unavailable.")); return result; }
        foreach (var message in messages)
        {
            if (remaining == 0 || depth == 0)
            {
                result.Add(Incomplete(remaining == 0 ? "Native diagnostic message budget exhausted." : "Native diagnostic depth bound reached."));
                break;
            }
            remaining--;
            result.Add(new JObject { ["state"] = state(message), ["description"] = description(message), ["path"] = path(message),
                ["children"] = CaptureLevel(children(message), state, description, path, children, ref remaining, depth - 1) });
        }
        return result;
    }

    private static JObject Incomplete(string reason) => new JObject {
        ["diagnosticCaptureIncomplete"] = true, ["reason"] = reason };

    internal static bool IsComplete(JToken messages) => messages is JArray rows && rows.All(row =>
        row is JObject && row.Value<bool?>("diagnosticCaptureIncomplete") != true &&
        new[] { "Success", "Information", "Warning", "Error" }.Contains(row.Value<string>("state")) && IsComplete(row["children"]));
}
