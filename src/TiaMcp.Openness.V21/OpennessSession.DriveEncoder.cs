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
    // drive_encoder_configure: one encoder of a drive object through Startdrive's HardwareProjection. Either an encoder is
    // set (SetEncoder: interface, type, absolute/incremental, rotary/linear) or the entries of an existing encoder
    // configuration are changed with exact expected values, never both in one change: before SetEncoder there is no
    // configuration whose entries could be checked. A G220 without an encoder answers every encoder read with
    // EngineeringTargetInvocationException (native 8 November 2026).
    internal sealed partial class OpennessSession
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject DriveEncoderConfigure(EngineeringChange change, Project project, bool write)
        {
            var payload = change.Payload;
            var projection = DriveProjection(ChangeDriveObject(project, payload));
            ushort number = DriveEncoderNumber(payload);
            var encoder = payload["encoder"] as JObject;
            var rows = payload["entries"] as JArray;
            if ((encoder == null) == (rows == null || rows.Count == 0))
                throw new BridgeException("InvalidRequest", "Give exactly one of encoder (to set an encoder) or 1..64 entries (to change an existing encoder's configuration).");
            var result = new JObject { ["encoderNumber"] = number };
            if (encoder != null)
            {
                var chosen = DriveEncoderChoice(encoder);
                result["encoder"] = encoder.DeepClone();
                if (!write) return result;
                projection.SetEncoder(chosen.Item1, chosen.Item2, chosen.Item3, chosen.Item4, number);
                var configured = DriveEncoderEntries(projection, number);
                result["readback"] = new JObject { ["entries"] = new JArray(configured.Keys.OrderBy(k => k, StringComparer.Ordinal)) };
                return result;
            }
            if (rows.Count > 64) throw new BridgeException("InvalidRequest", "entries must list 1..64 encoder configuration entries.");
            var before = DriveEncoderEntries(projection, number);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var planned = new JArray();
            foreach (var token in rows)
            {
                var row = token as JObject;
                if (row == null || row.Count != 3 || row["expected"] == null || row["value"] == null) throw new BridgeException("InvalidRequest", "Each entry needs exactly name, expected and value.");
                string name = ChangeText(row, "name", 64);
                if (!names.Add(name)) throw new BridgeException("InvalidRequest", "Duplicate encoder configuration entry: " + name);
                if (!before.TryGetValue(name, out var entry)) throw new BridgeException("ParameterNotFound", "Encoder " + number + " has no configuration entry " + name + ".");
                if (DriveParameterProtected((int)entry.Number)) throw new BridgeException("SafetyBoundary", "Safety Integrated, commissioning, reset, save and motor identification parameters require dedicated workflows: " + name);
                string current = DriveText(entry.Value);
                if (row["expected"].Type != JTokenType.String || row.Value<string>("expected") != current)
                    throw new BridgeException(write ? "PreviewStale" : "ExpectedValueMismatch", "The current value of " + name + " differs from expected; nothing was written.",
                        new JObject { ["name"] = name, ["current"] = current });
                ConvertChangeValue(row["value"], entry.Value?.GetType() ?? typeof(string));
                planned.Add(new JObject { ["name"] = name, ["number"] = entry.Number, ["unit"] = entry.Unit, ["before"] = current, ["after"] = row["value"].DeepClone() });
            }
            result["entries"] = planned;
            if (!write) return result;
            var configuration = projection.GetCurrentEncoderConfiguration(number);
            var editable = Entries(configuration);
            foreach (var token in rows)
            {
                var entry = editable[token.Value<string>("name")];
                entry.Value = ConvertChangeValue(token["value"], entry.Value?.GetType() ?? typeof(string));
            }
            projection.ProjectEncoderConfiguration(configuration, number);
            var after = DriveEncoderEntries(projection, number);
            var readback = new JArray();
            foreach (var token in rows)
            {
                string name = token.Value<string>("name"), wanted = DriveText(ConvertChangeValue(token["value"], editable[name].Value?.GetType() ?? typeof(string)));
                string actual = after.TryGetValue(name, out var entry) ? DriveText(entry.Value) : null;
                if (actual != wanted) throw new BridgeException("MutationVerificationFailed", "Encoder entry readback differs from the request: " + name + " (" + actual + ").");
                readback.Add(new JObject { ["name"] = name, ["value"] = actual });
            }
            result["readback"] = readback;
            return result;
        }

        // The change fingerprint: the encoder's entries, or that it reads none (no encoder yet), so apply refuses StaleRevision after any change.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject DriveEncoderFingerprint(EngineeringChange change, Project project)
        {
            var projection = DriveProjection(ChangeDriveObject(project, change.Payload));
            Dictionary<string, ConfigurationEntry> entries;
            try { entries = DriveEncoderEntries(projection, DriveEncoderNumber(change.Payload)); }
            catch (BridgeException error) when (error.Code == "UnsupportedCapability") { return new JObject { ["readable"] = false }; }
            var result = new JObject { ["readable"] = true };
            foreach (var pair in entries.OrderBy(p => p.Key, StringComparer.Ordinal)) result[pair.Key] = DriveText(pair.Value.Value);
            return result;
        }

        private static ushort DriveEncoderNumber(JObject payload)
        {
            var token = payload["encoderNumber"];
            if (token == null || token.Type != JTokenType.Integer || token.Value<long>() < 0 || token.Value<long>() > 15)
                throw new BridgeException("InvalidRequest", "encoderNumber must be an integer from 0 to 15, as drive_observe encoderNumbers reads it.");
            return (ushort)token.Value<long>();
        }

        private static Tuple<EncoderInterface, EncoderType, AbsoluteIncrementalFlag, RotaryLinearFlag> DriveEncoderChoice(JObject encoder)
        {
            if (encoder.Count != 4) throw new BridgeException("InvalidRequest", "encoder needs exactly interface, type, absoluteIncremental and rotaryLinear.");
            T Pick<T>(string name) where T : struct
            {
                string text = encoder.Value<string>(name);
                if (text == null || !Enum.GetNames(typeof(T)).Contains(text, StringComparer.Ordinal))
                    throw new BridgeException("InvalidRequest", "encoder." + name + " must be one of " + string.Join(", ", Enum.GetNames(typeof(T))) + ".");
                return (T)Enum.Parse(typeof(T), text);
            }
            var choice = Tuple.Create(Pick<EncoderInterface>("interface"), Pick<EncoderType>("type"), Pick<AbsoluteIncrementalFlag>("absoluteIncremental"), Pick<RotaryLinearFlag>("rotaryLinear"));
            // DRIVE-CLiQ encoders are plugged hardware items, as the G220 motor is; removing an encoder is not this kind's job.
            if (choice.Item1 == EncoderInterface.None || choice.Item2 == EncoderType.NoEncoder || choice.Item1 == EncoderInterface.DriveCliQ || choice.Item2 == EncoderType.DriveCliQ)
                throw new BridgeException("UnsupportedCapability", "Set an encoder on a terminal, D-Sub, HTL or SSI interface; DRIVE-CLiQ encoders and removing an encoder are not supported.");
            return choice;
        }

        private static Dictionary<string, ConfigurationEntry> DriveEncoderEntries(HardwareProjection projection, ushort number)
        {
            EncoderConfiguration configuration;
            try { configuration = projection.GetCurrentEncoderConfiguration(number); }
            catch (EngineeringTargetInvocationException error)
            { throw new BridgeException("UnsupportedCapability", "Startdrive cannot read encoder " + number + " on this drive object: " + error.Message); }
            if (configuration == null) throw new BridgeException("UnsupportedCapability", "The drive object has no configuration for encoder " + number + ".");
            return Entries(configuration);
        }

        private static Dictionary<string, ConfigurationEntry> Entries(EncoderConfiguration configuration)
        {
            var result = new Dictionary<string, ConfigurationEntry>(StringComparer.Ordinal);
            foreach (var entry in configuration.RequiredConfigurationEntries)
                if (!result.ContainsKey(entry.Name)) result[entry.Name] = entry;
            return result;
        }
    }
}
