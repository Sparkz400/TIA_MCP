#nullable disable
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.Enums;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;

namespace TiaMcp.Openness.V21
{
    // to_connect's telegram form: a SINAMICS drive object's Startdrive telegram through the Startdrive SDR connection interfaces.
    // Startdrive types stay in this NoInlining method, reached through Startdrive(...), so a missing assembly fails only this call.
    internal sealed partial class OpennessSession
    {
        private void ConnectTechnologyTelegram(TechnologicalInstanceDB technology, object slot, Project project, JObject payload, ConnectOption? option, bool apply)
            => Startdrive(() => { ConnectTechnologyTelegramCore(technology, slot, project, payload, option, apply); return true; });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ConnectTechnologyTelegramCore(TechnologicalInstanceDB technology, object slot, Project project, JObject payload, ConnectOption? option, bool apply)
        {
            string role = ChangeText(payload, "role", 32), typeName = ChangeText(payload, "telegramType", 32);
            if (!Enum.TryParse(typeName, false, out TelegramType type) || !Enum.IsDefined(typeof(TelegramType), type) || type.ToString() != typeName)
                throw new BridgeException("InvalidRequest", "telegramType must be a Startdrive TelegramType name as drive_observe reports it, such as MainTelegram.");
            var telegram = ChangeDriveObject(project, payload).Telegrams.Find(type)
                ?? throw new BridgeException("TelegramNotFound", "The selected drive object has no " + typeName + ".");
            object sdr;
            switch (role)
            {
                case "actor": sdr = technology.GetService<AxisHardwareConnectionSDRProvider>()?.ActorInterface; break;
                case "torque": sdr = technology.GetService<AxisHardwareConnectionSDRProvider>()?.TorqueInterface; break;
                case "sensor":
                {
                    int index = HardwareIndex(payload, "sensorIndex");
                    var sensors = technology.GetService<AxisHardwareConnectionSDRProvider>()?.SensorInterface;
                    sdr = sensors == null || index >= sensors.Count ? null : sensors[index];
                    break;
                }
                case "encoder": sdr = technology.GetService<EncoderHardwareConnectionSDRProvider>()?.SensorInterface; break;
                default: sdr = null; break;
            }
            if (sdr == null) throw new BridgeException("UnsupportedCapability", "The selected technology object exposes no Startdrive " + role + " telegram connection.");
            if (!apply) return;
            if (sdr is AxisEncoderHardwareConnectionSDRInterface axis) { if (option == null) axis.Connect(telegram); else axis.Connect(telegram, option.Value); }
            else { var torque = (TorqueHardwareConnectionSDRInterface)sdr; if (option == null) torque.Connect(telegram); else torque.Connect(telegram, option.Value); }
            // Read back through the TO's own slot: connected, at the telegram's input and output start addresses. The slot holds bit
            // addresses (native, 8 November 2026: telegram byte 256 is "input=2048"); Telegram.Addresses holds byte addresses.
            int Start(string io) => telegram.Addresses.Where(a => a.IoType.ToString() == io).Select(a => a.StartAddress).DefaultIfEmpty(-1).First();
            int input = Start("Input"), output = Start("Output");
            object observedInput = TechnologySlotValue(slot, "InputAddress"), observedOutput = TechnologySlotValue(slot, "OutputAddress");
            if (!TechnologySlotConnected(slot) || input >= 0 && !Equals(observedInput, input * 8) || output >= 0 && !Equals(observedOutput, output * 8)
                || option != null && !Equals(TechnologySlotValue(slot, "ConnectOption"), option.Value))
                throw new BridgeException("MutationVerificationFailed", "The connection readback does not report the drive telegram's addresses (expected bits " + input * 8 + "/" + output * 8
                    + ", observed " + (observedInput ?? "null") + "/" + (observedOutput ?? "null") + "); checkpoint recovery is required.");
        }
    }
}
