using System.Collections.Generic;
using System.Security;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // The session state of the Host credential route; the checks are in RoutedCredentials. The envelopes of one admitted
    // dispatch are opened at the native call and cleared when the dispatch returns.
    internal sealed partial class OpennessSession
    {
        private Dictionary<string, byte[]> _routedCredentials;

        // The public slots {slot, vaultName, reference, binding} the admitted fresh change apply needs, or null.
        private JArray SharedMutationCredentials()
        {
            if (_sharedMutationOperation != "change_apply" || !_changes.TryGetValue(_sharedMutationArguments.Value<string>("changeId") ?? "", out var change)) return null;
            return change.IdempotencyKey == null ? change.Preview?["credentials"] as JArray : null;
        }

        private void AcceptRoutedCredentials(JObject args) => _routedCredentials = RoutedCredentials.Accept(args["credentials"], SharedMutationCredentials());

        private void ClearRoutedCredentials()
        {
            RoutedCredentials.Clear(_routedCredentials);
            _routedCredentials = null;
        }

        // Opens before the checkpoint (then disposed) so a missing or mismatched credential refuses with nothing saved or archived.
        private void RequireRoutedCredentials(EngineeringChange change)
        {
            foreach (var row in change.Preview["credentials"] as JArray ?? new JArray()) OpenRoutedCredential(change, row.Value<string>("slot")).Dispose();
        }

        private SecureString OpenRoutedCredential(EngineeringChange change, string slot) =>
            RoutedCredentials.Open(change.Preview["credentials"] as JArray, _routedCredentials, _sharedMutationToken, change.Id, change.PreviewHash, slot);
    }
}
