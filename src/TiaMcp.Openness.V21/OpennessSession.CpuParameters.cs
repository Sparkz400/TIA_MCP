using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;

namespace TiaMcp.Openness.V21
{
    // Typed CPU parameter edits, modelled on hardware_cpu_memory_bytes: a reviewed attribute subset of one exact hardware
    // item, previewed with before/after values, applied under the checkpoint, read back, device-compiled and checked by
    // configured-hardware preservation.
    internal sealed partial class OpennessSession
    {
        private static HashSet<string> CpuParameterAttributeSet => new HashSet<string>(HardwareEngineeringRules.CpuParameterAttributes, StringComparer.Ordinal);
        // A property: partial-class static initializer order across files is not guaranteed.
        private static HashSet<string> HardwareCpuParameterPreservedAttributes => new HashSet<string>(HardwareWriteAttributes.Concat(HardwareEngineeringRules.CpuParameterAttributes), StringComparer.Ordinal);

        private DeviceItem PrepareCpuParameters(EngineeringChange change, Project project, JObject preview)
        {
            var item = ChangeHardware(project, change.Payload);
            var attributes = ChangeObject(change.Payload, "attributes");
            HardwareEngineeringRules.RequireCpuParameterRequest(attributes);
            var declared = new HashSet<string>(item.Item.GetAttributeInfos().Select(info => info.Name), StringComparer.Ordinal);
            var missing = attributes.Properties().Select(p => p.Name).Where(name => !declared.Contains(name)).ToArray();
            if (missing.Length != 0) throw new BridgeException("UnsupportedCapability", "The selected hardware item does not declare " + string.Join(", ", missing) + "; select the CPU item (or its interface) that declares the parameter.");
            var current = new JObject(attributes.Properties().Select(p => new JProperty(p.Name, ChangeScalar(ReadAttributeValue(item.Item, p.Name, CpuParameterAttributeSet, false, out _)))));
            var changes = PrepareAttributes(item.Item, attributes, CpuParameterAttributeSet, out var converted);
            // Preservation and the unchanged check compare native scalars, not the JSON input spelling.
            foreach (var name in converted.Keys) attributes[name] = ChangeScalar(converted[name]);
            HardwareEngineeringRules.RequireCpuParametersChanged(current, attributes);
            if (preview != null)
            {
                preview["attributeChanges"] = changes;
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["cpuParametersBefore"] = current, ["cpuParametersAfter"] = attributes.DeepClone() });
                ((JArray)preview["warnings"]).Add("Device compilation validates the parameter combination; the runtime effect is observed only after a download.");
            }
            return item.Item;
        }

        private void ApplyCpuParameters(EngineeringChange change, Project project) =>
            ApplyAttributes(PrepareCpuParameters(change, project, null), ChangeObject(change.Payload, "attributes"), CpuParameterAttributeSet);
    }
}
