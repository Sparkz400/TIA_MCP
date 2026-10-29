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
                bool metadata = true, bool owned = true, bool opened = false, bool latched = false) =>
                SimulationTlsPin.Decide(authorized, attempt!, observed ?? pin, count, notBefore ?? from, to, current ?? now, metadata, owned, opened, latched);

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
    }
}
