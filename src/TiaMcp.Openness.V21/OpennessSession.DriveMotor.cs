#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.DFI;
using Siemens.Engineering.MC.Drives.Enums;

namespace TiaMcp.Openness.V21
{
    // drive_motor_configure: the motor of one drive data set through Startdrive's HardwareProjection (third-generation drives
    // such as the G220; a G120C answers every read with EngineeringTargetInvocationException, native 8 November 2026).
    // Startdrive types stay in NoInlining methods reached through Startdrive(...).
    internal sealed partial class OpennessSession
    {
        // Motor types for a motor without a Siemens code number; a catalogued Siemens motor has its own commissioning route.
        private static readonly string[] MotorConfigureTypes = { "InductionMotor", "SynchronousMotor" };

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject DriveMotorConfigure(EngineeringChange change, Project project, bool write)
        {
            var payload = change.Payload;
            var driveObject = ChangeDriveObject(project, payload);
            ushort dataSet = DriveMotorDataSet(payload);
            string typeName = payload["motorType"] == null ? null : ChangeText(payload, "motorType", 32);
            if (typeName != null && Array.IndexOf(MotorConfigureTypes, typeName) < 0)
                throw new BridgeException("UnsupportedCapability", "motorType must be InductionMotor or SynchronousMotor (a motor without a Siemens code number).");
            var rows = payload["entries"] as JArray;
            if (rows == null || rows.Count == 0 || rows.Count > 64) throw new BridgeException("InvalidRequest", "entries must list 1..64 motor configuration entries.");
            var projection = DriveProjection(driveObject);
            var before = DriveMotorEntries(projection, dataSet);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var result = new JObject { ["driveDataSet"] = dataSet, ["motorType"] = typeName, ["entries"] = new JArray() };
            foreach (var token in rows)
            {
                var row = token as JObject;
                if (row == null || row.Count != 3 || row["expected"] == null || row["value"] == null) throw new BridgeException("InvalidRequest", "Each entry needs exactly name, expected and value.");
                string name = ChangeText(row, "name", 64);
                if (!names.Add(name)) throw new BridgeException("InvalidRequest", "Duplicate motor configuration entry: " + name);
                if (!before.TryGetValue(name, out var entry)) throw new BridgeException("ParameterNotFound", "The motor configuration of data set " + dataSet + " has no entry " + name + ".");
                string current = DriveText(entry.Value);
                if (row["expected"].Type != JTokenType.String || row.Value<string>("expected") != current)
                    throw new BridgeException(write ? "PreviewStale" : "ExpectedValueMismatch", "The current value of " + name + " differs from expected; nothing was written.",
                        new JObject { ["name"] = name, ["current"] = current });
                ((JArray)result["entries"]).Add(new JObject { ["name"] = name, ["number"] = entry.Number, ["unit"] = entry.Unit, ["before"] = current, ["after"] = row["value"].DeepClone() });
            }
            if (!write) return result;
            // The motor type comes first: it decides which entries the configuration holds. Every entry is preflighted above.
            if (typeName != null) projection.SetMotorType((MotorType)Enum.Parse(typeof(MotorType), typeName), dataSet);
            var configuration = projection.GetCurrentMotorConfiguration(dataSet)
                ?? throw new BridgeException("MutationVerificationFailed", "Startdrive returned no motor configuration after the motor type; checkpoint recovery is required.");
            var editable = Entries(configuration);
            foreach (var token in rows)
            {
                string name = token.Value<string>("name");
                if (!editable.TryGetValue(name, out var entry))
                    throw new BridgeException("MutationVerificationFailed", "The entry " + name + " is gone after the motor type change; checkpoint recovery is required.");
                entry.Value = ConvertChangeValue(token["value"], entry.Value?.GetType() ?? typeof(string));
            }
            projection.ProjectMotorConfiguration(configuration, dataSet);
            var after = DriveMotorEntries(projection, dataSet);
            var readback = new JArray();
            foreach (var token in rows)
            {
                string name = token.Value<string>("name"), wanted = DriveText(ConvertChangeValue(token["value"], editable[name].Value?.GetType() ?? typeof(string)));
                string actual = after.TryGetValue(name, out var entry) ? DriveText(entry.Value) : null;
                if (actual != wanted) throw new BridgeException("MutationVerificationFailed", "Motor entry readback differs from the request: " + name + " (" + actual + ").");
                readback.Add(new JObject { ["name"] = name, ["value"] = actual });
            }
            result["readback"] = readback;
            return result;
        }

        // The change fingerprint: the data set's entries by name with their values, so apply refuses StaleRevision after any change.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject DriveMotorFingerprint(EngineeringChange change, Project project)
        {
            var entries = DriveMotorEntries(DriveProjection(ChangeDriveObject(project, change.Payload)), DriveMotorDataSet(change.Payload));
            var result = new JObject();
            foreach (var pair in entries.OrderBy(p => p.Key, StringComparer.Ordinal)) result[pair.Key] = DriveText(pair.Value.Value);
            return result;
        }

        private static ushort DriveMotorDataSet(JObject payload)
        {
            var token = payload["driveDataSet"];
            if (token == null || token.Type != JTokenType.Integer || token.Value<long>() < 0 || token.Value<long>() > 31)
                throw new BridgeException("InvalidRequest", "driveDataSet must be an integer from 0 to 31, as drive_observe motorDataSets reads it.");
            return (ushort)token.Value<long>();
        }

        private static HardwareProjection DriveProjection(DriveObject driveObject)
        {
            HardwareProjection projection;
            try { projection = driveObject.GetService<DriveFunctionInterface>()?.HardwareProjection; }
            catch (Exception error) { throw new BridgeException("UnsupportedCapability", "The drive object exposes no readable HardwareProjection (" + error.GetType().Name + ")."); }
            return projection ?? throw new BridgeException("UnsupportedCapability", "The drive object exposes no HardwareProjection.");
        }

        private static Dictionary<string, ConfigurationEntry> DriveMotorEntries(HardwareProjection projection, ushort dataSet)
        {
            MotorConfiguration configuration;
            try { configuration = projection.GetCurrentMotorConfiguration(dataSet); }
            catch (EngineeringTargetInvocationException error)
            { throw new BridgeException("UnsupportedCapability", "Startdrive cannot read the motor configuration of data set " + dataSet + " on this drive: " + error.Message); }
            if (configuration == null) throw new BridgeException("UnsupportedCapability", "The drive has no motor configuration for data set " + dataSet + ".");
            return Entries(configuration);
        }

        private static Dictionary<string, ConfigurationEntry> Entries(MotorConfiguration configuration)
        {
            var result = new Dictionary<string, ConfigurationEntry>(StringComparer.Ordinal);
            foreach (var set in new[] { configuration.RequiredConfigurationEntries, configuration.OptionalConfigurationEntries })
                foreach (var entry in set)
                    if (!result.ContainsKey(entry.Name)) result[entry.Name] = entry;
            return result;
        }
    }
}
