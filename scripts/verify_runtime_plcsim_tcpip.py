"""Independent file-only oracle over one retained run of qualify_runtime_plcsim_tcpip.py; no native execution.

Every UA or web value is compared with the PLCSIM API read of the same tag, every connected session with the
certificate pin taken after the last compile, every Host and worker with its exit proof, and the retained session
stores with the attempts the runner opened (nothing reconnects or logs in on its own). Recovery modes add their
boundaries: an interrupted acquisition is never resumed, a lost write is never replayed and only an explicit read
decides it.
"""
import argparse
import datetime
import itertools
import json
from pathlib import Path

SIEMENS_NS = "http://www.siemens.com/simatic-s7-opcua"
MODES = ("normal", "host-loss-capture", "server-restart", "ua-write-loss", "web-write-loss")
POINTS = {"ua-write-loss": "ua.write.afterSend", "web-write-loss": "web.write.afterSend"}
HOSTS = {"normal": 1, "server-restart": 1, "host-loss-capture": 3, "ua-write-loss": 3, "web-write-loss": 3}
MARKER = "runtime-interruption-marker.json"
COMPILES = ("apply ", "simulation_download", "save the generated state")
PIN_STEP = "certificate_inspect after the last compile"
UA_WRITE, WEB_WRITE = "runtime_ua_write", "runtime_web_write"
TO_STOP, TO_RUN = "runtime_web_operating_mode RUN to STOP", "runtime_web_operating_mode STOP to RUN"
PLCSIM_STOP, PLCSIM_RUN = "PLCSIM reports STOP", "PLCSIM reports RUN"
LOST_STATUS, RETRY = "status of the lost session", "refuse a retry of the lost write"
NEW_SESSION_REFUSED, ACKNOWLEDGED = "refuse a new session before acknowledgement", "acknowledge the lost session"
DECISION, FRESH = "explicit read decides the lost write", "read through a fresh session"
AFTER_LOSS, RESUME = "acquisition status after Host loss", "no implicit resume"
BEFORE_LOSS = "both acquisitions still run right before the Host loss"
CYCLE = "sim_state %s during the captures"
START_BEFORE, START_AFTER = "server start time before the restart", "server start time after the restart"
ACROSS, OLD_READ = "rows across the restart", "read on the old session after the restart"
FINISHED = "acquisition status after the restart"
REFUSALS = {"ua": ("UaSessionUnavailable", "UaEndpointSessionUnresolved"), "web": ("RuntimeSessionUnavailable", "RuntimeEndpointSessionUnresolved")}
TOOLS = ("runtime_plcsim_bind",
         "runtime_ua_client_identity", "runtime_ua_credential_binding", "runtime_ua_connect", "runtime_ua_browse", "runtime_ua_read",
         "runtime_ua_write", "runtime_ua_status", "runtime_ua_disconnect", "runtime_ua_capture_start", "runtime_ua_capture_status",
         "runtime_ua_capture_stop", "runtime_ua_capture_page", "runtime_ua_capture_export", "runtime_ua_subscription_start",
         "runtime_ua_subscription_status", "runtime_ua_subscription_stop", "runtime_ua_subscription_page", "runtime_ua_subscription_export",
         "runtime_web_credential_binding", "runtime_web_connect", "runtime_web_browse", "runtime_web_read", "runtime_web_diagnostics",
         "runtime_web_write", "runtime_web_operating_mode", "runtime_web_status", "runtime_web_disconnect",
         "runtime_capture_start", "runtime_capture_status", "runtime_capture_stop", "runtime_capture_page", "runtime_capture_export",
         "runtime_capture_analyze")


def name(base, channel):
    return "%s (%s)" % (base, channel)


def channel_value(channel, read):
    """The decoded value of a UA read item or a web read result, or None when it is not a good value."""
    if channel == "ua":
        if read.get("outcome") != "value" or read.get("quality") != "Good":
            return None
        decode = {"decimalInteger": int, "boolean": lambda v: v, "roundTripFloat": float, "roundTripDouble": float, "string": lambda v: v}
        return decode[read["encoding"]](read["value"]) if read.get("encoding") in decode else None
    return read.get("value") if read.get("status") == "read" else None


def utc(text):
    """An ISO 8601 UTC time with any number of fractional digits, or None."""
    try:
        whole, _, rest = text.partition(".")
        digits = "".join(itertools.takewhile(str.isdigit, rest))
        return datetime.datetime.strptime(whole[:19], "%Y-%m-%dT%H:%M:%S") + datetime.timedelta(microseconds=int((digits + "000000")[:6]))
    except (AttributeError, TypeError, ValueError):
        return None


def same(left, right):
    return left is not None and isinstance(left, bool) == isinstance(right, bool) and left == right


def verify(root):
    root = Path(root).resolve()
    issues = []

    def need(condition, message):
        if not condition:
            issues.append(message)
        return bool(condition)

    try:
        report = json.loads((root / "qualification.json").read_text(encoding="utf-8"))
        mode = report.get("mode")
        need(mode in MODES, "Unknown mode %r" % mode)
        need(report.get("completed") is True, "The run did not complete")
        steps = report.get("steps", [])
        index = {s["name"]: i for i, s in enumerate(steps)}

        def result(step):
            return steps[index[step]].get("result") if need(step in index, "Missing step: " + step) else {}

        def lost(channel):
            need(result(name(LOST_STATUS, channel)).get("state") == "hostEndedRemoteUnknown", "The lost %s session is not hostEndedRemoteUnknown" % channel)
            need(result(name(NEW_SESSION_REFUSED, channel)).get("code") == REFUSALS[channel][1], "A new %s session was admitted before the lost one was acknowledged" % channel)
            need(result(name(ACKNOWLEDGED, channel)).get("state") == "abandonedRemoteUnknown", "The lost %s session was not acknowledged" % channel)

        # Channel values against the PLCSIM API read of the same tag.
        compared = set()
        for row in report.get("comparisons", []):
            plcsim = row["plcsimRead"]
            value = channel_value(row["channel"], row["channelRead"])
            need(plcsim.get("quality") == "good" and same(value, plcsim.get("value")),
                 "%s: the %s read %r differs from the PLCSIM API read %r" % (row["label"], row["channel"], value, plcsim.get("value")))
            compared.add(row["channel"])
        for channel in {"ua-write-loss": ("ua",), "web-write-loss": ("web",)}.get(mode, ("ua", "web")):
            need(channel in compared, "No %s value is compared with the PLCSIM API" % channel)

        # Pins come from certificate_inspect after the last compile and every connected session uses them.
        pins = report.get("pins", {})
        last_compile = max((i for i, s in enumerate(steps) if s["name"].startswith(COMPILES)), default=-1)
        for channel in ("ua", "web"):
            pin = pins.get(channel) or {}
            if need(pin.get("step") in index and index[pin["step"]] > last_compile, "The %s pin was not taken after the last compile" % channel):
                inspected = [c["sha256"] for d in result(pin["step"])["devices"] for c in d["certificates"]]
                need(pin.get("sha256") in inspected, "The %s pin is not a certificate the inspection returned" % channel)
        for step in steps:
            status = step.get("result")
            if isinstance(status, dict) and status.get("state") == "connected" and "schemaVersion" in status:
                ua = status["schemaVersion"].startswith("opcua-runtime-session")
                used = status["selection"]["certificateSha256"] if ua else status["login"]["peerCertificateSha256"]
                need(status.get("peerVerified") is True and used == (pins.get("ua" if ua else "web") or {}).get("sha256"),
                     "%s connected with another pin or an unverified peer" % step["name"])

        # Exit proofs of every Host and of the workers it left.
        hosts = report.get("hosts", [])
        need(len(hosts) == HOSTS.get(mode), "Expected %s Hosts, observed %d" % (HOSTS.get(mode), len(hosts)))
        for host in hosts:
            close = host.get("exit")
            if not need(close, "Host %s has no exit proof" % host.get("label")):
                continue
            need(close.get("hostExitCode") == host.get("expectedExitCode") and close.get("forcedHostStop") is False,
                 "Host %s exit proof differs: %s" % (host.get("label"), close.get("hostExitCode")))
            need(all(w.get("processExitVerified") is True and w.get("exitCode") == 0 and w.get("forcedTermination") is False for w in close.get("workerExits", []))
                 and all(c.get("exitVerified") is True for c in host.get("children", [])), "A worker of Host %s has no exit proof" % host.get("label"))

        # The session stores hold exactly the attempts the runner opened.
        attempts = report.get("attempts", {})
        for channel, folder in (("ua", "runtime-ua"), ("web", "runtime-web")):
            retained = {p.stem for p in (root / folder).glob("*.json")}
            need(retained == set(attempts.get(channel, [])), "The %s store holds other attempts than the runner opened: %s" % (channel, sorted(retained ^ set(attempts.get(channel, [])))))

        if mode == "normal":
            passed = {c["name"] for c in report.get("toolCalls", []) if not c["isError"]}
            for tool in TOOLS:
                need(tool in passed, "No successful call of " + tool)
            ua = result(UA_WRITE)
            need(ua.get("state") == "written" and ua.get("readbackMatches") and all(ua["readbackMatches"]), "The UA write was not read back")
            web = result(WEB_WRITE)
            need(web.get("state") == "written" and web.get("items") and all(i["readbackMatches"] for i in web["items"]), "The web write was not read back")
            for request, observed, state in ((TO_STOP, PLCSIM_STOP, "Stop"), (TO_RUN, PLCSIM_RUN, "Run")):
                need(result(request).get("state") == "changed" and result(observed) == state, "%s is not confirmed by PLCSIM" % request)
        elif mode in POINTS:
            channel = "ua" if mode == "ua-write-loss" else "web"
            fault = hosts[1] if len(hosts) > 1 else {}
            path = root / MARKER
            if need(path.is_file(), "The fault Host left no interruption marker"):
                marker = json.loads(path.read_text(encoding="utf-8"))
                need(marker.get("point") == POINTS[mode] and marker.get("automaticReplay") is False and marker.get("hostProcessId") == fault.get("pid")
                     and (marker.get("intent") or {}).get("attemptId") == report.get("lostAttempt"), "The interruption marker differs")
            need(fault.get("expectedExitCode") == 74, "The fault Host was not expected to exit with 74")
            lost(channel)
            need(result(name(RETRY, channel)).get("code") == REFUSALS[channel][0], "The retry of the lost write was not refused")
            need(any(r["label"] == DECISION and r["channel"] == channel for r in report.get("comparisons", [])), "No explicit read decides the lost write")
        elif mode == "host-loss-capture":
            need(hosts[1].get("expectedExitCode") == 1 if len(hosts) > 1 else False, "The lost Host was not terminated")
            need(result(BEFORE_LOSS) == {"ua": "running", "web": "running"}, "Both acquisitions must still run when the Host is terminated")
            for channel in ("ua", "web"):
                lost(channel)
                after, resumed = result(name(AFTER_LOSS, channel)), result(name(RESUME, channel))
                need(after.get("state") == "interrupted" and after.get("recordedProgress", {}).get("rows", 0) > 0, "The %s acquisition is not an interrupted prefix" % channel)
                need(resumed.get("state") != "running" and resumed.get("recordedProgress", {}).get("rows") == after.get("recordedProgress", {}).get("rows"),
                     "The %s acquisition resumed implicitly" % channel)
                need(any(r["label"] == name(FRESH, channel) for r in report.get("comparisons", [])), "No fresh %s session after the Host loss" % channel)
        elif mode == "server-restart":
            before, after = (result(s).get("items", [{}])[0] for s in (START_BEFORE, START_AFTER))
            need(before.get("quality") == after.get("quality") == "Good" and before.get("value") and before.get("value") != after.get("value"),
                 "The server start time did not change")
            # Downtime counts only from the STOP request on, and a credential failure is never the restart's downtime.
            stopped = utc(result(CYCLE % "stop").get("observedAtUtc"))
            need(stopped, "The STOP of the power cycle has no time")
            for channel in ("ua", "web"):
                rows = result(name(ACROSS, channel)) or []
                need(not any("Credential" in (r.get("errorCode") or "") for r in rows), "The %s capture failed on its credential, not on the restart" % channel)
                down = [r for r in rows if stopped and (utc(r.get("observedUtc")) or datetime.datetime.min) >= stopped and (r.get("kind") == "gap" or r.get("errorCode")
                        or r.get("kind") == "read" and channel_value(channel, json.loads(r["readResultJson"])["items"][0] if channel == "ua" else json.loads(r["readResultJson"])) is None)]
                finished = result(name(FINISHED, channel))
                final = finished.get("recordedProgress", {})
                # A failed read or gap row after STOP, or an acquisition that ended after STOP on the failure instead of its own limit.
                ended = (final.get("terminalReason") not in ("durationLimit", "sampleLimit", "notificationLimit", "byteLimit", None) or finished.get("localFailure")) \
                    and stopped and (utc(final.get("observedUtc")) or datetime.datetime.min) >= stopped
                need(down or ended, "The %s capture records no downtime" % channel)
                need(result(name(OLD_READ, channel)).get("code"), "The old %s session still answers after the restart" % channel)
                need(any(r["label"] == name(FRESH, channel) for r in report.get("comparisons", [])), "No fresh %s session after the restart" % channel)
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError) as error:
        issues.append("%s: %s" % (type(error).__name__, error))
    return {"completed": not issues, "kind": "runtime-plcsim-tcpip", "nativeInvoked": False, "issues": issues}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = verify(args.root)
    text = json.dumps(result, indent=2)
    if args.output:
        args.output.write_text(text, encoding="utf-8")
    print(text)
    return 0 if result["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
