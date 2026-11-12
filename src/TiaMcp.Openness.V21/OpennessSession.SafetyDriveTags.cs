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
    // (SafetyBoundary); this kind writes only Bool tags on bits of the S_ZSW1/S_STW1 words of one drive object's PROFIsafe
    // telegram 30, at the addresses Startdrive reports, inside an F-I/O range this F-PLC owns, after explicit safety acknowledgement.
    // Bool, not Word: the F-compiler refuses a Word access to a drive's F-I/O (11 November 2026). The caller names each bit; the
    // route encodes no meaning of a bit, only the S7 word layout (TagAddressRules.WordBitAddress).
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
            if (payload["tags"] != null)
                throw new BridgeException("InvalidRequest", "tags (Word tags) is no longer accepted: the F-compiler refuses a Word access to a drive's F-I/O. Name Bool tags per word bit in bits: [{ word: \"status\" or \"control\", bit: 0..15, name }].");
            var bits = payload["bits"] as JArray ?? throw new BridgeException("InvalidRequest", "bits must list 1..32 { word, bit, name } entries.");
            if (bits.Count == 0 || bits.Count > 32) throw new BridgeException("InvalidRequest", "bits must list 1..32 entries.");
            var device = ChangeHardware(project, payload).Path;
            device = device.Substring(0, device.IndexOf("/items/", StringComparison.Ordinal) < 0 ? device.Length : device.IndexOf("/items/", StringComparison.Ordinal));
            var ranges = FIoAddressRanges(plc);
            var table = plc.Software.TagTableGroup.TagTables.Find(ChangeText(payload, "tagTableName", 128))
                ?? throw new BridgeException("ObjectNotFound", "tagTableName must name an existing tag table in the PLC's root tag-table group.");
            var existing = plc.Software.TagTableGroup.TagTables.SelectMany(t => t.Tags).ToList();
            var rows = new JArray();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in bits)
            {
                if (!(entry is JObject item) || item.Properties().Any(p => p.Name != "word" && p.Name != "bit" && p.Name != "name"))
                    throw new BridgeException("InvalidRequest", "Each bits entry holds exactly word, bit and name.");
                string word = item.Value<string>("word");
                if (word != "status" && word != "control") throw new BridgeException("InvalidRequest", "word must be status (S_ZSW1) or control (S_STW1).");
                if (item["bit"]?.Type != JTokenType.Integer || item.Value<long>("bit") < 0 || item.Value<long>("bit") > 15)
                    throw new BridgeException("InvalidRequest", "bit must be an integer 0..15 of the 16-bit word.");
                int bit = item.Value<int>("bit");
                string name = item.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new BridgeException("InvalidRequest", "Each tag name must be non-empty and at most 128 characters.");
                if (!names.Add(name)) throw new BridgeException("InvalidRequest", "The tag name " + name + " is listed twice.");
                bool status = word == "status";
                int? start = status ? input : output;
                if (start == null) throw new BridgeException("SafetyTelegramMissing", "The PROFIsafe telegram has no " + (status ? "input" : "output") + " address.", identity);
                string address = TagAddressRules.WordBitAddress(status, start.Value, bit);
                if (!addresses.Add(address)) throw new BridgeException("InvalidRequest", "Bit " + bit + " of the " + word + " word is listed twice.");
                string fIo = TagAddressRules.FIoOverlap(address, ranges);
                if (fIo == null || !(fIo == device || fIo.StartsWith(device + "/", StringComparison.Ordinal)))
                    throw new BridgeException("SafetyBoundary", "The address " + address + " is not in an F-I/O range of this drive that the selected F-PLC owns. Nothing was written.",
                        new JObject { ["logicalAddress"] = address, ["fIoPath"] = fIo, ["drivePath"] = device });
                if (existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new BridgeException("ObjectExists", "A tag named " + name + " already exists in the PLC's root tag tables.");
                var overlapping = existing.FirstOrDefault(t => TagAddressRules.IoOverlaps(t.LogicalAddress, address));
                if (overlapping != null)
                    throw new BridgeException("AddressInUse", "The existing tag " + overlapping.Name + " (" + overlapping.LogicalAddress + ") already covers " + address + ".",
                        new JObject { ["logicalAddress"] = address, ["existingTag"] = overlapping.Name, ["existingAddress"] = overlapping.LogicalAddress });
                rows.Add(new JObject { ["word"] = word, ["signal"] = status ? "S_ZSW1" : "S_STW1", ["bit"] = bit, ["name"] = name,
                    ["dataType"] = "Bool", ["logicalAddress"] = address, ["fIoPath"] = fIo });
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
                table.Tags.Create(row.Value<string>("name"), "Bool", row.Value<string>("logicalAddress"));
            change.Stages.Add("safety_drive_tags_created");
            foreach (var row in plan["tags"].OfType<JObject>())
            {
                var tag = table.Tags.Find(row.Value<string>("name"));
                if (tag == null || tag.LogicalAddress != row.Value<string>("logicalAddress") || tag.DataTypeName != "Bool")
                    throw new BridgeException("ImportVerificationFailed", "Tag " + row.Value<string>("name") + " did not read back with the planned address and type.");
                if (!tag.IsSafety)
                    throw new BridgeException("SafetyVerificationFailed", "Tag " + row.Value<string>("name") + " did not become a safety tag; the address is not PROFIsafe data. The checkpoint is retained.");
            }
            change.Stages.Add("safety_drive_tags_verified");
        }
    }
}
