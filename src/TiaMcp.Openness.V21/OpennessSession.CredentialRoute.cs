using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Newtonsoft.Json.Linq;
using TiaMcp.Security;

namespace TiaMcp.Openness.V21
{
    // The worker half of the Host credential route (docs/reference/protected-credentials.md#change-apply-credential-route):
    // a prepared change names public credential slots, the Host seals each secret to the admitted lease, and only that
    // lease's dispatch carries the envelopes. They are opened at the native call and cleared when the dispatch returns.
    internal sealed partial class OpennessSession
    {
        private Dictionary<string, byte[]> _routedCredentials;

        // The public slots {slot, vaultName, reference, binding} the admitted fresh change apply needs, or null.
        private JArray SharedMutationCredentials()
        {
            if (_sharedMutationOperation != "change_apply" || !_changes.TryGetValue(_sharedMutationArguments.Value<string>("changeId") ?? "", out var change)) return null;
            return change.IdempotencyKey == null ? change.Preview?["credentials"] as JArray : null;
        }

        private void AcceptRoutedCredentials(JObject args)
        {
            var routed = args["credentials"];
            if (routed == null) return;
            var required = SharedMutationCredentials();
            var rows = routed as JArray;
            if (required == null || rows == null || rows.Count != required.Count || rows.Any(row => !(row is JObject item) || item.Count != 2 ||
                    item["slot"]?.Type != JTokenType.String || item["envelope"]?.Type != JTokenType.String) ||
                !new HashSet<string>(rows.Select(row => row.Value<string>("slot")), StringComparer.Ordinal).SetEquals(required.Select(row => row.Value<string>("slot"))))
                throw new BridgeException("CredentialRouteInvalid", "Routed credentials must match exactly the slots this prepared change requires.");
            try { _routedCredentials = rows.ToDictionary(row => row.Value<string>("slot"), row => Convert.FromBase64String(row.Value<string>("envelope")), StringComparer.Ordinal); }
            catch (FormatException) { throw new BridgeException("CredentialRouteInvalid", "A routed credential envelope is malformed."); }
        }

        private void ClearRoutedCredentials()
        {
            foreach (var envelope in _routedCredentials?.Values ?? Enumerable.Empty<byte[]>()) Array.Clear(envelope, 0, envelope.Length);
            _routedCredentials = null;
        }

        // Opens before the checkpoint (then disposed) so a missing or mismatched credential refuses with nothing saved or archived.
        private void RequireRoutedCredentials(EngineeringChange change)
        {
            foreach (var row in change.Preview["credentials"] as JArray ?? new JArray()) OpenRoutedCredential(change, row.Value<string>("slot")).Dispose();
        }

        private SecureString OpenRoutedCredential(EngineeringChange change, string slot)
        {
            var row = (change.Preview["credentials"] as JArray)?.FirstOrDefault(item => item.Value<string>("slot") == slot)
                ?? throw new BridgeException("CredentialRouteInvalid", "The prepared change has no credential slot " + slot + ".");
            if (_sharedMutationToken == null || _routedCredentials == null || !_routedCredentials.TryGetValue(slot, out var envelope))
                throw new BridgeException("CredentialRequired", "No protected credential was routed for " + slot + " to this apply; only the Host's admitted dispatch carries one. Nothing was saved, archived or changed.");
            var reference = row["reference"];
            var entropy = CredentialRouteEnvelope.Entropy(_sharedMutationToken, change.Id, change.PreviewHash, slot, reference.Value<string>("credentialId"), reference.Value<string>("bindingSha256"));
            return CredentialRouteEnvelope.Open(envelope, entropy)
                ?? throw new BridgeException("CredentialContextMismatch", "The routed credential for " + slot + " does not open for this exact apply, change and reference.");
        }
    }
}
