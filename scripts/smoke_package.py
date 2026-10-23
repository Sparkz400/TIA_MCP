"""Verify bundle hashes and its generated MCP launcher using read-only tools."""
import argparse
import hashlib
import json
import pathlib
import zipfile

from smoke_mcp import Client
from qualify_deployment import read_catalog, require


def verify_catalog(client, external_catalog, tool_names):
    catalog = read_catalog(client, include_features=True)
    require(catalog['features'] == external_catalog['features'], 'Packaged catalog feature metadata differs from the external contract.')
    require(catalog['catalogVersion'] == external_catalog['catalogVersion'], 'Packaged catalog version differs from the external contract.')
    require(set(tool_names) <= set(catalog['featureIds']), 'Packaged tool list is missing feature contracts.')
    return {'featureCount': catalog['featureCount'], 'catalogVersion': catalog['catalogVersion']}


def clean_shutdown(receipt):
    workers = receipt.get("workerExits", [])
    return (receipt.get("hostExitCode") == 0 and receipt.get("forcedHostStop") is False
            and receipt.get("stderrEofObserved") is True and len(workers) == 2
            and all(type(row.get("processId")) is int and row["processId"] > 0 for row in workers)
            and len({row["processId"] for row in workers}) == 2
            and all(row.get("processExitVerified") is True and row.get("exitCode") == 0
                    and row.get("forcedTermination") is False for row in workers))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=pathlib.Path, required=True)
    parser.add_argument("--configuration", type=pathlib.Path,
                        help="External MCP configuration; defaults to <bundle>.mcp.json beside the bundle")
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if args.report.exists():
        parser.error("Select a fresh report path; previous qualification evidence is immutable.")
    bundle = args.bundle.resolve()
    manifest = json.loads((bundle / "manifest.json").read_text(encoding="utf-8-sig"))
    for entry in manifest["files"]:
        path = (bundle / entry["path"]).resolve()
        assert path.is_relative_to(bundle), entry
        assert path.stat().st_size == entry["bytes"], entry
        assert hashlib.sha256(path.read_bytes()).hexdigest() == entry["sha256"], entry
    assert not list(bundle.rglob("Siemens.*.dll")), "Vendor binaries must not be redistributed."
    archive = bundle.with_name(bundle.name + ".zip")
    if archive.exists():
        with zipfile.ZipFile(archive) as zipped:
            for entry in manifest["files"]:
                data = zipped.read(entry["path"].replace("\\", "/"))
                assert hashlib.sha256(data).hexdigest() == entry["sha256"], entry
    configuration_path = (args.configuration or bundle.with_name(bundle.name + ".mcp.json")).resolve()
    configuration = json.loads(configuration_path.read_text(encoding="utf-8-sig"))["mcpServers"]["tia-v21"]
    client = None
    report = {"bundle": str(bundle), "manifestFilesVerified": len(manifest["files"]), "completed": False, "steps": [],
              "configuration": {"path": str(configuration_path), "sha256": hashlib.sha256(configuration_path.read_bytes()).hexdigest()}}

    def step(name, result):
        report["steps"].append({"name": name, "result": result})
        print("PASS " + name, flush=True)
        return result

    try:
        client = Client(None, 300, command=[configuration["command"], *configuration["args"]])
        step("launcher MCP initialize", client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-bundle-smoke", "version": "0.3.0"}}))
        client.notify("notifications/initialized")
        tools = client.request("tools/list")
        names = sorted(tool["name"] for tool in tools["tools"])
        assert {"project_retrieve", "call_graph", "change_apply", "safety_inspect", "simulation_download", "sim_test", "artifact_read", "feature_catalog", "object_resolve", "target_capabilities"}.issubset(names)
        step("packaged tool list", names)
        external_catalog = json.loads((bundle / "contracts/feature-catalog.v1.json").read_text(encoding="utf-8"))
        step("packaged feature catalog matches external contract", verify_catalog(client, external_catalog, names))
        step("packaged engineering capabilities", client.tool("tia_capabilities"))
        step("packaged engineering discovery", client.tool("tia_discover"))
        step("packaged simulator status", client.tool("sim_status"))
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error)[:2000], flush=True)
    finally:
        if client is not None:
            try:
                report["launcherShutdown"] = client.close()
                report["hostExitCode"] = client.process.returncode
                if not clean_shutdown(report["launcherShutdown"]):
                    report["completed"] = False
            except Exception as error:
                report["completed"] = False
                report["launcherCleanupFailure"] = str(error)
            report["stderrTail"] = client.errors[-20:]
        if report["completed"]:
            # Bundled conformance scripts accept --host directly, without the launcher.
            # This must resolve both adjacent worker directories independently of the checkout.
            direct = None
            try:
                direct = Client(bundle / "host/TiaMcp.Host.exe", 300)
                direct.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-bundle-direct-smoke", "version": "0.3.0"}})
                direct.notify("notifications/initialized")
                step("direct packaged host resolves engineering worker", direct.tool("tia_capabilities"))
                step("direct packaged host resolves simulator worker", direct.tool("sim_status"))
            except Exception as error:
                report["completed"] = False
                report["directHostFailure"] = str(error)
            finally:
                if direct is not None:
                    try:
                        report["directShutdown"] = direct.close()
                        report["directHostExitCode"] = direct.process.returncode
                        report["completed"] = report["completed"] and clean_shutdown(report["directShutdown"])
                    except Exception as error:
                        report["completed"] = False
                        report["directCleanupFailure"] = str(error)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
