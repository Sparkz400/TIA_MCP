using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaMcp.Openness.V21
{
    // Typed CPU parameter edits, modelled on hardware_cpu_memory_bytes: a reviewed attribute subset of one exact hardware
    // item, previewed with before/after values, applied under the checkpoint, read back, device-compiled and checked by
    // configured-hardware preservation. The CPU protection level takes its full-access password through the Host credential route.
    internal sealed partial class OpennessSession
    {
        private static HashSet<string> CpuParameterAttributeSet => new HashSet<string>(HardwareEngineeringRules.CpuParameterAttributes, StringComparer.Ordinal);
        // A property: partial-class static initializer order across files is not guaranteed.
        private static HashSet<string> HardwareCpuParameterPreservedAttributes => new HashSet<string>(HardwareWriteAttributes.Concat(HardwareEngineeringRules.CpuParameterAttributes), StringComparer.Ordinal);
        private const string AccessControl = "PlcAccessControlConfiguration";

        private DeviceItem PrepareCpuParameters(EngineeringChange change, Project project, JObject preview)
        {
            var item = ChangeHardware(project, change.Payload);
            var attributes = ChangeObject(change.Payload, "attributes");
            // A protection level needs no other parameter.
            if (change.Payload["protectionLevel"] == null || attributes.Count != 0) HardwareEngineeringRules.RequireCpuParameterRequest(attributes);
            var declared = new HashSet<string>(item.Item.GetAttributeInfos().Select(info => info.Name), StringComparer.Ordinal);
            string mode = declared.Contains(AccessControl) ? ReadAttributeValue(item.Item, AccessControl, CpuParameterAttributeSet, false, out _)?.ToString() : null;
            HardwareEngineeringRules.RequireCpuProtectionRequest(change.Payload, mode);
            string level = change.Payload.Value<string>("protectionLevel");
            PlcAccessLevelProvider access = null; JObject binding = null;
            if (level != null)
            {
                RoutedCredentials.RequireVaultName(change.Payload);
                RoutedCredentials.RequireReference(change.Payload, "credential");
                var plc = item.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware
                    ?? throw new BridgeException("UnsupportedCapability", "The protection level is set on the CPU item that holds the PLC software; select that item.");
                // V21 exposes the provider only under an access-level mode (30 October 2026); a change entering one finds it at apply.
                access = item.Item.GetService<PlcAccessLevelProvider>();
                if (access == null && HardwareEngineeringRules.IsAccessLevelMode(mode))
                    throw new BridgeException("UnsupportedCapability", "The selected CPU exposes no PlcAccessLevelProvider service.");
                // The full-access password is bound to this exact CPU: its PLC path, its hardware item and the purpose.
                binding = RoutedCredentials.Binding(item.Path + "/plc/" + Segment(plc.Name), item.Path, "cpuProtection", "fullAccess", "fullAccessPassword");
            }
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
            if (attributes.Count != 0) HardwareEngineeringRules.RequireCpuParametersChanged(current, attributes);
            if (preview != null)
            {
                if (pending) changes.Add(new JObject { ["name"] = HardwareEngineeringRules.MinimumCycleTime, ["type"] = null, ["before"] = null, ["after"] = attributes[HardwareEngineeringRules.MinimumCycleTime].DeepClone() });
                preview["attributeChanges"] = changes;
                var effect = new JObject { ["hardwarePath"] = item.Path, ["cpuParametersBefore"] = current, ["cpuParametersAfter"] = HardwareEngineeringRules.CpuParametersAfter(current, attributes) };
                ((JArray)preview["effects"]).Add(effect);
                ((JArray)preview["warnings"]).Add("Device compilation validates the parameter combination; the runtime effect is observed only after a download.");
                if (level != null)
                {
                    effect["protectionLevelBefore"] = access == null ? JValue.CreateNull() : SecurityValue(() => access.PlcProtectionAccessLevel); effect["protectionLevelAfter"] = level;
                    preview["credentials"] = RoutedCredentials.Slots(change.Payload, binding, "credential");
                    ((JArray)preview["warnings"]).Add("The full-access password is set from the credential reference; only the reference and its public binding are recorded. At apply the Host checks it against its credential store and seals the secret to that one admitted apply; no secret is previewed, journaled, checkpointed or returned. V21 cannot read a password back: device compilation is the evidence that one is set.");
                }
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
            string level = change.Payload.Value<string>("protectionLevel");
            if (level == null) return;
            // After the access-control mode, under which the level applies.
            var access = target.GetService<PlcAccessLevelProvider>()
                ?? throw new BridgeException("UnsupportedCapability", "The CPU exposes no PlcAccessLevelProvider service after its access-level mode was written.");
            access.PlcProtectionAccessLevel = (PlcProtectionAccessLevel)Enum.Parse(typeof(PlcProtectionAccessLevel), level);
            using (var password = OpenRoutedCredential(change, "credential"))
            {
                // As for block protection, TIA checks its password policy only in the native call, after the checkpoint.
                try { access.SetPassword(PlcProtectionAccessLevel.FullAccess, password); }
                catch (EngineeringPasswordPolicyViolationException error) { throw new BridgeException("CredentialPolicyViolation", "TIA refused the password under its password policy: " + error.Message); }
            }
            string readback = access.PlcProtectionAccessLevel.ToString();
            change.Readbacks.Add(new JObject { ["role"] = "cpu-protection-readback", ["hardwarePath"] = change.Payload.Value<string>("hardwarePath"), ["protectionLevel"] = readback });
            if (readback != level) throw new BridgeException("MutationVerificationFailed", "The CPU protection level reads back " + readback + ", not the prepared " + level + ".");
        }
    }
}
