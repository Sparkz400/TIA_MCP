"""Read-only capability qualification in a fresh archive-retrieved repository scratch project."""
import argparse
import hashlib
import json
import pathlib
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT


def canonical_xml(xml):
    root = ET.fromstring(xml)
    for info in list(root):
        if info.tag.rsplit("}", 1)[-1] == "DocumentInfo":
            root.remove(info)
    return hashlib.sha256(ET.tostring(root, encoding="utf-8")).hexdigest()


def stable(value):
    if isinstance(value, dict):
        return {key: stable(item) for key, item in value.items() if key not in {"observedAtUtc", "processId"}}
    if isinstance(value, list):
        return [stable(item) for item in value]
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--journal", type=pathlib.Path, default=ROOT / "artifacts/changes/change-85a07d54ead94b2a8a257a5dde0fe2a5/journal.json")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/target-capabilities-live.json")
    args = parser.parse_args()
    journal_path = args.journal.resolve()
    if not journal_path.is_relative_to((ROOT / "artifacts/changes").resolve()):
        raise ValueError("Only a repository scratch change journal can supply a validation archive.")
    journal = json.loads(journal_path.read_text(encoding="utf-8-sig"))
    assert pathlib.Path(journal["projectPath"]).name.startswith("TiaMcpScratch_"), "Only task-created scratch projects are allowed."
    archive = pathlib.Path(journal["recoveryArchive"]).resolve()
    assert archive.is_relative_to((ROOT / "artifacts/changes").resolve()) and archive.suffix == ".zap21"
    expected = journal["recoveryArchiveSha256"]
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == expected
    report = {"schemaVersion": 1, "completed": False, "steps": [], "limitations": [], "sourceJournal": str(journal_path)}
    report_path = args.report.resolve()
    assert report_path.is_relative_to((ROOT / "artifacts").resolve())

    def persist():
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    client = Client(ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe", 300)

    def call(name, arguments=None, label=None):
        value = client.tool(name, arguments)
        report["steps"].append({"name": label or name, "tool": name, "result": value})
        persist()
        print("PASS " + (label or name), flush=True)
        return value

    def probe(selectors, label):
        value = call("target_capabilities", {"selectors": selectors}, label)
        assert value["schemaVersion"] == 1
        assert set(value["identity"]) == {"projectPath", "plcPath", "scopePath", "objectPath", "kind", "name"}
        ids = [feature["featureId"] for feature in value["features"]]
        assert len(ids) == len(set(ids)), "Duplicate feature IDs"
        for feature in value["features"]:
            assert feature["qualification"] == {"status": "unverified", "evidenceIds": []}
            assert feature["api"]["status"] in {"available", "unavailable", "unknown"}
            assert feature["implementation"]["status"] in {"implemented", "partial", "not_implemented"}
            assert feature["availability"]["status"] in {"available", "conditional", "blocked", "unsupported", "unknown"}
        assert value["metadata"]["attributeValuesRead"] is False
        assert value["completeness"]["visitedMetadataItems"] <= selectors.get("maxItems", 500)
        return value

    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-target-capabilities-qualification", "version": "1.0"}})
        client.notify("notifications/initialized")
        tools = client.request("tools/list")
        assert {"target_capabilities", "object_resolve"}.issubset({tool["name"] for tool in tools["tools"]})
        call("tia_start")
        retrieved = call("project_retrieve", {"archivePath": str(archive), "expectedSha256": expected})
        assert retrieved["retrieved"] is True
        report["project"] = retrieved["project"]
        before_inventory = call("project_inventory", {"maxItems": 2000}, "inventory before discovery")
        before_software = call("software_inspect", {"maxItems": 2000}, "software before discovery")
        before_hardware = call("hardware_inspect", {"maxItems": 2000}, "hardware before discovery")
        block = next(block for block in before_inventory["blocks"] if block["name"] == "TiaMcp_EnableGate")
        before_export = call("block_read", {"blockPath": block["path"]}, "native block before discovery")
        project_cap = probe({"kind": "project", "maxItems": 500}, "project capability metadata")
        plc_cap = probe({"kind": "plc", "maxItems": 500}, "PLC service observations")
        block_cap = probe({"kind": "block", "objectPath": block["path"], "maxItems": 500}, "SCL block capabilities")
        features = {feature["featureId"]: feature for feature in block_cap["features"]}
        assert features["change.graphical_operand_set"]["availability"]["status"] == "unsupported"
        assert features["change.source_upsert"]["availability"]["status"] == "conditional"
        for language in ("LAD", "FBD", "STL"):
            selected = next((item for item in before_inventory["blocks"] if item["programmingLanguage"] == language), None)
            if selected:
                result = probe({"blockPath": selected["path"], "maxItems": 500}, language + " block capabilities")
                indexed = {feature["featureId"]: feature for feature in result["features"]}
                if language in {"LAD", "FBD"}:
                    assert indexed["change.graphical_operand_set"]["availability"]["status"] == "conditional"
                    assert indexed["change.source_upsert"]["availability"]["status"] == "unsupported"
            else:
                report["limitations"].append("No " + language + " block present in the selected checkpoint.")
        hardware_caps = []
        for item in before_inventory["hardware"]:
            hardware_caps.append(probe({"hardwarePath": item["path"], "maxItems": 500}, "hardware " + item["name"]))
        network_states = {feature["api"]["status"] for value in hardware_caps for feature in value["features"] if feature["featureId"] == "change.network_configure"}
        assert "unavailable" in network_states and "available" in network_states, network_states
        bounded = probe({"kind": "project", "maxItems": 1}, "bounded project metadata")
        assert bounded["completeness"]["truncated"] is True
        assert bounded["completeness"]["visitedMetadataItems"] == 1
        for collection, kind in (("types", "type"), ("tagTables", "tagTable"), ("technologyObjects", "technology")):
            values = before_software.get(collection, [])
            if values:
                probe({"kind": kind, "objectPath": values[0]["path"], "maxItems": 500}, kind + " metadata")
            else:
                report["limitations"].append("No " + kind + " object present in the selected checkpoint.")
        generated = next((item for item in before_inventory["blocks"] if "/systemGroups/" in item["path"]), None)
        if generated:
            result = probe({"blockPath": generated["path"], "maxItems": 500}, "generated block mutation boundary")
            indexed = {feature["featureId"]: feature for feature in result["features"]}
            assert indexed["change.block_delete"]["availability"]["status"] == "blocked"
        else:
            report["limitations"].append("No generated system-group block present; generated-object boundary not qualified by this run.")
        if not any(item.get("isKnowHowProtected") for item in before_inventory["blocks"]):
            report["limitations"].append("No know-how-protected block present; protected-block behavior not qualified by this run.")
        if not any(scope["kind"] == "safetyUnit" for scope in before_software["softwareScopes"]):
            report["limitations"].append("No safety unit present; safety-unit scope boundary not qualified by this run.")
        safety = next(feature for feature in plc_cap["features"] if feature["featureId"] == "safety_inspect")
        report["observedSafetyApi"] = safety["api"]["status"]
        if safety["api"]["status"] == "unavailable":
            assert safety["availability"]["status"] == "unsupported"
        after_inventory = call("project_inventory", {"maxItems": 2000}, "inventory after discovery")
        after_software = call("software_inspect", {"maxItems": 2000}, "software after discovery")
        after_hardware = call("hardware_inspect", {"maxItems": 2000}, "hardware after discovery")
        after_export = call("block_read", {"blockPath": block["path"]}, "native block after discovery")
        assert stable(before_inventory) == stable(after_inventory), "Inventory changed during capability observations"
        assert stable(before_software) == stable(after_software), "Software metadata changed during capability observations"
        assert stable(before_hardware) == stable(after_hardware), "Hardware metadata changed during capability observations"
        assert canonical_xml(before_export["xml"]) == canonical_xml(after_export["xml"]), "Native block content changed"
        assert hashlib.sha256(archive.read_bytes()).hexdigest() == expected, "Source archive changed"
        report["preservation"] = {"inventory": True, "softwareMetadata": True, "hardwareMetadata": True,
                                  "sampleNativeBlockSemanticSha256": canonical_xml(after_export["xml"]), "sourceArchiveSha256": expected,
                                  "limit": "Observable metadata plus one native block export; not a full project binary equivalence proof."}
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error), flush=True)
    finally:
        try:
            call("tia_detach")
        except Exception as error:
            report["limitations"].append("Detach: " + str(error))
        client.close()
        report["hostExitCode"] = client.process.returncode
        report["stderrTail"] = client.errors[-20:]
        persist()
        print("Report: " + str(report_path), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
