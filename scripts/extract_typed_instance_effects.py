"""Retain independently observed native caller/instance DB before-after pairs."""
import argparse
import hashlib
import json
from pathlib import Path
from xml.etree import ElementTree as ET

from smoke_mcp import ROOT


def extract(before_path, after_path, route="standard", prefix="Tgp_", namespace=""):
    suffix = "" if route == "standard" else "-" + route
    directory = ROOT / ("fixtures/typed-graphical/native/instance-effects" + suffix)
    directory.mkdir(exist_ok=True)
    result = {"schemaVersion": 1, "builderUsed": False, "nativeImportCompileVerified": True,
              "engineeringVerified": False, "preservationVerified": False, "persistenceVerified": False,
              "recoveryVerified": False, "runtimeVerified": False, "memberName": "Child",
              "tiaVersion": "V21", "route": route, "artifacts": {}, "sources": {}}
    if namespace:
        result["namespace"] = namespace
    documents = {}
    for phase, report_path in (("before", before_path), ("after", after_path)):
        report = json.loads(report_path.read_text())
        assert report["completed"] and report["builderUsed"] is False
        # One owned capture may hold both phases: the before source first, the redeclaring after source last.
        entries = [e for e in report["fixtures"] if prefix + "InstanceEffect" in e["names"]]
        assert len(entries) == (2 if before_path == after_path else 1), "Ambiguous instance-effect phases in one report."
        reference = entries[0] if phase == "before" else entries[-1]
        authored_path = Path(reference["sourcePath"])
        assert hashlib.sha256(authored_path.read_text(encoding="utf-8-sig").encode()).hexdigest() == reference["sha256"]
        result["sources"][phase] = {"path": authored_path.relative_to(ROOT).as_posix(),
            "sha256": hashlib.sha256(authored_path.read_bytes()).hexdigest(),
            "transportSha256": reference["sha256"], "changeId": reference["changeId"]}
        if "scratch" in report:
            result["cpuFamily"] = report["scratch"]["selectedCpu"]["typeIdentifier"]
        else:
            cpu = next(item for item in report["hardware"]["items"] if item.get("classification") == "CPU")
            result["cpuFamily"] = next(a["value"] for a in cpu["attributes"] if a["name"] == "TypeIdentifier")
        applied_index = next(i for i, c in enumerate(report["calls"]) if c["tool"] == "change_apply" and
                             c.get("status") == "passed" and c["arguments"]["changeId"] == reference["changeId"])
        for kind, name in (("caller", prefix + "InstanceEffect"), ("instance", prefix + "InstanceEffect_DB")):
            record = next(c["result"] for c in report["calls"][applied_index + 1:] if c["tool"] == "block_read" and c.get("status") == "passed" and c["result"]["block"]["name"] == name)
            raw = Path(record["artifact"]["path"]).read_bytes()
            assert hashlib.sha256(raw).hexdigest() == record["artifact"]["sha256"]
            target = directory / (phase + "-" + kind + ".xml")
            target.write_bytes(raw)
            documents[(phase, kind)] = ET.fromstring(raw)
            result["artifacts"][phase + kind.title()] = {"path": target.relative_to(ROOT).as_posix(),
                "sha256": hashlib.sha256(raw).hexdigest(), "captureReport": report_path.relative_to(ROOT).as_posix()}
    # Independent acceptance expectation: adding Child keeps all existing native
    # DB contents intact, including the explicitly assigned 42 start value.
    after = documents[("after", "instance")]
    before = documents[("before", "instance")]
    for document in (before, after):
        marker = next(e for e in document.iter() if e.attrib.get("Name") == "RetainedMarker")
        assert next(e.text for e in marker if e.tag.rsplit("}", 1)[-1] == "StartValue") == "42"
    child = next(e for e in after.iter() if e.attrib.get("Name") == "Child")
    assert not any(e.attrib.get("Name") == "Child" for e in before.iter())
    parent = next(e for e in after.iter() if child in list(e))
    parent.remove(child)
    def structural(node):
        return (node.tag, sorted(node.attrib.items()), (node.text or "").strip(),
                [structural(e) for e in node if e.tag != "DocumentInfo"])
    assert structural(before) == structural(after), "Native DB effects include more than the observed appended member."
    result["nativeObservedEffect"] = {"appendedStaticMember": "Child", "existingFullDbPayloadPreserved": True,
        "explicitStartValuePreserved": {"RetainedMarker": "42"}}
    callee = ROOT / ("fixtures/typed-graphical/native/" + prefix + "TemplateFB_LAD.xml")
    result["referenceCallee"] = {"path": callee.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(callee.read_bytes()).hexdigest()}
    target = ROOT / ("fixtures/typed-graphical/instance-effects" + suffix + ".v1.json")
    target.write_text(json.dumps(result, indent=2) + "\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--route", choices=("standard", "softwareUnit", "namespace"), default="standard")
    parser.add_argument("--prefix", default="Tgp_")
    parser.add_argument("--namespace", default="")
    args = parser.parse_args()
    extract(args.before.resolve(), args.after.resolve(), args.route, args.prefix, args.namespace)
