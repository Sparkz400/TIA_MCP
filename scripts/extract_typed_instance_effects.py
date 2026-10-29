"""Retain independently observed native caller/instance DB before-after pairs."""
import argparse
import hashlib
import json
from pathlib import Path
from xml.etree import ElementTree as ET

from smoke_mcp import ROOT


# IEC effect pairs append one static member of each IEC instance type (author_iec_effect_sources.py).
IEC_MEMBERS = ("Timer_TON", "Timer_TOF", "Timer_TP", "Counter_CTU", "Counter_CTD", "Counter_CTUD")
STANDARD_IEC_MEMBERS = IEC_MEMBERS + ("Timer_TONR", "Edge_RTRIG", "Edge_FTRIG")


def checkout_relative(path):
    """The path inside the checkout that holds it: this one, a frozen build, or the main checkout's artifacts."""
    return path.relative_to(next(p for p in path.parents if (p / "TiaMcp.slnx").exists())).as_posix()


def structural(node):
    return (node.tag, sorted(node.attrib.items()), (node.text or "").strip(), [structural(e) for e in node if e.tag != "DocumentInfo"])


def member_shapes(document, names):
    return {name: structural(next(e for e in document.iter() if e.attrib.get("Name") == name)) for name in names}


def extract(before_path, after_path, route="standard", prefix="Tgp_", namespace="", iec=False):
    suffix = "" if route == "standard" else "-" + route
    stem, effect, appended = (("instance-effects-iec", "IecEffect", STANDARD_IEC_MEMBERS if route == "standard" else IEC_MEMBERS) if iec
                              else ("instance-effects", "InstanceEffect", ("Child",)))
    manifest = ROOT / ("fixtures/typed-graphical/" + stem + suffix + ".v1.json")
    # A recapture that adds members must reproduce every deployed member exactly, so qualified types keep their effect.
    deployed = json.loads(manifest.read_text()) if iec and manifest.exists() else None
    if deployed:
        deployed = {kind: (ET.fromstring((ROOT / deployed["artifacts"]["after" + kind]["path"]).read_bytes()), list(deployed["members"].values()))
                    for kind in ("Caller", "Instance")}
    directory = ROOT / ("fixtures/typed-graphical/native/" + stem + suffix)
    directory.mkdir(exist_ok=True)
    result = {"schemaVersion": 1, "builderUsed": False, "nativeImportCompileVerified": True,
              "engineeringVerified": False, "preservationVerified": False, "persistenceVerified": False,
              "recoveryVerified": False, "runtimeVerified": False, **({} if iec else {"memberName": "Child"}),
              "tiaVersion": "V21", "route": route, "artifacts": {}, "sources": {}}
    if namespace:
        result["namespace"] = namespace
    documents = {}
    for phase, report_path in (("before", before_path), ("after", after_path)):
        report = json.loads(report_path.read_text())
        assert report["completed"] and report["builderUsed"] is False
        # One owned capture may hold both phases: the before source first, the redeclaring after source last.
        entries = [e for e in report["fixtures"] if prefix + effect in e["names"]]
        assert len(entries) == (2 if before_path == after_path else 1), "Ambiguous instance-effect phases in one report."
        reference = entries[0] if phase == "before" else entries[-1]
        # A capture may run from a frozen build of this commit; its source is the same committed file here.
        authored_path = ROOT / checkout_relative(Path(reference["sourcePath"]))
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
        for kind, name in (("caller", prefix + effect), ("instance", prefix + effect + "_DB")):
            record = next(c["result"] for c in report["calls"][applied_index + 1:] if c["tool"] == "block_read" and c.get("status") == "passed" and c["result"]["block"]["name"] == name)
            raw = Path(record["artifact"]["path"]).read_bytes()
            assert hashlib.sha256(raw).hexdigest() == record["artifact"]["sha256"]
            target = directory / (phase + "-" + kind + ".xml")
            target.write_bytes(raw)
            documents[(phase, kind)] = ET.fromstring(raw)
            result["artifacts"][phase + kind.title()] = {"path": target.relative_to(ROOT).as_posix(),
                "sha256": hashlib.sha256(raw).hexdigest(), "captureReport": checkout_relative(report_path)}
    for kind, (document, names) in (deployed or {}).items():
        assert member_shapes(document, names) == member_shapes(documents[("after", kind.lower())], names), \
            "Recaptured " + kind.lower() + " members differ from the deployed IEC effect."
    # Independent acceptance expectation: adding the new members keeps all existing native
    # DB contents intact, including the explicitly assigned 42 start value.
    after = documents[("after", "instance")]
    before = documents[("before", "instance")]
    for document in (before, after):
        marker = next(e for e in document.iter() if e.attrib.get("Name") == "RetainedMarker")
        assert next(e.text for e in marker if e.tag.rsplit("}", 1)[-1] == "StartValue") == "42"
    for name in appended:
        child = next(e for e in after.iter() if e.attrib.get("Name") == name)
        assert not any(e.attrib.get("Name") == name for e in before.iter())
        parent = next(e for e in after.iter() if child in list(e))
        parent.remove(child)
    assert structural(before) == structural(after), "Native DB effects include more than the observed appended members."
    result["nativeObservedEffect"] = {**({"appendedStaticMembers": list(appended)} if iec else {"appendedStaticMember": "Child"}),
        "existingFullDbPayloadPreserved": True, "explicitStartValuePreserved": {"RetainedMarker": "42"}}
    if iec:
        caller = documents[("after", "caller")]
        types = {e.attrib["Name"]: e.attrib["Datatype"] for e in caller.iter() if e.attrib.get("Name") in appended and "Datatype" in e.attrib}
        assert set(types) == set(appended) and len(set(types.values())) == len(appended), "Every IEC member needs one distinct native type."
        result["members"] = {datatype: name for name, datatype in types.items()}
    else:
        callee = ROOT / ("fixtures/typed-graphical/native/" + prefix + "TemplateFB_LAD.xml")
        result["referenceCallee"] = {"path": callee.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(callee.read_bytes()).hexdigest()}
    manifest.write_text(json.dumps(result, indent=2) + "\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--route", choices=("standard", "softwareUnit", "namespace"), default="standard")
    parser.add_argument("--prefix", default="Tgp_")
    parser.add_argument("--namespace", default="")
    parser.add_argument("--iec", action="store_true", help="Extract the IEC instance-type member pair instead of the FB Child pair.")
    args = parser.parse_args()
    extract(args.before.resolve(), args.after.resolve(), args.route, args.prefix, args.namespace, args.iec)
