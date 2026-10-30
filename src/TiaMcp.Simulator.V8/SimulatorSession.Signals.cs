using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Simatic.Simulation.Runtime;
using TiaMcp.Simulation;

namespace TiaMcp.Simulator.V8
{
    // Batch symbolic signals (ReadSignals/WriteSignals), I/Q/M process-image access and bounded operating-state and LED
    // observation. Reads and observation work on any selected instance; writes need an owned instance in its admitted mode.
    internal sealed partial class SimulatorSession
    {
        private static readonly Dictionary<string, EPrimitiveDataType> Primitives = new Dictionary<string, EPrimitiveDataType>(StringComparer.Ordinal)
        {
            ["bool"] = EPrimitiveDataType.Bool, ["int8"] = EPrimitiveDataType.Int8, ["uint8"] = EPrimitiveDataType.UInt8,
            ["int16"] = EPrimitiveDataType.Int16, ["uint16"] = EPrimitiveDataType.UInt16, ["int32"] = EPrimitiveDataType.Int32,
            ["uint32"] = EPrimitiveDataType.UInt32, ["int64"] = EPrimitiveDataType.Int64, ["uint64"] = EPrimitiveDataType.UInt64,
            ["float"] = EPrimitiveDataType.Float, ["double"] = EPrimitiveDataType.Double,
        };
        private const int MaxDisplayBytes = 4 * 1024 * 1024;

        private JObject SignalsRead(JObject args)
        {
            var selected = Get(args);
            var signals = BatchSignals(args["signals"], false);
            var batch = signals.Select(s => new SDataValueByName { Name = s.Symbol, DataValue = new SDataValue { Type = Primitives[s.Type] } }).ToArray();
            var result = Envelope(selected);
            // An item's code stays OK when the call throws before filling it; the whole batch is then bad.
            string failure = null;
            try { selected.Instance.ReadSignals(ref batch); }
            catch (Exception ex) { failure = ErrorCode(ex); result["batchError"] = ex.Message; }
            result["completedAtUtc"] = DateTime.UtcNow.ToString("O");
            result["values"] = new JArray(signals.Select((signal, i) =>
            {
                var value = new JObject { ["symbol"] = signal.Symbol, ["type"] = signal.Type };
                string code = batch[i].ErrorCode != ERuntimeErrorCode.OK ? batch[i].ErrorCode.ToString() : failure;
                JToken observed = code == null ? Unpack(batch[i].DataValue, signal.Type) : null;
                if (code == null && (signal.Type == "float" || signal.Type == "double") && (double.IsNaN((double)observed) || double.IsInfinity((double)observed)))
                    code = "NonFiniteValue";
                if (code == null) { value["value"] = observed; value["quality"] = "good"; }
                else { value["quality"] = "bad"; value["errorCode"] = code; }
                return value;
            }));
            result["basis"] = "One ReadSignals runtime call for all signals; values are not claimed to come from one scan.";
            return result;
        }

        private JObject SignalsWrite(JObject args)
        {
            var selected = Get(args);
            RequireOwned(selected);
            EnsureAdmitted(selected);
            var writes = BatchSignals(args["writes"], true);
            var batch = writes.Select(s => new SDataValueByName { Name = s.Symbol, DataValue = Pack(s) }).ToArray();
            string failure = null;
            try { selected.Instance.WriteSignals(ref batch); }
            catch (Exception ex) { failure = ErrorCode(ex); }
            string applied = DateTime.UtcNow.ToString("O");
            var items = new JArray(writes.Select((s, i) => new JObject { ["symbol"] = s.Symbol, ["type"] = s.Type, ["value"] = s.Value,
                ["result"] = batch[i].ErrorCode != ERuntimeErrorCode.OK ? batch[i].ErrorCode.ToString() : failure == null ? "OK" : "Unknown" }));
            if (failure != null || batch.Any(b => b.ErrorCode != ERuntimeErrorCode.OK))
                throw new SimulatorException("PartialSimulationWrite", "WriteSignals reported a failure; signals not reported failed may have been written.",
                    new JObject { ["writes"] = items, ["batchError"] = failure, ["retrySafe"] = false });
            var result = Envelope(selected);
            result["writes"] = items;
            result["appliedAtUtc"] = applied;
            result["note"] = "One WriteSignals runtime call. Values are assignments, not forces; program scans may immediately overwrite them.";
            return result;
        }

        private static List<Signal> BatchSignals(JToken input, bool writing)
        {
            var signals = Signals(input, writing);
            string refusal = SimulationRuntimeControl.SignalBatchRefusal(signals.Select(s => s.Symbol).ToList(), signals.Select(s => s.Type).ToList(), writing);
            if (refusal != null) throw new SimulatorException(refusal, "ReadSignals/WriteSignals carry bool, integer, float and double values (strings stay with sim_read/sim_write), symbols shorter than 300 characters and, for a write, distinct symbols.");
            return signals;
        }

        private static SDataValue Pack(Signal signal)
        {
            var value = new SDataValue { Type = Primitives[signal.Type] };
            var v = signal.Value;
            switch (signal.Type)
            {
                case "bool": value.Bool = v.Value<bool>(); break;
                case "int8": value.Int8 = v.Value<sbyte>(); break;
                case "uint8": value.UInt8 = v.Value<byte>(); break;
                case "int16": value.Int16 = v.Value<short>(); break;
                case "uint16": value.UInt16 = v.Value<ushort>(); break;
                case "int32": value.Int32 = v.Value<int>(); break;
                case "uint32": value.UInt32 = v.Value<uint>(); break;
                case "int64": value.Int64 = v.Value<long>(); break;
                case "uint64": value.UInt64 = (ulong)v; break;
                case "float": value.Float = (float)v; break;
                case "double": value.Double = (double)v; break;
            }
            return value;
        }

        private static JToken Unpack(SDataValue value, string type)
        {
            switch (type)
            {
                case "bool": return new JValue(value.Bool);
                case "int8": return new JValue(value.Int8);
                case "uint8": return new JValue(value.UInt8);
                case "int16": return new JValue(value.Int16);
                case "uint16": return new JValue(value.UInt16);
                case "int32": return new JValue(value.Int32);
                case "uint32": return new JValue(value.UInt32);
                case "int64": return new JValue(value.Int64);
                case "uint64": return new JValue(value.UInt64);
                case "float": return new JValue(value.Float);
                default: return new JValue(value.Double);
            }
        }

        private JObject AreaRead(JObject args)
        {
            var selected = Get(args);
            var plan = Control(() => SimulationRuntimeControl.AreaAccess(args, false));
            var area = Area(selected, plan);
            var result = Envelope(selected);
            result["access"] = plan;
            result["areaSize"] = area.AreaSize;
            uint offset = plan.Value<uint>("offset");
            if (plan["bit"] != null) result["value"] = area.ReadBit(offset, plan.Value<byte>("bit"));
            else result["bytes"] = new JArray(area.ReadBytes(offset, plan.Value<uint>("length")).Select(b => (int)b));
            result["completedAtUtc"] = DateTime.UtcNow.ToString("O");
            return result;
        }

        private JObject AreaWrite(JObject args)
        {
            var selected = Get(args);
            RequireOwned(selected);
            EnsureAdmitted(selected);
            var plan = Control(() => SimulationRuntimeControl.AreaAccess(args, true));
            var area = Area(selected, plan);
            uint offset = plan.Value<uint>("offset");
            var result = Envelope(selected);
            result["access"] = plan;
            if (plan["bit"] != null) area.WriteBit(offset, plan.Value<byte>("bit"), plan.Value<bool>("value"));
            else
            {
                uint written = area.WriteBytes(offset, plan["bytes"].Select(b => (byte)b).ToArray());
                result["bytesWritten"] = written;
                if (written != plan.Value<uint>("length"))
                    throw new SimulatorException("PartialSimulationWrite", "WriteBytes wrote " + written + " of " + plan.Value<uint>("length") + " bytes.", new JObject { ["access"] = plan, ["bytesWritten"] = written, ["retrySafe"] = false });
            }
            result["appliedAtUtc"] = DateTime.UtcNow.ToString("O");
            result["note"] = "An assignment to the process image, not a force. The program overwrites on its next scan whatever it writes itself.";
            return result;
        }

        private static IIOArea Area(Selection selected, JObject plan)
        {
            string name = plan.Value<string>("area");
            var area = name == "input" ? selected.Instance.InputArea : name == "output" ? selected.Instance.OutputArea : selected.Instance.MarkerArea;
            uint size = area.AreaSize;
            if (!SimulationRuntimeControl.AreaFits(plan, size))
                throw new SimulatorException("InvalidRequest", "The requested range ends beyond the " + size + "-byte " + name + " area. Nothing was read or written.");
            return area;
        }

        // Subscribes for the bounded window only and unsubscribes before returning; no thread or subscription outlives the call.
        private JObject Observe(JObject args)
        {
            var selected = Get(args);
            int window = OptionalInteger(args, "windowMs", 0, 0, 60000), maximum = OptionalInteger(args, "maxEvents", 64, 1, 256);
            bool display = args.Value<bool?>("includeDisplayData") ?? false;
            var result = Envelope(selected);
            result["before"] = ObservedState(selected);
            var events = new JArray();
            var gate = new object();
            bool open = true;
            void Add(JObject item)
            {
                item["receivedAtUtc"] = DateTime.UtcNow.ToString("O");
                lock (gate) { if (open && events.Count < maximum) { events.Add(item); Monitor.PulseAll(gate); } }
            }
            Delegate_II_EREC_DT_EOS_EOS state = (sender, code, time, previous, current) => Add(new JObject { ["kind"] = "operatingState",
                ["previous"] = previous.ToString(), ["current"] = current.ToString(), ["eventTime"] = time.ToString("O"), ["errorCode"] = code.ToString() });
            Delegate_II_EREC_DT_ELT_ELM led = (sender, code, time, type, mode) => Add(new JObject { ["kind"] = "led",
                ["led"] = type.ToString(), ["mode"] = mode.ToString(), ["eventTime"] = time.ToString("O"), ["errorCode"] = code.ToString() });
            var clock = Stopwatch.StartNew();
            if (window > 0)
            {
                bool states = false, leds = false;
                try
                {
                    selected.Instance.OnOperatingStateChanged += state; states = true;
                    selected.Instance.OnLedChanged += led; leds = true;
                    lock (gate)
                        while (events.Count < maximum && clock.ElapsedMilliseconds < window)
                            Monitor.Wait(gate, (int)Math.Max(1, window - clock.ElapsedMilliseconds));
                }
                finally
                {
                    if (states) selected.Instance.OnOperatingStateChanged -= state;
                    if (leds) selected.Instance.OnLedChanged -= led;
                    lock (gate) open = false;
                }
            }
            result["windowMs"] = window;
            result["elapsedMs"] = clock.ElapsedMilliseconds;
            result["events"] = events;
            result["truncated"] = events.Count >= maximum;
            var modes = new JObject();
            foreach (var item in events.Where(e => e.Value<string>("kind") == "led")) modes[item.Value<string>("led")] = item["mode"];
            result["ledModes"] = modes;
            result["after"] = ObservedState(selected);
            result["display"] = Display(selected, display);
            result["basis"] = "Operating-state and LED changes reported by the runtime during the window (OnOperatingStateChanged, OnLedChanged). V8 has no LED getter, so ledModes lists only LEDs that changed. This bridge runs one call at a time: changes made by other tool calls cannot fall inside the window.";
            return result;
        }

        private static JObject ObservedState(Selection selected)
        {
            var result = new JObject { ["operatingState"] = selected.Instance.OperatingState.ToString(), ["observedAtUtc"] = DateTime.UtcNow.ToString("O") };
            // An instance that is off may not report an operating mode.
            try { result["operatingMode"] = selected.Instance.OperatingMode.ToString(); }
            catch (Exception ex) { result["operatingMode"] = null; result["operatingModeErrorCode"] = ErrorCode(ex); }
            return result;
        }

        private static JObject Display(Selection selected, bool include)
        {
            var result = new JObject();
            try
            {
                bool available = selected.Instance.HasDisplay();
                result["hasDisplay"] = available;
                if (!available) return result;
                var data = selected.Instance.GetDisplayData();
                var bytes = data.displayData ?? new byte[0];
                int length = (int)Math.Min(data.dataLength, (uint)bytes.Length);
                result["dataLength"] = data.dataLength;
                using (var sha = SHA256.Create()) result["sha256"] = BitConverter.ToString(sha.ComputeHash(bytes, 0, length)).Replace("-", "").ToLowerInvariant();
                if (include && length <= MaxDisplayBytes) result["data"] = Convert.ToBase64String(bytes, 0, length);
                else if (include) result["dataOmitted"] = "Display data exceeds " + MaxDisplayBytes + " bytes.";
            }
            catch (Exception ex) { result["errorCode"] = ErrorCode(ex); result["error"] = ex.Message; }
            return result;
        }
    }
}
