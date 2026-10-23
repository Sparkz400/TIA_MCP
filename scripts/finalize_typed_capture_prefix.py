"""Read a byte-verified copy of a saved capture in one fresh owned TIA lifetime.

Never reopens the historical project, admits another source, compiles, saves,
restores a checkpoint or activates profiles. Original stopped evidence is immutable.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import re
import shutil
from xml.dom import Node, minidom

from block_test_runtime import utc_now
from qualification_owned_scratch import require, snapshot
from run_block_tests import save_json
from smoke_mcp import ROOT
from typed_graphical_owned_session import TypedOwnedSession
from typed_graphical_runtime_preservation import file_inventory, inventory_difference


def hash_file(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def bound_json(path, digest):
    raw = pathlib.Path(path).read_bytes()
    require(hashlib.sha256(raw).hexdigest() == digest, "Pinned input hash differs: " + str(path))
    return json.loads(raw)


def strict_payload(raw):
    text = raw.decode("utf-8-sig")
    require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(), "DTD/entity declarations are forbidden.")
    document = minidom.parseString(text)
    require(document.documentElement.tagName == "Document", "Native Document root is required.")
    infos = [node for node in document.documentElement.childNodes
        if node.nodeType == Node.ELEMENT_NODE and node.nodeName == "DocumentInfo" and node.namespaceURI is None]
    created_text = None
    if len(infos) == 1:
        created = [node for node in infos[0].childNodes
            if node.nodeType == Node.ELEMENT_NODE and node.nodeName == "Created" and node.namespaceURI is None]
        if (len(created) == 1 and not created[0].attributes.length and len(created[0].childNodes) == 1
                and created[0].firstChild.nodeType == Node.TEXT_NODE
                and re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z", created[0].firstChild.data)):
            created_text = created[0].firstChild
    def visit(node):
        if node.nodeType == Node.DOCUMENT_NODE:
            return ("document", tuple(visit(child) for child in node.childNodes))
        if node.nodeType == Node.ELEMENT_NODE:
            attributes = tuple(sorted((a.name, a.namespaceURI, a.value) for a in node.attributes.values()))
            children = tuple(visit(child) for child in node.childNodes)
            return ("element", node.nodeName, node.namespaceURI, attributes, children)
        if node.nodeType in (Node.TEXT_NODE, Node.CDATA_SECTION_NODE, Node.COMMENT_NODE):
            return (node.nodeType, "<native-export-created-timestamp>" if node is created_text else node.data)
        if node.nodeType == Node.PROCESSING_INSTRUCTION_NODE:
            return ("PI", node.target, node.data)
        raise RuntimeError("Unsupported native XML node: " + str(node.nodeType))
    return visit(document)


def checked_snapshot(manifest, root):
    data = json.loads(pathlib.Path(manifest).read_bytes())
    coverage = data["inventory"]["completeness"]
    require(coverage.get("scopeComplete") is True and coverage.get("truncated") is False, "Incomplete snapshot inventory.")
    identities = {}
    for collection, kind in (("blocks", "block"), ("types", "type"), ("tagTables", "tagTable")):
        for row in data["inventory"][collection]:
            key = kind + ":" + row["path"]
            require(key not in identities, "Duplicate exact native identity.")
            identities[key] = row
    require(set(identities) == set(data["objects"]), "Snapshot exports differ from its inventory.")
    payloads = {}
    for key, row in data["objects"].items():
        file = pathlib.Path(row["file"]).resolve()
        require(file.is_relative_to(pathlib.Path(root).resolve()), "Snapshot file escapes its evidence root.")
        raw = file.read_bytes()
        require(hashlib.sha256(raw).hexdigest() == row["sha256"] and len(raw) == row["bytes"], "Snapshot export changed.")
        require(row["name"] == identities[key]["name"] and row["path"] == identities[key]["path"], "Export identity differs.")
        payloads[key] = strict_payload(raw)
    return data, payloads


def cleanup_failure_admission(args, capture, audit, report_path):
    """Explicit saved-only admission; never relabel the original failed cleanup."""
    supplement_path = args.cleanup_failure_supplement.resolve()
    require(supplement_path.is_relative_to((ROOT / "artifacts").resolve()), "Cleanup supplement must be retained under artifacts.")
    supplement = bound_json(supplement_path, args.supplement_sha256)
    require(capture.get("status") == "blocked" and capture.get("completed") is False and not capture.get("persistenceVerified")
        and capture.get("failure") == {"type": "RuntimeError", "message": "Owned qualification cleanup failed: host_close"},
        "Only the explicit host-close failure boundary is eligible.")
    require(not any(capture.get(key) for key in ("cleanupFailure", "harnessIntegrityFailure", "sourceIntegrityFailure", "remainingSources")),
        "Other failures, uncertain sources or incomplete inputs are not eligible.")
    require(audit.get("verified", {}).get("finalSavedSnapshot") is True
        and audit["verified"].get("exactOwnedCleanup") is False and audit["verified"].get("freshProcessPersistence") is False,
        "Saved-only audit must retain failed cleanup and unverified persistence.")
    require(supplement.get("report", {}).get("sha256") == args.capture_sha256
        and pathlib.Path(supplement["report"]["path"]).resolve() == report_path
        and supplement.get("nativeProcessesRemainObserved") is False
        and supplement.get("cleanCleanupAccepted") is False and supplement.get("freshPersistenceEstablished") is False,
        "Cleanup supplement does not bind the retained failure/exit observation.")
    require(len(capture["sessions"]) == 1, "Failed-cleanup admission requires one original owned lifetime.")
    session = capture["sessions"][0]
    cleanup = session["cleanup"]
    require(cleanup.get("completed") is False and cleanup.get("errors") == [{"action": "host_close", "message": "Host shutdown was not clean."}]
        and cleanup.get("binaryHashesVerified") is True, "Original cleanup failure differs.")
    require(supplement.get("cleanup") == {key: value for key, value in cleanup.items() if key != "detach"},
        "Supplement replaced original cleanup facts.")
    shutdown = cleanup["launcherShutdown"]
    require(shutdown.get("hostExitCode") == 1 and shutdown.get("forcedHostStop") is False,
        "Original host did not reach the admitted non-forced failure boundary.")
    workers = shutdown.get("workerExits", [])
    require(len(workers) == 1 and type(workers[0].get("processId")) is int
        and workers[0].get("exitCode") is None and workers[0].get("processExitVerified") is False
        and workers[0].get("forcedTermination") is False, "Original worker receipt differs.")
    observation_ref = supplement["processObservation"]
    observation = bound_json(observation_ref["path"], observation_ref["sha256"])
    owned_ids = {session["processId"], workers[0]["processId"]}
    selected_paths = {str(pathlib.Path(capture["binaries"][key]["path"]).resolve()).casefold() for key in ("host", "worker")}
    require(set(observation.get("originalOwnedProcessIds", [])) == owned_ids
        and observation.get("originalOwnedIdsStillPresent") == [] and observation.get("selectedBundleProcessesStillPresent") == []
        and observation.get("terminationAttempted") is False and isinstance(observation.get("inventory"), list),
        "Post-failure process observation is incomplete or includes live owned resources.")
    require(not any(row.get("ProcessId") in owned_ids or (row.get("ExecutablePath")
        and str(pathlib.Path(row["ExecutablePath"]).resolve()).casefold() in selected_paths)
        for row in observation["inventory"]), "Process inventory contradicts claimed absence.")
    observed_at = datetime.datetime.fromisoformat(observation["observedAtUtc"])
    completed_at = datetime.datetime.fromisoformat(capture["completedAtUtc"])
    require(observed_at.utcoffset() is not None and completed_at.utcoffset() is not None and observed_at >= completed_at,
        "Process absence predates the original terminal outcome.")


def preflight(args):
    report_path = args.capture_report.resolve()
    require(report_path.is_relative_to((ROOT / "artifacts").resolve()), "Capture report must be retained under artifacts.")
    capture = bound_json(report_path, args.capture_sha256)
    audit = bound_json(args.prefix_audit, args.audit_sha256)
    failed_cleanup = getattr(args, "cleanup_failure_supplement", None)
    require(bool(failed_cleanup) == bool(getattr(args, "supplement_sha256", None)), "Cleanup supplement path and SHA256 must be supplied together.")
    if failed_cleanup:
        cleanup_failure_admission(args, capture, audit, report_path)
    else:
        require(capture.get("status") == "stopped_by_request" and capture.get("completed") is False
            and not capture.get("persistenceVerified"), "Only a stopped, unfinished capture prefix is eligible.")
        require(not any(capture.get(key) for key in ("failure", "cleanupFailure", "harnessIntegrityFailure", "sourceIntegrityFailure")),
            "Failed or uncertain captures are not eligible.")
    require(capture.get("builderUsed") is False and capture.get("runtimeVerified") is False
        and capture.get("physicalPlcAccess") is False, "Unexpected capture scope.")
    require(audit.get("passed") is True and audit["sourceReport"]["sha256"] == args.capture_sha256
        and pathlib.Path(audit["sourceReport"]["path"]).resolve() == report_path, "Independent prefix audit does not bind this capture.")
    required_gates = ("nativeImportCompileSave", "exactAddedBlocks", "allPeerXml") + (() if failed_cleanup else ("exactOwnedCleanup",))
    require(all(audit["verified"].get(key) is True for key in required_gates), "Prefix audit is incomplete.")
    fixtures = capture["fixtures"]
    require(fixtures and len(fixtures) == audit["inputs"] and len(capture["remainingSources"]) == audit["remainingInputs"], "Prefix boundary differs.")
    require(all(row.get("engineeringVerified") is True and row.get("unrelatedPreserved") is True for row in fixtures), "Incomplete prefix entry.")
    for session in capture["sessions"]:
        cleanup = session["cleanup"]
        if not failed_cleanup:
            require(cleanup.get("completed") is True and cleanup.get("errors") == [] and cleanup.get("binaryHashesVerified") is True,
                "Original owned lifetime cleanup is incomplete.")
        receipt = cleanup["tiaProcessExit"]
        require(receipt.get("processId") == session["processId"] and all(receipt.get(k) is True for k in
            ("completed", "processExitVerified", "handleClosed")), "Original exact process exit is unverified.")
    project = pathlib.Path(capture["retrieved"]["project"]["path"]).absolute()
    require(project.suffix.lower() == ".ap21" and project.is_file() and project.resolve().is_relative_to(report_path.parent / "recovery"),
        "Historical project is outside the recorded owned retrieval.")
    require(str(project.resolve()) == str(pathlib.Path(audit["ownedProject"]).resolve()), "Audit owned project differs.")
    expected_manifest = (report_path.parent / "saved" / "manifest.json") if failed_cleanup else (
        report_path.parent / ("source-" + str(len(fixtures) - 1).zfill(2)) / "after" / "manifest.json")
    require(pathlib.Path(audit["expectedSnapshot"]["path"]).resolve() == expected_manifest.resolve()
        and hash_file(expected_manifest) == audit["expectedSnapshot"]["sha256"], "Last accepted snapshot differs.")
    expected, payloads = checked_snapshot(expected_manifest, expected_manifest.parent)
    require(pathlib.Path(expected["inventory"]["project"]["path"]).resolve() == project.resolve(), "Expected snapshot belongs to another project.")
    return capture, audit, project, expected_manifest, payloads


class ReadOnlyPrefixSession(TypedOwnedSession):
    READ_TOOLS = frozenset({"tia_start", "project_open", "software_inspect", "block_read", "type_read", "tag_table_read", "artifact_read"})

    def invoke(self, name, arguments=None):
        require(name in self.READ_TOOLS, "Prefix finalization admits only owned open and read calls.")
        return super().invoke(name, arguments)


def finalize(args):
    capture, audit, source_project, expected_manifest, expected_payloads = preflight(args)
    output = args.output.resolve()
    require(output.is_relative_to((ROOT / "artifacts").resolve())
        and not output.is_relative_to(args.capture_report.resolve().parent), "New evidence must be outside the historical capture.")
    output.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "startedAtUtc": utc_now(), "completed": False, "status": "started",
        "sourceReport": {"path": str(args.capture_report.resolve()), "sha256": args.capture_sha256},
        "prefixAudit": {"path": str(args.prefix_audit.resolve()), "sha256": args.audit_sha256},
        "expectedSnapshot": audit["expectedSnapshot"], "prefixInputs": audit["inputs"], "remainingInputs": audit["remainingInputs"],
        "originalOutcome": capture["status"], "builderUsed": False, "runtimeVerified": False, "physicalPlcAccess": False,
        "profileActivationPerformed": False, "persistenceVerified": False, "calls": [],
        "scope": "Fresh-process readback of a byte-verified copy of a saved capture; no mutation, compile, save or continuation."}
    if getattr(args, "cleanup_failure_supplement", None):
        report["cleanupFailureSupplement"] = {"path": str(args.cleanup_failure_supplement.resolve()), "sha256": args.supplement_sha256}
        report["originalFailure"] = capture["failure"]
        report["originalCleanCleanupAccepted"] = False
    harness = [pathlib.Path(__file__), pathlib.Path(__file__).with_name("typed_graphical_owned_session.py"),
        pathlib.Path(__file__).with_name("qualification_owned_scratch.py"), pathlib.Path(__file__).with_name("typed_graphical_runtime_preservation.py")]
    report["harnessFiles"] = [{"path": str(p.resolve()), "sha256": hash_file(p)} for p in harness]
    def persist():
        report["calls"] = [call for lifetime in report.get("sessions", []) for call in lifetime["calls"]]
        save_json(output / "finalization.json", report)
    persist()
    session = None
    original_before = None
    try:
        original_before = file_inventory(source_project.parent)
        report["originalFilesBefore"] = original_before
        require(original_before["complete"], "Historical closed project inventory is incomplete.")
        clone_folder = output / "project" / source_project.parent.name
        shutil.copytree(source_project.parent, clone_folder, symlinks=True)
        copied = file_inventory(clone_folder)
        report["copiedFilesBeforeOpen"] = copied
        report["copyVerification"] = inventory_difference(original_before, copied)
        require(report["copyVerification"]["preserved"], "Copied project/companion bytes differ.")
        after_copy = file_inventory(source_project.parent)
        report["originalFilesAfterCopy"] = after_copy
        require(inventory_difference(original_before, after_copy)["preserved"], "Historical project changed while copying.")
        cloned_project = clone_folder / source_project.name
        report["copiedProject"] = str(cloned_project)
        persist()
        session = ReadOnlyPrefixSession(args.host, args.worker, output, report, persist, timeout=args.timeout,
            extra_tools={"type_read", "tag_table_read", "artifact_read"})
        # The only new eligible path was just created and completely byte-verified by this run.
        session.known_paths.add(str(cloned_project.resolve()))
        report["started"] = session.invoke("tia_start")
        require(report["started"]["processId"] not in {s["processId"] for s in capture["sessions"]}, "Fresh readback reused a historical process identity.")
        report["opened"] = session.invoke("project_open", {"path": str(cloned_project.resolve())})
        require(report["opened"].get("opened") is True
            and pathlib.Path(report["opened"]["project"]["path"]).resolve() == cloned_project.resolve(), "Fresh open receipt differs.")
        snapshot(session.invoke, output / "reopened")
        actual, actual_payloads = checked_snapshot(output / "reopened" / "manifest.json", output / "reopened")
        require(pathlib.Path(actual["inventory"]["project"]["path"]).resolve() == cloned_project.resolve()
            and actual["inventory"]["processId"] == report["started"]["processId"], "Readback belongs to another project/process.")
        require(set(expected_payloads) == set(actual_payloads), "Fresh copied-project inventory differs from accepted prefix.")
        changed = [key for key in expected_payloads if expected_payloads[key] != actual_payloads[key]]
        report["strictComparison"] = {"objects": len(expected_payloads), "changed": changed,
            "policy": "Normalize only simple direct DocumentInfo/Created timestamp text; retain all other provenance, namespaces, whitespace, comments, PI, CDATA, IDs and order."}
        require(not changed, "Fresh copied-project native XML differs: " + ", ".join(changed))
        report["persistenceVerified"] = True
    except Exception as error:
        report.update(status="blocked", failure={"type": type(error).__name__, "message": str(error)})
    finally:
        if session is not None:
            try:
                session.close()
            except Exception as error:
                report.update(status="blocked", cleanupFailure=str(error))
        try:
            report["originalFilesAfter"] = file_inventory(source_project.parent)
            report["originalPreservation"] = inventory_difference(original_before, report["originalFilesAfter"]) if original_before else {"preserved": False}
            require(report["originalPreservation"]["preserved"], "Historical project files changed or cannot be verified.")
            require(hash_file(args.capture_report) == args.capture_sha256 and hash_file(args.prefix_audit) == args.audit_sha256,
                "Historical report or independent audit changed.")
            if getattr(args, "cleanup_failure_supplement", None):
                require(hash_file(args.cleanup_failure_supplement) == args.supplement_sha256, "Original cleanup supplement changed.")
            require(hash_file(expected_manifest) == audit["expectedSnapshot"]["sha256"], "Expected snapshot manifest changed.")
            require(all(hash_file(row["path"]) == row["sha256"] for row in report["harnessFiles"]), "Finalizer source changed during execution.")
        except Exception as error:
            report.update(status="blocked", integrityFailure=str(error))
        if report["status"] != "blocked" and report["persistenceVerified"]:
            report.update(status="passed", completed=True)
        report["completedAtUtc"] = utc_now()
        persist()
    return 0 if report["completed"] else 2


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("capture-report", "prefix-audit", "host", "worker", "output"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    for name in ("capture-sha256", "audit-sha256"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--cleanup-failure-supplement", type=pathlib.Path,
        help="Explicit saved-only copy admission after a separately audited host-close failure; never accepts the original cleanup.")
    parser.add_argument("--supplement-sha256")
    args = parser.parse_args()
    if not 5 <= args.timeout <= 930:
        parser.error("--timeout must be within 5..930 seconds.")
    raise SystemExit(finalize(args))
