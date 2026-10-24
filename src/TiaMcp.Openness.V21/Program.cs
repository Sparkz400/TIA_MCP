using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal static class Program
    {
        private const int MaxRequestBytes = 4 * 1024 * 1024;
        private const int MaxResponseBytes = 16 * 1024 * 1024;

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
                string apiDirectory = InstalledApiDirectory();
                string simulatorApiDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Siemens", "PLCSIMADV", "API", "8.0");
                string discoveryProbe = null;
                bool startupDiscoveryProbe = false;
                bool visibleDiscoveryProbe = false;
                bool lateDiscoveryProbe = false;
                bool configuredDiscoveryProbe = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--pipe" && i + 1 < args.Length) pipe = args[++i];
                    else if ((args[i] == "--plcsim-discovery-probe" || args[i] == "--plcsim-startup-discovery-probe" || args[i] == "--plcsim-ui-discovery-probe" || args[i] == "--plcsim-late-discovery-probe" || args[i] == "--plcsim-configured-discovery-probe") && i + 1 < args.Length)
                    {
                        if (discoveryProbe != null) throw new ArgumentException("Only one discovery probe mode is allowed.");
                        visibleDiscoveryProbe = args[i] == "--plcsim-ui-discovery-probe";
                        lateDiscoveryProbe = args[i] == "--plcsim-late-discovery-probe";
                        configuredDiscoveryProbe = args[i] == "--plcsim-configured-discovery-probe";
                        startupDiscoveryProbe = visibleDiscoveryProbe || lateDiscoveryProbe || configuredDiscoveryProbe || args[i] == "--plcsim-startup-discovery-probe";
                        discoveryProbe = args[++i];
                    }
                    else if (args[i] == "--api-dir" && i + 1 < args.Length) apiDirectory = args[++i];
                    else if (args[i] == "--sim-api-dir" && i + 1 < args.Length) simulatorApiDirectory = args[++i];
                    else if (args[i] == "--component-qualification") componentQualification = true;
                    else if (args[i] == "--testsuite-scope-probe") testSuiteScopeProbe = true;
                    else if (args[i] == "--translation-qualification") translationQualification = true;
#if TESTSUITE_QUALIFICATION
                    else if (args[i] == "--testsuite-qualification") testSuiteQualification = true;
#endif
                    else throw new ArgumentException("Usage: TiaMcp.Openness.V21.exe --pipe NAME [--api-dir DIR]");
                }
                if (string.IsNullOrWhiteSpace(pipe) && discoveryProbe == null) throw new ArgumentException("--pipe is required.");
                if (discoveryProbe != null && (pipe != null || componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification))
                    throw new ArgumentException("Discovery probes cannot be combined with a pipe or another qualification mode.");
                apiDirectory = Path.GetFullPath(apiDirectory);
                string basePath = Path.Combine(apiDirectory, "Siemens.Engineering.Base.dll");
                if (!File.Exists(basePath)) throw new FileNotFoundException("Installed TIA V21 Openness Base assembly not found.", basePath);
                if (AssemblyName.GetAssemblyName(basePath).Version.Major != 21)
                    throw new InvalidOperationException("Only the TIA V21 API is supported by this worker.");
                simulatorApiDirectory = Path.GetFullPath(simulatorApiDirectory);
                AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => ResolveSiemens(eventArgs, apiDirectory, simulatorApiDirectory);
                if (discoveryProbe != null) return RunDiscoveryProbe(discoveryProbe, startupDiscoveryProbe, visibleDiscoveryProbe, lateDiscoveryProbe, configuredDiscoveryProbe);
                if (testSuiteScopeProbe && componentQualification) throw new ArgumentException("Separate qualification modes required.");
                if (testSuiteQualification && (componentQualification || testSuiteScopeProbe)) throw new ArgumentException("Separate qualification modes required.");
                if (translationQualification && (componentQualification || testSuiteScopeProbe || testSuiteQualification)) throw new ArgumentException("Separate qualification modes required.");
                return Run(pipe, apiDirectory, componentQualification, testSuiteScopeProbe, testSuiteQualification, translationQualification);
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
        private static int RunDiscoveryProbe(string outputDirectory, bool startCpu, bool visible, bool late, bool configured) => PlcsimDiscoveryProbe.Run(outputDirectory, startCpu, visible, late, configured);

        // Keep JIT loading of Siemens-dependent code after installing the resolver.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string pipeName, string apiDirectory, bool componentQualification, bool testSuiteScopeProbe, bool testSuiteQualification, bool translationQualification)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(30000);
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true))
                using (var writer = new TiaMcp.Transport.WorkerPipeWriter(pipe))
            using (var session = new OpennessSession(apiDirectory, componentQualification, testSuiteScopeProbe, testSuiteQualification, translationQualification))
                {
                    while (true)
                    {
                        string line = ReadBoundedLine(reader);
                        if (line == null) { writer.PeerEofReceived = true; break; }
                        JObject response;
                        string id = null;
                        try
                        {
                            using (var jsonReader = new JsonTextReader(new StringReader(line)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
                            {
                                var request = JObject.Load(jsonReader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                                if (jsonReader.Read()) throw new BridgeException("InvalidRequest", "Only one JSON object per request is accepted.");
                                id = request.Value<string>("id");
                                if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new BridgeException("InvalidRequest", "A request id of 1–128 characters is required.");
                                if (request.Value<int?>("protocolVersion") != 1) throw new BridgeException("ProtocolVersionMismatch", "This worker accepts protocolVersion 1.");
                                string operation = request.Value<string>("operation");
                                if (string.IsNullOrWhiteSpace(operation)) throw new BridgeException("InvalidRequest", "operation is required.");
                                var arguments = request["arguments"] as JObject;
                                if (request["arguments"] != null && arguments == null) throw new BridgeException("InvalidRequest", "arguments must be an object.");
                                response = new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = true, ["result"] = session.Execute(operation, arguments ?? new JObject()) };
                            }
                        }
                        catch (Exception exception) { response = Error(id, exception); }
                        string output = response.ToString(Formatting.None);
                        if (Encoding.UTF8.GetByteCount(output) > MaxResponseBytes)
                            output = Error(id, new BridgeException("ResponseTooLarge", "Response exceeds 16 MiB. Request fewer items. Export artifacts remain in the requested artifact directory.")).ToString(Formatting.None);
                        writer.WriteLine(output);
                    }
                }
            }
            return 0;
        }

        private static string ReadBoundedLine(TextReader reader)
        {
            var builder = new StringBuilder();
            int character;
            while ((character = reader.Read()) != -1)
            {
                if (character == '\n') break;
                if (builder.Length >= MaxRequestBytes) throw new InvalidDataException("Request exceeds 4 MiB; closing pipe.");
                builder.Append((char)character);
            }
            if (character == -1 && builder.Length == 0) return null;
            if (character == -1) throw new InvalidDataException("Unterminated request at EOF; refusing to execute a partial JSON frame.");
            string line = builder.ToString();
            if (Encoding.UTF8.GetByteCount(line) > MaxRequestBytes) throw new InvalidDataException("Request exceeds 4 MiB; closing pipe.");
            return line;
        }

        private static JObject Error(string id, Exception exception)
        {
            var bridge = exception as BridgeException;
            string code = bridge?.Code ?? (exception is JsonException || exception is ArgumentException || exception is FormatException ? "InvalidRequest" : "EngineeringError");
            if (exception.GetType().Name.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0) code = "AccessDenied";
            if (exception.GetType().Name.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0) code = "LicenseUnavailable";
            return new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = false,
                ["error"] = new JObject { ["code"] = code, ["message"] = exception.Message, ["details"] = bridge?.Details ?? new JObject { ["exceptionType"] = exception.GetType().FullName } } };
        }
    }
}
