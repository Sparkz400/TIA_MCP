using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TiaMcp.AddIn
{
    /// <summary>One request line to the Host's handoff pipe, sent only after the server proves to be the Host process discovery names.</summary>
    /// <remarks>Raw kernel32 handles: TIA's Add-In sandbox demands full trust for NamedPipeClientStream, SafeHandle and Marshal.GetLastWin32Error.</remarks>
    internal static class HostPipe
    {
        private const string Prefix = "TiaMcp-AddIn-";
        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, OpenExisting = 3, QueryLimitedInformation = 0x1000;
        private static readonly IntPtr Invalid = new IntPtr(-1);

        internal static string Exchange(string pipeName, uint processId, string startedAtUtc, string request, long responseLimit)
        {
            // The name becomes a device path: anything but the Host's own pipe name could open another file.
            if (pipeName == null || !pipeName.StartsWith(Prefix, StringComparison.Ordinal) || pipeName.Length > 128
                || !pipeName.All(c => c == '-' || c < 128 && char.IsLetterOrDigit(c)))
                throw new InvalidOperationException("Discovery names no TIA MCP handoff pipe.");
            string path = @"\\.\pipe\" + pipeName;
            if (!WaitNamedPipe(path, 5000)) throw Failure("No Host pipe instance became available");
            IntPtr pipe = CreateFile(path, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (pipe == Invalid) throw Failure("Opening the Host pipe failed");
            try
            {
                // Discovery is only a hint: send nothing unless the server is the exact Host process instance it names.
                if (!GetNamedPipeServerProcessId(pipe, out uint server) || server != processId || StartedAtUtc(server) != startedAtUtc)
                    throw new InvalidOperationException("The pipe server is not the Host named in discovery.");
                byte[] frame = Encoding.UTF8.GetBytes(request + "\n");
                if (!WriteFile(pipe, frame, frame.Length, out int written, IntPtr.Zero) || written != frame.Length) throw Failure("Writing to the Host pipe failed");
                var response = new MemoryStream(); var buffer = new byte[8192];
                while (true)
                {
                    if (!ReadFile(pipe, buffer, buffer.Length, out int read, IntPtr.Zero) || read == 0)
                        throw new InvalidOperationException("The Host response was truncated or exceeded its bound.");
                    int end = Array.IndexOf(buffer, (byte)'\n', 0, read);
                    response.Write(buffer, 0, end < 0 ? read : end);
                    if (response.Length > responseLimit) throw new InvalidOperationException("The Host response was truncated or exceeded its bound.");
                    if (end >= 0) return new UTF8Encoding(false, true).GetString(response.ToArray());
                }
            }
            finally { CloseHandle(pipe); }
        }

        private static string StartedAtUtc(uint processId)
        {
            IntPtr process = OpenProcess(QueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) throw Failure("Opening the Host process failed");
            try { return GetProcessTimes(process, out long created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created).ToString("O") : throw Failure("Reading the Host start time failed"); }
            finally { CloseHandle(process); }
        }

        // shortcut: the runtime may overwrite the last error before this reads it; the code is diagnostic only.
        private static Exception Failure(string what) => new InvalidOperationException(what + " (Windows error " + GetLastError() + ").");

        [DllImport("kernel32.dll")] private static extern int GetLastError();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WaitNamedPipeW")] private static extern bool WaitNamedPipe(string name, uint timeout);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
        [DllImport("kernel32.dll")] private static extern bool WriteFile(IntPtr file, byte[] buffer, int count, out int written, IntPtr overlapped);
        [DllImport("kernel32.dll")] private static extern bool ReadFile(IntPtr file, byte[] buffer, int count, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    }
}
