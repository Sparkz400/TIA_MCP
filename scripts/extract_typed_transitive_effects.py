"""Retain the independently observed transitive dependent-instance effect (author_transitive_effect_sources.py).

The native rule this capture must show: every member typed as the changed FB, at any depth of a parent FB or instance DB,
gains each new member at the end of its nested Static section, shaped like the direct instance-DB member without its
top-level AttributeList, Remanence and Accessibility. Everything else, start values included, stays identical.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
from xml.etree import ElementTree as ET

from smoke_mcp import ROOT

EFFECT, PARENT, GRAND = "Tgp_NestEffect", "Tgp_NestParent", "Tgp_NestGrand"
DEPENDENTS = (PARENT, GRAND, PARENT + "_DB", GRAND + "_DB")
NESTED_ONLY = ("Remanence", "Accessibility")


def local(node):
    return node.tag.rsplit("}", 1)[-1]


def structural(node):
    return (local(node), sorted(node.attrib.items()), (node.text or "").strip(), [structural(e) for e in node if local(e) != "DocumentInfo"])


def nested_shape(member):
    """The captured nested form of a direct instance-DB member."""
    result = copy.deepcopy(member)
    for name in NESTED_ONLY:
        result.attrib.pop(name, None)
    for child in [c for c in result if local(c) == "AttributeList"]:
        result.remove(child)
    return result


def static_of(member):
    sections = [s for s in member if local(s) == "Sections"]
    assert len(sections) == 1, "A multi-instance member needs exactly one nested Sections element."
    statics = [s for s in sections[0] if local(s) == "Section" and s.get("Name") == "Static"]
    assert len(statics) == 1, "A nested multi-instance needs exactly one Static section."
    return statics[0]


def verify(documents, appended):
    """documents: {(phase, block): root}. Returns the number of nested hosts per dependent."""
    direct_before, direct_after = documents[("before", EFFECT + "_DB")], documents[("after", EFFECT + "_DB")]
    direct_static = [s for s in direct_after.iter() if local(s) == "Section" and s.get("Name") == "Static"][0]
    direct = {m.get("Name"): m for m in direct_static if local(m) == "Member" and m.get("Name") in appended}
    assert list(direct) == list(appended) and [m.get("Name") for m in direct_static][-len(appended):] == list(appended), \
        "The direct instance DB must append exactly the new members."
    for member in direct.values():
        direct_static.remove(member)
    assert structural(direct_before) == structural(direct_after), "The direct instance DB changed beyond the appended members."
    hosts = {}
    for name in DEPENDENTS:
        before, after = documents[("before", name)], documents[("after", name)]
        found = [m for m in after.iter() if local(m) == "Member" and m.get("Datatype") == '"' + EFFECT + '"']
        assert found, name + " has no nested multi-instance of the changed FB."
        for host in found:
            static = static_of(host)
            tail = [m for m in static if local(m) == "Member"][-len(appended):]
            assert [m.get("Name") for m in tail] == list(appended), name + ": new members are not appended to the nested Static section."
            for member in tail:
                assert structural(member) == structural(nested_shape(direct[member.get("Name")])), \
                    name + "/" + member.get("Name") + ": nested shape differs from the direct member without top-level attributes."
                static.remove(member)
        assert structural(before) == structural(after), name + " changed beyond the nested appended members."
        hosts[name] = len(found)
    return hosts


def extract(capture_path):
    report = json.loads(capture_path.read_text(encoding="utf-8"))
    assert report["completed"] and report["builderUsed"] is False and len(report["fixtures"]) == 2
    packet = json.loads((ROOT / "fixtures/typed-graphical/route-capture-transitive-effect-standard-packet.v1.json").read_text(encoding="utf-8"))
    appended = tuple(m["name"] for m in packet["members"])
    directory = ROOT / "fixtures/typed-graphical/native/instance-effects-transitive"
    directory.mkdir(exist_ok=True)
    result = {"schemaVersion": 1, "builderUsed": False, "nativeImportCompileVerified": True, "engineeringVerified": False,
              "preservationVerified": False, "persistenceVerified": False, "recoveryVerified": False, "runtimeVerified": False,
              "tiaVersion": "V21", "route": "standard", "changedFb": EFFECT, "chain": packet["chain"],
              "cpuFamily": report["scratch"]["selectedCpu"]["typeIdentifier"] if "scratch" in report else next(
                  a["value"] for item in report["hardware"]["items"] if item.get("classification") == "CPU"
                  for a in item["attributes"] if a["name"] == "TypeIdentifier"),
              "artifacts": {}, "sources": {}}
    documents = {}
    for phase, entry in zip(("before", "after"), report["fixtures"]):
        source = ROOT / entry["sourcePath"]
        result["sources"][phase] = {"path": source.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                                    "transportSha256": entry["sha256"], "changeId": entry["changeId"]}
        for export in entry["exports"]:
            assert export["changedExisting"] is (phase == "after")
            raw = Path(export["path"]).read_bytes()
            assert hashlib.sha256(raw).hexdigest() == export["sha256"]
            target = directory / (phase + "-" + export["name"] + ".xml")
            target.write_bytes(raw)
            documents[(phase, export["name"])] = ET.fromstring(raw)
            result["artifacts"][phase + ":" + export["name"]] = {"path": target.relative_to(ROOT).as_posix(),
                "sha256": hashlib.sha256(raw).hexdigest(), "captureReport": capture_path.relative_to(ROOT).as_posix()}
    hosts = verify(documents, appended)
    result["nativeObservedEffect"] = {"appendedMembers": [dict(m) for m in packet["members"]], "nestedHostsPerDependent": hosts,
        "rule": "Each member typed as the changed FB, at any depth of a parent FB or instance DB, appends every new member to its "
                "nested Static section in the direct instance-DB shape without the top-level AttributeList, Remanence and Accessibility.",
        "existingFullPayloadPreserved": True}
    target = ROOT / "fixtures/typed-graphical/instance-effects-transitive.v1.json"
    target.write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path, help="capture.json of the transitive effect packet")
    print(json.dumps(extract(parser.parse_args().capture.resolve())["nativeObservedEffect"]["nestedHostsPerDependent"]))
