#nullable disable
using System;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal static class EngineeringFingerprintRules
    {
        // Native system-group provenance comes from typed traversal, not a block
        // name or a caller-supplied path. Protected derived blocks have no editable
        // source here; their generated payload checksums may be unavailable after
        // a successful safety compile. Never apply this exclusion to user blocks.
        internal static Content<T> ReadBlockContent<T>(bool nativeSystemGroup, bool knowHowProtected, bool consistent, bool hasSafetySignatureCoverage, string path, Func<T> readNativeFingerprints) where T : class
        {
            if (nativeSystemGroup && knowHowProtected && hasSafetySignatureCoverage)
            {
                string scope = ObjectIdentity.ScopePath(ObjectIdentity.PlcPath(path), path);
                if (scope == null || !path.StartsWith(scope + "/blocks/systemGroups/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Native generated-block provenance does not match its canonical software path.");
                return new Content<T> { Mode = "protectedGeneratedMetadata", Payload = null };
            }
            // Native errors for editable/user blocks intentionally propagate. An
            // inconsistent source block must not acquire a weaker recovery guard.
            if (!consistent) throw new BridgeException("FingerprintUnavailable", "An editable/user block is inconsistent; a reliable native payload fingerprint cannot be established: " + path);
            T payload = readNativeFingerprints();
            return new Content<T> { Mode = payload == null ? "metadataNoNativeProvider" : "nativeFingerprints", Payload = payload };
        }

        internal sealed class Content<T> where T : class
        {
            internal string Mode;
            internal T Payload;
        }
    }
}
