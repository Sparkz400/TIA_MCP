"""Qualify the runtime OPC UA, web and capture tools against the servers of an owned PLCSIM Advanced V8 CPU in the
operator's existing TCP/IP single-adapter mode (runtime real-server plan, Tasks 4 and 5).

Setup, every mode: an owned scratch CPU 1511-1 PN V4.1 at --cpu-ip with simulation support; an FB DiagGate (Active :=
Enable) with its instance DB, a global DB RealServer (Marker := 4711, Cycles counted up every scan, UaValue) and a Main
calling both; the scratch's runtime role gets OPC UA server access and the web rights to read and write process data,
read diagnostics and change the operating mode; the web server and the OPC UA server (Small license, automatic
client-certificate acceptance) are switched on; a saved review and a diagnostic case on DiagGate. A TCP/IP instance gets
the program (download, generated-state save, protection-credential provisioning); the OPC UA and web certificate pins
come from certificate_inspect after the last compile; the CPU runs and TIA closes. The OPC UA and web login is the
scratch's generated runtime user: its DPAPI password is copied into a run-owned Host credential vault, retired at the end.

--mode normal: every runtime_ua_*, runtime_web_* and runtime_capture_* tool plus runtime_plcsim_bind. Stale pins are
  refused, UA and web values equal PLCSIM API reads, writes need their expected values, RUN/STOP through the Web API.
--mode host-loss-capture: the Host is terminated during a UA subscription and a web capture; a new Host reopens both
  committed prefixes, reports hostEndedRemoteUnknown, resumes nothing and refuses new sessions until acknowledgeUnknown.
--mode server-restart: STOP, power off, power on and RUN during a UA and a web capture; the server start time changes,
  both captures record the downtime, the old sessions stay dead and nothing reconnects.
--mode ua-write-loss | web-write-loss: a --runtime-qualification Host exits (74) after sending a write; a new Host
  reports the session unknown and refuses the retry; an explicit read through a fresh session, confirmed by the PLCSIM
  API, decides the outcome.

PLCSIM's network mode, adapters and IP addresses are operator settings and are never changed. verify_runtime_plcsim_tcpip
checks the retained run; verification.json is written only when it passes.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import subprocess
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
END_ORGANIZATION_BLOCK

'''
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
UA_NS = "http://opcfoundation.org/UA/"
OBJECTS = {"namespaceUri": UA_NS, "identifierType": "numeric", "identifier": "85"}
START_TIME = {"namespaceUri": UA_NS, "identifierType": "numeric", "identifier": "2257"}
# member: (owning DB, PLCSIM API type); the UA nodes and web symbols name the same tags.
SIGNALS = {"Marker": ("RealServer", "int32"), "Cycles": ("RealServer", "int32"), "UaValue": ("RealServer", "int16"),
           "Enable": ("DiagGate_DB", "bool"), "Active": ("DiagGate_DB", "bool")}
ENTROPY = b"TiaMcp.V21.ScratchRuntimeUser.v1"
LIMITATIONS = [
    "One owned PLCSIM Advanced V8 CPU 1511 instance in the operator's TCP/IP single-adapter mode (PLCSIM Virtual Ethernet Adapter) of an owned scratch "
    "CPU 1511-1 PN V4.1; physical PLCs and other CPU or firmware profiles are not covered.",
    "The OPC UA and web login is the scratch's generated runtime user (UMAC, password from its CurrentUser DPAPI file into a run-owned vault); guest access, "
    "project users made by an operator and UMC are not exercised.",
    "Power-on needs the provisioned protection credential, provisioned with first-use TLS trust under the owned-empty-CPU policy.",
    "Scalar DInt, Int and Bool tags only; arrays, structures, events and methods are not exercised.",
]


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


def ua_node(member=None):
    return {"namespaceUri": oracle.SIEMENS_NS, "identifierType": "string", "identifier": '"RealServer"' + ('."%s"' % member if member else "")}


def symbol(member):
    return '"%s".%s' % (SIGNALS[member][0], member)


class Runner(Harness):
    """Harness steps across several Hosts on one run directory; every Host gets an exit proof."""

    def __init__(self, args):
        args.artifacts, args.runner = args.root, __file__
        super().__init__(args, "rs_", "Runtime UA, web and capture tools on an owned PLCSIM Advanced V8 CPU in TCP/IP mode (%s)" % args.mode, LIMITATIONS, [])
        self.report["simWorker"] = {"path": str(args.sim_worker.resolve()), "sha256": hashlib.sha256(args.sim_worker.read_bytes()).hexdigest()}
        self.report.update(mode=args.mode, cpuIp=args.cpu_ip, hosts=[], comparisons=[], pins={}, attempts={"ua": [], "web": []})
        self.calls, self.credentials, self.password, self.selections, self.watched = [], [], None, {}, []
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

    def plcsim(self, member):
        row = self.tool("sim_read", {"instanceId": self.instance["id"], "signals": [{"symbol": self.symbols[member], "type": SIGNALS[member][1]}]})["values"][0]
        return {k: row.get(k) for k in ("symbol", "quality", "value")}

    def credential(self, label, binding):
        """Store the runtime user's password under one public binding in this run's own vault, through the Host's credential CLI."""
        folder = self.directory / "credentials"
        folder.mkdir(exist_ok=True)
        path = folder / (label + ".binding.json")
        path.write_text(json.dumps(binding), encoding="utf-8")
        created = subprocess.run([str(self.args.host.resolve()), "--credential-store", "create", "--vault-name", self.vault, "--binding", str(path), "--stdin"],
                                 input=json.dumps(self.password).encode("utf-8"), capture_output=True, timeout=60)
        echoed = self.password.encode("utf-8") in created.stdout + created.stderr
        require(created.returncode == 0 and not echoed, "Credential %s was not created: %s" % (label, "<withheld>" if echoed else created.stderr.decode(errors="replace")[-300:]))
        reference = json.loads(created.stdout)["reference"]
        (folder / (label + ".reference.json")).write_text(json.dumps(reference), encoding="utf-8")
        self.credentials.append(label)
        return reference

    def retire(self):
        rows = self.report.setdefault("credentialRetirement", [])
        for label in self.credentials:
            folder = self.directory / "credentials"
            done = subprocess.run([str(self.args.host.resolve()), "--credential-store", "retire", "--vault-name", self.vault, "--binding", str(folder / (label + ".binding.json")),
                                   "--reference", str(folder / (label + ".reference.json"))], capture_output=True, timeout=60)
            rows.append({"credential": label, "exitCode": done.returncode})

    def leaked(self):
        """Retained text files that contain the runtime user's password; none may."""
        if not self.password:
            return []
        secret = self.password.encode("utf-8")
        return [str(p) for p in self.directory.rglob("*") if p.is_file() and p.suffix.lower() in (".json", ".jsonl", ".csv", ".md", ".html", ".txt", ".log")
                and p.stat().st_size < 64 << 20 and secret in p.read_bytes()]

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
    change(r, "source_upsert", {"language": "SCL", "source": MAIN, "expectedBlockNames": ["Main"]})

    umac = r.stage("access_inspect", lambda: t("access_inspect", {"maxItems": 2000}))["projectUmac"]
    roles, scopes = [n for n in umac["roleNames"].values() if n.startswith("TiaMcpRuntime_")], umac["deviceRightScopes"]
    require(len(roles) == 1 and len(scopes) == 1, "Expected one scratch runtime role and one UMAC device: %s %s" % (roles, scopes))
    change(r, "umac_role_set", {"action": "grant", "roleName": roles[0], "deviceRights": [{"device": scopes[0], "identifier": i} for i in RIGHTS]}, plc=False)

    r.before_servers = certificates(r, "certificate_inspect before the servers")
    # V21 turns the system pages on with the server (29 October 2026); activation alone fails its readback, so both are named.
    change(r, "webserver_page_set", {"webServerActive": {"expected": False, "value": True}, "systemWebPages": {"expected": False, "value": True}})
    change(r, "opcua_server_settings_set", ENABLE)
    change(r, "opcua_server_settings_set", AUTO_ACCEPT)

    def application_uri():
        settings = t("opcua_engineering_inspect", {"plcPath": r.plc, "maxItems": 2000})["serverSettings"]
        server = [row["hardwarePath"] for row in settings if any(a["name"] == "OpcUaServer" for a in row["attributes"])]
        require(len(server) == 1, "Expected one OPC UA sub-item: %s" % server)
        rows = t("hardware_inspect", {"hardwarePath": server[0], "maxItems": 2000})["items"]
        values = {a["name"]: a.get("value") for a in rows[0]["attributes"]}
        require(values.get("OpcUaServer") is True and values.get("OpcUaAutoAcceptAllClientCertificates") is True and values.get("OpcUaPort") == 4840,
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
        r.symbols = {}
        for member, (block, _) in SIGNALS.items():
            tags = t("sim_tags", {"instanceId": r.instance["id"], "filter": block, "maxItems": 2000})["tags"]
            found = [tag["symbol"] for tag in tags if tag["symbol"].replace('"', "") == block + "." + member]
            require(len(found) == 1, "PLCSIM symbol %s.%s is missing or ambiguous" % (block, member))
            r.symbols[member] = found[0]
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
    r.ua_credential = r.credential("ua", r.stage("runtime_ua_credential_binding", lambda: r.tool("runtime_ua_credential_binding", {"selection": ua_selection(r)})))
    def web(pin):
        return r.tool("runtime_web_credential_binding", {"endpoint": "https://" + r.args.cpu_ip, "peerCertificateSha256": pin, "user": r.username, "mode": "local"})
    r.web_credential = r.credential("web", r.stage("runtime_web_credential_binding", lambda: web(r.report["pins"]["web"]["sha256"])))
    r.stale_web_credential = r.credential("web-stale", r.stage("runtime_web_credential_binding for the stale pin", lambda: web(r.report["stalePins"]["web"]["sha256"])))


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
        result = r.stage("sim_state %s during the captures" % action, lambda a=action: r.tool("sim_state", {"instanceId": r.instance["id"], "action": a, "timeoutMs": 60000}))
        require(state in (None, result["instance"]["operatingState"]), "%s left the CPU in %s" % (action, result["instance"]["operatingState"]))
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
         "ua-write-loss": lambda r: write_loss(r, "ua"), "web-write-loss": lambda r: write_loss(r, "web")}


def body(r):
    r.instance = None
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
