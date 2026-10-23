"""Compare native unit creation AutoNumber flags through the MCP change lifecycle."""
import argparse
import hashlib
import json
import pathlib
import uuid
from xml.dom import minidom

from block_test_runtime import NativeAudit, canonical, utc_now
from smoke_mcp import Client, ROOT


class Audit(NativeAudit):
    ALLOWED_TOOLS = NativeAudit.ALLOWED_TOOLS | {"project_retrieve", "software_inspect", "change_restore"}


def main(args):
    output = args.output.resolve()
    output.mkdir(exist_ok=False)
    report = {"schemaVersion": 1, "startedAtUtc": utc_now(), "builderUsed": False, "calls": [], "cases": [], "completed": False}
    def persist():
        (output / "report.json").write_text(canonical(report))
    client = Client(args.host.resolve(), 300, ["--worker", str(args.worker.resolve()), "--artifacts", str(output)])
    call = Audit(client, report["calls"], persist)
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "native-unit-creation-probe", "version": "1"}})
        client.notify("notifications/initialized")
        report["started"] = call("tia_start")
        assert report["started"]["ownsPortalProcess"]
        archive = args.archive.resolve()
        assert archive.is_relative_to((ROOT / "artifacts").resolve())
        assert hashlib.sha256(archive.read_bytes()).hexdigest() == args.sha256
        report["retrieved"] = call("project_retrieve", {"archivePath": str(archive), "expectedSha256": args.sha256})
        inventory = call("software_inspect", {"maxItems": 2000})
        scope = next(s["path"] for s in inventory["softwareScopes"] if s["name"] == "TiaMcp_UnitA" and s["kind"] == "softwareUnit")
        used = {b["number"] for b in inventory["blocks"]}
        for ordinal, (kind, autonumber) in enumerate((("FC", True), ("FC", False), ("FB", True), ("FB", False))):
            number = 500 + ordinal
            assert number not in used
            name = "Tgp_Probe_" + kind + "_" + str(autonumber)
            source = ROOT / "fixtures/typed-graphical/native" / ("TgpUnit_Template" + kind + "_LAD.xml")
            document = minidom.parseString(source.read_bytes())
            block = document.getElementsByTagName("SW.Blocks." + kind)[0]
            attrs = next(e for e in block.childNodes if e.nodeType == e.ELEMENT_NODE and e.tagName == "AttributeList")
            for key, value in (("Name", name), ("Number", str(number)), ("AutoNumber", str(autonumber).lower())):
                field = next(e for e in attrs.childNodes if e.nodeType == e.ELEMENT_NODE and e.tagName == key)
                while field.firstChild:
                    field.removeChild(field.firstChild)
                field.appendChild(document.createTextNode(value))
            xml = document.toxml()
            case = {"name": name, "source": str(source), "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                    "autoNumber": autonumber, "requestedNumber": number, "accepted": False}
            report["cases"].append(case)
            prepared = call("change_prepare", {"kind": "block_import_xml", "payload": {"scopePath": scope,
                "namespace": "TiaMcp_UnitA", "xml": xml, "expectedBlockNames": [name]}})
            case["prepared"] = prepared
            try:
                case["applied"] = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                    "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
                exported = call("block_read", {"blockName": name, "scopePath": scope})
                case["native"] = {"block": exported["block"], "artifact": exported["artifact"]}
                case["accepted"] = True
                print("ACCEPTED " + name, flush=True)
            except Exception as error:
                case["failure"] = str(error)
                case["status"] = call("change_status", {"changeId": prepared["changeId"]})
                case["restored"] = call("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
                print("REJECTED " + name, flush=True)
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        try:
            report["detach"] = call("tia_detach")
        finally:
            client.close()
            report["hostExitCode"] = client.process.returncode
            persist()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("host", "worker", "output", "archive"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    parser.add_argument("--sha256", required=True)
    main(parser.parse_args())
