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
    report["steps"] += [step(oracle.START_BEFORE, {"items": [{"quality": "Good", "value": "2026-10-29T10:00:00Z"}]}),
                        step(oracle.START_AFTER, {"items": [{"quality": "Good", "value": "2026-10-29T10:05:00Z"}]}),
                        step(oracle.name(oracle.ACROSS, "ua"), [{"kind": "read", "errorCode": None, "readResultJson": json.dumps({"items": [ua_read(1)]})}, {"kind": "gap"}]),
                        step(oracle.name(oracle.ACROSS, "web"), [{"kind": "read", "errorCode": None, "readResultJson": json.dumps({"status": "failed"})}]),
                        step(oracle.name(oracle.FINISHED, "ua"), {"state": "completed", "recordedProgress": {"terminalReason": "durationLimit"}}),
                        step(oracle.name(oracle.FINISHED, "web"), {"state": "completed", "recordedProgress": {"terminalReason": "durationLimit"}}),
                        step(oracle.name(oracle.OLD_READ, "ua"), {"code": "UaSessionUnavailable"}),
                        step(oracle.name(oracle.OLD_READ, "web"), {"code": "RuntimeSessionUnavailable"})]
    report["comparisons"] += [fresh("ua"), fresh("web")]
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

    def test_a_failed_run_is_never_complete(self):
        report = normal(); report["completed"] = False
        self.assertTrue(self.issues(report))
        report = normal(); report["toolCalls"] = [c for c in report["toolCalls"] if c["name"] != "runtime_ua_subscription_stop"]
        self.assertTrue(any("runtime_ua_subscription_stop" in issue for issue in self.issues(report)))


if __name__ == "__main__":
    unittest.main()
