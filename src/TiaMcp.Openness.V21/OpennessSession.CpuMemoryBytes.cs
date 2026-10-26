using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly HashSet<string> CpuMemoryByteAttributes = new HashSet<string>(HardwareEngineeringRules.CpuMemoryByteAttributes, StringComparer.Ordinal);
        // A property: partial-class static initializer order across files is not guaranteed.
        private static HashSet<string> HardwareCpuMemoryPreservedAttributes => new HashSet<string>(HardwareWriteAttributes.Concat(CpuMemoryByteAttributes), StringComparer.Ordinal);

        private DeviceItem PrepareCpuMemoryBytes(EngineeringChange change, Project project, JObject preview)
        {
            var item = ChangeHardware(project, change.Payload);
            var declared = new HashSet<string>(item.Item.GetAttributeInfos().Select(info => info.Name), StringComparer.Ordinal);
            if (!HardwareEngineeringRules.CpuMemoryByteFlags.All(declared.Contains))
                throw new BridgeException("UnsupportedCapability", "The selected hardware item does not declare the native SystemMemoryByte and ClockMemoryByte attributes; select a supported CPU device item.");
            var attributes = ChangeObject(change.Payload, "attributes");
            var current = new JObject(HardwareEngineeringRules.CpuMemoryByteAttributes.Where(declared.Contains)
                .Select(name => new JProperty(name, ChangeScalar(ReadAttributeValue(item.Item, name, CpuMemoryByteAttributes, false, out _)))));
            HardwareEngineeringRules.RequireCpuMemoryBytes(current, attributes);
            // A byte number of a still-disabled flag is declared only after the flag write; it is converted then.
            var changes = PrepareAttributes(item.Item, new JObject(attributes.Properties().Where(p => declared.Contains(p.Name))), CpuMemoryByteAttributes, out var converted);
            // Preservation compares native scalars, not the JSON input spelling.
            foreach (var name in converted.Keys) attributes[name] = ChangeScalar(converted[name]);
            if (preview != null)
            {
                foreach (var pending in attributes.Properties().Where(p => !declared.Contains(p.Name)))
                    changes.Add(new JObject { ["name"] = pending.Name, ["type"] = null, ["before"] = null, ["after"] = pending.Value.DeepClone() });
                preview["attributeChanges"] = changes;
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["cpuMemoryBytesBefore"] = current,
                    ["cpuMemoryBytesAfter"] = HardwareEngineeringRules.CpuMemoryBytesAfter(current, attributes) });
                ((JArray)preview["warnings"]).Add("TIA may add or update its own system/clock memory tags in the PLC tag tables; compilation and the recovery checkpoint remain mandatory.");
            }
            return item.Item;
        }

        private void ApplyCpuMemoryBytes(EngineeringChange change, Project project)
        {
            var target = PrepareCpuMemoryBytes(change, project, null);
            var attributes = ChangeObject(change.Payload, "attributes");
            // Flags first: enabling one makes Openness declare its byte number.
            ApplyAttributes(target, new JObject(attributes.Properties().Where(p => HardwareEngineeringRules.CpuMemoryByteFlags.Contains(p.Name))), CpuMemoryByteAttributes);
            ApplyAttributes(target, new JObject(attributes.Properties().Where(p => !HardwareEngineeringRules.CpuMemoryByteFlags.Contains(p.Name))), CpuMemoryByteAttributes);
        }
    }
}
