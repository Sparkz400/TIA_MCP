#nullable disable
using System;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Security
{
    /// <summary>Seals one credential-store secret for one admitted worker change apply. Compiled into both the Host and the V21 worker.</summary>
    internal static class CredentialRouteEnvelope
    {
        internal const int MaxPasswordBytes = 4096;
        private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);

        // The lease token is fresh for every admission, so an envelope opens for one dispatch of one exact change, slot and reference.
        internal static byte[] Entropy(string leaseToken, string changeId, string previewHash, string slot, string credentialId, string bindingSha256) =>
            Strict.GetBytes(string.Join("\n", "TiaMcp.CredentialRoute.v1", "change_apply", leaseToken, changeId, previewHash, slot, credentialId, bindingSha256));

        /// <summary>A routed password is 1 to 4096 bytes of strict UTF-8, exactly as stored.</summary>
        internal static bool IsPassword(byte[] secret)
        {
            if (secret == null || secret.Length < 1 || secret.Length > MaxPasswordBytes) return false;
            try { Strict.GetCharCount(secret); return true; }
            catch (DecoderFallbackException) { return false; }
        }

        internal static byte[] Seal(byte[] secret, byte[] entropy) => ProtectedData.Protect(secret, entropy, DataProtectionScope.CurrentUser);

        /// <returns>A read-only SecureString, or null when the envelope does not open under this exact entropy as a password.</returns>
        internal static SecureString Open(byte[] envelope, byte[] entropy)
        {
            byte[] clear = null; char[] characters = null; SecureString password = null;
            try
            {
                clear = ProtectedData.Unprotect(envelope, entropy, DataProtectionScope.CurrentUser);
                if (!IsPassword(clear)) return null;
                characters = Strict.GetChars(clear);
                password = new SecureString();
                foreach (char character in characters) password.AppendChar(character);
                password.MakeReadOnly();
                return password;
            }
            catch (CryptographicException) { password?.Dispose(); return null; }
            finally
            {
                if (clear != null) Array.Clear(clear, 0, clear.Length);
                if (characters != null) Array.Clear(characters, 0, characters.Length);
            }
        }
    }
}
