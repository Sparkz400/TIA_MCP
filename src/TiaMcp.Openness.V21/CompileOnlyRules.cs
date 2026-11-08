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

        // Kinds whose apply compiles every device of the project, hardware and software, instead of one selected PLC.
        // project_simulation_enable is a project-wide setting that changes how every PLC compiles, so it compiles them all.
        private static readonly string[] DeviceCompiledKinds = { "cax_import", "network_configure", "network_io_assign", "drive_parameter_set", "drive_telegram_set", "drive_motor_configure", "drive_encoder_configure", "safety_drive_tags",
            "safety_fparameter_set", "safety_import_xml", "webserver_page_set", "umac_role_set", "opcua_server_settings_set", "project_simulation_enable", "network_mrp_set" };

        internal static bool CompilesEveryDevice(string kind) =>
            kind.StartsWith("hardware_", StringComparison.Ordinal) || kind.StartsWith("connection_", StringComparison.Ordinal) ||
            kind.StartsWith("certificate_", StringComparison.Ordinal) || Array.IndexOf(DeviceCompiledKinds, kind) >= 0;

        /// <summary>Dispatch only when the targets equal the reviewed preview, in order.</summary>
        internal static bool SameTargets(IEnumerable<string> reviewed, IEnumerable<string> current) =>
            reviewed != null && current != null && reviewed.SequenceEqual(current, StringComparer.Ordinal);
    }
}
