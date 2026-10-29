"""Every runner mode against a fake MCP Host and PLCSIM, then the oracle over its retained run; no native launch.

The fake answers only the shapes the runner reads. It catches flow and contract errors between the runner and the
oracle before an operator TCP/IP window, not native behavior.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

import library_native_harness

import qualify_runtime_plcsim_tcpip as runner
import verify_runtime_plcsim_tcpip as oracle

URI = "urn:SIMATIC:ScratchPLC"
CERTS = {"plc": {"sha256": "1" * 64, "subjectCommonName": "PLC-1", "subjectAlternativeNames": []},
         "web": {"sha256": "2" * 64, "subjectCommonName": "PLC-1/Webserver-3", "subjectAlternativeNames": [{"type": "IP", "value": "192.168.0.1"}]},
         "ua": {"sha256": "3" * 64, "subjectCommonName": "PLC-1/OPCUA", "subjectAlternativeNames": [{"type": "Uri", "value": URI}]}}


class Fail(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


class Plc:
    """One fake CPU, its sessions and acquisitions across fake Hosts."""

    def __init__(self, root):
        self.root, self.state, self.started, self.clock = root, "Off", 0, 0
        self.memory = {"Marker": 4711, "Cycles": 0, "UaValue": 0, "Enable": False, "Active": False}
        self.inspections, self.sessions, self.captures, self.host, self.stale = 0, {}, {}, None, False

    def cert(self, channel, argument):
        return argument["certificateSha256" if channel == "ua" else "peerCertificateSha256"]

    def connect(self, channel, attempt, argument):
        if any(s["channel"] == channel and s["state"] in ("connected", "unknown") for s in self.sessions.values()):
            raise Fail(oracle.REFUSALS[channel][1])
        (self.root / ("runtime-" + channel) / (attempt + ".json")).parent.mkdir(exist_ok=True)
        (self.root / ("runtime-" + channel) / (attempt + ".json")).write_text("{}", encoding="utf-8")
        pin = CERTS[channel]["sha256"]
        connected = self.cert(channel, argument) == pin and (channel == "web" or argument.get("authentication") == "username")
        self.sessions[attempt] = {"channel": channel, "state": "connected" if connected else "notConnected", "host": self.host, "argument": argument, "alive": connected}
        return self.status(attempt)

    def status(self, attempt):
        s = self.sessions[attempt]
        state = "hostEndedRemoteUnknown" if s["state"] in ("connected", "unknown") and s["host"] != self.host else s["state"]
        key = ("selection", "certificateSha256") if s["channel"] == "ua" else ("login", "peerCertificateSha256")
        return {"schemaVersion": "opcua-runtime-session.v1" if s["channel"] == "ua" else "runtime-web-session.v2", "attemptId": attempt, "state": state,
                "peerVerified": state == "connected", key[0]: {key[1]: self.cert(s["channel"], s["argument"])}}

    def live(self, attempt):
        s = self.sessions.get(attempt)
        if not s or s["state"] != "connected" or s["host"] != self.host:
            raise Fail("UaSessionUnavailable" if (s or {}).get("channel", "ua") == "ua" else "RuntimeSessionUnavailable")
        if not s["alive"]:
            raise Fail("UaOperationFailed")
        return s

    def now(self):
        """A fake UTC clock that advances one second per reading."""
        self.clock += 1
        return "2026-10-29T10:%02d:%02d.0000000Z" % divmod(self.clock, 60)

    def ua_item(self, node):
        if node == runner.START_TIME:
            return {"quality": "Good", "outcome": "value", "encoding": "utcDateTime", "value": "2026-10-29T10:%02d:00Z" % self.started}
        member = node["identifier"].split(".")[-1].strip('"')
        return {"node": node, "outcome": "value", "quality": "Good", "encoding": "decimalInteger", "value": str(self.memory[member])}

    def power(self, action):
        self.state = {"powerOn": "Stop", "run": "Run", "stop": "Stop", "powerOff": "Off"}[action]
        self.stale = self.stale or action == "powerOn"  # PLCSIM reads the old tag list as NotUpToDate until sim_tags refreshes it.
        if action == "powerOff":
            for s in self.sessions.values():
                s["alive"] = False
            for c in self.captures.values():
                if c["state"] == "running":
                    c.update(state="completed", downtime=True, terminalReason="readFailure", downAt=self.now())
        if action == "powerOn":
            self.started += 1

    def write(self, host, channel, arguments):
        s = self.live(arguments["attemptId"])
        item = arguments["writes"][0]
        member = item["node"]["identifier"].split(".")[-1].strip('"') if channel == "ua" else item["selectionId"].split(".")[-1]
        current = str(self.memory[member]) if channel == "ua" else self.memory[member]
        if current != item["expected"]:
            return {"state": "preconditionMismatch", "dispatched": False if channel == "ua" else 0}
        self.memory[member] = int(item["value"]) if channel == "ua" else item["value"]
        if member == "Enable":
            self.memory["Active"] = item["value"]
        if host.point == "%s.write.afterSend" % channel:
            (self.root / oracle.MARKER).write_text(json.dumps({"point": host.point, "hostProcessId": host.pid, "automaticReplay": False,
                                                                "intent": {"attemptId": s and arguments["attemptId"]}}), encoding="utf-8")
            host.exit_code = 74
            raise RuntimeError("MCP host exited: fault point")
        return {"state": "written", "readbackMatches": [True]} if channel == "ua" else {"state": "written", "items": [{"readbackMatches": True}]}

    def acquisition(self, tool, arguments):
        verb = tool.rsplit("_", 1)[1]
        if verb in ("page", "export"):
            capture = next(c for c in self.captures.values() if c["ref"] == arguments["snapshotRef"])
            if verb == "export":
                path = self.root / ("export-" + capture["id"] + ".json")
                path.write_text("{}", encoding="utf-8")
                return {"artifacts": [{"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}]}
            read = {"items": [self.ua_item(runner.ua_node("Cycles"))]} if capture["ua"] else {"status": "read", "value": 1}
            rows = [{"kind": "read", "errorCode": None, "observedUtc": capture["started"], "readResultJson": json.dumps(read)}] * capture["rows"]
            if capture.get("downtime"):
                rows[-1] = {"kind": "gap", "errorCode": None, "observedUtc": capture["downAt"]}
            return {"rows": rows[arguments.get("offset", 0):][:arguments.get("count", 50)]}
        capture = self.captures.get(arguments["captureId"])
        if verb == "start":
            if capture is None:
                self.live(arguments["request"]["attemptId"])
                capture = self.captures[arguments["captureId"]] = {"id": arguments["captureId"], "state": "running", "rows": 0, "host": self.host,
                                                                   "ua": tool.startswith("runtime_ua"), "ref": "ref-" + arguments["captureId"], "polls": 0,
                                                                   "limit": 6 if arguments["request"]["durationSeconds"] <= 10 else 10 ** 9, "started": self.now()}
        elif verb == "stop" and capture["state"] == "running":
            capture.update(state="completed", terminalReason="stopped")
        elif capture["state"] == "running" and capture["host"] == self.host:
            capture["polls"] += 1
            capture["rows"] += 1
            if capture["polls"] > capture["limit"]:
                capture.update(state="completed", terminalReason="durationLimit")
        state = "interrupted" if capture["state"] == "running" and capture["host"] != self.host else capture["state"]
        return {"state": state, "snapshotRef": capture["ref"], "captureId": capture["id"],
                "recordedProgress": {"rows": capture["rows"], "terminalReason": capture.get("terminalReason"), "remoteCleanupConfirmed": True}}


class Host:
    """A fake MCP Host process."""
    count = 0

    def __init__(self, plc, arguments):
        Host.count += 1
        self.plc, self.pid, self.exit_code, self.tool_calls, self.errors = plc, 1000 + Host.count, None, [], []
        self.point = arguments[arguments.index("--runtime-qualification") + 1] if "--runtime-qualification" in arguments else None
        plc.host = self.pid
        self.process = self

    def kill(self):
        self.exit_code = 1

    def wait(self, timeout=None):
        return self.exit_code

    def notify(self, *_):
        pass

    def close(self):
        if self.exit_code is None:
            for s in self.plc.sessions.values():
                if s["host"] == self.pid and s["state"] == "connected":
                    s["state"] = "closed"
        return {"hostExitCode": self.exit_code or 0, "forcedHostStop": False, "workerExits": [{"processExitVerified": True, "exitCode": 0, "forcedTermination": False}]
                if self.exit_code is None else [], "stderrEofObserved": True, "toolCalls": self.tool_calls}

    def request(self, method, params=None):
        if method != "tools/call":
            return {}
        if self.exit_code is not None:
            raise RuntimeError("MCP host exited")
        try:
            result = {"isError": False, "structuredContent": self.answer(params["name"], params.get("arguments") or {})}
        except Fail as error:
            result = {"isError": True, "structuredContent": {"error": {"code": error.code, "message": "refused"}}}
        self.tool_calls.append({"name": params["name"], "isError": result["isError"]})
        return result

    def tool(self, name, arguments=None):
        result = self.request("tools/call", {"name": name, "arguments": arguments or {}})
        if result["isError"]:
            raise RuntimeError("%s: %s" % (name, result))
        return result["structuredContent"]

    def answer(self, tool, a):
        p = self.plc
        if tool.startswith(("runtime_ua_subscription_", "runtime_ua_capture_", "runtime_capture_")) and tool != "runtime_capture_analyze":
            return p.acquisition(tool, a)
        if tool == "certificate_inspect":
            p.inspections += 1
            return {"devices": [{"certificates": [CERTS["plc"]] + ([CERTS["web"], CERTS["ua"]] if p.inspections > 1 else [])}]}
        if tool == "hardware_inspect":
            if a["hardwarePath"].endswith("PROFINET%20interface_1"):
                return {"items": [{"network": {"nodes": [{"name": "X1", "attributes": [{"name": "Address", "value": "192.168.0.1"}]}]}}]}
            return {"items": [{"attributes": [{"name": k, "value": v} for k, v in (("OpcUaServer", True), ("OpcUaAutoAcceptAllClientCertificates", True),
                                                                                  ("OpcUaPort", 4840), ("OpcUaApplicationUri", URI))]}]}
        if tool in ("sim_state", "sim_select", "sim_create"):
            if tool == "sim_state":
                p.power(a["action"])
            return {"observedAtUtc": p.now(), "instance": {"id": 7, "name": "TiaMcp_fake", "owned": True, "operatingState": p.state}, "downloadTicket": "ticket"}
        if tool == "sim_tags":
            p.stale = False
            return {"tags": [{"symbol": '"%s".%s' % (block, member)} for member, (block, _) in runner.SIGNALS.items() if block == a["filter"]]}
        if tool == "sim_read":
            return {"values": [{"symbol": s["symbol"], "quality": "bad" if p.stale else "good", "value": None if p.stale else p.memory[s["symbol"].split(".")[-1]]}
                               for s in a["signals"]]}
        if tool in ("runtime_ua_connect", "runtime_web_connect"):
            channel = tool.split("_")[1]
            return p.connect(channel, a["attemptId"], a["selection" if channel == "ua" else "login"])
        if tool in ("runtime_ua_status", "runtime_web_status"):
            return p.status(a["attemptId"])
        if tool in ("runtime_ua_disconnect", "runtime_web_disconnect"):
            s = p.sessions[a["attemptId"]]
            s["state"] = "closed" if s["state"] == "connected" and s["host"] == self.pid and s["alive"] else "abandonedRemoteUnknown" if a.get("acknowledgeUnknown") else "unknown"
            return p.status(a["attemptId"])
        if tool == "runtime_ua_read":
            p.live(a["attemptId"])
            return {"items": [p.ua_item(n) for n in a["nodes"]]}
        if tool == "runtime_web_read":
            p.live(a["attemptId"])
            return {"status": "read", "value": p.memory[a["selectionId"].split(".")[-1]]}
        if tool in ("runtime_ua_write", "runtime_web_write"):
            return p.write(self, tool.split("_")[1], a)
        if tool == "runtime_ua_browse":
            return {"references": [{"browseName": m, "displayName": m} for m in ("Marker", "Cycles", "UaValue")]}
        if tool == "runtime_web_browse":
            p.live(a["attemptId"])
            return {"selectionId": a["symbol"], "metadata": []} if a.get("children") is False else {"metadata": []}
        if tool == "runtime_web_diagnostics":
            return {"status": "read", "value": "run" if p.state == "Run" else "stop"}
        if tool == "runtime_web_operating_mode":
            p.state = {"stop": "Stop", "run": "Run"}[a["mode"]]
            return {"state": "changed", "readbackMatches": True}
        return {
            "tia_discover": {"processes": []}, "tia_session_status": {"ownsPortalProcess": True, "projects": []},
            "tia_create_scratch": {"completed": True, "compile": {"errorCount": 0}, "selectedCpu": {"typeIdentifier": "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"},
                                   "scratchSecurity": {"cpus": [{"credentialMetadataPath": "secret.json", "runtimeUser": {"supported": True, "credentialMetadataPath": str(p.user)}}]}},
            "project_inventory": {"plcs": [{"path": "/projects/P/devices/ScratchStation/items/ScratchPLC/plc/ScratchPLC"}]},
            "change_prepare": {"changeId": "c", "previewHash": "h", "state": "prepared", "preview": {}}, "change_apply": {"state": "applied"},
            "access_inspect": {"projectUmac": {"roleNames": {"r": "TiaMcpRuntime_1"}, "deviceRightScopes": ["d"]}},
            "opcua_engineering_inspect": {"serverSettings": [{"hardwarePath": "/opcua", "attributes": [{"name": "OpcUaServer"}]}]},
            "project_snapshot_capture": {"reviewId": "review"},
            "diagnostic_plan": {"id": "case", "plan": {"signals": [{"id": "enable", "resolution": "capturedDeclaration"}, {"id": "active", "resolution": "capturedDeclaration"}]}},
            "runtime_plcsim_bind": {"binding": {"sha256": "b" * 64}, "record": {"communicationInterface": "TCPIP", "controllerAddresses": ["192.168.0.1"]}},
            "runtime_ua_client_identity": {"certificateSha256": "c" * 64}, "runtime_ua_credential_binding": {"binding": "ua"},
            "runtime_web_credential_binding": {"binding": "web"}, "runtime_capture_analyze": {"id": "analysis"}, "sim_release": {"unregistered": True},
        }.get(tool, {})


class Fake(runner.Runner):
    def watch(self):
        self.watched, self.report["hosts"][-1]["children"] = [], []

    def credential(self, label, binding, vault):
        self.credentials.append((label, vault))
        return {"reference": label}

    def retire(self):
        self.report["credentialRetirement"] = [{"credential": c, "exitCode": 0} for c, _ in self.credentials]


class TagRefresh(unittest.TestCase):
    """A bad PLCSIM read is retried once after a sim_tags refresh; it fails only when the refreshed read is bad too."""

    def reader(self, qualities):
        class Stub:
            instance, symbols, calls = {"id": 7}, {"Marker": '"RealServer".Marker'}, []
            def tool(self, name, arguments=None):
                self.calls.append(name)
                if name == "sim_tags":
                    return {"tags": [{"symbol": '"%s".%s' % (block, member)} for member, (block, _) in runner.SIGNALS.items() if block == arguments["filter"]]}
                quality = qualities.pop(0)
                return {"values": [{"symbol": self.symbols["Marker"], "quality": quality, "value": 4711 if quality == "good" else None}]}
        stub = Stub()
        stub.calls = []
        stub.refresh_tags = lambda: runner.Runner.refresh_tags(stub)
        return stub

    def test_one_refresh_after_a_stale_read(self):
        stub = self.reader(["bad", "good"])
        row = runner.Runner.plcsim(stub, "Marker")
        self.assertEqual((row["quality"], row["value"], row["beforeTagRefresh"]["quality"]), ("good", 4711, "bad"))
        self.assertEqual(stub.calls, ["sim_read"] + ["sim_tags"] * len(runner.SIGNALS) + ["sim_read"])

    def test_good_read_needs_no_refresh_and_a_bad_refreshed_read_stays_bad(self):
        stub = self.reader(["good"])
        self.assertNotIn("beforeTagRefresh", runner.Runner.plcsim(stub, "Marker"))
        self.assertNotIn("sim_tags", stub.calls)
        stub = self.reader(["bad", "bad"])
        row = runner.Runner.plcsim(stub, "Marker")
        self.assertEqual(row["quality"], "bad")
        self.assertFalse(oracle.same(oracle.channel_value("ua", {"outcome": "value", "quality": "Good", "encoding": "decimalInteger", "value": "4711"}), row["value"]))


class Modes(unittest.TestCase):
    def run_mode(self, mode):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            plc = Plc(None)
            plc.user = root / "user.json"
            plc.user.write_text(json.dumps({"username": "TiaMcpTest_1", "protectedPath": "x.dpapi"}), encoding="utf-8")
            originals = {k: getattr(runner, k) for k in ("Client", "unprotect", "save_generated_scratch_project", "verify_download",
                                                          "verify_memory_card", "verify_credential_provisioning", "verify_owned_instance")}
            runner.Client = lambda executable, timeout, arguments: Host(plc, arguments)
            runner.unprotect = lambda path: "fake-password"
            runner.save_generated_scratch_project = lambda tool, download: {"applied": {"state": "applied"}}
            runner.verify_download = lambda download, instance: None
            runner.verify_memory_card = lambda download: {}
            runner.verify_credential_provisioning = lambda provisioned, download, **kw: {}
            runner.verify_owned_instance = lambda instance: instance
            try:
                exe = Path(sys.executable)
                args = argparse.Namespace(mode=mode, host=exe, worker=exe, sim_worker=exe, root=root / "r", cpu_ip="192.168.0.1", timeout=60, run_native=True)
                with mock.patch.object(library_native_harness, "require", lambda condition, message: None):  # Long temp paths; no scratch is made.
                    fake = Fake(args)
                plc.root = fake.directory
                sleep = runner.time.sleep
                with mock.patch.object(runner.time, "sleep", lambda seconds: sleep(min(seconds, 0.05))):
                    fake.run(runner.body)
                return json.loads((fake.directory / "verification.json").read_text(encoding="utf-8"))
            finally:
                for k, v in originals.items():
                    setattr(runner, k, v)

    def test_every_mode_passes_the_oracle(self):
        for mode in oracle.MODES:
            with self.subTest(mode=mode):
                verification = self.run_mode(mode)
                self.assertTrue(verification["completed"] and verification["oracle"]["issues"] == [], verification["oracle"])
                self.assertEqual(len(verification["hosts"]), oracle.HOSTS[mode])


if __name__ == "__main__":
    unittest.main()
