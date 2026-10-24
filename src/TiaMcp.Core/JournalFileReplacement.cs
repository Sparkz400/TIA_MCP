#nullable disable
using System;
using System.IO;
using System.Threading;

namespace TiaMcp.Core
{
    public static class JournalFileReplacement
    {
        public static void Replace(string temporary, string destination)
        {
            Replace(() => File.Replace(temporary, destination, null), Thread.Sleep);
        }

        // Each retried failure leaves both original names intact: ERROR_UNABLE_TO_REMOVE_REPLACED, or a sharing/lock
        // violation raised before ReplaceFile changes anything (an external reader without delete sharing, seen natively).
        private static bool Retryable(int result) => result == unchecked((int)0x80070497) || result == unchecked((int)0x80070020) || result == unchecked((int)0x80070021);

        // Bounded: 20 attempts, backoff 10..100 ms, about 1.5 s in total.
        internal static void Replace(Action replace, Action<int> delay)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { replace(); return; }
                catch (IOException error) when (Retryable(error.HResult) && attempt < 20)
                { delay(Math.Min(10 * attempt, 100)); }
            }
        }
    }
}
