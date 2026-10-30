#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private void PrepareTechnologyConnection(EngineeringChange change, Project project, PlcSelection plc, TechnologyEntry entry, JObject preview)
        {
            var slot = TechnologyConnectionSlot(entry.Item, change.Payload);
            if (change.Kind == "to_connect") ConnectTechnologySlot(slot, project, plc, change.Payload, false);
            else if (!TechnologySlotConnected(slot)) throw new BridgeException("NotConnected", "The selected technology connection is not connected.");
            var warnings = new JArray();
            var rows = TechnologyConnections(entry.Item, new ScanBudget(500), warnings, entry.Path, out var coverage, new TechnologyReferenceReader(plc.Project, plc.Path));
            preview["connectionsBefore"] = new JObject { ["rows"] = rows, ["coverage"] = coverage, ["warnings"] = warnings };
        }

        private void ApplyTechnologyConnection(EngineeringChange change, Project project)
        {
            var plc = SelectPlc(change.Selector);
            var slot = TechnologyConnectionSlot(RequiredTechnologyObject(plc, change.Payload), change.Payload);
            if (change.Kind == "to_connect") { ConnectTechnologySlot(slot, project, plc, change.Payload, true); return; }
            if (slot is AxisEncoderHardwareConnectionInterface axis) axis.Disconnect();
            else if (slot is TorqueHardwareConnectionInterface torque) torque.Disconnect();
            else if (slot is MeasuringInputHardwareConnectionProvider measuring) measuring.Disconnect();
            else ((OutputCamHardwareConnectionProvider)slot).Disconnect();
            if (TechnologySlotConnected(slot)) throw new BridgeException("MutationVerificationFailed", "The technology connection still reports connected; checkpoint recovery is required.");
        }

        // Role names match the technology_read hardwareConnections rows.
        private static object TechnologyConnectionSlot(TechnologicalInstanceDB technology, JObject payload)
        {
            string role = ChangeText(payload, "role", 32);
            if (role != "sensor" && payload["sensorIndex"] != null) throw new BridgeException("InvalidRequest", "sensorIndex applies only to role sensor.");
            object slot;
            switch (role)
            {
                case "actor": slot = technology.GetService<AxisHardwareConnectionProvider>()?.ActorInterface; break;
                case "torque": slot = technology.GetService<AxisHardwareConnectionProvider>()?.TorqueInterface; break;
                case "sensor":
                {
                    int index = HardwareIndex(payload, "sensorIndex");
                    var sensors = technology.GetService<AxisHardwareConnectionProvider>()?.SensorInterface;
                    slot = sensors == null || index >= sensors.Count ? null : sensors[index];
                    break;
                }
                case "encoder": slot = technology.GetService<EncoderHardwareConnectionProvider>()?.SensorInterface; break;
                case "measuringInput": slot = technology.GetService<MeasuringInputHardwareConnectionProvider>(); break;
                case "outputCam": slot = technology.GetService<OutputCamHardwareConnectionProvider>(); break;
                default: throw new BridgeException("InvalidRequest", "role must be actor, sensor, torque, encoder, measuringInput or outputCam.");
            }
            return slot ?? throw new BridgeException("UnsupportedCapability", "The selected technology object exposes no " + role + " hardware connection.");
        }

        // Resolves the exact target and the matching V21 Connect overload; apply=false only validates the pair.
        private void ConnectTechnologySlot(object slot, Project project, PlcSelection plc, JObject payload, bool apply)
        {
            string form = TechnologyTargetForm(ChangeText(payload, "role", 32), payload);
            object target = form == "tag" ? TechnologyConnectionTag(plc, payload) : form == "channel" ? HardwareChannel(project, payload)
                : form == "hardware" || form == "channelIndex" ? ChangeHardware(project, payload).Item : null;
            DeviceItem input = form == "modules" ? ChangeHardware(project, payload, "inputHardwarePath").Item : null;
            DeviceItem output = form == "modules" ? ChangeHardware(project, payload, "outputHardwarePath").Item : null;
            string member = form == "dbMember" ? ChangeText(payload, "dbMemberPath", 1024) : null;
            int channelIndex = form == "channelIndex" ? HardwareIndex(payload, "channelIndex") : -1;
            int inputAddress = payload["inputAddress"] != null ? HardwareIndex(payload, "inputAddress") : -1, outputAddress = payload["outputAddress"] != null ? HardwareIndex(payload, "outputAddress") : -1;
            string optionText = payload["connectOption"] == null ? "Default" : ChangeText(payload, "connectOption", 32);
            if (optionText != "Default" && optionText != "AllowAllModules") throw new BridgeException("InvalidRequest", "connectOption must be Default or AllowAllModules.");
            var option = (ConnectOption)Enum.Parse(typeof(ConnectOption), optionText);
            Action connect;
            switch (slot)
            {
                case AxisEncoderHardwareConnectionInterface s:
                    connect = form == "channel" ? () => s.Connect((Channel)target) : form == "tag" ? () => s.Connect((PlcTag)target) : form == "hardware" ? () => s.Connect((DeviceItem)target)
                        : form == "modules" ? () => s.Connect(input, output, option) : form == "dbMember" ? () => s.Connect(member) : (Action)(() => s.Connect(inputAddress, outputAddress, option));
                    break;
                case TorqueHardwareConnectionInterface s:
                    connect = form == "hardware" ? () => s.Connect((DeviceItem)target) : form == "modules" ? () => s.Connect(input, output, option)
                        : form == "dbMember" ? () => s.Connect(member) : (Action)(() => s.Connect(inputAddress, outputAddress, option));
                    break;
                case MeasuringInputHardwareConnectionProvider s:
                    connect = form == "channel" ? () => s.Connect((Channel)target) : form == "channelIndex" ? () => s.Connect((DeviceItem)target, channelIndex) : (Action)(() => s.Connect(inputAddress));
                    break;
                default:
                    var cam = (OutputCamHardwareConnectionProvider)slot;
                    connect = form == "channel" ? () => cam.Connect((Channel)target) : form == "tag" ? () => cam.Connect((PlcTag)target) : (Action)(() => cam.Connect(outputAddress));
                    break;
            }
            if (!apply) return;
            connect();
            Func<string, object> read = field => TechnologySlotValue(slot, field);
            // TIA may quote DB and member names in its readback.
            bool reported = form == "modules" ? Equals(read("InputModule"), input) && Equals(read("OutputModule"), output)
                : form == "dbMember" ? string.Equals((read("PathToDBMember") as string)?.Replace("\"", ""), member.Replace("\"", ""), StringComparison.Ordinal)
                : form == "addresses" ? (inputAddress < 0 || Equals(read("InputAddress"), inputAddress)) && (outputAddress < 0 || Equals(read("OutputAddress"), outputAddress))
                : TechnologySlotTargets(slot).Any(linked => Equals(linked, target));
            if (!TechnologySlotConnected(slot) || !reported || form == "channelIndex" && ((MeasuringInputHardwareConnectionProvider)slot).ChannelIndex != channelIndex
                || payload["connectOption"] != null && !Equals(read("ConnectOption"), option))
                throw new BridgeException("MutationVerificationFailed", "The connection readback does not report the requested target; checkpoint recovery is required.");
        }

        // A property that does not apply to this connection type throws; it reads as null.
        private static object TechnologySlotValue(object slot, string field)
        {
            try
            {
                if (slot is AxisEncoderHardwareConnectionInterface axis)
                    return field == "InputModule" ? axis.InputModule : field == "OutputModule" ? axis.OutputModule : field == "InputAddress" ? axis.InputAddress
                        : field == "OutputAddress" ? axis.OutputAddress : field == "PathToDBMember" ? axis.PathToDBMember : (object)axis.ConnectOption;
                if (slot is TorqueHardwareConnectionInterface torque)
                    return field == "InputModule" ? torque.InputModule : field == "OutputModule" ? torque.OutputModule : field == "InputAddress" ? torque.InputAddress
                        : field == "OutputAddress" ? torque.OutputAddress : field == "PathToDBMember" ? torque.PathToDBMember : (object)torque.ConnectOption;
                if (slot is MeasuringInputHardwareConnectionProvider measuring && field == "InputAddress") return measuring.InputAddress;
                if (slot is OutputCamHardwareConnectionProvider cam && field == "OutputAddress") return cam.OutputAddress;
            }
            catch (Exception) { }
            return null;
        }

        private static PlcTag TechnologyConnectionTag(PlcSelection plc, JObject payload)
        {
            string name = ChangeText(payload, "tagName", 128);
            var budget = new ScanBudget(50000);
            var matches = TagTables(plc.Software.TagTableGroup, plc.Path + "/tags", budget).SelectMany(table => table.Item.Tags.Where(item => item.Name == name)).Take(2).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish a complete tag selection within the scan bound.");
            if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "TagNotFound" : "AmbiguousTarget", "tagName must select exactly one tag in the selected PLC's tag tables.");
            return matches[0];
        }

        private static bool TechnologySlotConnected(object slot)
            => slot is AxisEncoderHardwareConnectionInterface axis ? axis.IsConnected : slot is TorqueHardwareConnectionInterface torque ? torque.IsConnected
                : slot is MeasuringInputHardwareConnectionProvider measuring ? measuring.IsConnected : ((OutputCamHardwareConnectionProvider)slot).IsConnected;

        private static IEnumerable<object> TechnologySlotTargets(object slot)
        {
            var reads = slot is AxisEncoderHardwareConnectionInterface axis ? new Func<object>[] { () => axis.Channel, () => axis.InputModule, () => axis.OutputModule, () => axis.InputOutputModule, () => axis.OutputTag }
                : slot is TorqueHardwareConnectionInterface torque ? new Func<object>[] { () => torque.InputModule, () => torque.OutputModule, () => torque.InputOutputModule }
                : slot is MeasuringInputHardwareConnectionProvider measuring ? new Func<object>[] { () => measuring.Channel, () => measuring.InputModule }
                : slot is OutputCamHardwareConnectionProvider output ? new Func<object>[] { () => output.Channel, () => output.OutputTag } : new Func<object>[0];
            foreach (var read in reads)
            {
                object value;
                try { value = read(); } catch (Exception) { continue; } // a property that does not apply to this connection type throws
                yield return value;
            }
        }

        private void PrepareTechnologyChildCreate(EngineeringChange change, JArray effects)
        {
            var payload = change.Payload;
            var owner = TechnologyChildOwner(SelectPlc(change.Selector), payload);
            ChangeText(payload, "typeIdentifier", 128);
            if (!Version.TryParse(ChangeText(payload, "version", 32), out _)) throw new BridgeException("InvalidRequest", "version must be a dotted numeric version supported by this CPU.");
            effects.Add(new JObject { ["createTechnologyObject"] = payload["name"], ["ownerPath"] = owner.Path, ["composition"] = payload["composition"],
                ["typeIdentifier"] = payload["typeIdentifier"], ["version"] = payload["version"] });
        }

        private void ApplyTechnologyChildCreate(EngineeringChange change)
        {
            var payload = change.Payload;
            var plc = SelectPlc(change.Selector);
            var owner = TechnologyChildOwner(plc, payload);
            string name = payload.Value<string>("name"), composition = payload.Value<string>("composition");
            var container = owner.Item.GetService<OutputCamMeasuringInputContainer>();
            (composition == "OutputCams" ? container.OutputCams : container.MeasuringInputs).Create(name, payload.Value<string>("typeIdentifier"), new Version(payload.Value<string>("version")));
            string path = owner.Path + "/children/" + composition + "/" + Uri.EscapeDataString(name);
            if (FindTechnologyEntry(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology", new JObject { ["technologyPath"] = path }, false) == null)
                throw new BridgeException("MutationVerificationFailed", "The created child is not observed at " + path + "; checkpoint recovery is required.");
        }

        // Checked at prepare and again immediately before creation.
        private static TechnologyEntry TechnologyChildOwner(PlcSelection plc, JObject payload)
        {
            string name = ChangeText(payload, "name", 128), composition = ChangeText(payload, "composition", 32);
            if (composition != "OutputCams" && composition != "MeasuringInputs") throw new BridgeException("InvalidRequest", "composition must be OutputCams or MeasuringInputs.");
            var group = plc.Software.TechnologicalObjectGroup;
            string root = plc.Path + "/technology";
            if (FindTechnologyEntry(group, root, new JObject { ["name"] = name }, false) != null) throw new BridgeException("ObjectExists", "Technology object already exists.");
            var owner = FindTechnologyEntry(group, root, new JObject { ["technologyPath"] = ChangeText(payload, "ownerPath", 4096) }, true);
            if (owner.Item.GetService<OutputCamMeasuringInputContainer>() == null)
                throw new BridgeException("UnsupportedCapability", "The owner technology object exposes no OutputCamMeasuringInputContainer.");
            return owner;
        }
    }
}
