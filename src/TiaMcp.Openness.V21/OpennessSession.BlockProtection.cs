using System;
using System.Linq;
using System.Security;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly string[] BlockProtectionKinds = { "block_protect", "block_unprotect", "block_write_protection_set" };
        private static readonly string[] CredentialReferenceFields = { "bindingSha256", "cipherSha256", "credentialId", "schemaVersion", "vaultId" };

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
            if (payload["vaultName"] != null && (payload["vaultName"].Type != JTokenType.String || payload.Value<string>("vaultName") != "default" && !LowerHex(payload.Value<string>("vaultName"), 32)))
                throw new BridgeException("CredentialInputInvalid", "vaultName must be default or a 32-character lowercase hexadecimal vault name.");
            RequireCredentialReference(payload, "credential");
            if (action == "change")
            {
                RequireCredentialReference(payload, "newCredential");
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
            warnings.Add("Only credential references are recorded. Apply refuses with CredentialRouteUnavailable until the Host can hand a protected credential to the worker; no secret is read, previewed, journaled or returned.");
        }

        private void ApplyBlockProtection(EngineeringChange change)
        {
            var plc = SelectPlc(change.Selector);
            var block = BlockProtectionTarget(change, plc).Item;
            string action = change.Payload.Value<string>("action");
            using (var password = OpenBlockProtectionCredential(change.Payload, "credential"))
            {
                if (change.Kind == "block_protect") BlockProtectionProvider(block).Protect(password);
                else if (change.Kind == "block_unprotect") BlockProtectionProvider(block).Unprotect(password);
                else if (action == "define") WriteProtectionProvider(block).Define(password);
                else if (action == "enable") WriteProtectionProvider(block).Protect(password);
                else if (action == "disable") WriteProtectionProvider(block).Unprotect(password);
                else using (var next = OpenBlockProtectionCredential(change.Payload, "newCredential")) WriteProtectionProvider(block).Change(password, next);
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

        private static void RequireCredentialReference(JObject payload, string key)
        {
            var reference = payload[key] as JObject;
            if (reference == null || !reference.Properties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(CredentialReferenceFields) ||
                reference.Properties().Any(property => property.Value.Type != JTokenType.String) || reference.Value<string>("schemaVersion") != "protected-credential-reference.v1" ||
                !LowerHex(reference.Value<string>("vaultId"), 32) || !LowerHex(reference.Value<string>("credentialId"), 32) ||
                !LowerHex(reference.Value<string>("bindingSha256"), 64) || !LowerHex(reference.Value<string>("cipherSha256"), 64))
                throw new BridgeException("CredentialInputInvalid", key + " must be an exact protected-credential-reference.v1 object from the Host credential store; plaintext passwords are never accepted.");
        }

        private static bool LowerHex(string value, int length) => value != null && value.Length == length && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        // shortcut: no route hands a Host vault secret to a worker change apply (docs/reference/protected-credentials.md);
        // resolve the reference here as a read-only SecureString once the Host provides one.
        private static SecureString OpenBlockProtectionCredential(JObject payload, string key) =>
            throw new BridgeException("CredentialRouteUnavailable", "No protected credential route from the Host credential store to a change apply exists yet. Nothing was saved, archived or changed; only the credential reference is recorded.");
    }
}
