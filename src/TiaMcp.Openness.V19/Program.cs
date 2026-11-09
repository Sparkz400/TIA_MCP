using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using TiaMcp.Openness.V21;

namespace TiaMcp.Openness.V19
{
    /// <summary>
    /// Read-only TIA Portal V19 worker. Same line-delimited JSON protocol (version 1) and limits as the V21 worker, but
    /// only the read operations V19Session implements; every other operation is refused before any native call.
    /// </summary>
    internal static class Program
    {
        internal const int TiaMajor = 19;
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string pipe = null;
                string apiDirectory = InstalledApiDirectory();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--pipe" && i + 1 < args.Length) pipe = args[++i];
                    else if (args[i] == "--api-dir" && i + 1 < args.Length) apiDirectory = args[++i];
                    // The V19 worker has no simulator, library-apply or qualification modes; a V21 option is a misconfigured Host.
                    else throw new ArgumentException("Usage: TiaMcp.Openness.V19.exe --pipe NAME [--api-dir DIR]. Qualification modes are V21-only.");
                }
                if (string.IsNullOrWhiteSpace(pipe)) throw new ArgumentException("--pipe is required.");
                apiDirectory = Path.GetFullPath(apiDirectory);
                string assemblyPath = Path.Combine(apiDirectory, "Siemens.Engineering.dll");
                if (!File.Exists(assemblyPath)) throw new FileNotFoundException("Installed TIA V19 Openness assembly not found.", assemblyPath);
                if (AssemblyName.GetAssemblyName(assemblyPath).Version.Major != TiaMajor)
                    throw new InvalidOperationException("Only the TIA V19 API is supported by this worker.");
                AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => ResolveSiemens(eventArgs, apiDirectory);
                return Run(pipe, apiDirectory);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        // Siemens registers the installed V19 API location; C:\Program Files is only the setup default.
        private static string InstalledApiDirectory()
        {
            using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\19.0\PublicAPI\19.0.0.0"))
            {
                var assemblyPath = key?.GetValue("Siemens.Engineering") as string;
                return string.IsNullOrWhiteSpace(assemblyPath)
                    ? @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19"
                    : Path.GetDirectoryName(assemblyPath);
            }
        }

        private static Assembly ResolveSiemens(ResolveEventArgs args, string apiDirectory)
        {
            var name = new AssemblyName(args.Name);
            if (name.Name != "Siemens.Engineering" && !name.Name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(apiDirectory, name.Name + ".dll");
            if (!File.Exists(path)) return null;
            var installed = AssemblyName.GetAssemblyName(path);
            if (!AssemblyName.ReferenceMatchesDefinition(name, installed)) return null;
            return Assembly.LoadFrom(path);
        }

        // Keep JIT loading of Siemens-dependent code after installing the resolver.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string pipeName, string apiDirectory) =>
            WorkerLoop.Run(pipeName, () => new V19Session(apiDirectory), (session, operation, arguments) => session.Execute(operation, arguments));
    }
}
