"""Create a separate F-CPU scratch and inspect native offline safety metadata.

The catalog identifier was verified by smoke_safety_scratch.py. Existing scratch
reuse requires a completed source report and closed-file snapshot. No safety login
is attempted and no physical PLC is used.
"""
import argparse
import json
import hashlib
import pathlib
import uuid
import xml.etree.ElementTree as ET
from xml.dom import minidom

from smoke_mcp import ROOT
import safety_lifecycle
import safety_scratch_admission
from run_conformance import safe_xml
from engineering_preservation import require, native_payload, native_payload_hash as complete_native_payload_hash
from compilation_evidence import require_clean_compile, require_compile_only, require_baseline_compile, BASELINE_POLICY


def native_payload_hash(xml):
    """Exclude only export provenance timestamps, retaining all native object XML."""
    root = safe_xml(xml)
    for info in list(root.findall("DocumentInfo")):
        root.remove(info)
    return hashlib.sha256(ET.canonicalize(ET.tostring(root, encoding="unicode")).encode("utf-8")).hexdigest()


def edit_user_comment(xml):
    """One eligible offline F-block comment; retain every other native element."""
    root = safe_xml(xml)
    blocks = [item for item in root if item.tag in ("SW.Blocks.FB", "SW.Blocks.FC")]
    if len(blocks) != 1 or blocks[0].findtext("AttributeList/ProgrammingLanguage") not in ("F_LAD", "F_FBD"):
        raise ValueError("Comment fixture requires one native user F-LAD/F-FBD FC/FB export.")
    candidates = blocks[0].findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem/AttributeList/Text")
    if not candidates:
        raise ValueError("No existing native block comment text; do not manufacture safety XML.")
    # Keep native default namespaces/prefixes. ElementTree serialization can
    # introduce prefixed network nodes which the native importer may reject.
    document = minidom.parseString(xml)
    block = next(node for node in document.documentElement.childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName in ("SW.Blocks.FB", "SW.Blocks.FC"))
    def children(node, tag):
        return [child for child in node.childNodes if child.nodeType == child.ELEMENT_NODE and child.tagName == tag]
    objects = children(block, 'ObjectList')[0]
    comment = next(node for node in children(objects, 'MultilingualText') if node.getAttribute('CompositionName') == 'Comment')
    item = children(children(comment, 'ObjectList')[0], 'MultilingualTextItem')[0]
    text = children(children(item, 'AttributeList')[0], 'Text')[0]
    text.appendChild(document.createTextNode(' [TIA MCP offline preservation fixture]'))
    return document.toxml()


def require_signature_evidence(snapshot):
    require(isinstance(snapshot, dict), "Native safety signatures require an inspection object")
    coverage = snapshot.get('completeness')
    require(isinstance(coverage, dict) and coverage.get('truncated') is False and coverage.get('scopeComplete') is True,
            "Truncated or incomplete native safety signatures cannot qualify preservation")
    require(snapshot.get('warnings') == [], "Incomplete native safety signatures require explicit empty warnings")
    require(snapshot.get('blockSignatureServiceAvailable') is True, "Selected-block signature service is unavailable")
    program = {'CollectiveOfflineSignature', 'SoftwareOfflineSignature', 'HardwareOfflineSignature', 'CommunicationAddressOfflineSignature'}
    # This is the existing 1511F fixture's signature set, not a generic safety profile.
    for key, expected in (('programSignatures', program), ('blockSignatures', {'BlockOfflineSignature'})):
        rows = snapshot.get(key)
        require(isinstance(rows, list) and len(rows) == len(expected), 'Missing or duplicate native safety signatures: ' + key)
        seen = set()
        for row in rows:
            require(isinstance(row, dict) and set(row) == {'type', 'value'} and isinstance(row.get('type'), str)
                    and row['type'] in expected and row['type'] not in seen,
                    'Unknown, duplicate or malformed native safety signature identity: ' + key)
            value = row['value']
            require(type(value) is int and 0 <= value < 2**64, 'Native safety signature value must be UInt64: ' + row['type'])
            require(value != 0 or row['type'] == 'CommunicationAddressOfflineSignature',
                    'Required native safety signature is absent or invalid: ' + row['type'])
            seen.add(row['type'])


def require_applied_evidence(applied, safety, selected_path):
    """A native import/compile alone does not qualify the post-compile correction."""
    require_signature_evidence(safety)
    plc_path = selected_path.rsplit('/blocks/', 1)[0]
    block = safety.get('block')
    require(safety.get('plcPath') == plc_path and isinstance(block, dict) and block.get('path') == selected_path
            and block.get('plcPath') == plc_path, 'Safety readback does not identify the selected PLC and block')
    require(applied.get("state") == "applied", applied)
    require_clean_compile(applied.get("compile"), expected_targets=[plc_path])
    require({"compile_finished", "changed_project_saved"}.issubset(applied.get("stages", [])), 'Safety evidence requirement failed: {"compile_finished", "changed_project_saved"}.issubset(applied.get("stages", []))')
    fingerprint = applied.get("afterFingerprint")
    require(isinstance(fingerprint, str) and len(fingerprint) == 64 and all(c in "0123456789abcdef" for c in fingerprint), "Missing durable post-change fingerprint")
    reported = applied.get('safetyAfter', {}).get('signatures')
    require(isinstance(reported, list) and all(isinstance(row, dict) and type(row.get('value')) is int for row in reported)
            and reported == safety['programSignatures'], 'Reported post-compile safety signatures differ from native readback')
    coverage = applied.get("fingerprintCoverage", {})
    require(coverage.get("policyVersion") == 2 and coverage.get("complete") is False, "Generated exclusions must retain explicit incomplete coverage")
    excluded = coverage.get("protectedGeneratedPayloadExclusions", [])
    require(any(row.get("name") == "DB1_C" for row in excluded), "Known generated DB1_C fingerprint path was not exercised")
    system_prefix = selected_path.rsplit("/blocks/", 1)[0] + "/blocks/systemGroups/"
    require(all(row.get("mode") == "protectedGeneratedMetadata" and row.get("path", "").startswith(system_prefix)
               and row["path"] != selected_path for row in excluded), "Generated exclusion escaped native system group")
    safety_coverage = coverage.get('safetyCoverage')
    require(isinstance(safety_coverage, list) and all(isinstance(row, dict) for row in safety_coverage),
            'Missing or malformed owning-PLC safety coverage')
    matching = [row for row in safety_coverage if row.get('plcPath') == plc_path]
    require(len(matching) == 1 and matching[0].get('available') is True
            and type(matching[0].get('signatureCount')) is int
            and matching[0]['signatureCount'] == len(safety['programSignatures']),
            'Missing, duplicate or incomplete owning-PLC safety signature coverage')
    return coverage


def require_safety_equal(expected, actual):
    require_signature_evidence(expected)
    require_signature_evidence(actual)
    for key in ("programSignatures", "blockSignatures", "runtimeGroups"):
        require(actual.get(key) == expected.get(key), "Native safety readback differs: " + key)


def require_native_equal(expected, actual):
    """Check the complete selected block set, retaining all non-provenance XML."""
    require(set(actual) == set(expected), "Native preservation block selection changed")
    for path in expected:
        require(native_payload_hash(actual[path]["xml"]) == native_payload_hash(expected[path]["xml"]), "Native block payload differs: " + path)
        require(native_payload(actual[path]["xml"]) == native_payload(expected[path]["xml"]), "Full native block payload differs: " + path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", help="Reopen the exact saved scratch created by a previous run; never a user project.")
    parser.add_argument("--source-report", type=pathlib.Path, help="Completed prior run with exact terminal process and closed-project evidence; required with --project.")
    parser.add_argument("--source-report-sha256", help="Expected SHA-256 of the prior report; required with --project.")
    parser.add_argument("--report", default="artifacts/safety-direct-smoke.json")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, default=ROOT / "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe")
    parser.add_argument("--artifacts", type=pathlib.Path, default=ROOT / "artifacts", help="Keep native artifact/project paths short even when using an isolated build.")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--roundtrip", action="store_true", help="Re-import one unchanged native user F-block with archive/compile/save and verify signatures.")
    parser.add_argument("--edit-comment", action="store_true", help="With --roundtrip, change one existing user F-block comment and verify all other XML.")
    parser.add_argument("--verify-recovery", action="store_true", help="With --roundtrip, restore the checkpoint into a new project and verify native XML and signatures.")
    args = parser.parse_args()
    if (args.edit_comment or args.verify_recovery) and not args.roundtrip:
        parser.error("--edit-comment and --verify-recovery require --roundtrip")
    report_path = ROOT / args.report
    require(not report_path.exists(), "Choose a new report path; prior safety evidence is immutable")
    if bool(args.project) != bool(args.source_report) or bool(args.project) != bool(args.source_report_sha256):
        parser.error("--project requires --source-report and --source-report-sha256 together")
    admission = (safety_scratch_admission.load_source(ROOT / args.project, args.source_report,
        args.source_report_sha256, args.artifacts, ROOT) if args.project else None)
    if admission:
        require(not report_path.absolute().is_relative_to(pathlib.Path(admission['closedScratch']['projectPath']).parent),
                'Safety reports must remain outside the mutable project tree')
    report = {"steps": [], "completed": False, "scope": "New worker-owned F-CPU scratch only"}
    if admission:
        report.update(scratchAdmission=admission, scope="Exact closed scratch from a byte-bound completed prior run")
    prepared = None
    apply_started = False
    restore_attempted = False
    active_change = None

    def persist():
        safety_lifecycle.sync_steps(report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    session = safety_lifecycle.new_session(args.host.resolve(), args.worker.resolve(), args.artifacts.resolve(),
        report, persist, args.artifacts / ("safety-lifetimes-" + uuid.uuid4().hex), args.timeout)

    def call(name, arguments=None, allow_error=False):
        return safety_lifecycle.call(session, name, arguments, allow_error)

    def reopen(project_path):
        nonlocal active_change
        previous_id = report["ownedProcessId"]
        opened = session.reopen(project_path)
        active_change = None
        report["ownedProcessId"] = session.session['processId']
        require(report["ownedProcessId"] != previous_id, "Reopen requires a fresh worker-owned TIA instance")
        require(pathlib.Path(opened["project"]["path"]).resolve() == pathlib.Path(project_path).resolve(), "Reopened project differs")
        return {"project": opened["project"], "processId": report["ownedProcessId"], "previousProcessId": previous_id}

    def read_blocks(paths):
        result = {}
        for path in paths:
            native = call("block_read", {"blockPath": path})
            require(hashlib.sha256(pathlib.Path(native["artifact"]["path"]).read_bytes()).hexdigest() == native["artifact"]["sha256"], 'Safety evidence requirement failed: hashlib.sha256(pathlib.Path(native["artifact"]["path"]).read_bytes()).hexdigest() == native["artifact"]["sha256"]')
            result[path] = native
        return result

    def record_blocks(snapshot):
        return {path: {"nativePayloadHash": native_payload_hash(native["xml"]),
                       "completeNativePayloadHash": complete_native_payload_hash(native["xml"]), "artifact": native["artifact"]}
                for path, native in snapshot.items()}

    try:
        start = session.start()
        report["ownedProcessId"] = start["processId"]
        if args.project:
            safety_scratch_admission.recheck(admission, args.artifacts, ROOT, before_open=True)
            scratch_path = pathlib.Path(admission['closedScratch']['projectPath'])
            session.known_paths.add(str(scratch_path))
            scratch = call("project_open", {"path": str(scratch_path)})
            require(scratch.get('project', {}).get('path') == str(scratch_path)
                    and type(scratch.get('processId')) is int and scratch['processId'] == start['processId'],
                    'Project open returned another project or process')
        else:
            scratch = call("tia_create_scratch", {"cpuTypeIdentifier": safety_scratch_admission.CPU})
            require(scratch.get("completed") is True, "Safety scratch creation did not complete")
            require_clean_compile(scratch.get("compile"))
        inventory = call("project_inventory", {"maxItems": 2000})
        require(len(inventory["plcs"]) == 1, "Cannot establish unique newly created F-CPU")
        plc = inventory["plcs"][0]
        project_path = inventory["project"]["path"]
        require(project_path == scratch.get('project', {}).get('path')
                and type(inventory.get('processId')) is int and inventory['processId'] == start['processId']
                and type(scratch.get('processId')) is int and scratch['processId'] == start['processId'],
                'Inventory returned another admitted project or process')
        require(not report_path.absolute().is_relative_to(pathlib.Path(project_path).parent),
                'Safety reports must remain outside the mutable project tree')
        report["projectPath"] = project_path
        selector = {"plcPath": plc["path"]}
        if not admission:
            require_clean_compile(scratch.get('compile'), expected_targets=[plc['path']])
        if args.roundtrip:
            baseline_prepared = call("change_prepare", {**selector, "kind": "plc_compile", "payload": {}})
            active_change = baseline_prepared
            apply_started = True
            baseline_applied = call("change_apply", {"changeId": baseline_prepared["changeId"], "previewHash": baseline_prepared["previewHash"],
                "idempotencyKey": "f-baseline-compile-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
            require_compile_only(baseline_prepared, baseline_applied, plc['path'])
            report["baselineCompilePolicy"] = BASELINE_POLICY
            report["baselineCompile"] = {"prepared": baseline_prepared, "applied": baseline_applied}
            active_change = None
            apply_started = False
        safety = call("safety_inspect", {**selector, "maxItems": 1000})
        require("runtimeGroups" in safety and "programSignatures" in safety, 'Safety evidence requirement failed: "runtimeGroups" in safety and "programSignatures" in safety')
        software = call("software_inspect", {**selector, "maxItems": 2000})
        require(software.get("completeness", {}).get("scopeComplete") is True and not inventory.get("truncated"), "Complete fixture inventory is required")
        blocks = software.get("blocks", [])
        fblocks = [b for b in blocks if b.get("programmingLanguage", "").startswith("F_")]
        report["fBlocks"] = fblocks
        user_names = {g.get("mainSafetyBlockName") for g in safety["runtimeGroups"]}
        for block in fblocks:
            if block.get("name") not in user_names:
                continue
            before = call("safety_inspect", {**selector, "blockPath": block["path"], "maxItems": 1000})
            exported = call("block_read", {**selector, "blockPath": block["path"]}, allow_error=not args.roundtrip)
            if args.roundtrip:
                require_signature_evidence(before)
                preservation_paths = [b["path"] for b in blocks if not b.get("isKnowHowProtected")
                                      and b.get("isConsistent") and "/blocks/systemGroups/" not in b["path"]]
                originals = read_blocks(preservation_paths)
                require(block["path"] in originals, 'Safety evidence requirement failed: block["path"] in originals')
                report["nativePreservation"] = {"comparison": "Full native XML except top-level DocumentInfo export provenance",
                                                "nativeComparison": "Includes comments, processing instructions, IDs, QName bindings and xml:space",
                                                "before": record_blocks(originals)}
                original_path = exported["artifact"]["path"]
                original_hash = hashlib.sha256(pathlib.Path(original_path).read_bytes()).hexdigest()
                require(original_hash == exported["artifact"]["sha256"], 'Safety evidence requirement failed: original_hash == exported["artifact"]["sha256"]')
                wrong_source = 'FUNCTION_BLOCK "' + block["name"] + '"\nVERSION : 0.1\nBEGIN\nEND_FUNCTION_BLOCK\n'
                rejected = call("change_prepare", {**selector, "kind": "source_upsert", "payload": {
                    "language": "SCL", "source": wrong_source, "expectedBlockNames": [block["name"]]}}, allow_error=True)
                require(rejected.get("error", {}).get("code") == "SafetyBoundary", rejected)
                unchanged = call("block_read", {**selector, "blockPath": block["path"]})
                require(native_payload_hash(unchanged["xml"]) == native_payload_hash(exported["xml"]), "Rejected standard prepare changed F block")
                requested_xml = edit_user_comment(exported["xml"]) if args.edit_comment else exported["xml"]
                prepared = call("change_prepare", {**selector, "kind": "safety_import_xml", "payload": {
                    "blockPath": block["path"], "xml": requested_xml, "expectedBlockNames": [block["name"]], "safetyAcknowledged": True}})
                active_change = prepared
                apply_started = True
                applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                    "idempotencyKey": "f-roundtrip-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
                require(applied.get("state") == "applied", "Safety import was not applied")
                require_clean_compile(applied.get("compile"))  # The import compiles every device; the receipt declares its scope.
                after = call("safety_inspect", {**selector, "blockPath": block["path"], "maxItems": 1000})
                require_signature_evidence(after)
                coverage = require_applied_evidence(applied, after, block["path"])
                require(applied["preview"] == prepared["preview"] and applied["previewHash"] == prepared["previewHash"], 'Safety evidence requirement failed: applied["preview"] == prepared["preview"] and applied["previewHash"] == prepared["previewHash"]')
                journal = json.loads(pathlib.Path(applied["journalPath"]).read_text(encoding="utf-8-sig"))
                require(journal["state"] == "applied" and journal["afterFingerprint"] == applied["afterFingerprint"], 'Safety evidence requirement failed: journal["state"] == "applied" and journal["afterFingerprint"] == applied["afterFingerprint"]')
                require(journal["fingerprintCoverage"] == coverage and journal["safetyAfter"] == applied["safetyAfter"], 'Safety evidence requirement failed: journal["fingerprintCoverage"] == coverage and journal["safetyAfter"] == applied["safetyAfter"]')
                report["fingerprintCorrection"] = {"status": "passed", "afterFingerprint": applied["afterFingerprint"],
                                                    "coverage": coverage, "journalPath": applied["journalPath"]}
                after_export = call("block_read", {**selector, "blockPath": block["path"]})
                expected_changed = {**originals, block["path"]: {"xml": requested_xml}}
                changed_blocks = read_blocks(preservation_paths)
                require_native_equal(expected_changed, changed_blocks)
                report["nativePreservation"]["after"] = record_blocks(changed_blocks)
                report["roundtrip"] = {"changeId": prepared["changeId"], "originalArtifact": exported["artifact"],
                    "afterArtifact": after_export["artifact"], "programSignaturesBefore": before["programSignatures"],
                    "nativePayloadHashBefore": native_payload_hash(exported["xml"]), "nativePayloadHashAfter": native_payload_hash(after_export["xml"]),
                    "programSignaturesAfter": after["programSignatures"], "blockSignaturesBefore": before["blockSignatures"],
                    "blockSignaturesAfter": after["blockSignatures"], "standardOverwriteRejected": True,
                    "requestedChange": "existing-comment" if args.edit_comment else "unchanged-roundtrip",
                    "nativeRequestedPayloadHash": native_payload_hash(requested_xml),
                    "programSignatureChanged": before["programSignatures"] != after["programSignatures"],
                    "blockSignatureChanged": before["blockSignatures"] != after["blockSignatures"]}
                require(hashlib.sha256(pathlib.Path(original_path).read_bytes()).hexdigest() == original_hash, "Original export was overwritten")
                require(native_payload_hash(requested_xml) == native_payload_hash(after_export["xml"]), "Safety import changed native XML beyond the requested edit")
                if not args.edit_comment:
                    require(before["programSignatures"] == after["programSignatures"], "Unchanged import altered safety signature")
                    require(before["blockSignatures"] == after["blockSignatures"], "Unchanged import altered block signature")
                if args.verify_recovery:
                    status = call("change_status", {"changeId": prepared["changeId"]})
                    require(status.get("recoveryArchive"), "No checkpoint archive available for safety recovery")
                    restore_attempted = True
                    restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                    require(restored["state"] == "restored" and restored.get("recoveredProject", {}).get("path"), restored)
                    recovered = call("safety_inspect", {**selector, "blockPath": block["path"], "maxItems": 1000})
                    recovered_export = call("block_read", {**selector, "blockPath": block["path"]})
                    require_safety_equal(before, recovered)
                    recovered_blocks = read_blocks(preservation_paths)
                    require_native_equal(originals, recovered_blocks)
                    require(native_payload_hash(exported["xml"]) == native_payload_hash(recovered_export["xml"]), "Recovery changed original F-block XML")
                    require(before["programSignatures"] == recovered["programSignatures"], "Recovery changed safety program signatures")
                    require(before["blockSignatures"] == recovered["blockSignatures"], "Recovery changed block signatures")
                    report["recovery"] = {"status": "passed", "recoveredProject": restored["recoveredProject"],
                                          "nativePayloadHash": native_payload_hash(recovered_export["xml"]),
                                          "programSignatures": recovered["programSignatures"], "blockSignatures": recovered["blockSignatures"],
                                          "blocks": record_blocks(recovered_blocks)}
                    recovered_path = restored["recoveredProject"]["path"]
                    require(pathlib.Path(recovered_path).resolve() != pathlib.Path(project_path).resolve(), "Recovery must use a new project copy")
                    recompilation = call("change_prepare", {**selector, "kind": "plc_compile", "payload": {}})
                    active_change = recompilation
                    restore_attempted = False
                    recompiled = call("change_apply", {"changeId": recompilation["changeId"], "previewHash": recompilation["previewHash"],
                        "idempotencyKey": "f-recovery-compile-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
                    require_compile_only(recompilation, recompiled, plc['path'])
                    recompiled_safety = call("safety_inspect", {**selector, "blockPath": block["path"], "maxItems": 1000})
                    require_applied_evidence(recompiled, recompiled_safety, block["path"])
                    require_safety_equal(before, recompiled_safety)
                    recompiled_blocks = read_blocks(preservation_paths)
                    require_native_equal(originals, recompiled_blocks)
                    report["recovery"]["compile"] = recompiled["compile"]
                    report["recovery"]["compileOnly"] = {"prepared": recompilation, "applied": recompiled}
                    report["recovery"]["reopen"] = reopen(recovered_path)
                    active_change = None
                    reopened_recovery = read_blocks(preservation_paths)
                    require_native_equal(originals, reopened_recovery)
                    reopened_safety = call("safety_inspect", {**selector, "blockPath": block["path"], "maxItems": 1000})
                    require_safety_equal(before, reopened_safety)
                    report["recovery"]["reopen"].update(status="passed", blocks=record_blocks(reopened_recovery),
                        programSignatures=reopened_safety["programSignatures"], blockSignatures=reopened_safety["blockSignatures"])
                # Reopen the exact changed copy even when recovery produced a new
                # project. Its native readback must still match the requested edit.
                report["saveReopen"] = reopen(project_path)
                active_change = None
                reopened_changed = read_blocks(preservation_paths)
                require_native_equal(expected_changed, reopened_changed)
                reopened_safety = call("safety_inspect", {**selector, "blockPath": block["path"], "maxItems": 1000})
                require_safety_equal(after, reopened_safety)
                report["saveReopen"].update(status="passed", blocks=record_blocks(reopened_changed),
                    programSignatures=reopened_safety["programSignatures"], blockSignatures=reopened_safety["blockSignatures"])
                for original in originals.values():
                    require(hashlib.sha256(pathlib.Path(original["artifact"]["path"]).read_bytes()).hexdigest() == original["artifact"]["sha256"], "Original native artifact was overwritten")
                break
        if args.roundtrip and "roundtrip" not in report:
            raise RuntimeError("No eligible native main F-block was exercised; requested safety qualification is blocked.")
        report["coverage"] = {"offlineSafetyRead": True, "runtimeGroupCount": len(safety["runtimeGroups"]),
            "programSignatureCount": len(safety["programSignatures"]), "fBlockCount": len(fblocks),
            "safetyImportTested": "roundtrip" in report, "physicalOperations": False,
            "scratchCompleted": scratch.get("completed", False),
            "safetyUnitRoutes": "unqualified: requires a dedicated native safety-unit fixture",
            "fIoConfiguration": "unqualified: requires selected installed F-I/O and dependency fixtures",
            "fLogicChanges": "unqualified: optional comment change does not qualify F-logic edits"}
        if report.get("roundtrip"):
            safety_lifecycle.sync_steps(report)
            require_baseline_compile(report, plc["path"])
        report["qualificationChecksPassed"] = True
    except BaseException as error:
        report["failure"] = str(error)
        if active_change and apply_started and not restore_attempted:
            recovery = {"status": "blocked", "primaryFailure": str(error)}
            report["failureRecovery"] = recovery
            try:
                status = call("change_status", {"changeId": active_change["changeId"]})
                recovery["changeStatus"] = status
                if status.get("recoveryArchive"):
                    restore_attempted = True
                    restored = call("change_restore", {"changeId": active_change["changeId"], "previewHash": active_change["previewHash"]})
                    recovery["result"] = restored
                    recovery["status"] = "restored-unverified" if restored.get("state") == "restored" else "blocked"
            except Exception as recovery_error:
                recovery["error"] = str(recovery_error)
            persist()
        raise
    finally:
        try:
            safety_lifecycle.finish(session, report, persist)
        finally:
            try:
                if admission:
                    safety_scratch_admission.recheck(admission, args.artifacts, ROOT)
                    report['scratchAdmission']['sourceReportsUnchangedAtClose'] = True
                if report.get('completed') is True:
                    report['closedScratch'] = safety_scratch_admission.closed_snapshot(report['projectPath'], args.artifacts, ROOT)
            except BaseException as error:
                report['completed'] = False
                report['scratchAdmissionFailure'] = str(error)
                raise
            finally:
                persist()
                print("Report: " + str(report_path), flush=True)


if __name__ == "__main__":
    main()
