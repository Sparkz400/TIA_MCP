"""Qualify the D06 Test Suite definition routes natively in one owned scratch CPU 1511-1 PN.

Default mode. testsuite_application_inventory and testsuite_system_inventory on the fresh scratch must report the Test Suite
service and empty, untruncated groups; an unknown projectName refuses. Phase A applies change kinds
testsuite_application_import (the packaged .tat and a renamed copy) and testsuite_system_import (the packaged .tst) with
checkpoint, device compile and save. Each preview is asserted against the request, and a complete state (every block, type and
tag-table export, every member and group path, and both inventories with every case's fresh native export hash) must differ
from the previous one in exactly the intended keys. maxItems=1 over two cases must report truncation. A fresh Portal then
reopens the saved project, which must show the identical state. Phase B imports one more case per kind and restores its
checkpoint, and an import whose reviewed names miss the file's case fails readback into recovery_required and is restored;
each recovered copy must equal the pre-change state. Typed refusals must change nothing.

--scope-probe mode runs a Host with --testsuite-scope-probe (a visible worker-owned Portal) in its own scratch:
qualification_testsuite_scope_probe refuses read before prepare and a foreign project path, prepares the packaged style rule
once (import and SetScope on Program blocks, saved, no executor), leaves every program export and both empty test inventories
unchanged, refuses a second prepare without replaying it, reads the rule back, and refuses to adopt the reopened project.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

from library_native_harness import Harness, require
from qualify_access_changes import Run as StateRun
from qualify_plc_object_mutations import coverage_gaps, diff

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "fixtures" / "testsuite" / "native-owned-v21" / "definitions"
KINDS = ("testsuite_application_import", "testsuite_system_import")
APP, SYS = "TiaMcp_ScratchSingleStep", "SingleStep"  # Case names inside the packaged native exports.
CRITERIA = {
    "AC-identity": "refused: an unknown projectName for both inventories; relative, wrong-family and colliding imports, set names with a .tat and empty name lists, each leaving the complete state unchanged.",
    "AC-completeness": "Both inventories report the service and empty, untruncated groups on a fresh scratch, every imported case after import, and truncation with counts at maxItems=1.",
    "AC-preview": "Every preview is asserted against the request (case names, source SHA-256, ImportOptions/load options None) before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every program export and member and both Test Suite inventories differ in exactly the intended keys.",
    "AC-native": "applied + persisted: checkpoint, device compile and save stages, change_status, fresh native .tat/.tst exports of each imported case and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore into a new directory equals the pre-change state, after a successful import and after a readback failure left the change recovery_required.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, the import files and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 and the packaged native .tat/.tst exports (one step each); application test sets (.ttst) are not imported because no native set export exists to start from.",
    "Definitions only: no SetScope, no Test Suite execution and no simulator; the system case's OPC UA endpoint stays text.",
    "The import file is not parsed: names are reviewed by the caller and checked by readback after the native import.",
]
PROBE_LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 and the packaged single-rule style fixture; SetScope's return is retained but is not complete effective-scope proof.",
    "No style executor runs and no license is checked; the probe Host flag enables no production profile.",
]


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def inventories(h, **extra):
    """Both inventories with fresh exports; refuses an absent service rather than reading it as empty."""
    result = {}
    for family, tool in (("tsApp", "testsuite_application_inventory"), ("tsSys", "testsuite_system_inventory")):
        inventory = h.tool(tool, {"export": True, "maxItems": 2000, **extra})["inventory"]
        require(inventory.get("servicePresent") is True and inventory.get("optionalAdapterAvailable") is not False,
                "%s did not observe the Test Suite service: %s" % (tool, inventory))
        result[family] = inventory
    return result


class Run(StateRun):
    plc_compiled = ()  # Test Suite imports compile all devices.

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded={}, inventoryChecks={}, exports={})

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, **top}

    def domains(self):
        state = {}
        for family, inventory in inventories(self.h).items():
            require(inventory["truncated"] is False, "Inventory unexpectedly truncated")
            state[family + ":@group"] = inventory["group"]
            for row in inventory.get("testCases", []):
                export = row.get("export") or {}
                if export:
                    self.h.report["exports"][family + ":" + row["name"]] = export
                state[family + ":" + row["name"]] = {**{k: v for k, v in row.items() if k != "export"},
                                                     "exportSha256": export.get("sha256"), "exportBytes": export.get("bytes")}
        return state

    def file(self, name, raw):
        path = self.h.directory / "imports" / name
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(raw)
        return str(path.resolve())

    def import_(self, label, kind, path, names, case, restore=False):
        family = "tsApp" if kind == "testsuite_application_import" else "tsSys"
        payload = {"sourcePath": path, "expectedTestCaseNames": names}
        effect = {"sourceSha256": sha(Path(path).read_bytes()), "importOptions": "None", "loadOptions": "None"}
        effect.update({"importTestCases": names, "importTestSets": []} if family == "tsApp" else {"importSystemTestCases": names})
        prepared = self.prepare(label, kind, payload, {"effects": [effect]})
        def exported(applied, state):
            row = state[family + ":" + case]
            require(row["exportSha256"] and not row.get("exportFailure"), "No fresh native export of %s: %s" % (case, row))
        self.finish(label, kind, prepared, added=[family + ":" + case], changed=[family + ":@group"], check=exported, restore=restore)
        return prepared


def renamed(raw, old, new):
    text = raw.decode("utf-8-sig")
    require(text.count('TEST_CASE "%s"' % old) == 1, "Expected one TEST_CASE %s in the fixture" % old)
    return text.replace('TEST_CASE "%s"' % old, 'TEST_CASE "%s"' % new).encode("utf-8")


def empty_and_identity(run):
    h = run.h
    def check():
        found = inventories(h)
        for family, inventory in found.items():
            require(inventory["group"]["testCaseCount"] == 0 and inventory["testCases"] == [] and inventory["truncated"] is False,
                    "%s is not an observed empty group: %s" % (family, inventory))
        require(found["tsApp"]["group"]["testSetCount"] == 0 and found["tsApp"]["testSets"] == [], "Unexpected application test sets")
        errors = {tool: h.call_error(tool, {"projectName": "TiaMcpNoSuchProject"}) for tool in ("testsuite_application_inventory", "testsuite_system_inventory")}
        require(all(e.get("code") == "ProjectNotFound" for e in errors.values()), "Unknown projectName was not refused: %s" % errors)
        return {"empty": {f: i["group"] for f, i in found.items()}, "unknownProject": errors}
    h.report["inventoryChecks"]["empty"] = h.stage("inventories: service present, empty groups, unknown project refused", check)


def truncation(run):
    h = run.h
    def check():
        inventory = h.tool("testsuite_application_inventory", {"maxItems": 1})["inventory"]
        require(inventory["truncated"] is True and len(inventory["testCases"]) == 1 and inventory["group"]["testCaseCount"] == 2,
                "maxItems=1 over two cases did not report truncation: %s" % inventory)
        return inventory
    h.report["inventoryChecks"]["truncated"] = h.stage("application inventory truncates at maxItems=1", check)


def phase_a(run, app, system):
    app_path, sys_path = run.file("application.tat", app), run.file("system.tst", system)
    A, S = "testsuite_application_import", "testsuite_system_import"
    run.refuse(A, "a relative path", {"sourcePath": "application.tat", "expectedTestCaseNames": [APP]}, "InvalidRequest")
    run.refuse(A, "a system .tst", {"sourcePath": sys_path, "expectedTestCaseNames": [SYS]}, "InvalidRequest")
    run.refuse(A, "set names with a .tat", {"sourcePath": app_path, "expectedTestCaseNames": [APP], "expectedTestSetNames": ["Set"]}, "InvalidRequest")
    run.refuse(A, "no expected case names", {"sourcePath": app_path, "expectedTestCaseNames": []}, "InvalidRequest")
    stale = run.prepare("application import (refused with a wrong previewHash)", A, {"sourcePath": app_path, "expectedTestCaseNames": [APP]})
    run.refuse_apply(A, "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    run.import_("import the packaged application case", A, app_path, [APP], APP)
    run.refuse(A, "an existing case", {"sourcePath": app_path, "expectedTestCaseNames": [APP]}, "ObjectExists")
    second = "TiaMcp_ScratchSecond"
    run.import_("import a second application case", A, run.file("second.tat", renamed(app, APP, second)), [second], second)
    truncation(run)
    run.refuse(S, "a relative path", {"sourcePath": "system.tst", "expectedTestCaseNames": [SYS]}, "InvalidRequest")
    run.refuse(S, "an application .tat", {"sourcePath": app_path, "expectedTestCaseNames": [APP]}, "InvalidRequest")
    run.refuse(S, "no expected case names", {"sourcePath": sys_path, "expectedTestCaseNames": []}, "InvalidRequest")
    run.import_("import the packaged system case", S, sys_path, [SYS], SYS)
    run.refuse(S, "an existing case", {"sourcePath": sys_path, "expectedTestCaseNames": [SYS]}, "ObjectExists")
    h = run.h
    h.report["inventoryChecks"]["roundTrip"] = {
        "application": {"fixtureSha256": sha(app), "exportSha256": h.report["exports"]["tsApp:" + APP]["sha256"]},
        "system": {"fixtureSha256": sha(system), "exportSha256": h.report["exports"]["tsSys:" + SYS]["sha256"]}}


def reopen(run):
    h, final = run.h, run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.b0 = run.snap("reopened")
    h.stage("reopened state equals every verified change",
            lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or len(final["view"]))
    for kind, rows in list(run.coverage.items()):
        if rows.get("applied"):
            run.mark(kind, "persisted", "fresh Portal reopen")


def phase_b(run, app, system):
    A, S = "testsuite_application_import", "testsuite_system_import"
    third = "TiaMcp_ScratchThird"
    run.import_("import a third application case", A, run.file("third.tat", renamed(app, APP, third)), [third], third, restore=True)
    other = "TiaMcp_SystemSecond"
    run.import_("import a second system case", S, run.file("second.tst", renamed(system, SYS, other)), [other], other, restore=True)
    path = run.file("mismatch.tat", renamed(app, APP, "TiaMcp_ScratchFourth"))
    prepared = run.prepare("application import whose reviewed name misses the file", A, {"sourcePath": path, "expectedTestCaseNames": ["TiaMcp_NotInFile"]})
    h = run.h
    def failed():
        error = h.call_error("change_apply", h.apply_args(prepared))
        require(error.get("code") == "MutationVerificationFailed", "Expected MutationVerificationFailed: %s" % error)
        status = h.tool("change_status", {"changeId": prepared["changeId"]})
        require(status["state"] == "recovery_required" and status["recoveryArchiveSha256"] and "testsuite_application_imported" in status["stages"]
                and "changed_project_saved" not in status["stages"], "Failed readback did not leave an unsaved recovery_required change: %s" % status)
        return {"error": error, "stages": status["stages"]}
    h.stage("readback mismatch fails into recovery_required", failed)
    run.restore("readback mismatch", A, prepared)


def body(h):
    app, system = (FIXTURES / "application.tat").read_bytes(), (FIXTURES / "system.tst").read_bytes()
    run = Run(h)
    run.plc = h.owned_scratch("Test Suite definitions")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    empty_and_identity(run)
    run.current = run.snap("baseline")
    phase_a(run, app, system)
    reopen(run)
    phase_b(run, app, system)
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


class ProbeRun(StateRun):
    """Program state plus both Test Suite inventories, which the style probe must leave empty."""

    def domains(self):
        return {family + ":@group": inventory["group"] for family, inventory in inventories(self.h).items()}


def probe_body(h):
    run = ProbeRun(h)
    h.report.update(criteria={
        "AC-identity": "Read before prepare, a foreign project path and the reopened (not worker-created) project are refused with typed codes.",
        "AC-preserve": "Every program export and both empty Test Suite inventories are unchanged by prepare.",
        "AC-recovery": "A second prepare is refused with RecoveryRequired and not replayed; the flushed intent and SetScope return are retained.",
        "AC-evidence": "This report and the probe's retained intent, SetScope return, observation and rule exports."})
    run.plc = h.owned_scratch("scope probe")
    run.project = project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = baseline = run.snap("baseline")
    probe = lambda action, path=project: {"action": action, "projectPath": path}
    def refused(label, arguments, code):
        def check():
            error = h.call_error("qualification_testsuite_scope_probe", arguments)
            require(error.get("code") == code, "Expected %s: %s" % (code, error))
            return error
        h.stage("refuse scope probe: " + label, check)
    refused("read before prepare", probe("read"), "InvalidRequest")
    refused("a foreign project path", probe("prepare", project + ".other"), "ProjectIsolationRequired")
    def prepare():
        result = h.tool("qualification_testsuite_scope_probe", probe("prepare"))
        rule = result["rules"][0]
        require(result["servicePresent"] is True and result["styleExecutorPresent"] is True and result["styleExecutionInvoked"] is False
                and result["completeEffectiveScope"] is False and result["nativeExecutionAuthorized"] is False and result["projectModified"] is False
                and rule["present"] is True and rule["effectiveScopeComplete"] is False, "Unexpected probe observation: %s" % result)
        directory = Path(result["artifactDirectory"])
        intent, returned = (directory / "setup-intent.json").read_text(encoding="utf-8"), (directory / "set-scope-return.json").read_text(encoding="utf-8")
        require('"executorInvoked": false' in intent and '"returned": true' in returned, "Missing flushed intent or SetScope return")
        result["ruleExportSha256"] = sha(Path(rule["definitionPath"]).read_bytes())
        return result
    prepared = h.stage("prepare the packaged style rule", prepare)
    run.current = run.snap("after prepare")
    h.stage("prepare left every program export and both inventories unchanged",
            lambda: require(run.current["view"] == baseline["view"], "Probe changed state: %s" % diff(baseline["view"], run.current["view"])) or len(baseline["view"]))
    refused("a second prepare", probe("prepare"), "RecoveryRequired")
    def read():
        result = h.tool("qualification_testsuite_scope_probe", probe("read"))
        require(result["rules"][0]["present"] is True and result["projectModified"] is False
                and sha(Path(result["rules"][0]["definitionPath"]).read_bytes()) == prepared["ruleExportSha256"], "Read differs from prepare: %s" % result)
        return result
    h.stage("read the rule back", read)
    h.stage("close the probed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(project))
    run.current = run.snap("reopened")
    h.stage("reopened state equals the baseline",
            lambda: require(run.current["view"] == baseline["view"], "Reopened state differs: %s" % diff(baseline["view"], run.current["view"])) or len(baseline["view"]))
    refused("the reopened project", probe("read"), "ProjectIsolationRequired")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--scope-probe", action="store_true")
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    if args.scope_probe:
        args.host_args = ["--testsuite-scope-probe"]
        Harness(args, "tp_", "D06 Test Suite scope probe in one owned scratch CPU 1511-1 PN", PROBE_LIMITATIONS, []).run(probe_body)
    else:
        Harness(args, "td_", "D06 Test Suite definition imports in one owned scratch CPU 1511-1 PN", LIMITATIONS,
                [FIXTURES / "application.tat", FIXTURES / "system.tst"]).run(body)
