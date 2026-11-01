using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

int count = 0;
const string station = "/projects/P/devices/Remote/items/Rack";
const string module = station + "/items/DI";
const string controller = "/projects/P/devices/CPU/items/Rack/items/CPU/items/PN";
const string remote = station + "/items/IM/items/PN";
JObject Row(string path, int slot = 0) => new JObject { ["path"] = path, ["name"] = path.Split('/').Last(), ["typeIdentifier"] = "Catalog:DI", ["positionNumber"] = slot,
    ["containerPath"] = path == station ? null : path.Substring(0, path.LastIndexOf("/items/", StringComparison.Ordinal)),
    ["attributes"] = new JObject { ["Comment"] = "keep" }, ["addresses"] = new JArray(), ["channels"] = new JArray() };
JObject SystemRef() => new JObject { ["state"] = "assigned", ["name"] = "Io", ["number"] = 100, ["ownerHardwarePath"] = controller, ["controllerIndex"] = 0, ["subnet"] = "PN" };
JArray Baseline()
{
    var parent = Row(station); var child = Row(module, 2); var cpu = Row(controller); var im = Row(remote);
    child["channels"] = new JArray(new JObject { ["number"] = 0, ["type"] = "Digital", ["ioType"] = "Input", ["attributes"] = new JObject { ["ChannelActivation"] = true } },
        new JObject { ["number"] = 1, ["type"] = "Digital", ["ioType"] = "Input", ["attributes"] = new JObject { ["ChannelActivation"] = true } });
    child["addresses"] = new JArray(new JObject { ["ioType"] = "Input", ["startAddress"] = 0, ["lengthBits"] = 16, ["owners"] = new JArray() });
    cpu["network"] = new JObject { ["nodes"] = new JArray(), ["controllers"] = new JArray(new JObject { ["index"] = 0, ["system"] = SystemRef() }), ["connectors"] = new JArray() };
    im["network"] = new JObject { ["nodes"] = new JArray(), ["controllers"] = new JArray(), ["connectors"] = new JArray(Enumerable.Range(0, 4).Select(index => new JObject { ["index"] = index, ["system"] = new JObject { ["state"] = "notAssigned" } })) };
    return new JArray(parent, child, cpu, im);
}
void Check(string name, string kind, JObject payload, Action<JArray> mutate, bool allowed, Action<JArray> setup = null)
{
    var before = Baseline(); setup?.Invoke(before); var after = (JArray)before.DeepClone(); mutate(after);
    try { HardwareEngineeringRules.RequirePreserved(kind, payload, before, after); if (!allowed) throw new Exception("Unexpected preservation acceptance: " + name); }
    catch (BridgeException error) when (!allowed && error.Code == "MutationVerificationFailed") { }
    count++; Console.WriteLine("PASS " + name);
}
var move = new JObject { ["hardwarePath"] = module, ["positionNumber"] = 3 };
Check("slot move", "hardware_move", move, rows => rows[1]["positionNumber"] = 3, true);
Check("unchanged move rejected", "hardware_move", move, _ => { }, false);
Check("collateral comment rejected", "hardware_move", move, rows => { rows[1]["positionNumber"] = 3; rows[0]["attributes"]["Comment"] = "lost"; }, false);
Check("collateral address rejected", "hardware_move", move, rows => { rows[1]["positionNumber"] = 3; rows[1]["addresses"][0]["startAddress"] = 2; }, false);
Check("non-object after row cannot disappear from preservation", "hardware_move", move, rows => { rows[1]["positionNumber"] = 3; rows.Add("unparsed native row"); }, false);
Check("removed non-object before row cannot disappear from preservation", "hardware_move", move, rows => { rows[1]["positionNumber"] = 3; rows.RemoveAt(rows.Count - 1); }, false, rows => rows.Add("unparsed native row"));
Check("duplicate unrelated path cannot prove preservation", "hardware_move", move, rows => rows[1]["positionNumber"] = 3, false, rows => rows.Add(rows[2].DeepClone()));
Check("missing unrelated path cannot prove preservation", "hardware_move", move, rows => rows[1]["positionNumber"] = 3, false, rows => ((JObject)rows[0]).Remove("path"));
Check("numeric unrelated path cannot prove preservation", "hardware_move", move, rows => rows[1]["positionNumber"] = 3, false, rows => rows[0]["path"] = 42);
Check("empty unrelated path cannot prove preservation", "hardware_move", move, rows => rows[1]["positionNumber"] = 3, false, rows => rows[0]["path"] = "");
foreach (var token in new JToken[] { JValue.CreateNull(), new JValue(true), new JValue(42), new JArray() })
    Check("invalid top-level " + token.Type + " row refused", "hardware_move", move, rows => { rows[1]["positionNumber"] = 3; rows.Add(token.DeepClone()); }, false);
Check("control-character path refused", "hardware_move", move, rows => rows[1]["positionNumber"] = 3, false, rows => rows[0]["path"] = station + "\n");
Check("path case distinctions are retained", "hardware_move", move, rows => rows[1]["positionNumber"] = 3, true, rows => rows.Add(Row(controller.ToLowerInvariant())));
Check("top-level manifest order is not identity", "hardware_move", move, rows => { rows[1]["positionNumber"] = 3; var row = rows[0]; rows.RemoveAt(0); rows.Add(row); }, true);
Check("missing selected path is a structured refusal", "hardware_move", new JObject { ["hardwarePath"] = "/projects/P/missing", ["positionNumber"] = 3 }, _ => { }, false);
foreach (string missing in new[] { "before", "after", "payload" })
{
    try { HardwareEngineeringRules.RequirePreserved("hardware_move", missing == "payload" ? null : move, missing == "before" ? null : Baseline(), missing == "after" ? null : Baseline()); throw new Exception("Missing " + missing + " was accepted"); }
    catch (BridgeException error) when (error.Code == "MutationVerificationFailed") { count++; Console.WriteLine("PASS missing " + missing + " is a structured refusal"); }
}
var channel = new JObject { ["hardwarePath"] = module, ["channelNumber"] = 0, ["channelType"] = "Digital", ["channelIoType"] = "Input", ["attributes"] = new JObject { ["ChannelActivation"] = false } };
Check("one exact channel", "hardware_channel_attributes", channel, rows => rows[1]["channels"][0]["attributes"]["ChannelActivation"] = false, true);
Check("wrong channel rejected", "hardware_channel_attributes", channel, rows => rows[1]["channels"][1]["attributes"]["ChannelActivation"] = false, false);
Check("channel collateral rejected", "hardware_channel_attributes", channel, rows => { rows[1]["channels"][0]["attributes"]["ChannelActivation"] = false; rows[1]["channels"][1]["attributes"]["ChannelActivation"] = false; }, false);
// The rising-edge hardware interrupt: its flag plus the linked OB, which the manifest records by block name.
var interrupt = new JObject { ["hardwarePath"] = module, ["channelNumber"] = 0, ["channelType"] = "Digital", ["channelIoType"] = "Input",
    ["attributes"] = new JObject { ["HardwareInterruptRisingEdgeActive"] = true, ["HardwareInterruptRisingEdgeInterrupt"] = "GapHardwareInterrupt" } };
void Unlinked(JArray rows) { foreach (var row in rows[1]["channels"]) { row["attributes"]["HardwareInterruptRisingEdgeActive"] = false; row["attributes"]["HardwareInterruptRisingEdgeInterrupt"] = null; } }
void Linked(JArray rows) { rows[1]["channels"][0]["attributes"]["HardwareInterruptRisingEdgeActive"] = true; rows[1]["channels"][0]["attributes"]["HardwareInterruptRisingEdgeInterrupt"] = "GapHardwareInterrupt"; }
Check("hardware interrupt flag and OB link of one channel", "hardware_channel_attributes", interrupt, Linked, true, Unlinked);
Check("hardware interrupt linked to another OB rejected", "hardware_channel_attributes", interrupt, rows => { Linked(rows); rows[1]["channels"][0]["attributes"]["HardwareInterruptRisingEdgeInterrupt"] = "Other"; }, false, Unlinked);
Check("hardware interrupt of a second channel rejected", "hardware_channel_attributes", interrupt, rows => { Linked(rows); rows[1]["channels"][1]["attributes"]["HardwareInterruptRisingEdgeActive"] = true; }, false, Unlinked);
// V21 declares the OB link only while the flag is on: enabling adds it, disabling removes it.
void Undeclared(JArray rows) { foreach (var row in rows[1]["channels"]) { row["attributes"]["HardwareInterruptRisingEdgeActive"] = false; ((JObject)row["attributes"]).Remove("HardwareInterruptRisingEdgeInterrupt"); } }
Check("enabling declares the OB link", "hardware_channel_attributes", interrupt, Linked, true, Undeclared);
var interruptOff = new JObject { ["hardwarePath"] = module, ["channelNumber"] = 0, ["channelType"] = "Digital", ["channelIoType"] = "Input", ["attributes"] = new JObject { ["HardwareInterruptRisingEdgeActive"] = false } };
Check("disabling undeclares the OB link", "hardware_channel_attributes", interruptOff, Undeclared, true, rows => { Undeclared(rows); Linked(rows); });
Check("disabling that keeps the OB link rejected", "hardware_channel_attributes", interruptOff, rows => rows[1]["channels"][0]["attributes"]["HardwareInterruptRisingEdgeActive"] = false, false, rows => { Undeclared(rows); Linked(rows); });
foreach (var (label, plcs, types, code) in new[] { ("no PLC in the device", 0, new[] { "HardwareInterrupt" }, "UnsupportedCapability"), ("two PLCs", 2, new[] { "HardwareInterrupt" }, "UnsupportedCapability"),
    ("missing block", 1, new string[0], "BlockNotFound"), ("ambiguous name", 1, new[] { "HardwareInterrupt", "HardwareInterrupt" }, "AmbiguousTarget"),
    ("a non-OB block", 1, new string[] { null }, "InvalidRequest"), ("another OB event class", 1, new[] { "DiagnosticErrorInterrupt" }, "InvalidRequest") })
{
    try { HardwareEngineeringRules.RequireInterruptOb("GapHardwareInterrupt", plcs, types); throw new Exception("Interrupt OB accepted: " + label); }
    catch (BridgeException error) when (error.Code == code) { count++; Console.WriteLine("PASS interrupt OB refused: " + label); }
}
HardwareEngineeringRules.RequireInterruptOb("GapHardwareInterrupt", 1, new[] { "HardwareInterrupt" }); count++; Console.WriteLine("PASS one hardware interrupt OB of the module's PLC");
// The CPU protection level: only under an access-level mode, never a downgrade, and always with a full-access credential reference.
const string umac = "EnabledWithAccessControl", viaLevel = "EnabledWithAccessControlViaAccessLevel";
var reference = new JObject { ["schemaVersion"] = "protected-credential-reference.v1" };
JObject Protection(string mode = null, JToken level = null, bool credential = true, string extra = null)
{
    var payload = new JObject { ["hardwarePath"] = controller, ["attributes"] = mode == null ? new JObject() : new JObject { ["PlcAccessControlConfiguration"] = mode } };
    if (level != null) payload["protectionLevel"] = level;
    if (credential && level != null) { payload["credential"] = reference.DeepClone(); payload["vaultName"] = "default"; }
    if (extra != null) payload[extra] = "SYNTHETIC";
    return payload;
}
foreach (var (label, payload, current, code) in new[] {
    ("entering an access-level mode without a level", Protection(viaLevel), umac, "InvalidRequest"),
    ("full access removes the protection", Protection(viaLevel, "FullAccess"), umac, "ProtectionDowngradeRefused"),
    ("full access including fail-safe removes the protection", Protection(viaLevel, "FullAccessIncludingFailsafe"), umac, "ProtectionDowngradeRefused"),
    ("an unknown level", Protection(viaLevel, "None"), umac, "InvalidRequest"),
    ("a numeric level", Protection(viaLevel, 2), umac, "InvalidRequest"),
    ("a level under a UMAC mode", Protection(null, "ReadAccess"), umac, "InvalidRequest"),
    ("a level while leaving the access-level mode", Protection(umac, "ReadAccess"), viaLevel, "InvalidRequest"),
    ("a level where the item declares no access control", Protection(null, "ReadAccess"), null, "InvalidRequest"),
    ("a level without its credential", Protection(viaLevel, "ReadAccess", credential: false), umac, "CredentialInputInvalid"),
    ("a credential without a level", new JObject { ["hardwarePath"] = controller, ["attributes"] = new JObject(), ["credential"] = reference.DeepClone() }, umac, "CredentialInputInvalid"),
    ("a plaintext password field", Protection(viaLevel, "ReadAccess", extra: "password"), umac, "CredentialInputInvalid") })
{
    try { HardwareEngineeringRules.RequireCpuProtectionRequest(payload, current); throw new Exception("CPU protection request accepted: " + label); }
    catch (BridgeException error) when (error.Code == code) { count++; Console.WriteLine("PASS CPU protection refused: " + label); }
}
foreach (var (label, payload, current) in new[] { ("entering with a level", Protection(viaLevel, "ReadAccess"), umac), ("a level alone under an access-level mode", Protection(null, "NoAccess"), viaLevel),
    ("HMI access", Protection(null, "HMIAccess"), viaLevel), ("leaving the access-level mode without a credential", Protection(umac), viaLevel), ("other parameters only", Protection(), umac) })
{
    HardwareEngineeringRules.RequireCpuProtectionRequest(payload, current); count++; Console.WriteLine("PASS CPU protection admitted: " + label);
}
var mode = new JObject { ["hardwarePath"] = module, ["channelNumber"] = 0, ["channelType"] = "Digital", ["channelIoType"] = "Input", ["attributes"] = new JObject { ["OperatingMode"] = 269 } };
void Modes(JArray rows) { foreach (var row in rows[1]["channels"]) row["attributes"]["OperatingMode"] = 277; }
void Moded(JArray rows) { foreach (var row in rows[1]["channels"]) row["attributes"]["OperatingMode"] = 269; rows[1]["addresses"][0]["lengthBits"] = 8; }
Check("operating mode couples the module's channels and address lengths", "hardware_channel_attributes", mode, Moded, true, Modes);
Check("operating mode keeps start addresses", "hardware_channel_attributes", mode, rows => { Moded(rows); rows[1]["addresses"][0]["startAddress"] = 2; }, false, Modes);
Check("operating mode keeps other modules", "hardware_channel_attributes", mode, rows => { Moded(rows); rows[0]["attributes"]["Comment"] = "lost"; }, false, Modes);
var address = new JObject { ["hardwarePath"] = module, ["ioType"] = "Input", ["oldStartAddress"] = 0, ["startAddress"] = 64 };
Check("address move", "hardware_address_set", address, rows => rows[1]["addresses"][0]["startAddress"] = 64, true);
Check("address length rejected", "hardware_address_set", address, rows => { rows[1]["addresses"][0]["startAddress"] = 64; rows[1]["addresses"][0]["lengthBits"] = 32; }, false);
var plug = new JObject { ["hardwarePath"] = station, ["name"] = "DO", ["typeIdentifier"] = "Catalog:DI", ["positionNumber"] = 3 };
Check("plug subtree", "hardware_plug", plug, rows => { rows.Add(Row(station + "/items/DO", 3)); rows.Add(Row(station + "/items/DO/items/Child", 1)); }, true);
Check("plug physical rack with logical sibling", "hardware_plug", plug, rows => { var row = Row("/projects/P/devices/Remote/items/DO", 3); row["containerPath"] = station; rows.Add(row); }, true);
Check("wrong physical container rejected", "hardware_plug", plug, rows => { var row = Row(station + "/items/DO", 3); row["containerPath"] = "/another/rack"; rows.Add(row); }, false);
Check("wrong plug type rejected", "hardware_plug", plug, rows => { var row = Row(station + "/items/DO", 3); row["typeIdentifier"] = "Wrong"; rows.Add(row); }, false);
Check("unrelated addition rejected", "hardware_plug", plug, rows => { rows.Add(Row(station + "/items/DO", 3)); rows.Add(Row(station + "/items/Stranger", 4)); }, false);
var retain = new JObject { ["importOption"] = "RetainTiaDevice" };
const string parked = "/projects/P/groups/Parking/devices/Remote/items/Rack";
Check("CAx import adds devices", "cax_import", retain, rows => rows.Add(Row(parked)), true);
Check("CAx retain rejects a changed existing row", "cax_import", retain, rows => rows[0]["attributes"]["Comment"] = "imported", false);
Check("CAx parking lot rejects a removed existing row", "cax_import", new JObject { ["importOption"] = "MoveToParkingLot" }, rows => rows.RemoveAt(1), false);
Check("CAx overwrite is recorded, not matched", "cax_import", new JObject { ["importOption"] = "OverwriteTiaDevice" }, rows => { rows.RemoveAt(1); rows[0]["attributes"]["Comment"] = "imported"; }, true);
var caxAfter = Baseline(); caxAfter.RemoveAt(1); caxAfter[0]["attributes"]["Comment"] = "imported"; caxAfter.Add(Row(parked));
if (HardwareEngineeringRules.Difference(Baseline(), caxAfter).ToString() != new JObject { ["added"] = new JArray(parked), ["removed"] = new JArray(module), ["changed"] = new JArray(station) }.ToString())
    throw new Exception("CAx hardware difference differs.");
count++; Console.WriteLine("PASS CAx hardware difference by exact path");
var assign = new JObject { ["hardwarePath"] = remote, ["connectorIndex"] = 0, ["controllerHardwarePath"] = controller, ["controllerIndex"] = 0, ["addressControllerOwnerPath"] = controller };
void Assigned(JArray rows) { rows[3]["network"]["connectors"][0]["system"] = SystemRef(); rows[1]["addresses"][0]["owners"] = new JArray(controller); }
Check("exact assignment plus ownership", "network_io_assign", assign, Assigned, true);
Check("missing selected connector owner refuses structurally", "network_io_assign", assign, rows => rows.RemoveAt(3), false);
Check("duplicate after identity refuses before assignment readback", "network_io_assign", assign, rows => { Assigned(rows); rows.Add(rows[0].DeepClone()); }, false);
Check("wrong connector rejected", "network_io_assign", assign, rows => rows[3]["network"]["connectors"][1]["system"] = SystemRef(), false);
Check("wrong controller rejected", "network_io_assign", assign, rows => { Assigned(rows); rows[3]["network"]["connectors"][0]["system"]["ownerHardwarePath"] = "/other"; }, false);
Check("shared ownership rejected", "network_io_assign", assign, rows => { Assigned(rows); ((JArray)rows[1]["addresses"][0]["owners"]).Add("/other"); }, false);
Check("preexisting foreign ownership rejected", "network_io_assign", assign, Assigned, false, rows => rows[1]["addresses"][0]["owners"] = new JArray("/other"));
Check("another assigned station interface rejected", "network_io_assign", assign, Assigned, false, rows => rows[0]["network"] = new JObject { ["connectors"] = new JArray(new JObject { ["index"] = 0, ["system"] = SystemRef() }) });
Check("assignment address relocation rejected", "network_io_assign", assign, rows => { Assigned(rows); rows[1]["addresses"][0]["startAddress"] = 20; }, false);
void Unaddressed(JArray rows) => rows[1]["addresses"][0]["startAddress"] = -1;
Check("unaddressed range takes its start with the assigned PLC", "network_io_assign", assign, rows => { Assigned(rows); rows[1]["addresses"][0]["startAddress"] = 0; }, true, Unaddressed);
Check("unaddressed range taking a start without an owner rejected", "network_io_assign", assign, rows => { rows[3]["network"]["connectors"][0]["system"] = SystemRef(); rows[1]["addresses"][0]["startAddress"] = 0; }, false, Unaddressed);
var create = (JObject)assign.DeepClone(); create["createIoSystemName"] = "Io";
Check("create exact controller system", "network_io_assign", create, rows => { Assigned(rows); rows[2]["network"]["controllers"][0]["system"] = SystemRef(); }, true,
    rows => rows[2]["network"]["controllers"][0]["system"] = new JObject { ["state"] = "notAssigned" });
var disconnect = new JObject { ["hardwarePath"] = remote, ["connectorIndex"] = 0, ["disconnect"] = true, ["addressControllerOwnerPath"] = controller };
Check("disconnect preserves system", "network_io_assign", disconnect, rows => { rows[3]["network"]["connectors"][0]["system"] = new JObject { ["state"] = "notAssigned" }; rows[1]["addresses"][0]["owners"] = new JArray(); }, true, Assigned);
Check("disconnect retaining owner rejected", "network_io_assign", disconnect, rows => rows[3]["network"]["connectors"][0]["system"] = new JObject { ["state"] = "notAssigned" }, false, Assigned);
var companion = (JObject)assign.DeepClone();
const string serverPath = "/projects/P/devices/Remote/items/Servermodul_1";
companion["expectedServerModule"] = new JObject { ["name"] = "Servermodul_1", ["typeIdentifier"] = "OrderNumber:6ES7 193-6PA00-0AA0/V1.2", ["containerHardwarePath"] = station, ["positionNumber"] = 1,
    ["attributes"] = new JObject { ["AdditionalInformation"] = "", ["Author"] = "fixture-author", ["Comment"] = "", ["LocationIdentifier"] = "", ["Name"] = "Servermodul_1", ["PlantDesignation"] = "" } };
void AssignedWithServer(JArray rows)
{
    Assigned(rows);
    rows.Add(new JObject { ["path"] = serverPath, ["name"] = "Servermodul_1", ["typeIdentifier"] = "OrderNumber:6ES7 193-6PA00-0AA0/V1.2", ["positionNumber"] = 1,
        ["containerPath"] = station, ["attributes"] = new JObject { ["AdditionalInformation"] = "", ["Author"] = "fixture-author", ["Comment"] = "", ["LocationIdentifier"] = "", ["Name"] = "Servermodul_1", ["PlantDesignation"] = "" }, ["channels"] = new JArray(), ["addresses"] = new JArray() });
    rows.Add(new JObject { ["path"] = serverPath + "/items/Servermodul_1", ["name"] = "Servermodul_1", ["typeIdentifier"] = null, ["positionNumber"] = 1,
        ["containerPath"] = serverPath, ["attributes"] = new JObject { ["Comment"] = "", ["Name"] = "Servermodul_1" }, ["channels"] = new JArray(),
        ["addresses"] = new JArray(new JObject { ["ioType"] = "Diagnosis", ["startAddress"] = -1, ["lengthBits"] = 0, ["owners"] = new JArray() }) });
}
Check("declared exact generated server", "network_io_assign", companion, AssignedWithServer, true);
string absentNativeType = null;
var rawNativeNull = new JObject { ["typeIdentifier"] = absentNativeType };
var explicitNull = new JObject { ["typeIdentifier"] = null };
if (rawNativeNull.ToString() != explicitNull.ToString() || JToken.DeepEquals(rawNativeNull, explicitNull)) throw new Exception("Native nullable-string representation regression did not reproduce");
if (HardwareEngineeringRules.MetadataString(absentNativeType).Type != JTokenType.Null || HardwareEngineeringRules.MetadataString("").Type != JTokenType.String)
    throw new Exception("Native metadata capture confused null and empty string");
count++; Console.WriteLine("PASS native string-null distinction reproduced and explicitly captured");
Check("canonical native null companion", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[5]["typeIdentifier"] = HardwareEngineeringRules.MetadataString(absentNativeType); }, true);
Check("raw native string-null differs before capture fix", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[5]["typeIdentifier"] = absentNativeType; }, false);
Check("missing companion type field rejected", "network_io_assign", companion, rows => { AssignedWithServer(rows); ((JObject)rows[5]).Remove("typeIdentifier"); }, false);
Check("empty companion type is not null", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[5]["typeIdentifier"] = ""; }, false);
Check("numeric companion type is not null", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[5]["typeIdentifier"] = 0; }, false);
Check("boolean companion type is not null", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[5]["typeIdentifier"] = false; }, false);
Check("undeclared generated server rejected", "network_io_assign", assign, AssignedWithServer, false);
Check("missing declared server rejected", "network_io_assign", companion, Assigned, false);
Check("changed companion author rejected", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[4]["attributes"]["Author"] = "another"; }, false);
Check("changed companion slot rejected", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[4]["positionNumber"] = 2; }, false);
Check("changed companion container rejected", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[4]["containerPath"] = controller; }, false);
Check("changed diagnostic metadata rejected", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[5]["addresses"][0]["lengthBits"] = 16; }, false);
Check("diagnostic ownership normalization rejected", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows[5]["addresses"][0]["owners"] = new JArray(controller); }, false);
Check("extra generated module rejected", "network_io_assign", companion, rows => { AssignedWithServer(rows); rows.Add(Row("/projects/P/devices/Remote/items/Extra", 2)); }, false);
JObject NodeRow(string name, string subnet) => new JObject { ["name"] = name, ["type"] = "Ethernet", ["subnet"] = subnet, ["attributes"] = new JObject { ["Address"] = "192.168.0.1", ["SubnetMask"] = "255.255.255.0" } };
void Nodes(JArray rows) { rows[2]["network"]["nodes"] = new JArray(NodeRow("PLC", "PN")); rows[3]["network"]["nodes"] = new JArray(NodeRow("IM", null)); }
var configure = new JObject { ["hardwarePath"] = remote, ["nodeName"] = "IM", ["subnetName"] = "pn", ["attributes"] = new JObject { ["Address"] = "192.0.2.11" } };
void Configured(JArray rows) { rows[3]["network"]["nodes"][0]["subnet"] = "PN"; rows[3]["network"]["nodes"][0]["attributes"]["Address"] = "192.0.2.11"; }
Check("requested node address and existing subnet", "network_configure", configure, Configured, true, Nodes);
Check("requested node address not applied rejected", "network_configure", configure, rows => rows[3]["network"]["nodes"][0]["subnet"] = "PN", false, Nodes);
Check("unrequested node attribute rejected", "network_configure", configure, rows => { Configured(rows); rows[3]["network"]["nodes"][0]["attributes"]["SubnetMask"] = "255.255.0.0"; }, false, Nodes);
Check("node on another subnet rejected", "network_configure", configure, rows => { Configured(rows); rows[3]["network"]["nodes"][0]["subnet"] = "Other"; }, false, Nodes);
Check("other node's subnet change rejected", "network_configure", configure, rows => { Configured(rows); rows[2]["network"]["nodes"][0]["subnet"] = null; }, false, Nodes);
Check("I/O-system subnet change rejected", "network_configure", configure, rows => { Configured(rows); rows[2]["network"]["controllers"][0]["system"]["subnet"] = "Other"; }, false, Nodes);
Check("connector assignment by subnet connect rejected", "network_configure", configure, rows => { Configured(rows); rows[3]["network"]["connectors"][0]["system"] = SystemRef(); }, false, Nodes);
var detach = new JObject { ["hardwarePath"] = controller, ["disconnect"] = true };
Check("node disconnect", "network_configure", detach, rows => rows[2]["network"]["nodes"][0]["subnet"] = null, true, Nodes);
Check("node still connected after disconnect rejected", "network_configure", detach, _ => { }, false, Nodes);
Check("unnamed node among two rejected", "network_configure", detach, rows => rows[2]["network"]["nodes"][0]["subnet"] = null, false, rows => { Nodes(rows); ((JArray)rows[2]["network"]["nodes"]).Add(NodeRow("PLC2", "PN")); });
void InvalidCompanion(string name, Action<JObject> mutate)
{
    var payload = (JObject)companion.DeepClone(); mutate(payload);
    try { RemoteIoCompanionContract.ExpectedRows(payload); throw new Exception("Unexpected descriptor acceptance: " + name); }
    catch (BridgeException error) when (error.Code == "InvalidRequest") { }
    count++; Console.WriteLine("PASS " + name);
}
InvalidCompanion("uncaptured companion version rejected", payload => payload["expectedServerModule"]["typeIdentifier"] = "OrderNumber:6ES7 193-6PA00-0AA0/V1.3");
InvalidCompanion("uncaptured initial companion slot rejected", payload => payload["expectedServerModule"]["positionNumber"] = 2);
InvalidCompanion("cross-station companion rejected", payload => payload["expectedServerModule"]["containerHardwarePath"] = controller);
InvalidCompanion("unknown companion metadata rejected", payload => payload["expectedServerModule"]["attributes"]["Unknown"] = "");
var captured = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-v21-initial-server.json")));
HardwareEngineeringRules.RequirePreserved(captured.Value<string>("kind"), (JObject)captured["payload"], (JArray)captured["before"], (JArray)captured["after"]);
count++; Console.WriteLine("PASS captured V21 initial server manifest with explicit contract");
var undeclaredCaptured = (JObject)captured["payload"].DeepClone(); undeclaredCaptured.Remove("expectedServerModule");
try { HardwareEngineeringRules.RequirePreserved(captured.Value<string>("kind"), undeclaredCaptured, (JArray)captured["before"], (JArray)captured["after"]); throw new Exception("Undeclared native companion accepted"); }
catch (BridgeException error) when (error.Code == "MutationVerificationFailed") { }
count++; Console.WriteLine("PASS captured V21 native failure without explicit contract");
var memory = new JObject { ["hardwarePath"] = controller, ["attributes"] = new JObject { ["SystemMemoryByte"] = true, ["SystemMemoryByteAddress"] = 20L } };
void MemoryRows(JArray rows) => rows[2]["attributes"] = new JObject { ["ClockMemoryByte"] = false, ["Comment"] = "keep", ["SystemMemoryByte"] = false };
// Native V21 byte numbers are UInt64; the payload may still carry the JSON Int64 spelling.
Check("cpu memory enable declares its byte number", "hardware_cpu_memory_bytes", memory, rows => { rows[2]["attributes"]["SystemMemoryByte"] = true; rows[2]["attributes"]["SystemMemoryByteAddress"] = JToken.FromObject(20UL); }, true, MemoryRows);
Check("cpu memory enable without its byte number rejected", "hardware_cpu_memory_bytes", memory, rows => rows[2]["attributes"]["SystemMemoryByte"] = true, false, MemoryRows);
Check("cpu memory request not applied rejected", "hardware_cpu_memory_bytes", memory, _ => { }, false, MemoryRows);
Check("cpu memory wrong byte rejected", "hardware_cpu_memory_bytes", memory, rows => { rows[2]["attributes"]["SystemMemoryByte"] = true; rows[2]["attributes"]["SystemMemoryByteAddress"] = 1UL; }, false, MemoryRows);
Check("cpu memory collateral clock byte rejected", "hardware_cpu_memory_bytes", memory, rows => { rows[2]["attributes"]["SystemMemoryByte"] = true; rows[2]["attributes"]["SystemMemoryByteAddress"] = 20UL; rows[2]["attributes"]["ClockMemoryByte"] = true; }, false, MemoryRows);
Check("cpu memory on another item rejected", "hardware_cpu_memory_bytes", memory, rows => { rows[2]["attributes"]["SystemMemoryByte"] = true; rows[2]["attributes"]["SystemMemoryByteAddress"] = 20UL; rows[0]["attributes"]["Comment"] = "lost"; }, false, MemoryRows);
var disable = new JObject { ["hardwarePath"] = controller, ["attributes"] = new JObject { ["SystemMemoryByte"] = false } };
void EnabledRows(JArray rows) { MemoryRows(rows); rows[2]["attributes"]["SystemMemoryByte"] = true; rows[2]["attributes"]["SystemMemoryByteAddress"] = 20UL; }
Check("cpu memory disable drops its byte number", "hardware_cpu_memory_bytes", disable, rows => { rows[2]["attributes"]["SystemMemoryByte"] = false; ((JObject)rows[2]["attributes"]).Remove("SystemMemoryByteAddress"); }, true, EnabledRows);
Check("cpu memory disable keeping its byte number rejected", "hardware_cpu_memory_bytes", disable, rows => rows[2]["attributes"]["SystemMemoryByte"] = false, false, EnabledRows);
string cpuParameter = HardwareEngineeringRules.CpuParameterAttributes[0];
var cpuParameters = new JObject { ["hardwarePath"] = controller, ["attributes"] = new JObject { [cpuParameter] = 150L } };
void CpuParameterRows(JArray rows) => rows[2]["attributes"][cpuParameter] = 100L;
Check("cpu parameter set on the exact item", "hardware_cpu_parameters", cpuParameters, rows => rows[2]["attributes"][cpuParameter] = 150L, true, CpuParameterRows);
Check("cpu parameter request not applied rejected", "hardware_cpu_parameters", cpuParameters, _ => { }, false, CpuParameterRows);
Check("cpu parameter collateral comment rejected", "hardware_cpu_parameters", cpuParameters, rows => { rows[2]["attributes"][cpuParameter] = 150L; rows[2]["attributes"]["Comment"] = "lost"; }, false, CpuParameterRows);
var minimumOff = new JObject { ["hardwarePath"] = controller, ["attributes"] = new JObject { ["CycleEnableMinimumCycleTime"] = false } };
void MinimumRows(JArray rows) { rows[2]["attributes"]["CycleEnableMinimumCycleTime"] = true; rows[2]["attributes"]["CycleMinimumCycleTime"] = 5UL; }
Check("cpu minimum cycle time off drops its value", "hardware_cpu_parameters", minimumOff, rows => { rows[2]["attributes"]["CycleEnableMinimumCycleTime"] = false; ((JObject)rows[2]["attributes"]).Remove("CycleMinimumCycleTime"); }, true, MinimumRows);
Check("cpu minimum cycle time off keeping its value rejected", "hardware_cpu_parameters", minimumOff, rows => rows[2]["attributes"]["CycleEnableMinimumCycleTime"] = false, false, MinimumRows);
Check("cpu parameter on another item rejected", "hardware_cpu_parameters", cpuParameters, rows => { rows[2]["attributes"][cpuParameter] = 150L; rows[0]["attributes"]["Comment"] = "lost"; }, false, CpuParameterRows);
var fParameters = new JObject { ["safetyAcknowledged"] = true, ["parameters"] = new JArray(new JObject { ["hardwarePath"] = module, ["name"] = "Failsafe_FDestinationAddress", ["expected"] = 100, ["value"] = 120 }) };
void FRows(JArray rows) { rows[1]["attributes"]["Failsafe_FDestinationAddress"] = 100; rows[1]["attributes"]["Failsafe_FMonitoringtime"] = 150; rows[3]["attributes"]["Failsafe_FDestinationAddress"] = 101; }
Check("f-destination address on the exact F-I/O", "safety_fparameter_set", fParameters, rows => rows[1]["attributes"]["Failsafe_FDestinationAddress"] = 120, true, FRows);
Check("f-parameter request not applied rejected", "safety_fparameter_set", fParameters, _ => { }, false, FRows);
Check("collateral f-monitoring time rejected", "safety_fparameter_set", fParameters, rows => { rows[1]["attributes"]["Failsafe_FDestinationAddress"] = 120; rows[1]["attributes"]["Failsafe_FMonitoringtime"] = 200; }, false, FRows);
Check("f-parameter on another F-I/O rejected", "safety_fparameter_set", fParameters, rows => { rows[1]["attributes"]["Failsafe_FDestinationAddress"] = 120; rows[3]["attributes"]["Failsafe_FDestinationAddress"] = 120; }, false, FRows);
var memoryCurrent = new JObject { ["SystemMemoryByte"] = false, ["ClockMemoryByte"] = true, ["ClockMemoryByteAddress"] = 0UL };
void Memory(string name, JObject requested, string code)
{
    try { HardwareEngineeringRules.RequireCpuMemoryBytes(memoryCurrent, requested); if (code != null) throw new Exception("Unexpected memory byte acceptance: " + name); }
    catch (BridgeException error) when (error.Code == code) { }
    count++; Console.WriteLine("PASS " + name);
}
Memory("enable system byte with its number", new JObject { ["SystemMemoryByte"] = true, ["SystemMemoryByteAddress"] = 1 }, null);
Memory("move the enabled clock byte", new JObject { ["ClockMemoryByteAddress"] = 100 }, null);
Memory("disable the clock byte", new JObject { ["ClockMemoryByte"] = false }, null);
Memory("enable without a byte number refused", new JObject { ["SystemMemoryByte"] = true }, "InvalidRequest");
Memory("byte number of a disabled flag refused", new JObject { ["SystemMemoryByteAddress"] = 1 }, "InvalidRequest");
Memory("byte number while disabling refused", new JObject { ["ClockMemoryByte"] = false, ["ClockMemoryByteAddress"] = 5 }, "InvalidRequest");
Memory("empty request refused", new JObject(), "InvalidRequest");
Memory("unchanged request refused", new JObject { ["ClockMemoryByteAddress"] = 0 }, "InvalidRequest");
Memory("unchanged flag refused", new JObject { ["SystemMemoryByte"] = false }, "InvalidRequest");
Memory("other attribute refused", new JObject { ["Comment"] = "x" }, "UnsupportedAttribute");
Memory("case-variant name refused", new JObject { ["systemMemoryByte"] = true }, "UnsupportedAttribute");
Memory("string flag refused", new JObject { ["ClockMemoryByte"] = "false" }, "InvalidRequest");
Memory("numeric flag refused", new JObject { ["ClockMemoryByte"] = 0 }, "InvalidRequest");
Memory("negative byte refused", new JObject { ["ClockMemoryByteAddress"] = -1 }, "InvalidRequest");
Memory("byte above 65535 refused", new JObject { ["ClockMemoryByteAddress"] = 65536 }, "InvalidRequest");
Memory("fractional byte refused", new JObject { ["ClockMemoryByteAddress"] = 1.5 }, "InvalidRequest");
Memory("string byte refused", new JObject { ["ClockMemoryByteAddress"] = "10" }, "InvalidRequest");
Memory("same enabled byte refused", new JObject { ["SystemMemoryByte"] = true, ["SystemMemoryByteAddress"] = 0 }, "AddressConflict");
const string portA = controller + "/items/P1", portB = remote + "/items/P1";
JObject PortRow(string path, params string[] links) { var row = Row(path); row["port"] = new JObject { ["connectedPorts"] = new JArray(links) }; return row; }
void FreePorts(JArray rows) { rows.Add(PortRow(portA)); rows.Add(PortRow(portB)); }
void LinkedPorts(JArray rows) { rows.Add(PortRow(portA, portB)); rows.Add(PortRow(portB, portA)); }
void SetLinks(JArray rows, bool linked) { rows[4]["port"]["connectedPorts"] = linked ? new JArray(portB) : new JArray(); rows[5]["port"]["connectedPorts"] = linked ? new JArray(portA) : new JArray(); }
var portLink = new JObject { ["hardwarePath"] = portA, ["partnerHardwarePath"] = portB };
Check("port connect links both ends", "hardware_port_connect", portLink, rows => SetLinks(rows, true), true, FreePorts);
Check("one-sided port connect rejected", "hardware_port_connect", portLink, rows => rows[4]["port"]["connectedPorts"] = new JArray(portB), false, FreePorts);
Check("unchanged port connect rejected", "hardware_port_connect", portLink, _ => { }, false, LinkedPorts);
Check("port connect collateral rejected", "hardware_port_connect", portLink, rows => { SetLinks(rows, true); rows[0]["attributes"]["Comment"] = "lost"; }, false, FreePorts);
Check("port connect without captured port rejected", "hardware_port_connect", new JObject { ["hardwarePath"] = module, ["partnerHardwarePath"] = portB }, _ => { }, false, FreePorts);
Check("self port link rejected", "hardware_port_connect", new JObject { ["hardwarePath"] = portA, ["partnerHardwarePath"] = portA }, _ => { }, false, FreePorts);
Check("port disconnect unlinks both ends", "hardware_port_disconnect", portLink, rows => SetLinks(rows, false), true, LinkedPorts);
Check("partial port disconnect rejected", "hardware_port_disconnect", portLink, rows => rows[4]["port"]["connectedPorts"] = new JArray(), false, LinkedPorts);
JObject Area(string name, int start) => new JObject { ["name"] = name, ["type"] = "CD", ["direction"] = "PartnerToLocal", ["positionNumber"] = start, ["localToPartnerLength"] = 0, ["partnerToLocalLength"] = 1,
    ["localAddresses"] = new JArray(new JObject { ["ioType"] = "Input", ["startAddress"] = start, ["lengthBits"] = 8 }), ["partnerAddresses"] = new JArray() };
void Areas(JArray rows, params JObject[] areas) => rows[2]["network"]["transferAreas"] = new JArray(areas);
var areaCreate = new JObject { ["hardwarePath"] = controller, ["name"] = "Ta" };
Check("transfer area create adds exactly the named area", "hardware_transfer_area_create", areaCreate, rows => Areas(rows, Area("Keep", 2), Area("Ta", 4)), true, rows => Areas(rows, Area("Keep", 2)));
Check("transfer area create without the area rejected", "hardware_transfer_area_create", areaCreate, _ => { }, false, rows => Areas(rows, Area("Keep", 2)));
Check("transfer area create of an existing area rejected", "hardware_transfer_area_create", areaCreate, _ => { }, false, rows => Areas(rows, Area("Ta", 4)));
Check("transfer area create changing another area rejected", "hardware_transfer_area_create", areaCreate, rows => Areas(rows, Area("Keep", 3), Area("Ta", 4)), false, rows => Areas(rows, Area("Keep", 2)));
Check("transfer area create with collateral address rejected", "hardware_transfer_area_create", areaCreate, rows => { Areas(rows, Area("Keep", 2), Area("Ta", 4)); rows[1]["addresses"][0]["startAddress"] = 4; }, false, rows => Areas(rows, Area("Keep", 2)));
Check("transfer area create on an uncaptured interface rejected", "hardware_transfer_area_create", areaCreate, _ => { }, false);
Check("transfer area delete removes exactly the named area", "hardware_transfer_area_delete", areaCreate, rows => Areas(rows, Area("Keep", 2)), true, rows => Areas(rows, Area("Keep", 2), Area("Ta", 4)));
Check("transfer area delete keeping the area rejected", "hardware_transfer_area_delete", areaCreate, _ => { }, false, rows => Areas(rows, Area("Keep", 2), Area("Ta", 4)));
Check("transfer area delete removing another area rejected", "hardware_transfer_area_delete", areaCreate, rows => Areas(rows), false, rows => Areas(rows, Area("Keep", 2), Area("Ta", 4)));
const string child = module + "/items/Sub";
var replace = new JObject { ["hardwarePath"] = module, ["typeIdentifier"] = "Catalog:DI32" };
void WithChild(JArray rows) => rows.Add(Row(child, 1));
void Replaced(JArray rows) { rows[1]["typeIdentifier"] = "Catalog:DI32"; rows[1]["channels"] = new JArray(); rows[1]["addresses"][0]["lengthBits"] = 32; rows[4]["typeIdentifier"] = "Catalog:Sub32"; }
Check("replace keeps addresses and children", "hardware_replace", replace, Replaced, true, WithChild);
Check("replace losing an address rejected", "hardware_replace", replace, rows => { Replaced(rows); rows[1]["addresses"] = new JArray(); }, false, WithChild);
Check("replace shrinking an address rejected", "hardware_replace", replace, rows => { Replaced(rows); rows[1]["addresses"][0]["lengthBits"] = 8; }, false, WithChild);
Check("replace losing a child rejected", "hardware_replace", replace, rows => { Replaced(rows); rows.RemoveAt(4); }, false, WithChild);
var replaceLoss = (JObject)replace.DeepClone(); replaceLoss["acceptLoss"] = true;
Check("replace loss accepted explicitly", "hardware_replace", replaceLoss, rows => { Replaced(rows); rows[1]["addresses"] = new JArray(); rows.RemoveAt(4); }, true, WithChild);
Check("replace wrong type rejected", "hardware_replace", replace, rows => rows[1]["typeIdentifier"] = "Catalog:Other", false, WithChild);
Check("replace unchanged type rejected", "hardware_replace", new JObject { ["hardwarePath"] = module, ["typeIdentifier"] = "Catalog:DI" }, _ => { }, false, WithChild);
Check("replace moving the slot rejected", "hardware_replace", replace, rows => { Replaced(rows); rows[1]["positionNumber"] = 5; }, false, WithChild);
Check("replace collateral outside subtree rejected", "hardware_replace", replaceLoss, rows => { Replaced(rows); rows[0]["attributes"]["Comment"] = "lost"; }, false, WithChild);
Check("replace losing a port link outside subtree rejected", "hardware_replace", replaceLoss, rows => { Replaced(rows); rows[5]["port"]["connectedPorts"] = new JArray(); rows[6]["port"]["connectedPorts"] = new JArray(); }, false, rows => { WithChild(rows); rows.Add(PortRow(child + "/P1", portB)); rows.Add(PortRow(portB, child + "/P1")); });
var inventoryRows = Baseline(); WithChild(inventoryRows);
var inventory = HardwareEngineeringRules.ReplaceInventory(inventoryRows, module);
if (inventory.Value<string>("typeIdentifier") != "Catalog:DI" || ((JArray)inventory["addresses"]).Count != 1 || inventory["addresses"][0].Value<string>("hardwarePath") != module || !JToken.DeepEquals(inventory["children"], new JArray(child)))
    throw new Exception("Replace inventory differs: " + inventory);
count++; Console.WriteLine("PASS replace inventory lists subtree addresses and children");
// Address-changing replacement: an address may move or resize with acceptAddressChange, but keeps its item, I/O type and owners.
var replaceMove = (JObject)replace.DeepClone(); replaceMove["acceptAddressChange"] = true;
void Moved(JArray rows) { Replaced(rows); rows[1]["addresses"][0]["startAddress"] = 4; }
Check("replace moving an address rejected without acceptAddressChange", "hardware_replace", replace, Moved, false, WithChild);
Check("replace moving an address accepted with acceptAddressChange", "hardware_replace", replaceMove, Moved, true, WithChild);
Check("address change losing an address rejected", "hardware_replace", replaceMove, rows => { Replaced(rows); rows[1]["addresses"] = new JArray(); }, false, WithChild);
Check("address change changing owners rejected", "hardware_replace", replaceMove, rows => { Moved(rows); rows[1]["addresses"][0]["owners"] = new JArray("/other"); }, false, WithChild);
Check("address change losing a child rejected", "hardware_replace", replaceMove, rows => { Moved(rows); rows.RemoveAt(4); }, false, WithChild);
var movedRows = Baseline(); WithChild(movedRows); Moved(movedRows);
var addressMap = HardwareEngineeringRules.ReplaceAddressMap(HardwareEngineeringRules.ReplaceInventory(Baseline(), module), HardwareEngineeringRules.ReplaceInventory(movedRows, module));
if (addressMap.Count != 1 || (int)addressMap[0]["before"]["startAddress"] != 0 || (int)addressMap[0]["after"]["startAddress"] != 4 || (int)addressMap[0]["after"]["lengthBits"] != 32 || !(bool)addressMap[0]["changed"])
    throw new Exception("Address map differs: " + addressMap);
count++; Console.WriteLine("PASS address map pairs the old and new range of one item and I/O type");
var unchangedMap = HardwareEngineeringRules.ReplaceAddressMap(HardwareEngineeringRules.ReplaceInventory(Baseline(), module), HardwareEngineeringRules.ReplaceInventory(Baseline(), module));
var tagOnModule = new JObject { ["path"] = "/plc/tags/T/tags/In", ["hardwarePath"] = module, ["ioType"] = "Input", ["startAddress"] = 0 };
var changedTags = HardwareEngineeringRules.ChangedReferencedTags(new JArray(tagOnModule), addressMap);
if (changedTags.Count != 1 || (int)changedTags[0]["rangeAfter"]["startAddress"] != 4 || HardwareEngineeringRules.ChangedReferencedTags(new JArray(tagOnModule), unchangedMap).Count != 0)
    throw new Exception("Changed referenced tags differ: " + changedTags);
count++; Console.WriteLine("PASS referenced tags report only ranges that changed");
var referencedTags = new JArray(tagOnModule, new JObject { ["path"] = "/plc/tags/T/tags/In2" });
foreach (var (label, acknowledged, expected) in new[] { ("exact set", (JToken)new JArray("/plc/tags/T/tags/In2", "/plc/tags/T/tags/In"), true), ("missing tag", new JArray("/plc/tags/T/tags/In"), false),
    ("extra tag", new JArray("/plc/tags/T/tags/In", "/plc/tags/T/tags/In2", "/x"), false), ("no acknowledgement", null, false) })
    if (HardwareEngineeringRules.TagsAcknowledged(referencedTags, acknowledged) != expected) throw new Exception("Tag acknowledgement differs: " + label); else { count++; Console.WriteLine("PASS tag acknowledgement: " + label); }
if (!HardwareEngineeringRules.TagsAcknowledged(new JArray(), null)) throw new Exception("No referenced tag needs no acknowledgement."); count++;
try { HardwareEngineeringRules.TagsAcknowledged(referencedTags, new JArray(1)); throw new Exception("Numeric acknowledgement accepted"); }
catch (BridgeException error) when (error.Code == "InvalidRequest") { count++; Console.WriteLine("PASS malformed acknowledgedTags refused"); }
// Shared device: a second connector of the IO device interface joins another controller's I/O system.
const string controller2 = "/projects/P/devices/CPU2/items/Rack/items/CPU/items/PN";
JObject SystemRef2() => new JObject { ["state"] = "assigned", ["name"] = "Io2", ["number"] = 101, ["ownerHardwarePath"] = controller2, ["controllerIndex"] = 0, ["subnet"] = "PN" };
void SharedBase(JArray rows)
{
    rows[3]["network"]["connectors"][0]["system"] = SystemRef(); rows[1]["addresses"][0]["owners"] = new JArray("/cpu1"); rows[1]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu1";
    var second = Row(controller2); second["network"] = new JObject { ["nodes"] = new JArray(), ["controllers"] = new JArray(new JObject { ["index"] = 0, ["system"] = SystemRef2() }), ["connectors"] = new JArray() }; rows.Add(second);
}
// A sharedDevice connection names the submodules that move to the new controller (sharedSubmodules); everything else stays exact.
var shared = new JObject { ["hardwarePath"] = remote, ["connectorIndex"] = 1, ["controllerHardwarePath"] = controller2, ["controllerIndex"] = 0, ["addressControllerOwnerPath"] = "/cpu2",
    ["sharedDevice"] = true, ["sharedAddressControllerOwnerPaths"] = new JArray("/cpu1"), ["sharedSubmodules"] = new JArray(module) };
void SharedAssigned(JArray rows) => rows[3]["network"]["connectors"][1]["system"] = SystemRef2();
void SharedMoved(JArray rows) { SharedAssigned(rows); rows[1]["addresses"][0]["owners"] = new JArray("/cpu2"); rows[1]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu2"; }
Check("shared device adds a second controller with its submodule", "network_io_assign", shared, SharedMoved, true, SharedBase);
Check("shared submodule that does not move rejected", "network_io_assign", shared, SharedAssigned, false, SharedBase);
Check("shared submodule keeping its address owner rejected", "network_io_assign", shared, rows => { SharedAssigned(rows); rows[1]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu2"; }, false, SharedBase);
Check("shared submodule keeping its assignment target rejected", "network_io_assign", shared, rows => { SharedAssigned(rows); rows[1]["addresses"][0]["owners"] = new JArray("/cpu2"); }, false, SharedBase);
Check("shared submodule without a captured assignment target rejected", "network_io_assign", shared, rows => { SharedMoved(rows); }, false, rows => { SharedBase(rows); ((JObject)rows[1]["attributes"]).Remove("SharedDeviceAssignmentTarget"); });
Check("shared device address owned by both rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[1]["addresses"][0]["owners"] = new JArray("/cpu1", "/cpu2"); }, false, SharedBase);
Check("shared device foreign owner rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[1]["addresses"][0]["owners"] = new JArray("/cpu3"); }, false, SharedBase);
Check("shared device changing the first connector rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[3]["network"]["connectors"][0]["system"] = new JObject { ["state"] = "notAssigned" }; }, false, SharedBase);
Check("shared device without another assigned connector rejected", "network_io_assign", shared, SharedMoved, false, rows => { SharedBase(rows); rows[3]["network"]["connectors"][0]["system"] = new JObject { ["state"] = "notAssigned" }; });
var sharedWithoutPeers = (JObject)shared.DeepClone(); sharedWithoutPeers.Remove("sharedAddressControllerOwnerPaths");
Check("shared device without peer PLCs rejected", "network_io_assign", sharedWithoutPeers, SharedMoved, false, SharedBase);
var sharedWithoutSubmodules = (JObject)shared.DeepClone(); sharedWithoutSubmodules.Remove("sharedSubmodules");
Check("shared device without sharedSubmodules rejected", "network_io_assign", sharedWithoutSubmodules, SharedAssigned, false, SharedBase);
// Rows outside the sharedSubmodules keep their owners and assignment targets; a subitem of a named item may follow it.
void SharedOther(JArray rows)
{
    SharedBase(rows); var other = Row(station + "/items/DO", 3); other["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu1";
    other["addresses"] = new JArray(new JObject { ["ioType"] = "Input", ["startAddress"] = 2, ["lengthBits"] = 8, ["owners"] = new JArray("/cpu1") }); rows.Add(other);
    var sub = Row(child, 1); sub["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu1";
    sub["addresses"] = new JArray(new JObject { ["ioType"] = "Diagnosis", ["startAddress"] = -1, ["lengthBits"] = 0, ["owners"] = new JArray() }); rows.Add(sub);
}
Check("shared submodule's subitem follows its target, diagnosis address exact", "network_io_assign", shared, rows => { SharedMoved(rows); rows[6]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu2"; }, true, SharedOther);
Check("subitem taking a third target rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[6]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu3"; }, false, SharedOther);
Check("address owner change outside the sharedSubmodules rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[5]["addresses"][0]["owners"] = new JArray("/cpu2"); }, false, SharedOther);
Check("assignment target change outside the sharedSubmodules rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[5]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu2"; }, false, SharedOther);
Check("diagnosis address of a shared submodule taking an owner rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[6]["addresses"][0]["owners"] = new JArray("/cpu2"); }, false, SharedOther);
Check("address count change of a station item rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[5]["addresses"] = new JArray(); }, false, SharedOther);
if (!HardwareEngineeringRules.CapturesSharedAccess("network_io_assign", shared) || HardwareEngineeringRules.CapturesSharedAccess("network_io_assign", assign) || HardwareEngineeringRules.CapturesSharedAccess("network_io_assign", null)
    || !HardwareEngineeringRules.CapturesSharedAccess("hardware_shared_device_access", null) || HardwareEngineeringRules.CapturesSharedAccess("hardware_replace", shared))
    throw new Exception("Shared-device attribute capture differs.");
count++; Console.WriteLine("PASS shared-device attributes join the manifest for a sharedDevice assignment and hardware_shared_device_access only");
var peersWithoutShared = (JObject)assign.DeepClone(); peersWithoutShared["sharedAddressControllerOwnerPaths"] = new JArray("/other");
Check("peer PLCs without sharedDevice rejected", "network_io_assign", peersWithoutShared, Assigned, false);
var sharedDisconnect = new JObject { ["hardwarePath"] = remote, ["connectorIndex"] = 1, ["disconnect"] = true, ["addressControllerOwnerPath"] = "/cpu2", ["sharedDevice"] = true, ["sharedAddressControllerOwnerPaths"] = new JArray("/cpu1") };
void SharedOwned(JArray rows) { SharedBase(rows); SharedAssigned(rows); rows[1]["addresses"][0]["owners"] = new JArray("/cpu2"); }
Check("shared disconnect releases the second controller", "network_io_assign", sharedDisconnect, rows => { rows[3]["network"]["connectors"][1]["system"] = new JObject { ["state"] = "notAssigned" }; rows[1]["addresses"][0]["owners"] = new JArray(); }, true, SharedOwned);
Check("shared disconnect keeping its owner rejected", "network_io_assign", sharedDisconnect, rows => rows[3]["network"]["connectors"][1]["system"] = new JObject { ["state"] = "notAssigned" }, false, SharedOwned);
// MRP: the selected interface's one instance takes the requested role and domain; its name, ring ports and every other interface stay.
JArray Mrp(string role, string domain, string ring = "/P1") => new JArray(new JObject { ["name"] = "MRP1", ["domain"] = domain, ["role"] = role, ["ringPorts"] = new JArray(ring, "/P2") });
void MrpCaptured(JArray rows) { rows[2]["mrp"] = Mrp("NotInRing", "mrpdomain-1"); rows[3]["mrp"] = Mrp("NotInRing", "mrpdomain-1"); }
var mrp = new JObject { ["hardwarePath"] = controller, ["role"] = "Manager", ["domainName"] = "Ring" };
Check("MRP role and domain", "network_mrp_set", mrp, rows => rows[2]["mrp"] = Mrp("Manager", "Ring"), true, MrpCaptured);
Check("MRP wrong role rejected", "network_mrp_set", mrp, rows => rows[2]["mrp"] = Mrp("Client", "Ring"), false, MrpCaptured);
Check("MRP domain not joined rejected", "network_mrp_set", mrp, rows => rows[2]["mrp"] = Mrp("Manager", "mrpdomain-1"), false, MrpCaptured);
Check("MRP ring port change rejected", "network_mrp_set", mrp, rows => rows[2]["mrp"] = Mrp("Manager", "Ring", "/P3"), false, MrpCaptured);
Check("MRP collateral interface rejected", "network_mrp_set", mrp, rows => { rows[2]["mrp"] = Mrp("Manager", "Ring"); rows[3]["mrp"] = Mrp("Client", "mrpdomain-1"); }, false, MrpCaptured);
Check("MRP role keeps the domain when none is named", "network_mrp_set", new JObject { ["hardwarePath"] = controller, ["role"] = "Client" }, rows => rows[2]["mrp"] = Mrp("Client", "mrpdomain-1"), true, MrpCaptured);
Check("MRP role moving the domain unasked rejected", "network_mrp_set", new JObject { ["hardwarePath"] = controller, ["role"] = "Client" }, rows => rows[2]["mrp"] = Mrp("Client", "Ring"), false, MrpCaptured);
Check("MRP without a captured instance rejected", "network_mrp_set", mrp, _ => { }, false);
// The subnet's MRP domain names: only createDomain adds one, on every interface row of that subnet.
void Domains(JArray rows, params string[] names) { foreach (int i in new[] { 2, 3 }) rows[i]["mrpDomains"] = new JObject { ["subnet"] = "PN", ["domains"] = new JArray(names) }; }
var mrpCreate = (JObject)mrp.DeepClone(); mrpCreate["createDomain"] = true;
Check("MRP createDomain adds the domain name", "network_mrp_set", mrpCreate, rows => { rows[2]["mrp"] = Mrp("Manager", "Ring"); Domains(rows, "Ring", "mrpdomain-1"); }, true, rows => { MrpCaptured(rows); Domains(rows, "mrpdomain-1"); });
Check("MRP domain appearing without createDomain rejected", "network_mrp_set", mrp, rows => { rows[2]["mrp"] = Mrp("Manager", "Ring"); Domains(rows, "Ring", "mrpdomain-1"); }, false, rows => { MrpCaptured(rows); Domains(rows, "mrpdomain-1"); });
Check("MRP createDomain adding another domain rejected", "network_mrp_set", mrpCreate, rows => { rows[2]["mrp"] = Mrp("Manager", "Ring"); Domains(rows, "Other", "Ring", "mrpdomain-1"); }, false, rows => { MrpCaptured(rows); Domains(rows, "mrpdomain-1"); });
Check("MRP createDomain of an existing name rejected", "network_mrp_set", mrpCreate, rows => rows[2]["mrp"] = Mrp("Manager", "Ring"), false, rows => { MrpCaptured(rows); Domains(rows, "Ring"); });
Check("MRP createDomain without captured domains rejected", "network_mrp_set", mrpCreate, rows => rows[2]["mrp"] = Mrp("Manager", "Ring"), false, MrpCaptured);
// A port link joins two ring ports or two other ports; a role that would make one end a ring port and not the other is refused.
HardwareEngineeringRules.RequireRingLinks(new (string, bool, string, bool)[0]);
HardwareEngineeringRules.RequireRingLinks(new[] { ("A/P1", true, "B/P2", true), ("A/P2", false, "C/P1", false) });
count += 2;
foreach (var (label, link) in new[] { ("a ring port linked to another port", ("A/P1", true, "B/P1", false)), ("leaving the ring while linked to a ring port", ("A/P1", false, "B/P1", true)) })
{
    try { HardwareEngineeringRules.RequireRingLinks(new[] { ("A/P2", true, "C/P2", true), link }); throw new Exception("Ring link accepted: " + label); }
    catch (BridgeException error) when (error.Code == "NetworkPrerequisite" && error.Message.Contains("B/P1")) { count++; Console.WriteLine("PASS MRP role refused: " + label); }
}
// Shared-device access: the module takes the access, subitems may follow, ranges stay and owners stay among the station's I/O-system PLCs.
var access = new JObject { ["hardwarePath"] = module, ["accessType"] = "OutsideOfProject", ["addressControllerOwnerPaths"] = new JArray("/cpu1") };
void AccessBase(JArray rows) { WithChild(rows); rows[1]["attributes"]["SharedDeviceAccessType"] = "PLC"; rows[4]["attributes"]["SharedDeviceAccessType"] = "PLC"; rows[1]["addresses"][0]["owners"] = new JArray("/cpu1"); }
void Outside(JArray rows) { rows[1]["attributes"]["SharedDeviceAccessType"] = "OutsideOfProject"; rows[1]["addresses"][0]["owners"] = new JArray(); }
Check("shared access releases the module", "hardware_shared_device_access", access, rows => { Outside(rows); rows[4]["attributes"]["SharedDeviceAccessType"] = "OutsideOfProject"; }, true, AccessBase);
Check("shared access subitem may keep its access", "hardware_shared_device_access", access, Outside, true, AccessBase);
Check("shared access subitem taking another access rejected", "hardware_shared_device_access", access, rows => { Outside(rows); rows[4]["attributes"]["SharedDeviceAccessType"] = "Unknown"; }, false, AccessBase);
Check("shared access without readback rejected", "hardware_shared_device_access", access, rows => rows[1]["addresses"][0]["owners"] = new JArray(), false, AccessBase);
Check("shared access moving an address rejected", "hardware_shared_device_access", access, rows => { Outside(rows); rows[1]["addresses"][0]["startAddress"] = 8; }, false, AccessBase);
Check("shared access foreign owner rejected", "hardware_shared_device_access", access, rows => { Outside(rows); rows[1]["addresses"][0]["owners"] = new JArray("/cpu9"); }, false, AccessBase);
Check("shared access collateral rejected", "hardware_shared_device_access", access, rows => { Outside(rows); rows[0]["attributes"]["Comment"] = "lost"; }, false, AccessBase);
// PLC assigns the module to one PLC of the station through SharedDeviceAssignmentTarget (with SharedDeviceAccessConfigured); V21 derives the access.
var accessPlc = new JObject { ["hardwarePath"] = module, ["accessType"] = "PLC", ["assignmentTargetPath"] = "/cpu2", ["addressControllerOwnerPaths"] = new JArray("/cpu1", "/cpu2") };
void Inputs(JArray rows, string access, bool configured, string target) { rows[1]["attributes"]["SharedDeviceAccessType"] = access; rows[1]["attributes"]["SharedDeviceAccessConfigured"] = configured; rows[1]["attributes"]["SharedDeviceAssignmentTarget"] = target; }
Check("shared access moving a module between two PLCs rejected", "hardware_shared_device_access", accessPlc, rows => { Inputs(rows, "PLC", true, "/cpu2"); rows[1]["addresses"][0]["owners"] = new JArray("/cpu2"); }, false,
    rows => { AccessBase(rows); Inputs(rows, "PLC", true, "/cpu1"); });
Check("shared access to another target than requested rejected", "hardware_shared_device_access", accessPlc, rows => Inputs(rows, "PLC", true, "/cpu1"), false,
    rows => { AccessBase(rows); Inputs(rows, "OutsideOfProject", false, null); });
Check("shared access target change outside the subtree rejected", "hardware_shared_device_access", accessPlc, rows => { Inputs(rows, "PLC", true, "/cpu2"); rows[0]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu2"; }, false,
    rows => { AccessBase(rows); Inputs(rows, "PLC", true, "/cpu1"); rows[0]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu1"; });
// OutsideOfProject releases the subtree's addresses (hw4); PLC brings them back owned by the target, and nothing else may appear.
Check("shared access outside the project releases the addresses", "hardware_shared_device_access", access, rows => { Outside(rows); rows[1]["addresses"] = new JArray(); }, true, AccessBase);
Check("shared access back to a PLC restores its addresses", "hardware_shared_device_access", accessPlc,
    rows => { Inputs(rows, "PLC", true, "/cpu2"); rows[1]["addresses"] = new JArray(new JObject { ["ioType"] = "Input", ["startAddress"] = 3, ["lengthBits"] = 8, ["owners"] = new JArray("/cpu2") }); }, true,
    rows => { AccessBase(rows); Inputs(rows, "OutsideOfProject", false, null); rows[1]["addresses"] = new JArray(); });
void OutsideWithSub(JArray rows) { AccessBase(rows); Inputs(rows, "OutsideOfProject", false, null); rows[1]["addresses"] = new JArray(); rows[4]["attributes"]["SharedDeviceAssignmentTarget"] = null; rows[4]["attributes"]["SharedDeviceAccessConfigured"] = false; }
void BackToPlc(JArray rows) { Inputs(rows, "PLC", true, "/cpu2"); rows[1]["addresses"] = new JArray(new JObject { ["ioType"] = "Input", ["startAddress"] = 3, ["lengthBits"] = 8, ["owners"] = new JArray("/cpu2") }); }
Check("shared access subitem follows the requested target", "hardware_shared_device_access", accessPlc, rows => { BackToPlc(rows); rows[4]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu2"; rows[4]["attributes"]["SharedDeviceAccessConfigured"] = true; }, true, OutsideWithSub);
Check("shared access subitem taking another target rejected", "hardware_shared_device_access", accessPlc, rows => { BackToPlc(rows); rows[4]["attributes"]["SharedDeviceAssignmentTarget"] = "/cpu1"; }, false, OutsideWithSub);
Check("shared access subitem undeclaring an input rejected", "hardware_shared_device_access", accessPlc, rows => { BackToPlc(rows); ((JObject)rows[4]["attributes"]).Remove("SharedDeviceAccessConfigured"); }, false, OutsideWithSub);
Check("restored addresses owned by another PLC rejected", "hardware_shared_device_access", accessPlc,
    rows => { Inputs(rows, "PLC", true, "/cpu2"); rows[1]["addresses"] = new JArray(new JObject { ["ioType"] = "Input", ["startAddress"] = 3, ["lengthBits"] = 8, ["owners"] = new JArray("/cpu1") }); }, false,
    rows => { AccessBase(rows); Inputs(rows, "OutsideOfProject", false, null); rows[1]["addresses"] = new JArray(); });
// A shared submodule moving to the new PLC takes an address in that PLC's space (hw4); other items keep theirs.
Check("shared submodule takes a new address of the new PLC", "network_io_assign", shared, rows => { SharedMoved(rows); rows[1]["addresses"][0]["startAddress"] = 2; }, true, SharedBase);
Check("address move outside the shared submodules rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[5]["addresses"][0]["startAddress"] = 4; }, false, SharedOther);
Check("shared submodule resizing its address rejected", "network_io_assign", shared, rows => { SharedMoved(rows); rows[1]["addresses"][0]["lengthBits"] = 32; }, false, SharedBase);
// externalIoControllers sets the station interface's NumberOfExternalIoControllers in the same change, and only then may it change.
void AccessExternalBase(JArray rows) { AccessBase(rows); rows[3]["attributes"]["NumberOfExternalIoControllers"] = 0; }
var accessExternal = (JObject)access.DeepClone(); accessExternal["externalIoControllers"] = 1; accessExternal["ioDeviceInterfacePath"] = remote;
Check("shared access with external controllers", "hardware_shared_device_access", accessExternal, rows => { Outside(rows); rows[3]["attributes"]["NumberOfExternalIoControllers"] = 1; }, true, AccessExternalBase);
Check("shared access external count differing rejected", "hardware_shared_device_access", accessExternal, rows => { Outside(rows); rows[3]["attributes"]["NumberOfExternalIoControllers"] = 2; }, false, AccessExternalBase);
Check("external count changing unasked rejected", "hardware_shared_device_access", access, rows => { Outside(rows); rows[3]["attributes"]["NumberOfExternalIoControllers"] = 1; }, false, AccessExternalBase);
Check("external count on an uncaptured interface row rejected", "hardware_shared_device_access", accessExternal, rows => { Outside(rows); ((JObject)rows[3]).Remove("attributes"); }, false, rows => { AccessExternalBase(rows); ((JObject)rows[3]).Remove("attributes"); });
Check("count-only change of a module already outside the project", "hardware_shared_device_access", accessExternal, rows => rows[3]["attributes"]["NumberOfExternalIoControllers"] = 1,
    true, rows => { AccessExternalBase(rows); Outside(rows); });
if (HardwareEngineeringRules.RequireExternalIoControllers(1) != 1 || HardwareEngineeringRules.RequireExternalIoControllers(HardwareEngineeringRules.MaxExternalIoControllers) != 32) throw new Exception("External controller bounds differ.");
count += 2;
foreach (var token in new JToken[] { 0, -1, 33, int.MaxValue, 1.5, "2", JValue.CreateNull(), true })
{
    try { HardwareEngineeringRules.RequireExternalIoControllers(token); throw new Exception("externalIoControllers accepted: " + token); }
    catch (BridgeException error) when (error.Code == "InvalidRequest") { count++; }
}
Console.WriteLine("PASS externalIoControllers takes an integer from 1 to 32");
// Capability profiles: when the nearest profiled item has a profile, a write must be declared writable on the written item's row.
JArray Declared(params (string name, string access)[] rows) => new JArray(rows.Select(row => new JObject { ["name"] = row.name, ["accessMode"] = row.access, ["type"] = "System.String" }));
var profile = new JObject { ["typeIdentifier"] = "Catalog:DI", ["items"] = new JArray(
    new JObject { ["relativePath"] = "", ["attributes"] = Declared(("Comment", "ReadWrite"), ("Author", "Read")),
        ["channels"] = new JArray(new JObject { ["type"] = "Analog", ["ioType"] = "Input", ["number"] = 0, ["attributes"] = Declared(("Smoothing", "Read, Write")) }) },
    new JObject { ["relativePath"] = "items/{module}", ["attributes"] = Declared(("Comment", "Read, Write")),
        ["channels"] = new JArray(new JObject { ["type"] = "Digital", ["ioType"] = "Input", ["number"] = 0, ["attributes"] = Declared(("HardwareInterruptRisingEdgeActive", "ReadWrite")) }) }) };
var channel0 = new JObject { ["type"] = "Analog", ["ioType"] = "Input", ["number"] = 0 };
var digital0 = new JObject { ["type"] = "Digital", ["ioType"] = "Input", ["number"] = 0 };
HardwareEngineeringRules.RequireProfileWritable(null, null, null, new[] { "Anything" });
HardwareEngineeringRules.RequireProfileWritable(profile, "", null, new[] { "Comment" });
HardwareEngineeringRules.RequireProfileWritable(profile, "", channel0, new[] { "Smoothing" });
HardwareEngineeringRules.RequireProfileWritable(profile, "items/{module}", null, new[] { "Comment" });
HardwareEngineeringRules.RequireProfileWritable(profile, "items/{module}", digital0, new[] { "HardwareInterruptRisingEdgeActive" });
count += 5; Console.WriteLine("PASS profile admits declared writable attributes on the module and its submodule rows; an absent profile admits all");
foreach (var (label, path, selector, name) in new[] { ("read-only attribute", "", (JObject)null, "Author"), ("undeclared attribute", "", null, "Name"), ("channel attribute on the module", "", null, "Smoothing"),
    ("another channel", "", new JObject { ["type"] = "Analog", ["ioType"] = "Input", ["number"] = 1 }, "Smoothing"), ("undeclared channel attribute", "", channel0, "OperatingType"),
    ("a submodule row the profile does not list", "items/Other", null, "Comment"), ("a submodule attribute declared only on the module", "items/{module}", null, "Author") })
{
    try { HardwareEngineeringRules.RequireProfileWritable(profile, path, selector, new[] { name }); throw new Exception("Profile accepted " + label); }
    catch (BridgeException error) when (error.Code == "AttributeNotWritable") { count++; Console.WriteLine("PASS profile refuses " + label); }
}
if (HardwareEngineeringRules.ProfilePath(new string[0], "Di") != "" || HardwareEngineeringRules.ProfilePath(new[] { "Di", "PROFINET%20interface", "Port_1" }, "Di") != "items/{module}/items/PROFINET%20interface/items/Port_1")
    throw new Exception("Profile row paths differ.");
count++; Console.WriteLine("PASS profile row paths name the module's own segment {module}");
if (HardwareEngineeringRules.ProfileKey("OrderNumber:X/V1.0", "V9") != "OrderNumber:X/V1.0" || HardwareEngineeringRules.ProfileKey("System:Rail", null) != null || HardwareEngineeringRules.ProfileKey("GSD:F/D", "V1.0") != "GSD:F/D|V1.0")
    throw new Exception("Profile keys differ.");
count++; Console.WriteLine("PASS versionless identifiers select a profile only with the item's firmware");
if (HardwareEngineeringRules.ParseProfiles("{\"schemaVersion\":1,\"profiles\":[{\"typeIdentifier\":\"GSD:F/D\",\"firmwareVersion\":\"V1\"},{\"typeIdentifier\":\"GSD:F/D\",\"firmwareVersion\":\"V2\"}]}").Count != 2)
    throw new Exception("Two firmware profiles of one versionless type were not told apart.");
count++;
var gsdProfiles = new[] { new JObject { ["typeIdentifier"] = "GSD:F/D", ["firmwareVersion"] = "V1.0" } };
HardwareEngineeringRules.RequireVersionlessMatch(gsdProfiles, "GSD:F/D", "V1.0");
HardwareEngineeringRules.RequireVersionlessMatch(gsdProfiles, "GSD:Other/D", null);
count += 2;
foreach (var (label, firmware) in new[] { ("another firmware", "V2.0"), ("another firmware format", "1.0"), ("an unreadable firmware", (string)null) })
{
    try { HardwareEngineeringRules.RequireVersionlessMatch(gsdProfiles, "GSD:F/D", firmware); throw new Exception("Versionless profile mismatch accepted: " + label); }
    catch (BridgeException error) when (error.Code == "AttributeNotWritable") { count++; Console.WriteLine("PASS versionless profile fails closed on " + label); }
}
foreach (var (label, json) in new[] { ("duplicate type", "{\"schemaVersion\":1,\"profiles\":[{\"typeIdentifier\":\"OrderNumber:A/V1\"},{\"typeIdentifier\":\"OrderNumber:A/V1\"}]}"), ("other schema", "{\"schemaVersion\":2,\"profiles\":[]}"),
    ("missing type", "{\"schemaVersion\":1,\"profiles\":[{}]}"), ("versionless type without firmware", "{\"schemaVersion\":1,\"profiles\":[{\"typeIdentifier\":\"System:Rail\"}]}") })
{
    try { HardwareEngineeringRules.ParseProfiles(json); throw new Exception("Profile contract accepted: " + label); }
    catch (Exception error) when (error is InvalidOperationException || error is ArgumentException) { count++; Console.WriteLine("PASS profile contract refuses " + label); }
}
if (HardwareEngineeringRules.Profile("OrderNumber:6ES7 999-9XX99-9XX9/V9.9", null) != null || HardwareEngineeringRules.Profile(null, null) != null) throw new Exception("Bundled profiles answer an unknown type.");
count++; Console.WriteLine("PASS bundled profile contract loads");
// Every attribute write that verified evidence performed must stay admitted once its type has a bundled profile.
foreach (var (type, path, channelSelector, name, proof) in new[] {
    ("OrderNumber:6ES7 521-1BH00-0AB0/V1.0", "", (JObject)null, "Comment", "local.hardware_changes.20261028"), ("OrderNumber:6ES7 521-1BH00-0AB0/V1.0", "", null, "Author", "local.hardware_changes.20261028"),
    ("OrderNumber:6ES7 521-1BH00-0AB0/V2.0", "", null, "Comment", "local.hardware_changes.20261028"),
    ("OrderNumber:6ES7 221-1BH30-0XB0/V1.0", "", null, "Comment", "local.hardware_changes.families.20261029"), ("OrderNumber:6ES7 221-1BH30-0XB0/V1.0", "", null, "Author", "local.hardware_changes.families.20261029"),
    ("OrderNumber:6ES7 221-1BH32-0XB0/V2.0", "", null, "Comment", "local.hardware_changes.families.20261029"),
    ("OrderNumber:6ES7 131-6BF00-0BA0/V1.1", "", null, "Comment", "local.hardware_changes.topology.20261101"), ("OrderNumber:6ES7 131-6BF00-0BA0/V1.1", "", null, "Author", "local.hardware_changes.topology.20261101"),
    ("OrderNumber:6ES7 131-6BF00-0BA0/V1.1", "", null, "SharedDeviceAccessConfigured", "local.hardware_changes.topology.20261101"),
    ("OrderNumber:6ES7 131-6BF00-0BA0/V1.1", "", null, "SharedDeviceAssignmentTarget", "local.hardware_changes.topology.20261101"),
    ("OrderNumber:6ES7 131-6BF01-0BA0/V0.0", "", null, "Comment", "local.hardware_changes.topology.20261101"),
    ("OrderNumber:6ES7 511-1AL03-0AB0/V4.1", "items/PROFINET%20interface_1", null, "InterfaceOperatingMode", "local.gsd_transfer_areas.20261028"),
    ("OrderNumber:6ES7 521-1BH00-0AB0/V1.0", "items/{module}", digital0, "HardwareInterruptRisingEdgeActive", "local.plcsim_gaps.ob40.20261030") })
{
    try { HardwareEngineeringRules.RequireProfileWritable(HardwareEngineeringRules.Profile(type, null), path, channelSelector, new[] { name }); count++; }
    catch (BridgeException error) { throw new Exception("The bundled profile of " + type + " refuses " + name + ", which " + proof + " wrote: " + error.Message); }
}
Console.WriteLine("PASS bundled profiles admit every attribute write of the verified hardware evidence");
// Interrupted-apply project disposition: an equal fingerprint releases only when nothing was saved after the checkpoint.
var archived = new DateTime(2026, 10, 27, 13, 0, 0, DateTimeKind.Utc);
void Disposition(string name, bool equal, double savedSeconds, bool applied, string expected, string reason = null)
{
    string actual = AdmissionReconcileRules.ApplyProject(equal, archived.AddSeconds(savedSeconds), archived, applied, out string basis);
    if (actual != expected || reason != null && !basis.Contains(reason)) throw new Exception("Unexpected disposition: " + name + " -> " + (actual ?? "held") + ": " + basis);
    count++; Console.WriteLine("PASS " + name);
}
Disposition("unsaved mutation lost releases unchanged", true, -1, false, "unchanged");
Disposition("save after checkpoint holds an equal fingerprint", true, 0.5, false, null, "saved after its checkpoint");
Disposition("save at the archive time holds", true, 0, false, null, "saved after its checkpoint");
Disposition("save one tick after the archive holds", true, 0.0000001, false, null, "saved after its checkpoint");
Disposition("recorded applied outcome verifies", false, 5, true, "verifiedApplied");
Disposition("no-op applied change verifies", true, 5, true, "verifiedApplied");
Disposition("unsaved checkpoint wins over a recorded outcome", true, -1, true, "unchanged");
Disposition("differing project without outcome holds", false, 5, false, null, "differs from its checkpoint");
Disposition("differing unsaved project holds", false, -1, false, null, "differs from its checkpoint");
try { AdmissionReconcileRules.ApplyProject(true, DateTime.SpecifyKind(archived.AddSeconds(-1), DateTimeKind.Local), archived, false, out _); throw new Exception("Local save time accepted"); }
catch (ArgumentException) { count++; Console.WriteLine("PASS non-UTC save time refused"); }
foreach (var kind in new[] { "hmi_engineering", "hmi_text_list_import", "hmi_classic_import", "hmi_external_tag_create", "hmi_screen_create", "hmi_dynamization_create", "hardware_attributes", "tag_set", "constant_set", "to_create", "to_parameters", "to_delete" })
    if (AdmissionReconcileRules.OutcomeRecomputable(kind)) throw new Exception("Target kind treated as recomputable: " + kind); else count++;
foreach (var kind in new[] { "source_upsert", "hardware_plug", "network_configure", "tag_delete", "constant_delete", "plc_compile", "project_simulation_enable" })
    if (!AdmissionReconcileRules.OutcomeRecomputable(kind)) throw new Exception("Covered kind treated as target-dependent: " + kind); else count++;
// A failed apply journals what TIA said in MessageData/DetailMessageData, not only the generic Message.
var native = new Siemens.Engineering.EngineeringException("Error when calling method 'ImportFromXlsx'.", new InvalidOperationException("inner"))
{
    MessageData = new Siemens.Engineering.ExceptionMessageData { Text = "Import failed.", DetailText = new string('x', 3000) },
    DetailMessageData = Enumerable.Range(0, 20).Select(i => new Siemens.Engineering.ExceptionMessageData { Text = "The required worksheet 'TextList' is missing." }).ToList()
};
var chain = NativeFailureText.Chain(native);
if (chain.Count != 2 || (string)chain[0]["text"] != "Import failed." || ((string)chain[0]["detail"]).Length != 2048 || ((JArray)chain[0]["details"]).Count != 16
    || (string)chain[0]["details"][0]["text"] != "The required worksheet 'TextList' is missing." || (string)chain[1]["message"] != "inner" || chain[1]["details"] != null)
    throw new Exception("Native failure chain differs: " + chain);
count++; Console.WriteLine("PASS native failure chain keeps Siemens message data, bounded");
count += RuntimeRouteChecks.Run();
count += FParameterAllowlistChecks.Run();
count += VciResolutionChecks.Run();
count += TagAddressChecks.Run();
count += DriveObjectChecks.Run();
// A project-wide setting compiles every device: one PLC's compile left the other PLC inconsistent and its fingerprint unavailable (sl2).
foreach (var kind in new[] { "project_simulation_enable", "safety_import_xml", "safety_fparameter_set", "hardware_plug", "connection_create", "certificate_create", "opcua_server_settings_set", "network_mrp_set", "hardware_shared_device_access" })
    if (!CompileOnlyRules.CompilesEveryDevice(kind)) throw new Exception("Device-compiled kind compiles one PLC: " + kind); else count++;
foreach (var kind in new[] { "tag_create", "source_upsert", "to_create", "project_languages_set", "plc_compile" })
    if (CompileOnlyRules.CompilesEveryDevice(kind)) throw new Exception("PLC-compiled kind compiles every device: " + kind); else count++;
Console.WriteLine("PASS project_simulation_enable and the hardware/safety kinds compile every device");
Console.WriteLine($"{count} hardware preservation checks passed.");
