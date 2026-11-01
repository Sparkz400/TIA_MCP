"""Merge a curated probe_technology_versions.py report into contracts/technology-version-profiles.v1.json.

Usage: python -B scripts/technology_version_profiles.py <qualification.json> <evidenceId> [--contract PATH]

Only native observations become cells: accepted stays accepted; refused (TIA's create threw) and substituted become refused
with the native code and message. failedAtApply and prepare-time refusals (including ones the profile already recorded)
change nothing. A cell of the
same CPU, type and version is replaced, so a later run supersedes an earlier one.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "contracts" / "technology-version-profiles.v1.json"


def version_key(text):
    return tuple(int(x) for x in text.split("."))


def native_message(row):
    """TIA's own words from one journaled exception row: its message data text and every detail text (the reason, such as
    'The type or version of the library element ... does not exist', sits in the details), else the exception message."""
    texts = [row.get("text"), row.get("detail")] + [t for d in row.get("details") or [] for t in (d.get("text"), d.get("detail"))]
    return " ".join(t.strip() for t in texts if t and t.strip()) or row.get("message")


def observed_cells(report, evidence_id):
    """(cpuTypeIdentifier, cpuTypeName, cell) for every native observation of a completed run."""
    if report.get("completed") is not True:
        raise ValueError("Only a completed probe run is merged.")
    rows = []
    for row in report["cells"]:
        cell = {"typeIdentifier": row["typeIdentifier"], "version": row["version"], "evidenceId": evidence_id}
        if row["outcome"] == "accepted":
            cell["outcome"] = "accepted"
        elif row["outcome"] == "refused":
            # Recomputed from the journaled chain, so a report written before native_message existed merges TIA's reason too.
            siemens = [chain[0] for chain in row.get("native") or [] if chain and str(chain[0].get("type") or "").startswith("Siemens.Engineering.")]
            message = native_message(siemens[0]) if siemens else row["nativeRefusal"]["message"]
            cell.update(outcome="refused", nativeRefusal={"code": row["nativeRefusal"]["code"], "message": message})
        elif row["outcome"] == "substituted":
            cell.update(outcome="refused", nativeRefusal={"code": "VersionSubstituted", "message": "V21 created %s %s instead." % (row["observed"]["typeIdentifier"], row["observed"]["version"])})
        else:
            continue
        rows.append((row["cpuTypeIdentifier"], row["cpuTypeName"], cell))
    return rows


def merge(document, observations):
    cpus = {cpu["cpuTypeIdentifier"]: cpu for cpu in document["cpus"]}
    for identity, name, cell in observations:
        cpu = cpus.setdefault(identity, {"cpuTypeIdentifier": identity, "cpuTypeName": name, "cells": []})
        cpu["cells"] = [c for c in cpu["cells"] if (c["typeIdentifier"], version_key(c["version"])) != (cell["typeIdentifier"], version_key(cell["version"]))] + [cell]
        cpu["cells"].sort(key=lambda c: (c["typeIdentifier"], version_key(c["version"])))
    return {**document, "cpus": sorted(cpus.values(), key=lambda cpu: cpu["cpuTypeIdentifier"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("report", type=Path)
    parser.add_argument("evidence_id")
    parser.add_argument("--contract", type=Path, default=CONTRACT)
    args = parser.parse_args()
    document = json.loads(args.contract.read_text(encoding="utf-8"))
    merged = merge(document, observed_cells(json.loads(args.report.read_text(encoding="utf-8")), args.evidence_id))
    with open(args.contract, "w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(merged, indent=2, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
