"""Check that every curated evidence run's raw report is in the main checkout's store.

Runs listed in evidence/raw-reports-lost.json were already gone when the store was
introduced on 28 October 2026; they are counted but do not fail the audit.
"""
import json
import sys

from curate_evidence import ROOT, SCHEMA, file_sha256, store_root, stored_path

LOST = ROOT / "evidence" / "raw-reports-lost.json"


def audit(evidence, store, lost):
    result = {"retained": [], "lost": [], "missing": [], "mismatched": []}
    for path in sorted(evidence.glob("*.json")):
        summary = json.loads(path.read_text(encoding="utf-8"))
        if summary.get("schema") != SCHEMA:
            continue
        for run in summary["runs"]:
            name = f"{summary['evidenceId']}/{run['report']}"
            target = stored_path(store, summary["evidenceId"], run)
            if target.is_file():
                result["retained" if file_sha256(target) == run["sha256"] else "mismatched"].append(name)
            else:
                result["lost" if (summary["evidenceId"], run["sha256"]) in lost else "missing"].append(name)
    return result


def main():
    lost = {(r["evidenceId"], r["sha256"]) for r in json.loads(LOST.read_text(encoding="utf-8"))["runs"]}
    store = store_root()
    result = audit(ROOT / "evidence", store, lost)
    print(f"Evidence store {store}: {len(result['retained'])} retained, {len(result['lost'])} lost before the store, "
          f"{len(result['missing'])} missing, {len(result['mismatched'])} changed")
    for name in result["missing"]:
        print("MISSING " + name)
    for name in result["mismatched"]:
        print("CHANGED " + name)
    return 1 if result["missing"] or result["mismatched"] else 0


if __name__ == "__main__":
    sys.exit(main())
