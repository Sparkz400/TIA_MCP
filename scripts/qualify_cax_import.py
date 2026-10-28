"""Qualify the D05 cax_import change kind natively in one owned scratch CPU 1511-1 PN.

Adds an S7-300 CPU 315-2 PN/DP device (it compiles without generated security, unlike an S7-1500 CPU), exports it with
cax_export, and restores the checkpoint taken before the device existed. cax_import with RetainTiaDevice then brings the
device back from the AML with checkpoint, device compile and save: the complete state (hardware, device inventory, every
PLC's program) must differ from the device-free state only below the imported device, whose hardware rows must equal the
exported device's. The import's restore runs first and must equal the device-free state; the import is then applied again,
and a fresh Portal reopen must show the identical state. Typed refusals must change nothing.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from qualify_hardware_changes import Run as HardwareRun, under
from qualify_plc_object_mutations import coverage_gaps, diff

KINDS = ("cax_import",)
DEVICE = "CaxCpu315"
CRITERIA = {
    "AC-identity": "refused: an AML given both by record and by path, an unknown import option and a changed hash, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: the complete state differs only below the imported device, whose hardware rows equal the exported device's.",
    "AC-native": "applied + persisted: checkpoint, device compile and save stages, change_status and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the device-free state.",
    "AC-evidence": "This report: binaries, tool calls, step results, the AML and transfer-log hashes, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "One S7-300 CPU 315-2 PN/DP device imported with RetainTiaDevice into an owned scratch CPU 1511-1 PN; an imported S7-1500 CPU fails the device "
    "compile because the AML carries no confidential-data password.",
    "MoveToParkingLot and OverwriteTiaDevice and devices that already exist in the target are not exercised.",
    "Engineering only; nothing is downloaded.",
]


def prepare_import(run, label, payload, aml):
    """The Host resolves amlRef to the retained AML's path and hash before the worker sees the payload."""
    prepared = run.h.stage("prepare " + label, lambda: run.h.tool("change_prepare", run.args("cax_import", payload)))
    shown = prepared["preview"]
    echoed = shown["payload"]
    require(prepared["state"] == "prepared" and shown["kind"] == "cax_import" and shown["project"]["path"] == run.project
            and echoed.get("importOption") == payload["importOption"] and echoed.get("amlRef") == payload["amlRef"]
            and echoed.get("expectedSha256") == aml["sha256"] and str(echoed.get("path", "")).lower().endswith(".aml"),
            "Preview of %s differs: %s" % (label, echoed))
    return prepared


def catalog_cpu(run):
    entries = run.h.tool("hardware_catalog_search", {"filter": "315-2 PN/DP", "maxItems": 200})["entries"]
    rows = sorted(e["typeIdentifier"] for e in entries if e.get("typeName") == "CPU 315-2 PN/DP" and "/V" in e["typeIdentifier"])
    require(rows, "No CPU 315-2 PN/DP in the installed catalog")
    return rows[-1]


def body(h):
    run = HardwareRun(h)
    h.report["criteria"] = CRITERIA
    h.owned_scratch("cax import")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.current = run.snap("baseline")
    empty = run.current
    identifier = catalog_cpu(run)
    added = run.prepare("add " + DEVICE, "hardware_add", {"typeIdentifier": identifier, "deviceName": DEVICE, "itemName": DEVICE + "Cpu"})
    run.finish("add " + DEVICE, "hardware_add", added, added=under(run.path(DEVICE)))
    original = {k: v for k, v in run.current["view"].items() if k.startswith("hw:" + run.path(DEVICE))}
    exported = h.stage("cax_export " + DEVICE, lambda: h.tool("cax_export", {"hardwarePath": run.path(DEVICE)}))
    require(exported["transferResult"]["state"] == "Success", "cax_export failed: %s" % exported["transferResult"])
    record, aml = exported["automationml"]["recordRef"], exported["artifact"]
    h.report["aml"] = {"record": record, "artifact": aml}
    run.b0 = empty
    run.restore("remove " + DEVICE + " through its checkpoint", "hardware_add", added)
    payload = {"importOption": "RetainTiaDevice", "amlRef": record}
    run.refuse("cax_import", "an AML given by record and by path", {**payload, "path": aml["path"], "expectedSha256": aml["sha256"]}, "InvalidRequest")
    run.refuse("cax_import", "an unknown import option", {**payload, "importOption": "TiaMcpMerge"}, "InvalidRequest")
    run.refuse("cax_import", "a changed hash", {"importOption": "RetainTiaDevice", "path": aml["path"], "expectedSha256": "0" * 64}, "ArtifactChanged")
    stale = prepare_import(run, "import %s (previewHash checked below)" % DEVICE, payload, aml)
    run.refuse_apply("cax_import", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    def same_device(applied, state):
        imported = {k: v for k, v in state.items() if k.startswith("hw:" + run.path(DEVICE))}
        require(imported == original, "The imported device differs from the exported one: %s" % diff(original, imported))
    for label, restore in (("import %s from its AML" % DEVICE, True), ("import %s again" % DEVICE, False)):
        prepared = prepare_import(run, label, payload, aml)
        run.finish(label, "cax_import", prepared, added=under(run.path(DEVICE)), check=same_device, restore=restore)
    final = run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.snap("reopened")
    h.stage("reopened state equals the import", lambda: require(run.current["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.current["view"])) or 1)
    run.mark("cax_import", "persisted", "fresh Portal reopen")
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "ci_", "D05 cax_import in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
