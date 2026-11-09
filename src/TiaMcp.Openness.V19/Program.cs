using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private const int MaxRequestBytes = 4 * 1024 * 1024;
        private const int MaxResponseBytes = 16 * 1024 * 1024;

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
        private static int Run(string pipeName, string apiDirectory)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(30000);
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true))
                using (var writer = new TiaMcp.Transport.WorkerPipeWriter(pipe))
                using (var session = new V19Session(apiDirectory))
                {
                    while (true)
                    {
                        string line = ReadBoundedLine(reader);
                        if (line == null) { writer.PeerEofReceived = true; break; }
                        JObject response;
                        string id = null;
                        try
                        {
                            using (var jsonReader = new ExactNumberReader(line) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
                            {
                                var request = JObject.Load(jsonReader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                                if (jsonReader.Read()) throw new BridgeException("InvalidRequest", "Only one JSON object per request is accepted.");
                                id = request.Value<string>("id");
                                if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new BridgeException("InvalidRequest", "A request id of 1–128 characters is required.");
                                if (jsonReader.OutOfRange) throw new BridgeException("InvalidRequest", "A request number is outside the double range.");
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
                        // Openness proxies release their Portal-side objects only when finalized.
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
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
