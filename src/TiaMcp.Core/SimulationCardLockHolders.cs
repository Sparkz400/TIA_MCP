#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TiaMcp.Simulation
{
    /// <summary>
    /// Names the processes holding a file open below a card directory. Windows refuses to move a directory with any open file
    /// below it, whatever the handle's share mode, with access denied (0x80070005); the Restart Manager lists those holders.
    /// Diagnostic only: file names are registered, contents are never read, and failures are returned, never thrown.
    /// </summary>
    public static class SimulationCardLockHolders
    {
        public const int MaximumFiles = 4096, MaximumHolders = 32;

        public sealed class Holder
        {
            public int ProcessId;
            public string AppName, ServiceName;
            public int ApplicationType;
        }

        public static Holder[] Find(string directory, out string error)
        {
            error = null;
            uint session = 0; bool started = false;
            try
            {
                var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(MaximumFiles + 1).ToArray();
                if (files.Length > MaximumFiles) { error = "More than " + MaximumFiles + " files; holders were not queried."; return new Holder[0]; }
                if (files.Length == 0) return new Holder[0];
                int code = RmStartSession(out session, 0, new StringBuilder(33));
                if (code != 0) { error = "RmStartSession " + code; return new Holder[0]; }
                started = true;
                code = RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null);
                if (code != 0) { error = "RmRegisterResources " + code; return new Holder[0]; }
                uint needed, count = MaximumHolders, reasons = 0;
                var found = new RmProcessInfo[MaximumHolders];
                code = RmGetList(session, out needed, ref count, found, ref reasons);
                if (code == 234) error = needed + " holders; only the first " + MaximumHolders + " are listed."; // ERROR_MORE_DATA fills nothing
                else if (code != 0) { error = "RmGetList " + code; return new Holder[0]; }
                return found.Take(code == 0 ? (int)count : 0).Select(info => new Holder { ProcessId = info.Process.ProcessId,
                    AppName = info.AppName, ServiceName = info.ServiceShortName, ApplicationType = info.ApplicationType }).ToArray();
            }
            catch (Exception exception) { error = exception.GetType().FullName + ": " + exception.Message; return new Holder[0]; }
            finally { if (started) RmEndSession(session); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess { public int ProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME StartTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
            public int ApplicationType;
            public uint AppStatus, TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount, IntPtr applications, uint serviceCount, string[] services);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[] processes, ref uint rebootReasons);
    }
}
