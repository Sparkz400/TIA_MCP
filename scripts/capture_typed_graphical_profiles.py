"""Capture independent SIMATIC SD fixtures through the existing MCP change lifecycle.

This runner creates its own scratch and never attaches to another TIA process.
The handwritten source fixtures, not the typed builder, produce the reference XML.
The fixture-source route is new-block-only; native failures retain exact evidence.
"""
import argparse
import hashlib
import json
import pathlib
import re
import uuid

from block_test_runtime import NativeAudit, canonical, utc_now
from smoke_mcp import Client, ROOT


class Audit(NativeAudit):
    ALLOWED_TOOLS = NativeAudit.ALLOWED_TOOLS | {"project_inventory", "change_restore", "project_open"}


def capture(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "startedAtUtc": utc_now(), "origin": "independently_authored_simatic_sd",
              "builderUsed": False, "calls": [], "fixtures": [], "completed": False,
              "runtimeVerified": False, "physicalPlcAccess": False}
    def persist():
        (output / "capture.json").write_text(canonical(report), encoding="utf-8")
    client = Client(args.host.resolve(), 300, ["--worker", str(args.worker.resolve()), "--artifacts", str(output)])
    call = Audit(client, report["calls"], persist)
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                       "clientInfo": {"name": "typed-native-profile-capture", "version": "1.0.0"}})
        client.notify("notifications/initialized")
        report["started"] = call("tia_start")
        assert report["started"]["ownsPortalProcess"] is True
        if args.resume_report:
            prior = json.loads(args.resume_report.read_text(encoding="utf-8"))
            scratch = prior["scratch"]
            project_path = pathlib.Path(scratch["project"]["path"])
            assert prior["builderUsed"] is False and prior["started"]["ownsPortalProcess"] is True
            assert project_path.resolve().is_relative_to((ROOT / "artifacts").resolve())
            assert project_path.name.startswith("TiaMcpScratch_") and project_path.suffix == ".ap21"
            call("project_open", {"path": str(project_path)})
        else:
            scratch = call("tia_create_scratch", {"cpuTypeIdentifier": args.cpu_type_identifier} if args.cpu_type_identifier else {})
            assert scratch["completed"] and scratch["compile"]["errorCount"] == 0
        report["scratch"] = scratch
        for source_path in args.sources:
            source = source_path.read_text(encoding="utf-8-sig")
            names = re.findall(r'\b(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\s+"([^"]+)"', source)
            entry = {"sourcePath": str(source_path.resolve()), "sha256": hashlib.sha256(source.encode()).hexdigest(),
                     "names": names, "engineeringVerified": False, "runtimeVerified": False}
            report["fixtures"].append(entry)
            print("PREPARE " + source_path.name, flush=True)
            prepared = call("change_prepare", {"kind": "source_upsert", "payload": {
                "language": "SCL" if source_path.suffix.lower() == ".scl" else "SIMATIC_SD", "source": source, "expectedBlockNames": names}})
            entry["changeId"] = prepared["changeId"]
            try:
                applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                    "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
            except Exception as error:
                entry["failure"] = {"type": type(error).__name__, "message": str(error)}
                entry["statusAfterFailure"] = call("change_status", {"changeId": prepared["changeId"]})
                entry["recovery"] = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                persist()
                print("REJECTED " + source_path.name, flush=True)
                continue
            assert applied["state"] == "applied" and applied["compile"]["errorCount"] == 0
            entry["compile"] = applied["compile"]
            entry["exports"] = []
            missing = []
            for name in dict.fromkeys([*names, *args.observe_block]):
                try:
                    exported = call("block_read", {"blockName": name})
                except Exception as error:  # An applied source whose block cannot be read back is recorded, not captured.
                    missing.append({"name": name, "message": str(error)})
                    continue
                target = output / (name + ".xml")
                target.write_text(exported["xml"], encoding="utf-8")
                entry["exports"].append({"name": name, "path": str(target),
                    "sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "block": exported["block"]})
            if missing:
                entry["exportFailures"] = missing
                persist()
                print("UNREADABLE " + source_path.name, flush=True)
                continue
            entry["engineeringVerified"] = True
            persist()
            print("CAPTURED " + source_path.name, flush=True)
        report["completed"] = True
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sources", type=pathlib.Path, nargs="+")
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--resume-report", type=pathlib.Path, help="Reopen only the owned scratch identified by this capture runner's prior report.")
    parser.add_argument("--observe-block", action="append", default=[], help="Also export this exact existing dependent block after each source import.")
    parser.add_argument("--cpu-type-identifier", help="Exact catalog CPU for a new isolated target-specific reference capture.")
    raise SystemExit(capture(parser.parse_args()))
