"""Validate every library plan, change result and lock receipt retained by a native library qualification report."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from validate_library_observations import validate_bytes

DETAILED = {"library_update_project": "update", "library_publish": "sync", "library_copy_elements": "sync"}


def kinds(change_kind):
    family = DETAILED.get(change_kind, "operation")
    return family + "-plan", family + "-change"


def checked(row, raw, kind, expected):
    try:
        return {**row, **validate_bytes(raw, kind, expected)}
    except ValueError as error:
        return {**row, "valid": False, "kind": kind, "error": str(error)}


def validate_report(path):
    report = json.loads(path.read_text(encoding="utf-8"))
    rows, kinds_by_id = [], {}
    for step in report["steps"]:
        result = step.get("result")
        if step.get("status") != "passed" or not isinstance(result, dict) or not str(result.get("changeId", "")).startswith("lib-") or "preview" not in result:
            continue
        plan_kind, change_kind = kinds(result["preview"]["kind"])
        prepared = step["name"].startswith("prepare ")
        observation = result["preview"] if prepared else result
        # Tool responses are retained parsed in the report; this re-serializes them, it is not their original bytes.
        rows.append(checked({"step": step["name"], "changeId": result["changeId"], "source": "reportReserialized"},
                            json.dumps(observation).encode(), plan_kind if prepared else change_kind, None))
        manifest = result.get("journal", {}).get("lockManifest")
        if manifest and result["changeId"] not in kinds_by_id:
            raw = Path(manifest["path"]).read_bytes()
            lock_kind = "compiler-lock" if json.loads(raw).get("compilation") else "lock"
            kinds_by_id[result["changeId"]] = lock_kind
            rows.append(checked({"step": step["name"], "changeId": result["changeId"], "source": "originalBytes"},
                                raw, lock_kind, manifest["sha256"]))
    return {"report": str(path), "reportSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "validated": len(rows), "invalid": sum(not r["valid"] for r in rows), "locks": len(kinds_by_id), "observations": rows}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path, nargs="+")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    summary = [validate_report(p) for p in args.report]
    args.output.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps([{k: s[k] for k in ("report", "validated", "invalid", "locks")} for s in summary]))
    raise SystemExit(1 if any(s["invalid"] for s in summary) else 0)
