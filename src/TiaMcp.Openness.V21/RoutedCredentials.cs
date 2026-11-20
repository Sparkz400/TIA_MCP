#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Newtonsoft.Json.Linq;
using TiaMcp.Security;

namespace TiaMcp.Openness.V21
{
    // The worker half of the Host credential route (docs/reference/protected-credentials.md#change-apply-credential-route),
    // free of Siemens types so it is tested offline: a prepared change names public slots, the Host seals each secret to the
    // admitted lease, and only that lease's dispatch carries the envelopes.
    internal static class RoutedCredentials
    {
        private static readonly string[] ReferenceFields = { "bindingSha256", "cipherSha256", "credentialId", "schemaVersion", "vaultId" };

        // The public binding the worker derives from the exact target; the Host checks each reference against it.
        internal static JObject Binding(string plcPath, string ownerId, string purpose, string resourceId, string subjectId) => new JObject {
            ["resource"] = new JObject { ["provider"] = "siemensTiaPortalV21", ["authority"] = plcPath, ["ownerId"] = ownerId, ["resourceKind"] = purpose, ["resourceId"] = resourceId },
            ["purpose"] = purpose, ["subjectId"] = subjectId };

        // The preview's public slots {slot, vaultName, reference, binding}, one per reference the payload names.
        internal static JArray Slots(JObject payload, JObject binding, params string[] slots) => new JArray(slots.Where(slot => payload[slot] != null).Select(slot => new JObject {
            ["slot"] = slot, ["vaultName"] = payload.Value<string>("vaultName") ?? "default", ["reference"] = payload[slot].DeepClone(), ["binding"] = binding.DeepClone() }));

        internal static void RequireVaultName(JObject payload)
        {
            if (payload["vaultName"] != null && (payload["vaultName"].Type != JTokenType.String || payload.Value<string>("vaultName") != "default" && !LowerHex(payload.Value<string>("vaultName"), 32)))
                throw new BridgeException("CredentialInputInvalid", "vaultName must be default or a 32-character lowercase hexadecimal vault name.");
        }

        internal static void RequireReference(JObject payload, string key)
        {
            var reference = payload[key] as JObject;
            if (reference == null || !reference.Properties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(ReferenceFields) ||
                reference.Properties().Any(property => property.Value.Type != JTokenType.String) || reference.Value<string>("schemaVersion") != "protected-credential-reference.v1" ||
                !LowerHex(reference.Value<string>("vaultId"), 32) || !LowerHex(reference.Value<string>("credentialId"), 32) ||
                !LowerHex(reference.Value<string>("bindingSha256"), 64) || !LowerHex(reference.Value<string>("cipherSha256"), 64))
                throw new BridgeException("CredentialInputInvalid", key + " must be an exact protected-credential-reference.v1 object from the Host credential store; plaintext passwords are never accepted.");
        }

        // The dispatch's envelopes by slot, or null when it carries none; they must match exactly the slots the admitted change requires.
        internal static Dictionary<string, byte[]> Accept(JToken routed, JArray required)
        {
            if (routed == null) return null;
            var rows = routed as JArray;
            if (required == null || rows == null || rows.Count != required.Count || rows.Any(row => !(row is JObject item) || item.Count != 2 ||
                    item["slot"]?.Type != JTokenType.String || item["envelope"]?.Type != JTokenType.String) ||
                !new HashSet<string>(rows.Select(row => row.Value<string>("slot")), StringComparer.Ordinal).SetEquals(required.Select(row => row.Value<string>("slot"))))
                throw new BridgeException("CredentialRouteInvalid", "Routed credentials must match exactly the slots this prepared change requires.");
            try { return rows.ToDictionary(row => row.Value<string>("slot"), row => Convert.FromBase64String(row.Value<string>("envelope")), StringComparer.Ordinal); }
            catch (FormatException) { throw new BridgeException("CredentialRouteInvalid", "A routed credential envelope is malformed."); }
        }

        internal static void Clear(Dictionary<string, byte[]> envelopes)
        {
            foreach (var envelope in envelopes?.Values ?? Enumerable.Empty<byte[]>()) Array.Clear(envelope, 0, envelope.Length);
        }

        // slots is the prepared change's preview credentials; a missing envelope or lease refuses before anything is changed.
        internal static SecureString Open(JArray slots, Dictionary<string, byte[]> envelopes, string leaseToken, string changeId, string previewHash, string slot)
        {
            var row = slots?.FirstOrDefault(item => item.Value<string>("slot") == slot)
                ?? throw new BridgeException("CredentialRouteInvalid", "The prepared change has no credential slot " + slot + ".");
            if (leaseToken == null || envelopes == null || !envelopes.TryGetValue(slot, out var envelope))
                throw new BridgeException("CredentialRequired", "No protected credential was routed for " + slot + " to this apply; only the Host's admitted dispatch carries one. Nothing was saved, archived or changed.");
            var reference = row["reference"];
            var entropy = CredentialRouteEnvelope.Entropy(leaseToken, changeId, previewHash, slot, reference.Value<string>("credentialId"), reference.Value<string>("bindingSha256"));
            return CredentialRouteEnvelope.Open(envelope, entropy)
                ?? throw new BridgeException("CredentialContextMismatch", "The routed credential for " + slot + " does not open for this exact apply, change and reference.");
        }

        // project_open of a UMAC-protected project (docs/reference/protected-credentials.md#project-open-umac-route). The binding
        // names the exact project file and UMAC user, so a credential serves one user of one project path only.
        internal const string UmacPurpose = "projectUmac";

        internal static string UmacSubject(string userType) => userType == "global" ? "globalUser" : userType == "project" ? "projectUser"
            : throw new BridgeException("CredentialInputInvalid", "umac.userType must be project or global.");

        internal static JObject UmacBinding(string projectPath, string userName, string userType) => new JObject {
            ["resource"] = new JObject { ["provider"] = "siemensTiaPortalV21", ["authority"] = projectPath, ["ownerId"] = projectPath, ["resourceKind"] = UmacPurpose, ["resourceId"] = userName },
            ["purpose"] = UmacPurpose, ["subjectId"] = UmacSubject(userType) };

        internal sealed class UmacLogin
        {
            internal string UserName, UserType, CredentialId, BindingSha256, Nonce;
            internal byte[] Envelope;
        }

        /// <summary>The sealed login the Host put in project_open's umac argument; null when there is none.</summary>
        internal static UmacLogin ParseUmac(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var umac = token as JObject;
            string[] fields = { "bindingSha256", "credentialId", "envelope", "nonce", "userName", "userType" };
            if (umac == null || !umac.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(fields) || umac.Properties().Any(p => p.Value.Type != JTokenType.String))
                throw new BridgeException("CredentialRouteInvalid", "umac must be the Host's sealed login; pass umacCredential to project_open, never a password.");
            var login = new UmacLogin { UserName = umac.Value<string>("userName"), UserType = umac.Value<string>("userType"), CredentialId = umac.Value<string>("credentialId"),
                BindingSha256 = umac.Value<string>("bindingSha256"), Nonce = umac.Value<string>("nonce") };
            UmacSubject(login.UserType);
            if (string.IsNullOrWhiteSpace(login.UserName) || login.UserName.Length > 256 || login.UserName.Any(char.IsControl) || !LowerHex(login.CredentialId, 32) ||
                !LowerHex(login.BindingSha256, 64) || !LowerHex(login.Nonce, 32))
                throw new BridgeException("CredentialRouteInvalid", "The sealed UMAC login is malformed.");
            try { login.Envelope = Convert.FromBase64String(umac.Value<string>("envelope")); }
            catch (FormatException) { throw new BridgeException("CredentialRouteInvalid", "The sealed UMAC login envelope is malformed."); }
            return login;
        }

        /// <summary>Opens the login's password for this exact project path; the envelope is cleared either way.</summary>
        internal static SecureString OpenUmac(UmacLogin login, string projectPath)
        {
            try
            {
                return CredentialRouteEnvelope.Open(login.Envelope, CredentialRouteEnvelope.ProjectOpenEntropy(login.Nonce, projectPath, login.UserName, login.UserType, login.CredentialId, login.BindingSha256))
                    ?? throw new BridgeException("CredentialContextMismatch", "The sealed UMAC login does not open for this exact project path (" + projectPath + ") and user; nothing was opened.");
            }
            finally { Array.Clear(login.Envelope, 0, login.Envelope.Length); }
        }

        internal static bool LowerHex(string value, int length) => value != null && value.Length == length && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    }
}
