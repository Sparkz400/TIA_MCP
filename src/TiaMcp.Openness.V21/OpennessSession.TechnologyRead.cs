#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static bool IsSensitiveAttribute(string name) => Regex.IsMatch(name, @"password|credential|privatekey|secret|token", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static JObject EngineeringParameterInfo(TechnologicalParameter parameter)
            => TechnologyParameterInfo(parameter, ChangeScalar);

        private static JObject TechnologyParameterInfo(TechnologicalParameter parameter, Func<object, JToken> render)
        {
            string name = parameter.Name;
            var row = new JObject { ["name"] = name, ["type"] = null, ["valueAccess"] = "NotExposed",
                ["writeAccessKnown"] = false, ["writable"] = null, ["readAccessKnown"] = false, ["readable"] = null,
                ["valueCaptured"] = false, ["valueComplete"] = false, ["valueStatus"] = "unknownAccess" };
            if (IsSensitiveAttribute(name))
            {
                row["excluded"] = true; row["value"] = "<excluded>"; row["valueStatus"] = "excluded";
                return row;
            }
            try
            {
                var declarations = parameter.GetAttributeInfos()?.Where(info => info?.Name == "Value").Take(2).ToArray();
                if (declarations == null || declarations.Length != 1)
                {
                    row["error"] = "Parameter requires one explicit Value access declaration.";
                    return row;
                }
                var info = declarations[0];
                row["valueAccess"] = info.AccessMode.ToString();
                row["writeAccessKnown"] = true; row["readAccessKnown"] = true;
                row["writable"] = (info.AccessMode & EngineeringAttributeAccessMode.Write) != 0;
                bool readable = (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0;
                row["readable"] = readable;
                var types = info.SupportedTypes?.Take(65).ToArray();
                if (types?.Length > 64)
                {
                    row["valueStatus"] = "metadataLimit";
                    row["error"] = "Parameter supported-type metadata exceeds 64 entries.";
                    return row;
                }
                row["supportedTypes"] = types == null ? (JToken)JValue.CreateNull() : new JArray(types.Select(type => type?.FullName));
                if (!readable)
                {
                    row["valueStatus"] = "notReadable";
                    row["error"] = "Parameter Value does not declare read access.";
                    return row;
                }
            }
            catch (Exception)
            {
                row["valueStatus"] = "metadataError";
                row["error"] = "Native parameter metadata could not be read.";
                return row;
            }
            try
            {
                object value = parameter.Value;
                row["type"] = value?.GetType().FullName;
                JToken rendered = render(value);
                if (rendered == null) throw new InvalidOperationException();
                row["value"] = rendered;
                row["valueCaptured"] = true;
                bool scalar = value == null || value is string || value is bool || value is byte || value is sbyte ||
                    value is short || value is ushort || value is int || value is uint || value is long || value is ulong ||
                    value is float || value is double || value is decimal || value.GetType().IsEnum;
                bool finite = !(value is double d && (double.IsNaN(d) || double.IsInfinity(d))) &&
                    !(value is float f && (float.IsNaN(f) || float.IsInfinity(f)));
                bool complete = scalar && finite && rendered is JValue;
                row["valueComplete"] = complete;
                row["valueStatus"] = complete ? "captured" : "displayOnly";
            }
            catch (Exception)
            {
                // Native exception text may contain a parameter value; retain a bounded generic failure instead.
                row["valueStatus"] = "readError";
                row["error"] = "Native parameter value could not be captured.";
            }
            return row;
        }

        private static JArray CaptureTechnologyParameters(IEnumerable<TechnologicalParameter> parameters, string prefix,
            Func<bool> take, Func<object, JToken> render, out bool scanTruncated, out int visited)
        {
            var rows = new JArray(); scanTruncated = false; visited = 0;
            foreach (var parameter in parameters)
            {
                if (visited == 50000) { scanTruncated = true; break; }
                visited++;
                if (!string.IsNullOrEmpty(prefix) && !parameter.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!take()) break;
                rows.Add(TechnologyParameterInfo(parameter, render));
            }
            return rows;
        }

        private static bool TechnologyParametersComplete(JArray rows)
            => rows.All(row => row.Value<bool?>("valueComplete") == true && !string.IsNullOrEmpty(row.Value<string>("name"))) &&
                rows.Select(row => row.Value<string>("name")).Distinct(StringComparer.Ordinal).Count() == rows.Count;

        private static JArray TechnologyParameterWarnings(JArray rows)
        {
            var warnings = new JArray(rows.Where(row => row.Value<bool?>("valueComplete") != true).Select(row => new JObject {
                ["path"] = row["name"]?.DeepClone(), ["message"] = row["error"]?.DeepClone() ?? new JValue("Parameter value is excluded, unresolved or display-only.") }));
            if (rows.Any(row => string.IsNullOrEmpty(row.Value<string>("name"))) ||
                rows.Select(row => row.Value<string>("name")).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                warnings.Add(new JObject { ["path"] = null, ["message"] = "Parameter identities are missing or duplicated." });
            return warnings;
        }
    }
}
