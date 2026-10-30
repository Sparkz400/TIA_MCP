using System;
using System.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>
    /// User-authorized first-use TLS trust for a ticket-bound owned PLCSIM instance, shared by credential provisioning and the
    /// station upload: the first certificate of an attempt is pinned and every later challenge must present the same one. It is
    /// never granted without the caller's explicit authorization and a revalidated owned target, and it is not certificate authentication.
    /// Only the station upload passes allowWithoutCertificate (maintainer decision of 29 October 2026): a challenge offering no
    /// certificate is then trusted as unauthenticated TLS, its identity resting on the ownership checks alone, and only when TIA
    /// reported the challenge NonVerified as natively observed; an attempt trusted that way never pins a certificate later.
    /// </summary>
    internal static class SimulationTlsPin
    {
        internal const string FirstUse = "allowOwnedEmptyCpuFirstUse", SamePin = "allowSameAttemptPinnedCertificate",
            WithoutCertificate = "allowOwnedSimulationWithoutCertificate";

        internal static bool Allows(string decision) => decision == FirstUse || decision == SamePin || decision == WithoutCertificate;

        internal static string Decide(bool firstUseAuthorized, string attemptPin, string observedSha256, int certificateCount,
            DateTime notBeforeUtc, DateTime notAfterUtc, DateTime nowUtc, bool metadataComplete, bool ownedTargetVerified,
            bool credentialOpened, bool rejectionLatched, bool allowWithoutCertificate,
            bool attemptTrustedWithoutCertificate = false, bool reportedNonVerified = false)
        {
            if (!firstUseAuthorized) return "firstUseTrustNotAuthorized";
            if (rejectionLatched) return "priorRejectionLatched";
            if (credentialOpened) return "credentialAlreadyOpened";
            if (!ownedTargetVerified) return "ownedTargetNotVerified";
            if (!metadataComplete) return "certificateMetadataIncomplete";
            if (certificateCount == 0 && allowWithoutCertificate)
            {
                // An attempt that pinned a certificate keeps it; it never falls back to a challenge without one.
                if (attemptPin != null) return "attemptCertificateHashChanged";
                // The enum, not the localized verification text, is the native signal (29 October: NonVerified, "certificate not matching").
                return reportedNonVerified ? WithoutCertificate : "withoutCertificateNotReportedNonVerified";
            }
            // The mirror: an attempt trusted without a certificate never pins one later, also not after the login was answered.
            if (attemptTrustedWithoutCertificate) return "attemptTrustedWithoutCertificate";
            if (certificateCount != 1) return "certificateCountNotOne";
            if (!ValidPin(observedSha256)) return "invalidObservedCertificateHash";
            if (notBeforeUtc.Kind != DateTimeKind.Utc || notAfterUtc.Kind != DateTimeKind.Utc || nowUtc.Kind != DateTimeKind.Utc ||
                notBeforeUtc > nowUtc || nowUtc > notAfterUtc) return "certificateOutsideValidityInterval";
            if (attemptPin == null) return FirstUse;
            if (!ValidPin(attemptPin) || !string.Equals(attemptPin, observedSha256, StringComparison.Ordinal))
                return "attemptCertificateHashChanged";
            return SamePin;
        }

        internal static bool ValidPin(string value) => value != null && value.Length == 64 && value.All(character =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
    }
}
