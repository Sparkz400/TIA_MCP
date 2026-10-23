using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Simulator.V8
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string pipe = null;
                string directory = @"C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\API\8.0";
                bool diagnose = false;
                bool selfTest = false;
                string selfTestDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--pipe" && i + 1 < args.Length) pipe = args[++i];
                    else if (args[i] == "--api-dir" && i + 1 < args.Length) directory = args[++i];
                    else if (args[i] == "--diagnose") diagnose = true;
                    else if (args[i] == "--self-test") selfTest = true;
                    else if (args[i] == "--self-test-directory" && i + 1 < args.Length) selfTestDirectory = args[++i];
                    else throw new ArgumentException("Use --pipe NAME [--api-dir DIR], or --diagnose.");
                }
                if (selfTest) return SimulatorSelfTests.Run(selfTestDirectory);
                if (!diagnose && string.IsNullOrWhiteSpace(pipe)) throw new ArgumentException("--pipe is required.");
                string dll = Path.Combine(Path.GetFullPath(directory), "Siemens.Simatic.Simulation.Runtime.Api.x64.dll");
                if (!File.Exists(dll) || AssemblyName.GetAssemblyName(dll).Version.Major != 8)
                    throw new InvalidOperationException("Installed PLCSIM Advanced V8 x64 API was not found.");
                AppDomain.CurrentDomain.AssemblyResolve += (sender, evt) =>
                    new AssemblyName(evt.Name).Name == "Siemens.Simatic.Simulation.Runtime.Api.x64" ? Assembly.LoadFrom(dll) : null;
                return Run(pipe, diagnose);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string pipeName, bool diagnose)
        {
            using (var session = new SimulatorSession())
            {
                if (diagnose) { Console.WriteLine(session.Execute("sim_status", new JObject()).ToString(Formatting.Indented)); return 0; }
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(30000);
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true))
                    using (var writer = new TiaMcp.Transport.WorkerPipeWriter(pipe))
                    {
                        string line;
                        while ((line = ReadLine(reader)) != null)
                        {
                            string id = null;
                            JObject response;
                            try
                            {
                                using (var json = new JsonTextReader(new StringReader(line)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
                                {
                                    var request = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                                    if (json.Read()) throw new SimulatorException("InvalidRequest", "One JSON object is required.");
                                    id = request.Value<string>("id");
                                    if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || request.Value<int?>("protocolVersion") != 1)
                                        throw new SimulatorException("InvalidRequest", "Invalid request id or protocol version.");
                                    var arguments = request["arguments"] as JObject;
                                    if (arguments == null) throw new SimulatorException("InvalidRequest", "arguments must be an object.");
                                    response = new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = true,
                                        ["result"] = session.Execute(request.Value<string>("operation"), arguments) };
                                }
                            }
                            catch (Exception ex) { response = Error(id, ex); }
                            string output = response.ToString(Formatting.None);
                            if (Encoding.UTF8.GetByteCount(output) > 16 * 1024 * 1024)
                                output = Error(id, new SimulatorException("ResponseTooLarge", "Simulator response exceeds 16 MiB.")).ToString(Formatting.None);
                            writer.WriteLine(output);
                        }
                        writer.PeerEofReceived = true;
                    }
                }
            }
            return 0;
        }

        internal static string ReadLine(TextReader reader)
        {
            const int maximum = 4 * 1024 * 1024;
            var text = new StringBuilder();
            int c;
            while ((c = reader.Read()) != -1)
            {
                if (c == '\n')
                {
                    string value = text.ToString();
                    if (Encoding.UTF8.GetByteCount(value) > maximum) throw new InvalidDataException("Request exceeds 4 MiB.");
                    return value;
                }
                if (text.Length >= maximum) throw new InvalidDataException("Request exceeds 4 MiB.");
                text.Append((char)c);
            }
            if (text.Length != 0) throw new EndOfStreamException("Incomplete request; no operation executed.");
            return null;
        }

        private static JObject Error(string id, Exception ex)
        {
            var sim = ex as SimulatorException;
            var runtime = ex as Siemens.Simatic.Simulation.Runtime.SimulationRuntimeException;
            var initialization = ex as Siemens.Simatic.Simulation.Runtime.SimulationInitializationException;
            string code = sim?.Code ?? runtime?.RuntimeErrorCode.ToString() ?? initialization?.RuntimeErrorCode.ToString()
                ?? (ex is ArgumentException || ex is JsonException || ex is FormatException || ex is OverflowException ? "InvalidRequest" : "SimulatorError");
            return new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = false,
                ["error"] = new JObject { ["code"] = code, ["message"] = ex.Message,
                    ["details"] = sim?.Details ?? new JObject { ["exceptionType"] = ex.GetType().FullName } } };
        }
    }

    internal sealed class SimulatorException : Exception
    {
        internal string Code { get; }
        internal JObject Details { get; }
        internal SimulatorException(string code, string message, JObject details = null) : base(message) { Code = code; Details = details; }
    }
}
