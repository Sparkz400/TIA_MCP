"""Qualify cax_export (D05) natively in one owned scratch CPU 1511-1 PN with a second CPU.

Exports each device and the whole project through Siemens CaxProvider. Each export must name the exact device, write an AML
file whose SHA-256 the retained AutomationML record repeats and whose instance hierarchy names the device and its CPU,
report a successful transfer without errors, and leave the complete state (the hardware runner's hardware, device inventory
and program state) unchanged. An unknown device path is refused without a change.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from qualify_hardware_changes import Run as HardwareRun, add_cpu
from typed_graphical_transport import artifact_bytes

CRITERIA = {
    "AC-identity": "Each export names the exact requested device (or the project) and its AML names that device and its CPU; an unknown device path is refused.",
    "AC-completeness": "The transfer result reports success without errors, and the retained AutomationML record repeats the AML's SHA-256 and validates against its schema.",
    "AC-native": "Native CaxProvider exports of two devices and the whole project; the project reports no unsaved change before or after and its complete state is unchanged.",
    "AC-evidence": "This report: binaries, tool calls, step results, the AML and transfer-log hashes and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 with an added CPU 1511-1 PN V4.1; drives, HMI and the device types Siemens excludes from CAx exchange are not exported.",
    "The AML is checked for its device and CPU names and the retained record's hash and schema validity, not interpreted further.",
    "cax_import is not qualified here: an imported S7-1500 CPU lacks its confidential-data password and fails the device compile.",
]


def export(run, label, device=None):
    h = run.h
    def check():
        before = run.state()
        result = h.tool("cax_export", {**({"hardwarePath": device} if device else {}), "maxItems": 500})
        transfer = result["transferResult"]
        record = result["automationml"]
        aml = artifact_bytes(h.tool, result["artifact"]).decode("utf-8-sig")
        names = [device.rsplit("/", 1)[1]] if device else ["ScratchStation", "HwPeer"]
        require(result.get("hardwarePath") == device and transfer["state"] == "Success" and transfer["errorCount"] == 0
                and result["projectModifiedBefore"] is False and result["projectModifiedAfter"] is False,
                "cax_export of %s reported %s" % (label, {k: result.get(k) for k in ("hardwarePath", "projectModifiedBefore", "projectModifiedAfter")}))
        require(record.get("sourceSha256") == result["artifact"]["sha256"] and record.get("schemaValid") is True, "The AutomationML record does not repeat the AML: %s" % record)
        require(all('Name="%s"' % name in aml for name in names), "The AML lacks %s" % names)
        require(run.state() == before, "cax_export changed the project")
        return {"artifact": result["artifact"], "log": result["log"], "record": record["recordRef"], "messages": len(transfer.get("messages", []))}
    h.report.setdefault("exports", {})[label] = h.stage("cax_export " + label, check)
    run.mark("cax_export", "applied", label)


def body(h):
    run = HardwareRun(h)
    h.report["criteria"] = CRITERIA
    h.owned_scratch("cax export")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.current = run.snap("baseline")
    add_cpu(run, "add CPU HwPeer", "HwPeer")
    export(run, "ScratchStation", run.path("ScratchStation"))
    export(run, "HwPeer", run.path("HwPeer"))
    export(run, "the project")
    def refused():
        before = run.state()
        error = h.call_error("cax_export", {"hardwarePath": run.path("TiaMcpMissing")})
        require(error.get("code") == "DeviceNotFound" and run.state() == before, "Expected a typed refusal of an unknown device: %s" % error)
        return error
    h.stage("refuse cax_export: an unknown device", refused)
    run.mark("cax_export", "refused", "unknown device")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "cx_", "D05 cax_export in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
