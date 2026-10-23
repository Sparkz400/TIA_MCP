"""Qualify typed LAD/FBD changes using MCP and the existing owned PLCSIM backend.

Each case starts an isolated scratch. Reference XML and expected behavior must
come from the independent fixture corpus. Five gates remain separate; a failed
runtime assertion cannot be relabeled a compiler or preservation pass.
"""
import argparse
import copy
import importlib.util
import json
import pathlib
import uuid
import hashlib
from xml.etree import ElementTree as ET

from block_test_executor import execute_suite, validate_suite
from block_test_stop import StopRequest
from block_test_program import generate_program, inspect_interface
from block_test_runtime import NativeAudit, NativeToolError, canonical, digest, utc_now
from run_block_tests import apply_change, binary_manifest, save_json
from run_conformance import native_document, structural_xml
from smoke_mcp import Client, ROOT
from typed_graphical_native_expectations import assert_independent_topology, adapt_reference_bindings, assert_existing_caller_insertion
from typed_graphical_transport import artifact_bytes
from qualify_deployment import McpClient, load_configuration
from typed_graphical_owned_session import TypedOwnedSession
from engineering_preservation import PAYLOAD_HASH_POLICY, native_payload_hash
from typed_graphical_runtime_preservation import RuntimePreservation, ObservedRuntimeBackend


class Audit(NativeAudit):
    ALLOWED_TOOLS = NativeAudit.ALLOWED_TOOLS | {"change_restore", "engineering_read", "project_open", "project_inventory", "project_retrieve", "software_inspect", "artifact_read", "tia_capabilities", "type_read", "tag_table_read"}


def native_hash(xml):
    return native_payload_hash(xml)


def read_artifact(artifact, call=None):
    if call is not None:
        return artifact_bytes(call, artifact).decode("utf-8-sig")
    import hashlib
    path = pathlib.Path(artifact["path"])
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != artifact["sha256"] or len(raw) != artifact["bytes"]:
        raise ValueError("Prepared artifact no longer matches its immutable manifest.")
    return raw.decode("utf-8-sig")


def independent_behavior(family):
    source = "extended_behavior.py" if family.startswith("numeric-") else "behavior.py"
    spec = importlib.util.spec_from_file_location("typed_independent_behavior", ROOT / "fixtures/typed-graphical" / source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.cases(family)


def selected_blocks(call, case):
    inventory = call("software_inspect", {"maxItems": 2000})
    completeness = inventory.get("completeness", {})
    assert completeness.get("scopeComplete") is True and completeness.get("truncated") is False, "Complete block identity coverage is required."
    scope = None
    if case.get("unitName"):
        matches = [item for item in inventory["softwareScopes"] if item["kind"] == "softwareUnit" and item["name"] == case["unitName"]]
        assert len(matches) == 1, "Exact software unit selection is ambiguous."
        scope = matches[0]["path"]
    # Keep every unrelated unit/namespace in the snapshot; only the selected
    # route receives short lookup keys for the case's explicit block names.
    def key(block):
        selected = not case.get("unitName") or block.get("scopePath") == scope and block.get("identity", {}).get("namespace", "") == case["namespace"]
        return block["name"] if selected else "@unselected:" + block["path"]
    result = {key(block): block for block in inventory["blocks"]}
    assert len(result) == len(inventory["blocks"]), "Exact selected block names are ambiguous."
    return result, scope


def snapshot(call, names, directory, case=None):
    directory.mkdir()
    exports = {}
    blocks = selected_blocks(call, case)[0] if case is not None else None
    include_peers = names is None
    if include_peers:
        names = sorted(blocks)
    for ordinal, name in enumerate(dict.fromkeys(names)):
        result = call("block_read", {"blockPath": blocks[name]["path"]} if blocks is not None else {"blockName": name})
        (directory / (str(ordinal).zfill(3) + ".xml")).write_bytes(result["xml"].encode("utf-8"))
        exports[name] = {"xml": result["xml"], "payloadSha256": native_hash(result["xml"]), "block": result["block"]}
    if include_peers:
        inventory = call("software_inspect", {"maxItems": 2000})
        coverage = inventory.get("completeness", {})
        assert coverage.get("scopeComplete") is True and coverage.get("truncated") is False, "Complete peer identities are required."
        for collection, kind, tool, selector in (("types", "type", "type_read", "typePath"),
                ("tagTables", "tagTable", "tag_table_read", "tagTablePath")):
            for row in sorted(inventory[collection], key=lambda item: item["path"]):
                key = "@" + kind + ":" + row["path"]
                assert key not in exports, "Duplicate exact peer identity."
                xml = artifact_bytes(call, call(tool, {selector: row["path"]})["artifact"]).decode("utf-8-sig")
                (directory / (str(len(exports)).zfill(3) + ".xml")).write_bytes(xml.encode("utf-8"))
                exports[key] = {"xml": xml, "payloadSha256": native_hash(xml), "block": row, "kind": kind}
    return exports


def assert_snapshot(expected, actual):
    assert set(expected) == set(actual), "Export coverage differs."
    differences = [name for name in expected if expected[name]["payloadSha256"] != actual[name]["payloadSha256"]]
    assert not differences, "Complete native payload changed: " + ", ".join(differences)


def read_graph(call, block_path, artifact_directory):
    selectors = {"scope": "graphical", "blockPath": block_path}
    if artifact_directory is not None:
        selectors["artifactDirectory"] = str(artifact_directory)
    return call("engineering_read", {"selectors": selectors})


def run_case(case, args, output):
    output.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "fixtureId": case["fixtureId"], "language": case["language"],
              "startedAtUtc": utc_now(), "engineeringVerified": False, "preservationVerified": False,
              "persistenceVerified": False, "recoveryVerified": False, "runtimeVerified": False,
              "calls": [], "status": "running", "physicalPlcAccess": False}
    report["payloadHashPolicy"] = PAYLOAD_HASH_POLICY
    report["caseSha256"] = digest(case)
    report["harnessFiles"] = [{"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        for path in (pathlib.Path(__file__).resolve(), pathlib.Path(__file__).with_name("typed_graphical_owned_session.py").resolve(),
            pathlib.Path(__file__).with_name("qualification_owned_scratch.py").resolve(),
            pathlib.Path(__file__).with_name("engineering_preservation.py").resolve(),
            pathlib.Path(__file__).with_name("typed_graphical_native_expectations.py").resolve(),
            pathlib.Path(__file__).with_name("typed_graphical_runtime_preservation.py").resolve(),
            pathlib.Path(__file__).with_name("block_test_runtime.py").resolve(),
            pathlib.Path(__file__).with_name("block_test_executor.py").resolve(),
            pathlib.Path(__file__).with_name("block_test_program.py").resolve(),
            ROOT / "fixtures/typed-graphical/extended_behavior.py",
            ROOT / "fixtures/typed-graphical/behavior.py")]
    def persist():
        if report.get("sessions"):
            report["calls"] = [row for session in report["sessions"] for row in session["calls"]]
        save_json(output / "result.json", report)
    configuration_path = getattr(args, "config", None)
    session, runtime_preservation = None, None
    if configuration_path:
        configuration = load_configuration(configuration_path, args.server)
        report["deployment"] = {"transport": configuration["transport"], "configurationSha256": configuration["configurationSha256"],
                                "artifactTransfer": "MCP artifact_read; remote paths are not client filesystem paths"}
        # The actual configured launcher selects its own immutable bundle and
        # external artifact root. Never replace SSH with a local executable.
        native_artifacts = None
        client = McpClient(configuration, args.timeout)
    else:
        report["binaries"] = binary_manifest(args.host, args.worker, args.sim_worker if args.live else None)
        # TIA limits project directories to 143 characters; keep native roots short.
        native_artifacts = ROOT / "artifacts" / ("q" + uuid.uuid4().hex[:6])
        native_artifacts.mkdir(exist_ok=False)
        report["nativeArtifacts"] = str(native_artifacts)
        session = TypedOwnedSession(args.host, args.worker, native_artifacts, report, persist,
            timeout=getattr(args, "timeout", 300), sim_worker=args.sim_worker if args.live else None,
            extra_tools=Audit.ALLOWED_TOOLS)
    call = session.invoke if session is not None else Audit(client, report["calls"], persist)
    try:
        if configuration_path:
            client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                           "clientInfo": {"name": "typed-graphical-qualification", "version": "1.0.0"}})
            client.notify("notifications/initialized")
            report["engineeringEnvironment"] = call("tia_capabilities")
        started = call("tia_start")
        assert started["ownsPortalProcess"] is True
        engineering_only = case.get("engineeringOnly", False) or getattr(args, "engineering_only", False)
        if case.get("archivePath"):
            assert not configuration_path, "Client-local baseline archives cannot be used by a remote configured host."
            archive = (ROOT / case["archivePath"]).resolve()
            assert archive.is_relative_to((ROOT / "artifacts").resolve()) and archive.suffix.lower() == ".zap21"
            assert hashlib.sha256(archive.read_bytes()).hexdigest() == case["archiveSha256"]
            scratch = call("project_retrieve", {"archivePath": str(archive), "expectedSha256": case["archiveSha256"]})
            assert engineering_only, "Archived route cases currently require engineeringOnly; no runtime credential provenance is inferred."
        else:
            scratch = call("tia_create_scratch", {"cpuTypeIdentifier": case["cpuTypeIdentifier"]})
            assert scratch["completed"] is True and scratch["compile"]["errorCount"] == 0
        report["scratch"] = scratch
        _, scope = selected_blocks(call, case)
        route = {"scopePath": scope, "namespace": case["namespace"]} if scope else {}
        reference = ROOT / case["referenceXml"]
        reference_xml = reference.read_bytes().decode("utf-8-sig")
        report["independentReference"] = {"path": str(reference), "payloadSha256": native_hash(reference_xml)}
        for dependency in case.get("dependencySources", []):
            apply_change(call, "source_upsert", {**route, **dependency})
        for dependency in case.get("dependencyXmls", []):
            xml = (ROOT / dependency["path"]).read_bytes().decode("utf-8-sig")
            apply_change(call, "block_import_xml", {**route, "xml": xml, "expectedBlockNames": dependency["names"]})
        apply_change(call, "block_import_xml", {**route, "xml": reference_xml, "expectedBlockNames": [case["blockName"]]})
        for specification in case.get("runtimeSources", []):
            source_payload = copy.deepcopy(specification)
            if "sourcePath" in source_payload:
                assert "source" not in source_payload, "An authored runtime source must select exactly one source representation."
                source_path = (ROOT / source_payload.pop("sourcePath")).resolve()
                assert source_path.is_relative_to((ROOT / "fixtures").resolve()), "Runtime source must be an authored repository fixture."
                source_payload["source"] = source_path.read_bytes().decode("utf-8-sig")
                report.setdefault("runtimeSourceArtifacts", []).append({"path": str(source_path), "sha256": hashlib.sha256(source_path.read_bytes()).hexdigest()})
            apply_change(call, "source_upsert", {**route, **source_payload})
        if not engineering_only:
            runtime_name = case.get("runtimeBlockName", case["blockName"])
            interface = call("block_interface", {"blockName": runtime_name})
            report["runtimeBlockName"] = runtime_name
            definitions = {}
            for name in case.get("interfaceDependencies", []):
                definitions[name] = call("block_interface", {"blockName": name})
            instances = tuple(case.get("runtimeInstances", ["dut"]))
            program = generate_program(inspect_interface(interface, definitions), instances=instances, prefix="TgpTest")
            suite = {"fixtureId": case["fixtureId"], "cases": independent_behavior(case["family"]), "instances": list(instances)}
            validate_suite(suite, program)
            if args.live:
                assert len(suite["cases"]) == 1 and suite["cases"][0].get("repeat", 1) == 1, "Live qualification currently admits one fresh authored diagnostic only."
            (output / "suite.json").write_text(canonical(suite), encoding="utf-8")
            (output / "program.json").write_text(canonical(program), encoding="utf-8")
            apply_change(call, "source_upsert", {"language": "SCL", "source": program["source"], "expectedBlockNames": program["expectedBlockNames"]})
        if args.live and not engineering_only:
            apply_change(call, "project_simulation_enable", {"enabled": True})
        before = snapshot(call, None, output / "before", case)
        target = before[case["blockName"]]
        target_name = case.get("createdBlockName", case["blockName"])
        payload = copy.deepcopy(case["payload"])
        payload.update(route)
        if payload["model"]["operation"] != "createBlock":
            payload["blockPath"] = target["block"]["path"]
        else:
            assert target_name not in before, "Creation target must not exist before preparation."
        document = native_document(target["xml"])
        # Exact callee paths are observed from this scratch, never synthesized.
        for node in payload["model"].get("nodes", []):
            if node.get("calleePath", "").startswith("$block:"):
                node["calleePath"] = before[node["calleePath"][7:]]["block"]["path"]
            if node.get("nativeUid", "").startswith("$part:"):
                _, kind, ordinal = node["nativeUid"].split(":")
                matches = [e for e in document.iter() if e.tag.rsplit("}", 1)[-1] == "Part" and e.get("Name") == kind]
                node["nativeUid"] = matches[int(ordinal)].get("UId")
        for instance in payload.get("instances", []):
            if instance.get("blockPath", "").startswith("$block:"):
                instance["blockPath"] = before[instance["blockPath"][7:]]["block"]["path"]
        for field in ("networkId", "insertAfterNetworkId"):
            if payload["model"].get(field) == "$first":
                payload["model"][field] = next(e.attrib["ID"] for e in document.iter() if e.tag == "SW.Blocks.CompileUnit")
        prepared = call("change_prepare", {"kind": "graphical_typed_edit", "payload": payload})
        report["prepared"] = prepared
        expected_xml = read_artifact(prepared["preview"]["typed"]["nativeArtifact"], call if configuration_path else None)
        (output / "prepared.xml").write_bytes(expected_xml.encode("utf-8"))
        if payload["model"]["operation"] == "insertNetwork":
            assert all(instance["kind"] in {"existingGlobal", "existingMulti"} for instance in payload.get("instances", [])), "Insertion plus storage changes requires a separately captured composite preservation contract."
            assert_existing_caller_insertion(target["xml"], expected_xml, prepared["preview"]["typed"]["networkId"], payload["model"].get("insertAfterNetworkId"))
            report["insertionPreservationContract"] = "network-only-strict-additive-v1"
        oracle = adapt_reference_bindings(reference_xml, case.get("referenceBindings", []))
        assert_independent_topology(oracle, expected_xml, prepared["preview"]["typed"]["networkId"])
        report["independentNativeTopologyVerified"] = True
        report["applyIntent"] = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}
        persist()
        applied = call("change_apply", report["applyIntent"])
        assert applied["state"] == "applied" and applied["compile"]["errorCount"] == 0
        assert "changed_project_saved" in applied["stages"]
        report["engineeringVerified"] = True
        report["applied"] = applied
        after = snapshot(call, None, output / "after", case)
        additions = ({target_name} if payload["model"]["operation"] == "createBlock" else set()) | {i["name"] for i in payload.get("instances", []) if i["kind"] == "createGlobal"}
        assert set(after) == set(before) | additions, "Unexpected native blocks were created or deleted."
        assert native_hash(expected_xml) == after[target_name]["payloadSha256"], "Complete prepared native XML differs from native readback."
        prepared_effects = prepared["preview"].get("effects", [])
        changed_peers = set()
        for effect in prepared_effects:
            artifact = effect.get("expectedArtifact", effect.get("artifact"))
            if artifact and effect.get("action") in ("createInstanceDB", "updateDependentInstanceDB", "nativeDependentRecompile"):
                name = effect.get("name")
                if not name:
                    name = next(k for k, v in after.items() if v["block"]["path"] == effect["path"])
                assert native_hash(read_artifact(artifact, call if configuration_path else None)) == after[name]["payloadSha256"], "Prepared dependency/storage XML differs from native readback."
                changed_peers.add(name)
        assert additions - {target_name} <= changed_peers, "Every created storage DB must have a compared prepared artifact."
        peers = {name: value for name, value in before.items() if name != target_name and name not in changed_peers}
        assert_snapshot(peers, {name: after[name] for name in peers})
        report["preservationVerified"] = True
        report["graph"] = read_graph(call, after[target_name]["block"]["path"], native_artifacts)
        assert report["graph"]["readOnly"] is True
        report["statusJournal"] = call("change_status", {"changeId": prepared["changeId"]})
        # Restore while the original owned attachment is active; the saved changed
        # project remains available at its original exact path for fresh reopen.
        report["restoreDispatched"] = True
        persist()
        restored = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        assert restored["state"] == "restored"
        recovered_path = restored["recoveredProject"]["path"]
        report["restored"] = restored
        recovered = snapshot(call, None, output / "recovered", case)
        assert_snapshot(before, recovered)
        call("tia_detach")
        call("tia_start")
        call("project_open", {"path": scratch["project"]["path"]})
        reopened = snapshot(call, None, output / "reopened", case)
        assert_snapshot(after, reopened)
        report["persistenceVerified"] = True
        call("tia_detach")
        call("tia_start")
        call("project_open", {"path": recovered_path})
        assert_snapshot(before, snapshot(call, None, output / "recovered-reopened", case))
        report["recoveryVerified"] = True
        if args.live and not engineering_only:
            # Complete every engineering admission before the one runtime diagnostic.
            # Runtime failure permits only read-only observations and owned cleanup.
            call("tia_detach")
            def plc_readback():
                session.guard()
                readback = snapshot(call, None, output / "runtime-before-cleanup", case)
                assert_snapshot(after, readback)
                return {"payloadHashPolicy": report["payloadHashPolicy"], "exports":
                    {name: value["payloadSha256"] for name, value in readback.items()}}
            runtime_preservation = RuntimePreservation(scratch["project"]["path"], native_artifacts, report, persist, plc_readback)
            call("tia_start")
            call("project_open", {"path": scratch["project"]["path"]})
            assert_snapshot(after, snapshot(call, None, output / "runtime-before", case))
            runtime_preservation.capture_active_project()
            credentials = scratch["scratchSecurity"]["cpus"]
            assert len(credentials) == 1
            revision = {"caseSha256": digest(case), "nativeProgram": program["revision"],
                        "exports": {name: value["payloadSha256"] for name, value in reopened.items()}}
            def backend_factory(_case):
                return ObservedRuntimeBackend(call, program, revision, credentials[0]["credentialMetadataPath"],
                    preservation=runtime_preservation,
                    provision_runtime_credential=args.provision_runtime_credential,
                    trust_owned_empty_cpu_certificate=args.trust_owned_empty_cpu_certificate)
            report["runtime"] = execute_suite(suite, program, backend_factory, revision,
                persist=lambda value: (report.update({"runtime": value}), persist()))
            report["runtimeBehaviorVerified"] = report["runtime"].get("status") == "passed"
        else:
            report["runtime"] = {"status": "not_run", "reason": "Engineering-only case or --live was not selected; compile is not runtime evidence."}
        report["status"] = "passed" if engineering_only or not args.live or report.get("runtimeBehaviorVerified") else "blocked"
    except Exception as error:
        report["status"] = "blocked"
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        if isinstance(error, NativeToolError):
            details = error.result.get("error", {}).get("details", {}) or {}
            compiler = details.get("compile")
            if isinstance(compiler, dict):
                report["compileObservedSuccess"] = compiler.get("errorCount") == 0
                report["failureCompile"] = compiler
        # Failed readback/compilation is a changed project, not a safe retry.
        # Reconcile the exact known change and recover its recorded checkpoint
        # while the attachment still exists; retain the changed-state archive.
        typed_prepared = report.get("prepared")
        if typed_prepared is not None and "restored" not in report:
            try:
                status = call("change_status", {"changeId": typed_prepared["changeId"]})
                report["failureStatus"] = status
                if not report.get("restoreDispatched") and status.get("state") in ("applied", "recovery_required") and status.get("recoveryArchive"):
                    report["restoreDispatched"] = True
                    persist()
                    recovered = call("change_restore", {"changeId": typed_prepared["changeId"], "previewHash": typed_prepared["previewHash"]})
                    report["failureRecovery"] = recovered
                    assert recovered["state"] == "restored"
                    assert_snapshot(before, snapshot(call, None, output / "failure-recovered", case))
                    report["failureRecoveryOriginalVerified"] = True
            except Exception as recovery_error:
                report["failureRecoveryError"] = {"type": type(recovery_error).__name__, "message": str(recovery_error)}
    finally:
        if runtime_preservation is not None and not runtime_preservation.before_cleanup_attempted:
            try:
                runtime_preservation.before_cleanup()
            except Exception as error:
                report["runtimeReadbackFailure"] = {"type": type(error).__name__, "message": str(error)}
                report["status"] = "blocked"
        try:
            if session is not None:
                session.close()
                report["ownedCleanupVerified"] = bool(report.get("sessions")) and all(row.get("cleanup", {}).get("completed") is True for row in report["sessions"])
                report["hostExitCode"] = session.session["cleanup"]["launcherShutdown"]["hostExitCode"]
            else:
                report["detach"] = call("tia_detach")
        except Exception as error:
            report["cleanupFailure"] = str(error)
            report["status"] = "blocked"
        if session is None:
            report["launcherShutdown"] = client.close()
            report["hostExitCode"] = client.process.returncode
        if configuration_path and (report["hostExitCode"] != 0 or report["launcherShutdown"].get("forcedLauncherStop")):
            report["status"] = "blocked"
            report["cleanupFailure"] = "Configured launcher did not complete a clean shutdown; remote process cleanup remains unverified."
        if runtime_preservation is not None:
            try:
                preserved = runtime_preservation.after_cleanup(report.get("ownedCleanupVerified") is True and not report.get("cleanupFailure"))
                report["runtimeVerified"] = bool(report.get("runtimeBehaviorVerified") and preserved
                    and report.get("ownedCleanupVerified") and not report.get("cleanupFailure"))
                if not report["runtimeVerified"]:
                    report["status"] = "blocked"
            except Exception as error:
                report["runtimePreservationFailure"] = {"type": type(error).__name__, "message": str(error)}
                report["status"] = "blocked"
        if not all(hashlib.sha256(pathlib.Path(row["path"]).read_bytes()).hexdigest() == row["sha256"] for row in report["harnessFiles"]):
            report["status"] = "blocked"
            report["runtimeVerified"] = False
            report["harnessIntegrityFailure"] = "Qualification harness changed during the run."
        report["completedAtUtc"] = utc_now()
        persist()
    return report


def run_batch(selected, args, runner=run_case):
    """Admit one case only after its predecessor and cleanup completed."""
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    stop = StopRequest(getattr(args, "stop_file", None) or output / "stop-request.json")
    identifiers = [case["fixtureId"] for case in selected]
    assert len(identifiers) == len(set(identifiers)), "Duplicate fixture IDs."
    batch = {"schemaVersion": 1, "status": "running", "startedAtUtc": utc_now(),
        "selected": identifiers, "completed": [], "remaining": identifiers.copy(),
        "stopRequest": stop.evidence(), "resumePolicy": "Fresh explicit output only; never replay or merge reports."}
    results = []
    def persist():
        save_json(output / "batch.json", batch)
        save_json(output / "index.json", results)
    persist()
    for case in selected:
        if stop():
            batch["status"] = "stopped_by_request"
            break
        batch["activeFixtureId"] = case["fixtureId"]
        persist()
        try:
            result = runner(case, args, output / case["fixtureId"])
        except Exception as error:
            batch["status"] = "blocked"
            batch["blockedFixtureId"] = case["fixtureId"]
            batch["failure"] = {"type": type(error).__name__, "message": str(error)}
            break
        results.append(result)
        batch["completed"].append(case["fixtureId"])
        batch["remaining"].remove(case["fixtureId"])
        batch.pop("activeFixtureId", None)
        persist()
        if result.get("status") != "passed" or result.get("cleanupFailure"):
            batch["status"] = "blocked"
            batch["blockedFixtureId"] = case["fixtureId"]
            break
    else:
        batch["status"] = "passed"
    batch["stopRequest"] = stop.evidence()
    batch["completedAtUtc"] = utc_now()
    persist()
    return 0 if batch["status"] == "passed" else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", type=pathlib.Path, default=ROOT / "fixtures/typed-graphical/cases.v1.json")
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path)
    parser.add_argument("--config", type=pathlib.Path, help="Actual local or strict SSH MCP configuration; never substitutes a local worker.")
    parser.add_argument("--server", default="tia-v21")
    parser.add_argument("--timeout", type=int, default=330, help="Configured transport request bound in seconds.")
    parser.add_argument("--sim-worker", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--stop-file", type=pathlib.Path, help="Presence-only sentinel; finish active case and cleanup, then admit no next case.")
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--engineering-only", action="store_true", help="Skip runtime harness generation; never claims behavioral qualification.")
    parser.add_argument("--baseline-archive", type=pathlib.Path, help="SHA-bound owned minimal baseline, freshly retrieved for each engineering-only case.")
    parser.add_argument("--baseline-sha256")
    parser.add_argument("--provision-runtime-credential", action="store_true")
    parser.add_argument("--trust-owned-empty-cpu-certificate", action="store_true")
    args = parser.parse_args()
    if bool(args.config) == bool(args.worker):
        parser.error("Select exactly one of --config or --worker")
    if args.config and (args.live or args.sim_worker or not args.engineering_only or args.baseline_archive):
        parser.error("--config currently requires --engineering-only and fresh owned scratch; remote simulator and client baseline access are not qualified")
    if not 5 <= args.timeout <= 930:
        parser.error("--timeout must be within 5..930 seconds")
    if args.live and not args.sim_worker:
        parser.error("--live requires --sim-worker")
    if args.trust_owned_empty_cpu_certificate and not args.provision_runtime_credential:
        parser.error("TLS trust requires explicit credential provisioning")
    if bool(args.baseline_archive) != bool(args.baseline_sha256):
        parser.error("--baseline-archive and --baseline-sha256 are required together")
    if args.baseline_archive and (not args.engineering_only or args.live):
        parser.error("Owned baseline retrieval requires --engineering-only and cannot be combined with --live")
    selected = json.loads(args.cases.read_text(encoding="utf-8"))["cases"]
    if args.case:
        selected = [case for case in selected if case["fixtureId"] in args.case]
        if {case["fixtureId"] for case in selected} != set(args.case):
            parser.error("Unknown --case selector")
    assert selected, "No cases selected."
    if args.live and len(selected) != 1:
        parser.error("--live requires exactly one explicit diagnostic case; a runtime corpus is not admitted")
    if args.config and any(case.get("archivePath") for case in selected):
        parser.error("Selected case requires a client-local archive; configured deployment qualification requires fresh owned scratch cases")
    if args.baseline_archive:
        for case in selected:
            if case.get("archivePath"):
                parser.error("A case already selects its own exact baseline archive")
            case["archivePath"] = str(args.baseline_archive.resolve())
            case["archiveSha256"] = args.baseline_sha256
    return run_batch(selected, args)


if __name__ == "__main__":
    raise SystemExit(main())
