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
            // The minimum cycle time is declared only after the same request has enabled it; it is converted then.
            bool pending = attributes[HardwareEngineeringRules.MinimumCycleTime] != null && !declared.Contains(HardwareEngineeringRules.MinimumCycleTime)
                && declared.Contains(HardwareEngineeringRules.MinimumCycleFlag) && attributes.Value<bool?>(HardwareEngineeringRules.MinimumCycleFlag) == true;
            var missing = attributes.Properties().Select(p => p.Name).Where(name => !declared.Contains(name) && !(name == HardwareEngineeringRules.MinimumCycleTime && declared.Contains(HardwareEngineeringRules.MinimumCycleFlag))).ToArray();
            if (missing.Length != 0) throw new BridgeException("UnsupportedCapability", "The selected hardware item does not declare " + string.Join(", ", missing) + "; select the CPU item (or its interface) that declares the parameter.");
            var observed = attributes.Properties().Select(p => p.Name).Append(HardwareEngineeringRules.MinimumCycleFlag).Where(declared.Contains).Distinct(StringComparer.Ordinal);
            var current = new JObject(observed.Select(name => new JProperty(name, ChangeScalar(ReadAttributeValue(item.Item, name, CpuParameterAttributeSet, false, out _)))));
            var changes = PrepareAttributes(item.Item, new JObject(attributes.Properties().Where(p => declared.Contains(p.Name))), CpuParameterAttributeSet, out var converted);
            // Preservation and the unchanged check compare native scalars, not the JSON input spelling.
            foreach (var name in converted.Keys) attributes[name] = ChangeScalar(converted[name]);
            HardwareEngineeringRules.RequireCpuParametersChanged(current, attributes);
            if (preview != null)
            {
                if (pending) changes.Add(new JObject { ["name"] = HardwareEngineeringRules.MinimumCycleTime, ["type"] = null, ["before"] = null, ["after"] = attributes[HardwareEngineeringRules.MinimumCycleTime].DeepClone() });
                preview["attributeChanges"] = changes;
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["cpuParametersBefore"] = current,
                    ["cpuParametersAfter"] = HardwareEngineeringRules.CpuParametersAfter(current, attributes) });
                ((JArray)preview["warnings"]).Add("Device compilation validates the parameter combination; the runtime effect is observed only after a download.");
            }
            return item.Item;
        }

        private void ApplyCpuParameters(EngineeringChange change, Project project)
        {
            var target = PrepareCpuParameters(change, project, null);
            var attributes = ChangeObject(change.Payload, "attributes");
            // The flag first: enabling it makes Openness declare the minimum cycle time.
            ApplyAttributes(target, new JObject(attributes.Properties().Where(p => p.Name == HardwareEngineeringRules.MinimumCycleFlag)), CpuParameterAttributeSet);
            ApplyAttributes(target, new JObject(attributes.Properties().Where(p => p.Name != HardwareEngineeringRules.MinimumCycleFlag)), CpuParameterAttributeSet);
        }
    }
}
