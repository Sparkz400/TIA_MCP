"""Qualify the runtime OPC UA, web and capture tools against the servers of an owned PLCSIM Advanced V8 CPU in the
operator's existing TCP/IP single-adapter mode (runtime real-server plan, Tasks 4 and 5).

Setup, every mode: an owned scratch CPU 1511-1 PN V4.1 at --cpu-ip with simulation support; an FB DiagGate (Active :=
Enable) with its instance DB, a global DB RealServer (Marker := 4711, Cycles counted up every scan, UaValue) and a Main
calling both; the scratch's runtime role gets OPC UA server access and the web rights to read and write process data,
read diagnostics and change the operating mode; the web server and the OPC UA server (Small license, automatic
client-certificate acceptance) are switched on; a saved review and a diagnostic case on DiagGate. A TCP/IP instance gets
the program (download, generated-state save, protection-credential provisioning); the OPC UA and web certificate pins
come from certificate_inspect after the last compile; the CPU runs and TIA closes. The OPC UA and web login is the
scratch's generated runtime user: its DPAPI password is copied into one run-owned Host credential vault, which the
concurrent UA and web acquisitions share, and retired at the end.

--mode normal: every runtime_ua_*, runtime_web_* and runtime_capture_* tool plus runtime_plcsim_bind. Stale pins are
  refused, UA and web values equal PLCSIM API reads, writes need their expected values, RUN/STOP through the Web API.
--mode host-loss-capture: the Host is terminated during a UA subscription and a web capture; a new Host reopens both
  committed prefixes, reports hostEndedRemoteUnknown, resumes nothing and refuses new sessions until acknowledgeUnknown.
--mode server-restart: STOP, power off, power on and RUN during a UA and a web capture; the server start time changes,
  both captures record the downtime, the old sessions stay dead and nothing reconnects.
--mode ua-write-loss | web-write-loss: a --runtime-qualification Host exits (74) after sending a write; a new Host
  reports the session unknown and refuses the retry; an explicit read through a fresh session, confirmed by the PLCSIM
  API, decides the outcome.
--mode ua-services: the scratch also holds a user-program server method (FB ServerMethod with OPC_UA_ServerMethodPre/Post
  V1.1, instance ServerMethod_DB) and a Program_Alarm (FB AlarmGate on RealServer.AlarmTrigger), with Alarms and Conditions
  on the OPC UA server. runtime_ua_call finds the method by browse, calls it (the PLCSIM API counts the calls) and is
  refused for a standard server method, a wrong argument count and a missing acknowledgement; the method's own Bad result
  is returned. runtime_ua_conditions with ConditionRefresh observes the alarm after a PLCSIM write raises it and after it
  falls. runtime_ua_history_probe records the server's HistoryRead boundary.
--mode device-inventory: device_online_browse and device_identify, which keep Windows TLS validation without a pin. The runner
  writes the instance's web certificate to web-server.cer in the run directory, prints TRUST_READY <path> <sha1> with the
  operator's import and removal commands, and waits up to 30 minutes for trust-command.json beside it ({"command": "trusted"} or
  {"command": "abort"}); it only reads Cert:\\CurrentUser\\Root. When the web server refuses Modules.Browse without a session, the
  runtime user logs in over the pinned connection and a TIA_INVENTORY_PLCSIM record of that token is in the next Host's
  environment only. That Host reopens the saved scratch for its device_inventory snapshot, browses and identifies; every result is
  compared field by field with pinned Modules.Browse and I&M0 reads of the same hardware IDs.

PLCSIM's network mode, adapters and IP addresses are operator settings and are never changed, nor are certificate stores.
verify_runtime_plcsim_tcpip checks the retained run; verification.json is written only when it passes.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import http.client
import json
import os
from pathlib import Path
import ssl
import subprocess
import threading
import time
import traceback
import uuid

import psutil

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_memory_card, verify_owned_instance
from library_native_harness import Harness, require
from smoke_mcp import Client
import verify_runtime_plcsim_tcpip as oracle
from verify_runtime_plcsim_tcpip import name

FB = '''FUNCTION_BLOCK "DiagGate"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Enable : Bool;
   END_VAR
   VAR_OUTPUT
      Active : Bool;
   END_VAR

BEGIN
\t#Active := #Enable;
END_FUNCTION_BLOCK

'''
DB = '''DATA_BLOCK "RealServer"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Marker : DInt := 4711;
      Cycles : DInt;
      UaValue : Int;
      MethodCalls : DInt;
      AlarmTrigger : Bool;
   END_VAR

BEGIN

END_DATA_BLOCK

'''
MAIN = '''ORGANIZATION_BLOCK "Main"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
BEGIN
    "DiagGate_DB"();
    "RealServer".Cycles := "RealServer".Cycles + 1;
%sEND_ORGANIZATION_BLOCK

'''
# ua-services only. The server method follows the V21 help's OPC_UA_ServerMethodPre/Post V1.1 example (both instructions
# called every cycle); OutInt := InInt + 1, OutBool := NOT InBool, and InInt 32767 is the method's own BadInvalidArgument.
SERVER_METHOD = '''FUNCTION_BLOCK "ServerMethod"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR DB_SPECIFIC
      UAMethod_InParameters : Struct
         InInt : Int;
         InBool : Bool;
      END_STRUCT;
      UAMethod_OutParameters : Struct
         OutInt : Int;
         OutBool : Bool;
      END_STRUCT;
   END_VAR
   VAR
      OPC_UA_ServerMethodPre_Instance {InstructionName := 'OPC_UA_ServerMethodPre'; LibVersion := '1.1'} : OPC_UA_ServerMethodPre;
      DONE_PRE : Bool;
      BUSY_PRE : Bool;
      ERROR_PRE : Bool;
      STATUS_PRE : DWord;
      UAMethod_Called : Bool;
      OPC_UA_ServerMethodPost_Instance {InstructionName := 'OPC_UA_ServerMethodPost'; LibVersion := '1.1'} : OPC_UA_ServerMethodPost;
      UAMethod_Result : DWord;
      UAMethod_Finished : Bool;
      DONE_POST : Bool;
      BUSY_POST : Bool;
      ERROR_POST : Bool;
      STATUS_POST : DWord;
   END_VAR

BEGIN
\t#OPC_UA_ServerMethodPre_Instance(Done => #DONE_PRE, Busy => #BUSY_PRE, Error => #ERROR_PRE, Status => #STATUS_PRE,
\t                                 UAMethod_Called => #UAMethod_Called, UAMethod_InParameters := #UAMethod_InParameters);
\tIF #UAMethod_Called AND NOT #ERROR_PRE THEN
\t    IF #UAMethod_InParameters.InInt = 32767 THEN
\t        #UAMethod_Result := DW#16#80AB0000;
\t    ELSE
\t        #UAMethod_OutParameters.OutInt := #UAMethod_InParameters.InInt + 1;
\t        #UAMethod_OutParameters.OutBool := NOT #UAMethod_InParameters.InBool;
\t        #UAMethod_Result := 0;
\t        "RealServer".MethodCalls := "RealServer".MethodCalls + 1;
\t    END_IF;
\t    #UAMethod_Finished := TRUE;
\tEND_IF;
\t#OPC_UA_ServerMethodPost_Instance(UAMethod_Result := #UAMethod_Result, UAMethod_Finished := #UAMethod_Finished, Done => #DONE_POST,
\t                                  Busy => #BUSY_POST, Error => #ERROR_POST, Status => #STATUS_POST, UAMethod_OutParameters := #UAMethod_OutParameters);
\tIF #DONE_POST OR #ERROR_POST THEN
\t    #UAMethod_Finished := FALSE;
\t    #UAMethod_Result := 0;
\tEND_IF;
END_FUNCTION_BLOCK

'''
# The Program_Alarm form that compiled in local.alarm_imports.20261028; its alarm text and class stay TIA's defaults.
ALARM_GATE = '''FUNCTION_BLOCK "AlarmGate"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR
      Overtemperature : Program_Alarm;
   END_VAR

BEGIN
\t#Overtemperature(SIG := "RealServer".AlarmTrigger);
END_FUNCTION_BLOCK

'''
SERVICES_MAIN = '    "ServerMethod_DB"();\n    "AlarmGate_DB"();\n'
HYPOTHESES = [
    {"id": "follows", "statement": "Active is true whenever Enable is true.",
     "prediction": {"when": [{"signalId": "enable", "op": "eq", "value": True}], "expect": [{"signalId": "active", "op": "eq", "value": True}]}},
    {"id": "releases", "statement": "Active is false whenever Enable is false.",
     "prediction": {"when": [{"signalId": "enable", "op": "eq", "value": False}], "expect": [{"signalId": "active", "op": "eq", "value": False}]}},
]
UNITS = {"enable": "1", "active": "1"}
# Device function rights of a CPU 1511-1 PN V4.1 in V21 Update 3 (listed by a refused umac_role_set grant on 28 October 2026).
RIGHTS = ("OPCUAServerAccess", "WebReadTags", "WebWriteTags", "WebAllowDiagnostics", "WebChangeOperatingMode")
ENABLE = {"serverEnabled": {"expected": False, "value": True}, "runtimeLicense": {"expected": "NoLicense", "value": "SimaticOpcUaS71500Small"}}
AUTO_ACCEPT = {"autoAcceptClientCertificates": {"expected": False, "value": True}}
ALARMS_AND_CONDITIONS = {"alarmsAndConditions": {"expected": False, "value": True}}
SERVER = {"namespaceUri": "http://opcfoundation.org/UA/", "identifierType": "numeric", "identifier": "2253"}
GET_MONITORED_ITEMS = {"namespaceUri": "http://opcfoundation.org/UA/", "identifierType": "numeric", "identifier": "11492"}
UA_NS = "http://opcfoundation.org/UA/"
OBJECTS = {"namespaceUri": UA_NS, "identifierType": "numeric", "identifier": "85"}
START_TIME = {"namespaceUri": UA_NS, "identifierType": "numeric", "identifier": "2257"}
# member: (owning DB, PLCSIM API type); the UA nodes and web symbols name the same tags.
SIGNALS = {"Marker": ("RealServer", "int32"), "Cycles": ("RealServer", "int32"), "UaValue": ("RealServer", "int16"),
           "MethodCalls": ("RealServer", "int32"), "AlarmTrigger": ("RealServer", "bool"),
           "Enable": ("DiagGate_DB", "bool"), "Active": ("DiagGate_DB", "bool")}
ENTROPY = b"TiaMcp.V21.ScratchRuntimeUser.v1"
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance in the operator's TCP/IP single-adapter mode (PLCSIM Virtual Ethernet Adapter) of an owned scratch "
    "CPU 1511-1 PN V4.1; physical PLCs and other CPU or firmware profiles are not covered.",
    "The OPC UA and web login is the scratch's generated runtime user (UMAC, password from its CurrentUser DPAPI file into a run-owned vault); guest access, "
    "project users made by an operator and UMC are not exercised.",
    "Power-on needs the provisioned protection credential, provisioned with first-use TLS trust under the owned-empty-CPU policy.",
    "Scalar DInt, Int and Bool tags only; arrays and structures are not exercised. ua-services adds one user-program server method with "
    "Int/Bool arguments and one Program_Alarm with TIA's default text and class; Acknowledge, AddComment and history access are not product routes.",
]
INVENTORY_LIMITATION = ("Web API module configuration and I&M0 of one PLCSIM CPU 1511 only, through the operator's temporary CurrentUser\\Root trust "
                        "of that instance's self-signed web certificate; physical CPUs, distributed I/O and other families are not covered.")
SESSION = "TIA_INVENTORY_PLCSIM"  # sessionEnvironmentVariable of the device_* tools; the record lives in the inventory Host's environment only.
TRUST_WAIT = 1800
REMOVE = "Remove-Item Cert:\\CurrentUser\\Root\\%s"


def unprotect(path):
    """The scratch runtime user's generated password (CurrentUser DPAPI). Held in memory only, never written or logged."""
    class Blob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]
    data = Path(path).read_bytes()
    buffers = [ctypes.create_string_buffer(b, len(b)) for b in (data, ENTROPY)]
    source, entropy = (Blob(len(b), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char))) for b, buffer in zip((data, ENTROPY), buffers))
    output = Blob()
    require(ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(source), None, ctypes.byref(entropy), None, None, 0, ctypes.byref(output)),
            "DPAPI refused the runtime user's password file")
    try:
        return ctypes.string_at(output.pbData, output.cbData).decode("utf-8")
    finally:
        ctypes.windll.kernel32.LocalFree(ctypes.cast(output.pbData, ctypes.c_void_p))


def ua_node(member=None, block="RealServer"):
    return {"namespaceUri": oracle.SIEMENS_NS, "identifierType": "string", "identifier": '"%s"' % block + ('."%s"' % member if member else "")}


def symbol(member):
    return '"%s".%s' % (SIGNALS[member][0], member)


class Runner(Harness):
    """Harness steps across several Hosts on one run directory; every Host gets an exit proof."""

    def __init__(self, args):
        args.artifacts, args.runner = args.root, __file__
        super().__init__(args, "rs_", "Runtime UA, web and capture tools on an owned PLCSIM Advanced V8 CPU in TCP/IP mode (%s)" % args.mode,
                         LIMITATIONS + ([INVENTORY_LIMITATION] if args.mode == "device-inventory" else []), [])
        self.report["simWorker"] = {"path": str(args.sim_worker.resolve()), "sha256": hashlib.sha256(args.sim_worker.read_bytes()).hexdigest()}
        self.report.update(mode=args.mode, cpuIp=args.cpu_ip, hosts=[], comparisons=[], pins={}, attempts={"ua": [], "web": []})
        self.calls, self.credentials, self.password, self.selections, self.watched = [], [], None, {}, []
        self.token = self.thumbprint = None  # device-inventory: the borrowed Web API token (memory only) and the certificate the operator trusts
        # One vault for both channels: concurrent UA and web users must take turns on it (runs rs_abde1bb3 and rs_db5b4b21
        # failed while the vault refused a concurrent user at once).
        self.vault = uuid.uuid4().hex

    def persist(self):
        self.report["toolCalls"] = self.calls + (self.client.tool_calls if self.client else [])
        (self.directory / "qualification.json").write_text(json.dumps(self.report, indent=2), encoding="utf-8")

    def start_host(self, label, *extra, adopt=True):
        self.client = Client(self.args.host.resolve(), self.args.timeout, ["--worker", str(self.args.worker.resolve()), "--artifacts", str(self.directory),
                             "--sim-worker", str(self.args.sim_worker.resolve()), *extra])
        self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "runtime-real-server", "version": "1"}})
        self.client.notify("notifications/initialized")
        self.report["hosts"].append({"label": label, "pid": self.client.process.pid, "arguments": list(extra), "expectedExitCode": 0, "children": []})
        self.persist()
        if adopt:  # The instance outlives the Host that created it; its receipt lets this Host's simulator worker own it.
            self.stage("adopt the owned instance in Host " + label, lambda: self.tool("sim_select", {
                "instanceId": self.instance["id"], "expectedName": self.instance["name"], "creationId": self.creation})["instance"])

    def watch(self):
        """The current Host's descendants, taken before a fault, so that their exits can be proven after it."""
        self.watched = psutil.Process(self.client.process.pid).children(recursive=True)
        self.report["hosts"][-1]["children"] = [{"pid": p.pid, "name": p.name(), "createTime": p.create_time()} for p in self.watched]
        self.persist()

    def close_host(self, expected=0, kill=False):
        row, client = self.report["hosts"][-1], self.client
        row["expectedExitCode"] = expected
        if kill:
            client.process.kill()  # This run's own Host only; its workers see the closed pipe and exit by themselves.
        if expected:
            client.process.wait(timeout=120)
            gone, _ = psutil.wait_procs(self.watched, timeout=420)
            for child in row["children"]:
                child["exitVerified"] = any(p.pid == child["pid"] for p in gone)
        receipt = client.close()
        row["exit"] = {k: receipt[k] for k in ("hostExitCode", "forcedHostStop", "workerExits", "stderrEofObserved")}
        row["logs"] = client.errors
        self.calls += client.tool_calls
        self.client = None
        self.persist()
        return row["exit"]

    def refresh_tags(self):
        """Refresh PLCSIM's tag list through sim_tags and resolve every symbol. After a restart or power cycle PLCSIM reads the
        old list as NotUpToDate (-47) until it is refreshed (observed 29 October 2026)."""
        self.symbols = {}
        for member, (block, _) in SIGNALS.items():
            tags = self.tool("sim_tags", {"instanceId": self.instance["id"], "filter": block, "maxItems": 2000})["tags"]
            found = [tag["symbol"] for tag in tags if tag["symbol"].replace('"', "") == block + "." + member]
            require(len(found) == 1, "PLCSIM symbol %s.%s is missing or ambiguous" % (block, member))
            self.symbols[member] = found[0]
        return self.symbols

    def plcsim(self, member):
        """One PLCSIM API read. A bad quality is a failure only after one tag-list refresh; the first read is kept beside it."""
        def read():
            row = self.tool("sim_read", {"instanceId": self.instance["id"], "signals": [{"symbol": self.symbols[member], "type": SIGNALS[member][1]}]})["values"][0]
            return {k: row.get(k) for k in ("symbol", "quality", "value")}
        row = read()
        if row["quality"] != "good":
            self.refresh_tags()
            row = {**read(), "beforeTagRefresh": row}
        return row

    def credential(self, label, binding, vault):
        """Store the runtime user's password under one public binding in this run's vault, through the Host's credential CLI."""
        folder = self.directory / "credentials"
        folder.mkdir(exist_ok=True)
        path = folder / (label + ".binding.json")
        path.write_text(json.dumps(binding), encoding="utf-8")
        created = subprocess.run([str(self.args.host.resolve()), "--credential-store", "create", "--vault-name", vault, "--binding", str(path), "--stdin"],
                                 input=json.dumps(self.password).encode("utf-8"), capture_output=True, timeout=60)
        echoed = self.password.encode("utf-8") in created.stdout + created.stderr
        require(created.returncode == 0 and not echoed, "Credential %s was not created: %s" % (label, "<withheld>" if echoed else created.stderr.decode(errors="replace")[-300:]))
        reference = json.loads(created.stdout)["reference"]
        (folder / (label + ".reference.json")).write_text(json.dumps(reference), encoding="utf-8")
        self.credentials.append((label, vault))
        return reference

    def retire(self):
        rows = self.report.setdefault("credentialRetirement", [])
        for label, vault in self.credentials:
            folder = self.directory / "credentials"
            done = subprocess.run([str(self.args.host.resolve()), "--credential-store", "retire", "--vault-name", vault, "--binding", str(folder / (label + ".binding.json")),
                                   "--reference", str(folder / (label + ".reference.json"))], capture_output=True, timeout=60)
            rows.append({"credential": label, "exitCode": done.returncode})

    def leaked(self):
        """Retained text files that contain the runtime user's password or the borrowed Web API token; none may."""
        secrets = [s.encode("utf-8") for s in (self.password, self.token) if s]
        if not secrets:
            return []
        return [str(p) for p in self.directory.rglob("*") if p.is_file() and p.suffix.lower() in (".json", ".jsonl", ".csv", ".md", ".html", ".txt", ".log")
                and p.stat().st_size < 64 << 20 and any(s in p.read_bytes() for s in secrets)]

    def run(self, body):
        failure = None
        try:
            self.start_host("setup", adopt=False)
            body(self)
        except Exception as error:
            failure = error
            self.report["failure"], self.report["traceback"] = str(error), traceback.format_exc()
        finally:
            if self.client is not None:
                try:
                    self.client.tool("tia_detach")
                except Exception as error:
                    self.report["detachFailure"] = str(error)
                self.close_host()
            self.retire()
            self.report["secretLeaks"] = self.leaked()
            self.report["completed"] = failure is None and not self.report["secretLeaks"] and all(r["exitCode"] == 0 for r in self.report["credentialRetirement"])
            self.persist()
            print("Report: " + str(self.directory / "qualification.json"), flush=True)
        if failure is not None:
            raise failure
        result = self.report["oracle"] = oracle.verify(self.directory)
        self.persist()
        require(result["completed"], "The oracle refused the run: %s" % result["issues"])
        (self.directory / "verification.json").write_text(json.dumps({"schemaVersion": 1, "kind": "runtime-plcsim-tcpip", "mode": self.args.mode,
            "completed": True, "steps": [s["name"] for s in self.report["steps"]], "toolCalls": self.report["toolCalls"], "oracle": result,
            "hosts": [{k: h[k] for k in ("label", "pid", "expectedExitCode", "exit")} for h in self.report["hosts"]]}, indent=2), encoding="utf-8")
        print("Verification: " + str(self.directory / "verification.json"), flush=True)


def change(r, kind, payload, plc=True):
    prepared = r.stage("prepare " + kind, lambda: r.tool("change_prepare", {"kind": kind, "payload": payload, **({"plcPath": r.plc} if plc else {})}))
    return r.apply_prepared(kind, prepared)


def certificates(r, label):
    return r.stage(label, lambda: r.tool("certificate_inspect", {"maxItems": 500}))


def store(inspected):
    return {c["sha256"]: c for d in inspected["devices"] for c in d["certificates"] if c.get("sha256")}


def setup(r):
    t = r.tool
    r.tia_before = r.stage("TIA processes before the run", lambda: {str(p["processId"]): p.get("projectPath") for p in t("tia_discover")["processes"]})
    r.plc = r.owned_scratch("real server")
    r.cpu = r.plc.rsplit("/plc/", 1)[0]
    cpus = r.scratch["scratchSecurity"]["cpus"]
    require(len(cpus) == 1 and cpus[0]["runtimeUser"].get("supported") is True, "Exactly one scratch CPU with a generated runtime user is required")
    r.secret = cpus[0]["credentialMetadataPath"]
    user = json.loads(Path(cpus[0]["runtimeUser"]["credentialMetadataPath"]).read_text(encoding="utf-8"))
    r.username, r.protected = user["username"], user["protectedPath"]

    def address():
        interface = r.cpu + "/items/PROFINET%20interface_1"
        rows = t("hardware_inspect", {"hardwarePath": interface, "maxItems": 50})["items"]
        nodes = rows[0]["network"]["nodes"] if len(rows) == 1 else []
        require(len(nodes) == 1, "Expected one network node on %s: %s" % (interface, nodes))
        current = {a["name"]: a.get("value") for a in nodes[0]["attributes"]}.get("Address")
        if current != r.args.cpu_ip:
            change(r, "network_configure", {"hardwarePath": interface, "nodeName": nodes[0]["name"], "attributes": {"Address": r.args.cpu_ip}})
        return {"before": current, "after": r.args.cpu_ip}
    r.report["cpuAddress"] = r.stage("CPU address " + r.args.cpu_ip, address)

    r.apply("project_simulation_enable", {"enabled": True})
    change(r, "source_upsert", {"language": "SCL", "source": FB, "expectedBlockNames": ["DiagGate"]})
    change(r, "instance_db_create", {"name": "DiagGate_DB", "instanceOfName": "DiagGate"})
    change(r, "source_upsert", {"language": "SCL", "source": DB, "expectedBlockNames": ["RealServer"]})
    services = r.args.mode == "ua-services"
    if services:
        for block, source in (("ServerMethod", SERVER_METHOD), ("AlarmGate", ALARM_GATE)):
            change(r, "source_upsert", {"language": "SCL", "source": source, "expectedBlockNames": [block]})
            change(r, "instance_db_create", {"name": block + "_DB", "instanceOfName": block})
    change(r, "source_upsert", {"language": "SCL", "source": MAIN % (SERVICES_MAIN if services else ""), "expectedBlockNames": ["Main"]})

    umac = r.stage("access_inspect", lambda: t("access_inspect", {"maxItems": 2000}))["projectUmac"]
    roles, scopes = [n for n in umac["roleNames"].values() if n.startswith("TiaMcpRuntime_")], umac["deviceRightScopes"]
    require(len(roles) == 1 and len(scopes) == 1, "Expected one scratch runtime role and one UMAC device: %s %s" % (roles, scopes))
    change(r, "umac_role_set", {"action": "grant", "roleName": roles[0], "deviceRights": [{"device": scopes[0], "identifier": i} for i in RIGHTS]}, plc=False)

    r.before_servers = certificates(r, "certificate_inspect before the servers")
    # V21 turns the system pages on with the server (29 October 2026); activation alone fails its readback, so both are named.
    change(r, "webserver_page_set", {"webServerActive": {"expected": False, "value": True}, "systemWebPages": {"expected": False, "value": True}})
    change(r, "opcua_server_settings_set", ENABLE)
    change(r, "opcua_server_settings_set", {**AUTO_ACCEPT, **(ALARMS_AND_CONDITIONS if services else {})})
    if services:  # A&C needs central alarm management in the PLC: record the CPU's alarm attributes, never change them.
        r.report["cpuAlarmAttributes"] = r.stage("CPU alarm attributes", lambda: {a["name"]: a.get("value") for a in t("hardware_inspect", {
            "hardwarePath": r.cpu, "maxItems": 2000})["items"][0]["attributes"] if "alarm" in a["name"].lower()})

    def application_uri():
        settings = t("opcua_engineering_inspect", {"plcPath": r.plc, "maxItems": 2000})["serverSettings"]
        server = [row["hardwarePath"] for row in settings if any(a["name"] == "OpcUaServer" for a in row["attributes"])]
        require(len(server) == 1, "Expected one OPC UA sub-item: %s" % server)
        rows = t("hardware_inspect", {"hardwarePath": server[0], "maxItems": 2000})["items"]
        values = {a["name"]: a.get("value") for a in rows[0]["attributes"]}
        require(values.get("OpcUaServer") is True and values.get("OpcUaAutoAcceptAllClientCertificates") is True and values.get("OpcUaPort") == 4840
                and (not services or values.get("OpcUaAlarmsAndConditionsEnable") is True),
                "The OPC UA server settings differ: %s" % {k: v for k, v in values.items() if k.startswith("OpcUa")})
        return values["OpcUaApplicationUri"]
    r.app_uri = r.stage("OPC UA application URI", application_uri)
    r.servers_on = certificates(r, "certificate_inspect with the servers on")

    r.review = r.stage("save project review", lambda: t("project_snapshot_capture", {"request": {"maxBlocks": 25}}))["reviewId"]
    fb, db = r.plc + "/blocks/DiagGate", r.plc + "/blocks/DiagGate_DB"
    signals = [{"id": member.lower(), "role": role, "reason": "The instance's %s the hypotheses relate." % member, "hypothesisIds": ["follows", "releases"],
                "storagePath": db, "memberPath": [member]} for member, role in (("Enable", "command"), ("Active", "output"))]
    case = r.stage("diagnostic_plan", lambda: t("diagnostic_plan", {"request": {"reviewId": r.review, "report": "DiagGate's Active output should follow its Enable input.",
                   "blockPath": fb, "instance": {"dbPath": db, "memberPath": []}, "signals": signals, "hypotheses": HYPOTHESES}}))
    require([(s["id"], s["resolution"]) for s in case["plan"]["signals"]] == [("enable", "capturedDeclaration"), ("active", "capturedDeclaration")],
            "Case signals did not resolve to captured declarations")
    r.case = case["id"]

    r.creation = uuid.uuid4().hex
    created = r.stage("sim_create TCPIP", lambda: t("sim_create", {"cpuType": "CPU1511", "creationId": r.creation, "communicationInterface": "TCPIP"}))
    r.instance = verify_owned_instance(created["instance"])
    require(r.instance.get("operatingState") == "Off", "A fresh owned OFF instance is required")
    # The ticket expires 15 minutes after sim_create; download, save and provisioning must start before then.
    ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": r.secret}
    download = r.stage("simulation_download", lambda: t("simulation_download", ticket))
    verify_download(download, r.instance)
    r.report["memoryCard"] = verify_memory_card(download)
    r.stage("save the generated state", lambda: save_generated_scratch_project(t, download)["applied"]["state"])
    provisioned = r.stage("simulation_provision_scratch_credential", lambda: t("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True}))
    r.report["credentialProvisioning"] = verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
    r.report["restoredMemoryCard"] = verify_memory_card(download)

    def pins():
        before = store(r.before_servers)
        def servers(certs):
            """The OPC UA server certificate (SAN URI = application URI) and the web server certificates the servers added."""
            return {"ua": [s for s, c in certs.items() if {"type": "Uri", "value": r.app_uri} in (c.get("subjectAlternativeNames") or [])],
                    "web": [s for s, c in certs.items() if s not in before and "webserver" in ((c.get("subjectCommonName") or "") + (c.get("subject") or "")).lower()]}
        final_store = store(certificates(r, oracle.PIN_STEP))
        final, earlier = servers(final_store), servers(store(r.servers_on))
        for channel in ("ua", "web"):  # A store that keeps a certificate a later compile replaced: the one the later compiles added is served.
            if len(final[channel]) > 1:
                final[channel] = [s for s in final[channel] if s not in earlier[channel]]
        require(len(final["ua"]) == len(final["web"]) == 1, "Expected one OPC UA and one new web server certificate: %s" % {
            s: [c.get("subjectCommonName"), c.get("subjectAlternativeNames")] for s, c in final_store.items()})
        r.report["pins"] = {channel: {"sha256": final[channel][0], "step": oracle.PIN_STEP} for channel in ("ua", "web")}
        # A certificate a compile replaced is the stale pin; otherwise the other server's certificate of this CPU stands in.
        stale = {}
        for channel, other in (("ua", "web"), ("web", "ua")):
            replaced = [s for s in earlier[channel] if s != final[channel][0]]
            stale[channel] = {"sha256": replaced[0], "source": "beforeTheLastCompile"} if replaced else {"sha256": final[other][0], "source": "otherServerOfThisCpu"}
        r.report["stalePins"] = stale
        return {"pins": r.report["pins"], "stalePins": stale}
    r.stage("pin the server certificates", pins)

    r.stage("power on", lambda: t("sim_state", {"instanceId": r.instance["id"], "action": "powerOn", "timeoutMs": 60000})["instance"]["operatingState"])
    running = r.stage("run", lambda: t("sim_state", {"instanceId": r.instance["id"], "action": "run", "timeoutMs": 30000}))
    require(running["instance"]["operatingState"] == "Run", "The instance did not enter RUN")

    def resolve():
        r.refresh_tags()
        require(r.plcsim("Marker")["value"] == 4711, "Marker does not start at 4711")
        return r.symbols
    r.stage("resolve the PLCSIM symbols", resolve)
    r.stage("tia_detach", lambda: t("tia_detach"))

    def preserved():
        after = {str(p["processId"]): p.get("projectPath") for p in t("tia_discover")["processes"]}
        require(all(after.get(pid) == path for pid, path in r.tia_before.items()), "A preexisting TIA process or its project changed")
        return after
    r.stage("preexisting TIA processes are unchanged", preserved)
    r.password = unprotect(r.protected)


def bind(r):
    bound = r.stage("runtime_plcsim_bind", lambda: r.tool("runtime_plcsim_bind", {"instanceId": r.instance["id"], "instanceName": r.instance["name"], "creationId": r.creation}))
    require(bound["record"].get("communicationInterface") == "TCPIP" and r.args.cpu_ip in bound["record"]["controllerAddresses"],
            "The binding is not a TCP/IP binding at %s: %s" % (r.args.cpu_ip, bound["record"]))
    r.binding = bound["binding"]["sha256"]


def compare(r, channel, label, session, member):
    """One channel read and the PLCSIM API read of the same tag, retained raw for the oracle."""
    def run():
        read = (r.tool("runtime_ua_read", {"attemptId": session, "nodes": [ua_node(member)]})["items"][0] if channel == "ua"
                else r.tool("runtime_web_read", {"attemptId": session, "selectionId": r.selections[member]}))
        row = {"label": label, "channel": channel, "member": member, "channelRead": read, "plcsimRead": r.plcsim(member)}
        r.report["comparisons"].append(row)
        require(oracle.same(oracle.channel_value(channel, read), row["plcsimRead"]["value"]), "%s differs from the PLCSIM API read: %s" % (label, row))
        return row
    return r.stage(label, run)


def ua_selection(r, pin=None, anonymous=False):
    selection = {"endpoint": "opc.tcp://%s:4840/" % r.args.cpu_ip, "applicationUri": r.app_uri, "certificateSha256": pin or r.report["pins"]["ua"]["sha256"]}
    if not anonymous:
        selection.update(authentication="username", user=r.username, vaultName=r.vault)
        if getattr(r, "ua_credential", None):
            selection["credential"] = r.ua_credential
    return selection


def web_login(r, pin=None, credential=None):
    return {"endpoint": "https://" + r.args.cpu_ip, "peerCertificateSha256": pin or r.report["pins"]["web"]["sha256"], "user": r.username, "mode": "local",
            "vaultName": r.vault, "credential": credential or r.web_credential}


def credentials(r):
    """The runtime user's vault credentials for the pinned UA and web endpoints."""
    r.ua_credential = None
    r.ua_credential = r.credential("ua", r.stage("runtime_ua_credential_binding", lambda: r.tool("runtime_ua_credential_binding", {"selection": ua_selection(r)})), r.vault)
    def web(pin):
        return r.tool("runtime_web_credential_binding", {"endpoint": "https://" + r.args.cpu_ip, "peerCertificateSha256": pin, "user": r.username, "mode": "local"})
    r.web_credential = r.credential("web", r.stage("runtime_web_credential_binding", lambda: web(r.report["pins"]["web"]["sha256"])), r.vault)
    r.stale_web_credential = r.credential("web-stale", r.stage("runtime_web_credential_binding for the stale pin", lambda: web(r.report["stalePins"]["web"]["sha256"])),
                                          r.vault)


def connect(r, channel, label, argument, connected=True):
    attempt = uuid.uuid4().hex
    r.report["attempts"][channel].append(attempt)
    r.persist()
    tool, key = ("runtime_ua_connect", "selection") if channel == "ua" else ("runtime_web_connect", "login")
    status = r.stage(label, lambda: r.tool(tool, {"attemptId": attempt, key: argument}))
    require((status["state"] == "connected" and status["peerVerified"]) if connected else (status["state"] == "notConnected" and not status["peerVerified"]),
            "%s: unexpected session state %s (%s)" % (label, status["state"], status.get("errorCode")))
    return attempt


def refused(r, label, tool, arguments, code=None):
    def check():
        error = r.call_error(tool, arguments)
        require(code is None or error.get("code") == code, "%s: expected %s, observed %s" % (label, code, error.get("code")))
        return error
    return r.stage(label, check)


def select(r, session):
    r.selections = {}
    for member in SIGNALS:
        r.selections[member] = r.stage("runtime_web_browse " + symbol(member), lambda m=member: r.tool("runtime_web_browse", {
            "attemptId": session, "symbol": symbol(m), "children": False}))["selectionId"]


ACQUIRE = {"subscription": "runtime_ua_subscription_", "ua": "runtime_ua_capture_", "web": "runtime_capture_"}


def acquire(r, label, kind, request, stop_after=None, wait=True):
    """Start an acquisition; wait for its end, or stop it after stop_after seconds. wait=False returns once it has two rows."""
    capture, prefix = uuid.uuid4().hex, ACQUIRE[kind]
    def run():
        status = r.tool(prefix + "start", {"captureId": capture, "request": request})
        deadline = time.monotonic() + (stop_after or request["durationSeconds"] + 60)
        while status["state"] == "running" and time.monotonic() < deadline and (wait or status["recordedProgress"]["rows"] < 2):
            time.sleep(0.5)
            status = r.tool(prefix + "status", {"captureId": capture})
        if wait and status["state"] == "running":
            status = r.tool(prefix + "stop", {"captureId": capture})
        return {**status, "captureId": capture, "request": request}
    return r.stage(label, run)


def retained(r, label, kind, status, rows=2):
    prefix = ACQUIRE[kind]
    require(status["recordedProgress"]["rows"] >= rows, "%s retained %s rows" % (label, status["recordedProgress"]["rows"]))
    page = r.stage(prefix + "page: " + label, lambda: r.tool(prefix + "page", {"snapshotRef": status["snapshotRef"], "count": 200}))
    def export():
        exported = r.tool(prefix + "export", {"snapshotRef": status["snapshotRef"], "format": "json"})
        for artifact in exported["artifacts"]:
            require(hashlib.sha256(Path(artifact["path"]).read_bytes()).hexdigest() == artifact["sha256"], "Export file differs from its receipt")
        return exported
    r.stage(prefix + "export: " + label, export)
    return page


def normal(r):
    t = r.tool
    bind(r)
    r.stage("runtime_ua_client_identity", lambda: t("runtime_ua_client_identity")["certificateSha256"])
    credentials(r)
    connect(r, "ua", "runtime_ua_connect with a stale pin", ua_selection(r, r.report["stalePins"]["ua"]["sha256"], anonymous=True), connected=False)
    ua = connect(r, "ua", "runtime_ua_connect", ua_selection(r))
    r.stage("runtime_ua_browse ObjectsFolder", lambda: t("runtime_ua_browse", {"attemptId": ua, "node": OBJECTS})["references"])
    def members():
        names = {n for ref in t("runtime_ua_browse", {"attemptId": ua, "node": ua_node()})["references"] for n in (ref.get("browseName"), ref.get("displayName"))}
        require({"Marker", "Cycles", "UaValue"} <= names, "RealServer lacks its members: %s" % names)
        return sorted(n for n in names if n)
    r.stage("runtime_ua_browse RealServer", members)
    compare(r, "ua", "Marker read through UA", ua, "Marker")
    compare(r, "ua", "UaValue before the UA write", ua, "UaValue")
    write = {"node": ua_node("UaValue"), "encoding": "decimalInteger", "expected": "0", "value": "1234"}
    written = r.stage(oracle.UA_WRITE, lambda: t("runtime_ua_write", {"attemptId": ua, "bindingSha256": r.binding, "writes": [write], "acknowledgeWrite": True}))
    require(written["state"] == "written" and all(written["readbackMatches"]), "The UA write was not read back: %s" % written)
    compare(r, "ua", "UaValue after the UA write", ua, "UaValue")
    stale = r.stage("runtime_ua_write with a stale expected value", lambda: t("runtime_ua_write", {"attemptId": ua, "bindingSha256": r.binding,
                    "writes": [{**write, "value": "5"}], "acknowledgeWrite": True}))
    require(stale["state"] == "preconditionMismatch" and stale["dispatched"] is False, "A stale expected value was written: %s" % stale)
    compare(r, "ua", "UaValue after the refused write", ua, "UaValue")
    require(r.stage("runtime_ua_status", lambda: t("runtime_ua_status", {"attemptId": ua}))["state"] == "connected", "The UA session is not connected")
    nodes = [ua_node("Cycles"), ua_node("UaValue")]
    subscribed = acquire(r, "runtime_ua_subscription to its duration", "subscription", {"attemptId": ua, "nodes": nodes, "publishingIntervalMilliseconds": 200,
                         "samplingIntervalMilliseconds": 100, "durationSeconds": 4, "maximumNotifications": 1000})
    require(subscribed["recordedProgress"]["remoteCleanupConfirmed"], "DeleteSubscriptions was not confirmed")
    retained(r, "subscription", "subscription", subscribed)
    stopped = acquire(r, "runtime_ua_subscription stopped", "subscription", {"attemptId": ua, "nodes": nodes[:1], "durationSeconds": 60}, stop_after=3)
    require(stopped["recordedProgress"]["terminalReason"] == "stopped", "The subscription did not stop on request: %s" % stopped["recordedProgress"])
    polled = acquire(r, "runtime_ua_capture to its duration", "ua", {"attemptId": ua, "nodes": nodes, "intervalMilliseconds": 200, "durationSeconds": 3})
    retained(r, "polling capture", "ua", polled)
    stopped = acquire(r, "runtime_ua_capture stopped", "ua", {"attemptId": ua, "nodes": nodes[:1], "intervalMilliseconds": 200, "durationSeconds": 60}, stop_after=3)
    require(stopped["recordedProgress"]["terminalReason"] == "stopped", "The capture did not stop on request: %s" % stopped["recordedProgress"])
    require(r.stage("runtime_ua_disconnect", lambda: t("runtime_ua_disconnect", {"attemptId": ua}))["state"] == "closed", "The UA session did not close")

    connect(r, "web", "runtime_web_connect with a stale pin", web_login(r, r.report["stalePins"]["web"]["sha256"], r.stale_web_credential), connected=False)
    web = connect(r, "web", "runtime_web_connect", web_login(r))
    r.stage("runtime_web_browse the root", lambda: t("runtime_web_browse", {"attemptId": web})["metadata"])
    select(r, web)
    compare(r, "web", "Marker read through the Web API", web, "Marker")
    mode = r.stage("runtime_web_diagnostics operatingMode", lambda: t("runtime_web_diagnostics", {"attemptId": web, "kind": "operatingMode"}))
    require(mode["status"] == "read" and mode["value"] == "run", "The Web API does not read RUN: %s" % mode)
    require(r.stage("runtime_web_status", lambda: t("runtime_web_status", {"attemptId": web}))["state"] == "connected", "The web session is not connected")
    compare(r, "web", "Enable before the web write", web, "Enable")
    written = r.stage(oracle.WEB_WRITE, lambda: t("runtime_web_write", {"attemptId": web, "bindingSha256": r.binding,
                      "writes": [{"selectionId": r.selections["Enable"], "expected": False, "value": True}], "acknowledgeWrite": True}))
    require(written["state"] == "written" and all(i["readbackMatches"] for i in written["items"]), "The web write was not read back: %s" % written)
    settle(r, "Active", True)
    compare(r, "web", "Enable after the web write", web, "Enable")
    compare(r, "web", "Active follows Enable", web, "Active")
    mapped = [{"selectionId": r.selections[m], "signalId": m.lower(), "reason": "The instance's %s, read through the Web API." % m} for m in ("Enable", "Active")]
    request = {"attemptId": web, "selectionIds": [r.selections["Enable"], r.selections["Active"]], "intervalMilliseconds": 200, "durationSeconds": 3,
               "reviewId": r.review, "association": {"caseId": r.case, "mappings": mapped}}
    captured = acquire(r, "runtime_capture to its duration", "web", request)
    retained(r, "web capture", "web", captured)
    analysis = r.stage("runtime_capture_analyze", lambda: t("runtime_capture_analyze", {"snapshotRef": captured["snapshotRef"], "units": UNITS}))
    r.report["analysis"] = analysis
    stopped = acquire(r, "runtime_capture stopped", "web", {k: request[k] for k in ("attemptId", "selectionIds", "intervalMilliseconds")} | {"durationSeconds": 60}, stop_after=3)
    require(stopped["recordedProgress"]["terminalReason"] == "stopped", "The web capture did not stop on request: %s" % stopped["recordedProgress"])
    for label, expected, mode, state, observed in ((oracle.TO_STOP, "run", "stop", "Stop", oracle.PLCSIM_STOP), (oracle.TO_RUN, "stop", "run", "Run", oracle.PLCSIM_RUN)):
        changed = r.stage(label, lambda e=expected, m=mode: t("runtime_web_operating_mode", {"attemptId": web, "bindingSha256": r.binding, "expectedMode": e, "mode": m,
                                                                                           "acknowledgeModeChange": True}))
        require(changed["state"] == "changed", "%s did not change the mode: %s" % (label, changed))
        require(r.stage(observed, lambda: operating_state(r)) == state, "PLCSIM does not report " + state)
    require(r.stage("runtime_web_disconnect", lambda: t("runtime_web_disconnect", {"attemptId": web}))["state"] == "closed", "The web session did not close")


def operating_state(r):
    return r.tool("sim_select", {"instanceId": r.instance["id"], "expectedName": r.instance["name"], "creationId": r.creation})["instance"]["operatingState"]


def settle(r, member, value):
    def check():
        deadline = time.monotonic() + 5
        while (read := r.plcsim(member))["value"] != value and time.monotonic() < deadline:
            time.sleep(0.2)
        require(read["value"] == value, "PLCSIM does not read %s as %s: %s" % (member, value, read))
        return read
    return r.stage("PLCSIM reads %s %s" % (member, value), check)


def fresh(r, channel):
    """A new session after the lost one was acknowledged; its read is compared with the PLCSIM API, then it closes."""
    if channel == "ua":
        session = connect(r, "ua", name("runtime_ua_connect", "fresh"), ua_selection(r))
        row = compare(r, "ua", name(oracle.FRESH, "ua"), session, "Marker")
        require(r.stage(name("runtime_ua_disconnect", "fresh"), lambda: r.tool("runtime_ua_disconnect", {"attemptId": session}))["state"] == "closed", "The fresh UA session did not close")
    else:
        session = connect(r, "web", name("runtime_web_connect", "fresh"), web_login(r))
        select(r, session)
        row = compare(r, "web", name(oracle.FRESH, "web"), session, "Marker")
        require(r.stage(name("runtime_web_disconnect", "fresh"), lambda: r.tool("runtime_web_disconnect", {"attemptId": session}))["state"] == "closed", "The fresh web session did not close")
    return row


def lost(r, channel, session, decide=None):
    """The new Host's view of a session another Host lost: unknown, blocking new sessions until acknowledged."""
    tool = "runtime_%s_" % channel
    status = r.stage(name(oracle.LOST_STATUS, channel), lambda: r.tool(tool + "status", {"attemptId": session}))
    require(status["state"] == "hostEndedRemoteUnknown", "The lost %s session is %s" % (channel, status["state"]))
    if decide:
        decide()
    argument = ("selection", ua_selection(r)) if channel == "ua" else ("login", web_login(r))
    refused(r, name(oracle.NEW_SESSION_REFUSED, channel), tool + "connect", {"attemptId": uuid.uuid4().hex, argument[0]: argument[1]}, oracle.REFUSALS[channel][1])
    acknowledged = r.stage(name(oracle.ACKNOWLEDGED, channel), lambda: r.tool(tool + "disconnect", {"attemptId": session, "acknowledgeUnknown": True}))
    require(acknowledged["state"] == "abandonedRemoteUnknown", "The lost %s session was not released: %s" % (channel, acknowledged["state"]))


def host_loss_capture(r):
    r.close_host()
    r.start_host("lost")
    credentials(r)
    ua = connect(r, "ua", "runtime_ua_connect before the Host loss", ua_selection(r))
    web = connect(r, "web", "runtime_web_connect before the Host loss", web_login(r))
    select(r, web)
    subscription = acquire(r, "runtime_ua_subscription during the Host loss", "subscription", {"attemptId": ua, "nodes": [ua_node("Cycles")],
                           "publishingIntervalMilliseconds": 200, "samplingIntervalMilliseconds": 100, "durationSeconds": 120}, wait=False)
    capture = acquire(r, "runtime_capture during the Host loss", "web", {"attemptId": web, "selectionIds": [r.selections["Cycles"]],
                      "intervalMilliseconds": 200, "durationSeconds": 120}, wait=False)
    def running():
        states = {channel: r.tool(ACQUIRE[kind] + "status", {"captureId": started["captureId"]})["state"]
                  for channel, kind, started in (("ua", "subscription", subscription), ("web", "web", capture))}
        require(all(state == "running" for state in states.values()), "An acquisition ended before the Host loss: %s" % states)
        return states
    r.stage(oracle.BEFORE_LOSS, running)
    r.watch()
    r.stage("terminate the Host", lambda: r.close_host(expected=1, kill=True))
    r.start_host("recovery")
    for channel, session, started in (("ua", ua, subscription), ("web", web, capture)):
        prefix = ACQUIRE["subscription" if channel == "ua" else "web"]
        after = r.stage(name(oracle.AFTER_LOSS, channel), lambda p=prefix, s=started: r.tool(p + "status", {"captureId": s["captureId"]}))
        require(after["state"] == "interrupted", "The %s acquisition is %s after the Host loss" % (channel, after["state"]))
        retained(r, "interrupted " + channel, "subscription" if channel == "ua" else "web", after, rows=1)
        r.stage(name("start the interrupted acquisition again", channel), lambda p=prefix, s=started: r.client.request("tools/call", {
            "name": p + "start", "arguments": {"captureId": s["captureId"], "request": s["request"]}}).get("structuredContent"))
        time.sleep(2)
        r.stage(name(oracle.RESUME, channel), lambda p=prefix, s=started: r.tool(p + "status", {"captureId": s["captureId"]}))
        lost(r, channel, session)
        fresh(r, channel)


def server_restart(r):
    credentials(r)
    ua = connect(r, "ua", "runtime_ua_connect before the restart", ua_selection(r))
    r.stage(oracle.START_BEFORE, lambda: r.tool("runtime_ua_read", {"attemptId": ua, "nodes": [START_TIME]}))
    web = connect(r, "web", "runtime_web_connect before the restart", web_login(r))
    select(r, web)
    polled = acquire(r, "runtime_ua_capture across the restart", "ua", {"attemptId": ua, "nodes": [ua_node("Cycles")], "intervalMilliseconds": 500,
                     "durationSeconds": 60, "maximumReconnectAttempts": 2}, wait=False)
    captured = acquire(r, "runtime_capture across the restart", "web", {"attemptId": web, "selectionIds": [r.selections["Cycles"]],
                       "intervalMilliseconds": 500, "durationSeconds": 60}, wait=False)
    for action, state in (("stop", "Stop"), ("powerOff", "Off"), ("powerOn", None), ("run", "Run")):
        result = r.stage(oracle.CYCLE % action, lambda a=action: r.tool("sim_state", {"instanceId": r.instance["id"], "action": a, "timeoutMs": 60000}))
        require(state in (None, result["instance"]["operatingState"]), "%s left the CPU in %s" % (action, result["instance"]["operatingState"]))
    r.stage("refresh the PLCSIM tag list after the restart", r.refresh_tags)
    for channel, started in (("ua", polled), ("web", captured)):
        prefix = ACQUIRE[channel]
        def finish(p=prefix, s=started):
            deadline = time.monotonic() + 120
            while (status := r.tool(p + "status", {"captureId": s["captureId"]}))["state"] == "running" and time.monotonic() < deadline:
                time.sleep(1)
            return status if status["state"] != "running" else r.tool(p + "stop", {"captureId": s["captureId"]})
        status = r.stage(name(oracle.FINISHED, channel), finish)
        def rows(p=prefix, s=status):
            collected = []
            while len(collected) < s["recordedProgress"]["rows"]:
                page = r.tool(p + "page", {"snapshotRef": s["snapshotRef"], "offset": len(collected), "count": 200})["rows"]
                require(page, "The capture page ended early")
                collected += page
            return collected
        r.stage(name(oracle.ACROSS, channel), rows)
    refused(r, name(oracle.OLD_READ, "ua"), "runtime_ua_read", {"attemptId": ua, "nodes": [ua_node("Marker")]})
    refused(r, name(oracle.OLD_READ, "web"), "runtime_web_read", {"attemptId": web, "selectionId": r.selections["Marker"]})
    for channel, session in (("ua", ua), ("web", web)):
        r.stage(name("release the old session", channel), lambda c=channel, s=session: r.tool("runtime_%s_disconnect" % c, {"attemptId": s, "acknowledgeUnknown": True})["state"])
    session = connect(r, "ua", "runtime_ua_connect after the restart", ua_selection(r))
    r.stage(oracle.START_AFTER, lambda: r.tool("runtime_ua_read", {"attemptId": session, "nodes": [START_TIME]}))
    compare(r, "ua", name(oracle.FRESH, "ua"), session, "Marker")
    r.stage("runtime_ua_disconnect after the restart", lambda: r.tool("runtime_ua_disconnect", {"attemptId": session})["state"])
    fresh(r, "web")


def write_loss(r, channel):
    point = oracle.POINTS[r.args.mode]
    r.close_host()
    r.start_host("fault", "--component-qualification", "--runtime-qualification", point)
    bind(r)
    credentials(r)
    if channel == "ua":
        session = connect(r, "ua", "runtime_ua_connect before the lost write", ua_selection(r))
        compare(r, "ua", "UaValue before the lost write", session, "UaValue")
        tool, write = "runtime_ua_write", {"node": ua_node("UaValue"), "encoding": "decimalInteger", "expected": "0", "value": "1234"}
    else:
        session = connect(r, "web", "runtime_web_connect before the lost write", web_login(r))
        select(r, session)
        compare(r, "web", "Enable before the lost write", session, "Enable")
        tool, write = "runtime_web_write", {"selectionId": r.selections["Enable"], "expected": False, "value": True}
    r.report["lostAttempt"] = session
    r.watch()
    def send():
        try:
            r.tool(tool, {"attemptId": session, "bindingSha256": r.binding, "writes": [write], "acknowledgeWrite": True})
        except RuntimeError as error:
            require("MCP host exited" in str(error), "The write failed for another reason: %s" % error)
            marker = json.loads((r.directory / oracle.MARKER).read_text(encoding="utf-8"))
            require(marker["point"] == point and marker["automaticReplay"] is False, "Unexpected interruption marker: %s" % marker)
            return marker
        raise AssertionError("The fault point did not fire; nothing is retried")
    r.stage("send the write the Host loses", send)
    r.stage("the fault Host exits with 74", lambda: r.close_host(expected=74))
    r.start_host("recovery")
    bind(r)
    def decide():
        refused(r, name(oracle.RETRY, channel), tool, {"attemptId": session, "bindingSha256": r.binding, "writes": [write], "acknowledgeWrite": True}, oracle.REFUSALS[channel][0])
    lost(r, channel, session, decide)
    member = "UaValue" if channel == "ua" else "Enable"
    if channel == "ua":
        fresh_session = connect(r, "ua", "runtime_ua_connect to decide the lost write", ua_selection(r))
    else:
        fresh_session = connect(r, "web", "runtime_web_connect to decide the lost write", web_login(r))
        select(r, fresh_session)
    decision = compare(r, channel, oracle.DECISION, fresh_session, member)
    r.report["lostWriteOutcome"] = "applied" if oracle.channel_value(channel, decision["channelRead"]) == (1234 if channel == "ua" else True) else "notApplied"
    r.stage("close the deciding session", lambda: r.tool("runtime_%s_disconnect" % channel, {"attemptId": fresh_session})["state"])


def plc_methods(r, ua, limit=300):
    """Every Method the PLC namespace exposes below Objects, by a bounded walk over its Object nodes: runtime_ua_call admits any of
    them, so the run shows whether system methods sit beside the user program's."""
    queue, seen, found, browsed = [OBJECTS], set(), [], 0
    while queue and browsed < limit:
        parent = queue.pop(0)
        browsed += 1
        for ref in r.tool("runtime_ua_browse", {"attemptId": ua, "node": parent})["references"]:
            node = ref.get("node")
            key = json.dumps(node, sort_keys=True)
            if not node or node.get("namespaceUri") != oracle.SIEMENS_NS or key in seen:
                continue
            seen.add(key)
            if ref.get("nodeClass") == "Method":
                found.append({"parent": parent, "method": node, "browseName": ref.get("browseName")})
            elif ref.get("nodeClass") == "Object":
                queue.append(node)
    return {"methods": found, "browsed": browsed, "complete": not queue}


def services(r):
    """Server-method Call, Alarms and Conditions and the HistoryRead boundary on the owned instance, through one UA session."""
    t = r.tool
    bind(r)
    credentials(r)
    ua = connect(r, "ua", "runtime_ua_connect", ua_selection(r))
    r.stage(oracle.EVENT_MODEL, lambda: t("runtime_ua_browse", {"attemptId": ua, "node": SERVER})["references"])
    r.report["plcMethods"] = r.stage(oracle.METHOD_SET, lambda: plc_methods(r, ua))

    def discover():
        references = t("runtime_ua_browse", {"attemptId": ua, "node": ua_node(block="ServerMethod_DB")})["references"]
        found = [x["node"] for x in references if x.get("nodeClass") == "Method" and x.get("node")]
        require(len(found) == 1, "ServerMethod_DB exposes no single server method: %s" % references)
        return found[0]
    target = {"attemptId": ua, "bindingSha256": r.binding, "objectNode": ua_node(block="ServerMethod_DB"), "methodNode": r.stage(oracle.METHOD_BROWSE, discover),
              "acknowledgeCall": True}

    def inputs(number, flag):
        return [{"encoding": "decimalInteger", "value": str(number)}, {"encoding": "boolean", "value": flag}]
    before = compare(r, "ua", oracle.CALLS[0], ua, "MethodCalls")["plcsimRead"]["value"]
    refused(r, oracle.CALL_UNACKNOWLEDGED, "runtime_ua_call", {**target, "inputs": inputs(41, True), "acknowledgeCall": False}, "RuntimeAcknowledgementRequired")
    for label, arguments in ((oracle.CALL_STANDARD, {**target, "objectNode": SERVER, "methodNode": GET_MONITORED_ITEMS, "inputs": inputs(1, True)[:1]}),
                             (oracle.CALL_ARGUMENTS, {**target, "inputs": inputs(41, True)[:1]})):
        result = r.stage(label, lambda a=arguments: t("runtime_ua_call", a))
        require(result["state"] == "refused" and result["dispatched"] is False and result["refusal"] == oracle.CALL_REFUSALS[label], "%s was not refused: %s" % (label, result))
    called = r.stage(oracle.UA_CALL, lambda: t("runtime_ua_call", {**target, "inputs": inputs(41, True)}))
    require(called["state"] == "called" and [(o.get("encoding"), o.get("value")) for o in called["outputs"]] == [("decimalInteger", "42"), ("boolean", False)],
            "The server method did not answer 42 and false: %s" % called)
    settle(r, "MethodCalls", before + 1)
    compare(r, "ua", oracle.CALLS[1], ua, "MethodCalls")
    failed = r.stage(oracle.CALL_FAILED, lambda: t("runtime_ua_call", {**target, "inputs": inputs(32767, False)}))
    require(failed["state"] == "methodFailed" and failed["statusCode"] == oracle.METHOD_BAD, "The method's own Bad result was not returned: %s" % failed)
    compare(r, "ua", oracle.CALLS[2], ua, "MethodCalls")

    def snapshot(label):
        result = r.stage(label, lambda: t("runtime_ua_conditions", {"attemptId": ua, "windowMilliseconds": 4000, "maximumEvents": 128}))
        require(result["refresh"]["startObserved"] and result["refresh"]["endObserved"], "%s lacks the ConditionRefresh boundaries" % label)
        return {json.dumps(e["conditionId"], sort_keys=True) for e in result["events"] if e["kind"] == "condition" and e.get("activeState") is True}

    def trigger(value):
        r.stage("sim_write AlarmTrigger %s" % value, lambda: t("sim_write", {"instanceId": r.instance["id"], "writes": [
            {"symbol": r.symbols["AlarmTrigger"], "type": "bool", "value": value}]}))
        settle(r, "AlarmTrigger", value)
    idle = snapshot(oracle.CONDITIONS[0])
    trigger(True)
    raised = snapshot(oracle.CONDITIONS[1]) - idle
    trigger(False)
    fallen = snapshot(oracle.CONDITIONS[2])
    # PLCSIM may not raise A&C conditions at all; that stays a recorded observation, never a product failure.
    r.report["alarmCondition"] = {"raised": len(raised) == 1, "conditionIds": sorted(raised), "activeAfterFall": sorted(raised & fallen)}
    require(len(raised) <= 1, "The trigger raised several conditions: %s" % sorted(raised))
    # Last before the disconnect: an unexpected HistoryRead fault ends the session, so the other evidence comes first.
    r.stage(oracle.HISTORY, lambda: t("runtime_ua_history_probe", {"attemptId": ua, "node": ua_node("Marker")}))
    require(r.stage("runtime_ua_disconnect", lambda: t("runtime_ua_disconnect", {"attemptId": ua}))["state"] == "closed", "The UA session did not close")


def pinned(ip, pin, method, params=None, token=None):
    """One Web API JSON-RPC call that only the pinned web certificate may answer: (DER, HTTP status, parsed response or None).
    The route beside the device_* tools; neither the request (an Api.Login password) nor the token is retained."""
    context = ssl.create_default_context()
    context.check_hostname, context.verify_mode = False, ssl.CERT_NONE  # The pin decides, before anything is sent.
    connection = http.client.HTTPSConnection(ip, 443, timeout=15, context=context)
    try:
        connection.connect()
        der = connection.sock.getpeercert(binary_form=True)
        require(hashlib.sha256(der).hexdigest() == pin.lower(), "The web server certificate is not the pinned one")
        body = {"jsonrpc": "2.0", "id": 1, "method": method, **({} if params is None else {"params": params})}
        connection.request("POST", "/api/jsonrpc", json.dumps(body), {"Content-Type": "application/json", **({"X-Auth-Token": token} if token else {})})
        response = connection.getresponse()
        status, text = response.status, response.read()
    finally:
        connection.close()
    try:
        return der, status, json.loads(text)
    except ValueError:
        return der, status, None


def pinned_call(r, method, params=None):
    _, status, response = pinned(r.args.cpu_ip, r.report["pins"]["web"]["sha256"], method, params, r.token)
    return {"httpStatus": status, "response": response}


def await_trust(path, timeout=TRUST_WAIT, poll=5):
    """The coordinator's {"command": "trusted"} or {"command": "abort"} in path; an absent, partly written or other file waits."""
    deadline = time.monotonic() + timeout
    while True:
        try:
            command = json.loads(path.read_text(encoding="utf-8")).get("command")
        except (OSError, ValueError, AttributeError):
            command = None
        if command in ("trusted", "abort"):
            return command
        require(time.monotonic() < deadline, "No trust command in %s within %d minutes" % (path, timeout // 60))
        time.sleep(poll)


def in_root(thumbprint):
    """Read-only: whether Cert:\\CurrentUser\\Root holds the thumbprint. The runner never imports or removes a certificate."""
    done = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "Test-Path -LiteralPath 'Cert:\\CurrentUser\\Root\\%s'" % thumbprint],
                          capture_output=True, text=True, timeout=60)
    return done.returncode == 0 and done.stdout.strip() == "True"


def keep_alive(r, stop, alive):
    """Api.GetPermissions every 30 seconds until stop: the borrowed session must not idle out while TIA reopens the scratch."""
    while not stop.wait(30):
        try:
            alive["answered" if oracle.answered(pinned_call(r, "Api.GetPermissions")) is not None else "failed"] += 1
        except Exception:  # Counted only: a session that expires anyway fails the tools with their own codes.
            alive["failed"] += 1


def snapshot(r):
    """Every row of one device_inventory capture; device_identify needs a snapshot of the Host that calls it."""
    page = r.tool("device_inventory", {"pageSize": 200, "maxVisited": 50000})
    coverage, rows = page["snapshot"]["coverage"], list(page["rows"])
    require(coverage["scopeComplete"] is True and coverage["truncated"] is False, "device_inventory is incomplete: %s" % coverage)
    while page.get("nextCursor"):
        page = r.tool("device_inventory", {"snapshotId": page["snapshot"]["snapshotId"], "cursor": page["nextCursor"], "pageSize": 200})
        rows += page["rows"]
    return {"snapshotId": page["snapshot"]["snapshotId"], "rows": rows}


def flatten(nodes):
    """Browsed nodes with their nested children: PLCSIM answers Modules.Browse with the station node and its CPU under children."""
    for node in nodes:
        yield node
        yield from flatten(node.get("children") or [])


def bind_rows(rows, nodes):
    """The CPU row bound to the one cpu node of its name (PLCSIM gives the station the CPU's name too), then every other row whose name
    is unique in the snapshot and among the other browsed modules and submodules, bound to that node (at most 32)."""
    def unique(names):
        return {n for n in names if names.count(n) == 1}
    names = unique([row["name"] for row in rows])
    cpus = [row for row in rows if row["kind"] == "cpu"]
    cpu_nodes = [node for node in nodes if cpus and node.get("subtype") == "cpu" and node.get("name") == cpus[0]["name"]]
    require(len(cpus) == 1 and cpus[0]["name"] in names and len(cpu_nodes) == 1,
            "Expected one CPU row named like one online cpu node: %s, %s" % ([row["name"] for row in cpus], sorted({n.get("name") for n in nodes if n.get("subtype") == "cpu"})))
    cpu, cpu_node = cpus[0], cpu_nodes[0]
    others = [node for node in nodes if node is not cpu_node and node.get("type") != "device"]
    online = unique([node.get("name") for node in others])
    online = {node["name"]: node for node in others if node.get("name") in online}
    bindings = [{"rowId": cpu["rowId"], "hardwareId": cpu_node["hwid"]}] + [{"rowId": row["rowId"], "hardwareId": online[row["name"]]["hwid"]}
                for row in rows if row is not cpu and row["name"] in names and row["name"] in online]
    return cpu["rowId"], cpu_node["hwid"], bindings[:32]


def agree(issues):
    require(not issues, "; ".join(issues))
    return issues


def inventory(r):
    """device_online_browse and device_identify through Windows trust of this instance's web certificate, each beside pinned reads."""
    endpoint, path, command = "https://" + r.args.cpu_ip, r.directory / "web-server.cer", r.directory / "trust-command.json"

    def export():
        der = pinned(r.args.cpu_ip, r.report["pins"]["web"]["sha256"], "Api.Browse")[0]
        path.write_bytes(der)
        # Not a trust decision (the SHA-256 pin above is): Windows names a certificate-store entry by its SHA-1 thumbprint.
        r.thumbprint = hashlib.sha1(der).hexdigest().upper()
        return {"path": str(path), "sha1": r.thumbprint, "sha256": hashlib.sha256(der).hexdigest()}
    r.stage("export the pinned web server certificate", export)
    r.close_host()  # The inventory Host starts after the trust and the login, so that its environment can hold the session record.
    print("TRUST_READY %s %s" % (path, r.thumbprint), flush=True)
    print("  import:  Import-Certificate -FilePath \"%s\" -CertStoreLocation Cert:\\CurrentUser\\Root" % path, flush=True)
    print("  removal: " + REMOVE % r.thumbprint, flush=True)
    print('  then write {"command": "trusted"} or {"command": "abort"} to %s within 30 minutes' % command, flush=True)
    require(r.stage(oracle.TRUST, lambda: await_trust(command)) == "trusted", "The coordinator aborted the run")

    def trusted():
        require(in_root(r.thumbprint), "Cert:\\CurrentUser\\Root does not hold " + r.thumbprint)
        return True
    r.stage(oracle.IN_ROOT, trusted)

    def probe():
        answer = pinned_call(r, "Modules.Browse", {"mode": "children"})
        error = answer["response"].get("error") if isinstance(answer["response"], dict) else None
        required = answer["httpStatus"] in (401, 403) or isinstance(error, dict) and error.get("code") == 2
        require(required or oracle.answered(answer) is not None, "Modules.Browse without a session answered %s" % answer)
        return {**answer, "sessionRequired": required}
    if r.stage("Modules.Browse without a session", probe)["sessionRequired"]:
        def login():
            answer = pinned_call(r, "Api.Login", {"user": r.username, "password": r.password, "mode": "local"})
            result = oracle.answered(answer)
            require(isinstance(result, dict) and isinstance(result.get("token"), str) and result["token"],
                    "Api.Login was refused (HTTP %s)" % answer["httpStatus"])
            r.token = result["token"]
            return {"httpStatus": answer["httpStatus"], "resultKeys": sorted(result), "runtimeTimeout": result.get("runtime_timeout")}
        r.stage("Api.Login of the runtime user over the pinned connection", login)
        r.stage("Api.GetPermissions of that session", lambda: pinned_call(r, "Api.GetPermissions"))
    session = {"sessionEnvironmentVariable": SESSION} if r.token else {}
    stop, alive = threading.Event(), r.report.setdefault("sessionKeepAlive", {"answered": 0, "failed": 0})
    if r.token:
        threading.Thread(target=keep_alive, args=(r, stop, alive), daemon=True).start()
        os.environ[SESSION] = json.dumps({"endpoint": endpoint, "token": r.token})
    try:
        try:
            r.start_host("inventory")
        finally:
            os.environ.pop(SESSION, None)  # Only the inventory Host, and the workers it starts, inherit the record.
        r.owned_portal("inventory")
        r.stage("reopen the saved scratch project", lambda: r.reopen_project(r.scratch["project"]["path"]))
        captured = r.stage("device_inventory of the reopened scratch", lambda: snapshot(r))
        r.stage("tia_detach after the snapshot", lambda: r.tool("tia_detach"))

        def browse():
            """From the top level down, each node's children once (at most 32 calls), each beside the same pinned Modules.Browse."""
            rows, queue, seen = [], [None], set()
            while queue and len(rows) < 32:
                hwid = queue.pop(0)
                subtree = {} if hwid is None else {"hardwareId": hwid}
                tool = r.client.request("tools/call", {"name": "device_online_browse", "arguments": {"endpoint": endpoint, **session, **subtree}})
                rows.append({"hardwareId": hwid, "tool": {"isError": bool(tool.get("isError")), "structuredContent": tool.get("structuredContent")},
                             "pinned": pinned_call(r, "Modules.Browse", {"mode": "children", **({} if hwid is None else {"hwid": hwid})})})
                for node in [] if tool.get("isError") else flatten(tool["structuredContent"]["result"]["nodes"]):
                    if isinstance(node.get("hwid"), int) and node["hwid"] not in seen:
                        seen.add(node["hwid"])
                        queue.append(node["hwid"])
            require(not rows[0]["tool"]["isError"], "device_online_browse failed: %s" % rows[0]["tool"]["structuredContent"])
            return rows
        browsed = r.stage(oracle.BROWSE, browse)
        r.stage("device_online_browse equals the pinned Modules.Browse", lambda: agree([i for row in browsed for i in oracle.browse_issues(row)]))

        def identify():
            nodes = {n["hwid"]: n for row in browsed if not row["tool"]["isError"] for n in flatten(row["tool"]["structuredContent"]["result"]["nodes"])
                     if isinstance(n.get("hwid"), int)}
            row_id, hwid, bindings = bind_rows(captured["rows"], list(nodes.values()))
            tool = r.tool("device_identify", {"snapshotId": captured["snapshotId"], "endpoint": endpoint, "controllerRowId": row_id,
                                              "controllerHardwareId": hwid, "bindings": bindings, **session})
            return {"controllerRowId": row_id, "controllerHardwareId": hwid, "bindings": bindings, "tool": tool,
                    "pinned": [pinned_call(r, "Modules.ReadIdentificationMaintenance", {"hwid": b["hardwareId"], "number": 0, "type": "actual"}) for b in bindings]}
        identified = r.stage(oracle.IDENTIFY, identify)
        r.stage("device_identify equals the pinned I&M0 reads", lambda: agree(oracle.identify_issues(identified)))
    finally:
        stop.set()
        if r.token:
            try:
                r.stage("Api.Logout of the borrowed session", lambda: pinned_call(r, "Api.Logout"))
            except Exception as error:  # Keep the original failure; the logout failure is retained beside it.
                r.report["logoutFailure"] = str(error)


def release(r):
    """Stop, switch off and release the owned instance from the current Host (a new one adopts it when none is open)."""
    if getattr(r, "instance", None) is None:
        return
    if r.client is None:
        r.start_host("cleanup")
    i = r.instance["id"]
    try:
        if operating_state(r) == "Run":
            r.stage("stop", lambda: r.tool("sim_state", {"instanceId": i, "action": "stop", "timeoutMs": 30000})["instance"]["operatingState"])
        if operating_state(r) != "Off":
            r.stage("power off", lambda: r.tool("sim_state", {"instanceId": i, "action": "powerOff", "timeoutMs": 60000})["instance"]["operatingState"])
    finally:
        released = r.stage("sim_release", lambda: r.tool("sim_release", {"instanceId": i, "deleteOwned": True}))
        require(released.get("unregistered") is True, "The owned instance was not released")


MODES = {"normal": normal, "host-loss-capture": host_loss_capture, "server-restart": server_restart,
         "ua-write-loss": lambda r: write_loss(r, "ua"), "web-write-loss": lambda r: write_loss(r, "web"), "ua-services": services,
         "device-inventory": inventory}


def body(r):
    r.instance = None
    try:
        try:
            setup(r)
            MODES[r.args.mode](r)
        except BaseException:
            try:
                release(r)
            except Exception as error:  # Keep the original failure; the release failure is retained beside it.
                r.report["releaseFailure"] = str(error)
            raise
        release(r)
    finally:
        if r.thumbprint:  # device-inventory: the operator's trust outlives the instance until it is removed by hand.
            print("Remove the trusted web certificate: " + REMOVE % r.thumbprint, flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--mode", choices=sorted(MODES), required=True)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--sim-worker", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--cpu-ip", default="192.168.0.1")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    Runner(parser.parse_args()).run(body)
