using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>The line-delimited JSON request loop (protocol version 1) shared by the V21 and V19 workers.</summary>
    internal static class WorkerLoop
    {
        internal const int MaxRequestBytes = 4 * 1024 * 1024;
        internal const int MaxResponseBytes = 16 * 1024 * 1024;

        // The session is created after the pipe connects and disposed before it closes, as each worker did before.
        internal static int Run<TSession>(string pipeName, Func<TSession> createSession, Func<TSession, string, JObject, JToken> execute) where TSession : IDisposable
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(30000);
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true))
                using (var writer = new TiaMcp.Transport.WorkerPipeWriter(pipe))
                using (var session = createSession())
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
                                response = new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = true, ["result"] = execute(session, operation, arguments ?? new JObject()) };
                            }
                        }
                        catch (Exception exception) { response = Error(id, exception); }
                        string output = response.ToString(Formatting.None);
                        if (Encoding.UTF8.GetByteCount(output) > MaxResponseBytes)
                            output = Error(id, new BridgeException("ResponseTooLarge", "Response exceeds 16 MiB. Request fewer items. Export artifacts remain in the requested artifact directory.")).ToString(Formatting.None);
                        writer.WriteLine(output);
                        // TIA caps live Openness object instances per Portal (500,000); proxies release them only when
                        // finalized, so large library walks accumulated until EngineeringOutOfMemoryException (scale run).
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    }
                }
            }
            return 0;
        }

        internal static string ReadBoundedLine(TextReader reader)
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

        internal static JObject Error(string id, Exception exception)
        {
            var bridge = exception as BridgeException;
            string code = bridge?.Code ?? (exception is JsonException || exception is ArgumentException || exception is FormatException ? "InvalidRequest" : "EngineeringError");
            // Matched by full name: the worker and V19 compile against different Siemens assemblies of the same public types.
            string type = exception.GetType().FullName;
            if (type == "Siemens.Engineering.EngineeringSecurityException") code = "AccessDenied";
            if (type == "Siemens.Engineering.LicenseNotFoundException") code = "LicenseUnavailable";
            return new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = false,
                ["error"] = new JObject { ["code"] = code, ["message"] = exception.Message, ["details"] = bridge?.Details ?? new JObject { ["exceptionType"] = exception.GetType().FullName } } };
        }
    }
}
