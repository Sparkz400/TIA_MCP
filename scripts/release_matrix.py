"""Generate or check the frozen V21 release matrix.

The matrix is derived from the feature catalog: one row per catalog feature, one
cell per acceptance criterion. A cell is bound only when the feature is verified,
and then names the evidence records and the hashes of their retained artifacts.
Every other cell is open work. Owners come from the release-obligation ledger;
features added after the ledger take the owner of their feature-name prefix.
"""
import argparse
import collections
import hashlib
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
CATALOG = "contracts/feature-catalog.v1.json"
LEDGER = "docs/plans/release-obligations-20261015.json"
MATRIX = "docs/plans/release-matrix.v1.json"
PROFILE_KEYS = ("tiaVersion", "cpu", "firmware", "language", "languages", "simulatorVersion")

# Obligation owners for features newer than the 15 October ledger, by feature-name prefix.
PREFIX_OWNERS = {
    "access_inspect": ["X03", "D12"], "access_review": ["X03"], "certificate_inspect": ["X03"], "change.graph_import": ["X06"], "plc_alarm_inspect": ["D11", "L04"], "web_inspect": ["D12"], "addin_": ["X04"], "automationml_": ["D05"], "cax_export": ["D05"], "change.cax_import": ["D05"],
    "change.block_attributes_set": ["X11"], "change.block_number_set": ["X11"], "change.connection_": ["D08"], "change.constant_create": ["X11"],
    "change.project_attributes_set": ["X01"], "change.project_languages_set": ["X01", "D03"], "change.software_unit_": ["D07"], "change.tag_create": ["X11"], "change.to_child_create": ["D13"], "change.to_connect": ["D13", "C02"], "change.to_disconnect": ["D13"],
    "change.hardware_cpu_memory_bytes": ["X10"], "change.hardware_port_": ["X10"], "change.hardware_replace": ["X10"], "change.plc_compile": ["X11"], "composite_engineering": ["C02"], "connection_inspect": ["D08"], "connection_review": ["D08"],
    "db_snapshot": ["D09"], "device_": ["X10"], "diagnostic_": ["D17"], "drive_observe": ["D10"], "drive_profile": ["D10"], "change.drive_parameter_set": ["D10"],
    "engineering_": ["F03"], "graph_": ["X06"], "hardware_gsd_export": ["X10"], "hmi_": ["X07", "X08"], "multiuser_": ["X05"],
    "opcua_": ["D04"], "plc.document_formats": ["X11"], "plc_compare": ["X01"], "plc_document_inventory": ["X11"], "plc_loadable_generate": ["X11"], "project_copy": ["X01"], "project_open.read_only": ["X01"], "project_properties": ["X01"], "project_snapshot": ["L04"], "public_certificate": ["X03"],
    "qualification_component": ["L02"], "qualification_testsuite": ["D06"],
    "qualification_translation": ["D03"], "qualification_watch": ["D02"],
    "runtime.physical_plc": ["D16"], "runtime_capture": ["D16"], "runtime_handoff": ["C03"],
    "runtime_ua": ["C03"], "runtime_web": ["D12"], "safety_evidence": ["D14"], "safety_validation_inspect": ["D14"],
    "simulation_": ["L03"], "software_unit": ["D07"], "teamcenter_": ["X09"],
    "technology_profile": ["D13"], "testsuite_": ["D06"], "translation_": ["D03"],
    "vci_": ["D01"], "watch_table": ["D02"], "web_application": ["D12"],
}


def support(feature):
    """Classify a feature the way release checklist section 1 asks."""
    if feature["implementation"]["status"] == "not_implemented":
        return "deferred"
    if feature["availability"]["status"] in ("unsupported", "blocked"):
        return "unavailable_target"
    if feature["qualification"]["status"] != "verified":
        return "unqualified"
    return "shipped" if feature["implementation"]["status"] == "implemented" else "limited"


def owners(feature_id, ledger_owners):
    if feature_id in ledger_owners:
        return ledger_owners[feature_id]
    matches = [prefix for prefix in PREFIX_OWNERS if feature_id.startswith(prefix)]
    if not matches:
        raise ValueError(f"No release owner for {feature_id}; add it to PREFIX_OWNERS.")
    return PREFIX_OWNERS[max(matches, key=len)]


def lineage(record, root):
    retained, absent = [], []
    for artifact in record["artifacts"]:
        path = root / artifact
        if artifact.startswith(("artifacts/", "output/")) or not path.is_file():
            absent.append(artifact)
        else:
            retained.append({"path": artifact, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    summary = {}
    for artifact in retained:
        if artifact["path"].startswith("evidence/"):
            summary = json.loads((root / artifact["path"]).read_text(encoding="utf-8")).get("source", {})
    return {"id": record["id"], "status": record["status"],
            "profile": {k: record["profile"][k] for k in PROFILE_KEYS if k in record["profile"]},
            "sourceCommit": summary.get("commit"), "retained": retained, "notRetained": absent}


def build(root=ROOT):
    catalog_bytes = (root / CATALOG).read_bytes()
    ledger_bytes = (root / LEDGER).read_bytes()
    catalog, ledger = json.loads(catalog_bytes), json.loads(ledger_bytes)
    ledger_owners = {row["featureId"]: row["obligations"] for row in ledger["catalogFeatures"]}
    roles = {row["id"]: row["ownerRole"] for row in ledger["obligations"]}
    records = {row["id"]: row for row in catalog["evidence"]}
    rows, cited = [], set()
    for feature in catalog["features"]:
        verified = feature["qualification"]["status"] == "verified"
        proofs = feature["qualification"]["evidenceIds"]
        cited.update(proofs)
        owner = owners(feature["featureId"], ledger_owners)
        unknown = set(owner) - roles.keys()
        if unknown:
            raise ValueError(f"{feature['featureId']}: unknown obligation {sorted(unknown)}")
        rows.append({
            "featureId": feature["featureId"], "domain": feature["domain"],
            "mcpTools": feature["mcpTools"], "changeKind": feature["changeKind"],
            "support": support(feature), "owners": owner, "evidenceIds": proofs,
            "cells": [{"gate": gate, "status": "bound" if verified else "open",
                       "evidenceIds": proofs if verified else []} for gate in feature["acceptanceCriteria"]]})
    classes = collections.Counter(row["support"] for row in rows)
    open_by_owner = collections.Counter()
    cells = collections.Counter()
    for row in rows:
        for cell in row["cells"]:
            state = "deferred" if row["support"] == "deferred" else cell["status"]
            cells[state] += 1
            if state == "open":
                open_by_owner.update(row["owners"])
    return {
        "schemaVersion": 1,
        "description": "Frozen V21 release matrix generated by scripts/release_matrix.py; do not edit by hand.",
        "frozenFrom": {"catalog": CATALOG, "catalogVersion": catalog["catalogVersion"],
                       "catalogSha256": hashlib.sha256(catalog_bytes).hexdigest(),
                       "ledger": LEDGER, "ledgerSha256": hashlib.sha256(ledger_bytes).hexdigest()},
        "targetTiaVersions": catalog["targetTiaVersions"],
        "summary": {"features": len(rows), "support": dict(sorted(classes.items())),
                    "cells": dict(sorted(cells.items())),
                    "openCellsByOwner": {k: {"role": roles[k], "open": v} for k, v in sorted(open_by_owner.items())}},
        "rows": rows,
        "evidence": [lineage(records[i], root) for i in sorted(cited)],
    }


def render(matrix):
    return json.dumps(matrix, indent=2, ensure_ascii=False) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the committed matrix is stale")
    args = parser.parse_args()
    text = render(build())
    target = ROOT / MATRIX
    if args.check:
        if not target.is_file() or target.read_text(encoding="utf-8") != text:
            raise SystemExit(f"{MATRIX} is stale; run python -B scripts/release_matrix.py")
        print(f"PASS {MATRIX} matches the catalog and ledger.")
        return
    target.write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {MATRIX}.")


if __name__ == "__main__":
    main()
