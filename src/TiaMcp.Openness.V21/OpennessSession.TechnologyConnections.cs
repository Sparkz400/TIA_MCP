#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static JArray TechnologyConnections(TechnologicalInstanceDB technology, ScanBudget budget, JArray warnings, string path, out JObject coverage, TechnologyReferenceReader references = null)
        {
            InspectionValueRenderer render = references == null ? (InspectionValueRenderer)null : references.Render;
            var rows = new JArray();
            var services = new JArray();
            string root = path + "/hardwareConnections";
            CaptureTechnologyService(() => technology.GetService<AxisHardwareConnectionProvider>(), "axis", services, rows, budget, warnings, root, (axis, service) =>
            {
                CaptureTechnologyRow(rows, () => axis.ActorInterface, "actor", "axis", root + "/axis/actor", budget, warnings,
                    (connection, row) => ReadTechnologyInterface(connection, row, budget, warnings, render));
                CaptureTechnologySensors(() => axis.SensorInterface, service, rows, budget, warnings, root + "/axis/sensors", render);
                CaptureTechnologyRow(rows, () => axis.TorqueInterface, "torque", "axis", root + "/axis/torque", budget, warnings,
                    (connection, row) => ReadTechnologyAttributes(connection, row, budget, warnings, render));
            });
            CaptureTechnologyService(() => technology.GetService<EncoderHardwareConnectionProvider>(), "encoder", services, rows, budget, warnings, root, (encoder, service) =>
                CaptureTechnologyRow(rows, () => encoder.SensorInterface, "encoder", "encoder", root + "/encoder", budget, warnings,
                    (connection, row) => ReadTechnologyInterface(connection, row, budget, warnings, render)));
            CaptureTechnologyService(() => technology.GetService<MeasuringInputHardwareConnectionProvider>(), "measuringInput", services, rows, budget, warnings, root, (measuring, service) =>
                CaptureTechnologyRow(rows, () => measuring, "measuringInput", "measuringInput", root + "/measuringInput", budget, warnings, (connection, row) =>
                {
                    ReadTechnologyConnectionField(row, "isConnected", () => connection.IsConnected, budget, warnings, render);
                    ReadTechnologyConnectionField(row, "inputAddress", () => connection.InputAddress, budget, warnings, render);
                    ReadTechnologyConnectionField(row, "inputModule", () => connection.InputModule, budget, warnings, render);
                    ReadTechnologyConnectionField(row, "channel", () => connection.Channel, budget, warnings, render);
                    ReadTechnologyConnectionField(row, "channelIndex", () => connection.ChannelIndex, budget, warnings, render);
                }));
            CaptureTechnologyService(() => technology.GetService<OutputCamHardwareConnectionProvider>(), "outputCam", services, rows, budget, warnings, root, (output, service) =>
                CaptureTechnologyRow(rows, () => output, "outputCam", "outputCam", root + "/outputCam", budget, warnings, (connection, row) =>
                {
                    ReadTechnologyConnectionField(row, "isConnected", () => connection.IsConnected, budget, warnings, render);
                    ReadTechnologyConnectionField(row, "outputAddress", () => connection.OutputAddress, budget, warnings, render);
                    ReadTechnologyConnectionField(row, "outputTag", () => connection.OutputTag, budget, warnings, render);
                    ReadTechnologyConnectionField(row, "channel", () => connection.Channel, budget, warnings, render);
                }));
            coverage = new JObject { ["schemaVersion"] = 1, ["technologyPath"] = path, ["complete"] = false,
                ["scope"] = "Observed axis, encoder, measuring-input and output-cam connection services only; notExposed records a null service or slot, not target capability or physical absence.",
                ["scopeComplete"] = services.All(service => service.Value<bool>("scopeComplete")),
                ["truncated"] = budget.Truncated, ["services"] = services, ["referenceCoverage"] = references?.Coverage() };
            return rows;
        }

        // The V21 Connect overloads per slot type (PublicAPI V21, SW.TechnologicalObjects.Motion); actor, sensor and encoder share one interface type.
        private static readonly Dictionary<string, string[]> TechnologyConnectForms = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["actor"] = new[] { "hardware", "channel", "tag", "modules", "dbMember", "addresses" },
            ["torque"] = new[] { "hardware", "modules", "dbMember", "addresses" },
            ["measuringInput"] = new[] { "channel", "channelIndex", "addresses" },
            ["outputCam"] = new[] { "channel", "tag", "addresses" },
        };

        /// <summary>Validates the payload's one target and returns its form; refuses a role without a matching V21 Connect overload.</summary>
        internal static string TechnologyTargetForm(string role, JObject payload)
        {
            string family = role == "sensor" || role == "encoder" ? "actor" : role;
            if (!TechnologyConnectForms.TryGetValue(family, out var supported))
                throw new BridgeException("InvalidRequest", "role must be actor, sensor, torque, encoder, measuringInput or outputCam.");
            bool channel = new[] { "channelNumber", "channelType", "channelIoType" }.Any(field => payload[field] != null), index = payload["channelIndex"] != null;
            var forms = new[]
            {
                payload["hardwarePath"] != null ? channel ? "channel" : index ? "channelIndex" : "hardware" : null,
                payload["tagName"] != null ? "tag" : null,
                payload["inputHardwarePath"] != null || payload["outputHardwarePath"] != null ? "modules" : null,
                payload["dbMemberPath"] != null ? "dbMember" : null,
                payload["inputAddress"] != null || payload["outputAddress"] != null ? "addresses" : null,
            }.Where(form => form != null).ToArray();
            // Any channel field selects the channel overload, so a partial selector never falls back to the whole module.
            if (forms.Length != 1 || channel && index || (channel || index) && forms[0] != "channel" && forms[0] != "channelIndex"
                || forms[0] == "modules" && (payload["inputHardwarePath"] == null || payload["outputHardwarePath"] == null))
                throw new BridgeException("InvalidRequest", "Select exactly one target: hardwarePath, hardwarePath with channelNumber/channelType/channelIoType, hardwarePath with channelIndex, tagName, "
                    + "inputHardwarePath with outputHardwarePath, dbMemberPath, or inputAddress/outputAddress.");
            string form = forms[0];
            if (!supported.Contains(form)) throw new BridgeException("UnsupportedTarget", "The " + role + " role has no V21 Connect overload for a " + form + " target.");
            // Axis, encoder and torque address overloads take both addresses; a measuring input takes an input and an output cam an output address.
            if (form == "addresses" && ((payload["inputAddress"] != null) != (family != "outputCam") || (payload["outputAddress"] != null) != (family != "measuringInput")))
                throw new BridgeException("InvalidRequest", family == "measuringInput" ? "A measuring input connects to inputAddress only." : family == "outputCam" ? "An output cam connects to outputAddress only." : "The " + role + " role connects to inputAddress and outputAddress together.");
            if (payload["connectOption"] != null && (family != "actor" && family != "torque" || form != "modules" && form != "addresses"))
                throw new BridgeException("UnsupportedTarget", "connectOption exists only on the V21 two-module and address overloads of actor, sensor, encoder and torque connections.");
            return form;
        }

        private static void ConnectionWarning(JArray warnings, string path, string status)
            => warnings.Add(new JObject { ["path"] = path, ["status"] = status,
                ["message"] = "Connection observation is unvisited, unresolved or display-only; native exception values are omitted." });

        private static void CaptureTechnologyService<T>(Func<T> read, string name, JArray services, JArray rows, ScanBudget budget, JArray warnings,
            string root, Action<T, JObject> capture) where T : class
        {
            var observation = new JObject { ["service"] = name, ["status"] = "notVisited", ["scopeComplete"] = false, ["rowsCaptured"] = 0 };
            services.Add(observation);
            if (!budget.Take()) { ConnectionWarning(warnings, root + "/" + name, "notVisited"); return; }
            int start = rows.Count, warningStart = warnings.Count;
            try
            {
                T service = read();
                if (service == null) { observation["status"] = "notExposed"; observation["scopeComplete"] = true; return; }
                capture(service, observation);
                bool complete = !budget.Truncated && warnings.Count == warningStart;
                observation["status"] = complete ? "observed" : "incomplete";
                observation["scopeComplete"] = complete;
            }
            catch (Exception)
            {
                observation["status"] = "readError";
                ConnectionWarning(warnings, root + "/" + name, "readError");
            }
            finally { observation["rowsCaptured"] = rows.Count - start; }
        }

        private static void CaptureTechnologySensors(Func<IEnumerable<AxisEncoderHardwareConnectionInterface>> read, JObject service, JArray rows,
            ScanBudget budget, JArray warnings, string path, InspectionValueRenderer render)
        {
            service["sensorCollectionStatus"] = "notVisited";
            if (!budget.Take()) { ConnectionWarning(warnings, path, "notVisited"); return; }
            try
            {
                var sensors = read();
                if (sensors == null) { service["sensorCollectionStatus"] = "missing"; ConnectionWarning(warnings, path, "missing"); return; }
                int index = 0;
                foreach (var sensor in sensors)
                {
                    // Null entries consume the same budget as real interfaces; one lookahead may detect exhaustion.
                    if (!CaptureTechnologyRow(rows, () => sensor, "sensor", "axis", path + "/" + index, budget, warnings,
                        (connection, row) => ReadTechnologyInterface(connection, row, budget, warnings, render), false))
                    { service["sensorCollectionStatus"] = "truncated"; return; }
                    index++;
                }
                service["sensorCollectionStatus"] = "observed";
            }
            catch (Exception)
            {
                service["sensorCollectionStatus"] = "readError";
                ConnectionWarning(warnings, path, "readError");
            }
        }

        private static bool CaptureTechnologyRow<T>(JArray rows, Func<T> read, string role, string service, string path,
            ScanBudget budget, JArray warnings, Action<T, JObject> capture, bool allowAbsent = true) where T : class
        {
            var row = new JObject { ["role"] = role, ["service"] = service, ["observationPath"] = path,
                ["status"] = "notVisited", ["scopeComplete"] = false, ["fieldStatus"] = new JObject() };
            rows.Add(row);
            if (!budget.Take()) { ConnectionWarning(warnings, path, "notVisited"); return false; }
            int warningStart = warnings.Count;
            try
            {
                T connection = read();
                if (connection == null)
                {
                    row["status"] = allowAbsent ? "notExposed" : "missing"; row["scopeComplete"] = allowAbsent;
                    if (!allowAbsent) ConnectionWarning(warnings, path, "missing");
                    return true;
                }
                capture(connection, row);
                bool complete = !budget.Truncated && warnings.Count == warningStart;
                row["scopeComplete"] = complete; row["status"] = complete ? "observed" : "incomplete";
            }
            catch (Exception) { row["status"] = "readError"; ConnectionWarning(warnings, path, "readError"); }
            return true;
        }

        private static void ReadTechnologyInterface(AxisEncoderHardwareConnectionInterface connection, JObject row, ScanBudget budget, JArray warnings, InspectionValueRenderer render)
        {
            ReadTechnologyConnectionField(row, "isConnected", () => connection.IsConnected, budget, warnings, render);
            ReadTechnologyConnectionField(row, "connectOption", () => connection.ConnectOption, budget, warnings, render);
            ReadTechnologyAttributes(connection, row, budget, warnings, render);
        }

        private static void ReadTechnologyConnectionField(JObject row, string field, Func<object> read, ScanBudget budget, JArray warnings, InspectionValueRenderer render)
        {
            string status = "notVisited";
            if (budget.Take())
            {
                try
                {
                    object value = read();
                    bool complete;
                    row[field] = render == null ? InspectionValue(value, out complete) : render(value, out complete);
                    status = complete ? "captured" : "displayOnly";
                }
                catch (Exception) { status = "readError"; }
            }
            row["fieldStatus"][field] = status;
            if (status != "captured") ConnectionWarning(warnings, row.Value<string>("observationPath") + "/" + field, status);
        }

        private static void ReadTechnologyAttributes(IEngineeringObject connection, JObject row, ScanBudget budget, JArray warnings, InspectionValueRenderer render)
        {
            string status = "notVisited";
            if (budget.Take())
            {
                int start = warnings.Count;
                row["attributes"] = InspectionAttributes(connection, budget, warnings, row.Value<string>("observationPath"), render);
                status = !budget.Truncated && warnings.Count == start ? "captured" : "incomplete";
            }
            row["fieldStatus"]["attributes"] = status;
            if (status != "captured") ConnectionWarning(warnings, row.Value<string>("observationPath") + "/attributes", status);
        }
    }
}
