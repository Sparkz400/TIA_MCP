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
