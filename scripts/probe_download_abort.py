"""Probe: can an Openness download be stopped from inside its pre-download callback before anything is loaded?

Question (docs/TODO.md, "P2 — Download pre-check as data"): the V21 public API has no preview or dry run of a download;
a DownloadConfiguration (with its Message) reaches the caller only through the delegate DownloadProvider.Download calls.
Throwing from that delegate may stop the download before anything is loaded. This probe aborts once at the first and once at
a later pre-download callback on owned PLCSIM Advanced V8 instances and checks that nothing was loaded.

Mechanism: the change kind simulation_download_online (src/TiaMcp.Openness.V21/OpennessSession.SimulationOnlineDownload.cs,
SimulationOnlineDownloadRules.cs). With dataBlockValues keepActualValues and allowStop false it answers
ConsistentBlocksDownload, AlarmTextLibrariesDownload, UserManagementDownload, PlcMasterSecretPassword and
DataBlockReinitializationOrKeepActualValues, and throws from the callback for DataBlockReinitialization
(SimulationDownloadReinitializationRequired), StopModules (SimulationDownloadStopRequired) and any other type
(UnsupportedDownloadConfiguration). Each callback is recorded in the readback report's downloadConfigurations (phase,
type, message, selection, exceptionType).

Fixture: an owned scratch CPU 1511-1 PN V4.1 with simulation support, a STANDARD-access, non-retain global DB AbortDb and
Main. Kept is written 7 through PLCSIM and never by the program, Cycles is incremented by Main (the program runs), and
ProgramVersion is written by Main (a Main change is observable). Standard access is deliberate: the optimized DB of the
29 October run (docs/verification/plcsim-openness-gaps-20261029.md) got DataBlockReinitializationOrKeepActualValues,
which keeps the values, so no refusal was reached.

One lifecycle per case. A refused simulation_download_online leaves its change recovery_required, and change_prepare then
refuses every further change on that project (RequireNoUnresolvedChange). change_restore retrieves a copy, and that copy
no longer matches the instance's card (SimulationTargetMismatch). So each case runs in a fresh owned Portal, scratch project
and instance: tia_start, tia_create_scratch, fixture, sim_create, simulation_download (card),
simulation_provision_scratch_credential, power on, RUN, then Kept := 7.

- first: offline, one member is added to AbortDb only, then a download with softwareScope changes, keepActualValues and
  allowStop false. Expected: refused at the first pre-download callback (DataBlockReinitialization or StopModules). If TIA
  answers everything and the download applies, the case is inconclusive. A fallback then runs in the same lifecycle,
  where no change is unresolved: a second member and softwareScope all with allowStop false. The hardware download of
  8 November asked StopModules first.
- later: offline, the same AbortDb member plus Main with ProgramVersion 2, same options. Expected:
  ConsistentBlocksDownload answered, then DataBlockReinitialization or StopModules refused. The order is recorded, not
  assumed.
- control: the same changes as later, with reinitialize and allowStop true. It must load: online compare identical
  afterwards, ProgramVersion 2, Kept reinitialized to 0, the new member in the PLCSIM tag list and a softwareConfiguration
  event in the sim_observe watch. This proves the checks would have seen a load. Its pre-download configurations are the
  reference list for the same change set.

Around each download the probe runs plc_online_compare before and after (owned ticket and generated runtime user). It also
reads the PLCSIM values (Cycles twice, to show the program runs), the sim_observe operating state and the PLCSIM tag
list, with a sim_observe watch across the download. Each case releases its instance with deleteOwned; the run detaches
at the end.

Reading report.json ("verdicts", "control", "outcome"):
- noGap: the refusal came from a pre-download callback and nothing changed online. The compare shows the same differences
  as before, Kept is still 7 with the old ProgramVersion, PLCSIM has no new member, the watch shows no software
  configuration or operating-state event, and the CPU still runs. For later, every pre-download configuration that the
  control raised for the same change set was also raised up to the refusal.
- gap: the aborted download loaded or stopped something, or configurations the control raised never arrived before the
  abort (the abort cannot return every pre-download configuration). Only then register an Openness gap
  (docs/reference/openness-gaps.md), as the TODO says.
- inconclusive: no refusal (TIA answered everything and the download applied), another failure, an expired ticket, or a
  check that could not run. Adapt the fixture and repeat before deciding.
Only a direction a load produces counts as evidence: compare rows that differed before and are identical after, or an
event kind that the baseline watch around the compare before (online, no download) did not show. Other changes are
recorded as unverified and make the verdict inconclusive. The outcome is noGap only when noGap verdicts cover an abort
at the first and at a later pre-download callback. A gap is a finding, not a failure: completed is true when every abort
case run has a noGap or gap verdict (first may take its fallback's), no case failed, the control showed that the compare
detects a load and, with all three cases run, the outcome is noGap or gap.

Opt-ins: --run-native, and --trust-owned-empty-cpu-certificate, the user's authorization of first-use TLS trust for
certificates from fresh, owned, empty scratch instances (trustOwnedEmptyCpuCertificate of credential provisioning).
--artifacts must lie inside the repository's artifacts/ (this checkout's or the main checkout's). Use a short one in the
main checkout: TIA limits the scratch project path to 143 characters.

  python -B scripts/probe_download_abort.py --bundle <bundle root> --artifacts <main checkout>/artifacts/da --run-native --trust-owned-empty-cpu-certificate
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import time
import traceback
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_memory_card, verify_owned_instance
from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
DB = "AbortDb"
NEW_MEMBER, SECOND_MEMBER = "Added", "Again"
KEPT = 7
DB_SOURCE = '''DATA_BLOCK "AbortDb"
{ S7_Optimized_Access := 'FALSE' }
VERSION : 0.1
NON_RETAIN
   VAR
      Kept : DInt;
      Cycles : DInt;
      ProgramVersion : Int;%s
   END_VAR

BEGIN

END_DATA_BLOCK

'''
MAIN_SOURCE = '''ORGANIZATION_BLOCK "Main"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1

BEGIN
    "AbortDb".Cycles := "AbortDb".Cycles + 1;
    "AbortDb".ProgramVersion := %d;
END_ORGANIZATION_BLOCK

'''
KEEP = {"softwareScope": "changes", "dataBlockValues": "keepActualValues", "allowStop": False}
REINITIALIZE = {"softwareScope": "changes", "dataBlockValues": "reinitialize", "allowStop": True}
FALLBACK = {"softwareScope": "all", "dataBlockValues": "keepActualValues", "allowStop": False}
# The offline change of each case: AbortDb members added after Kept/Cycles/ProgramVersion, and Main's ProgramVersion (None: unchanged).
CASES = {
    "first": {"members": (NEW_MEMBER,), "programVersion": None, "payload": KEEP, "intent": "first"},
    "later": {"members": (NEW_MEMBER,), "programVersion": 2, "payload": KEEP, "intent": "later"},
    "control": {"members": (NEW_MEMBER,), "programVersion": 2, "payload": REINITIALIZE, "intent": None},
}
FALLBACK_CASE = {"members": (NEW_MEMBER, SECOND_MEMBER), "programVersion": None, "payload": FALLBACK, "intent": "first"}
# The worker's refusals raised from a download callback (SimulationOnlineDownloadRules.Selection).
REFUSALS = ("SimulationDownloadReinitializationRequired", "SimulationDownloadStopRequired", "UnsupportedDownloadConfiguration")
# CompareResultState values that mean no difference (as in qualify_online_routes.py).
SAME = ("ObjectsIdentical", "FolderContentsIdentical", "CompareIrrelevant")
BUNDLE = {"host": "host/TiaMcp.Host.exe", "worker": "workers/openness/TiaMcp.Openness.V21.exe", "sim_worker": "workers/simulator/TiaMcp.Simulator.V8.exe"}
DEBUG = {"host": "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe", "worker": "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe",
         "sim_worker": "src/TiaMcp.Simulator.V8/bin/Debug/net48/TiaMcp.Simulator.V8.exe"}
LIMITATIONS = [
    "Owned PLCSIM Advanced V8 CPU 1511 instances on Softbus of owned scratch CPU 1511-1 PN V4.1 projects, provisioned with first-use TLS trust under the owned-empty-CPU policy; a physical PLC (B05) is not exercised.",
    "Aborts are the worker's own refusals (DataBlockReinitialization, StopModules, unknown types) raised from the Openness download callbacks; which callback TIA raises first is TIA's choice and is recorded, not forced.",
    "'Nothing loaded' is observed through plc_online_compare, PLCSIM values, the PLCSIM tag list, the operating state and a sim_observe watch; a change none of them shows is not excluded.",
    "The configuration list of an aborted download is compared with a control download of the same change set in another lifecycle; TIA may ask differently when earlier questions are answered differently.",
]


# --- pure helpers (offline tests: scripts/tests/test_probe_download_abort.py) -------------------------------------------

def resolve_binaries(bundle, host=None, worker=None, sim_worker=None, root=ROOT):
    """host, worker and sim_worker from a packaged bundle root, or the explicit paths, or the Debug build outputs."""
    explicit = {"host": host, "worker": worker, "sim_worker": sim_worker}
    if bundle is not None:
        require(not any(explicit.values()), "Pass either --bundle or --host/--worker/--sim-worker, not both")
        paths = {name: Path(bundle) / relative for name, relative in BUNDLE.items()}
    else:
        paths = {name: Path(explicit[name]) if explicit[name] else Path(root) / DEBUG[name] for name in DEBUG}
    for name, path in paths.items():
        require(path.is_file(), "%s not found: %s" % (name.replace("_", "-"), path))
    return {name: path.resolve() for name, path in paths.items()}


def artifact_roots(root=ROOT):
    """This checkout's artifacts/ and, from a git worktree, the main checkout's (curate_evidence.store_root uses the same)."""
    roots = [Path(root) / "artifacts"]
    try:
        common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=root, check=True, capture_output=True, text=True).stdout.strip()
        roots.append(Path(common).parent / "artifacts")
    except (OSError, subprocess.CalledProcessError):
        pass
    return roots


def inside_artifacts(path, roots):
    resolved = Path(path).resolve()
    return any(resolved == r.resolve() or resolved.is_relative_to(r.resolve()) for r in roots)


def nodes(tree):
    yield tree
    for child in tree.get("children") or []:
        yield from nodes(child)


def differences(tree):
    """The compare rows whose CompareResultState is neither identical nor irrelevant, sorted for comparison."""
    return sorted([[node.get("left"), node.get("right"), node.get("state")] for node in nodes(tree) if node.get("state") not in SAME], key=str)


def refusal_of(configurations):
    """The first recorded callback that threw, with its index among all callbacks and among the pre-download ones."""
    pre = 0
    for index, entry in enumerate(configurations or []):
        if entry.get("exceptionType"):
            return {"index": index, "preIndex": pre if entry.get("phase") == "pre" else None, "phase": entry.get("phase"), "type": entry.get("type"),
                    "message": entry.get("message"), "exceptionType": entry.get("exceptionType")}
        if entry.get("phase") == "pre":
            pre += 1
    return None


def pre_types(configurations):
    return [entry.get("type") for entry in configurations or [] if entry.get("phase") == "pre"]


def compare_shift(before, after):
    """Rows that differed before and are identical after (what a load does), rows whose differing state changed, and rows
    that differ only after. Rows are keyed by their left/right names."""
    was = {(left, right): state for left, right, state in before}
    now = {(left, right): state for left, right, state in after}
    identical = [list(key) for key in was if key not in now]
    changed = [[*key, was[key], now[key]] for key in was if key in now and now[key] != was[key]]
    new = [[*key, now[key]] for key in now if key not in was]
    return identical, changed, new


def load_evidence(row, new_member, old_version):
    """What shows that the aborted download loaded or stopped something, and which checks could not run or cannot be
    attributed to the download. Only a direction a load produces counts as evidence; anything else is unverified."""
    evidence, unverified = [], []
    download = row.get("download") or {}
    configurations = download.get("configurations") or []
    refusal = refusal_of(configurations)
    if refusal is not None and any(entry.get("phase") == "post" for entry in configurations[refusal["index"] + 1:]):
        evidence.append("TIA raised post-download configurations after the refusal")
    before, after = row.get("compareBefore") or {}, row.get("compareAfter") or {}
    if "differences" in before and "differences" in after:
        identical, changed, new = compare_shift(before["differences"], after["differences"])
        if identical:
            evidence.append("compare rows that differed before are identical after the abort: %s" % identical)
        if changed or new:
            unverified.append("compare rows that changed state %s or differ only after the abort %s" % (changed, new))
    else:
        unverified.append("online compare before and after")
    observed = row.get("after") or {}
    values = observed.get("values")
    if values is None:
        unverified.append("PLCSIM values")
    else:
        if values.get("Kept") != KEPT:
            evidence.append("Kept is %s instead of %d (data block reinitialized or reloaded)" % (values.get("Kept"), KEPT))
        if old_version is None:
            unverified.append("ProgramVersion before the download")
        elif values.get("ProgramVersion") != old_version:
            evidence.append("ProgramVersion is %s instead of %s (Main loaded)" % (values.get("ProgramVersion"), old_version))
    members = observed.get("members")
    if members is None:
        unverified.append("PLCSIM tag list")
    elif new_member in members:
        evidence.append("the PLCSIM tag list has the new member " + new_member)
    if observed.get("operatingState") is None:
        unverified.append("operating state")
    elif observed["operatingState"] != "Run":
        evidence.append("the CPU is in %s after the abort" % observed["operatingState"])
    if observed.get("cyclesAdvanced") is False:
        evidence.append("Main no longer counts Cycles")
    # An event kind counts only when the baseline watch around the compare before (online, no download) did not show it.
    watch, baseline = row.get("watch") or {}, row.get("watchBaseline") or {}
    if "kinds" not in watch:
        unverified.append("sim_observe watch")
    else:
        for kind, label in (("softwareConfiguration", "software configuration change events"), ("operatingState", "operating-state events")):
            events = [e for e in watch.get("events") or [] if e.get("kind") == kind]
            if not events:
                continue
            if "kinds" not in baseline:
                unverified.append("%s without a baseline watch: %s" % (label, events))
            elif kind in baseline["kinds"]:
                unverified.append("%s, also seen while only going online: %s" % (label, events))
            else:
                evidence.append("%s: %s" % (label, events))
    return evidence, unverified


def classify(row, intent, reference=None, same_change_set=False):
    """noGap, gap or inconclusive for one aborted download (see the module docstring)."""
    download = row.get("download") or {}
    configurations = download.get("configurations") or []
    result = {"intent": intent, "callbacks": [[e.get("phase"), e.get("type"), e.get("selection"), e.get("exceptionType")] for e in configurations],
              "errorCode": (download.get("error") or {}).get("code")}
    refusal = refusal_of(configurations)
    result["refusal"] = refusal
    if refusal is not None and refusal["phase"] == "pre":
        result["abortedAt"] = "first" if refusal["preIndex"] == 0 else "later"
        result["intentMet"] = result["abortedAt"] == intent
    attempted = pre_types(configurations)
    if reference is None:
        result["completeness"] = "unknown"
    else:
        # Only the configurations the control raised before the refused one can arrive: TIA stops asking at the refusal
        # (natively, 26 November: DataBlockReinitialization comes before ConsistentBlocksDownload).
        refused_type = (refusal or {}).get("type")
        expected = reference[:reference.index(refused_type) + 1] if refused_type in reference else reference
        missing = [t for t in expected if t not in attempted]
        result["missingConfigurations"] = missing
        result["completeness"] = ("incomplete" if missing else "complete") if same_change_set else "notComparable"
    old_version = ((row.get("before") or {}).get("values") or {}).get("ProgramVersion")
    evidence, unverified = load_evidence(row, (row.get("offlineChange") or {}).get("newMember"), old_version)
    result["loadEvidence"], result["unverified"] = evidence, unverified
    if not download or download.get("skipped"):
        return {**result, "verdict": "inconclusive", "reason": "the download did not run: " + (download.get("skipped") or "the case stopped before it")}
    if not download.get("isError"):
        return {**result, "verdict": "inconclusive", "reason": "no refusal: TIA's configurations were all answered and the download applied"}
    if result["errorCode"] not in REFUSALS:
        return {**result, "verdict": "inconclusive", "reason": "the download failed for another reason: %s" % result["errorCode"]}
    if refusal is None:
        return {**result, "verdict": "inconclusive", "reason": "a refusal code without a recorded callback exception"}
    if refusal["phase"] != "pre":
        return {**result, "verdict": "gap", "reason": "the refusal came from a post-download callback, after the load"}
    if evidence:
        return {**result, "verdict": "gap", "reason": "the aborted download loaded or stopped something"}
    if unverified:
        return {**result, "verdict": "inconclusive", "reason": "checks that could not run: %s" % unverified}
    if result["completeness"] == "incomplete":
        return {**result, "verdict": "gap", "reason": "configurations the control raised for the same change set never arrived before the abort"}
    return {**result, "verdict": "noGap", "reason": "refused in a pre-download callback; nothing observable was loaded"}


def control_check(row):
    """The control must load the changes, so that the compare is shown to detect a load."""
    download = row.get("download") or {}
    before, after = row.get("compareBefore") or {}, row.get("compareAfter") or {}
    observed = row.get("after") or {}
    values = observed.get("values") or {}
    checks = {
        "applied": download.get("isError") is False,
        "compareDifferedBefore": bool(before.get("differences")),
        "compareIdenticalAfter": "differences" in after and not after["differences"],
        "programVersion2": values.get("ProgramVersion") == 2,
        "keptReinitialized": values.get("Kept") == 0,
        "newMemberInPlcsim": NEW_MEMBER in (observed.get("members") or []),
        "softwareConfigurationEvent": bool((row.get("watch") or {}).get("softwareChanges")),
        "running": observed.get("operatingState") == "Run",
    }
    checks["sensitive"] = all(checks[k] for k in ("applied", "compareDifferedBefore", "compareIdenticalAfter", "programVersion2"))
    checks["preDownloadConfigurations"] = pre_types(download.get("configurations"))
    return checks


def overall(verdicts, control):
    found = [name for name, v in verdicts.items() if v.get("verdict") == "gap"]
    if found:
        return {"result": "gap", "cases": found, "next": "Register an Openness gap in docs/reference/openness-gaps.md with this report as evidence."}
    covered = {v.get("abortedAt") for v in verdicts.values() if v.get("verdict") == "noGap"}
    if {"first", "later"} <= covered and control and control.get("sensitive"):
        return {"result": "noGap", "next": "An abort from a pre-download callback loads nothing on PLCSIM; a pre-check can return the configurations up to the abort. Repeat on the bench PLC (B05)."}
    return {"result": "inconclusive", "covered": sorted(c for c in covered if c), "controlSensitive": bool(control and control.get("sensitive")),
            "next": "See each verdict's reason; adapt the fixture and repeat."}


# --- native steps ------------------------------------------------------------------------------------------------------

def raw(h, name, arguments):
    """A tool call whose failure is data: (isError, structuredContent)."""
    result = h.client.request("tools/call", {"name": name, "arguments": arguments})
    return bool(result.get("isError")), result.get("structuredContent") or {}


class Lab:
    def __init__(self, h, instance, deadline):
        self.h, self.instance, self.deadline, self.symbols = h, instance, deadline, {}

    def online_left(self):
        """An online session starts only with two minutes left on the 15-minute ticket."""
        return time.monotonic() < self.deadline

    def tags(self):
        return self.h.tool("sim_tags", {"instanceId": self.instance["id"], "filter": DB, "maxItems": 2000})["tags"]

    def members(self):
        return sorted({t["symbol"].replace('"', "").split(".", 1)[1] for t in self.tags() if t["symbol"].replace('"', "").startswith(DB + ".")})

    def resolve(self, names=("Kept", "Cycles", "ProgramVersion")):
        """PLCSIM answers NotUpToDate (-47) after a program change until sim_tags refreshes its tag list."""
        tags = self.tags()
        for name in names:
            found = [t["symbol"] for t in tags if t["symbol"].replace('"', "") == DB + "." + name]
            require(len(found) == 1, "PLCSIM symbol %s.%s is missing or ambiguous" % (DB, name))
            self.symbols[name] = found[0]

    def read(self, **signals):
        rows = self.h.tool("sim_read", {"instanceId": self.instance["id"], "signals": [{"symbol": self.symbols[n], "type": k} for n, k in signals.items()]})["values"]
        require(all(r["quality"] == "good" for r in rows), "Bad PLCSIM read: %s" % rows)
        return {n: r["value"] for n, r in zip(signals, rows)}

    def write(self, name, kind, value):
        self.h.tool("sim_write", {"instanceId": self.instance["id"], "writes": [{"symbol": self.symbols[name], "type": kind, "value": value}]})

    def state(self, action, timeout=60000):
        return self.h.tool("sim_state", {"instanceId": self.instance["id"], "action": action, "timeoutMs": timeout})["instance"]["operatingState"]

    def observe(self):
        """Values, whether Main still counts, the operating state (read only: sim_observe, never sim_state) and AbortDb's PLCSIM members.
        Each part that fails is recorded instead of failing the case."""
        result = {}
        try:
            self.resolve()
            result["values"] = self.read(Kept="int32", Cycles="int32", ProgramVersion="int16")
            time.sleep(1)
            result["cyclesAdvanced"] = self.read(Cycles="int32")["Cycles"] > result["values"]["Cycles"]
        except Exception as error:
            result["valuesError"] = str(error)
        try:
            result["operatingState"] = self.h.tool("sim_observe", {"instanceId": self.instance["id"]})["after"]["operatingState"]
        except Exception as error:
            result["operatingStateError"] = str(error)
        try:
            result["members"] = self.members()
        except Exception as error:
            result["membersError"] = str(error)
        return result


def compare(h, lab, base, plc):
    if not lab.online_left():
        return {"skipped": "the download ticket expires before another online session"}
    error, content = raw(h, "plc_online_compare", {"downloadTicket": base["downloadTicket"], "plcPath": plc, "maxItems": 2000,
                                                   "runtimeCredentialMetadataPath": base["runtimeCredentialMetadataPath"]})
    if error:
        return {"error": content.get("error")}
    tree = content["compare"]
    rows = [[n.get("left"), n.get("right"), n.get("state")] for n in nodes(tree) if any(name in (n.get(side) or "") for name in (DB, "Main") for side in ("left", "right"))]
    return {"truncated": content.get("compareTruncated"), "root": tree.get("state"), "differences": differences(tree), "rows": rows,
            "operatingStateBefore": content.get("operatingStateBefore"), "operatingStateAfter": content.get("operatingStateAfter")}


def download(h, plc, base, payload):
    """simulation_download_online whose refusal is the result; the worker's readback report carries every callback."""
    prepared = h.tool("change_prepare", {"kind": "simulation_download_online", "plcPath": plc, "payload": {**base, **payload}})
    row = {"kind": "simulation_download_online", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
    h.report["mutations"].append(row)
    h.persist()
    is_error, content = raw(h, "change_apply", {**h.apply_args(prepared), "compile": False, "save": False, "includeDetails": True})
    error = content.get("error") or {}
    readbacks = content.get("readbacks") or (error.get("details") or {}).get("readbacks") or []
    status_error, status = raw(h, "change_status", {"changeId": prepared["changeId"], "includeDetails": True})
    row["state"] = status.get("state") or ("failed" if is_error else content.get("state"))
    if not any(r.get("role") == "owned-simulation-download" for r in readbacks):
        readbacks = status.get("readbacks") or []
    report = next((r.get("report") or {} for r in readbacks if r.get("role") == "owned-simulation-download"), {})
    return {"changeId": prepared["changeId"], "isError": is_error, "error": {k: error.get(k) for k in ("code", "message")} if is_error else None,
            "changeState": row["state"], "changeStatusError": status.get("error") if status_error else None,
            "configurations": report.get("downloadConfigurations"), "options": report.get("options"), "stage": report.get("stage"),
            "downloadAttempted": report.get("downloadAttempted"), "download": report.get("download"), "failure": report.get("failure"),
            "operatingStateBefore": report.get("operatingStateBefore"), "operatingStateAfterDownload": report.get("operatingStateAfterDownload"),
            "operatingStateAfter": report.get("operatingStateAfter"), "onlineChallenges": report.get("onlineChallenges"), "cleanup": report.get("cleanup"),
            "projectModified": [report.get("projectModifiedBefore"), report.get("projectModifiedAfter")], "report": report}


def collect(h, lab):
    error, content = raw(h, "sim_observe", {"instanceId": lab.instance["id"], "watch": "collect", "windowMs": 1000})
    if error:
        return {"error": content.get("error")}
    events = content.get("events") or []
    return {"kinds": [e.get("kind") for e in events], "softwareChanges": [e.get("changeType") for e in events if e.get("kind") == "softwareConfiguration"],
            "operatingStateEvents": [e for e in events if e.get("kind") == "operatingState"], "truncated": content.get("truncated"), "events": events}


def attempt(h, lab, plc, base, label, spec, row):
    """Change the offline project, then compare, observe, download inside a watch, observe and compare again. row is filled
    in place, so a step that fails keeps what was observed before it."""
    version = spec["programVersion"]
    row.update(label=label, payload=spec["payload"], offlineChange={"members": list(spec["members"]), "newMember": spec["members"][-1], "programVersion": version})
    source = DB_SOURCE % "".join("\n      %s : Int;" % m for m in spec["members"]) + (MAIN_SOURCE % version if version else "")
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": source, "expectedBlockNames": [DB] + (["Main"] if version else [])})

    def prime():
        lab.resolve()
        lab.write("Kept", "int32", KEPT)
        return lab.read(Kept="int32")
    require(h.stage("Kept := %d before %s" % (KEPT, label), prime)["Kept"] == KEPT, "Kept was not written")
    # A baseline watch around the compare: what only going online raises, so the download's own events can be told apart.
    instance = lab.instance["id"]
    row["watchBaselineStart"] = h.stage("sim_observe baseline watch start before " + label, lambda: h.tool("sim_observe", {"instanceId": instance, "watch": "start", "maxEvents": 256}))
    row["compareBefore"] = h.stage("plc_online_compare before " + label, lambda: compare(h, lab, base, plc))
    row["watchBaseline"] = h.stage("sim_observe baseline watch collect before " + label, lambda: collect(h, lab))
    row["before"] = h.stage("PLCSIM before " + label, lab.observe)
    if not lab.online_left():
        row["download"] = {"skipped": "the download ticket expires before another online session"}
        return row
    row["watchStart"] = h.stage("sim_observe watch start before " + label, lambda: h.tool("sim_observe", {"instanceId": instance, "watch": "start", "maxEvents": 256}))
    row["download"] = h.stage("simulation_download_online " + label, lambda: download(h, plc, base, spec["payload"]))
    row["watch"] = h.stage("sim_observe watch collect after " + label, lambda: collect(h, lab))
    row["after"] = h.stage("PLCSIM after " + label, lab.observe)
    row["compareAfter"] = h.stage("plc_online_compare after " + label, lambda: compare(h, lab, base, plc))
    try:
        row["unresolvedAdmissions"] = h.unresolved_admissions()
    except Exception as error:
        row["unresolvedAdmissionsError"] = str(error)
    return row


def lifecycle(h, case, index):
    """One fresh owned Portal, scratch project and instance for one case (see the module docstring for why)."""
    if index:
        h.stage("detach the previous case's Portal", lambda: h.tool("tia_detach", {"waitForExitSeconds": 120}))
    plc = h.owned_scratch(case)
    cpus = h.scratch["scratchSecurity"]["cpus"]
    require(len(cpus) == 1 and cpus[0]["runtimeUser"].get("supported") is True, "Exactly one scratch CPU with a generated runtime user is required")
    secret, login = cpus[0]["credentialMetadataPath"], cpus[0]["runtimeUser"]["credentialMetadataPath"]
    h.apply("project_simulation_enable", {"enabled": True})
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": DB_SOURCE % "" + MAIN_SOURCE % 1, "expectedBlockNames": [DB, "Main"]})
    h.stage("discovery baseline " + case, lambda: h.tool("simulation_discovery_baseline", {})["baseline"])
    created = h.stage("sim_create " + case, lambda: h.tool("sim_create", {"cpuType": "CPU1511", "creationId": uuid.uuid4().hex}))
    # The ticket expires 15 minutes after sim_create; online steps start only with two minutes to spare.
    lab = Lab(h, verify_owned_instance(created["instance"]), time.monotonic() + 13 * 60)
    result = {"case": case, "plcPath": plc, "instance": lab.instance}
    h.report["cases"][case] = result
    try:
        ticket = created["downloadTicket"]
        card = {"downloadTicket": ticket, "credentialMetadataPath": secret}
        # The user's explicit first-use trust (--trust-owned-empty-cpu-certificate); main() refuses to start without it.
        trust = h.args.trust_owned_empty_cpu_certificate is True
        loaded = h.stage("simulation_download " + case, lambda: h.tool("simulation_download", card))
        verify_download(loaded, lab.instance)
        result["memoryCard"] = verify_memory_card(loaded)
        result["generatedProjectSave"] = save_generated_scratch_project(h.tool, loaded)
        provisioned = h.stage("simulation_provision_scratch_credential " + case, lambda: h.tool("simulation_provision_scratch_credential", {**card, "trustOwnedEmptyCpuCertificate": trust}))
        result["credentialProvisioning"] = verify_credential_provisioning(provisioned, loaded, trust_owned_empty_cpu_certificate=trust)
        h.stage("power on " + case, lambda: lab.state("powerOn"))
        require(h.stage("run " + case, lambda: lab.state("run", 30000)) == "Run", "The instance did not enter RUN")
        h.stage("resolve AbortDb in PLCSIM " + case, lab.resolve)
        require(h.stage("program version 1 runs " + case, lambda: lab.read(ProgramVersion="int16"))["ProgramVersion"] == 1, "Version 1 is not running")
        base = {"downloadTicket": ticket, "runtimeCredentialMetadataPath": login, "credentialMetadataPath": secret}
        attempt(h, lab, plc, base, case, CASES[case], result.setdefault("attempt", {}))
        h.persist()
        if case == "first" and (result["attempt"].get("download") or {}).get("isError") is False:
            # Applied: no change is unresolved, so a full software download (StopModules expected first) can be aborted here.
            attempt(h, lab, plc, base, "first fallback (softwareScope all)", FALLBACK_CASE, result.setdefault("fallback", {}))
    finally:
        released = h.stage("sim_release " + case, lambda: h.tool("sim_release", {"instanceId": lab.instance["id"], "deleteOwned": True}))
        result["released"] = released.get("unregistered")
    return result


def body(h):
    h.report["question"] = "docs/TODO.md P2 'Download pre-check as data': can an Openness download be stopped from inside its pre-download callback before anything is loaded?"
    h.report["simWorker"] = {"path": str(h.args.sim_worker), "sha256": hashlib.sha256(h.args.sim_worker.read_bytes()).hexdigest()}
    h.report["cases"] = {}
    for index, case in enumerate(h.args.cases):
        try:
            lifecycle(h, case, index)
        except Exception as error:
            h.report["cases"].setdefault(case, {})["failure"] = str(error)
            h.report["cases"][case]["traceback"] = traceback.format_exc()
        h.persist()
    cases = h.report["cases"]
    control_row = (cases.get("control") or {}).get("attempt")
    control = control_check(control_row) if control_row else None
    reference = control["preDownloadConfigurations"] if control and control["applied"] else None
    verdicts = {}
    for case in ("first", "later"):
        row = (cases.get(case) or {}).get("attempt")
        if row:
            verdicts[case] = classify(row, CASES[case]["intent"], reference, same_change_set=case == "later")
        fallback = (cases.get(case) or {}).get("fallback")
        if fallback:
            verdicts[case + "-fallback"] = classify(fallback, FALLBACK_CASE["intent"], None)
    h.report["control"], h.report["verdicts"], h.report["outcome"] = control, verdicts, overall(verdicts, control)
    h.persist()
    failed = sorted(case for case, row in cases.items() if "failure" in row)
    require(not failed, "Cases that failed before a verdict: %s" % failed)
    require(control is None or control["sensitive"], "The control did not show that the compare detects a load: %s" % control)
    # completed means the question was answered for every abort case run: an inconclusive case (first counts its fallback) fails the run.
    open_cases = [case for case in ("first", "later") if case in cases
                  and not {verdicts.get(case, {}).get("verdict"), verdicts.get(case + "-fallback", {}).get("verdict")} & {"noGap", "gap"}]
    require(not open_cases, "No noGap or gap verdict for %s: %s" % (open_cases, {c: verdicts.get(c, {}).get("reason") for c in open_cases}))
    # With every case run, noGap must also cover an abort at a later callback (TIA may raise the refused question first).
    require(set(h.args.cases) != set(CASES) or h.report["outcome"]["result"] != "inconclusive", "The question stays open: %s" % h.report["outcome"])


class ProbeHarness(Harness):
    def persist(self):
        """Keep the shared qualification.json and copy it to report.json, the name this probe's readers use."""
        super().persist()
        shutil.copyfile(self.directory / "qualification.json", self.directory / "report.json")


def parse(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bundle", type=Path, help="Packaged bundle root (host/TiaMcp.Host.exe, workers/openness/..., workers/simulator/...)")
    parser.add_argument("--host", type=Path)
    parser.add_argument("--worker", type=Path)
    parser.add_argument("--sim-worker", type=Path)
    parser.add_argument("--artifacts", type=Path, required=True, help="Inside the repository's artifacts/; keep it short (TIA's 143-character project path limit)")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--cases", default="first,later,control", help="Comma-separated subset of first, later, control (in this order)")
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--trust-owned-empty-cpu-certificate", action="store_true",
                        help="The user's authorization of first-use TLS trust for fresh, owned, empty scratch PLCSIM instances (default off; required)")
    args = parser.parse_args(argv)
    names = [c.strip() for c in args.cases.split(",") if c.strip()]
    if not names or any(c not in CASES for c in names) or len(set(names)) != len(names):
        parser.error("--cases takes distinct names from: " + ", ".join(CASES))
    args.cases = [c for c in CASES if c in names]
    return args


def main(argv=None):
    args = parse(argv)
    # Refuse before any Host or TIA starts.
    require(args.run_native, "Explicit --run-native is required")
    require(args.trust_owned_empty_cpu_certificate, "Credential provisioning needs --trust-owned-empty-cpu-certificate, the user's first-use trust of fresh owned empty scratch instances")
    require(inside_artifacts(args.artifacts, artifact_roots()), "--artifacts must lie inside the repository's artifacts/ directory: %s" % [str(r) for r in artifact_roots()])
    binaries = resolve_binaries(args.bundle, args.host, args.worker, args.sim_worker)
    args.host, args.worker, args.sim_worker = binaries["host"], binaries["worker"], binaries["sim_worker"]
    args.runner = __file__
    ProbeHarness(args, "da_", "Openness download abort from a pre-download callback on owned PLCSIM Advanced V8 instances", LIMITATIONS, []).run(body)


if __name__ == "__main__":
    main()
