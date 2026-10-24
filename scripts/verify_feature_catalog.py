"""Exercise the bundled feature catalog through MCP with deliberately absent native workers."""
import argparse
import hashlib
import json
import pathlib

from smoke_mcp import Client, ROOT
from smoke_package import verify_catalog


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/feature-catalog-live.json")
    args = parser.parse_args()
    if args.report.exists():
        parser.error("Evidence report already exists; select a fresh --report path.")
    missing = ROOT / "artifacts/catalog-test-workers-intentionally-absent.exe"
    assert not missing.exists()
    report = {"completed": False, "nativeWorkersAvailable": False, "steps": [],
              "harnessSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              "managedBinaryHashes": {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest()
                                       for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}}
    client = Client(args.host, 30, ["--worker", str(missing), "--sim-worker", str(missing)])
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-catalog-contract", "version": "0.3.0"}})
        client.notify("notifications/initialized")
        tools = client.request("tools/list")
        tool_names = {tool["name"] for tool in tools["tools"]}
        assert not any(name.startswith("qualification_") for name in tool_names), "Qualification tool leaked into a normal Host"
        rows = []
        offset = 0
        while True:
            page = client.tool("feature_catalog", {"offset": offset, "maxItems": 7})
            assert page["targetAvailabilityProbed"] is False
            assert len(page["features"]) <= 7
            rows.extend(page["features"])
            if page["pagination"]["complete"]:
                break
            offset = page["pagination"]["nextOffset"]
        assert len(rows) == len({row["featureId"] for row in rows}) == page["pagination"]["total"]
        assert tool_names <= {row["featureId"] for row in rows}
        report["steps"].append({"name": "catalog pagination without native workers", "featureCount": len(rows), "toolCount": len(tool_names)})
        external_catalog = json.loads((ROOT / 'contracts/feature-catalog.v1.json').read_text(encoding='utf-8'))
        report['steps'].append({'name':'package catalog comparison without native workers',
                                **verify_catalog(client, external_catalog, tool_names)})
        backlog = client.tool("feature_catalog", {"implementationStatus": "not_implemented", "maxItems": 200})
        assert backlog["features"] and all(not row["mcpTools"] for row in backlog["features"])
        report["steps"].append({"name": "unimplemented backlog has no callable bindings", "count": len(backlog["features"])})
        startup = client.tool("feature_catalog", {"featureId": "sim_state"})
        assert startup["features"][0]["qualification"]["status"] == "unverified"
        evidence = {row['id']: row for row in startup['evidence']}
        assert evidence['local.sim.startup.v02']['status'] == 'failed'
        assert evidence['local.sim.startup.20261024']['status'] == 'passed'
        assert evidence['local.sim.startup.20261024']['profile']['actions'] == ['powerOn', 'powerOff']
        report["steps"].append({"name": "scoped empty startup success and historical failure do not qualify all CPU controls"})
        for arguments, code in [({"featureId": "invented"}, "FeatureNotFound"), ({"maxItems": 0}, "InvalidRequest")]:
            response = client.request("tools/call", {"name": "feature_catalog", "arguments": arguments})
            assert response.get("isError")
            assert response["structuredContent"]["error"]["code"] == code
            report["steps"].append({"name": "rejected " + code})
        report["completed"] = True
        print(f"PASS feature_catalog through MCP: {len(rows)} features, {len(tool_names)} tools, no native worker dependency.")
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error))
    finally:
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["completed"] = report["completed"] and client.process.returncode == 0
        args.report.parent.mkdir(parents=True, exist_ok=True)
        with args.report.open("x", encoding="utf-8") as destination:
            destination.write(json.dumps(report, indent=2))
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
