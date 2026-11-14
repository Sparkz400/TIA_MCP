using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TiaMcp.Openness.V21
{
    internal static class Program
    {
        // Set from --physical-plc-online. Kept here, not on OpennessSession: Main must not touch a type that loads Siemens
        // assemblies before the resolver is installed.
        internal static bool PhysicalPlcOnline;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string pipe = null;
                bool componentQualification = false;
                bool testSuiteScopeProbe = false;
                bool testSuiteQualification = false;
                bool translationQualification = false;
                bool libraryIntegrateApply = false;
                bool fingerprintReuse = false;
                string hmiTextListQualification = null;
                string hmiChangeQualification = null;
                string apiDirectory = InstalledApiDirectory();
                string simulatorApiDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Siemens", "PLCSIMADV", "API", "8.0");
                string discoveryProbe = null;
                bool startupDiscoveryProbe = false;
                bool visibleDiscoveryProbe = false;
                bool lateDiscoveryProbe = false;
                bool configuredDiscoveryProbe = false;
                bool freshPortalDiscoveryProbe = false;
                string placeholderIdentityProbe = null;
                bool staleConnectProbe = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--pipe" && i + 1 < args.Length) pipe = args[++i];
                    else if ((args[i] == "--plcsim-discovery-probe" || args[i] == "--plcsim-startup-discovery-probe" || args[i] == "--plcsim-ui-discovery-probe" || args[i] == "--plcsim-late-discovery-probe" || args[i] == "--plcsim-configured-discovery-probe" || args[i] == "--plcsim-fresh-portal-discovery-probe" || args[i] == "--plcsim-stale-connect-probe") && i + 1 < args.Length)
                    {
                        if (discoveryProbe != null) throw new ArgumentException("Only one discovery probe mode is allowed.");
                        visibleDiscoveryProbe = args[i] == "--plcsim-ui-discovery-probe";
                        lateDiscoveryProbe = args[i] == "--plcsim-late-discovery-probe";
                        staleConnectProbe = args[i] == "--plcsim-stale-connect-probe";
                        freshPortalDiscoveryProbe = staleConnectProbe || args[i] == "--plcsim-fresh-portal-discovery-probe";
                        configuredDiscoveryProbe = freshPortalDiscoveryProbe || args[i] == "--plcsim-configured-discovery-probe";
                        startupDiscoveryProbe = visibleDiscoveryProbe || lateDiscoveryProbe || configuredDiscoveryProbe || args[i] == "--plcsim-startup-discovery-probe";
                        discoveryProbe = args[++i];
                    }
                    else if (args[i] == "--plcsim-placeholder-identity-probe" && i + 1 < args.Length && placeholderIdentityProbe == null) placeholderIdentityProbe = args[++i];
                    else if (args[i] == "--api-dir" && i + 1 < args.Length) apiDirectory = args[++i];
                    else if (args[i] == "--sim-api-dir" && i + 1 < args.Length) simulatorApiDirectory = args[++i];
                    else if (args[i] == "--component-qualification") componentQualification = true;
                    else if (args[i] == "--physical-plc-online") PhysicalPlcOnline = true;
                    else if (args[i] == "--testsuite-scope-probe") testSuiteScopeProbe = true;
                    else if (args[i] == "--translation-qualification") translationQualification = true;
                    else if (args[i] == "--enable-library-integrate-apply") libraryIntegrateApply = true;
                    else if (args[i] == "--enable-fingerprint-reuse") fingerprintReuse = true;
                    else if (args[i] == "--hmi-text-list-qualification" && i + 1 < args.Length && hmiTextListQualification == null &&
                        Array.IndexOf(TiaMcp.Model.HmiTextListImportRules.QualificationProfiles, args[i + 1]) >= 0) hmiTextListQualification = args[++i];
                    else if (args[i] == "--hmi-change-qualification" && i + 1 < args.Length && hmiChangeQualification == null &&
                        Array.IndexOf(TiaMcp.Model.HmiEngineeringRules.QualificationProfiles, args[i + 1]) >= 0) hmiChangeQualification = args[++i];
#if TESTSUITE_QUALIFICATION
                    else if (args[i] == "--testsuite-qualification") testSuiteQualification = true;
#endif
                    else throw new ArgumentException("Usage: TiaMcp.Openness.V21.exe --pipe NAME [--api-dir DIR]");
                }
                if (placeholderIdentityProbe != null && (discoveryProbe != null || pipe != null || componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || hmiTextListQualification != null || hmiChangeQualification != null))
                    throw new ArgumentException("The placeholder identity probe runs alone.");
                if (string.IsNullOrWhiteSpace(pipe) && discoveryProbe == null && placeholderIdentityProbe == null) throw new ArgumentException("--pipe is required.");
                if (discoveryProbe != null && (pipe != null || componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || hmiTextListQualification != null || hmiChangeQualification != null))
                    throw new ArgumentException("Discovery probes cannot be combined with a pipe or another qualification mode.");
                apiDirectory = Path.GetFullPath(apiDirectory);
                string basePath = Path.Combine(apiDirectory, "Siemens.Engineering.Base.dll");
                if (!File.Exists(basePath)) throw new FileNotFoundException("Installed TIA V21 Openness Base assembly not found.", basePath);
                if (AssemblyName.GetAssemblyName(basePath).Version.Major != 21)
                    throw new InvalidOperationException("Only the TIA V21 API is supported by this worker.");
                simulatorApiDirectory = Path.GetFullPath(simulatorApiDirectory);
                AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => ResolveSiemens(eventArgs, apiDirectory, simulatorApiDirectory);
                if (placeholderIdentityProbe != null) return PlcsimPlaceholderIdentityProbe.Run(placeholderIdentityProbe);
                if (discoveryProbe != null) return RunDiscoveryProbe(discoveryProbe, startupDiscoveryProbe, visibleDiscoveryProbe, lateDiscoveryProbe, configuredDiscoveryProbe, freshPortalDiscoveryProbe, staleConnectProbe);
                if (testSuiteScopeProbe && componentQualification) throw new ArgumentException("Separate qualification modes required.");
                if (testSuiteQualification && (componentQualification || testSuiteScopeProbe)) throw new ArgumentException("Separate qualification modes required.");
                if (translationQualification && (componentQualification || testSuiteScopeProbe || testSuiteQualification)) throw new ArgumentException("Separate qualification modes required.");
                if (hmiTextListQualification != null && (componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification)) throw new ArgumentException("Separate qualification modes required.");
                if (hmiChangeQualification != null && (componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || hmiTextListQualification != null)) throw new ArgumentException("Separate qualification modes required.");
                return Run(pipe, apiDirectory, componentQualification, testSuiteScopeProbe, testSuiteQualification, translationQualification, libraryIntegrateApply, hmiTextListQualification, hmiChangeQualification, fingerprintReuse);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        // Siemens registers the installed V21 API location; C:\Program Files is only the setup default.
        private static string InstalledApiDirectory()
        {
            using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48"))
            {
                var basePath = key?.GetValue("Siemens.Engineering.Base") as string;
                return string.IsNullOrWhiteSpace(basePath)
                    ? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48"
                    : Path.GetDirectoryName(basePath);
            }
        }

        private static Assembly ResolveSiemens(ResolveEventArgs args, string apiDirectory, string simulatorApiDirectory)
        {
            var name = new AssemblyName(args.Name);
            string path;
            if (name.Name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal))
                path = Path.Combine(apiDirectory, name.Name + ".dll");
            else if (name.Name == "Siemens.Simatic.Simulation.Runtime.Api.x64")
                path = Path.Combine(simulatorApiDirectory, name.Name + ".dll");
            else return null;
            if (!File.Exists(path)) return null;
            var installed = AssemblyName.GetAssemblyName(path);
            if (!AssemblyName.ReferenceMatchesDefinition(name, installed)) return null;
            return Assembly.LoadFrom(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunDiscoveryProbe(string outputDirectory, bool startCpu, bool visible, bool late, bool configured, bool freshPortal, bool staleConnect) => PlcsimDiscoveryProbe.Run(outputDirectory, startCpu, visible, late, configured, freshPortal, staleConnect);

        // Keep JIT loading of Siemens-dependent code after installing the resolver.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string pipeName, string apiDirectory, bool componentQualification, bool testSuiteScopeProbe, bool testSuiteQualification, bool translationQualification, bool libraryIntegrateApply, string hmiTextListQualification, string hmiChangeQualification, bool fingerprintReuse) =>
            WorkerLoop.Run(pipeName,
                () => new OpennessSession(apiDirectory, componentQualification, testSuiteScopeProbe, testSuiteQualification, translationQualification, libraryIntegrateApply) { FingerprintReuse = fingerprintReuse, HmiTextListQualificationProfile = hmiTextListQualification, HmiChangeQualificationProfile = hmiChangeQualification },
                (session, operation, arguments) => session.Execute(operation, arguments));
    }
}
