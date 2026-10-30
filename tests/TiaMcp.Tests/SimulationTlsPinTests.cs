using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class SimulationTlsPinTests
    {
        internal static Task FirstUsePinsOnlyTheAuthorizedOwnedCertificate()
        {
            string pin = new string('a', 64), other = new string('b', 64);
            var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2045, 12, 31, 0, 0, 0, DateTimeKind.Utc);
            var now = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
            string Decide(bool authorized = true, string? attempt = null, string? observed = null, int count = 1, DateTime? notBefore = null, DateTime? current = null,
                bool metadata = true, bool owned = true, bool opened = false, bool latched = false, bool withoutCertificate = false) =>
                SimulationTlsPin.Decide(authorized, attempt!, observed ?? pin, count, notBefore ?? from, to, current ?? now, metadata, owned, opened, latched, withoutCertificate);

            Check.Equal(SimulationTlsPin.FirstUse, Decide());
            Check.Equal(SimulationTlsPin.SamePin, Decide(attempt: pin));
            Check.Equal(true, SimulationTlsPin.Allows(Decide()) && SimulationTlsPin.Allows(Decide(attempt: pin)));
            // A changed certificate within the attempt, and every missing precondition, keeps the default NonTrusted.
            Check.Equal("attemptCertificateHashChanged", Decide(attempt: pin, observed: other));
            Check.Equal("firstUseTrustNotAuthorized", Decide(authorized: false));
            Check.Equal("priorRejectionLatched", Decide(latched: true));
            Check.Equal("credentialAlreadyOpened", Decide(opened: true));
            Check.Equal("ownedTargetNotVerified", Decide(owned: false));
            Check.Equal("certificateMetadataIncomplete", Decide(metadata: false));
            Check.Equal("certificateCountNotOne", Decide(count: 0));
            Check.Equal("certificateCountNotOne", Decide(count: 2));
            Check.Equal("invalidObservedCertificateHash", Decide(observed: pin.ToUpperInvariant()));
            Check.Equal("certificateOutsideValidityInterval", Decide(current: to.AddDays(1)));
            Check.Equal("certificateOutsideValidityInterval", Decide(notBefore: DateTime.SpecifyKind(from, DateTimeKind.Local)));
            foreach (string decision in new[] { "attemptCertificateHashChanged", "firstUseTrustNotAuthorized", "certificateCountNotOne" })
                Check.Equal(false, SimulationTlsPin.Allows(decision));
            return Task.CompletedTask;
        }

        // The station upload's opt-in (maintainer decision of 29 October 2026): a challenge without a certificate is trusted as
        // unauthenticated TLS only for the authorized, revalidated owned instance; provisioning keeps requiring one certificate.
        internal static Task WithoutCertificateOnlyForTheAuthorizedOwnedUpload()
        {
            var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
            string pin = new string('a', 64);
            string None(bool allow = true, bool authorized = true, string? attempt = null, int count = 0, bool owned = true, bool opened = false,
                bool latched = false, bool metadata = true, bool unauthenticated = false, bool nonVerified = true) =>
                SimulationTlsPin.Decide(authorized, attempt!, count == 1 ? pin : null!, count, from, from.AddYears(20), now, metadata, owned, opened, latched, allow,
                    unauthenticated, nonVerified);

            Check.Equal(SimulationTlsPin.WithoutCertificate, None());
            Check.Equal(true, SimulationTlsPin.Allows(None()));
            // Only the NonVerified state TIA reported natively; Trusted, NonTrusted or an unread selection refuses, and so does the default.
            Check.Equal("withoutCertificateNotReportedNonVerified", None(nonVerified: false));
            Check.Equal(false, SimulationTlsPin.Allows(None(nonVerified: false)));
            Check.Equal("withoutCertificateNotReportedNonVerified",
                SimulationTlsPin.Decide(true, null!, null!, 0, from, from.AddYears(20), now, true, true, false, false, true));
            Check.Equal("certificateCountNotOne", None(allow: false));
            Check.Equal("firstUseTrustNotAuthorized", None(authorized: false));
            Check.Equal("ownedTargetNotVerified", None(owned: false));
            Check.Equal("credentialAlreadyOpened", None(opened: true));
            Check.Equal("priorRejectionLatched", None(latched: true));
            Check.Equal("certificateMetadataIncomplete", None(metadata: false));
            Check.Equal("attemptCertificateHashChanged", None(attempt: pin));
            Check.Equal("certificateCountNotOne", None(count: 2));
            // One certificate keeps the pin rule.
            Check.Equal(SimulationTlsPin.FirstUse, None(count: 1));
            Check.Equal(SimulationTlsPin.SamePin, None(count: 1, attempt: pin));
            return Task.CompletedTask;
        }

        // The mirror of "a pinned attempt never falls back to no certificate": once an attempt trusted a certificate-less challenge,
        // a later challenge offering a certificate is refused. The caller passes credentialOpened=false then (the trust preceded the
        // login), so this is the only rule that keeps a certificate from being pinned after the login was answered.
        internal static Task UnauthenticatedAttemptNeverPinsACertificateLater()
        {
            var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var now = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
            string pin = new string('a', 64);
            string Later(int count, bool unauthenticated = true, bool nonVerified = true) =>
                SimulationTlsPin.Decide(true, null!, count == 1 ? pin : null!, count, from, from.AddYears(20), now, true, true, false, false, true, unauthenticated, nonVerified);

            Check.Equal("attemptTrustedWithoutCertificate", Later(1));
            Check.Equal("attemptTrustedWithoutCertificate", Later(2));
            Check.Equal(false, SimulationTlsPin.Allows(Later(1)));
            // A later certificate-less challenge may recur in the same form, still only when reported NonVerified.
            Check.Equal(SimulationTlsPin.WithoutCertificate, Later(0));
            Check.Equal("withoutCertificateNotReportedNonVerified", Later(0, nonVerified: false));
            // Without a prior unauthenticated trust the certificate is pinned on first use as before.
            Check.Equal(SimulationTlsPin.FirstUse, Later(1, unauthenticated: false));
            // Provisioning never trusts without a certificate, so the flag cannot arise there; its defaults keep the old rule.
            Check.Equal(SimulationTlsPin.FirstUse, SimulationTlsPin.Decide(true, null!, pin, 1, from, from.AddYears(20), now, true, true, false, false, false));
            return Task.CompletedTask;
        }
    }
}
