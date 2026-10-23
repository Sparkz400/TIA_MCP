"""Capture graphical references in separately selected software-unit namespaces.

Retrieves a SHA-bound archive into a new owned project. It never opens the
original project, creates software units, or uses the typed graphical builder.
"""
import argparse
import hashlib
import json
import pathlib
import re
import uuid
from xml.dom import minidom

from block_test_runtime import NativeAudit, canonical, utc_now
from qualify_typed_graphical import native_hash
from smoke_mcp import Client, ROOT


class Audit(NativeAudit):
    ALLOWED_TOOLS = NativeAudit.ALLOWED_TOOLS | {"project_retrieve", "project_inventory", "software_inspect", "hardware_inspect", "change_restore", "project_open"}


def capture(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    archive = args.archive.resolve()
    assert archive.is_relative_to((ROOT / "artifacts").resolve())
    assert archive.suffix.lower() == ".zap21"
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == args.sha256
    report = {"schemaVersion": 1, "startedAtUtc": utc_now(), "origin": "independently_authored_native_references",
              "builderUsed": False, "calls": [], "fixtures": [], "completed": False,
              "runtimeVerified": False, "physicalPlcAccess": False,
              "sourceArchive": {"path": str(archive), "sha256": args.sha256}}
    def persist():
        (output / "capture.json").write_text(canonical(report), encoding="utf-8")
    client = Client(args.host.resolve(), 300, ["--worker", str(args.worker.resolve()), "--artifacts", str(output)])
    call = Audit(client, report["calls"], persist)
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                       "clientInfo": {"name": "typed-native-route-capture", "version": "1.0.0"}})
        client.notify("notifications/initialized")
        report["started"] = call("tia_start")
        assert report["started"]["ownsPortalProcess"] is True
        if args.resume_report:
            prior = json.loads(args.resume_report.read_text(encoding="utf-8"))
            assert prior["builderUsed"] is False and prior["started"]["ownsPortalProcess"] is True
            assert prior["sourceArchive"] == report["sourceArchive"]
            project = pathlib.Path(prior.get("currentProject", prior["retrieved"]["project"])["path"])
            assert project.resolve().is_relative_to((ROOT / "artifacts").resolve())
            call("project_open", {"path": str(project)})
            report["retrieved"] = prior["retrieved"]
            report["resumeReport"] = str(args.resume_report.resolve())
        else:
            report["retrieved"] = call("project_retrieve", {"archivePath": str(archive), "expectedSha256": args.sha256})
        report["currentProject"] = {"path": str(project)} if args.resume_report else report["retrieved"]["project"]
        report["hardware"] = call("hardware_inspect", {"maxItems": 2000})
        inventory = call("software_inspect", {"maxItems": 2000})
        report["inventory"] = inventory
        unit = next(scope for scope in inventory["softwareScopes"] if scope["name"] == args.unit and scope["kind"] == "softwareUnit")
        peers = {block["path"]: call("block_read", {"blockPath": block["path"]}) for block in inventory["blocks"]}
        report["baseline"] = {path: native_hash(value["xml"]) for path, value in peers.items()}
        expected_changed = {block["path"] for block in inventory["blocks"] if block.get("scopePath") == unit["path"] and block["name"] in args.expected_existing_change}
        report["expectedExistingChanges"] = sorted(expected_changed)
        known_payloads = dict(report["baseline"])
        for source_path in args.sources:
            source = source_path.read_text(encoding="utf-8-sig")
            if source_path.suffix.lower() == ".xml":
                document = minidom.parseString(source)
                block = next(n for n in document.documentElement.childNodes if n.nodeType == n.ELEMENT_NODE and n.tagName.startswith("SW.Blocks."))
                attributes = next(n for n in block.childNodes if n.nodeType == n.ELEMENT_NODE and n.tagName == "AttributeList")
                values = {n.tagName: "".join(c.data for c in n.childNodes if c.nodeType == c.TEXT_NODE) for n in attributes.childNodes if n.nodeType == n.ELEMENT_NODE}
                names, namespace = [values["Name"]], values["Namespace"]
                kind, payload = "block_import_xml", {"xml": source}
                authored_format = "native_xml_template_derivation"
            else:
                names = re.findall(r'\b(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\s+"([^\"]+)"', source)
                namespace = re.search(r'^NAMESPACE\s+([A-Za-z0-9_.]+)', source, re.MULTILINE).group(1)
                kind = "source_upsert"
                payload = {"language": "SCL" if source_path.suffix.lower() == ".scl" else "SIMATIC_SD", "source": source}
                authored_format = payload["language"]
            route = "softwareUnit" if namespace == args.unit else "namespace"
            entry = {"sourcePath": str(source_path.resolve()), "sha256": hashlib.sha256(source.encode()).hexdigest(),
                     "names": names, "namespace": namespace, "route": route, "scopePath": unit["path"],
                     "engineeringVerified": False, "runtimeVerified": False, "authoredFormat": authored_format}
            report["fixtures"].append(entry)
            print("PREPARE " + source_path.name, flush=True)
            prepared = call("change_prepare", {"kind": kind, "payload": {**payload, "scopePath": unit["path"],
                "namespace": namespace, "expectedBlockNames": names}})
            entry["changeId"] = prepared["changeId"]
            try:
                applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                    "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
            except Exception as error:
                entry["failure"] = {"type": type(error).__name__, "message": str(error)}
                entry["failureExports"] = []
                for name in names:
                    try:
                        observed = call("block_read", {"blockName": name, "scopePath": unit["path"]})
                        entry["failureExports"].append({"block": observed["block"], "artifact": observed["artifact"]})
                    except Exception as export_error:
                        entry["failureExports"].append({"name": name, "failure": str(export_error)})
                entry["statusAfterFailure"] = call("change_status", {"changeId": prepared["changeId"]})
                try:
                    entry["recovery"] = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                    report["currentProject"] = entry["recovery"]["recoveredProject"]
                except Exception as restore_error:
                    rejection = getattr(restore_error, "result", {}).get("error", {}).get("code")
                    if not args.retrieve_after_restore_rejection or rejection != "StaleRevision":
                        raise
                    # The original recovery guard stays intact. Retrieve its
                    # SHA-bound checkpoint into another newly owned instance;
                    # preserve the original project on disk and never label
                    # this as successful change_restore qualification.
                    status = entry["statusAfterFailure"]
                    checkpoint = pathlib.Path(status["recoveryArchive"]).resolve()
                    assert checkpoint.is_relative_to((ROOT / "artifacts").resolve())
                    assert hashlib.sha256(checkpoint.read_bytes()).hexdigest() == status["recoveryArchiveSha256"]
                    entry["changeRestoreBlocked"] = str(restore_error)
                    call("tia_detach")
                    assert call("tia_start")["ownsPortalProcess"] is True
                    retrieved = call("project_retrieve", {"archivePath": str(checkpoint), "expectedSha256": status["recoveryArchiveSha256"]})
                    entry["checkpointRetrieveRecovery"] = retrieved
                    report["currentProject"] = retrieved["project"]
                    entry["checkpointPayloadsVerified"] = all(native_hash(call("block_read", {"blockPath": path})["xml"]) == expected
                        for path, expected in known_payloads.items())
                    assert entry["checkpointPayloadsVerified"]
                # Retrieval changes the root identity. Refresh every selector from
                # the recovered project's inventory before the next operation.
                inventory = call("software_inspect", {"maxItems": 2000})
                unit = next(scope for scope in inventory["softwareScopes"] if scope["name"] == args.unit and scope["kind"] == "softwareUnit")
                persist()
                print("REJECTED " + source_path.name, flush=True)
                if args.stop_on_failure:
                    break
                continue
            assert applied["state"] == "applied" and applied["compile"]["errorCount"] == 0
            entry["compile"] = applied["compile"]
            entry["exports"] = []
            current = call("software_inspect", {"scopePath": unit["path"], "maxItems": 2000})
            for name in names:
                matches = [block for block in current["blocks"] if block["name"] == name and block["identity"]["namespace"] == namespace]
                assert len(matches) == 1
                exported = call("block_read", {"blockPath": matches[0]["path"]})
                target = output / (name + ".xml")
                target.write_bytes(pathlib.Path(exported["artifact"]["path"]).read_bytes())
                entry["exports"].append({"name": name, "path": str(target),
                    "sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "block": exported["block"]})
                known_payloads[exported["block"]["path"]] = native_hash(exported["xml"])
            entry["observedDependencies"] = []
            for name in args.observe_block:
                matches = [block for block in current["blocks"] if block["name"] == name and block["identity"]["namespace"] == namespace]
                assert len(matches) == 1
                exported = call("block_read", {"blockPath": matches[0]["path"]})
                target = output / (name + ".xml")
                target.write_bytes(pathlib.Path(exported["artifact"]["path"]).read_bytes())
                entry["observedDependencies"].append({"name": name, "path": str(target),
                    "sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "block": exported["block"]})
                known_payloads[exported["block"]["path"]] = native_hash(exported["xml"])
            entry["engineeringVerified"] = True
            persist()
            print("CAPTURED " + source_path.name, flush=True)
        # Unrelated unit/root objects use complete native payload equality.
        report["unrelatedPreserved"] = all(native_hash(call("block_read", {"blockPath": path})["xml"]) == expected
                                           for path, expected in report["baseline"].items() if path not in expected_changed)
        report["completed"] = all(entry["engineeringVerified"] for entry in report["fixtures"]) and report["unrelatedPreserved"]
    except Exception as error:
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        print("FAILED " + str(error), flush=True)
    finally:
        try:
            report["detach"] = call("tia_detach")
        except Exception as error:
            report["cleanupFailure"] = str(error)
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["completedAtUtc"] = utc_now()
        persist()
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, fromfile_prefix_chars="@")
    parser.add_argument("sources", type=pathlib.Path, nargs="+")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--archive", type=pathlib.Path, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--unit", default="TiaMcp_UnitA")
    parser.add_argument("--resume-report", type=pathlib.Path)
    parser.add_argument("--observe-block", action="append", default=[], help="Export exact same-namespace dependent blocks after each source mutation.")
    parser.add_argument("--expected-existing-change", action="append", default=[], help="Explicit owned source-effect probe objects excluded from unrelated-object equality; retain full before/after exports.")
    parser.add_argument("--stop-on-failure", action="store_true", help="Stop dependent source batches after explicit checkpoint recovery of their first failure.")
    parser.add_argument("--retrieve-after-restore-rejection", action="store_true", help="For owned source probes only, retain StaleRevision and retrieve the exact checkpoint into a new owned instance; this does not qualify change_restore.")
    raise SystemExit(capture(parser.parse_args()))
