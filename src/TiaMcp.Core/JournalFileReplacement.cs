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

        internal static void Replace(Action replace, Action<int> delay)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { replace(); return; }
                // Only ERROR_UNABLE_TO_REMOVE_REPLACED guarantees both original names remain intact.
                catch (IOException error) when (error.HResult == unchecked((int)0x80070497) && attempt < 10)
                { delay(10); }
            }
        }
    }
}
