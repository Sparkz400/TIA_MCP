using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly string[] BlockProtectionKinds = { "block_protect", "block_unprotect", "block_write_protection_set" };

        private void PrepareBlockProtection(EngineeringChange change, JObject preview)
        {
            var payload = change.Payload;
            bool write = change.Kind == "block_write_protection_set";
            string action = write ? ChangeText(payload, "action", 16) : null;
            if (write && !new[] { "define", "change", "enable", "disable" }.Contains(action, StringComparer.Ordinal))
                throw new BridgeException("InvalidRequest", "action must be define, change, enable or disable.");
            // The allowlist keeps any stray plaintext field out of the preview, journal and response.
            var allowed = new[] { "blockName", "blockPath", "scopePath", "namespace", "groupPath", "vaultName", "credential" }
                .Concat(write ? new[] { "action" } : new string[0]).Concat(action == "change" ? new[] { "newCredential" } : new string[0]);
            if (payload.Properties().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
                throw new BridgeException("CredentialInputInvalid", "Block protection payloads accept only block selectors, vaultName, action and protected credential references; plaintext passwords are never accepted.");
            RoutedCredentials.RequireVaultName(payload);
            RoutedCredentials.RequireReference(payload, "credential");
            if (action == "change")
            {
                RoutedCredentials.RequireReference(payload, "newCredential");
                if (JToken.DeepEquals(payload["credential"], payload["newCredential"]) || payload["credential"].Value<string>("vaultId") != payload["newCredential"].Value<string>("vaultId"))
                    throw new BridgeException("CredentialInputInvalid", "credential and newCredential must be different references in the same vault.");
            }
            var plc = SelectPlc(change.Selector);
            change.BlockNames = new[] { MutationBlockName(payload, plc) };
            PrepareMutationScope(change, plc, "block", change.BlockNames);
            var entry = BlockProtectionTarget(change, plc);
            var effects = (JArray)preview["effects"];
            var warnings = (JArray)preview["warnings"];
            if (change.Kind == "block_unprotect")
            {
                effects.Add(new JObject { ["action"] = change.Kind, ["existingBlock"] = BlockInfo(entry, plc.Path) });
                warnings.Add("Know-how protected content has no before-XML backup; the whole-project checkpoint archive is the recovery source.");
            }
            else PrepareBlockTargets(change, plc, false, true, effects);
            if (!write) BlockProtectionProvider(entry.Item);
            var state = BlockProtectionState(entry.Item, write);
            var after = (JObject)state.DeepClone();
            if (!write) after["isKnowHowProtected"] = change.Kind == "block_protect";
            else if (action == "define" && state.Value<bool>("writeProtectionDefined")) throw new BridgeException("InvalidChangeState", "Write protection is already defined; use action change.");
            else if (action != "define" && !state.Value<bool>("writeProtectionDefined")) throw new BridgeException("InvalidChangeState", "Write protection is not defined; use action define first.");
            else if (action == "define") { after["writeProtectionDefined"] = true; after["writeProtectionEnabled"] = null; } // whether Define also enables is not documented; not predicted
            else if (action == "enable" || action == "disable") after["writeProtectionEnabled"] = action == "enable";
            if (action == "enable" && state.Value<bool>("writeProtectionEnabled") || action == "disable" && !state.Value<bool>("writeProtectionEnabled"))
                throw new BridgeException("InvalidChangeState", "Write protection is already " + (action == "enable" ? "enabled." : "disabled."));
            preview["protectionBefore"] = state; preview["protectionAfter"] = after;
            // Each reference must be bound to exactly this public binding; the Host checks that against its vault before admission.
            preview["credentials"] = RoutedCredentials.Slots(payload, RoutedCredentials.Binding(plc.Path, entry.Path, "blockProtection", write ? "write" : "knowHow", "blockPassword"), "credential", "newCredential");
            warnings.Add("Only credential references and their public binding are recorded. At apply the Host checks each reference against its credential store and seals the secret to that one admitted apply; no secret is previewed, journaled, checkpointed or returned.");
        }

        private void ApplyBlockProtection(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var block = BlockProtectionTarget(change, plc).Item;
            string action = change.Payload.Value<string>("action");
            using (var password = OpenRoutedCredential(change, "credential"))
            {
                // TIA checks its password policy only in the native call, after the checkpoint (native run 30 October 2026).
                try
                {
                    if (change.Kind == "block_protect") BlockProtectionProvider(block).Protect(password);
                    else if (change.Kind == "block_unprotect") BlockProtectionProvider(block).Unprotect(password);
                    else if (action == "define") WriteProtectionProvider(block).Define(password);
                    else if (action == "enable") WriteProtectionProvider(block).Protect(password);
                    else if (action == "disable") WriteProtectionProvider(block).Unprotect(password);
                    else using (var next = OpenRoutedCredential(change, "newCredential")) WriteProtectionProvider(block).Change(password, next);
                }
                catch (EngineeringPasswordPolicyViolationException error)
                {
                    throw new BridgeException("CredentialPolicyViolation", "TIA refused the password under its password policy: " + error.Message);
                }
            }
            var readback = BlockProtectionState(FindBlockByName(plc, change.BlockNames[0], true, change).Item, action != null);
            change.Readbacks.Add(new JObject { ["role"] = "protection-readback", ["state"] = readback });
            // A password change keeps the same observable state; the native call returning is the only evidence of it.
            if (((JObject)change.Preview["protectionAfter"]).Properties().Any(expected => expected.Value.Type != JTokenType.Null && !JToken.DeepEquals(expected.Value, readback[expected.Name])))
                throw new BridgeException("MutationVerificationFailed", "Block protection readback does not match the prepared state.");
        }

        private BlockEntry BlockProtectionTarget(EngineeringChange change, PlcSelection plc)
        {
            var entry = FindBlockByName(plc, change.BlockNames[0], true, change);
            if (entry.NativeSystemGroup) throw new BridgeException("GeneratedObject", "Generated system-group blocks are excluded from protection changes.");
            if (change.Kind != "block_unprotect") EnsureEditableBlock(entry.Item, false);
            else if (entry.Item.ProgrammingLanguage.ToString().StartsWith("F_", StringComparison.Ordinal)) throw new BridgeException("SafetyBoundary", "Safety blocks are excluded from protection changes.");
            else if (!entry.Item.IsKnowHowProtected) throw new BridgeException("InvalidChangeState", "The selected block is not know-how protected.");
            return entry;
        }

        private static JObject BlockProtectionState(PlcBlock block, bool write)
        {
            var state = new JObject { ["isKnowHowProtected"] = block.IsKnowHowProtected };
            if (!write) return state;
            var provider = WriteProtectionProvider(block);
            state["writeProtectionDefined"] = provider.IsDefined; state["writeProtectionEnabled"] = provider.IsProtected;
            return state;
        }

        private static PlcBlockProtectionProvider BlockProtectionProvider(PlcBlock block) => block.GetService<PlcBlockProtectionProvider>()
            ?? throw new BridgeException("UnsupportedCapability", "The selected block exposes no PlcBlockProtectionProvider service.");

        private static PlcBlockWriteProtectionProvider WriteProtectionProvider(PlcBlock block) => block.GetService<PlcBlockWriteProtectionProvider>()
            ?? throw new BridgeException("UnsupportedCapability", "The selected block exposes no PlcBlockWriteProtectionProvider service.");
    }
}
