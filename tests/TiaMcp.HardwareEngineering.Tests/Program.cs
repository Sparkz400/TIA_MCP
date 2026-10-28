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
foreach (var kind in new[] { "hmi_engineering", "hardware_attributes", "tag_set", "constant_set", "to_create", "to_parameters", "to_delete" })
    if (AdmissionReconcileRules.OutcomeRecomputable(kind)) throw new Exception("Target kind treated as recomputable: " + kind); else count++;
foreach (var kind in new[] { "source_upsert", "hardware_plug", "network_configure", "tag_delete", "constant_delete", "plc_compile", "project_simulation_enable" })
    if (!AdmissionReconcileRules.OutcomeRecomputable(kind)) throw new Exception("Covered kind treated as target-dependent: " + kind); else count++;
Console.WriteLine($"{count} hardware preservation checks passed.");
