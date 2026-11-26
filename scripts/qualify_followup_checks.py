"""Native runs of the still-open worker and Host checks of 16, 18, 20 and 25 November (docs/TODO.md), on owned scratch
projects and owned PLCSIM instances only (no user project is opened or changed, no machine-wide setting is touched).

Every check is its own stage with its own pass/fail record; a failed stage does not stop the next. completed is true only
when every stage that was not skipped passed; skipped stages are listed with their reason (--stages, a missing flag or
--seeded-report, or no CPU 1518 in the installed catalog). A stage whose precondition fails is failed, never skipped.

Host A (--queue-timeout-seconds, default the minimum 5 s) starts an owned Portal; Host B only attaches to Host A's owned
Portal or starts its own; Host C runs inside an owned job (scripts/owned_process_job.py) and only attaches to Host A's Portal.

- tia_capabilities: workerVersion equals Directory.Build.props, physical_plc is listed (conditional).
- attach_behind_busy_client: Host A creates its scratch (a long call); Host B lists the Portal's attachedSessions during it and
  times a plain tia_attach (no waitForIdleSeconds) sent while the call runs. A measurement: outcome and duration are recorded.
- project_locked_and_portals: Host B starts its own Portal and opens Host A's open scratch: ProjectLocked naming Host A's
  Portal; tia_session_status portals on both Hosts (thisWorker, ownedByThisWorker, projectPath).
- exited_client: this script, started again in --hold-attach mode inside an owned job, runs Host C, which attaches to Host A's
  Portal and holds. The job's processes are checked (only Python, the Host, the worker and conhost; never a Siemens process)
  and the job is terminated: the attached worker dies without detaching. Host B then lists the Portal's attachedSessions
  (is the worker still listed, with processRunning false?) and times a plain tia_attach behind it.
- hmi_inspect_page_and_early_kind_refusal / hmi_change_early_kind_refusal: on the plain scratch (no HMI) with a synthetic
  identity: page is the first field of hmi_inspect; a target.kind of another section, and an HMI change target.kind the
  operation does not take, are refused (InvalidRequest) before any identity check or capture.
- repeated_name_sim_write / repeated_name_change_prepare: a sim_write row and a change_prepare payload with one property name
  twice (sent as raw JSON text) are refused with InvalidRequest; the next calls on the same Host still work.
- queue_timeout_during_change_apply: a plc_compile change_apply (checkpoint, compile, save) and, one second later,
  project_inventory on the same Host: the second call ends with QueueTimeout after the queue timeout.
- simulation_download_ticket_state: a ticket that is not one (unusable); the ticket of a released owned instance (fails before
  the claim: reusable); a download without credentialMetadataPath on the protected scratch CPU (fails in TIA's password
  callback after the claim: spent), then the same ticket again (FreshSimulationStorageRequired, spent).
- simulation_registrations_present: with one fresh owned instance registered, simulation_discovery_baseline is refused with
  SimulationRegistrationsPresent (details registered, remediation), and tia_detach reports it in plcsimRegistrations.
- stale_change_directory_sweep: two plc_compile preparations, a detach (which releases them from the worker), a new scratch
  and a third preparation (held). The journal.json write time of one released and of the held preparation is set 25 h back
  (StaleChangeDirectories reads only that); the next change_prepare removes only the released aged one and logs it to
  changes/pruned-changes.jsonl.
- sim_write_signals_both_spellings (--running-instance): download to a fresh owned instance, provisioning with the user's
  first-use TLS authorization when --trust-owned-empty-cpu-certificate is given, RUN, then one sim_write_signals batch with
  the tag list's and TIA's quoted spelling of one tag: InvalidRequest; one spelling alone writes.
- loadable_provider_cpu_1518: a CPU 1518 from the installed catalog is added to the owned scratch (hardware_add); its
  target_capabilities lists plc_loadable_generate with the API available, the CPU 1511 does not.
- hmi_seeded_page_and_kind_refusals / hmi_preservation_unavailable_remediation (--seeded-report, an HmiSeeder positive qualify
  report): the profile's baseline is retrieved; page and both early refusals on real identities; hmi_engineering with a
  valid payload is refused with HmiPreservationUnavailable and its remediation.

Host A's host_build_identity is recorded first (hostBuild: the build that ran). host_build_identity from an installed
package and the UMC global user are not covered here. The report has the shape
scripts/curate_evidence.py reads (completed, steps, toolCalls); artifacts stay under the repository's artifacts/ directory.
"""
import argparse
import hashlib
import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
import traceback
import uuid
from datetime import datetime, timezone
from pathlib import Path

from smoke_mcp import Client, HostExited, ROOT

DURATION_META = "tiaMcp/durationMs"
STALE_HOURS = 25  # StaleChangeDirectories.MinimumAge is 24 h.
HOLD_SECONDS = 900  # A held Host C closes itself after this, so it never outlives a crashed runner for long.
TEST_DB = "TiaMcp_TestIO"
CPU_1518 = "CPU 1518-4 PN/DP"

STAGES = (
    "tia_capabilities",
    "attach_behind_busy_client",
    "project_locked_and_portals",
    "exited_client",
    "hmi_inspect_page_and_early_kind_refusal",
    "hmi_change_early_kind_refusal",
    "repeated_name_sim_write",
    "repeated_name_change_prepare",
    "queue_timeout_during_change_apply",
    "simulation_download_ticket_state",
    "simulation_registrations_present",
    "stale_change_directory_sweep",
    "sim_write_signals_both_spellings",
    "loadable_provider_cpu_1518",
    "hmi_seeded_page_and_kind_refusals",
    "hmi_preservation_unavailable_remediation",
)
SEEDED_STAGES = ("hmi_seeded_page_and_kind_refusals", "hmi_preservation_unavailable_remediation")
FAMILY = {"classic-comfort": "classic", "classic-rt-advanced": "classic", "classic-basic": "classic",
          "unified-comfort": "unified", "unified-pc": "unified"}


class StageSkipped(Exception):
    """Raised inside a stage when the environment cannot carry it (recorded as skipped with the message as reason)."""


# --- Pure helpers (scripts/tests/test_qualify_followup_checks.py) ---------------------------------------------------------

def require(value, message):
    if not value:
        raise AssertionError(message)
    return value


def stage_plan(selected, args_view):
    """Stage name -> skip reason (None runs it). args_view: running_instance, seeded_report (bools)."""
    plan = {}
    for name in STAGES:
        if selected is not None and name not in selected:
            plan[name] = "not selected (--stages)"
        elif name == "sim_write_signals_both_spellings" and not args_view.get("running_instance"):
            plan[name] = "needs --running-instance (download to an owned instance and RUN; provisioning with --trust-owned-empty-cpu-certificate)"
        elif name in SEEDED_STAGES and not args_view.get("seeded_report"):
            plan[name] = "needs --seeded-report (an HmiSeeder positive qualify report with a retrieval-verified baseline)"
        else:
            plan[name] = None
    return plan


def summarize(stages):
    """completed: every stage that was not skipped passed, and at least one ran."""
    ran = [s for s in stages if s["status"] != "skipped"]
    return {"completed": bool(ran) and all(s["status"] == "passed" for s in ran),
            "passed": [s["name"] for s in stages if s["status"] == "passed"],
            "failed": [s["name"] for s in stages if s["status"] == "failed"],
            "skipped": [{"name": s["name"], "reason": s.get("reason")} for s in stages if s["status"] == "skipped"]}


def error_of(value):
    return (value or {}).get("error") or {}


def details_of(value):
    return error_of(value).get("details") or {}


def repeated_member_object(pairs):
    """JSON object text with the members in order, a name allowed twice (json.dumps of a dict cannot write that)."""
    return "{" + ",".join(json.dumps(str(name)) + ":" + json.dumps(value) for name, value in pairs) + "}"


def tools_call_frame(sequence, name, arguments_text):
    """One JSON-RPC tools/call line whose arguments are given as JSON text, sent as is."""
    return '{"jsonrpc":"2.0","id":%d,"method":"tools/call","params":{"name":%s,"arguments":%s}}' % (sequence, json.dumps(name), arguments_text)


def spellings(symbol):
    """(unquoted, quoted) spelling of a DB member symbol: TiaMcp_TestIO.Enable and "TiaMcp_TestIO".Enable."""
    head, dot, member = symbol.replace('"', "").partition(".")
    require(head and dot and member, "Not a DB member symbol: " + symbol)
    return head + "." + member, '"' + head + '".' + member


def product_version(props_text):
    found = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", props_text)
    return found.group(1) if found else None


def version_key(entry):
    return tuple(int(x) for x in re.findall(r"\d+", entry.get("version") or "")), entry.get("typeIdentifier") or ""


def catalog_cpu(entries, type_name):
    """The newest installed entry with exactly this type name, else None."""
    matching = [e for e in entries or [] if (e.get("typeName") or "") == type_name and e.get("typeIdentifier")]
    return max(matching, key=version_key) if matching else None


def portal_sessions(processes, portal_id):
    """attachedSessions of one Portal in a tia_discover result (None when the Portal is not listed)."""
    for process in processes or []:
        if process.get("processId") == portal_id:
            return process.get("attachedSessions") or []
    return None


def session_of(sessions, process_id):
    return next((s for s in sessions or [] if s.get("processId") == process_id), None)


def portal_row(portals, portal_id):
    return next((p for p in portals or [] if p.get("processId") == portal_id), None)


def same_path(left, right):
    if not left or not right:
        return False
    return os.path.normcase(os.path.normpath(str(left))) == os.path.normcase(os.path.normpath(str(right)))


def change_journal(changes_root, change_id):
    """<changes>/change-<32 hex>/journal.json of one change this run prepared; anything else is refused."""
    require(re.fullmatch(r"[0-9a-f]{32}", change_id or "") is not None, "Not a change id: %r" % change_id)
    path = Path(changes_root) / ("change-" + change_id) / "journal.json"
    require(path.resolve().parent.parent == Path(changes_root).resolve(), "Journal outside the changes folder: %s" % path)
    return path


def pruned_ids(log_text):
    ids = []
    for line in (log_text or "").splitlines():
        if line.strip():
            ids.append(json.loads(line).get("changeId"))
    return ids


def sweep_verdict(removed_expected, kept_expected, existing_after, logged):
    """What the stale sweep did against what it should: the aged released change removed and logged, the rest kept."""
    problems = []
    for change_id in removed_expected:
        if change_id in existing_after:
            problems.append("aged released preparation %s was not removed" % change_id)
        if change_id not in logged:
            problems.append("removal of %s was not logged" % change_id)
    for change_id in kept_expected:
        if change_id not in existing_after:
            problems.append("preparation %s was removed but should be kept" % change_id)
    unexpected = [i for i in logged if i not in removed_expected]
    if unexpected:
        problems.append("unexpected log entries: %s" % unexpected)
    return problems


def foreign_images(images, allowed):
    """Job members whose image is not one of the allowed executables (an unreadable image counts as foreign)."""
    permitted = {os.path.normcase(os.path.normpath(str(a))) for a in allowed if a}
    return [image for image in images if image is None or os.path.normcase(os.path.normpath(str(image))) not in permitted]


def seeded_archive(report, profile):
    """The retrieval-verified archive row of one profile in an HmiSeeder qualify report, else None."""
    if report.get("completed") is not True:
        return None
    rows = [r for r in report.get("profiles", []) if r.get("profile") == profile and r.get("archiveRetrievalVerified") is True]
    if len(rows) != 1 or not (rows[0].get("archive") or {}).get("path"):
        return None
    return rows[0]["archive"]


def key_order(value, first, then):
    """Whether key first comes before key then in a result object (dicts keep the wire order)."""
    keys = list(value or {})
    return first in keys and then in keys and keys.index(first) < keys.index(then)


def duration_ms(result):
    return ((result or {}).get("_meta") or {}).get(DURATION_META)


def now_utc():
    return datetime.now(timezone.utc).isoformat()


# --- Raw frames: repeated names and overlapping calls ----------------------------------------------------------------------

class RawCalls:
    """Sends tools/call frames with their own ids and collects every reply by id; Client.request drops replies whose id is not
    the one it waits for, so overlapping calls and frames json.dumps cannot write go through here."""

    def __init__(self, client):
        self.client = client
        self.replies = {}

    def send(self, name, arguments_text):
        self.client.sequence += 1
        sequence = self.client.sequence
        self.client.process.stdin.write(tools_call_frame(sequence, name, arguments_text) + "\n")
        self.client.process.stdin.flush()
        return sequence

    def collect(self, sequences, names, timeout):
        deadline = time.monotonic() + timeout
        wanted = set(sequences)
        while not wanted <= self.replies.keys():
            try:
                line = self.client.lines.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty:
                raise TimeoutError("No reply to %s within %d s" % (sorted(wanted - self.replies.keys()), timeout)) from None
            if line is None:
                raise HostExited("MCP host exited while raw calls were pending: " + "\n".join(self.client.errors[-10:]))
            message = json.loads(line)
            if "id" in message:
                self.replies[message["id"]] = (message, time.monotonic())
        out = {}
        for sequence in sequences:
            message, received = self.replies.pop(sequence)
            if "result" in message:
                result = message["result"]
                self.client.tool_calls.append({"name": names[sequence], "isError": bool(result.get("isError"))})
                self.client.check_output(names[sequence], result, {})
            out[sequence] = {"message": message, "receivedMonotonic": received}
        return out


# --- Owned job of Host C ---------------------------------------------------------------------------------------------------

def job_members(job):
    """Process ids in an OwnedProcessJob (JobObjectBasicProcessIdList), with each image path (None if unreadable)."""
    import ctypes as c
    from ctypes import wintypes as w

    class IdList(c.Structure):
        _fields_ = [("assigned", w.DWORD), ("listed", w.DWORD), ("ids", c.c_size_t * 256)]

    k = job.k
    k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
    k.OpenProcess.restype = w.HANDLE
    k.QueryFullProcessImageNameW.argtypes = [w.HANDLE, w.DWORD, w.LPWSTR, c.POINTER(w.DWORD)]
    k.QueryFullProcessImageNameW.restype = w.BOOL
    listing = IdList()
    job.check(k.QueryInformationJobObject(job.job, 3, c.byref(listing), c.sizeof(listing), None), "QueryInformationJobObject process ids")
    members = []
    for pid in list(listing.ids)[:listing.listed]:
        image = None
        handle = k.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION; a member of our own job.
        if handle:
            try:
                length = w.DWORD(32768)
                buffer = c.create_unicode_buffer(length.value)
                if k.QueryFullProcessImageNameW(handle, 0, buffer, c.byref(length)):
                    image = buffer.value
            finally:
                k.CloseHandle(handle)
        members.append({"pid": int(pid), "image": image})
    return {"assigned": listing.assigned, "listed": listing.listed, "members": members}


def terminate_job(job):
    """TerminateJobObject on the owned job only: every process in it ends (owned_process_job never terminates by itself)."""
    from ctypes import wintypes as w
    job.k.TerminateJobObject.argtypes = [w.HANDLE, w.UINT]
    job.k.TerminateJobObject.restype = w.BOOL
    job.check(job.k.TerminateJobObject(job.job, 1), "TerminateJobObject owned job")


def hold_attach(args):
    """--hold-attach: run Host C, attach to the given Portal (never tia_start), report, and hold until killed, until the runner
    creates <artifacts>/release, or for HOLD_SECONDS; the last two detach and close Host C normally."""
    host, worker, sim_worker = binaries(args)
    folder = args.artifacts.resolve()
    folder.mkdir(parents=True, exist_ok=True)
    client = Client(host, 300, ["--worker", str(worker), "--sim-worker", str(sim_worker), "--artifacts", str(folder)])

    def say(event, **data):
        print(json.dumps({"event": event, "atUtc": now_utc(), **data}, default=str), flush=True)

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "followup-hold-attach", "version": "1"}})
        client.notify("notifications/initialized")
        say("hostStarted", hostPid=client.process.pid)
        result = client.request("tools/call", {"name": "tia_attach", "arguments": {"processId": args.hold_attach, "waitForIdleSeconds": 60}})
        value = result.get("structuredContent") or {}
        if result.get("isError"):
            say("attachFailed", error=error_of(value))
            return 2
        say("attached", hostPid=client.process.pid, attachedSessions=value.get("attachedSessions"))
        deadline = time.monotonic() + HOLD_SECONDS
        release = folder / "release"
        while time.monotonic() < deadline and not release.exists():
            time.sleep(1)
        client.request("tools/call", {"name": "tia_detach", "arguments": {}})
        say("released", onRequest=release.exists())
        return 0
    finally:
        client.close()


# --- Runner ---------------------------------------------------------------------------------------------------------------

def binaries(args):
    """Host, engineering worker and simulator worker: a packaged bundle (--bundle) or explicit paths (Debug builds by default,
    which Register-DevOpennessWorker.ps1 keeps in Siemens' Openness AllowList)."""
    if args.bundle:
        bundle = args.bundle.resolve()
        return bundle / "host/TiaMcp.Host.exe", bundle / "workers/openness/TiaMcp.Openness.V21.exe", bundle / "workers/simulator/TiaMcp.Simulator.V8.exe"
    return args.host.resolve(), args.worker.resolve(), args.sim_worker.resolve()


class Run:
    def __init__(self, args):
        self.args = args
        self.root = args.artifacts.resolve()
        self.host, self.worker, self.sim_worker = binaries(args)
        self.tool_calls = []
        self.report = {"completed": False, "startedUtc": now_utc(),
                       "binaries": {"host": str(self.host), "worker": str(self.worker), "simWorker": str(self.sim_worker)},
                       "options": {"queueTimeoutSeconds": args.queue_timeout_seconds, "runningInstance": args.running_instance,
                                   "trustOwnedEmptyCpuCertificate": args.trust_owned_empty_cpu_certificate,
                                   "seededReport": str(args.seeded_report.resolve()) if args.seeded_report else None,
                                   "hmiProfile": args.hmi_profile, "stages": args.stages},
                       "stages": [], "skipped": [], "steps": [], "toolCalls": self.tool_calls}
        self.clients = {}
        self.instances = []  # (client label, instance id) still registered.
        self.scratch = None  # {"path", "credential", "plcPath", "portal"} of Host A's open scratch.
        self.seeded = None
        self.current = None

    # Recording ---------------------------------------------------------------------------------------------------------
    def save(self):
        (self.root / "report.json").write_text(json.dumps(self.report, indent=2, default=str), encoding="utf-8")

    def record(self, name, data=None):
        label = (self.current + ": " if self.current else "") + name
        self.report["steps"].append({"name": label, "result": data})
        self.save()
        print(label, flush=True)
        return data

    def client(self, label, extra=None):
        existing = self.clients.get(label)
        if existing is not None and (getattr(existing, "exited", False) or existing.process.poll() is not None):
            # A Host that ended (its worker session lost) is replaced, so one stage's crash does not fail every later stage.
            self.record("host %s exited; replaced" % label.upper(), {"exitCode": existing.process.poll(), "stderr": existing.errors[-10:]})
            try:
                self.report.setdefault("hostClose", {})["%s-exited-%d" % (label, len(self.report["steps"]))] = existing.close()
            except Exception as error:  # noqa: BLE001 - the replacement starts regardless
                self.record("close of the exited host %s" % label.upper(), str(error))
            del self.clients[label]
            if label == "a":
                self.scratch = None
                self.seeded = None
        if label not in self.clients:
            folder = self.root / label
            folder.mkdir(exist_ok=True)
            c = Client(self.host, 900, ["--worker", str(self.worker), "--sim-worker", str(self.sim_worker), "--artifacts", str(folder), *(extra or [])])
            c.tool_calls = self.tool_calls  # One list for the report; every Host's calls count.
            c.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                     "clientInfo": {"name": "followup-checks-" + label, "version": "1"}})
            c.notify("notifications/initialized")
            self.clients[label] = c
        return self.clients[label]

    @property
    def a(self):
        return self.client("a", ["--queue-timeout-seconds", str(self.args.queue_timeout_seconds)])

    @property
    def b(self):
        return self.client("b")

    def call(self, client, name, arguments=None):
        """(structuredContent, isError, durationMs)."""
        result = client.request("tools/call", {"name": name, "arguments": arguments or {}})
        return (result.get("structuredContent") or {}), bool(result.get("isError")), duration_ms(result)

    def ok(self, client, name, arguments=None):
        value, failed, _ = self.call(client, name, arguments)
        require(not failed, "%s failed: %s" % (name, json.dumps(error_of(value), default=str)[:2000]))
        return value

    def refused(self, step, client, name, arguments, codes):
        value, failed, ms = self.call(client, name, arguments)
        error = error_of(value)
        self.record(step, {"failed": failed, "code": error.get("code"), "message": error.get("message"), "durationMs": ms})
        require(failed and error.get("code") in codes, "%s: expected %s, got %s" % (step, codes, json.dumps(value, default=str)[:2000]))
        return value

    def run_stage(self, name, function, skip=None):
        entry = {"name": name, "startedUtc": now_utc()}
        if skip:
            entry.update(status="skipped", reason=skip)
            self.report["stages"].append(entry)
            self.report["skipped"].append({"name": name, "reason": skip})
            self.save()
            print("SKIPPED " + name + ": " + skip, flush=True)
            return
        self.current = name
        clock = time.monotonic()
        try:
            entry["observations"] = function()
            entry["status"] = "passed"
        except StageSkipped as skipped:
            entry.update(status="skipped", reason=str(skipped))
            self.report["skipped"].append({"name": name, "reason": str(skipped)})
        except Exception as error:  # noqa: BLE001 - one stage's failure is recorded and the next stage runs
            entry.update(status="failed", error="%s: %s" % (type(error).__name__, str(error)[:4000]),
                         traceback=traceback.format_exc()[-4000:])
        finally:
            self.current = None
            entry["durationSeconds"] = round(time.monotonic() - clock, 1)
            self.report["stages"].append(entry)
            self.save()
            print(entry["status"].upper() + " " + name, flush=True)

    # Owned scratch on Host A --------------------------------------------------------------------------------------------
    def adopt_scratch(self, created):
        status = self.ok(self.a, "tia_session_status")
        projects = status.get("projects") or []
        require(len(projects) == 1, "Expected one open scratch project: %s" % projects)
        cpus = (created.get("scratchSecurity") or {}).get("cpus") or []
        plcs = self.ok(self.a, "project_inventory", {"includeBlocks": False}).get("plcs") or []
        require(len(plcs) == 1, "Expected one PLC in the scratch: %s" % plcs)
        self.scratch = {"path": projects[0]["path"], "credential": cpus[0].get("credentialMetadataPath") if cpus else None,
                        "plcPath": plcs[0]["path"], "portal": status.get("processId"), "plcCount": 1}
        self.record("scratch", self.scratch)
        return self.scratch

    def new_scratch(self):
        """Host A: detach whatever it holds, start an owned Portal and create a fresh scratch."""
        self.record("tia_detach before a new scratch", self.call(self.a, "tia_detach", {"waitForExitSeconds": 120})[:2])
        self.scratch = None
        started = self.ok(self.a, "tia_start")
        require(started.get("ownsPortalProcess") is True, "tia_start did not report an owned Portal: %s" % started)
        created = self.ok(self.a, "tia_create_scratch")
        return self.adopt_scratch(created)

    def ensure_scratch(self):
        if self.scratch and self.scratch.get("plcCount") == 1:
            status, failed, _ = self.call(self.a, "tia_session_status")
            if not failed and any(same_path(p.get("path"), self.scratch["path"]) for p in status.get("projects") or []):
                return self.scratch
        return self.new_scratch()

    def apply_change(self, client, kind, payload, save=True, plc_path=None):
        arguments = {"kind": kind, "payload": payload}
        if plc_path:
            arguments["plcPath"] = plc_path
        prepared = self.ok(client, "change_prepare", arguments)
        return self.ok(client, "change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                                                "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": save})

    def enable_simulation(self):
        value, failed, _ = self.call(self.a, "change_prepare", {"kind": "project_simulation_enable", "payload": {"enabled": True}})
        if failed:
            return self.record("project_simulation_enable prepare refused", error_of(value))
        applied = self.ok(self.a, "change_apply", {"changeId": value["changeId"], "previewHash": value["previewHash"],
                                                   "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
        return self.record("project_simulation_enable", applied.get("state"))

    def sim_create(self):
        created = self.ok(self.a, "sim_create", {"cpuType": "CPU1511"})
        self.instances.append(created["instance"]["id"])
        return created

    def sim_release(self, instance_id):
        """Release with deleteOwned; never raises, so a cleanup cannot hide the stage's own result."""
        try:
            value, failed, _ = self.call(self.a, "sim_release", {"instanceId": instance_id, "deleteOwned": True})
        except Exception as error:  # noqa: BLE001 - recorded; the instance stays listed for the final cleanup
            return self.record("sim_release %s failed" % instance_id, str(error))
        if instance_id in self.instances and not failed:
            self.instances.remove(instance_id)
        return self.record("sim_release %s" % instance_id, {"failed": failed, "result": value})

    # Stages ------------------------------------------------------------------------------------------------------------
    def tia_capabilities(self):
        value = self.ok(self.a, "tia_capabilities")
        expected = product_version((ROOT / "Directory.Build.props").read_text(encoding="utf-8"))
        physical = [c for c in value.get("capabilities") or [] if c.get("name") == "physical_plc"]
        observed = self.record("tia_capabilities", {"workerVersion": value.get("workerVersion"), "productVersion": expected,
                                                    "physical_plc": physical})
        require(value.get("workerVersion") == expected, "workerVersion %s differs from Directory.Build.props %s" % (value.get("workerVersion"), expected))
        require(len(physical) == 1 and physical[0].get("status") == "conditional", "physical_plc is not listed as conditional")
        return observed

    def attach_behind_busy_client(self):
        self.record("tia_detach before tia_start", self.call(self.a, "tia_detach", {"waitForExitSeconds": 120})[:2])
        self.scratch = None
        started = self.ok(self.a, "tia_start")
        portal = self.ok(self.a, "tia_session_status")["processId"]
        b = self.b
        self.record("host B tia_discover idle", portal_sessions(self.ok(b, "tia_discover")["processes"], portal))
        outcome = {}

        def create():
            outcome["sent"] = time.monotonic()
            try:
                outcome["created"] = self.a.tool("tia_create_scratch")
            except Exception as error:  # noqa: BLE001 - reported by the main thread
                outcome["error"] = str(error)
            outcome["ended"] = time.monotonic()

        thread = threading.Thread(target=create, daemon=True)
        thread.start()
        time.sleep(self.args.busy_delay_seconds)
        during = self.record("host B tia_discover during scratch creation", portal_sessions(self.ok(b, "tia_discover")["processes"], portal))
        sent = time.monotonic()
        value, failed, ms = self.call(b, "tia_attach", {"processId": portal})
        returned = time.monotonic()
        thread.join()
        require("created" in outcome, "Host A could not create its scratch: %s" % outcome.get("error"))
        measured = self.record("host B plain tia_attach during the call", {
            "failed": failed, "error": error_of(value) or None, "durationMs": ms, "waitedSeconds": round(returned - sent, 1),
            "attachSentWhileBusy": sent < outcome["ended"], "busyCallEndedBeforeAttachReturned": outcome["ended"] <= returned,
            "secondsAfterBusyCallEnded": round(returned - outcome["ended"], 1), "busyClientsSeen": [s for s in during or [] if s.get("isActive")],
            "attachedSessions": value.get("attachedSessions")})
        if not failed:
            self.record("host B tia_detach", self.ok(b, "tia_detach"))
        self.adopt_scratch(outcome["created"])
        require(measured["attachSentWhileBusy"], "The scratch creation ended before Host B's attach was sent; raise --busy-delay-seconds' margin")
        return {"portal": portal, "ownsPortal": started.get("ownsPortalProcess"), **measured}

    def project_locked_and_portals(self):
        scratch = self.ensure_scratch()
        b = self.b
        self.record("host B tia_detach before tia_start", self.call(b, "tia_detach", {"waitForExitSeconds": 120})[:2])
        own = self.ok(b, "tia_start")
        require(own.get("ownsPortalProcess") is True, "Host B did not start its own Portal")
        try:
            locked = self.refused("host B project_open of Host A's open scratch", b, "project_open", {"path": scratch["path"]}, ("ProjectLocked",))
            owners = details_of(locked).get("lockOwners") or []
            require(any(o.get("processId") == scratch["portal"] for o in owners), "ProjectLocked does not name Host A's Portal %s: %s" % (scratch["portal"], owners))
            b_status = self.ok(b, "tia_session_status")
            b_portal = b_status.get("processId")
            a_status = self.ok(self.a, "tia_session_status")
            rows = {"hostB": {"a": portal_row(b_status.get("portals"), scratch["portal"]), "b": portal_row(b_status.get("portals"), b_portal)},
                    "hostA": {"a": portal_row(a_status.get("portals"), scratch["portal"]), "b": portal_row(a_status.get("portals"), b_portal)}}
            self.record("portals", {**rows, "writerRule": b_status.get("writerRule")})
            require(rows["hostB"]["a"] and rows["hostB"]["a"]["thisWorker"] is False and same_path(rows["hostB"]["a"]["projectPath"], scratch["path"]),
                    "Host B does not list Host A's Portal with the scratch")
            require(rows["hostB"]["b"] and rows["hostB"]["b"]["thisWorker"] is True and rows["hostB"]["b"]["ownedByThisWorker"] is True,
                    "Host B does not list its own Portal as thisWorker/ownedByThisWorker")
            require(rows["hostA"]["a"] and rows["hostA"]["a"]["thisWorker"] is True and rows["hostA"]["a"]["ownedByThisWorker"] is True,
                    "Host A does not list its own Portal as thisWorker/ownedByThisWorker")
            return {"lockOwners": owners, "remediation": details_of(locked).get("remediation"), "portals": rows}
        finally:
            self.record("host B tia_detach of its own Portal", self.call(b, "tia_detach", {"waitForExitSeconds": 120})[:2])

    def exited_client(self):
        if os.name != "nt":
            raise AssertionError("Owned jobs need Windows")
        from owned_process_job import OwnedProcessJob
        scratch = self.ensure_scratch()
        portal = scratch["portal"]
        b = self.b
        folder = self.root / "c"
        out_path, err_path = self.root / "c-helper.out.jsonl", self.root / "c-helper.err.txt"
        command = [sys.executable, "-B", str(Path(__file__).resolve()), "--hold-attach", str(portal), "--artifacts", str(folder),
                   "--host", str(self.host), "--worker", str(self.worker), "--sim-worker", str(self.sim_worker)]
        job = OwnedProcessJob()
        observations = {"portal": portal}
        with open(out_path, "wb") as out, open(err_path, "wb") as err:
            job.start(command, stdout=out, stderr=err)
        observations["helperPid"] = job.pid
        try:
            attached = None
            deadline = time.monotonic() + 420
            while attached is None and time.monotonic() < deadline and job.poll() is None:
                time.sleep(2)
                events = [json.loads(line) for line in out_path.read_text(encoding="utf-8", errors="replace").splitlines() if line.strip().startswith("{")]
                attached = next((e for e in events if e.get("event") in ("attached", "attachFailed")), None)
            observations["helperEvent"] = self.record("Host C attach", attached)
            require(attached and attached["event"] == "attached", "Host C did not attach to Host A's Portal")
            members = job_members(job)
            allowed = [sys.executable, getattr(sys, "_base_executable", None), self.host, self.worker, self.sim_worker,
                       Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "conhost.exe"]
            foreign = foreign_images([m["image"] for m in members["members"]], allowed)
            observations["jobMembers"] = self.record("owned job members", members)
            require(not foreign, "The owned job holds other images %s; nothing is terminated" % foreign)
            workers = [m["pid"] for m in members["members"] if same_path(m["image"], self.worker)]
            require(len(workers) == 1, "Expected Host C's one worker in the job: %s" % members)
            worker_pid = observations["workerPid"] = workers[0]
            before = session_of(portal_sessions(self.ok(b, "tia_discover")["processes"], portal), worker_pid)
            observations["sessionBeforeKill"] = self.record("Host C's worker in attachedSessions before the kill", before)
            require(before is not None and before.get("processRunning") is True, "Host C's worker is not listed as a running client")
            terminate_job(job)
            killed = time.monotonic()
            job.wait(timeout=60)
            for _ in range(100):
                if job.snapshot()["activeProcesses"] == 0:
                    break
                time.sleep(0.1)
            observations["terminalProof"] = self.record("owned job terminal proof", job.terminal_proof())
            polls = []
            for _ in range(self.args.exited_polls):
                time.sleep(5)
                row = session_of(portal_sessions(self.ok(b, "tia_discover")["processes"], portal), worker_pid)
                polls.append({"secondsAfterKill": round(time.monotonic() - killed, 1), "listed": row is not None,
                              "processRunning": None if row is None else row.get("processRunning"), "isActive": None if row is None else row.get("isActive")})
            observations["pollsAfterKill"] = self.record("attachedSessions after the kill", polls)
            observations["outcome"] = ("listedWithProcessRunningFalse" if any(p["listed"] and p["processRunning"] is False for p in polls)
                                       else "listedRunning" if any(p["listed"] for p in polls) else "notListed")
            sent = time.monotonic()
            value, failed, ms = self.call(b, "tia_attach", {"processId": portal})
            observations["plainAttachAfterKill"] = self.record("host B plain tia_attach after the kill", {
                "failed": failed, "error": error_of(value) or None, "durationMs": ms, "waitedSeconds": round(time.monotonic() - sent, 1),
                "exitedClient": session_of(value.get("attachedSessions"), worker_pid)})
            if not failed:
                self.record("host B tia_detach", self.ok(b, "tia_detach"))
            observations["hostAAfterKill"] = self.record("Host A tia_session_status after the kill", self.call(self.a, "tia_session_status")[:2])
            return observations
        finally:
            if job.poll() is None:
                # Not terminated (a check failed first): ask Host C to detach and close normally.
                folder.mkdir(parents=True, exist_ok=True)
                (folder / "release").write_text("release", encoding="utf-8")
                try:
                    job.wait(timeout=300)
                except TimeoutError:
                    self.record("Host C did not end after its release request; it ends at its own hold limit", {"helperPid": job.pid})
            if job.poll() is not None:
                try:
                    job.close()
                except Exception as error:  # noqa: BLE001 - recorded, the job handle stays open until exit
                    self.record("owned job close", str(error))

    def hmi_inspect_page_and_early_kind_refusal(self):
        self.ensure_scratch()
        value, failed, ms = self.call(self.a, "hmi_inspect", {"request": {"section": "devices", "limit": 1}})
        require(not failed, "hmi_inspect devices failed: %s" % error_of(value))
        page = self.record("hmi_inspect devices page", {"keys": list(value)[:8], "page": value.get("page"), "durationMs": ms})
        require(isinstance(value.get("page"), dict) and key_order(value, "page", "items"), "page is not ahead of items")
        require({"limit", "maxBytes", "payloadBytes", "returned", "stoppedBy"} <= set(value["page"]), "page lacks bounds: %s" % value["page"])
        hmi = "devices/NoHmi/hmi"
        synthetic = {"kind": "hmiScreen", "family": "unified", "hmiPath": hmi, "objectPath": hmi + "/screens/NoScreen",
                     "projectCopyId": "synthetic", "sessionGeneration": "synthetic"}
        refused = self.refused("hmi_inspect tags with a screen identity", self.a, "hmi_inspect", {"request": {"section": "tags", "target": synthetic}}, ("InvalidRequest",))
        message = error_of(refused).get("message") or ""
        require("does not belong to section tags" in message, "Not the early target.kind refusal: " + message)
        return {"page": page, "refusal": message, "identity": "synthetic (no HMI in the scratch)"}

    def hmi_change_early_kind_refusal(self):
        self.ensure_scratch()
        hmi = "devices/NoHmi/hmi"
        payload = {"schemaVersion": 1, "operation": "screen_create", "name": "NoScreen", "properties": {"Width": 800, "Height": 480},
                   "expectedContentHash": "0" * 64,
                   "target": {"kind": "hmiTagTable", "family": "unified", "hmiPath": hmi, "objectPath": hmi + "/tags/NoTable",
                              "projectCopyId": "synthetic", "sessionGeneration": "synthetic"}}
        refused = self.refused("change_prepare hmi_screen_create with target.kind hmiTagTable", self.a, "change_prepare",
                               {"kind": "hmi_screen_create", "payload": payload}, ("InvalidRequest",))
        message = error_of(refused).get("message") or ""
        require("takes target.kind hmiDevice" in message, "Not the early target.kind refusal: " + message)
        return {"refusal": message, "identity": "synthetic (no HMI in the scratch)"}

    def repeated_name_sim_write(self):
        before = self.ok(self.a, "sim_status")
        raw = RawCalls(self.a)
        row = repeated_member_object([("symbol", TEST_DB + ".Enable"), ("symbol", TEST_DB + ".Active"), ("type", "bool"), ("value", True)])
        arguments = '{"instanceId":0,"writes":[' + row + "]}"
        sequence = raw.send("sim_write", arguments)
        reply = raw.collect([sequence], {sequence: "sim_write"}, 120)[sequence]["message"]
        value = (reply.get("result") or {}).get("structuredContent") or {}
        observed = self.record("sim_write with a repeated name", {"arguments": arguments, "isError": (reply.get("result") or {}).get("isError"),
                                                                  "rpcError": reply.get("error"), "error": error_of(value)})
        require(error_of(value).get("code") == "InvalidRequest" and "repeats the property name" in (error_of(value).get("message") or ""),
                "Not refused as a repeated property name")
        after = self.ok(self.a, "sim_status")
        self.record("sim_status after", {"instancesBefore": len(before.get("instances") or []), "instancesAfter": len(after.get("instances") or [])})
        return observed

    def repeated_name_change_prepare(self):
        scratch = self.ensure_scratch()
        raw = RawCalls(self.a)
        arguments = '{"kind":"project_simulation_enable","payload":' + repeated_member_object([("enabled", True), ("enabled", False)]) + "}"
        sequence = raw.send("change_prepare", arguments)
        reply = raw.collect([sequence], {sequence: "change_prepare"}, 120)[sequence]["message"]
        value = (reply.get("result") or {}).get("structuredContent") or {}
        observed = self.record("change_prepare with a repeated name", {"arguments": arguments, "rpcError": reply.get("error"), "error": error_of(value)})
        require(error_of(value).get("code") == "InvalidRequest" and "repeats the property name" in (error_of(value).get("message") or ""),
                "Not refused as a repeated property name")
        status = self.ok(self.a, "tia_session_status")
        require(status.get("processId") == scratch["portal"] and any(same_path(p.get("path"), scratch["path"]) for p in status.get("projects") or []),
                "The worker session was lost: %s" % status)
        inventory = self.ok(self.a, "project_inventory", {"includeBlocks": False})
        return {**observed, "sessionKept": {"portal": status.get("processId"), "plcs": [p["path"] for p in inventory.get("plcs") or []]}}

    def queue_timeout_during_change_apply(self):
        scratch = self.ensure_scratch()
        # The apply must outlast the queue timeout (5 s minimum): a plc_compile of an unchanged scratch took 1.8 s natively, so
        # the timed apply generates and compiles 100 functions (change_prepare's limit) of 200 statements each instead.
        names = ["QtFc%03d" % i for i in range(100)]
        body = "".join("   #T := #T + %d;\n" % k for k in range(200))
        source = "\n".join('FUNCTION "%s" : Int\nVERSION : 0.1\n   VAR_TEMP\n      T : Int;\n   END_VAR\nBEGIN\n   #T := %d;\n%s   #%s := #T;\nEND_FUNCTION\n'
                           % (name, i, body, name) for i, name in enumerate(names))
        prepared = self.ok(self.a, "change_prepare", {"kind": "source_upsert", "plcPath": scratch["plcPath"],
                                                       "payload": {"language": "SCL", "source": source, "expectedBlockNames": names}})
        raw = RawCalls(self.a)
        apply_arguments = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                           "checkpointSave": True, "compile": True, "save": True}
        started = time.monotonic()
        first = raw.send("change_apply", json.dumps(apply_arguments))
        time.sleep(1)
        second = raw.send("project_inventory", json.dumps({"includeBlocks": False}))
        replies = raw.collect([first, second], {first: "change_apply", second: "project_inventory"}, 1800)
        apply_result = replies[first]["message"].get("result") or {}
        queued_result = replies[second]["message"].get("result") or {}
        queued = queued_result.get("structuredContent") or {}
        observed = self.record("change_apply and a queued project_inventory", {
            "applyState": (apply_result.get("structuredContent") or {}).get("state"), "applyIsError": apply_result.get("isError"),
            "applyDurationMs": duration_ms(apply_result), "applySeconds": round(replies[first]["receivedMonotonic"] - started, 1),
            "queuedIsError": queued_result.get("isError"), "queuedError": error_of(queued) or None, "queuedDurationMs": duration_ms(queued_result),
            "queuedAnsweredFirst": replies[second]["receivedMonotonic"] < replies[first]["receivedMonotonic"],
            "queueTimeoutSeconds": self.args.queue_timeout_seconds})
        require(error_of(queued).get("code") == "QueueTimeout",
                "The second call was not refused with QueueTimeout (the apply took %s s; queue timeout %s s)" % (observed["applySeconds"], self.args.queue_timeout_seconds))
        self.ok(self.a, "project_inventory", {"includeBlocks": False})
        return observed

    def simulation_download_ticket_state(self):
        scratch = self.ensure_scratch()
        require(scratch.get("credential"), "The scratch has no generated CPU credential")
        self.record("sim_status before", self.ok(self.a, "sim_status"))
        self.enable_simulation()
        try:
            return self.ticket_states(scratch)
        finally:
            for instance in list(self.instances):
                self.sim_release(instance)

    def ticket_states(self, scratch):
        result = {}
        # Unusable: not a ticket at all.
        value, failed, _ = self.call(self.a, "simulation_download", {"downloadTicket": "not-a-ticket"})
        result["unusable"] = self.record("simulation_download with a string that is not a ticket", {
            "failed": failed, "code": error_of(value).get("code"), "ticketState": details_of(value).get("ticketState"),
            "remediation": details_of(value).get("remediation")})
        # Before the claim: the exact instance is no longer registered (released), so nothing is written.
        released = self.sim_create()
        self.sim_release(released["instance"]["id"])
        value, failed, _ = self.call(self.a, "simulation_download", {"downloadTicket": released["downloadTicket"], "credentialMetadataPath": scratch["credential"]})
        details = details_of(value)
        result["reusable"] = self.record("simulation_download for a released instance", {
            "failed": failed, "code": error_of(value).get("code"), "ticketState": details.get("ticketState"),
            "retrySafe": details.get("retrySafe"), "remediation": details.get("remediation")})
        # After the claim: no credential for the protected scratch CPU fails inside TIA's password callback.
        spent = self.sim_create()
        value, failed, _ = self.call(self.a, "simulation_download", {"downloadTicket": spent["downloadTicket"]})
        details = details_of(value)
        result["spent"] = self.record("simulation_download without credentialMetadataPath", {
            "failed": failed, "code": error_of(value).get("code"), "ticketState": details.get("ticketState"),
            "retrySafe": details.get("retrySafe"), "completed": value.get("completed"), "configurations": details.get("configurations")})
        # A worker exception thrown from a download callback once left the Portal disposed (online route, 26 November); the
        # retry needs a session, so it is checked and rebuilt first. Without a credential the retry still reaches the spent check.
        status, lost, _ = self.call(self.a, "tia_session_status")
        result["sessionAfterCallbackFailure"] = self.record("tia_session_status after the callback failure", {
            "failed": lost, "attached": status.get("attached"), "projects": [p.get("path") for p in status.get("projects") or []]})
        if self.ensure_scratch() is not scratch:
            self.enable_simulation()
        value, failed, _ = self.call(self.a, "simulation_download", {"downloadTicket": spent["downloadTicket"]})
        result["spentRetry"] = self.record("the same ticket again", {"failed": failed, "code": error_of(value).get("code"),
                                                                       "ticketState": details_of(value).get("ticketState")})
        self.sim_release(spent["instance"]["id"])
        require(result["unusable"]["code"] == "InvalidSimulationTicket" and result["unusable"]["ticketState"] == "unusable",
                "The non-ticket was not reported unusable: %s" % result["unusable"])
        require(result["reusable"]["failed"] and result["reusable"]["ticketState"] == "reusable" and result["reusable"]["retrySafe"] is True,
                "The released instance's ticket was not reported reusable")
        require(result["spent"]["failed"], "TIA did not ask for the configuration password, so the after-claim failure was not provoked")
        require(result["spent"]["ticketState"] == "spent", "The after-claim failure was not reported spent")
        require(result["spentRetry"]["code"] == "FreshSimulationStorageRequired" and result["spentRetry"]["ticketState"] == "spent",
                "The spent ticket was not refused as spent")
        return result

    def simulation_registrations_present(self):
        self.ensure_scratch()
        foreign = self.record("sim_status before", self.ok(self.a, "sim_status"))
        created = self.sim_create()
        mine = {"id": created["instance"]["id"], "name": created["instance"]["name"]}
        try:
            value = self.refused("simulation_discovery_baseline with an owned instance registered", self.a, "simulation_discovery_baseline", {},
                                 ("SimulationRegistrationsPresent",))
            details = details_of(value)
            require(any(r.get("id") == mine["id"] and r.get("name") == mine["name"] for r in details.get("registered") or []),
                    "The refusal does not list the owned instance %s" % mine)
            require(details.get("remediation"), "The refusal has no remediation")
            detached = self.ok(self.a, "tia_detach", {"waitForExitSeconds": 120})
            self.scratch = None
            registrations = self.record("tia_detach plcsimRegistrations", detached.get("plcsimRegistrations"))
            require(registrations and registrations.get("checked") is True, "tia_detach did not read the registrations")
            require(any(r.get("id") == mine["id"] for r in registrations.get("instances") or []), "tia_detach does not list the owned instance")
            require("SimulationRegistrationsPresent" in (registrations.get("remediation") or ""), "tia_detach's remediation does not name SimulationRegistrationsPresent")
            return {"instance": mine, "otherInstancesBefore": [i for i in foreign.get("instances") or []],
                    "baselineRefusal": {"registered": details.get("registered"), "remediation": details.get("remediation")},
                    "plcsimRegistrations": registrations}
        finally:
            self.sim_release(mine["id"])

    def stale_change_directory_sweep(self):
        changes = (self.root / "a" / "changes").resolve()
        scratch = self.ensure_scratch()

        def prepare(label):
            prepared = self.ok(self.a, "change_prepare", {"kind": "plc_compile", "plcPath": self.scratch["plcPath"], "payload": {}})
            self.record("prepare " + label, {"changeId": prepared["changeId"], "state": prepared.get("state")})
            return prepared["changeId"]

        released, young = prepare("released, to be aged"), prepare("released, left young")
        self.record("tia_detach releases the preparations", self.ok(self.a, "tia_detach", {"waitForExitSeconds": 120}).get("projectsClosed"))
        self.scratch = None
        self.new_scratch()
        held = prepare("held, to be aged")
        journals = {}
        for change_id in (released, young, held):
            journal = change_journal(changes, change_id)
            content = json.loads(journal.read_text(encoding="utf-8"))
            require(content.get("changeId") == change_id and content.get("state") == "prepared", "%s is not a prepared journal: %s" % (change_id, content.get("state")))
            journals[change_id] = journal
        aged = time.time() - STALE_HOURS * 3600
        for change_id in (released, held):
            os.utime(journals[change_id], (aged, aged))  # Only the journal's write time, which the sweep reads; nothing else.
        self.record("journal write times set back", {c: datetime.fromtimestamp(journals[c].stat().st_mtime, timezone.utc).isoformat() for c in (released, held)})
        trigger = prepare("trigger")
        existing = sorted(p.name[len("change-"):] for p in changes.glob("change-*") if p.is_dir())
        log = changes / "pruned-changes.jsonl"
        logged = pruned_ids(log.read_text(encoding="utf-8") if log.exists() else "")
        problems = sweep_verdict([released], [young, held, trigger], existing, logged)
        observed = self.record("after the next change_prepare", {"removedExpected": released, "keptExpected": [young, held, trigger],
                                                                 "existing": existing, "pruneLog": log.read_text(encoding="utf-8") if log.exists() else None,
                                                                 "problems": problems, "firstScratch": scratch["path"]})
        require(not problems, "; ".join(problems))
        return observed

    def sim_write_signals_both_spellings(self):
        scratch = self.ensure_scratch()
        self.enable_simulation()
        self.record("sim_status before", self.ok(self.a, "sim_status"))
        if self.args.trust_owned_empty_cpu_certificate:
            self.record("simulation_discovery_baseline", self.ok(self.a, "simulation_discovery_baseline").get("baseline"))
        created = self.sim_create()
        instance = created["instance"]["id"]
        try:
            arguments = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": scratch["credential"]}
            download = self.ok(self.a, "simulation_download", arguments)
            self.record("simulation_download", {k: download.get(k) for k in ("completed", "saveRequiredBeforeProvisioning", "nextStep")})
            if download.get("saveRequiredBeforeProvisioning"):
                self.record("project_save", self.ok(self.a, "project_save").get("saved"))
            if self.args.trust_owned_empty_cpu_certificate:
                provisioned = self.ok(self.a, "simulation_provision_scratch_credential", {**arguments, "trustOwnedEmptyCpuCertificate": True})
                self.record("provisioning with first-use trust", {k: provisioned.get(k) for k in ("completed", "provisioned")})
            self.ok(self.a, "sim_state", {"instanceId": instance, "action": "powerOn", "timeoutMs": 60000})
            running = self.ok(self.a, "sim_state", {"instanceId": instance, "action": "run", "timeoutMs": 30000})
            require(running["instance"]["operatingState"] == "Run", "The instance is not in RUN")
            tags = self.ok(self.a, "sim_tags", {"instanceId": instance, "filter": TEST_DB, "maxItems": 2000})
            listed = [t["symbol"] for t in tags.get("tags") or [] if t["symbol"].replace('"', "") == TEST_DB + ".Enable"]
            require(len(listed) == 1, "Tag list lacks %s.Enable: %s" % (TEST_DB, listed))
            unquoted, quoted = spellings(listed[0])
            writes = [{"symbol": unquoted, "type": "bool", "value": True}, {"symbol": quoted, "type": "bool", "value": False}]
            refused = self.refused("sim_write_signals with both spellings of one tag", self.a, "sim_write_signals", {"instanceId": instance, "writes": writes}, ("InvalidRequest",))
            message = error_of(refused).get("message") or ""
            require("different spellings" in message, "Not the same-tag refusal: " + message)
            single = self.ok(self.a, "sim_write_signals", {"instanceId": instance, "writes": [{"symbol": quoted, "type": "bool", "value": True}]})
            readback = self.ok(self.a, "sim_read_signals", {"instanceId": instance, "signals": [{"symbol": unquoted, "type": "bool"}]})
            return self.record("spellings", {"tagList": listed[0], "unquoted": unquoted, "quoted": quoted, "refusal": message,
                                             "singleWrite": single, "readback": readback.get("values")})
        finally:
            for action in ("stop", "powerOff"):
                self.record("sim_state " + action, self.call(self.a, "sim_state", {"instanceId": instance, "action": action, "timeoutMs": 60000})[1])
            self.sim_release(instance)

    def loadable_provider_cpu_1518(self):
        scratch = self.ensure_scratch()
        entries = self.ok(self.a, "hardware_catalog_search", {"filter": "1518", "maxItems": 500}).get("entries") or []
        entry = catalog_cpu(entries, CPU_1518)
        self.record("catalog", {"entries": len(entries), "selected": entry})
        if entry is None:
            raise StageSkipped("The installed hardware catalog has no %s entry" % CPU_1518)
        station = "Followup1518"
        self.scratch["plcCount"] = 2  # From here on a later stage needs a fresh scratch (two PLCs make selectors ambiguous).
        applied = self.apply_change(self.a, "hardware_add", {"typeIdentifier": entry["typeIdentifier"], "deviceName": station,
                                                              "itemName": station + "Plc", "configureScratchSecurity": True})
        self.record("hardware_add", applied.get("state"))
        plcs = [p["path"] for p in self.ok(self.a, "project_inventory", {"includeBlocks": False}).get("plcs") or []]
        added = [p for p in plcs if "/devices/%s/" % station in p + "/"]
        require(len(added) == 1, "The added CPU's PLC is not listed: %s" % plcs)

        def loadable(plc):
            features = self.ok(self.a, "target_capabilities", {"selectors": {"kind": "plc", "plcPath": plc}}).get("features") or []
            return next((f for f in features if f.get("featureId") == "plc_loadable_generate"), None)

        observed = self.record("plc_loadable_generate", {"cpu1518": {"plcPath": added[0], "version": entry.get("version"), "feature": loadable(added[0])},
                                                         "cpu1511": {"plcPath": scratch["plcPath"], "feature": loadable(scratch["plcPath"])}})
        require(((observed["cpu1518"]["feature"] or {}).get("api") or {}).get("status") == "available",
                "LoadableProvider is not available on the CPU 1518 (version %s)" % entry.get("version"))
        return observed

    def ensure_seeded(self):
        if self.seeded:
            return self.seeded
        report_path = self.args.seeded_report.resolve(strict=True)
        archive = seeded_archive(json.loads(report_path.read_text(encoding="utf-8-sig")), self.args.hmi_profile)
        require(archive, "The seeded report has no completed, retrieval-verified %s baseline" % self.args.hmi_profile)
        path = Path(archive["path"]).resolve(strict=True)
        sha = hashlib.sha256(path.read_bytes()).hexdigest()
        require(sha == archive.get("sha256"), "The seeded baseline differs from the SHA-256 its report recorded")
        if not self.ok(self.a, "tia_session_status").get("attached"):
            self.ok(self.a, "tia_start")
        for _ in range(3):
            projects = self.ok(self.a, "tia_session_status").get("projects") or []
            if not projects:
                break
            self.record("project_close " + projects[0].get("name", ""), self.ok(self.a, "project_close", {"projectName": projects[0].get("name"), "discardUnsaved": True}).get("closed"))
        self.scratch = None
        retrieved = self.ok(self.a, "project_retrieve", {"archivePath": str(path), "expectedSha256": sha})
        family = FAMILY[self.args.hmi_profile]
        devices = {d["name"]: d for d in self.ok(self.a, "hmi_inspect", {"request": {"section": "devices"}}).get("items") or [] if d.get("family") == family}
        require("HMI_RT_1" in devices, "The baseline lacks HMI_RT_1 of family %s: %s" % (family, sorted(devices)))
        device = devices["HMI_RT_1"]
        screens = self.ok(self.a, "hmi_inspect", {"request": {"section": "screens", "target": {"family": family, "hmiPath": device["hmiPath"]}, "limit": 200}}).get("items") or []
        require(screens, "HMI_RT_1 has no screens")
        self.seeded = {"profile": self.args.hmi_profile, "family": family, "project": retrieved.get("project"), "device": device,
                       "screen": next((s for s in screens if s.get("name") == "Overview_A"), screens[0]), "screenCount": len(screens)}
        self.record("seeded baseline", {"profile": self.args.hmi_profile, "archive": str(path), "project": retrieved.get("project"),
                                        "device": device["identity"], "screen": self.seeded["screen"]["identity"]})
        return self.seeded

    def change_payloads(self, seeded):
        """(kind, valid payload) for the profile's family: a screen_create on Unified, a BackColor scalar on Classic."""
        if seeded["family"] == "unified":
            properties = seeded["screen"].get("properties") or {}
            size = {k: properties.get(k) if isinstance(properties.get(k), int) else v for k, v in (("Width", 800), ("Height", 480))}
            return "hmi_screen_create", {"schemaVersion": 1, "operation": "screen_create", "target": seeded["device"]["identity"],
                                         "name": "FollowupScreen", "properties": size, "expectedContentHash": "0" * 64}
        return "hmi_classic_import", {"schemaVersion": 1, "operation": "scalar_property_set", "target": seeded["screen"]["identity"],
                                      "properties": {"BackColor": "200, 210, 220"}, "expectedContentHash": "0" * 64}

    def hmi_seeded_page_and_kind_refusals(self):
        seeded = self.ensure_seeded()
        target = {"family": seeded["family"], "hmiPath": seeded["device"]["hmiPath"]}
        value, failed, ms = self.call(self.a, "hmi_inspect", {"request": {"section": "screens", "target": target, "limit": 1}})
        require(not failed, "hmi_inspect screens failed: %s" % error_of(value))
        page = self.record("hmi_inspect screens limit 1", {"keys": list(value)[:8], "page": value.get("page"), "durationMs": ms})
        require(key_order(value, "page", "items"), "page is not ahead of items")
        expected_stop = "limit" if seeded["screenCount"] > 1 else "end"
        require(value["page"].get("returned") == 1 and value["page"].get("stoppedBy") == expected_stop, "page bounds differ: %s" % value["page"])
        refused = self.refused("hmi_inspect tags with a screen identity", self.a, "hmi_inspect",
                               {"request": {"section": "tags", "target": seeded["screen"]["identity"]}}, ("InvalidRequest",))
        inspect_message = error_of(refused).get("message") or ""
        require("does not belong to section tags" in inspect_message, "Not the early target.kind refusal: " + inspect_message)
        kind, payload = self.change_payloads(seeded)
        wrong = {**payload, "target": {**payload["target"], "kind": "hmiTagTable"}}
        refused = self.refused("change_prepare %s with target.kind hmiTagTable" % kind, self.a, "change_prepare", {"kind": kind, "payload": wrong}, ("InvalidRequest",))
        change_message = error_of(refused).get("message") or ""
        require("takes target.kind" in change_message, "Not the early target.kind refusal: " + change_message)
        return {"page": page, "inspectRefusal": inspect_message, "changeRefusal": change_message, "changeRefusalDurationMs": self.report["steps"][-1]["result"]["durationMs"]}

    def hmi_preservation_unavailable_remediation(self):
        seeded = self.ensure_seeded()
        _, payload = self.change_payloads(seeded)
        value = self.refused("change_prepare hmi_engineering", self.a, "change_prepare", {"kind": "hmi_engineering", "payload": payload}, ("HmiPreservationUnavailable",))
        details = details_of(value)
        require(isinstance(details.get("remediation"), str) and details["remediation"].startswith("No change was made."),
                "HmiPreservationUnavailable carries no remediation")
        return {"message": error_of(value).get("message"), "remediation": details["remediation"],
                "gate": (details.get("gate") or {}).get("reasonCode"), "durationMs": self.report["steps"][-1]["result"]["durationMs"]}

    # Lifecycle ---------------------------------------------------------------------------------------------------------
    def execute(self):
        selected = set(self.args.stages.split(",")) if self.args.stages else None
        unknown = (selected or set()) - set(STAGES)
        if unknown:
            raise SystemExit("Unknown stages: %s" % sorted(unknown))
        plan = stage_plan(selected, {"running_instance": self.args.running_instance, "seeded_report": self.args.seeded_report is not None})
        self.report["plan"] = plan
        try:
            try:
                self.report["hostBuild"] = self.record("host A host_build_identity", self.ok(self.a, "host_build_identity"))
            except Exception as error:  # noqa: BLE001 - recorded; every stage reports its own failure
                self.record("host A host_build_identity failed", str(error))
            for name in STAGES:
                if name == "hmi_inspect_page_and_early_kind_refusal" and "b" in self.clients:
                    self.close_client("b")  # Host B's part ends with the exited client.
                self.run_stage(name, getattr(self, name), plan[name])
        finally:
            for instance in list(self.instances):
                self.sim_release(instance)
            for label in list(self.clients):
                self.close_client(label)
            self.report.update(summarize(self.report["stages"]))
            self.report["finishedUtc"] = now_utc()
            self.save()
            print(("PASSED " if self.report["completed"] else "FAILED ") + str(self.root / "report.json"), flush=True)

    def close_client(self, label):
        """Detach and close one Host; never raises, so every Host is closed and the report is written."""
        client = self.clients.pop(label)
        try:
            self.record("host %s tia_detach" % label.upper(), self.call(client, "tia_detach", {"waitForExitSeconds": 120})[:2])
        except Exception as error:  # noqa: BLE001 - the Host is closed regardless
            self.record("host %s tia_detach failed" % label.upper(), str(error))
        try:
            self.report.setdefault("hostClose", {})[label] = client.close()
        except Exception as error:  # noqa: BLE001 - recorded
            self.report.setdefault("hostClose", {})[label] = {"closeFailed": str(error)}
        self.save()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, help="Packaged engineering bundle root (host/, workers/); otherwise --host/--worker/--sim-worker.")
    parser.add_argument("--host", type=Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=Path, default=ROOT / "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe")
    parser.add_argument("--sim-worker", type=Path, default=ROOT / "src/TiaMcp.Simulator.V8/bin/Debug/net48/TiaMcp.Simulator.V8.exe")
    parser.add_argument("--stages", help="Comma-separated stage names to run (default all); the others are skipped. Names: " + ", ".join(STAGES))
    parser.add_argument("--queue-timeout-seconds", type=int, default=5, help="Host A's --queue-timeout-seconds (the Host's minimum is 5).")
    parser.add_argument("--busy-delay-seconds", type=float, default=8.0, help="Seconds after Host A's scratch creation starts before Host B attaches.")
    parser.add_argument("--exited-polls", type=int, default=6, help="tia_discover polls, 5 s apart, after Host C's job is terminated.")
    parser.add_argument("--running-instance", action="store_true", help="Run sim_write_signals_both_spellings (download to an owned instance, RUN).")
    parser.add_argument("--trust-owned-empty-cpu-certificate", action="store_true",
                        help="With --running-instance: provision the runtime credential with the user's explicit first-use TLS authorization for fresh owned empty instances.")
    parser.add_argument("--seeded-report", type=Path, help="HmiSeeder positive qualify report (for example artifacts/n26/hps/native-report.json).")
    parser.add_argument("--hmi-profile", default="unified-pc", choices=sorted(FAMILY))
    parser.add_argument("--hold-attach", type=int, help=argparse.SUPPRESS)  # Internal: Host C inside the owned job.
    args = parser.parse_args(argv)
    if not args.artifacts.resolve().is_relative_to((ROOT / "artifacts").resolve()):
        raise SystemExit("Artifacts must stay under the repository artifacts directory.")
    if args.hold_attach is not None:
        return hold_attach(args)
    if args.trust_owned_empty_cpu_certificate and not args.running_instance:
        parser.error("--trust-owned-empty-cpu-certificate is used only with --running-instance")
    if args.queue_timeout_seconds < 5:
        parser.error("--queue-timeout-seconds: the Host accepts 5 s at least")
    args.artifacts.resolve().mkdir(parents=True, exist_ok=False)
    run = Run(args)
    run.execute()
    return 0 if run.report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
