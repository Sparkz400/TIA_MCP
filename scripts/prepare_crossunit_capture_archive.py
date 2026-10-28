"""Prepare the crossUnit capture archive of the cross-scope call packet. Owned TIA only.

Retrieves the packet's SHA-bound baseline archive into a new owned project and applies exactly the
packet's prerequisites (compile=true, save=true, each read back from a complete native snapshot):
the TiaMcp_UnitA callees, the SoftwareUnit relation TiaMcp_UnitB -> TiaMcp_UnitA and publication of
TgpUnit_NumericFC. The baseline already holds TiaMcp_UnitB, so no unit is created. A compile-only
checkpoint archives the saved result; it is copied under repository artifacts/ with a JSON receipt
and the crossUnit capture command is printed. The baseline archive itself is never written.
"""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import uuid

from block_test_runtime import utc_now
from capture_typed_routes_owned import authored_reference, verify_reference_additions
from compilation_evidence import require_clean_compile, require_compile_only
from qualification_owned_scratch import require, snapshot, assert_snapshot, complete_inventory
from run_block_tests import save_json
from run_conformance import safe_xml
from smoke_mcp import ROOT
from typed_graphical_owned_session import TypedOwnedSession

PACKET = ROOT / "fixtures/typed-graphical/route-capture-crossscope-call-packet.v1.json"
CALLEES = ROOT / "fixtures/typed-graphical/sources/route-Unit-advanced-callees.scl"
CALLEE_UNIT, CALLER_UNIT, PUBLISHED = "TiaMcp_UnitA", "TiaMcp_UnitB", "TgpUnit_NumericFC"


def sha256(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def block_access(xml):
    """Native unit publication state (AttributeList/Access) of one exported FC or FB."""
    blocks = [node for node in safe_xml(xml) if node.tag in ("SW.Blocks.FC", "SW.Blocks.FB")]
    require(len(blocks) == 1, "One exported FC or FB is required.")
    return blocks[0].findtext("AttributeList/Access")


def unit_relations(inventory):
    return {row["name"]: row["relations"] for row in inventory["softwareScopes"] if row["kind"] == "softwareUnit"}


def capture_command(capture, archive, digest, host, worker, output):
    """The packet's capture command bound to one archive; an @args file becomes the packet's own sources."""
    values = {"--archive": str(archive), "--sha256": digest, "--host": str(host), "--worker": str(worker), "--output": str(output)}
    command = []
    for token in capture["command"]:
        if command and command[-1] in values:
            token = values[command[-1]]
        elif token.startswith("@"):
            command += [entry["path"] for entry in capture["sources"]]
            continue
        command.append(token)
    require(all(name in command for name in values) and not any("<" in token for token in command), "Capture command keeps a placeholder.")
    return command


def prepare(args):
    packet = json.loads(PACKET.read_bytes())
    [capture] = [row for row in packet["captures"] if row["id"] == "crossUnit"]
    baseline_archive, baseline_sha = (ROOT / packet["archive"]["path"]).resolve(), packet["archive"]["sha256"]
    require(baseline_archive.is_relative_to((ROOT / "artifacts").resolve()) and sha256(baseline_archive) == baseline_sha,
            "Baseline archive is missing under repository artifacts or its hash differs.")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "origin": "crossunit_capture_prerequisites", "startedAtUtc": utc_now(), "completed": False,
        "sourceArchive": {"path": packet["archive"]["path"], "sha256": baseline_sha}, "changes": []}
    def persist():
        report["calls"] = [call for lifetime in report.get("sessions", []) for call in lifetime["calls"]]
        save_json(output / "prepare.json", report)
    session = TypedOwnedSession(args.host, args.worker, output, report, persist, timeout=args.timeout)
    pending = None
    try:
        report["started"] = session.invoke("tia_start")
        report["retrieved"] = session.invoke("project_retrieve", {"archivePath": str(baseline_archive), "expectedSha256": baseline_sha})
        inventory = complete_inventory(session.invoke)
        [plc] = [row["path"] for row in inventory["softwareScopes"] if row["kind"] == "plc"]
        units = {row["name"]: row for row in inventory["softwareScopes"] if row["kind"] == "softwareUnit"}
        require(units.get(CALLER_UNIT, {}).get("namespacePreset") == CALLER_UNIT and units.get(CALLEE_UNIT, {}).get("namespacePreset") == CALLEE_UNIT
                and units[CALLER_UNIT]["relations"] == units[CALLEE_UNIT]["relations"] == [],
                "Baseline must already hold both units with their namespace presets and no relations.")

        def apply(step, kind, payload, selector, before=None, verify=None):
            nonlocal pending
            prepared = pending = session.invoke("change_prepare", {"kind": kind, "payload": payload, **selector})
            row = {"step": step, "kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                "preview": prepared.get("preview"), "applyIntent": {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}}
            report["changes"].append(row)
            persist()
            applied = session.invoke("change_apply", row["applyIntent"])
            row["applied"] = {key: applied.get(key) for key in ("state", "stages", "compile", "recoveryArchive", "recoveryArchiveSha256")}
            after = None
            if verify is None:
                require_compile_only(prepared, applied, plc)
            else:
                require_clean_compile(applied.get("compile"))
                require(applied.get("state") == "applied" and applied.get("compile", {}).get("errorCount") == 0
                    and "changed_project_saved" in applied.get("stages", []), step + ": apply, compile or save failed.")
                after = snapshot(session.invoke, output / step)
                row["readback"] = verify(before, after)
                row["verified"] = True
            pending = None
            persist()
            print("APPLIED " + step + " " + prepared["changeId"], flush=True)
            return applied, after

        # 1. Callees into TiaMcp_UnitA: exactly the two declared blocks are added, no existing XML changes.
        entry, kind, payload = authored_reference(CALLEES, CALLEE_UNIT)
        require(entry["namespace"] == CALLEE_UNIT and PUBLISHED in entry["names"], "Callee source no longer declares the published FC.")
        scope_a, scope_b = units[CALLEE_UNIT]["path"], units[CALLER_UNIT]["path"]
        def callees_added(before, after):
            added, _ = verify_reference_additions(before, after, entry["names"], scope_a, CALLEE_UNIT)
            require(unit_relations(before["inventory"]) == unit_relations(after["inventory"]), "Callee import changed unit relations.")
            return {"added": [{"name": row["name"], "path": row["path"], "payloadSha256": row["payloadSha256"]} for row in added]}
        _, state = apply("callees", kind, {**payload, "scopePath": scope_a, "namespace": CALLEE_UNIT, "expectedBlockNames": entry["names"]}, {},
            snapshot(session.invoke, output / "baseline"), callees_added)

        # 2. SoftwareUnit relation TiaMcp_UnitB -> TiaMcp_UnitA; no native XML and no other relation changes.
        def related(before, after):
            assert_snapshot(before, after)
            expected = {**unit_relations(before["inventory"]), CALLER_UNIT: [{"relationType": "SoftwareUnit", "relatedObject": CALLEE_UNIT}]}
            require(unit_relations(after["inventory"]) == expected, "Unit relations did not read back as exactly B -> A.")
            return {"relations": unit_relations(after["inventory"])}
        _, state = apply("relation", "software_unit_relation_set", {"unitPath": scope_b, "relatedUnitPath": scope_a,
            "relationType": "SoftwareUnit", "action": "add"}, {"plcPath": plc}, state, related)

        # 3. Publish TgpUnit_NumericFC; only its own export may change and it reads back Published.
        [callee] = [row["path"] for row in state["inventory"]["blocks"] if row["scopePath"] == scope_a
                    and row["identity"]["namespace"] == CALLEE_UNIT and row["name"] == PUBLISHED]
        def published(before, after):
            assert_snapshot(before, after, except_paths={callee})
            require(unit_relations(before["inventory"]) == unit_relations(after["inventory"]), "Publication changed unit relations.")
            access = [block_access(pathlib.Path(view["objects"]["block:" + callee]["file"]).read_text(encoding="utf-8-sig")) for view in (before, after)]
            require(access[1] == "Published", "Callee Access did not read back as Published.")
            return {"objectPath": callee, "accessBefore": access[0], "accessAfter": access[1]}
        apply("publication", "software_unit_publication_set", {"objectPath": callee, "attributes": {"Access": "Published"}},
            {"plcPath": plc}, state, published)

        # 4. A compile-only change checkpoints (saves and archives) the prepared state before it compiles.
        checkpointed, _ = apply("checkpoint", "plc_compile", {}, {"plcPath": plc})
        checkpoint, digest = pathlib.Path(checkpointed["recoveryArchive"]).resolve(), checkpointed["recoveryArchiveSha256"]
        require(checkpoint.is_relative_to(output) and sha256(checkpoint) == digest, "Checkpoint archive is outside this run or its hash differs.")
        target = ROOT / "artifacts" / "crossunit-prep" / (digest + ".zap21")
        require(not target.exists(), "Prepared archive destination already exists.")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(checkpoint, target)
        require(sha256(target) == digest, "Copied archive hash differs.")
        relative = target.relative_to(ROOT).as_posix()
        receipt = {"schemaVersion": 1, "purpose": "crossUnit capture archive of " + PACKET.relative_to(ROOT).as_posix(),
            "archive": {"path": relative, "sha256": digest}, "sourceArchive": report["sourceArchive"],
            "appliedChangeIds": [row["changeId"] for row in report["changes"]],
            "changes": [{"step": row["step"], "kind": row["kind"], "changeId": row["changeId"]} for row in report["changes"]],
            "report": str(output / "prepare.json"), "createdAtUtc": utc_now()}
        save_json(target.with_suffix(".json"), receipt)
        report["receipt"] = {"path": str(target.with_suffix(".json")), **receipt}
        report["captureCommand"] = capture_command(capture, relative, digest, args.host, args.worker,
            args.capture_output or output.parent / "cap-crossunit")
        report["completed"], report["status"] = True, "passed"
    except Exception as error:
        report.update(status="blocked", failure={"type": type(error).__name__, "message": str(error)})
        # The project is a disposable retrieval of the baseline, so nothing is restored; the change state is kept as evidence.
        if pending is not None:
            try:
                report["failureStatus"] = session.invoke("change_status", {"changeId": pending["changeId"]})
            except Exception as status_error:
                report["failureStatusError"] = str(status_error)
    finally:
        try:
            session.close()
        except Exception as error:
            report.update(status="blocked", completed=False, cleanupFailure=str(error))
        report["completedAtUtc"] = utc_now()
        persist()
    if report["completed"]:
        print("ARCHIVE " + report["receipt"]["archive"]["path"] + " " + report["receipt"]["archive"]["sha256"])
        print("RECEIPT " + report["receipt"]["path"])
        print(subprocess.list2cmdline(report["captureCommand"]))
        return 0
    print("BLOCKED " + json.dumps(report.get("failure") or report.get("cleanupFailure")))
    return 2


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("host", "worker", "output"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    parser.add_argument("--capture-output", type=pathlib.Path, help="--output of the printed crossUnit capture; default: <output>/../cap-crossunit.")
    parser.add_argument("--timeout", type=int, default=300)
    options = parser.parse_args()
    if not 5 <= options.timeout <= 930:
        parser.error("--timeout must be within 5..930 seconds.")
    raise SystemExit(prepare(options))
