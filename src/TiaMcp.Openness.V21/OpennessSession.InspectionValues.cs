#nullable disable
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private delegate JToken InspectionValueRenderer(object value, out bool complete);

        private static JArray InspectionAttributes(IEngineeringObject target, ScanBudget budget, JArray warnings, string path, InspectionValueRenderer render = null, Func<string, bool> include = null)
        {
            var rows = new JArray();
            try
            {
                // Establish unique identities before paging can hide a conflicting declaration.
                var metadata = target.GetAttributeInfos()?.Take(4097)
                    .Select(info => new { Name = info?.Name, Info = info }).ToArray();
                if (metadata == null || metadata.Length > 4096 || metadata.Any(info => string.IsNullOrEmpty(info.Name) || info.Name.Length > 4096))
                {
                    if (metadata?.Length > 4096) budget.Truncated = true;
                    warnings.Add(new JObject { ["path"] = path, ["message"] = "Attribute metadata is missing, invalid or exceeds 4096 declarations." });
                    return rows;
                }
                var counts = metadata.GroupBy(info => info.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                foreach (var attribute in metadata)
                {
                    if (include != null && !include(attribute.Name)) continue;
                    if (!budget.Take()) break;
                    string name = attribute.Name;
                    var row = new JObject { ["name"] = name, ["readAccessKnown"] = false, ["readable"] = null,
                        ["type"] = null, ["valueCaptured"] = false, ["valueComplete"] = false, ["valueStatus"] = "unknownAccess" };
                    rows.Add(row);
                    if (IsSensitiveAttribute(name)) { row["excluded"] = true; row["valueStatus"] = "excluded"; row["value"] = "<excluded>"; }
                    else if (counts[name] != 1) row["valueStatus"] = "ambiguousIdentity";
                    else
                    {
                        try
                        {
                            var access = attribute.Info.AccessMode;
                            row["accessMode"] = access.ToString(); row["readAccessKnown"] = true;
                            row["readable"] = (access & EngineeringAttributeAccessMode.Read) != 0;
                            row["valueStatus"] = "notReadable";
                            if (row.Value<bool>("readable"))
                            {
                                row["valueStatus"] = "readError";
                                object value = target.GetAttribute(name);
                                row["type"] = value?.GetType().FullName;
                                bool complete;
                                row["value"] = render == null ? InspectionValue(value, out complete) : render(value, out complete);
                                row["valueCaptured"] = true; row["valueComplete"] = complete;
                                row["valueStatus"] = complete ? "captured" : "displayOnly";
                            }
                        }
                        catch (Exception) { row["error"] = "Native attribute metadata or value could not be captured."; }
                    }
                    if (row.Value<bool>("valueComplete") != true)
                        warnings.Add(new JObject { ["path"] = path + "/" + name, ["valueStatus"] = row["valueStatus"].DeepClone(),
                            ["message"] = "Attribute value is excluded, unreadable, unresolved or display-only." });
                }
            }
            catch (Exception)
            {
                // Native exception text can contain attribute values.
                warnings.Add(new JObject { ["path"] = path, ["message"] = "Native attribute metadata could not be captured." });
            }
            return rows;
        }

        private sealed class InspectionValueBudget
        {
            internal Func<object, JObject> ResolveReference;
            internal int Remaining = 512;
            internal bool Complete = true;
            internal JObject Incomplete(string reason, bool truncated = false)
            {
                Complete = false;
                return new JObject { ["valueComplete"] = false, ["valueStatus"] = reason, ["truncated"] = truncated };
            }
        }

        private static JToken InspectionValue(object value) => InspectionValue(value, out _);

        private static JToken InspectionValue(object value, out bool complete) => InspectionValue(value, out complete, null);

        private static JToken InspectionValue(object value, out bool complete, Func<object, JObject> resolveReference)
        {
            var budget = new InspectionValueBudget { ResolveReference = resolveReference };
            JToken result;
            try { result = InspectionValue(value, budget, 0); }
            catch (Exception) { result = budget.Incomplete("readError"); }
            complete = budget.Complete;
            return result;
        }

        private static JToken InspectionValue(object value, InspectionValueBudget budget, int depth)
        {
            if (depth > 16 || budget.Remaining == 0) return budget.Incomplete("traversalLimit", true);
            budget.Remaining--;
            if (value == null) return JValue.CreateNull();
            if (value is string text)
            {
                if (text.Length <= 4096) return new JValue(text);
                var result = budget.Incomplete("textLimit", true);
                int length = char.IsHighSurrogate(text[4095]) ? 4095 : 4096;
                result["text"] = text.Substring(0, length); result["originalCharacters"] = text.Length;
                return result;
            }
            if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)) || value is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                return budget.Incomplete("nonFinite");
            if (value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
                return new JValue(value);
            if (value.GetType().IsEnum) return InspectionValue(value.ToString(), budget, depth + 1);
            // Preserve the existing library-version string contract, without invoking arbitrary ToString implementations.
            if (value is Version) return InspectionValue(value.ToString(), budget, depth + 1);
            if (value is DateTime || value is DateTimeOffset || value is TimeSpan || value is Guid)
            {
                var display = budget.Incomplete("displayOnly");
                display["text"] = Convert.ToString(value, CultureInfo.InvariantCulture); return display;
            }
            if (value is Channel || value is DeviceItem || value is PlcTag || value is PlcBlock || value is PlcUnitBase || value is IEngineeringInstance)
            {
                var reference = budget.ResolveReference?.Invoke(value);
                if (reference != null)
                {
                    if (reference.Value<bool?>("valueComplete") != true) budget.Complete = false;
                    return reference;
                }
                var result = budget.Incomplete("displayOnly");
                result["engineeringType"] = value.GetType().FullName; result["identityComplete"] = false;
                if (value is Channel channel)
                {
                    result["number"] = channel.Number; result["type"] = channel.Type.ToString(); result["ioType"] = channel.IoType.ToString();
                    object parent = channel.Parent;
                    if (parent is DeviceItem item)
                    {
                        var owner = (JObject)InspectionValue(item, budget, depth + 1);
                        owner["typeIdentifier"] = InspectionValue(item.TypeIdentifier, budget, depth + 1);
                        object container = item.Container;
                        if (container is DeviceItem module)
                        {
                            var moduleRow = (JObject)InspectionValue(module, budget, depth + 1);
                            moduleRow["typeIdentifier"] = InspectionValue(module.TypeIdentifier, budget, depth + 1);
                            owner["container"] = moduleRow;
                        }
                        else owner["container"] = null;
                        result["parent"] = owner;
                    }
                    else result["parent"] = InspectionValue(parent, budget, depth + 1);
                }
                else if (value is DeviceItem device) { result["name"] = InspectionValue(device.Name, budget, depth + 1); result["positionNumber"] = device.PositionNumber; }
                else if (value is PlcTag tag) { result["name"] = InspectionValue(tag.Name, budget, depth + 1); result["logicalAddress"] = InspectionValue(tag.LogicalAddress, budget, depth + 1); }
                else if (value is PlcBlock block) { result["name"] = InspectionValue(block.Name, budget, depth + 1); result["number"] = block.Number; }
                else if (value is PlcUnitBase unit) result["name"] = InspectionValue(unit.Name, budget, depth + 1);
                else result["expanded"] = false;
                return result;
            }
            if (value is IEnumerable enumerable)
            {
                var result = new JArray();
                try
                {
                    int count = 0;
                    foreach (var item in enumerable)
                    {
                        if (count++ == 64 || budget.Remaining == 0) { result.Add(budget.Incomplete("itemLimit", true)); break; }
                        result.Add(InspectionValue(item, budget, depth + 1));
                        if (budget.Remaining == 0) { result.Add(budget.Incomplete("traversalLimit", true)); break; }
                    }
                }
                catch (Exception) { result.Add(budget.Incomplete("readError")); }
                return result;
            }
            var unknown = budget.Incomplete("unsupportedType"); unknown["type"] = value.GetType().FullName;
            return unknown;
        }
    }
}
