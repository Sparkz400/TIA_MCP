"""Publish bounded typed-builder evidence without merging gates across attempts.

Only completed qualifier reports are included. A successful historical binary is
not automatically qualification of a later build. Native source captures and
pure tests intentionally do not enter this builder acceptance matrix.
"""
import argparse
import hashlib
import json
import os
import pathlib
from datetime import datetime, timezone
from block_test_runtime import digest


ROOT = pathlib.Path(__file__).resolve().parents[1]
GATES = ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified")


def relative(path):
    return str(path.resolve().relative_to(ROOT)).replace("\\", "/")


def attempt(path):
    raw = path.read_bytes()
    report = json.loads(raw)
    if not report.get("completedAtUtc") or not report.get("fixtureId"):
        return None
    source = {}
    if report.get("sourceReport"):
        source_path = pathlib.Path(report["sourceReport"])
        source_raw = source_path.read_bytes()
        source = json.loads(source_raw)
        assert digest(source) == report["sourceReportSha256"], "Continuation source report changed."
    runtime = report.get("runtime", {})
    assertions = [assertion for case in runtime.get("cases", []) for step in case.get("steps", []) for assertion in step.get("assertions", [])]
    gates = {key: report.get(key) is True for key in GATES}
    if gates["runtimeVerified"]:
        assert runtime.get("status") == "passed" and runtime.get("caseCoverage", {}).get("fullSuitePassed") is True
        assert assertions and all(item.get("status") == "passed" for item in assertions), "Runtime pass lacks complete executed assertions: " + str(path)
    binaries = {}
    for name, manifest in report.get("binaries", {}).items():
        if not manifest:
            continue
        executable = next((item for item in manifest.get("files", []) if item.get("path") == manifest.get("path")), None)
        binaries[name] = {"path": manifest.get("path"), "sha256": executable.get("sha256") if executable else None,
                          "manifestSha256": hashlib.sha256(json.dumps(manifest, sort_keys=True).encode()).hexdigest()}
    preview = report.get("prepared", source.get("prepared", {})).get("preview", {}).get("typed", {})
    failures = runtime.get("failures", [])
    for case in runtime.get("cases", []):
        failures.extend(case.get("failures", []))
    return {"report": relative(path), "reportSha256": hashlib.sha256(raw).hexdigest(),
        "fixtureId": report["fixtureId"], "language": report.get("language", source.get("language")), "status": report.get("status"),
        "sourceReport": report.get("sourceReport"), "sourceReportSha256": report.get("sourceReportSha256"),
        "sourceReportHashBasis": "SHA-256 of canonical JSON through block_test_runtime.digest" if source else None,
        "startedAtUtc": report.get("startedAtUtc"), "completedAtUtc": report["completedAtUtc"],
        "gates": gates, "allFourNativeGates": all(gates[key] for key in GATES[:4]),
        "allFiveGates": all(gates.values()), "passedAssertionCount": sum(item.get("status") == "passed" for item in assertions),
        "assertionCount": len(assertions), "target": preview.get("target"), "operation": preview.get("operation"),
        "profiles": preview.get("profiles", []), "binaries": binaries,
        "failure": report.get("failure"), "runtimeStatus": runtime.get("status"), "runtimeFailures": failures,
        "compileObservedSuccess": report.get("compileObservedSuccess", False),
        "failedChangeOriginalRecovered": report.get("failureRecoveryOriginalVerified", False),
        "failureRecoveryError": report.get("failureRecoveryError")}


def summarize(runs):
    records = []
    for run in runs:
        root = run.resolve()
        if not root.is_relative_to((ROOT / "artifacts").resolve()):
            raise ValueError("Qualification reports must be under the owned repository artifacts directory.")
        paths = [root] if root.is_file() else sorted(root.rglob("result.json"))
        records.extend(value for path in paths if (value := attempt(path)) is not None)
    records = list({item["report"]: item for item in records}.values())
    groups = {}
    for record in records:
        groups.setdefault(record["fixtureId"], []).append(record)
    cells = []
    for key, attempts in sorted(groups.items()):
        attempts.sort(key=lambda value: value["completedAtUtc"])
        # A cell's displayed evidence is ONE actual attempt, never the union of
        # runtime from one program and preservation/recovery from another.
        best = max(attempts, key=lambda value: (value["allFiveGates"], value["allFourNativeGates"],
                                               sum(value["gates"].values()), value["completedAtUtc"]))
        cells.append({"fixtureId": key, "language": best["language"], "selectedEvidence": best["report"],
            "latestAttempt": attempts[-1]["report"], "gates": best["gates"],
            "allFourNativeGates": best["allFourNativeGates"], "allFiveGates": best["allFiveGates"],
            "passedAssertionCount": best["passedAssertionCount"], "attempts": [item["report"] for item in attempts]})
    return {"schemaVersion": 1, "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "scope": "Completed exact fixture attempts only. Historical binary lineages remain explicit; this ledger does not certify the current release, remote hosts, unexecuted assertions or broader instruction/type/route combinations.",
        "counts": {"attempts": len(records), "distinctCells": len(cells),
                   "allFourNativeGates": sum(cell["allFourNativeGates"] for cell in cells),
                   "allFiveGates": sum(cell["allFiveGates"] for cell in cells)},
        "cells": cells, "attempts": records}


def markdown(ledger, markdown_path=None):
    markdown_path = pathlib.Path(markdown_path or ROOT / "docs/verification/typed-lad-fbd-qualification.md").resolve()
    counts = ledger["counts"]
    lines = ["# Typed LAD/FBD native qualification ledger", "", ledger["scope"], "",
             f"Generated {ledger['generatedAtUtc']}. {counts['allFourNativeGates']} of {counts['distinctCells']} executed cells have all four native gates; {counts['allFiveGates']} have all five gates including runtime. Failed and superseded attempts remain in the JSON ledger.", "",
             "E = import/compile; X = complete XML; P = save/reopen; R = checkpoint recovery/reopen; B = independent simulator assertions. A dash means that gate was not passed in the selected attempt.", "",
             "| Exact fixture | E | X | P | R | B | Executed passing assertions | Selected report |", "| --- | --- | --- | --- | --- | --- | --- | --- |"]
    for cell in ledger["cells"]:
        flags = " | ".join("pass" if cell["gates"][gate] else "—" for gate in GATES)
        report_path = pathlib.Path(os.path.relpath(ROOT / cell["selectedEvidence"], markdown_path.parent)).as_posix()
        lines.append(f"| `{cell['fixtureId']}` | {flags} | {cell['passedAssertionCount']} | [report](<{report_path}>) |")
    lines.extend(["", "The report path and its SHA-256, native target, profile IDs, operation, binary hashes, diagnostics, failed-change recovery and assertion counts are recorded in `fixtures/typed-graphical/qualification.v1.json`. A newly built binary needs its own acceptance evidence; earlier results are retained as development evidence.", ""])
    return "\n".join(lines)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", action="append", required=True, type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=ROOT / "fixtures/typed-graphical/qualification.v1.json")
    parser.add_argument("--markdown", type=pathlib.Path, default=ROOT / "docs/verification/typed-lad-fbd-qualification.md")
    args = parser.parse_args()
    ledger = summarize(args.run)
    args.output.write_text(json.dumps(ledger, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    args.markdown.write_text(markdown(ledger, args.markdown), encoding="utf-8")
    print(json.dumps(ledger["counts"]))
