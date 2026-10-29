#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Simulation
{
    // Validates virtual-time, event-injection and storage requests for an owned PLCSIM Advanced V8 instance before any
    // runtime call. Event codes are the numeric values of the V8 API enums (SimulationRuntimeApi.h); the worker casts them.
    internal static class SimulationRuntimeControl
    {
        private static readonly Dictionary<string, int> ProcessEdges = new Dictionary<string, int>(StringComparer.Ordinal)
            { ["risingEdge"] = 1, ["fallingEdge"] = 2, ["limit1Underrun"] = 3, ["limit1Overrun"] = 4, ["limit2Underrun"] = 5, ["limit2Overrun"] = 6 };
        private static readonly Dictionary<string, int> PullOrPlug = new Dictionary<string, int>(StringComparer.Ordinal)
            { ["pull"] = 1, ["plug"] = 2, ["plugErrorRemains"] = 3, ["plugWrongModule"] = 4 };
        private static readonly Dictionary<string, int> RackOrStation = new Dictionary<string, int>(StringComparer.Ordinal) { ["fault"] = 0, ["return"] = 1 };
        private static readonly Dictionary<string, int> ModuleStates = new Dictionary<string, int>(StringComparer.Ordinal)
            { ["ok"] = 0, ["error"] = 1, ["maintenanceDemanded"] = 2, ["maintenanceRequired"] = 4 };
        private static readonly Dictionary<string, int> Severities = new Dictionary<string, int>(StringComparer.Ordinal)
            { ["failure"] = 0, ["maintenanceDemanded"] = 1, ["maintenanceRequired"] = 2 };
        private static readonly Dictionary<string, int> Directions = new Dictionary<string, int>(StringComparer.Ordinal) { ["appear"] = 1, ["disappear"] = 2 };

        /// <summary>V8 accepts 0..1000; zero would stop the virtual clock, so the bound here is exclusive.</summary>
        public static double ScaleFactor(JToken value)
        {
            if (value == null || value.Type != JTokenType.Float && value.Type != JTokenType.Integer) throw new ArgumentException("scaleFactor must be a number.");
            double factor = value.Value<double>();
            if (double.IsNaN(factor) || factor <= 0 || factor > 1000) throw new ArgumentException("scaleFactor must be greater than 0 and at most 1000.");
            return factor;
        }

        /// <summary>An explicit UTC instant within V8's range (1 Jan 1970 to 31 Dec 2200).</summary>
        public static DateTime SystemTimeUtc(JToken value)
        {
            // The worker parses requests with DateParseHandling.None, so an instant arrives as its exact string.
            string text = value?.Type == JTokenType.String ? value.Value<string>() : null;
            if (text == null ||!text.EndsWith("Z", StringComparison.Ordinal) ||
                !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                throw new ArgumentException("systemTimeUtc must be an ISO 8601 UTC string ending in Z.");
            time = DateTime.SpecifyKind(time, DateTimeKind.Utc);
            if (time < new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) || time >= new DateTime(2201, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                throw new ArgumentException("systemTimeUtc must lie between 1970-01-01 and 2200-12-31.");
            return time;
        }

        /// <summary>Normalized event with the V8 enum codes; every field is checked before the first runtime call.</summary>
        public static JObject Event(JToken input)
        {
            var item = input as JObject ?? throw new ArgumentException("event must be an object.");
            string type = Text(item, "type");
            var result = new JObject { ["type"] = type, ["hardwareId"] = UInt16(item, "hardwareId", 1) };
            string[] allowed;
            switch (type)
            {
                case "hardwareInterrupt":
                    result["channel"] = UInt16(item, "channel", 0);
                    result["edge"] = Text(item, "edge"); result["code"] = Code(ProcessEdges, item, "edge");
                    allowed = new[] { "type", "hardwareId", "channel", "edge" }; break;
                case "pullOrPlug":
                    result["action"] = Text(item, "action"); result["code"] = Code(PullOrPlug, item, "action");
                    allowed = new[] { "type", "hardwareId", "action" }; break;
                case "rackOrStationFault":
                    result["action"] = Text(item, "action"); result["code"] = Code(RackOrStation, item, "action");
                    allowed = new[] { "type", "hardwareId", "action" }; break;
                case "statusEvent":
                    result["specifier"] = UInt16(item, "specifier", 0);
                    allowed = new[] { "type", "hardwareId", "specifier" }; break;
                case "diagnosticAlarm":
                {
                    result["moduleState"] = Text(item, "moduleState"); result["moduleStateCode"] = Code(ModuleStates, item, "moduleState");
                    var channels = item["channels"] as JArray ?? throw new ArgumentException("diagnosticAlarm requires a channels array (it may be empty).");
                    if (channels.Count > 16) throw new ArgumentException("diagnosticAlarm accepts at most 16 channel items.");
                    result["channels"] = new JArray(channels.Select(token =>
                    {
                        var channel = token as JObject ?? throw new ArgumentException("Each diagnostic channel must be an object.");
                        if (channel.Properties().Any(p => Array.IndexOf(new[] { "channel", "errorType", "extErrorType", "severity", "direction" }, p.Name) < 0))
                            throw new ArgumentException("Unknown diagnostic channel field.");
                        return new JObject { ["channel"] = UInt16(channel, "channel", 0), ["errorType"] = UInt16(channel, "errorType", 0),
                            ["extErrorType"] = channel["extErrorType"] == null ? 0 : UInt16(channel, "extErrorType", 0),
                            ["severity"] = Text(channel, "severity"), ["severityCode"] = Code(Severities, channel, "severity"),
                            ["direction"] = Text(channel, "direction"), ["directionCode"] = Code(Directions, channel, "direction") };
                    }));
                    allowed = new[] { "type", "hardwareId", "moduleState", "channels" }; break;
                }
                default: throw new ArgumentException("event.type must be hardwareInterrupt, pullOrPlug, rackOrStationFault, diagnosticAlarm or statusEvent.");
            }
            var unknown = item.Properties().Select(p => p.Name).Where(name => Array.IndexOf(allowed, name) < 0).ToArray();
            if (unknown.Length != 0) throw new ArgumentException("Unknown " + type + " field: " + unknown[0]);
            return result;
        }

        /// <summary>The interrupt OB the event is documented to start on an S7-1500 (V8 API manual).</summary>
        public static string ExpectedOb(string type) => type == "hardwareInterrupt" ? "OB40 (hardware interrupt)" : type == "pullOrPlug" ? "OB83 (pull/plug)"
            : type == "rackOrStationFault" ? "OB86 (rack or station failure)" : type == "diagnosticAlarm" ? "OB82 (diagnostic error)" : "OB55 (status)";

        /// <summary>A retrieve accepts only an unchanged archive this adapter wrote from the same owned instance.</summary>
        public static string ArchiveRefusal(string archivePath, string directory, JObject receipt, int instanceId, string instanceName, string actualSha256, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !Path.IsPathRooted(archivePath) || !string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(archivePath)), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return "SimulationArchiveOutsideStore";
            if (receipt == null || receipt.Value<string>("archivePath") == null || !string.Equals(Path.GetFullPath(receipt.Value<string>("archivePath")), Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase))
                return "SimulationArchiveReceiptMissing";
            if (receipt.Value<int?>("instanceId") != instanceId || receipt.Value<string>("instanceName") != instanceName) return "SimulationArchiveTargetMismatch";
            if (string.IsNullOrEmpty(expectedSha256) || expectedSha256 != receipt.Value<string>("sha256") || expectedSha256 != actualSha256) return "SimulationArchiveChanged";
            return null;
        }

        private static string Text(JObject item, string name)
        {
            if (item[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>(name))) throw new ArgumentException(name + " must be a nonempty string.");
            return item.Value<string>(name);
        }

        private static int Code(Dictionary<string, int> codes, JObject item, string name)
        {
            if (!codes.TryGetValue(Text(item, name), out int code)) throw new ArgumentException(name + " must be one of " + string.Join(", ", codes.Keys) + ".");
            return code;
        }

        private static int UInt16(JObject item, string name, int minimum)
        {
            if (item[name]?.Type != JTokenType.Integer) throw new ArgumentException(name + " must be an integer.");
            long value = item.Value<long>(name);
            if (value < minimum || value > ushort.MaxValue) throw new ArgumentException(name + " must be between " + minimum + " and 65535.");
            return (int)value;
        }
    }
}
