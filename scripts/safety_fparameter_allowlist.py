"""Derive allowlist rows from a curated probe_safety_fparameters.py report into contracts/safety-fparameter-allowlist.v1.json.

Usage: python -B scripts/safety_fparameter_allowlist.py <qualification.json> <evidenceId> [--contract PATH]

A qualified row is added for every F-parameter whose round trip passed; the worker-resolved module type must be one of the
catalog entries the run plugged, otherwise the merge stops. A later run's row replaces an earlier one (qualified or pending)
of the same module type and name. Refused, skipped and unattempted parameters add nothing, so the allowlist only ever
qualifies what a native write, readback and checkpoint restore showed.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "contracts" / "safety-fparameter-allowlist.v1.json"


def passed_rows(report, evidence_id):
    """(moduleTypeIdentifier, moduleTypeName, row) per passed round trip of a completed run. The worker-resolved module key
    must be one of the catalog entries the run plugged, so a rack or any other identity never becomes a row."""
    if report.get("completed") is not True:
        raise ValueError("Only a completed discovery run is merged.")
    plugged = [entry for entry in [report.get("fModule"), *(report.get("fModules") or {}).values()] if entry and "typeIdentifier" in entry]
    names = {entry["typeIdentifier"]: entry["typeName"] for entry in plugged}
    rows = []
    for module in report["modules"]:
        identity = module.get("moduleTypeIdentifier")
        passed = sorted(name for name, trip in module["roundTrips"].items() if trip["outcome"] == "passed")
        if passed and identity not in names:
            raise ValueError("Round trips on %s resolved module type %r, which is no plugged catalog entry." % (module["path"], identity))
        rows.extend((identity, names[identity], {"name": name, "status": "qualified", "evidenceId": evidence_id}) for name in passed)
    return rows


def merge(document, rows):
    modules = {module["moduleTypeIdentifier"]: module for module in document["modules"]}
    for identity, type_name, row in rows:
        module = modules.setdefault(identity, {"moduleTypeIdentifier": identity, "moduleTypeName": type_name, "parameters": []})
        module["parameters"] = sorted([p for p in module["parameters"] if p["name"] != row["name"]] + [row], key=lambda p: p["name"])
    return {**document, "modules": sorted(modules.values(), key=lambda module: module["moduleTypeIdentifier"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("report", type=Path)
    parser.add_argument("evidence_id")
    parser.add_argument("--contract", type=Path, default=CONTRACT)
    args = parser.parse_args()
    document = json.loads(args.contract.read_text(encoding="utf-8"))
    merged = merge(document, passed_rows(json.loads(args.report.read_text(encoding="utf-8")), args.evidence_id))
    with open(args.contract, "w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(merged, indent=2, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
