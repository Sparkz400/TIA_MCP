#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.DFI;

namespace TiaMcp.Openness.V21
{
    // Startdrive is optional. Its types appear only in NoInlining methods reached through Startdrive(...),
    // so a missing Siemens.Engineering.Startdrive assembly fails the drive call, never worker start.
    internal sealed partial class OpennessSession
    {
        private static bool StartdriveMissing(Exception error) => (error is FileNotFoundException || error is FileLoadException || error is TypeLoadException)
            && error.Message.IndexOf("Startdrive", StringComparison.OrdinalIgnoreCase) >= 0;

        private static T Startdrive<T>(Func<T> action)
        {
            try { return action(); }
            catch (Exception error) when (StartdriveMissing(error))
            { throw new BridgeException("UnsupportedCapability", "SINAMICS Startdrive Openness (Siemens.Engineering.Startdrive) is not installed or cannot be loaded."); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ApiObservation ProbeDriveContainer(DeviceItem item, JArray probes) => ProbeCapabilityService<DriveObjectContainer>(item, "hardware", probes);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject DriveObserve(JObject args)
        {
            var project = SelectProject(args);
            string wanted = args.Value<string>("hardwarePath");
            var offsetToken = args["parameterOffset"];
            int offset = offsetToken == null || offsetToken.Type == JTokenType.Null ? 0
                : offsetToken.Type == JTokenType.Integer && offsetToken.Value<long>() >= 0 && offsetToken.Value<long>() <= int.MaxValue ? offsetToken.Value<int>()
                : throw new BridgeException("InvalidRequest", "parameterOffset must be a nonnegative integer.");
            var requested = DriveIntegers(args, "parameterNumbers", int.MaxValue, 4096);
            var numbers = requested == null ? null : new HashSet<int>(requested);
            var motors = DriveIntegers(args, "motorDataSets", ushort.MaxValue, 16);
            var encoders = DriveIntegers(args, "encoderNumbers", ushort.MaxValue, 16);
            var lookup = new ScanBudget(50000);
            var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray();
            var drives = new JArray();
            var seen = new HashSet<DriveObject>();
            bool matched = false;
            foreach (var entry in DeviceItems(project, lookup))
            {
                if (!string.IsNullOrWhiteSpace(wanted) && !string.Equals(entry.Path, wanted, StringComparison.Ordinal)) continue;
                matched = true;
                DriveObjectContainer container;
                try { container = entry.Item.GetService<DriveObjectContainer>(); }
                catch (Exception exception) { warnings.Add(InspectionWarning(entry.Path, exception)); continue; }
                if (container == null)
                {
                    if (!string.IsNullOrWhiteSpace(wanted)) throw new BridgeException("UnsupportedCapability", "The selected hardware item exposes no Startdrive DriveObjectContainer service.");
                    continue;
                }
                var objects = new JArray();
                drives.Add(new JObject { ["hardwarePath"] = entry.Path, ["name"] = entry.Item.Name, ["typeIdentifier"] = entry.Item.TypeIdentifier, ["driveObjects"] = objects });
                try
                {
                    foreach (var driveObject in container.DriveObjects)
                    {
                        if (!seen.Add(driveObject)) continue;
                        if (!budget.Take()) break;
                        objects.Add(ObserveDriveObject(driveObject, entry, numbers, offset, motors, encoders, budget, warnings));
                    }
                }
                catch (Exception exception) { warnings.Add(InspectionWarning(entry.Path, exception)); }
                if (budget.Truncated) break;
            }
            if (lookup.Truncated) warnings.Add("Hardware lookup reached its 50000-item traversal bound.");
            if (!matched && !string.IsNullOrWhiteSpace(wanted) && !lookup.Truncated)
                throw new BridgeException("HardwareNotFound", "No hardware item matches hardwarePath.");
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["drives"] = drives;
            result["parameterOffset"] = offset;
            result["parameterNumbers"] = args["parameterNumbers"]?.DeepClone();
            result["reviewFormat"] = "Each driveObjects[].snapshot is a drive-profile-review-request.v2 before/after snapshot for that drive's hardwarePath. driveId is the local handle DO<driveObjectNumber>.";
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Offline Startdrive drive objects, DriveObject.Parameters values/units/limits, BICO source references, telegrams with directional byte sizes and Address ranges, and explicitly requested motor/encoder configuration entries.",
                "Parameter access is reported unknown; Openness does not expose per-parameter write access", "Bits sub-compositions and ReadParameters are not expanded",
                "Telegram addresses use the hardware Address convention (start byte, length in bits); PLC ownership, PKW and hardware identifiers are not resolved",
                "Drive family and parameter set are not exposed; order number and firmware come from the hosting hardware item's attributes",
                "Parameters are paged per drive object with parameterOffset; nextParameterOffset marks the first omitted row", "No online values, upload, download or RAM-to-ROM");
            return result;
        }

        private static int[] DriveIntegers(JObject args, string name, int max, int maxCount)
        {
            if (args[name] == null || args[name].Type == JTokenType.Null) return null;
            var values = args[name] as JArray;
            if (values == null || values.Count == 0 || values.Count > maxCount || values.Any(v => v.Type != JTokenType.Integer || v.Value<long>() < 0 || v.Value<long>() > max))
                throw new BridgeException("InvalidRequest", name + " must list 1.." + maxCount + " integers from 0 to " + max + ".");
            return values.Select(v => v.Value<int>()).Distinct().ToArray();
        }

        private static JObject ObserveDriveObject(DriveObject driveObject, HardwareEntry entry, HashSet<int> numbers, int offset, int[] motors, int[] encoders, ScanBudget budget, JArray warnings)
        {
            int number = driveObject.DriveObjectNumber;
            var telegrams = new JArray(); var ids = new HashSet<string>(StringComparer.Ordinal); bool telegramsComplete = true;
            try
            {
                foreach (var telegram in driveObject.Telegrams)
                {
                    if (telegrams.Count == 128 || !budget.Take()) { telegramsComplete = false; break; }
                    try { telegrams.Add(DriveTelegram(telegram, ids)); }
                    catch (Exception exception) { telegramsComplete = false; warnings.Add(InspectionWarning(entry.Path + "#DO" + number + "/telegrams", exception)); }
                }
            }
            catch (Exception exception) { telegramsComplete = false; warnings.Add(InspectionWarning(entry.Path + "#DO" + number + "/telegrams", exception)); }
            var parameters = new JArray(); int matched = 0, visited = 0; int? next = null; bool scanTruncated = false;
            try
            {
                foreach (var parameter in driveObject.Parameters)
                {
                    if (++visited > 50000) { scanTruncated = true; break; }
                    if (numbers != null && !numbers.Contains(parameter.Number)) continue;
                    if (matched++ < offset) continue;
                    if (!budget.Take()) { next = matched - 1; break; }
                    parameters.Add(DriveParameterRow(parameter));
                }
            }
            catch (Exception exception) { scanTruncated = true; warnings.Add(InspectionWarning(entry.Path + "#DO" + number + "/parameters", exception)); }
            return new JObject
            {
                ["driveObjectNumber"] = number,
                ["snapshot"] = new JObject { ["identity"] = DriveIdentity(entry.Item, number), ["valueDomain"] = "offline",
                    ["parametersComplete"] = numbers == null && offset == 0 && next == null && !scanTruncated, ["parameters"] = parameters,
                    ["telegramsComplete"] = telegramsComplete, ["telegrams"] = telegrams },
                ["parameterOffset"] = offset, ["nextParameterOffset"] = next, ["parameterScanVisited"] = visited, ["parameterScanTruncated"] = scanTruncated,
                ["motorConfigurations"] = DriveConfigurations(driveObject, motors, true, budget),
                ["encoderConfigurations"] = DriveConfigurations(driveObject, encoders, false, budget)
            };
        }

        private static JObject DriveIdentity(DeviceItem item, int number)
        {
            string order = DriveAttribute(item, "OrderNumber"), firmware = DriveAttribute(item, "FirmwareVersion");
            return new JObject { ["driveId"] = "DO" + number, ["family"] = null, ["orderNumber"] = order, ["firmware"] = firmware, ["parameterSet"] = null,
                ["unknownReason"] = "Drive family and parameter set are not exposed by drive_observe" + (order == null || firmware == null ? "; the hosting hardware item did not expose OrderNumber/FirmwareVersion" : "") + "." };
        }

        private static string DriveAttribute(DeviceItem item, string name)
        {
            try { string text = item.GetAttribute(name) as string; return string.IsNullOrWhiteSpace(text) ? null : text; }
            catch (Exception) { return null; }
        }

        private static JObject DriveParameterRow(DriveParameter parameter)
        {
            string unit;
            try { unit = parameter.Unit; } catch (Exception) { unit = null; }
            return new JObject { ["name"] = parameter.Name, ["number"] = parameter.Number, ["arrayIndex"] = parameter.ArrayIndex, ["parentName"] = null,
                ["unit"] = unit, ["access"] = "unknown", ["value"] = DriveValue(parameter, out _),
                ["metadata"] = new JObject { ["parameterText"] = DriveRead(() => parameter.ParameterText), ["arrayLength"] = DriveRead(() => parameter.ArrayLength),
                    ["minValue"] = DriveRead(() => DriveText(parameter.MinValue)), ["maxValue"] = DriveRead(() => DriveText(parameter.MaxValue)),
                    ["enumValues"] = DriveRead(() => parameter.EnumValueList == null ? null : new JObject(parameter.EnumValueList.Select(pair => new JProperty(pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value)))) } };
        }

        // Shaped as a drive-profile-review value: scalar, BICO reference or unavailable.
        private static JObject DriveValue(DriveParameter parameter, out object value)
        {
            try { value = parameter.Value; }
            catch (Exception) { value = null; return new JObject { ["kind"] = "unavailable", ["reason"] = "Native value could not be read." }; }
            if (value is DriveParameter source)
            {
                string owner = null, name = null;
                try
                {
                    name = source.Name;
                    IEngineeringObject parent = source.Parent;
                    for (int depth = 0; depth < 4 && parent != null && !(parent is DriveObject); depth++) parent = parent.Parent;
                    if (parent is DriveObject driveObject) owner = "DO" + driveObject.DriveObjectNumber;
                }
                catch (Exception) { }
                return new JObject { ["kind"] = "reference", ["driveId"] = owner, ["parameterName"] = name,
                    ["reason"] = owner == null || name == null ? "BICO source owner or name could not be resolved." : null };
            }
            if (value == null) return new JObject { ["kind"] = "unavailable", ["reason"] = "Native value is null; for a BICO sink this can mean an inaccessible or invalid source." };
            var type = value.GetType();
            if (!(value is string || value is bool || value is decimal || type.IsPrimitive || type.IsEnum))
                return new JObject { ["kind"] = "unavailable", ["reason"] = "Non-scalar native value " + type.FullName + " is not serialized." };
            return new JObject { ["kind"] = "scalar", ["type"] = type.FullName, ["text"] = DriveText(value) };
        }

        private static string DriveText(object value) => value == null ? null
            : value is IFormattable formattable ? formattable.ToString(value is float || value is double ? "R" : null, CultureInfo.InvariantCulture) : value.ToString();

        private static JToken DriveRead(Func<JToken> read)
        {
            try { return read() ?? JValue.CreateNull(); }
            catch (Exception error) { return new JObject { ["readError"] = error.GetType().Name }; }
        }

        private static JObject DriveTelegram(Telegram telegram, HashSet<string> ids)
        {
            string type = telegram.Type.ToString(), id = type;
            for (int n = 2; !ids.Add(id); n++) id = type + "#" + n;
            var addresses = new JArray(); var addressIds = new HashSet<string>(StringComparer.Ordinal); bool complete = true;
            foreach (var address in telegram.Addresses)
            {
                if (addresses.Count == 16) { complete = false; break; }
                string io = address.IoType.ToString(), addressId = io;
                for (int n = 2; !addressIds.Add(addressId); n++) addressId = io + "#" + n;
                string direction = io == "Input" || io == "Output" ? io : null;
                addresses.Add(new JObject { ["id"] = addressId, ["plcPath"] = null, ["direction"] = direction, ["context"] = "Telegram.Addresses",
                    ["startAddress"] = address.StartAddress < 0 ? null : (int?)address.StartAddress, ["addressUnit"] = "byte",
                    ["length"] = address.Length < 0 ? null : (int?)address.Length, ["lengthUnit"] = "bit",
                    ["unknownReason"] = "PLC ownership is not resolved by drive_observe" + (direction == null ? "; IoType " + io + " is neither Input nor Output" : "") + "." });
            }
            return new JObject { ["id"] = id, ["type"] = type, ["number"] = telegram.TelegramNumber,
                ["inputBytes"] = telegram.GetSizeInBytes(AddressIoType.Input), ["outputBytes"] = telegram.GetSizeInBytes(AddressIoType.Output),
                ["addressesComplete"] = complete, ["addresses"] = addresses, ["unknownReason"] = complete ? null : "More than 16 addresses; capture is truncated." };
        }

        // Dataset/encoder indices are caller-supplied: their ranges are not documented, so none are guessed.
        private static JArray DriveConfigurations(DriveObject driveObject, int[] numbers, bool motor, ScanBudget budget)
        {
            var result = new JArray();
            if (numbers == null) return result;
            HardwareProjection projection = null; string failure = null;
            try { projection = driveObject.GetService<DriveFunctionInterface>()?.HardwareProjection; }
            catch (Exception error) { failure = error.GetType().Name; }
            foreach (int number in numbers)
            {
                var row = new JObject { ["number"] = number };
                result.Add(row);
                if (projection == null) { row["status"] = failure == null ? "notExposed" : "readError"; row["error"] = failure; continue; }
                try
                {
                    ConfigurationEntryComposition[] sets; string[] names;
                    if (motor)
                    {
                        var configuration = projection.GetCurrentMotorConfiguration((ushort)number);
                        sets = configuration == null ? null : new[] { configuration.RequiredConfigurationEntries, configuration.OptionalConfigurationEntries };
                        names = new[] { "required", "optional" };
                    }
                    else
                    {
                        var configuration = projection.GetCurrentEncoderConfiguration((ushort)number);
                        sets = configuration == null ? null : new[] { configuration.RequiredConfigurationEntries, configuration.EncoderTypes };
                        names = new[] { "required", "encoderTypes" };
                    }
                    if (sets == null) { row["status"] = "null"; continue; }
                    var entries = new JArray();
                    for (int i = 0; i < sets.Length; i++)
                        foreach (var entry in sets[i])
                        {
                            if (!budget.Take()) break;
                            entries.Add(new JObject { ["composition"] = names[i], ["name"] = DriveRead(() => entry.Name), ["number"] = DriveRead(() => entry.Number),
                                ["value"] = DriveRead(() => DriveText(entry.Value)), ["unit"] = DriveRead(() => entry.Unit) });
                        }
                    row["status"] = budget.Truncated ? "truncated" : "observed"; row["entries"] = entries;
                }
                catch (Exception error) { row["status"] = "readError"; row["error"] = error.GetType().Name; }
            }
            return result;
        }

        // Safety Integrated (p9300..p10999) and commissioning, reset, save and motor identification
        // parameters trigger drive functions; they need dedicated workflows, not a scalar write.
        private static bool DriveParameterProtected(int number)
            => number >= 9300 && number <= 10999 || Array.IndexOf(new[] { 9, 10, 970, 971, 972, 976, 977, 1900, 1910, 1960, 3900 }, number) >= 0;

        private DriveObject ChangeDriveObject(Project project, JObject payload)
        {
            var entry = ChangeHardware(project, payload);
            if (payload["driveObjectNumber"]?.Type != JTokenType.Integer) throw new BridgeException("InvalidRequest", "driveObjectNumber must be the integer reported by drive_observe.");
            long number = payload.Value<long>("driveObjectNumber");
            var container = entry.Item.GetService<DriveObjectContainer>()
                ?? throw new BridgeException("UnsupportedCapability", "The selected hardware item exposes no Startdrive DriveObjectContainer service.");
            DriveObject found = null;
            foreach (var driveObject in container.DriveObjects)
            {
                if (driveObject.DriveObjectNumber != number) continue;
                if (found != null) throw new BridgeException("AmbiguousTarget", "More than one drive object has driveObjectNumber " + number + ".");
                found = driveObject;
            }
            return found ?? throw new BridgeException("DriveObjectNotFound", "No drive object on the selected hardware item has driveObjectNumber " + number + ".");
        }

        // Prepare (write=false) returns the preview; apply (write=true) rechecks every expected value, writes, then reads back.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JArray DriveParameterSet(EngineeringChange change, Project project, bool write)
        {
            var driveObject = ChangeDriveObject(project, change.Payload);
            var rows = change.Payload["parameters"] as JArray;
            if (rows == null || rows.Count == 0 || rows.Count > 200) throw new BridgeException("InvalidRequest", "parameters must list 1..200 drive parameters.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<DriveParameter>(); var values = new List<object>(); var result = new JArray();
            foreach (var token in rows)
            {
                var row = token as JObject;
                if (row == null || row.Count != 3 || row["expected"] == null || row["value"] == null) throw new BridgeException("InvalidRequest", "Each parameter needs exactly name, expected and value.");
                string name = ChangeText(row, "name", 256);
                if (!names.Add(name)) throw new BridgeException("InvalidRequest", "Duplicate drive parameter: " + name);
                var parameter = driveObject.Parameters.Find(name);
                if (parameter == null || !string.Equals(parameter.Name, name, StringComparison.Ordinal)) throw new BridgeException("ParameterNotFound", "No drive parameter has the exact name: " + name);
                if (DriveParameterProtected(parameter.Number)) throw new BridgeException("SafetyBoundary", "Safety Integrated, commissioning, reset, save and motor identification parameters require dedicated workflows: " + name);
                var current = DriveValue(parameter, out object before);
                if (current.Value<string>("kind") != "scalar") throw new BridgeException("UnsupportedParameterValue", "Only scalar drive parameter values can be written; BICO interconnections and unavailable values are refused: " + name);
                if (!JToken.DeepEquals(current, row["expected"]))
                    throw new BridgeException(write ? "PreviewStale" : "ExpectedValueMismatch", "The current value of " + name + " differs from expected; nothing was written.", new JObject { ["name"] = name, ["current"] = current });
                targets.Add(parameter); values.Add(ConvertChangeValue(row["value"], before.GetType()));
                result.Add(new JObject { ["name"] = name, ["number"] = parameter.Number, ["arrayIndex"] = parameter.ArrayIndex, ["before"] = current, ["after"] = row["value"].DeepClone() });
            }
            if (!write) return result;
            // The whole batch is preflighted before the first setter; a native failure after dispatch still requires checkpoint recovery.
            for (int i = 0; i < targets.Count; i++) targets[i].Value = values[i];
            result = new JArray();
            for (int i = 0; i < targets.Count; i++)
            {
                var expected = new JObject { ["kind"] = "scalar", ["type"] = values[i].GetType().FullName, ["text"] = DriveText(values[i]) };
                var actual = DriveValue(targets[i], out _);
                if (!JToken.DeepEquals(expected, actual)) throw new BridgeException("MutationVerificationFailed", "Drive parameter readback differs from the request: " + rows[i].Value<string>("name"));
                result.Add(new JObject { ["name"] = rows[i].Value<string>("name"), ["value"] = actual });
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject DriveParameterFingerprint(EngineeringChange change, Project project)
        {
            var driveObject = ChangeDriveObject(project, change.Payload);
            var values = new JObject();
            foreach (var row in change.Payload["parameters"].OfType<JObject>())
            {
                string name = row.Value<string>("name");
                var parameter = driveObject.Parameters.Find(name);
                values[name] = parameter == null ? null : DriveValue(parameter, out _);
            }
            return values;
        }
    }
}
