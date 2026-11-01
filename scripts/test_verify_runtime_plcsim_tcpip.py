"""Oracle refusals on tampered real-server runs; no native launch."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

import verify_runtime_plcsim_tcpip as oracle

UA, WEB = "a" * 64, "b" * 64
COUNTER = {"namespaceUri": oracle.SIEMENS_NS, "identifierType": "string", "identifier": '"RealServer"."UaValue"'}


def ua_read(value):
    return {"node": COUNTER, "outcome": "value", "quality": "Good", "encoding": "decimalInteger", "builtInType": "Int16", "value": str(value)}


def plcsim(value):
    return {"symbol": "RealServer.UaValue", "quality": "good", "value": value}


def step(name, result):
    return {"name": name, "status": "passed", "result": result}


def ua_status(attempt, state="connected", pin=UA):
    return {"schemaVersion": "opcua-runtime-session.v1", "attemptId": attempt, "state": state, "peerVerified": state == "connected",
            "selection": {"endpoint": "opc.tcp://192.168.0.1:4840/", "certificateSha256": pin}}


def web_status(attempt, state="connected", pin=WEB):
    return {"schemaVersion": "runtime-web-session.v2", "attemptId": attempt, "state": state, "peerVerified": state == "connected",
            "login": {"endpoint": "https://192.168.0.1", "peerCertificateSha256": pin}}


def host(label, code=0, pid=100):
    return {"label": label, "pid": pid, "expectedExitCode": code, "children": [],
            "exit": {"hostExitCode": code, "forcedHostStop": False, "workerExits": [{"processExitVerified": True, "exitCode": 0, "forcedTermination": False}]}}


def normal():
    certificates = {"devices": [{"certificates": [{"sha256": UA}, {"sha256": WEB}]}]}
    steps = [step("apply opcua_server_settings_set", {"state": "applied"}), step("simulation_download", {"completed": True}),
             step("save the generated state", {}), step(oracle.PIN_STEP, certificates),
             step("connect ua", ua_status("1" * 32)), step("connect web", web_status("2" * 32)),
             step(oracle.UA_WRITE, {"state": "written", "readbackMatches": [True]}),
             step(oracle.WEB_WRITE, {"state": "written", "items": [{"readbackMatches": True}]}),
             step(oracle.TO_STOP, {"state": "changed", "readbackMatches": True}), step(oracle.PLCSIM_STOP, "Stop"),
             step(oracle.TO_RUN, {"state": "changed", "readbackMatches": True}), step(oracle.PLCSIM_RUN, "Run")]
    return {"mode": "normal", "completed": True, "steps": steps, "pins": {"ua": {"sha256": UA, "step": oracle.PIN_STEP}, "web": {"sha256": WEB, "step": oracle.PIN_STEP}},
            "comparisons": [{"label": "UaValue after the UA write", "channel": "ua", "channelRead": ua_read(1234), "plcsimRead": plcsim(1234)},
                            {"label": "Enable after the web write", "channel": "web", "channelRead": {"status": "read", "value": True},
                             "plcsimRead": {"symbol": "DiagGate_DB.Enable", "quality": "good", "value": True}}],
            "hosts": [host("setup")], "attempts": {"ua": ["1" * 32], "web": ["2" * 32]},
            "toolCalls": [{"name": name, "isError": False} for name in oracle.TOOLS]}


def ua_write_loss():
    report = normal()
    report.update(mode="ua-write-loss", hosts=[host("setup", pid=1), host("fault", 74, pid=2), host("recovery", pid=3)])
    report["hosts"][1].update(children=[{"pid": 20, "exitVerified": True}], exit={"hostExitCode": 74, "forcedHostStop": False, "workerExits": []})
    report["steps"] += [step(oracle.name(oracle.LOST_STATUS, "ua"), ua_status("3" * 32, "hostEndedRemoteUnknown")),
                        step(oracle.name(oracle.RETRY, "ua"), {"code": "UaSessionUnavailable"}),
                        step(oracle.name(oracle.NEW_SESSION_REFUSED, "ua"), {"code": "UaEndpointSessionUnresolved"}),
                        step(oracle.name(oracle.ACKNOWLEDGED, "ua"), ua_status("3" * 32, "abandonedRemoteUnknown"))]
    report["comparisons"].append({"label": oracle.DECISION, "channel": "ua", "channelRead": ua_read(77), "plcsimRead": plcsim(77)})
    report["lostAttempt"] = "3" * 32
    report["attempts"]["ua"].append("3" * 32)
    return report


def named(report, step_name):
    return next(s for s in report["steps"] if s["name"] == step_name)


def fresh(channel):
    read = ua_read(4711) if channel == "ua" else {"status": "read", "value": 4711}
    return {"label": oracle.name(oracle.FRESH, channel), "channel": channel, "channelRead": read, "plcsimRead": plcsim(4711)}


def host_loss():
    report = normal()
    report.update(mode="host-loss-capture", hosts=[host("setup", pid=1), host("lost", 1, pid=2), host("recovery", pid=3)])
    report["hosts"][1].update(children=[{"pid": 20, "exitVerified": True}], exit={"hostExitCode": 1, "forcedHostStop": False, "workerExits": []})
    report["steps"].append(step(oracle.BEFORE_LOSS, {"ua": "running", "web": "running"}))
    for channel, status in (("ua", ua_status), ("web", web_status)):
        report["steps"] += [step(oracle.name(oracle.LOST_STATUS, channel), status("3" * 32, "hostEndedRemoteUnknown")),
                            step(oracle.name(oracle.NEW_SESSION_REFUSED, channel), {"code": oracle.REFUSALS[channel][1]}),
                            step(oracle.name(oracle.ACKNOWLEDGED, channel), status("3" * 32, "abandonedRemoteUnknown")),
                            step(oracle.name(oracle.AFTER_LOSS, channel), {"state": "interrupted", "recordedProgress": {"rows": 5}}),
                            step(oracle.name(oracle.RESUME, channel), {"state": "interrupted", "recordedProgress": {"rows": 5}})]
        report["comparisons"].append(fresh(channel))
    return report


def server_restart():
    report = normal()
    report["mode"] = "server-restart"
    report["steps"] += [step(oracle.CYCLE % "stop", {"observedAtUtc": "2026-10-29T10:01:00.1234567Z", "instance": {"operatingState": "Stop"}}),
                        step(oracle.START_BEFORE, {"items": [{"quality": "Good", "value": "2026-10-29T10:00:00Z"}]}),
                        step(oracle.START_AFTER, {"items": [{"quality": "Good", "value": "2026-10-29T10:05:00Z"}]}),
                        step(oracle.name(oracle.ACROSS, "ua"), [{"kind": "read", "errorCode": None, "observedUtc": "2026-10-29T10:00:59Z", "readResultJson": json.dumps({"items": [ua_read(1)]})},
                                                                  {"kind": "gap", "observedUtc": "2026-10-29T10:01:02.5+00:00"}]),
                        step(oracle.name(oracle.ACROSS, "web"), [{"kind": "read", "errorCode": None, "observedUtc": "2026-10-29T10:01:03Z", "readResultJson": json.dumps({"status": "failed"})}]),
                        step(oracle.name(oracle.FINISHED, "ua"), {"state": "completed", "recordedProgress": {"terminalReason": "durationLimit"}}),
                        step(oracle.name(oracle.FINISHED, "web"), {"state": "completed", "recordedProgress": {"terminalReason": "durationLimit"}}),
                        step(oracle.name(oracle.OLD_READ, "ua"), {"code": "UaSessionUnavailable"}),
                        step(oracle.name(oracle.OLD_READ, "web"), {"code": "RuntimeSessionUnavailable"})]
    report["comparisons"] += [fresh("ua"), fresh("web")]
    return report


def services():
    report = normal()
    report["mode"] = "ua-services"
    report["attempts"]["web"] = []
    report["steps"] = [s for s in report["steps"] if "web" not in s["name"] and s["name"] not in (oracle.UA_WRITE, oracle.TO_STOP, oracle.PLCSIM_STOP, oracle.TO_RUN, oracle.PLCSIM_RUN)]
    report["steps"] = [s for s in report["steps"] if s["name"] != "connect web"]

    def conditions(*alarms):
        return {"refresh": {"startObserved": True, "endObserved": True},
                "events": [{"kind": "refreshStart"}] + [{"kind": "condition", "conditionId": a, "activeState": True, "retain": True} for a in alarms] + [{"kind": "refreshEnd"}]}
    alarm, method = {"identifier": "alarm"}, {"identifier": '"ServerMethod_DB"."Method"'}
    report["steps"] += [step(oracle.METHOD_BROWSE, method), step(oracle.METHOD_SET, {"methods": [{"method": method}], "complete": True}),
                        step(oracle.UA_CALL, {"state": "called", "dispatched": True, "outputs": [{"encoding": "decimalInteger", "value": "42"}, {"encoding": "boolean", "value": False}]}),
                        step(oracle.CALL_UNACKNOWLEDGED, {"code": "RuntimeAcknowledgementRequired"}),
                        step(oracle.CALL_FAILED, {"state": "methodFailed", "dispatched": True, "statusCode": oracle.METHOD_BAD}),
                        step(oracle.HISTORY, {"product": {"code": "UaHistoryReadUnsupported"}, "historyReadRaw": {"statusCode": "0x80720000", "valueCount": None}}),
                        step(oracle.CONDITIONS[0], conditions()), step(oracle.CONDITIONS[1], conditions(alarm)), step(oracle.CONDITIONS[2], conditions())]
    report["steps"] += [step(label, {"state": "refused", "dispatched": False, "refusal": code}) for label, code in oracle.CALL_REFUSALS.items()]
    report["comparisons"] = [{"label": label, "channel": "ua", "channelRead": ua_read(n), "plcsimRead": plcsim(n)} for label, n in zip(oracle.CALLS, (0, 1, 1))]
    report["alarmCondition"] = {"raised": True, "conditionIds": [json.dumps(alarm, sort_keys=True)]}
    report["toolCalls"] = [{"name": name, "isError": False} for name in oracle.SERVICE_TOOLS]
    return report


CPU_NODE = {"hwid": 49, "name": "ScratchPLC", "type": "module", "subtype": "cpu"}
IM0 = {"order_number": "6ES7 511-1AL03-0AB0 ", "serial_number": "S V-L0X12345", "hardware_revision": 3, "software_revision": {"type": "V", "major": 4, "minor": 1, "patch": 0}}


def pinned(result=None, code=None, status=200):
    return {"httpStatus": status, "response": {"jsonrpc": "2.0", "id": 1, **({"result": result} if code is None else {"error": {"code": code}})}}


def browsed(hwid, nodes):
    return {"hardwareId": hwid, "tool": {"isError": False, "structuredContent": {"result": {"nodes": copy.deepcopy(nodes)}}}, "pinned": pinned({"nodes": nodes})}


def identified():
    """The CPU answers I&M0; its interface answers 1103, which SiemensWebIdentity reports as unsupported."""
    cpu = {"rowId": "r1", "name": "ScratchPLC", "online": {"status": "read", "hardwareId": 49}, "actual": {
        "mlfb": {"value": "6ES7 511-1AL03-0AB0"}, "serialNumber": {"value": "S V-L0X12345"}, "hardwareRevision": {"value": "3"}, "firmware": {"value": "V4.1.0"}}}
    interface = {"rowId": "r2", "name": "PROFINET interface_1", "online": {"status": "unsupported", "hardwareId": 64}, "actual": {k: {"value": None} for k in oracle.ACTUAL}}
    return {"controllerRowId": "r1", "controllerHardwareId": 49, "bindings": [{"rowId": "r1", "hardwareId": 49}, {"rowId": "r2", "hardwareId": 64}],
            "tool": {"selectedRows": [cpu, interface]}, "pinned": [pinned({"data": copy.deepcopy(IM0)}), pinned(code=1103)]}


def device_inventory():
    report = normal()
    report.update(mode="device-inventory", hosts=[host("setup", pid=1), host("inventory", pid=2)], comparisons=[], attempts={"ua": [], "web": []},
                  toolCalls=[{"name": name, "isError": False} for name in oracle.INVENTORY_TOOLS])
    report["steps"] = report["steps"][:4] + [step(oracle.TRUST, "trusted"), step(oracle.IN_ROOT, True), step(oracle.BROWSE, [
        browsed(None, [CPU_NODE]), browsed(49, [{"hwid": 64, "name": "PROFINET interface_1", "subtype": "interface"}]), browsed(64, [])]),
        step(oracle.IDENTIFY, identified())]
    return report


class Oracle(unittest.TestCase):
    def issues(self, report, marker=None, stored=None):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            (root / "qualification.json").write_text(json.dumps(report), encoding="utf-8")
            for channel, folder in (("ua", "runtime-ua"), ("web", "runtime-web")):
                (root / folder).mkdir()
                for attempt in (stored or report.get("attempts", {})).get(channel, []):
                    (root / folder / (attempt + ".json")).write_text("{}", encoding="utf-8")
            if marker is not None:
                (root / oracle.MARKER).write_text(json.dumps(marker), encoding="utf-8")
            return oracle.verify(root)["issues"]

    def marker(self):
        return {"point": "ua.write.afterSend", "hostProcessId": 2, "automaticReplay": False, "intent": {"attemptId": "3" * 32}}

    def test_untampered_runs_pass(self):
        self.assertEqual(self.issues(normal()), [])
        self.assertEqual(self.issues(ua_write_loss(), self.marker()), [])

    def test_ua_value_that_differs_from_the_plcsim_read_is_an_issue(self):
        report = normal()
        report["comparisons"][0]["plcsimRead"]["value"] = 1235
        self.assertTrue(any("differs" in issue for issue in self.issues(report)))

    def test_bad_quality_or_a_missing_channel_comparison_is_an_issue(self):
        report = normal()
        report["comparisons"][0]["channelRead"]["quality"] = "Uncertain"
        self.assertTrue(self.issues(report))
        report = normal()
        del report["comparisons"][1]
        self.assertTrue(any("web" in issue for issue in self.issues(report)))

    def test_pin_taken_before_compile_is_an_issue(self):
        report = normal()
        steps = report["steps"]
        steps.insert(1, steps.pop(3))
        self.assertTrue(any("compile" in issue for issue in self.issues(report)))

    def test_connect_with_another_pin_is_an_issue(self):
        report = normal()
        report["steps"][4]["result"]["selection"]["certificateSha256"] = "c" * 64
        self.assertTrue(any("pin" in issue for issue in self.issues(report)))

    def test_missing_host_exit_proof_is_an_issue(self):
        report = normal()
        del report["hosts"][0]["exit"]
        self.assertTrue(any("exit proof" in issue for issue in self.issues(report)))
        report = normal()
        report["hosts"][0]["exit"]["workerExits"][0]["processExitVerified"] = False
        self.assertTrue(any("exit proof" in issue for issue in self.issues(report)))

    def test_session_the_runner_did_not_open_is_an_issue(self):
        stored = {"ua": ["1" * 32, "9" * 32], "web": ["2" * 32]}
        self.assertTrue(any("attempt" in issue for issue in self.issues(normal(), stored=stored)))

    def test_lost_write_needs_its_marker_exit_and_refused_retry(self):
        self.assertTrue(self.issues(ua_write_loss()))
        for change in (lambda m: m.update(automaticReplay=True), lambda m: m.update(point="web.write.afterSend"), lambda m: m.update(hostProcessId=3)):
            marker = self.marker(); change(marker)
            self.assertTrue(self.issues(ua_write_loss(), marker))
        report = ua_write_loss(); report["hosts"][1]["exit"]["hostExitCode"] = 0
        self.assertTrue(self.issues(report, self.marker()))
        report = ua_write_loss(); next(s for s in report["steps"] if s["name"] == oracle.name(oracle.RETRY, "ua"))["result"] = {"state": "written"}
        self.assertTrue(any("retry" in issue for issue in self.issues(report, self.marker())))
        report = ua_write_loss(); next(s for s in report["steps"] if s["name"] == oracle.name(oracle.LOST_STATUS, "ua"))["result"]["state"] = "closed"
        self.assertTrue(self.issues(report, self.marker()))

    def test_host_loss_needs_an_unresumed_interrupted_prefix(self):
        self.assertEqual(self.issues(host_loss()), [])
        report = host_loss(); next(s for s in report["steps"] if s["name"] == oracle.name(oracle.RESUME, "web"))["result"]["recordedProgress"]["rows"] = 6
        self.assertTrue(any("resumed" in issue for issue in self.issues(report)))
        report = host_loss(); report["hosts"][1]["children"][0]["exitVerified"] = False
        self.assertTrue(any("exit proof" in issue for issue in self.issues(report)))

    def test_server_restart_needs_a_new_start_time_and_recorded_downtime(self):
        self.assertEqual(self.issues(server_restart()), [])
        report = server_restart(); named(report, oracle.START_AFTER)["result"]["items"][0]["value"] = "2026-10-29T10:00:00Z"
        self.assertTrue(any("start time" in issue for issue in self.issues(report)))
        report = server_restart(); named(report, oracle.name(oracle.ACROSS, "web"))["result"] = [{"kind": "read", "errorCode": None, "readResultJson": json.dumps({"status": "read", "value": 1})}]
        self.assertTrue(any("downtime" in issue for issue in self.issues(report)))
        report = server_restart(); named(report, oracle.name(oracle.OLD_READ, "web"))["result"] = {"status": "read", "value": 1}
        self.assertTrue(any("still answers" in issue for issue in self.issues(report)))

    def test_credential_failures_and_rows_before_stop_are_not_restart_downtime(self):
        # Run rs_db5b4b21: the UA capture's only failure was UaCredentialUnavailable (vault contention with the web capture).
        report = server_restart()
        named(report, oracle.name(oracle.ACROSS, "ua"))["result"].append({"kind": "read", "errorCode": "UaCredentialUnavailable", "observedUtc": "2026-10-29T10:01:04Z"})
        self.assertTrue(any("credential" in issue for issue in self.issues(report)))
        report = server_restart()
        named(report, oracle.name(oracle.ACROSS, "ua"))["result"][1]["observedUtc"] = "2026-10-29T10:01:00.1Z"
        self.assertTrue(any("ua capture records no downtime" in issue for issue in self.issues(report)))
        report = server_restart()
        named(report, oracle.name(oracle.ACROSS, "ua"))["result"][1]["observedUtc"] = "2026-10-29T10:01:00.0999999Z"
        named(report, oracle.name(oracle.FINISHED, "ua"))["result"] = {"state": "ended", "recordedProgress": {"terminalReason": "sessionUnavailable", "observedUtc": "2026-10-29T10:00:59Z"}}
        self.assertTrue(any("ua capture records no downtime" in issue for issue in self.issues(report)))

    def test_host_loss_needs_both_acquisitions_running_at_the_kill(self):
        # Run rs_abde1bb3: the UA subscription had already ended (sessionUnavailable) before the Host was terminated.
        report = host_loss(); named(report, oracle.BEFORE_LOSS)["result"] = {"ua": "ended", "web": "running"}
        self.assertTrue(any("still run" in issue for issue in self.issues(report)))

    def test_services_need_one_counted_call_refusals_the_history_boundary_and_a_falling_condition(self):
        self.assertEqual(self.issues(services()), [])
        report = services(); report["alarmCondition"] = {"raised": False}  # PLCSIM raised nothing: recorded, not a product failure.
        self.assertEqual(self.issues(report), [])
        report = services(); named(report, oracle.CALL_STANDARD)["result"]["dispatched"] = True
        self.assertTrue(any("standard server method" in issue for issue in self.issues(report)))
        report = services(); report["comparisons"][1]["plcsimRead"]["value"] = report["comparisons"][1]["channelRead"]["value"] = 2
        self.assertTrue(any("exactly one successful call" in issue for issue in self.issues(report)))
        report = services(); named(report, oracle.HISTORY)["result"]["historyReadRaw"] = {"statusCode": "0x00000000", "valueCount": 1}
        self.assertTrue(any("HistoryRead" in issue for issue in self.issues(report)))
        report = services(); named(report, oracle.CONDITIONS[2])["result"] = named(report, oracle.CONDITIONS[1])["result"]
        self.assertTrue(any("still active" in issue for issue in self.issues(report)))
        report = services(); named(report, oracle.METHOD_SET)["result"]["methods"] = []
        self.assertTrue(any("method set" in issue for issue in self.issues(report)))
        report = services(); named(report, oracle.CONDITIONS[0])["result"]["refresh"]["endObserved"] = False
        self.assertTrue(any("ConditionRefresh boundaries" in issue for issue in self.issues(report)))

    def test_device_inventory_compares_every_field_with_the_pinned_reads(self):
        self.assertEqual(self.issues(device_inventory()), [])
        for change, text in ((lambda r: named(r, oracle.IDENTIFY)["result"]["pinned"][0]["response"]["result"]["data"].update(serial_number="S V-OTHER"), "serialNumber"),
                             (lambda r: named(r, oracle.IDENTIFY)["result"]["pinned"][0]["response"]["result"]["data"]["software_revision"].update(patch=1), "firmware"),
                             (lambda r: named(r, oracle.IDENTIFY)["result"]["pinned"].__setitem__(1, pinned({"data": IM0})), "expects read"),
                             (lambda r: named(r, oracle.IDENTIFY)["result"]["tool"]["selectedRows"][0]["online"].update(hardwareId=50), "at hwid 50"),
                             (lambda r: named(r, oracle.BROWSE)["result"][1]["pinned"]["response"]["result"]["nodes"][0].update(name="Port_1"), "field name"),
                             (lambda r: named(r, oracle.BROWSE)["result"][0]["pinned"]["response"]["result"]["nodes"][0].update(hwid=50), "hardware IDs"),
                             (lambda r: named(r, oracle.BROWSE)["result"][2].update(pinned=pinned(code=1100)), "the pinned call failed"),
                             (lambda r: named(r, oracle.TRUST).update(result="abort"), "trusted"),
                             (lambda r: r["hosts"].pop(), "Expected 2 Hosts")):
            report = device_inventory()
            change(report)
            self.assertTrue(any(text in issue for issue in self.issues(report)), text)

    def test_device_inventory_failures_agree_and_the_cpu_must_answer(self):
        row = browsed(64, [])
        row.update(tool={"isError": True, "structuredContent": {"error": {"code": "IdentityMismatch"}}}, pinned=pinned(code=1100))
        self.assertEqual(oracle.browse_issues(row), [])
        denied = identified()
        denied["pinned"][0] = pinned(code=2)
        denied["tool"]["selectedRows"][0].update(online={"status": "permissionDenied", "hardwareId": 49}, actual={k: {"value": None} for k in oracle.ACTUAL})
        self.assertEqual(oracle.identify_issues(denied), ["The CPU row did not answer I&M0"])
        denied["pinned"][0] = pinned(status=401)
        self.assertEqual(oracle.identify_issues(denied), ["The CPU row did not answer I&M0"])

    def test_a_failed_run_is_never_complete(self):
        report = normal(); report["completed"] = False
        self.assertTrue(self.issues(report))
        report = normal(); report["toolCalls"] = [c for c in report["toolCalls"] if c["name"] != "runtime_ua_subscription_stop"]
        self.assertTrue(any("runtime_ua_subscription_stop" in issue for issue in self.issues(report)))


if __name__ == "__main__":
    unittest.main()
