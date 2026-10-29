"""Check that every curated evidence run's raw report is in the main checkout's store.

Runs listed in evidence/raw-reports-lost.json were already gone when the store was
introduced on 28 October 2026; they are counted but do not fail the audit. Catalog
records from before curation cite raw files under artifacts/ directly; those must
exist in place or be listed there as lost.
"""
import json
import sys

from curate_evidence import ROOT, SCHEMA, file_sha256, store_root, stored_path

LOST = ROOT / "evidence" / "raw-reports-lost.json"
CATALOG = ROOT / "contracts" / "feature-catalog.v1.json"


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


def audit_uncurated(catalog, root, lost):
    """Raw files that pre-curation catalog records cite directly; lost holds (evidenceId, artifact) pairs."""
    result = {"inPlace": [], "lost": [], "missing": []}
    for record in catalog["evidence"]:
        for artifact in record["artifacts"]:
            if artifact.startswith("artifacts/"):
                key = "inPlace" if (root / artifact).is_file() else "lost" if (record["id"], artifact) in lost else "missing"
                result[key].append(f"{record['id']}/{artifact}")
    return result


def main():
    listed = json.loads(LOST.read_text(encoding="utf-8"))
    lost = {(r["evidenceId"], r["sha256"]) for r in listed["runs"]}
    store = store_root()
    result = audit(ROOT / "evidence", store, lost)
    print(f"Evidence store {store}: {len(result['retained'])} retained, {len(result['lost'])} lost before the store, "
          f"{len(result['missing'])} missing, {len(result['mismatched'])} changed")
    uncurated = audit_uncurated(json.loads(CATALOG.read_text(encoding="utf-8")), store.parent.parent,
                                {(r["evidenceId"], r["artifact"]) for r in listed.get("uncuratedArtifacts", [])})
    print(f"Uncurated catalog artifacts: {len(uncurated['inPlace'])} in place, {len(uncurated['lost'])} lost, "
          f"{len(uncurated['missing'])} missing")
    result["missing"] += uncurated["missing"]
    for name in result["missing"]:
        print("MISSING " + name)
    for name in result["mismatched"]:
        print("CHANGED " + name)
    return 1 if result["missing"] or result["mismatched"] else 0


if __name__ == "__main__":
    sys.exit(main())
