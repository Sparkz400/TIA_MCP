"""Capture authored native routes in a fresh SHA-bound owned archive retrieval.

No typed builder, previous-project open, physical PLC or simulator is used.
Every source is admitted separately; failed/uncertain operations never replay.
The first blocked source stops capture and retains its tracked recovery result.
"""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import uuid

from block_test_runtime import utc_now
from block_test_stop import StopRequest
from qualification_owned_scratch import require, snapshot, assert_snapshot, complete_inventory
from run_block_tests import save_json
from run_conformance import safe_xml
from smoke_mcp import ROOT
from typed_graphical_owned_session import TypedOwnedSession
from compilation_evidence import require_clean_compile
from engineering_preservation import PAYLOAD_HASH_POLICY
from typed_graphical_native_expectations import adapt_reference_culture, project_culture


def authored_reference(path, unit):
    text = path.read_text(encoding="utf-8-sig")
    if path.suffix.lower() == ".xml":
        document = safe_xml(text)
        blocks = [node for node in document if node.tag in ("SW.Blocks.FC", "SW.Blocks.FB")]
        require(len(blocks) == 1, "One independently derived native caller is required.")
        names = [blocks[0].findtext("AttributeList/Name")]
        namespace = blocks[0].findtext("AttributeList/Namespace")
        kind, payload, form = "block_import_xml", {"xml": text}, "native_xml_template_derivation"
    else:
        require(path.suffix.lower() in {".scl", ".s7dcl"}, "Unsupported authored source format.")
        names = re.findall(r'\b(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\s+"([^\"]+)"', text)
        namespaces = re.findall(r'^NAMESPACE\s+([A-Za-z0-9_.]+)', text, re.MULTILINE)
        require(len(namespaces) == 1, "Exactly one explicit authored namespace is required.")
        namespace = namespaces[0]
        form = "SCL" if path.suffix.lower() == ".scl" else "SIMATIC_SD"
        kind, payload = "source_upsert", {"language": form, "source": text}
    require(names and all(names) and len(names) == len(set(names)), "Authored exact block names are missing or duplicated.")
    require(namespace == unit or isinstance(namespace, str) and namespace.startswith(unit + "."), "Source namespace escapes the selected software unit.")
    return {"sourcePath": str(path.resolve()), "sha256": hashlib.sha256(text.encode()).hexdigest(),
        "fileSha256": hashlib.sha256(path.read_bytes()).hexdigest(), "names": names, "namespace": namespace,
        "route": "softwareUnit" if namespace == unit else "namespace", "authoredFormat": form,
        "engineeringVerified": False, "runtimeVerified": False}, kind, payload


def verify_reference_additions(before, after, names, scope, namespace):
    old, new = before["objects"], after["objects"]
    require(set(old) <= set(new), "Reference capture deleted an existing native object.")
    require(all(old[key]["payloadSha256"] == new[key]["payloadSha256"] for key in old), "Reference capture changed existing native XML.")
    added = [new[key] for key in set(new) - set(old)]
    require(len(added) == len(names) and all(row["kind"] == "block" for row in added)
        and {row["name"] for row in added} == set(names), "Reference capture did not add exactly the declared blocks.")
    inventory = after["inventory"]["blocks"]
    for row in added:
        match = [block for block in inventory if block["path"] == row["path"]]
        require(len(match) == 1 and match[0]["scopePath"] == scope and match[0]["identity"]["namespace"] == namespace,
            "Reference block belongs to the wrong exact scope/namespace.")
    return sorted(added, key=lambda row: row["name"])


def capture(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    archive = args.archive.resolve()
    require(archive.is_relative_to((ROOT / "artifacts").resolve()) and archive.suffix.lower() == ".zap21",
            "Source archive must be under repository artifacts.")
    require(hashlib.sha256(archive.read_bytes()).hexdigest() == args.sha256, "Source archive hash differs.")
    specifications = [authored_reference(path, args.unit) for path in args.sources]
    require(len({row[0]["sourcePath"] for row in specifications}) == len(specifications), "Sources may be admitted only once.")
    report = {"schemaVersion": 1, "origin": "independently_authored_native_references", "builderUsed": False,
        "startedAtUtc": utc_now(), "completed": False, "runtimeVerified": False, "physicalPlcAccess": False,
        "calls": [], "fixtures": [], "sourceArchive": {"path": str(archive), "sha256": args.sha256},
        "remainingSources": [row[0]["sourcePath"] for row in specifications],
        "payloadHashPolicy": PAYLOAD_HASH_POLICY}
    harness_paths = [pathlib.Path(__file__).resolve(), pathlib.Path(__file__).with_name("typed_graphical_owned_session.py").resolve(),
        pathlib.Path(__file__).with_name("qualification_owned_scratch.py").resolve(),
        pathlib.Path(__file__).with_name("engineering_preservation.py").resolve(),
        pathlib.Path(__file__).with_name("typed_graphical_native_expectations.py").resolve()]
    report["harnessFiles"] = [{"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in harness_paths]
    stop = StopRequest(args.stop_file or output / "stop-request.json")
    def persist():
        report["calls"] = [call for lifetime in report.get("sessions", []) for call in lifetime["calls"]]
        report["stopRequest"] = stop.evidence()
        save_json(output / "capture.json", report)
    session = TypedOwnedSession(args.host, args.worker, output, report, persist, timeout=args.timeout,
        extra_tools={"hardware_inspect", "type_read", "tag_table_read", "artifact_read"})
    pending, baseline = None, None
    try:
        if stop():
            report["status"] = "stopped_by_request"
            return 2
        report["started"] = session.invoke("tia_start")
        report["retrieved"] = session.invoke("project_retrieve", {"archivePath": str(archive), "expectedSha256": args.sha256})
        report["currentProject"] = report["retrieved"]["project"]
        report["hardware"] = session.invoke("hardware_inspect", {"maxItems": 2000})
        report["inventory"] = complete_inventory(session.invoke)
        units = [row for row in report["inventory"]["softwareScopes"] if row["kind"] == "softwareUnit" and row["name"] == args.unit]
        require(len(units) == 1, "Exact software-unit selection is missing or ambiguous.")
        scope = units[0]["path"]
        # Native XML inputs were derived in a de-DE project; only empty text items follow the observed project culture.
        report["projectCulture"] = project_culture(session.invoke("block_read", {"blockName": "Main"})["xml"])
        for ordinal, (entry, kind, payload) in enumerate(specifications):
            if kind == "block_import_xml":
                xml, entry["cultureItemsRetargeted"] = adapt_reference_culture(payload["xml"], report["projectCulture"])
                payload = {**payload, "xml": xml}
            if stop():
                report["status"] = "stopped_by_request"
                break
            entry["scopePath"] = scope
            folder = output / ("source-" + str(ordinal).zfill(2))
            folder.mkdir()
            report["fixtures"].append(entry)
            baseline = snapshot(session.invoke, folder / "before")
            require(not any(block["scopePath"] == scope and block["identity"]["namespace"] == entry["namespace"]
                    and block["name"] in entry["names"] for block in baseline["inventory"]["blocks"]),
                    "New independent capture cannot overwrite an existing native reference.")
            prepared = session.invoke("change_prepare", {"kind": kind, "payload": {**payload,
                "scopePath": scope, "namespace": entry["namespace"], "expectedBlockNames": entry["names"]}})
            pending = prepared
            entry["changeId"] = prepared["changeId"]
            entry["applyIntent"] = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}
            persist()
            applied = session.invoke("change_apply", entry["applyIntent"])
            require_clean_compile(applied.get("compile"))
            require(applied.get("state") == "applied" and applied.get("compile", {}).get("errorCount") == 0
                and "changed_project_saved" in applied.get("stages", []), "Independent source import/compile/save failed.")
            after = snapshot(session.invoke, folder / "after")
            additions = verify_reference_additions(baseline, after, entry["names"], scope, entry["namespace"])
            entry["compile"], entry["exports"] = applied["compile"], []
            for row in additions:
                target = output / (row["name"] + ".xml")
                require(not target.exists(), "Native export destination already exists.")
                shutil.copyfile(row["file"], target)
                block = next(block for block in after["inventory"]["blocks"] if block["path"] == row["path"])
                entry["exports"].append({"name": row["name"], "path": str(target), "sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "block": block})
            entry["engineeringVerified"] = entry["unrelatedPreserved"] = True
            pending = None
            report["remainingSources"].remove(entry["sourcePath"])
            persist()
            print("CAPTURED " + pathlib.Path(entry["sourcePath"]).name, flush=True)
        else:
            expected = snapshot(session.invoke, output / "saved")
            session.reopen(session.current_project)
            assert_snapshot(expected, snapshot(session.invoke, output / "reopened"))
            report["persistenceVerified"] = report["completed"] = report["unrelatedPreserved"] = True
            report["status"] = "passed"
    except Exception as error:
        report.update(status="blocked", failure={"type": type(error).__name__, "message": str(error)})
        if pending is not None:
            try:
                state = session.invoke("change_status", {"changeId": pending["changeId"]})
                report["failureStatus"] = state
                if state.get("state") in {"applied", "recovery_required"} and state.get("recoveryArchive"):
                    report["restoreDispatched"] = True
                    persist()
                    restored = session.invoke("change_restore", {"changeId": pending["changeId"], "previewHash": pending["previewHash"]})
                    report["failureRecovery"] = restored
                    assert_snapshot(baseline, snapshot(session.invoke, output / "failure-recovered"))
                    session.reopen(session.current_project)
                    assert_snapshot(baseline, snapshot(session.invoke, output / "failure-recovered-reopened"))
                    report["failureRecoveryVerified"] = True
            except Exception as recovery_error:
                report["failureRecoveryError"] = str(recovery_error)
    finally:
        try:
            session.close()
        except Exception as error:
            report.update(status="blocked", completed=False, cleanupFailure=str(error))
        if not all(hashlib.sha256(pathlib.Path(row["path"]).read_bytes()).hexdigest() == row["sha256"] for row in report["harnessFiles"]):
            report.update(status="blocked", completed=False, harnessIntegrityFailure="Capture harness changed.")
        if not all(hashlib.sha256(pathlib.Path(entry["sourcePath"]).read_bytes()).hexdigest() == entry["fileSha256"] for entry, _, _ in specifications):
            report.update(status="blocked", completed=False, sourceIntegrityFailure="Authored source changed.")
        report["completedAtUtc"] = utc_now()
        persist()
    return 0 if report["completed"] else 2


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, fromfile_prefix_chars="@")
    parser.add_argument("sources", type=pathlib.Path, nargs="+")
    for name in ("host", "worker", "output", "archive"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--unit", default="TiaMcp_UnitA")
    parser.add_argument("--stop-file", type=pathlib.Path)
    parser.add_argument("--timeout", type=int, default=300)
    options = parser.parse_args()
    if not 5 <= options.timeout <= 930:
        parser.error("--timeout must be within 5..930 seconds.")
    raise SystemExit(capture(options))
