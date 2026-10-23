"""Negative-only native checks for namespace case aliases in an existing isolated fixture."""
import argparse
import json
import pathlib
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from validate_object_model import semantic_xml


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture-report", type=pathlib.Path, required=True)
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/object-model-namespace-guards.json")
    args = parser.parse_args()
    fixture = json.loads(args.fixture_report.read_text(encoding="utf-8"))
    assert fixture["completed"]
    project = pathlib.Path(fixture["recoveredProjectPath"]).resolve()
    assert project.is_relative_to(ROOT / "artifacts") and project.name.startswith("TiaMcpScratch_") and project.suffix == ".ap21"
    client = Client(ROOT / "src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe", 300,
                    ["--worker", str(ROOT / "src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe")])
    report = {"schemaVersion": 1, "completed": False, "projectPath": str(project), "steps": []}

    def persist():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def call(name, arguments=None):
        value = client.tool(name, arguments)
        report["steps"].append({"name": name, "result": value})
        persist()
        print("PASS " + name, flush=True)
        return value

    def reject(kind, payload, label):
        raw = client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": kind, "payload": payload}})
        value = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        assert raw.get("isError") and value["error"]["code"] == "NamespaceMismatch", value
        report["steps"].append({"name": label, "result": value})
        persist()
        print("PASS " + label, flush=True)

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "namespace-guard-check", "version": "1"}})
        client.notify("notifications/initialized")
        call("tia_start")
        call("project_open", {"path": str(project)})
        inventory = call("software_inspect", {"maxItems": 2000})
        a = next(block for block in inventory["blocks"] if block["name"] == "TiaMcp_IsEnabled_AChanged")
        b_scope = next(scope["path"] for scope in inventory["softwareScopes"] if scope["name"] == "TiaMcp_UnitB")
        block = call("block_read", {"blockPath": a["path"]})
        before = semantic_xml(block["xml"])
        source = 'NAMESPACE tiamcp_unita\nFUNCTION "TiaMcp_IsEnabled_AChanged" : Bool\nVAR_INPUT\n Enable : Bool;\nEND_VAR\nBEGIN\n #TiaMcp_IsEnabled_AChanged := #Enable;\nEND_FUNCTION\nEND_NAMESPACE\n'
        reject("source_upsert", {"scopePath": a["scopePath"], "source": source, "language": "SCL", "expectedBlockNames": [a["name"]]}, "same-scope source namespace case alias rejected")
        reject("source_upsert", {"scopePath": b_scope, "source": source, "language": "SCL", "expectedBlockNames": [a["name"]]}, "cross-scope source namespace case alias rejected")
        document = ET.fromstring(block["xml"])
        document.find("SW.Blocks.FC/AttributeList/Namespace").text = "tiamcp_unita"
        reject("block_import_xml", {"scopePath": a["scopePath"], "xml": ET.tostring(document, encoding="unicode"), "expectedBlockNames": [a["name"]]}, "scope-only XML namespace case alias rejected")
        unit_type = next(item for item in inventory["types"] if item["name"] == "TiaMcp_UnitData")
        original_type = call("type_read", {"typePath": unit_type["path"]})
        original_type_xml = pathlib.Path(original_type["artifact"]["path"]).read_text(encoding="utf-8-sig")
        type_source = 'NAMESPACE tiamcp_unita\nTYPE "TiaMcp_UnitData"\nVERSION : 0.1\nSTRUCT\n Enabled : Bool;\nEND_STRUCT;\nEND_TYPE\nEND_NAMESPACE\n'
        reject("type_source_upsert", {"scopePath": a["scopePath"], "source": type_source, "expectedTypeNames": ["TiaMcp_UnitData"]}, "type source namespace case alias rejected")
        assert semantic_xml(call("block_read", {"blockPath": a["path"]})["xml"]) == before
        after_type = call("type_read", {"typePath": unit_type["path"]})
        after_type_xml = pathlib.Path(after_type["artifact"]["path"]).read_text(encoding="utf-8-sig")
        assert semantic_xml(after_type_xml) == semantic_xml(original_type_xml)
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        try:
            call("tia_detach")
        finally:
            client.close()
            report["hostExitCode"] = client.process.returncode
            persist()


if __name__ == "__main__":
    main()
