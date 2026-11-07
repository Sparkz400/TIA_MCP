using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Simatic.Simulation.Runtime;

namespace TiaMcp.Simulator.V8
{
    // Acyclic data records: answers a user program's RDREC/WRREC (SFB 52/53) on an owned instance as the addressed I/O module
    // would, from a reviewed response table. action "arm" subscribes OnDataRecordRead/OnDataRecordWrite and answers matching
    // requests with ReadRecordDone/WriteRecordDone while later tool calls run; requests without a row are logged and left
    // unanswered. action "collect" returns the log after an optional window and unsubscribes; release and shutdown unsubscribe.
    internal sealed partial class SimulatorSession
    {
        private sealed class RecordArm { internal readonly JArray Log = new JArray(); internal JArray Responses; internal Action Unsubscribe; internal string ArmedAtUtc; internal bool Truncated; }
        private const int RecordLogLimit = 256;

        private JObject Record(JObject args)
        {
            var selected = Owned(args);
            string action = args["action"]?.Type == JTokenType.String ? args.Value<string>("action") : null;
            if (action != "arm" && action != "collect") throw new SimulatorException("InvalidRequest", "action must be arm or collect.");
            var result = Envelope(selected);
            if (action == "collect")
            {
                var armed = selected.Records ?? throw new SimulatorException("SimulationRecordsInactive", "No data record responses are armed on this instance.");
                int window = OptionalInteger(args, "windowMs", 0, 0, 60000);
                if (window > 0) Thread.Sleep(window);
                StopRecords(selected);
                lock (armed.Log) result["requests"] = new JArray(armed.Log.Select(r => r.DeepClone()));
                result["armedAtUtc"] = armed.ArmedAtUtc;
                result["responses"] = armed.Responses;
                result["truncated"] = armed.Truncated;
                result["basis"] = "RDREC/WRREC requests the runtime raised between arm and collect, other tool calls included; answered only where a response row matched the hardware identifier and record index.";
                return result;
            }
            if (selected.Records != null) throw new SimulatorException("SimulationRecordsActive", "Data record responses are already armed on this instance; collect them first.");
            var responses = RecordResponses(args["responses"]);
            var arm = new RecordArm { Responses = responses, ArmedAtUtc = DateTime.UtcNow.ToString("O") };
            JObject Row(SDataRecordInfo info, string kind) => responses.OfType<JObject>().FirstOrDefault(r => r.Value<uint>("hardwareId") == info.HardwareId
                && r.Value<uint>("recordIndex") == info.RecordIdx && r[kind] != null);
            void Log(JObject entry)
            {
                entry["receivedAtUtc"] = DateTime.UtcNow.ToString("O");
                lock (arm.Log) { if (arm.Log.Count < RecordLogLimit) arm.Log.Add(entry); else arm.Truncated = true; }
            }
            Delegate_II_EREC_DT_SDRI read = (sender, code, time, info) =>
            {
                var entry = new JObject { ["kind"] = "read", ["hardwareId"] = info.HardwareId, ["recordIndex"] = info.RecordIdx, ["dataSize"] = info.DataSize, ["errorCode"] = code.ToString() };
                var row = Row(info, "read");
                try
                {
                    if (row != null)
                    {
                        var bytes = ((JArray)row["read"]["bytes"]).Select(b => (byte)b.Value<int>()).ToArray();
                        uint status = row["read"].Value<uint?>("status") ?? 0;
                        sender.ReadRecordDone(info, bytes, status);
                        entry["answered"] = true; entry["bytes"] = new JArray(bytes.Select(b => (int)b)); entry["status"] = status;
                    }
                    else entry["answered"] = false;
                }
                catch (Exception error) { entry["answered"] = false; entry["answerFailure"] = error.GetType().Name + ": " + error.Message; }
                Log(entry);
            };
            Delegate_II_EREC_DT_SDR write = (sender, code, time, record) =>
            {
                var entry = new JObject { ["kind"] = "write", ["hardwareId"] = record.Info.HardwareId, ["recordIndex"] = record.Info.RecordIdx, ["dataSize"] = record.Info.DataSize,
                    ["data"] = new JArray((record.Data ?? new byte[0]).Select(b => (int)b)), ["errorCode"] = code.ToString() };
                var row = Row(record.Info, "write");
                try
                {
                    if (row != null)
                    {
                        uint status = row["write"].Value<uint?>("status") ?? 0;
                        sender.WriteRecordDone(record.Info, status);
                        entry["answered"] = true; entry["status"] = status;
                    }
                    else entry["answered"] = false;
                }
                catch (Exception error) { entry["answered"] = false; entry["answerFailure"] = error.GetType().Name + ": " + error.Message; }
                Log(entry);
            };
            bool reads = false, writes = false;
            Action remove = () =>
            {
                if (reads) selected.Instance.OnDataRecordRead -= read;
                if (writes) selected.Instance.OnDataRecordWrite -= write;
            };
            try
            {
                selected.Instance.OnDataRecordRead += read; reads = true;
                selected.Instance.OnDataRecordWrite += write; writes = true;
            }
            catch { remove(); throw; }
            arm.Unsubscribe = remove;
            selected.Records = arm;
            result["armed"] = true;
            result["armedAtUtc"] = arm.ArmedAtUtc;
            result["responses"] = responses;
            result["basis"] = "Matching RDREC/WRREC requests are answered from now on, also during other tool calls, until sim_record collect, sim_release or the worker's shutdown.";
            return result;
        }

        private static void StopRecords(Selection selected)
        {
            var armed = selected.Records;
            if (armed == null) return;
            selected.Records = null;
            armed.Unsubscribe();
        }

        // 1..16 rows of {hardwareId 1..65535, recordIndex 0..65535, read?: {bytes: 1..1024 values 0..255, status?: uint32},
        // write?: {status?: uint32}}, at least one of read and write, each (hardwareId, recordIndex) once.
        internal static JArray RecordResponses(JToken value)
        {
            if (!(value is JArray rows) || rows.Count == 0 || rows.Count > 16)
                throw new SimulatorException("InvalidRequest", "responses must list 1 to 16 rows.");
            var result = new JArray();
            foreach (var token in rows)
            {
                if (!(token is JObject row) || row.Properties().Any(p => p.Name != "hardwareId" && p.Name != "recordIndex" && p.Name != "read" && p.Name != "write"))
                    throw new SimulatorException("InvalidRequest", "A response row has hardwareId, recordIndex, read and/or write only.");
                long id = Whole(row["hardwareId"], 1, 65535, "hardwareId"), index = Whole(row["recordIndex"], 0, 65535, "recordIndex");
                if (row["read"] == null && row["write"] == null) throw new SimulatorException("InvalidRequest", "A response row needs read or write.");
                var output = new JObject { ["hardwareId"] = id, ["recordIndex"] = index };
                if (row["read"] != null)
                {
                    if (!(row["read"] is JObject read) || read.Properties().Any(p => p.Name != "bytes" && p.Name != "status") || !(read["bytes"] is JArray bytes) || bytes.Count == 0 || bytes.Count > 1024)
                        throw new SimulatorException("InvalidRequest", "read needs bytes (1 to 1024 values) and an optional status.");
                    output["read"] = new JObject { ["bytes"] = new JArray(bytes.Select(b => Whole(b, 0, 255, "a read byte"))), ["status"] = read["status"] == null ? 0 : Whole(read["status"], 0, uint.MaxValue, "status") };
                }
                if (row["write"] != null)
                {
                    if (!(row["write"] is JObject write) || write.Properties().Any(p => p.Name != "status"))
                        throw new SimulatorException("InvalidRequest", "write has an optional status only.");
                    output["write"] = new JObject { ["status"] = write["status"] == null ? 0 : Whole(write["status"], 0, uint.MaxValue, "status") };
                }
                if (result.OfType<JObject>().Any(r => r.Value<long>("hardwareId") == id && r.Value<long>("recordIndex") == index))
                    throw new SimulatorException("InvalidRequest", "Each hardwareId and recordIndex appears once.");
                result.Add(output);
            }
            return result;
        }

        private static long Whole(JToken value, long minimum, long maximum, string name)
        {
            if (value == null || value.Type != JTokenType.Integer) throw new SimulatorException("InvalidRequest", name + " must be an integer.");
            long number = value.Value<long>();
            if (number < minimum || number > maximum) throw new SimulatorException("InvalidRequest", name + " must lie between " + minimum + " and " + maximum + ".");
            return number;
        }
    }
}
