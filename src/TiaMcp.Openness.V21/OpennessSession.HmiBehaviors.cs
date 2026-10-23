using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HmiUnifiedBehaviors(HmiNativeEntry entry, HmiCapture capture)
        {
            var coverage = new JObject();
            foreach (string composition in new[] { "Dynamizations", "EventHandlers", "PropertyEventHandlers" })
            {
                if (capture.Budget.Truncated) break;
                var rows = new JArray(); int bytes = 0;
                string field = composition == "Dynamizations" ? "dynamizations" : composition == "EventHandlers" ? "eventHandlers" : "propertyEventHandlers";
                var property = entry.Native.GetType().GetProperty(composition, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    coverage[field] = new JObject { ["status"] = "notExposed", ["reason"] = "This exact native owner type does not expose the public composition." };
                    continue;
                }
                try
                {
                    var objects = property.GetValue(entry.Native, null) as IEnumerable;
                    if (objects == null) { coverage[field] = new JObject { ["status"] = "notExposed", ["reason"] = "The public property has no enumerable native composition." }; continue; }
                    foreach (IEngineeringObject native in objects)
                    {
                        if (!capture.Budget.Take()) break;
                        var properties = HmiProperties(native, capture);
                        var row = new JObject { ["nativeType"] = native.GetType().FullName, ["properties"] = properties,
                            ["ownerPath"] = entry.Identity["objectPath"], ["composition"] = composition,
                            ["scriptsExecuted"] = false, ["resolution"] = "dynamic" };
                        if (native is Siemens.Engineering.HmiUnified.UI.Dynamization.DynamizationBase dynamic)
                        {
                            row["property"] = dynamic.PropertyName;
                            JObject converter = null;
                            if (dynamic is Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization binding && binding.ValueConverter != null)
                            {
                                if (!capture.Budget.Take()) break;
                                converter = HmiProperties(binding.ValueConverter, capture);
                                properties["ValueConverter"] = converter;
                            }
                            if (dynamic is Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization && HmiInspectionRules.DirectTagBinding(properties, converter)) row["resolution"] = "explicitTagReference";
                        }
                        foreach (string partName in new[] { "Script", "Trigger" })
                        {
                            var part = native.GetType().GetProperty(partName, BindingFlags.Public | BindingFlags.Instance);
                            if (part == null || !part.CanRead || part.GetIndexParameters().Length != 0) continue;
                            if (!capture.Budget.Take()) break;
                            var value = part.GetValue(native, null);
                            if (value is IEngineeringObject engineering) row[partName.ToLowerInvariant()] = HmiProperties(engineering, capture);
                            else if (value != null) row[partName.ToLowerInvariant()] = HmiScalar(value);
                            if (value is Siemens.Engineering.HmiUnified.UI.Dynamization.Script.IHmiScript script) row["script"] = HmiScriptSnapshot(script);
                        }
                        if (native is Siemens.Engineering.HmiUnified.UI.Dynamization.Script.IHmiScript ownScript) row["script"] = HmiScriptSnapshot(ownScript);
                        int length = Encoding.UTF8.GetByteCount(row.ToString(Formatting.None));
                        if (bytes + length > 262144)
                        {
                            capture.Budget.Truncated = true;
                            capture.Diagnostics.Add(new JObject { ["code"] = "HmiBehaviorLimit", ["ownerPath"] = entry.Identity["objectPath"], ["composition"] = composition, ["reason"] = "Behavior metadata exceeds the 256 KiB per-composition bound." });
                            break;
                        }
                        bytes += length; rows.Add(row);
                    }
                    HmiAttachMetadata(entry, field, rows, capture);
                    coverage[field] = new JObject { ["status"] = capture.Budget.Truncated ? "truncated" : "observed", ["count"] = rows.Count,
                        ["scope"] = "Public scalar properties, native tag/value-converter metadata, script text/hash and trigger scalar properties. Script semantics and navigation targets remain unresolved." };
                }
                catch (Exception error)
                {
                    if (rows.Count > 0) HmiAttachMetadata(entry, field, rows, capture);
                    capture.Status = "unavailable"; capture.Diagnostics.Add(HmiDiagnostic(composition, error));
                    coverage[field] = new JObject { ["status"] = "unavailable", ["reason"] = error.GetType().Name };
                }
            }
            HmiAttachMetadata(entry, "behaviorCoverage", coverage, capture);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject HmiScriptSnapshot(Siemens.Engineering.HmiUnified.UI.Dynamization.Script.IHmiScript script)
        {
            string code = script.ScriptCode, global = script.GlobalDefinitionAreaScriptCode;
            return new JObject { ["scriptCode"] = HmiScalar(code), ["scriptCodeSha256"] = ChangeHash(code ?? ""),
                ["globalDefinitionAreaScriptCode"] = HmiScalar(global), ["globalDefinitionAreaScriptCodeSha256"] = ChangeHash(global ?? ""),
                ["async"] = script.Async, ["scriptsExecuted"] = false, ["semanticResolution"] = "unresolved",
                ["reason"] = "Native script source is retained as text; static navigation and tag-expression parsing are not qualified, and no script is executed." };
        }

        private static bool HmiAttachMetadata(HmiNativeEntry entry, string field, JToken value, HmiCapture capture)
        {
            int extra = Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
            if (capture.RetainedBytes + extra > 8 * 1024 * 1024)
            {
                capture.Budget.Truncated = true;
                capture.Diagnostics.Add(new JObject { ["code"] = "HmiMetadataLimit", ["field"] = field, ["reason"] = "Native metadata reached its shared 8 MiB bound." });
                return false;
            }
            capture.RetainedBytes += extra; entry.Row[field] = value; return true;
        }
    }
}
