"""Actual MCP rejection cases in one fresh owned scratch; never downloads a PLC."""
import argparse
import copy
import json
import pathlib
import uuid
import hashlib

from block_test_runtime import NativeToolError, canonical, utc_now
from qualify_typed_graphical import Audit, snapshot, assert_snapshot
from run_block_tests import apply_change, binary_manifest
from smoke_mcp import Client, ROOT
from run_conformance import native_document


def run(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    artifacts = ROOT / "artifacts" / ("n" + uuid.uuid4().hex[:6])
    artifacts.mkdir()
    report = {"schemaVersion": 1, "startedAtUtc": utc_now(), "calls": [], "cases": [], "status": "running",
              "physicalPlcAccess": False, "runtimeVerified": False, "binaries": binary_manifest(args.host, args.worker, None)}
    def persist():
        (output / "result.json").write_text(canonical(report), encoding="utf-8")
    client = Client(args.host.resolve(), 300, ["--worker", str(args.worker.resolve()), "--artifacts", str(artifacts)])
    call = Audit(client, report["calls"], persist)
    mutation = None
    def negative(identifier, payload, expected):
        entry = {"id": identifier, "expectedCodes": expected, "status": "running"}
        report["cases"].append(entry)
        try:
            entry["unexpectedPrepared"] = call("change_prepare", {"kind": "graphical_typed_edit", "payload": payload})
            raise AssertionError("Invalid typed request unexpectedly prepared.")
        except NativeToolError as error:
            code = error.result.get("error", {}).get("code")
            entry["diagnostic"] = error.result
            assert code in expected, identifier + " returned unrelated diagnostic " + str(code)
        assert_snapshot(before, snapshot(call, None, output / identifier, {}))
        entry.update(status="passed", originalNativeXmlPreserved=True)
        persist()
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "typed-negative-qualification", "version": "1.0.0"}})
        client.notify("notifications/initialized")
        assert call("tia_start")["ownsPortalProcess"] is True
        if args.baseline_archive:
            archive = args.baseline_archive.resolve()
            assert archive.is_relative_to((ROOT / "artifacts").resolve()) and archive.suffix.lower() == ".zap21"
            assert hashlib.sha256(archive.read_bytes()).hexdigest() == args.baseline_sha256
            report["scratch"] = call("project_retrieve", {"archivePath": str(archive), "expectedSha256": args.baseline_sha256})
        else:
            report["scratch"] = call("tia_create_scratch", {"cpuTypeIdentifier": "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"})
            assert report["scratch"]["compile"]["errorCount"] == 0
        names = ["Tgp_TemplateFC_LAD", "Tgp_NumericFC", "Tgp_NumericFB", "Tgp_NumericFC_LAD", "Tgp_NumericFB_LAD"]
        for name in names:
            xml = (ROOT / "fixtures/typed-graphical/native" / (name + ".xml")).read_bytes().decode("utf-8-sig")
            apply_change(call, "block_import_xml", {"xml": xml, "expectedBlockNames": [name]})
        ambiguous = (ROOT / "fixtures/typed-graphical/sources/negative-ambiguous-members.scl").read_text(encoding="utf-8")
        apply_change(call, "source_upsert", {"language": "SCL", "source": ambiguous, "expectedBlockNames": ["Tgp_NegativeOne", "Tgp_NegativeTwo"]})
        before = snapshot(call, None, output / "before", {})
        def model_payload(input_name="InputBool", output_name="OutputBool"):
            target = before["Tgp_TemplateFC_LAD"]
            network_id = next(e.get("ID") for e in native_document(target["xml"]).iter() if e.tag == "SW.Blocks.CompileUnit")
            return {"blockPath": target["block"]["path"], "model": {"schemaVersion": 1, "language": "LAD", "operation": "replaceNetwork", "networkId": network_id,
                "nodes": [{"key": "rail", "kind": "powerrail"}, {"key": "input", "kind": "contact", "operand": "input"}, {"key": "output", "kind": "coil", "operand": "output"}],
                "operands": [{"key": "input", "scope": "local", "components": [input_name]}, {"key": "output", "scope": "local", "components": [output_name]}],
                "connections": [{"from": {"node": "rail", "port": "out"}, "to": {"node": "input", "port": "in"}},
                                {"from": {"node": "input", "port": "out"}, "to": {"node": "output", "port": "in"}}]}}
        authored = json.loads((ROOT / "fixtures/typed-graphical/numeric-cases.v1.json").read_bytes())["cases"]
        def numeric(kind):
            case = next(c for c in authored if c["family"] == "numeric-" + kind and c["language"] == "LAD")
            payload = copy.deepcopy(case["payload"])
            target = before[case["blockName"]]
            payload["blockPath"] = target["block"]["path"]
            payload["model"]["networkId"] = next(e.get("ID") for e in native_document(target["xml"]).iter() if e.tag == "SW.Blocks.CompileUnit")
            for node in payload["model"]["nodes"]:
                if "calleePath" in node:
                    node["calleePath"] = before[node["calleePath"][7:]]["block"]["path"]
            return payload
        negative("wrong-type", model_payload("InputInt"), ["OperandTypeMismatch"])
        negative("read-only-output", model_payload(output_name="InputBool"), ["OperandNotWritable"])
        missing = numeric("fc")
        missing["model"]["connections"] = [edge for edge in missing["model"]["connections"] if edge["to"]["port"] != "Value"]
        negative("missing-argument", missing, ["GraphicalMissingArgument"])
        wrong = numeric("fb")
        wrong["instances"] = [{"key": "child", "kind": "existingGlobal", "blockPath": before["TiaMcp_EnableGate_DB"]["block"]["path"]}]
        negative("wrong-fb-instance", wrong, ["InstanceTypeMismatch"])
        ambiguous = model_payload()
        ambiguous["model"]["operands"][0] = {"key": "input", "scope": "db", "components": ["Flag"]}
        negative("ambiguous-db-owner", ambiguous, ["InvalidTypedGraphicalPayload"])
        language = model_payload()
        language["model"]["language"] = "FBD"
        language["model"]["nodes"] = [{"key": "output", "kind": "assign", "operand": "output"}]
        language["model"]["connections"] = [{"from": {"node": "$input", "port": "value"}, "to": {"node": "output", "port": "in"}}]
        negative("language-mismatch", language, ["LanguageConversionNotSupported"])
        shared = numeric("fb")
        duplicate = copy.deepcopy(next(node for node in shared["model"]["nodes"] if node["kind"] == "call"))
        duplicate["key"] = "second"
        shared["model"]["nodes"].append(duplicate)
        negative("implicit-shared-instance", shared, ["SharedInstanceRejected"])
        if args.include_planned_collision:
            collision = numeric("fb")
            collision.pop("blockPath")
            collision["model"].pop("networkId")
            collision["model"]["operation"] = "createBlock"
            collision["model"]["creation"] = {
                "name": "Tgp_NegativeCollision", "blockType": "FB", "interface": [
                    {"name": "Enable", "section": "Input", "dataType": "Bool"},
                    {"name": "Delta", "section": "Input", "dataType": "Int"},
                    {"name": "Reset", "section": "Input", "dataType": "Bool"},
                    {"name": "Accumulator", "section": "InOut", "dataType": "Int"},
                    {"name": "Total", "section": "Output", "dataType": "Int"}]}
            collision["instances"] = [{"key": "child", "kind": "createGlobal", "name": "Tgp_NegativeCollision"}]
            negative("planned-caller-instance-collision", collision, ["DeclarationCollision"])
        stale = call("change_prepare", {"kind": "graphical_typed_edit", "payload": numeric("fc")})
        source = (ROOT / "fixtures/typed-graphical/sources/numeric-callees.scl").read_text(encoding="utf-8")
        assert source.count("#Accumulator * 2") == 1
        mutation = call("change_prepare", {"kind": "source_upsert", "payload": {"language": "SCL", "source": source.replace("#Accumulator * 2", "#Accumulator * 3"), "expectedBlockNames": ["Tgp_NumericFC", "Tgp_NumericFB"]}})
        report["deliberatePrepared"] = mutation
        changed = call("change_apply", {"changeId": mutation["changeId"], "previewHash": mutation["previewHash"], "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
        assert changed["state"] == "applied"
        changed_snapshot = snapshot(call, None, output / "deliberate-callee-change", {})
        try:
            call("change_apply", {"changeId": stale["changeId"], "previewHash": stale["previewHash"], "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
            raise AssertionError("Stale declaration was applied.")
        except NativeToolError as error:
            assert error.result.get("error", {}).get("code") in ("StaleRevision", "StaleDeclaration")
            report["cases"].append({"id": "stale-declaration", "status": "passed", "diagnostic": error.result})
        assert_snapshot(changed_snapshot, snapshot(call, None, output / "stale-rejected", {}))
        report["restored"] = call("change_restore", {"changeId": mutation["changeId"], "previewHash": mutation["previewHash"]})
        assert report["restored"]["state"] == "restored"
        assert_snapshot(before, snapshot(call, None, output / "restored", {}))
        report.update(status="passed", rejectionPreservesNativeXml=True, deliberateMutationRecovered=True)
    except Exception as error:
        report.update(status="blocked", failure={"type": type(error).__name__, "message": str(error)})
        if mutation is not None and "restored" not in report:
            try:
                status = call("change_status", {"changeId": mutation["changeId"]})
                report["deliberateFailureStatus"] = status
                if status.get("state") in ("applied", "recovery_required") and status.get("recoveryArchive"):
                    restored = call("change_restore", {"changeId": mutation["changeId"], "previewHash": mutation["previewHash"]})
                    report["deliberateFailureRecovery"] = restored
                    assert restored["state"] == "restored"
                    assert_snapshot(before, snapshot(call, None, output / "failure-restored", {}))
                    report["failureRecoveryOriginalVerified"] = True
            except Exception as recovery_error:
                report["failureRecoveryError"] = {"type": type(recovery_error).__name__, "message": str(recovery_error)}
    finally:
        try:
            report["detach"] = call("tia_detach")
        except Exception as error:
            report["cleanupFailure"] = str(error)
        client.close()
        report["completedAtUtc"] = utc_now()
        persist()
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--baseline-archive", type=pathlib.Path)
    parser.add_argument("--baseline-sha256")
    parser.add_argument("--include-planned-collision", action="store_true", help="Also exercise the new-block/global-instance name reservation guard.")
    args = parser.parse_args()
    if bool(args.baseline_archive) != bool(args.baseline_sha256):
        parser.error("--baseline-archive and --baseline-sha256 are required together")
    raise SystemExit(run(args))
