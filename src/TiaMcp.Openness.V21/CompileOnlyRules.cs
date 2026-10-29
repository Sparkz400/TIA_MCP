using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure payload and scope decisions for the compile-only kinds device_compile, project_compile and hmi_compile.
    internal static class CompileOnlyRules
    {
        /// <summary>The refusal message, or null when the payload has exactly the kind's fields.</summary>
        internal static string PayloadRefusal(string kind, JObject payload)
        {
            if (payload == null) return "payload must be an object.";
            switch (kind)
            {
                case "project_compile": return payload.Count == 0 ? null : "Project compilation requires an empty payload.";
                case "device_compile":
                    return payload.Count == 1 && payload["devicePath"]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(payload.Value<string>("devicePath"))
                        ? null : "Device compilation accepts only a nonempty devicePath.";
                case "hmi_compile": return payload.Count == 1 && payload["target"] is JObject ? null : "HMI compilation accepts only a target object.";
                default: return "Unknown compile-only kind: " + kind;
            }
        }

        /// <summary>Dispatch only when the targets equal the reviewed preview, in order.</summary>
        internal static bool SameTargets(IEnumerable<string> reviewed, IEnumerable<string> current) =>
            reviewed != null && current != null && reviewed.SequenceEqual(current, StringComparer.Ordinal);
    }
}
