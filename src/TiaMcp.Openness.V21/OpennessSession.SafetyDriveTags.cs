using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.Enums;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp.Openness.V21
{
    // #212: the dedicated route for a drive's PROFIsafe control/status tags. Generic tag routes keep refusing F-I/O addresses
    // (SafetyBoundary); this kind writes only the S_ZSW1/S_STW1 words of one drive object's PROFIsafe telegram 30, at the
    // addresses Startdrive reports, inside an F-I/O range this F-PLC owns, after explicit safety acknowledgement.
    internal sealed partial class OpennessSession
    {
        private const int QualifiedSafetyTelegram = 30;

        // Every PROFIsafe telegram address range of a drive whose addresses the PLC owns or may own, like F-I/O modules.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<(string IoType, int StartByte, int LengthBits, string Path)> DriveSafetyTelegramRanges(PlcSelection plc, IEnumerable<HardwareEntry> hardware)
        {
            var ranges = new List<(string IoType, int StartByte, int LengthBits, string Path)>();
            foreach (var entry in hardware)
            {
                DriveObjectContainer container;
                try { container = entry.Item.GetService<DriveObjectContainer>(); } catch (Exception) { continue; }
                if (container == null) continue;
                foreach (var drive in container.DriveObjects)
                    foreach (var telegram in drive.Telegrams.Where(t => t.Type == TelegramType.SafetyTelegram))
                        foreach (var address in telegram.Addresses)
                            if (!address.AddressControllers.Any() || address.AddressControllers.Any(controller => ControllerMatchesSelectedPlc(controller, plc.Software) != false))
                                ranges.Add((address.IoType.ToString(), address.StartAddress, address.Length, entry.Path));
            }
            return ranges;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject SafetyDriveTagPlan(EngineeringChange change, Project project)
        {
            var payload = change.Payload;
            RequireChangeBool(payload, "safetyAcknowledged");
            var plc = SelectPlc(change.Selector);
            EnsureSafetyWritable(plc.Software);
            var drive = ChangeDriveObject(project, payload, out var identity);
            var telegram = drive.Telegrams.FirstOrDefault(t => t.Type == TelegramType.SafetyTelegram)
                ?? throw new BridgeException("SafetyTelegramMissing", "The drive object has no PROFIsafe telegram; configure it in Startdrive first. Nothing was written.", identity);
            int number = telegram.TelegramNumber;
            if (number != QualifiedSafetyTelegram)
                throw new BridgeException("UnsupportedSafetyTelegram", "Only PROFIsafe telegram 30 is qualified for this route; the drive object has telegram " + number + ". Nothing was written.", identity);
            int? Start(string ioType) => telegram.Addresses.Where(a => a.IoType.ToString() == ioType).Select(a => (int?)a.StartAddress).FirstOrDefault();
            int? input = Start("Input"), output = Start("Output");
            var tags = payload["tags"] as JObject ?? throw new BridgeException("InvalidRequest", "tags must name status and/or control.");
            if (tags.Properties().Any(p => p.Name != "status" && p.Name != "control") || tags.Count == 0)
                throw new BridgeException("InvalidRequest", "tags accepts only status (S_ZSW1) and control (S_STW1) names.");
            var device = ChangeHardware(project, payload).Path;
            device = device.Substring(0, device.IndexOf("/items/", StringComparison.Ordinal) < 0 ? device.Length : device.IndexOf("/items/", StringComparison.Ordinal));
            var ranges = FIoAddressRanges(plc);
            var table = plc.Software.TagTableGroup.TagTables.Find(ChangeText(payload, "tagTableName", 128))
                ?? throw new BridgeException("ObjectNotFound", "tagTableName must name an existing tag table in the PLC's root tag-table group.");
            var existing = plc.Software.TagTableGroup.TagTables.SelectMany(t => t.Tags).ToList();
            var rows = new JArray();
            foreach (var property in tags.Properties())
            {
                string name = property.Value.Value<string>();
                if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("InvalidRequest", "Each tag name must be non-empty.");
                int? start = property.Name == "status" ? input : output;
                if (start == null) throw new BridgeException("SafetyTelegramMissing", "The PROFIsafe telegram has no " + (property.Name == "status" ? "input" : "output") + " address.", identity);
                string address = (property.Name == "status" ? "%IW" : "%QW") + start.Value;
                string fIo = TagAddressRules.FIoOverlap(address, ranges);
                if (fIo == null || !(fIo == device || fIo.StartsWith(device + "/", StringComparison.Ordinal)))
                    throw new BridgeException("SafetyBoundary", "The address " + address + " is not in an F-I/O range of this drive that the selected F-PLC owns. Nothing was written.",
                        new JObject { ["logicalAddress"] = address, ["fIoPath"] = fIo, ["drivePath"] = device });
                if (existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new BridgeException("ObjectExists", "A tag named " + name + " already exists in the PLC's root tag tables.");
                if (existing.Any(t => TagAddressRules.Io(t.LogicalAddress) is var io && io != null && TagAddressRules.FIoOverlap(t.LogicalAddress,
                        new[] { (property.Name == "status" ? "Input" : "Output", start.Value, 16, "target") }) != null))
                    throw new BridgeException("AddressInUse", "An existing tag already overlaps " + address + ".", new JObject { ["logicalAddress"] = address });
                rows.Add(new JObject { ["role"] = property.Name, ["signal"] = property.Name == "status" ? "S_ZSW1" : "S_STW1", ["name"] = name,
                    ["dataType"] = "Word", ["logicalAddress"] = address, ["fIoPath"] = fIo });
            }
            return new JObject { ["drive"] = identity, ["telegram"] = new JObject { ["number"] = number, ["inputStart"] = input, ["outputStart"] = output },
                ["tagTable"] = table.Name, ["tags"] = rows };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplySafetyDriveTags(EngineeringChange change, Project project)
        {
            var plan = SafetyDriveTagPlan(change, project);
            if (!JToken.DeepEquals(plan, change.Preview["safetyDriveTags"]))
                throw new BridgeException("StaleRevision", "The drive's PROFIsafe telegram, its addresses or the target table changed since preparation. Nothing was written.");
            var plc = SelectPlc(change.Selector);
            var table = plc.Software.TagTableGroup.TagTables.Find(plan.Value<string>("tagTable"));
            foreach (var row in plan["tags"].OfType<JObject>())
                table.Tags.Create(row.Value<string>("name"), "Word", row.Value<string>("logicalAddress"));
            change.Stages.Add("safety_drive_tags_created");
            foreach (var row in plan["tags"].OfType<JObject>())
            {
                var tag = table.Tags.Find(row.Value<string>("name"));
                if (tag == null || tag.LogicalAddress != row.Value<string>("logicalAddress") || tag.DataTypeName != "Word")
                    throw new BridgeException("ImportVerificationFailed", "Tag " + row.Value<string>("name") + " did not read back with the planned address and type.");
                if (!tag.IsSafety)
                    throw new BridgeException("SafetyVerificationFailed", "Tag " + row.Value<string>("name") + " did not become a safety tag; the address is not PROFIsafe data. The checkpoint is retained.");
            }
            change.Stages.Add("safety_drive_tags_verified");
        }
    }
}
