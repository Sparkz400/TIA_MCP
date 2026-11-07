"""Generate or check the frozen V21 release matrix.

The matrix is derived from the feature catalog: one row per catalog feature, one
cell per acceptance criterion. A cell is bound only when the feature is verified,
and then names the evidence records and the hashes of their retained artifacts.
Every other cell is open, except in two support classes summarized apart. Cells of
unavailable_target rows are blocked: they do not count toward 1.0 (maintainer decision,
29 October 2026; the ledger's dispositions records each row). A feature V21 has no API for is
unavailable_target even when not implemented. Cells of deferred (not implemented) rows are
deferred: not open cells, but still 1.0 work, listed in docs/TODO.md.
Owners come from the release-obligation ledger; features added after the ledger take the
owner of their feature-name prefix.
"""
import argparse
import collections
import datetime
import hashlib
import json
import pathlib
import re
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
CATALOG = "contracts/feature-catalog.v1.json"
LEDGER = "docs/plans/release-obligations-20261015.json"
MATRIX = "docs/plans/release-matrix.v1.json"
PROFILE_KEYS = ("tiaVersion", "cpu", "firmware", "language", "languages", "simulatorVersion")
# Support classes whose unbound cells are summarized apart from open 1.0 work.
NOT_OPEN = {"deferred": "deferred", "unavailable_target": "blocked"}

# Obligation owners for features newer than the 15 October ledger, by feature-name prefix.
PREFIX_OWNERS = {
    "change.hmi_classic_screen_edit": ["X07"],
    "access_inspect": ["X03", "D12"], "access_review": ["X03"], "certificate_inspect": ["X03"], "change.certificate_create": ["D12", "X03"], "change.certificate_import": ["D12", "X03"], "change.certificate_assign": ["D12", "X03"], "change.certificate_delete": ["D12", "X03"], "change.graph_import": ["X06"], "plc_alarm_inspect": ["D11", "L04"], "web_inspect": ["D12"], "addin_": ["X04"], "automationml_": ["D05"], "cax_export": ["D05"], "change.cax_import": ["D05"], "change.webserver_page_set": ["D12"], "change.webserver_user_pages_generate": ["D12"], "change.umac_role_set": ["X03", "D12"],
    "change.block_attributes_set": ["X11"], "change.block_number_set": ["X11"], "change.instance_db_create": ["X11"],"change.connection_": ["D08"], "change.constant_create": ["X11"],
    "change.project_attributes_set": ["X01"], "change.project_languages_set": ["X01", "D03"], "change.software_unit_": ["D07"], "change.block_protect": ["X11"], "change.block_unprotect": ["X11"], "change.block_write_protection_set": ["X11"], "change.opcua_": ["D04"], "change.plc_alarm_": ["D11"], "change.plc_supervision_": ["D11"], "change.tag_create": ["X11"], "change.testsuite_application_import": ["D06"], "change.testsuite_system_import": ["D06"],"change.to_child_create": ["D13"], "change.to_connect": ["D13", "C02"], "change.to_disconnect": ["D13"],
    "change.hardware_cpu_memory_bytes": ["X10"], "change.hardware_port_": ["X10"], "change.hardware_replace": ["X10"], "change.hardware_transfer_area_": ["X10"], "change.hardware_shared_device_access": ["X10"], "change.network_mrp_set": ["X10"], "change.plc_compile": ["X11"], "composite_engineering": ["C02"], "connection_inspect": ["D08"], "connection_review": ["D08"],
    "db_snapshot": ["D09"], "change.db_snapshot_": ["D09"], "device_": ["X10"], "diagnostic_": ["D17"], "diagnostic_plcsim_write": ["D02", "D16", "D17", "L03"], "diagnostic_runtime_acquisition_compare": ["D02", "D16", "D17", "C04"], "instance_lookup": ["D17", "L04", "X11"], "drive_observe": ["D10"], "drive_inventory": ["D10"], "servo_call_plan": ["D10"], "host_build_identity": ["L06"], "drive_profile": ["D10"], "change.drive_parameter_set": ["D10"], "change.drive_telegram_set": ["D10"], "change.safety_drive_tags": ["D14"],
    "engineering_": ["F03"], "graph_": ["X06"], "hardware_gsd_export": ["X10"], "hmi_": ["X07", "X08"], "multiuser_": ["X05"],
    "opcua_": ["D04"], "plc.document_formats": ["X11"], "plc_compare": ["X01"], "plc_online_compare": ["D09", "C03"], "plc_station_upload": ["D09"], "plc_document_inventory": ["X11"], "plc_loadable_generate": ["X11"], "project_copy": ["X01"], "project_upgrade": ["X01"], "project_open.read_only": ["X01"], "project_properties": ["X01"], "project_text_export": ["D03"], "project_snapshot": ["L04"], "public_certificate": ["X03"],
    "qualification_component": ["L02"], "qualification_testsuite": ["D06"],
    "qualification_translation": ["D03"], "qualification_watch": ["D02"],
    "runtime.physical_plc": ["D16"], "runtime_capture": ["D16"], "runtime_handoff": ["C03"],
    "runtime_plcsim_bind": ["D16", "C03", "L03"], "runtime_web_write": ["D16", "L03"], "runtime_web_operating_mode": ["D16", "L03"], "runtime_ua_write": ["D16", "C03", "L03"],
    "runtime_ua_call": ["D16", "C03", "L03", "X13"], "runtime_ua_conditions": ["D16", "C03", "X13"], "runtime_ua_history_probe": ["D16", "C03", "X13"],
    "library_update_check": ["X02", "L02"], "change.library_cleanup": ["X02", "L02"], "change.library_harmonize": ["X02", "L02"], "runtime_ua": ["C03"], "runtime_web": ["D12"], "safety_evidence": ["D14"], "safety_validation_inspect": ["D14"], "safety_printout": ["D14"], "change.safety_fparameter_set": ["D14"], "library.integrate_combined_apply": ["X02", "L02"],
    "simulation_": ["L03"], "software_unit": ["D07"], "teamcenter_": ["X09"],
    "sim_time": ["L03"], "sim_event": ["L03"], "sim_storage": ["L03"], "sim_read_signals": ["L03"], "sim_write_signals": ["L03"], "sim_area_": ["L03"], "sim_observe": ["L03"], "sim_record": ["L03"], "change.simulation_download_online": ["L03", "D09"],
    "change.hardware_cpu_parameters": ["X10"], "change.device_compile": ["X10", "X11"], "change.project_compile": ["X11", "X10"], "change.hmi_compile": ["X07"], "change.hmi_text_list_import": ["X07"], "change.hmi_classic_import": ["X07"], "change.hmi_external_tag_create": ["X08"], "change.hmi_screen_create": ["X08"], "change.hmi_dynamization_create": ["X08"], "change.hmi_event_handler_create": ["X08"], "change.tag_table_": ["X11"],
    "technology_profile": ["D13"], "testsuite_": ["D06"], "translation_": ["D03"],
    "vci_": ["D01"], "watch_table": ["D02"], "web_application": ["D12"],
    "change.vci_workspace_create": ["D01"], "change.vci_workspace_delete": ["D01"], "change.vci_mapping_add": ["D01"], "change.vci_mapping_remove": ["D01"], "change.vci_synchronize": ["D01"], "change.vci_conflict_resolve": ["D01"],
}


def support(feature):
    """Classify a feature the way release checklist section 1 asks."""
    # No V21 API route at all (block.move): a target boundary, not unbuilt 1.0 work.
    if feature["api"]["status"] == "unavailable" and feature["availability"]["status"] in ("unsupported", "blocked"):
        return "unavailable_target"
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
            # Maintainer decision, 29 October 2026: rows V21 cannot close stay blocked and do not count toward 1.0.
            state = NOT_OPEN.get(row["support"], cell["status"])
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


def disposition_errors(rows, dispositions):
    """A blocked row needs a maintainer disposition, and a disposition needs its blocked row."""
    blocked = {row["featureId"] for row in rows if row["support"] == "unavailable_target"}
    decided = {row["featureId"] for row in dispositions}
    return ([f"{f}: blocked without a ledger disposition" for f in sorted(blocked - decided)]
            + [f"{f}: ledger disposition without a blocked row" for f in sorted(decided - blocked)])


def acceptance_errors(obligation, ledger, matrix, root):
    """An approval flag must name the unchanged evidence and source the maintainer reviewed."""
    record = obligation.get("acceptance")
    prefix = obligation["id"] + ": "
    if not isinstance(record, dict):
        return [prefix + "acceptance record missing"]
    try:
        datetime.date.fromisoformat(record["date"])
        if record.get("acceptedBy") != "maintainer":
            raise ValueError("acceptedBy must be maintainer")
        for key, length in (("commit", 40), ("matrixSha256", 64)):
            if not isinstance(record.get(key), str) or not re.fullmatch("[0-9a-f]{%d}" % length, record[key]):
                raise ValueError(key + " must be a complete lowercase hash")
        for key in ("evidenceIds", "dispositions", "limitations"):
            values = record[key]
            if not isinstance(values, list) or any(not isinstance(v, str) or not v.strip() for v in values) or len(values) != len(set(values)):
                raise ValueError(key + " must contain unique nonempty strings")
        blockers = record["blockers"]
        if not isinstance(blockers, dict) or set(blockers) != set(obligation.get("blockers", [])) or any(
                not isinstance(v, str) or not v.strip() for v in blockers.values()):
            raise ValueError("every listed blocker needs a disposition")
        rows = [row for row in matrix["rows"] if obligation["id"] in row["owners"]]
        required = {e for row in rows for e in row["evidenceIds"]}
        known = {e["id"] for e in matrix["evidence"]} | {p.stem for p in (root / "evidence").glob("*.json")}
        if not required <= set(record["evidenceIds"]) <= known:
            raise ValueError("evidenceIds must cover every owned row and name current evidence")
        blocked = {r["featureId"] for r in rows if r["support"] == "unavailable_target"}
        if set(record["dispositions"]) != blocked:
            raise ValueError("dispositions must name exactly the owned blocked rows")
        decided = {d["featureId"] for d in ledger["dispositions"]}
        if not blocked <= decided:
            raise ValueError("blocked row has no ledger disposition")

        def git(*args):
            result = subprocess.run(["git", "-C", str(root), *args], capture_output=True, timeout=30)
            if result.returncode:
                raise ValueError("reviewed commit/matrix is unavailable or not an ancestor; fetch full history")
            return result.stdout

        git("merge-base", "--is-ancestor", record["commit"], "HEAD")
        reviewed_bytes = git("show", record["commit"] + ":" + MATRIX)
        if hashlib.sha256(reviewed_bytes).hexdigest() != record["matrixSha256"]:
            raise ValueError("reviewed matrix hash does not match its commit")
        reviewed = json.loads(reviewed_bytes)
        if rows != [r for r in reviewed["rows"] if obligation["id"] in r["owners"]]:
            raise ValueError("owned matrix rows changed after acceptance")
        current_proofs = [e for e in matrix["evidence"] if e["id"] in record["evidenceIds"]]
        if current_proofs != [e for e in reviewed["evidence"] if e["id"] in record["evidenceIds"]]:
            raise ValueError("accepted evidence changed after acceptance")
        # Ledger/matrix edits record the approvals; code, contracts and build inputs must already be frozen.
        changed = git("diff", "--name-only", record["commit"], "--", "src", "tests", "scripts", "fixtures",
                      "contracts", "evidence", "ui", "e2e", ".github", "Directory.Build.props", "global.json",
                      "NuGet.Config", "TiaMcp.slnx", "package.json", "package-lock.json")
        if changed.strip():
            raise ValueError("release inputs changed after acceptance; review the frozen candidate again")
    except (KeyError, TypeError, ValueError, OSError, subprocess.TimeoutExpired) as error:
        return [prefix + "invalid acceptance: " + str(error)]
    return []


def release_gate_errors(version, ledger, matrix, root=ROOT):
    """From 1.0 on, a release needs a ready ledger, every release obligation accepted and no open or deferred cell."""
    if int(version.split(".")[0]) < 1:
        return []
    cells = matrix["summary"]["cells"]
    # releaseReady alone is one flag; each obligation's own acceptance must agree with it.
    return ([] if ledger["releaseReady"] is True else ["ledger releaseReady is false"]) + [
        f"{o['id']}: release obligation not accepted" for o in ledger["obligations"]
        if o["releaseObligation"] and o["releaseAccepted"] is not True] + [
        error for o in ledger["obligations"] if o["releaseObligation"] and o["releaseAccepted"] is True
        for error in acceptance_errors(o, ledger, matrix, root)] + [
        f"{cells.get(state, 0)} {state} cells" for state in ("open", "deferred") if cells.get(state, 0)]


def render(matrix):
    return json.dumps(matrix, indent=2, ensure_ascii=False) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the committed matrix is stale")
    parser.add_argument("--release-gate", metavar="VERSION", help="fail unless VERSION may ship (1.0 and later need releaseReady, every obligation accepted and no open or deferred cells)")
    args = parser.parse_args()
    matrix = build()
    ledger = json.loads((ROOT / LEDGER).read_text(encoding="utf-8"))
    errors = disposition_errors(matrix["rows"], ledger["dispositions"])
    if args.release_gate:
        errors += release_gate_errors(args.release_gate, ledger, matrix)
    if errors:
        raise SystemExit("\n".join(["Release matrix check failed:", *errors]))
    if args.release_gate:
        print(f"PASS {args.release_gate} may ship.")
        return
    text = render(matrix)
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
