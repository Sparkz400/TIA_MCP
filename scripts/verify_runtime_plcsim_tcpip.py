"""Independent file-only oracle over one retained run of qualify_runtime_plcsim_tcpip.py; no native execution.

Every UA or web value is compared with the PLCSIM API read of the same tag, every connected session with the
certificate pin taken after the last compile, every Host and worker with its exit proof, and the retained session
stores with the attempts the runner opened (nothing reconnects or logs in on its own). Recovery modes add their
boundaries: an interrupted acquisition is never resumed, a lost write is never replayed and only an explicit read
decides it. ua-services adds the server-method call counted by the PLCSIM API, its refusals, the HistoryRead boundary
and the ConditionRefresh snapshots around a PLCSIM-raised alarm. device-inventory compares every device_online_browse and
device_identify result field by field with the pinned Modules.Browse and I&M0 reads retained beside it. web-services checks
runtime_web_alarm_acknowledge, runtime_web_download and runtime_web_backup: every refusal before sending, every result
against the methods the session's Api.Browse advertised, every stored file against artifact_read and the file's own hash,
and the PLCSIM API's operating state around the backup; it reports per tool the outcome and the release-matrix gates the
run demonstrated, so a run that only records a missing method is never read as a qualification.
"""
import argparse
import datetime
import itertools
import json
from pathlib import Path

SIEMENS_NS = "http://www.siemens.com/simatic-s7-opcua"
MODES = ("normal", "host-loss-capture", "server-restart", "ua-write-loss", "web-write-loss", "ua-services", "device-inventory", "web-services")
POINTS = {"ua-write-loss": "ua.write.afterSend", "web-write-loss": "web.write.afterSend"}
HOSTS = {"normal": 1, "server-restart": 1, "host-loss-capture": 3, "ua-write-loss": 3, "web-write-loss": 3, "ua-services": 1, "device-inventory": 2,
         "web-services": 1}
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
EVENT_MODEL, METHOD_BROWSE = "runtime_ua_browse Server (event model)", "runtime_ua_browse ServerMethod_DB (server method)"
METHOD_SET = "the PLC namespace's methods below Objects"
UA_CALL, CALL_FAILED, METHOD_BAD = "runtime_ua_call", "runtime_ua_call whose method returns Bad", "0x80ab0000"
CALL_UNACKNOWLEDGED = "runtime_ua_call without acknowledgement"
CALL_STANDARD, CALL_ARGUMENTS = "runtime_ua_call of a standard server method", "runtime_ua_call with a wrong argument count"
CALL_REFUSALS = {CALL_STANDARD: "UaMethodNotServerMethod", CALL_ARGUMENTS: "UaMethodArgumentInvalid"}
CALLS = ("MethodCalls before the call", "MethodCalls after the call", "MethodCalls after the failed call")
HISTORY = "runtime_ua_history_probe"
CONDITIONS = ("runtime_ua_conditions before the alarm", "runtime_ua_conditions with the alarm raised", "runtime_ua_conditions after the alarm falls")
SERVICE_TOOLS = ("runtime_plcsim_bind", "runtime_ua_connect", "runtime_ua_browse", "runtime_ua_read", "runtime_ua_call", "runtime_ua_history_probe",
                 "runtime_ua_conditions", "runtime_ua_disconnect")
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
TRUST, IN_ROOT = "the coordinator's trust command", "the web certificate is in Cert:\\CurrentUser\\Root"
BROWSE, IDENTIFY = "device_online_browse beside the pinned Modules.Browse", "device_identify beside the pinned I&M0 reads"
INVENTORY_TOOLS = ("device_inventory", "device_online_browse", "device_identify")
ACTUAL = ("mlfb", "serialNumber", "hardwareRevision", "firmware")
# SiemensWebIdentity's row state for a JSON-RPC error code of Modules.ReadIdentificationMaintenance; any other code is readError.
IM_FAILURES = {2: "permissionDenied", -32601: "unsupported", 1100: "identityMismatch", 1102: "unreachable", 1103: "unsupported"}
# web-services: the Web API methods each operation needs in the session's Api.Browse list (the Host sends nothing otherwise),
# its step names, and the release-matrix gates a demonstrated outcome stands for. A gated step's result is the tool's answer
# or, for a tool error, {"isError": true, "code": ...}.
WS_METHODS = {"alarm": ("Alarms.Acknowledge", "Alarms.Browse"), "profiling": ("PlcProgram.DownloadProfilingData", "Api.CloseTicket"),
              "serviceData": ("Modules.DownloadServiceData", "Api.CloseTicket"), "backup": ("Plc.CreateBackup", "Api.CloseTicket")}
WS_STATUS, WS_HWID = "runtime_web_status of the web-services session", "the CPU's hardware ID from the pinned Modules.Browse"
LANGUAGES = "runtime_web_diagnostics languages"
ALARM_LISTS = ("runtime_web_diagnostics alarms before the trigger", "runtime_web_diagnostics alarms with the trigger raised",
               "runtime_web_diagnostics alarms after the acknowledgement", "runtime_web_diagnostics alarms after the trigger fell")
TRIGGER = ("AlarmTrigger before the trigger", "AlarmTrigger with the trigger raised")
ACK, ACK_AGAIN = "runtime_web_alarm_acknowledge", "runtime_web_alarm_acknowledge again"
ACK_NOT_LISTED, ACK_FALLEN = "runtime_web_alarm_acknowledge of an alarm that is not listed", "runtime_web_alarm_acknowledge after the trigger fell"
NOT_LISTED_ALARM = "999999999"
UNRETAINED_BINDING = "0" * 64
# None: any refusal code (an unretained binding fails reading the binding artifact, whose code the run records).
ACK_REFUSALS = {"runtime_web_alarm_acknowledge without acknowledgement": "RuntimeAcknowledgementRequired",
                "runtime_web_alarm_acknowledge with an unretained binding": None}
DOWNLOAD = {"profiling": "runtime_web_download profiling", "serviceData": "runtime_web_download serviceData of the CPU"}
DOWNLOAD_REFUSALS = {"runtime_web_download of kind backup": "RuntimeTicketKindUnsupported",
                     "runtime_web_download serviceData without a hwid": "RuntimeTicketSelectionInvalid",
                     "runtime_web_download profiling with a hwid": "RuntimeTicketSelectionInvalid",
                     "runtime_web_download serviceData with hwid 65536": "RuntimeTicketSelectionInvalid"}
BACKUP_REFUSALS = {"runtime_web_backup without acknowledgement": "RuntimeAcknowledgementRequired", "runtime_web_backup with an unretained binding": None}
BACKUP_RUN, BACKUP_RUN_STATE = "runtime_web_backup in RUN", "PLCSIM still reports RUN after the refused backup"
BACKUP, BACKUP_STATE = "runtime_web_backup in STOP", "PLCSIM still reports STOP after the backup"
STORED = "artifact_read and the file hash of %s"
WS_TOOLS = ("runtime_plcsim_bind", "runtime_web_credential_binding", "runtime_web_connect", "runtime_web_status", "runtime_web_browse", "runtime_web_read",
            "runtime_web_diagnostics", "runtime_web_operating_mode", "runtime_web_disconnect")
GATES = {"runtime_web_download": ("AC-identity", "AC-evidence"), "runtime_web_backup": ("AC-identity", "AC-preview", "AC-recovery", "AC-evidence"),
         "runtime_web_alarm_acknowledge": ("AC-identity", "AC-preview", "AC-recovery", "AC-evidence")}


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


def answered(pinned):
    """The JSON-RPC result of one retained pinned call, or None when it failed (HTTP status or JSON-RPC error)."""
    response = pinned.get("response")
    return response["result"] if pinned.get("httpStatus") == 200 and isinstance(response, dict) and "result" in response else None


def browse_issues(row):
    """One device_online_browse call against the pinned Modules.Browse of the same subtree: both fail, or both list the same
    hardware IDs with the same value in every node field."""
    label = "Modules.Browse of " + ("the top level" if row["hardwareId"] is None else "hwid %s" % row["hardwareId"])
    tool, pinned = row["tool"], answered(row["pinned"])
    if tool["isError"] or pinned is None:
        return [] if tool["isError"] and pinned is None else ["%s: the tool %s, the pinned call %s" % (
            label, "failed" if tool["isError"] else "answered", "failed" if pinned is None else "answered")]
    ours, theirs = tool["structuredContent"]["result"]["nodes"], pinned.get("nodes") or []
    if sorted(str(n.get("hwid")) for n in ours) != sorted(str(n.get("hwid")) for n in theirs):
        return ["%s: the tool lists hardware IDs %s, the pinned call %s" % (label, [n.get("hwid") for n in ours], [n.get("hwid") for n in theirs])]
    by_id = {n.get("hwid"): n for n in theirs}
    return ["%s: hwid %s field %s is %r, the pinned call reads %r" % (label, node.get("hwid"), key, node.get(key), by_id[node.get("hwid")].get(key))
            for node in ours for key in sorted(set(node) | set(by_id[node.get("hwid")])) if node.get(key) != by_id[node.get("hwid")].get(key)]


def im0(data):
    """The actual fields SiemensWebIdentity derives from one I&M0 data object: trimmed texts, unsigned numbers, type+major.minor.patch."""
    def text(value):
        return value.strip() if isinstance(value, str) and value.strip() else None

    def number(value):
        return str(value) if isinstance(value, int) and not isinstance(value, bool) and 0 <= value < 1 << 32 else None
    revision = data.get("software_revision") if isinstance(data.get("software_revision"), dict) else {}
    parts = [number(revision.get(key)) for key in ("major", "minor", "patch")]
    firmware = revision["type"] + ".".join(parts) if isinstance(revision.get("type"), str) and None not in parts else None
    return {"mlfb": text(data.get("order_number")), "serialNumber": text(data.get("serial_number")),
            "hardwareRevision": number(data.get("hardware_revision")), "firmware": text(firmware)}


def identify_issues(identified):
    """Every device_identify row against the pinned I&M0 read of its hardware ID, field by field, and the CPU row answered I&M0."""
    rows = {row.get("rowId"): row for row in identified["tool"].get("selectedRows", [])}
    issues = [] if len(identified["bindings"]) == len(identified["pinned"]) else ["Not every binding has a pinned I&M0 read"]
    for binding, pinned in zip(identified["bindings"], identified["pinned"]):
        row = rows.get(binding["rowId"]) or {}
        online, actual, label = row.get("online") or {}, row.get("actual") or {}, "%s (hwid %s)" % (row.get("name", binding["rowId"]), binding["hardwareId"])
        result = answered(pinned)
        if isinstance(result, dict) and isinstance(result.get("data"), dict):
            expected = im0(result["data"])
            status = "read" if None not in expected.values() else "partial"
        else:
            error = (pinned.get("response") or {}).get("error") if isinstance(pinned.get("response"), dict) else None
            expected = dict.fromkeys(ACTUAL)
            status = "permissionDenied" if pinned.get("httpStatus") in (401, 403) else IM_FAILURES.get((error or {}).get("code") if isinstance(error, dict) else None, "readError")
        if online.get("status") != status or online.get("hardwareId") != binding["hardwareId"]:
            issues.append("%s: the tool row is %s at hwid %s, the pinned read expects %s" % (label, online.get("status"), online.get("hardwareId"), status))
        issues += ["%s: %s is %r, I&M0 reads %r" % (label, key, (actual.get(key) or {}).get("value"), value)
                   for key, value in expected.items() if (actual.get(key) or {}).get("value") != value]
    if ((rows.get(identified["controllerRowId"]) or {}).get("online") or {}).get("status") not in ("read", "partial"):
        issues.append("The CPU row did not answer I&M0")
    return issues


def alarm_entries(listed):
    """The Alarms.Browse entries of one runtime_web_diagnostics kind alarms result, by id; empty unless it was read."""
    if not isinstance(listed, dict) or listed.get("status") != "read" or not isinstance(listed.get("value"), dict):
        return {}
    return {e["id"]: e for e in listed["value"].get("entries") or [] if isinstance(e, dict) and isinstance(e.get("id"), str)}


def acknowledgement(entry):
    """An alarm entry's acknowledgement state; None when it has none (an alarm class without acknowledgement)."""
    ack = entry.get("acknowledgement") if isinstance(entry, dict) else None
    return ack.get("state") if isinstance(ack, dict) else None


def sent_nothing(answer):
    """A tool error (refused before or without a request) or an answer that says nothing was dispatched."""
    return bool(answer.get("isError")) or answer.get("dispatched") is False


def outcome(advertised, answer, demonstrated):
    """One operation's recorded outcome: demonstrated, methodNotAdvertised (a B05 row), permissionDenied, or what the tool answered."""
    if not advertised:
        return "methodNotAdvertised"
    if answer.get("isError"):
        return "permissionDenied" if answer.get("code") == "RuntimePermissionDenied" else "error " + str(answer.get("code"))
    # A provider refusal keeps its mapped status, for example "refused unsupportedMethod" for a listed method the CPU rejects.
    return "demonstrated" if demonstrated else " ".join(str(x) for x in (answer.get("state"), answer.get("status")) if x is not None)


def stored(answer, check):
    """A downloaded file whose ticket was closed and whose size and hash agree in the tool result, artifact_read and the runner's own hash."""
    artifact = answer.get("artifact") or {}
    read = check.get("artifactRead") or {}
    return (answer.get("state") == "downloaded" and answer.get("ticketClosed") is True and isinstance(artifact.get("bytes"), int) and artifact["bytes"] > 0
            and isinstance(artifact.get("sha256"), str) and read.get("sha256") == check.get("localSha256") == artifact["sha256"]
            and read.get("bytes") == check.get("localBytes") == artifact["bytes"])


def web_services(report, result, need, issues):
    """Outcomes and demonstrated gates of runtime_web_alarm_acknowledge, runtime_web_download and runtime_web_backup."""
    methods = set(result(WS_STATUS).get("methods") or [])
    advertised = {kind: all(m in methods for m in needed) for kind, needed in WS_METHODS.items()}
    outcomes, gates = {}, {}

    def refused(labels):
        return all([need(result(label).get("isError") is True and result(label).get("code") and code in (None, result(label).get("code")),
                         "%s was not refused before sending%s" % (label, "" if code is None else " with " + code)) for label, code in labels.items()])

    def consistent(kind, label, answer):
        """An unsupported answer exactly when Api.Browse lacks a needed method, and never a request for a method it lacks."""
        if answer.get("state") == "unsupported":
            need(not advertised[kind], "%s answered unsupported although Api.Browse lists %s" % (label, " and ".join(WS_METHODS[kind])))
        if not advertised[kind] or answer.get("state") == "unsupported":
            need(sent_nothing(answer), "%s sent a request for a method Api.Browse does not list" % label)

    def verified(label, answer):
        return answer.get("state") == "downloaded" and need(stored(answer, result(STORED % label)), "%s: the stored file does not match artifact_read and its hash" % label)

    # runtime_web_alarm_acknowledge: the alarm the PLCSIM trigger raised (or, recorded as such, another listed unacknowledged alarm).
    alarm = (report.get("webServices") or {}).get("alarm") or {}
    target, lists = alarm.get("targetId"), [alarm_entries(result(label)) for label in ALARM_LISTS[:2]]
    triggered = {r["label"]: r["plcsimRead"].get("value") for r in report.get("comparisons", []) if r["label"] in TRIGGER}
    need(triggered == dict(zip(TRIGGER, (False, True))), "The PLCSIM API does not show the trigger raised: %s" % triggered)
    ack_gated = refused(ACK_REFUSALS)
    if target is None:
        outcomes["runtime_web_alarm_acknowledge"] = "noAlarmListed" if advertised["alarm"] else "methodNotAdvertised"
    else:
        answer = result(ACK)
        consistent("alarm", ACK, answer)
        listed = need(target in lists[1], "The acknowledged alarm %s was not listed with the trigger raised" % target)
        if alarm.get("source") == "programAlarm":
            need(target not in lists[0], "The alarm %s was listed before the trigger" % target)
        done = answer.get("state") == "acknowledged"
        if answer.get("state") == "notAcknowledgeable" or answer.get("state") == "alreadyAcknowledged":
            need(answer.get("dispatched") is False, "%s was sent for an alarm that was not unacknowledged" % ACK)
        if done:
            start = len(issues)
            need(listed and acknowledgement(lists[1][target]) == "not_acknowledged", "The alarm was not listed as not_acknowledged before %s" % ACK)
            need(answer.get("dispatched") is True and answer.get("readbackMatches") is True, "%s was not read back as acknowledged" % ACK)
            after = alarm_entries(result(ALARM_LISTS[2]))
            need(acknowledgement(after.get(target)) == "acknowledged", "An independent Alarms.Browse does not list %s as acknowledged" % target)
            again = result(ACK_AGAIN)
            need(again.get("dispatched") is False and again.get("state") in ("alreadyAcknowledged", "alarmNotListed"), "A second acknowledgement was sent")
            missing = result(ACK_NOT_LISTED)
            need(sent_nothing(missing) and missing.get("state") != "acknowledged", "An alarm that is not listed was acknowledged")
            need(sent_nothing(result(ACK_FALLEN)), "The fallen alarm was acknowledged again")
            done = len(issues) == start
        outcomes["runtime_web_alarm_acknowledge"] = outcome(advertised["alarm"], answer, done)

    # runtime_web_download: selection refusals before any request, then each kind's stored file.
    gated, any_done = refused(DOWNLOAD_REFUSALS), False
    hwid = result(WS_HWID).get("hwid")
    for kind, label in DOWNLOAD.items():
        if kind == "serviceData" and hwid is None:
            outcomes["runtime_web_download " + kind] = "hwidNotFound"
            continue
        answer = result(label)
        consistent(kind, label, answer)
        done = verified(label, answer)
        any_done = any_done or done
        outcomes["runtime_web_download " + kind] = outcome(advertised[kind], answer, done)
    if gated and any_done:
        gates["runtime_web_download"] = list(GATES["runtime_web_download"])

    # runtime_web_backup: refused in RUN without a request, the PLCSIM API sees no mode change; stored in STOP; RUN again afterwards.
    gated = refused(BACKUP_REFUSALS)
    in_run = result(BACKUP_RUN)
    consistent("backup", BACKUP_RUN, in_run)
    precondition = bool(in_run.get("isError")) or need(in_run.get("state") == "preconditionMismatch" and in_run.get("dispatched") is False,
                                                       "The backup in RUN was not refused before sending")
    need(result(BACKUP_RUN_STATE) == "Run", "PLCSIM does not report RUN after the refused backup")
    modes = all([need(result(request).get("state") == "changed" and result(observed) == state, "%s is not confirmed by PLCSIM" % request)
                 for request, observed, state in ((TO_STOP, PLCSIM_STOP, "Stop"), (TO_RUN, PLCSIM_RUN, "Run"))])
    answer = result(BACKUP)
    consistent("backup", BACKUP, answer)
    done = verified(BACKUP, answer) and need(result(BACKUP_STATE) == "Stop", "PLCSIM does not report STOP after the backup")
    outcomes["runtime_web_backup"] = outcome(advertised["backup"], in_run if in_run.get("isError") else answer, done and precondition and modes)
    if gated and outcomes["runtime_web_backup"] == "demonstrated":
        gates["runtime_web_backup"] = list(GATES["runtime_web_backup"])
    if ack_gated and outcomes.get("runtime_web_alarm_acknowledge") == "demonstrated":
        gates["runtime_web_alarm_acknowledge"] = list(GATES["runtime_web_alarm_acknowledge"])
    # Which alarm was acknowledged: the one the PLCSIM trigger raised, or another the Web API listed as not_acknowledged.
    return {"advertised": advertised, "outcomes": outcomes, "gates": gates, "alarmSource": alarm.get("source"), "alarmId": target}


def verify(root):
    root = Path(root).resolve()
    issues, summary = [], None

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
        for channel in {"ua-write-loss": ("ua",), "web-write-loss": ("web",), "ua-services": ("ua",), "device-inventory": (), "web-services": ("web",)}.get(mode, ("ua", "web")):
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
        elif mode == "ua-services":
            passed = {c["name"] for c in report.get("toolCalls", []) if not c["isError"]}
            for tool in SERVICE_TOOLS:
                need(tool in passed, "No successful call of " + tool)
            methods = [m.get("method") for m in result(METHOD_SET).get("methods", [])]
            need(result(METHOD_BROWSE) in methods, "The PLC namespace's method set does not contain the browsed server method")
            called = result(UA_CALL)
            need(called.get("state") == "called" and called.get("dispatched") is True and [(o.get("encoding"), o.get("value")) for o in called.get("outputs", [])]
                 == [("decimalInteger", "42"), ("boolean", False)], "The server method call did not answer 42 and false")
            need(result(CALL_UNACKNOWLEDGED).get("code") == "RuntimeAcknowledgementRequired", "A call without acknowledgement was not refused")
            for label, code in CALL_REFUSALS.items():
                refusal = result(label)
                need(refusal.get("state") == "refused" and refusal.get("dispatched") is False and refusal.get("refusal") == code, "%s was not refused with %s" % (label, code))
            failed = result(CALL_FAILED)
            need(failed.get("state") == "methodFailed" and failed.get("statusCode") == METHOD_BAD, "The method's own Bad result was not returned")
            counts = {r["label"]: r["plcsimRead"].get("value") for r in report.get("comparisons", []) if r["label"] in CALLS}
            need(len(counts) == 3 and isinstance(counts[CALLS[0]], int) and counts[CALLS[1]] == counts[CALLS[0]] + 1 and counts[CALLS[2]] == counts[CALLS[1]],
                 "The PLCSIM call counter does not show exactly one successful call: %s" % counts)
            probe = result(HISTORY)
            raw = probe.get("historyReadRaw") or {}
            status = raw.get("statusCode")
            need((probe.get("product") or {}).get("code") == "UaHistoryReadUnsupported" and raw.get("valueCount") is None
                 and (raw.get("serviceStatusCode") or (isinstance(status, str) and int(status, 16) & 0x80000000)), "The HistoryRead probe did not record a server refusal")
            snapshots = [result(label) for label in CONDITIONS]
            need(all((s.get("refresh") or {}).get("startObserved") and (s.get("refresh") or {}).get("endObserved") for s in snapshots),
                 "A condition snapshot lacks the ConditionRefresh boundaries")
            alarm = report.get("alarmCondition") or {}
            if need(isinstance(alarm.get("raised"), bool), "The run does not record whether PLCSIM raised the condition") and alarm["raised"]:
                def active(snapshot):
                    return {json.dumps(e.get("conditionId"), sort_keys=True) for e in snapshot.get("events", []) if e.get("kind") == "condition" and e.get("activeState") is True
                            and e.get("retain") is True}
                raised = active(snapshots[1]) - active(snapshots[0])
                need(raised == set(alarm.get("conditionIds", [])) and len(raised) == 1, "The raised condition is not one new active retained condition")
                need(not raised & active(snapshots[2]), "The condition is still active after the trigger fell")
        elif mode == "device-inventory":
            passed = {c["name"] for c in report.get("toolCalls", []) if not c["isError"]}
            for tool in INVENTORY_TOOLS:
                need(tool in passed, "No successful call of " + tool)
            need(result(TRUST) == "trusted" and result(IN_ROOT) is True, "The certificate was not trusted in Cert:\\CurrentUser\\Root before the tools ran")
            browsed = result(BROWSE) or []
            if need(browsed and not browsed[0]["tool"]["isError"], "The top-level device_online_browse did not answer"):
                issues.extend(issue for row in browsed for issue in browse_issues(row))
            issues.extend(identify_issues(result(IDENTIFY)))
        elif mode == "web-services":
            passed = {c["name"] for c in report.get("toolCalls", []) if not c["isError"]}
            for tool in WS_TOOLS:
                need(tool in passed, "No successful call of " + tool)
            summary = web_services(report, result, need, issues)
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError) as error:
        issues.append("%s: %s" % (type(error).__name__, error))
    verdict = {"completed": not issues, "kind": "runtime-plcsim-tcpip", "nativeInvoked": False, "issues": issues}
    if summary is not None:  # web-services: per operation what the run showed; only "gates" may be curated as matrix cells.
        verdict["webServices"] = {**summary, "gates": summary["gates"] if not issues else {}}
    return verdict


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
