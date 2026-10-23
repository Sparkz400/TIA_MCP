using System;

namespace TiaMcp.Model
{
    internal static class HmiAssemblyIdentityRules
    {
        // Required token is supplied from the actually loaded Siemens Base API;
        // it is not guessed from a module filename or a fabricated fixture.
        internal static string Rejection(string expectedName, string actualName, string expectedToken, string actualToken,
            int majorVersion, string culture)
        {
            if (majorVersion != 21) return "HmiVersionMismatch";
            if (string.IsNullOrEmpty(expectedToken) || !string.Equals(expectedName, actualName, StringComparison.Ordinal)
                || !string.Equals(expectedToken, actualToken, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(culture) && culture != "neutral") return "HmiAssemblyIdentityMismatch";
            return null;
        }
    }
}
