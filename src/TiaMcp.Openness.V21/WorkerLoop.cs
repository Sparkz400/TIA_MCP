#nullable disable
using System;
using System.Collections.Generic;
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
                    int lightSinceCollect = 0;
                    while (true)
                    {
                        string line = ReadBoundedLine(reader);
                        if (line == null) { writer.PeerEofReceived = true; break; }
                        JObject response;
                        string id = null, operation = null;
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
                                operation = request.Value<string>("operation");
                                if (string.IsNullOrWhiteSpace(operation)) throw new BridgeException("InvalidRequest", "operation is required.");
                                var arguments = request["arguments"] as JObject;
                                if (request["arguments"] != null && arguments == null) throw new BridgeException("InvalidRequest", "arguments must be an object.");
                                response = new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = true, ["result"] = execute(session, operation, arguments ?? new JObject()) };
                            }
                        }
                        catch (Exception exception) { id = id ?? RecoverRequestId(line); response = Error(id, exception); }
                        writer.WriteLine(Frame(id, operation, response));
                        // TIA caps live Openness object instances per Portal (500,000); proxies release them only when
                        // finalized, so large library walks accumulated until EngineeringOutOfMemoryException (scale run).
                        // Only the calls that cannot have created many proxies skip it, and never more than a few in a row.
                        if (ShouldCollect(operation, ref lightSinceCollect)) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
                    }
                }
            }
            return 0;
        }

        // Status-style calls read a few objects at most; collecting after each of them only delays the next real request.
        private static readonly HashSet<string> LightOperations = new HashSet<string>(StringComparer.Ordinal) { "bridge_info", "session_status", "discover", "change_status" };
        internal const int LightOperationsPerCollect = 10;

        // Collect after every operation except a run of light ones; the counter makes sure a collect still happens regularly.
        internal static bool ShouldCollect(string operation, ref int lightSinceCollect)
        {
            if (operation != null && LightOperations.Contains(operation) && lightSinceCollect < LightOperationsPerCollect) { lightSinceCollect++; return false; }
            lightSinceCollect = 0;
            return true;
        }

        // Operations that commit before they answer: their full view can be large, and an error would hide a finished change.
        private static readonly HashSet<string> WriteOperations = new HashSet<string>(StringComparer.Ordinal) { "change_apply", "change_restore", "change_prepare", "mutation_dispatch" };
        private const int MaxSummaryString = 1024;
        private const int MaxSummaryValueBytes = 64 * 1024;

        /// <summary>The response line. A read that does not fit is refused so the caller asks for less; a write that already
        /// happened is answered with a summary of its result instead, never with an error.</summary>
        // A request that cannot be loaded (a repeated name, too deep, a malformed number) fails before its id is read.
        // Answered without the id, the Host could only fault the bridge and end this worker's session; the Host writes
        // the id second in every frame, so it is read from there and the refusal stays an ordinary InvalidRequest.
        internal static string RecoverRequestId(string line)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line ?? "", "^[{]\"protocolVersion\":1,\"id\":\"([0-9A-Za-z_.-]{1,128})\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        internal static string Frame(string id, string operation, JObject response)
        {
            string output = response.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(output) <= MaxResponseBytes) return output;
            if (operation != null && WriteOperations.Contains(operation) && response.Value<bool?>("success") == true && response["result"] is JObject result)
            {
                output = new JObject { ["protocolVersion"] = 1, ["id"] = id, ["success"] = true, ["result"] = Summarize(result) }.ToString(Formatting.None);
                if (Encoding.UTF8.GetByteCount(output) <= MaxResponseBytes) return output;
            }
            return Error(id, new BridgeException("ResponseTooLarge", "Response exceeds 16 MiB. Request fewer items. Export artifacts remain in the requested artifact directory.")).ToString(Formatting.None);
        }

        // Scalars (long strings cut) and small structures stay: identity, state, hashes, stages and failures. Payload echoes,
        // compiler messages and readbacks are named in omitted; the change journal holds them.
        private static JObject Summarize(JObject result)
        {
            var summary = new JObject();
            var omitted = new JArray();
            foreach (var property in result.Properties())
            {
                var value = property.Value;
                if (value.Type == JTokenType.String && ((string)value).Length > MaxSummaryString) value = new JValue(((string)value).Substring(0, MaxSummaryString));
                if (value is JContainer && Encoding.UTF8.GetByteCount(value.ToString(Formatting.None)) > MaxSummaryValueBytes) { omitted.Add(property.Name); continue; }
                summary[property.Name] = value.DeepClone();
            }
            summary["omitted"] = omitted;
            summary["responseDetail"] = "oversizeSummary";
            summary["fullEvidence"] = "The full result exceeded 16 MiB and the operation had already completed. The change journal (journalPath) and change_status hold the complete evidence.";
            return summary;
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
