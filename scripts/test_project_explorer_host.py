"""Actual stdio MCP capability negotiation and read-only explorer adapter checks; no TIA startup."""
import argparse
import json
import pathlib
from smoke_mcp import Client, ROOT


def check(host, report):
    results = []
    for enabled in (False, True):
        client = Client(host, 60)
        try:
            capabilities = {"extensions": {"io.modelcontextprotocol/ui": {"mimeTypes": ["text/html;profile=mcp-app"]}}} if enabled else {}
            client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": capabilities, "clientInfo": {"name": "explorer-host-contract-test", "version": "1"}})
            client.notify("notifications/initialized")
            tools = client.request("tools/list")["tools"]
            explorer = [t for t in tools if t["name"].startswith("project_model")]
            assert len(explorer) == 9
            assert all(t["annotations"]["readOnlyHint"] and not t["annotations"]["destructiveHint"] for t in explorer)
            capture = next(t for t in explorer if t["name"] == "project_model_capture")
            assert (capture.get("_meta", {}).get("ui", {}).get("resourceUri") == "ui://tia-project-explorer/view.html") == enabled
            resources = client.request("resources/list")["resources"]
            assert any(r["uri"] == "ui://tia-project-explorer/view.html" for r in resources) == enabled
            for action in ("change_apply", "start", "simulation_download", "unknown"):
                result = client.request("tools/call", {"name": "project_model", "arguments": {"request": {"action": action}}})
                assert result.get("isError") and result["structuredContent"]["error"]["code"] == "InvalidRequest"
            if enabled:
                content = client.request("resources/read", {"uri": "ui://tia-project-explorer/view.html"})["contents"][0]
                assert content["mimeType"] == "text/html;profile=mcp-app"
                assert "__EXPLORER_" not in content["text"] and "__PROJECT_MODEL_JSON__" not in content["text"]
                assert "ui/initialize" in content["text"]
                assert content["_meta"]["ui"]["csp"]["connectDomains"] == []
                assert all(t.get("_meta", {}).get("ui", {}).get("visibility") == ["model"] for t in tools if not t["name"].startswith("project_model") and t["name"] != "artifact_read")
            else:
                try:
                    client.request("resources/read", {"uri": "ui://tia-project-explorer/view.html"})
                    raise AssertionError("Unnegotiated resource read must fail")
                except RuntimeError:
                    pass
            results.append({"mcpAppsNegotiated": enabled, "passed": True, "explorerTools": len(explorer), "mutatingActionsRejected": 4})
        finally:
            client.close()
    output = {"schemaVersion": 1, "completed": True, "hostPath": str(host), "results": results,
              "limitations": "Protocol conformance against this test host only; does not certify every desktop MCP Apps host or native capture."}
    report.write_text(json.dumps(output, indent=2), encoding="utf-8")
    print(json.dumps(output, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-host-tests.json")
    args = parser.parse_args()
    check(args.host.resolve(), args.report.resolve())
