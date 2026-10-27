"""Audit staged typed profile candidates against their native sources before activation.

Independent of the extractor's code: each source must equal its capture export and
native read receipt, each profile fragment must structurally equal an element of its
native export, and every route/language/CPU/qualification binding must hold.
Re-exported prerequisites are compared with the committed exports they replace.
"""
import argparse
import hashlib
import json
import pathlib

from run_conformance import safe_xml

ROOT = pathlib.Path(__file__).resolve().parents[1]
GATES = ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified")


def sha(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def local(tag):
    return tag.rsplit("}", 1)[-1]


def shape(element, ignore=()):
    """Structure, attributes and stripped text by local name; namespace prefixes do not matter."""
    if local(element.tag) in ignore:
        return None
    children = tuple(child for child in (shape(child, ignore) for child in element) if child is not None)
    return (local(element.tag), tuple(sorted((local(k), v) for k, v in element.attrib.items() if not k.startswith("{http://www.w3.org/2000/xmlns/"))),
            (element.text or "").strip(), children)


def parse(path):
    return safe_xml(pathlib.Path(path).read_text(encoding="utf-8-sig"))


def without_auto_numbers(document):
    """Drop a block's Number only where the block lets TIA allocate it (AutoNumber true)."""
    for attributes in document.iter():
        if local(attributes.tag) != "AttributeList":
            continue
        children = {local(child.tag): child for child in attributes}
        if "Number" in children and (children.get("AutoNumber") is not None and children["AutoNumber"].text == "true"):
            attributes.remove(children["Number"])
    return document


def elements(document):
    return {shape(node) for node in document.iter()}


def audit(catalog_path, capture_path, prerequisites=()):
    catalog = json.loads(pathlib.Path(catalog_path).read_bytes())
    # One or more capture reports of the same CPU; a source re-admitted in a later run must be identical.
    captures = [json.loads(pathlib.Path(path).read_bytes()) for path in (capture_path if isinstance(capture_path, (list, tuple)) else [capture_path])]
    findings = []
    def check(condition, message):
        if not condition:
            findings.append(message)
    fixtures, receipts, cpus = {}, set(), set()
    for capture in captures:
        for entry in capture["fixtures"]:
            fixtures.setdefault(pathlib.Path(entry["sourcePath"]).resolve(), []).append(entry)
        receipts |= {call["result"]["artifact"]["sha256"] for call in capture["calls"]
                     if call["tool"] == "block_read" and call.get("status") == "passed"}
        cpus |= {a["value"] for item in capture["hardware"]["items"] if item.get("classification") == "CPU"
                 for a in item["attributes"] if a["name"] == "TypeIdentifier"}
    check(len(cpus) == 1, "capture reports disagree on the CPU")
    cpu = next(iter(cpus))
    sources = {}
    for source in catalog["sources"]:
        path, authored = ROOT / source["path"], (ROOT / source["authoredSourcePath"]).resolve()
        check(sha(path) == source["sha256"], "source hash: " + source["path"])
        check(sha(authored) == source["authoredSourceSha256"], "authored hash: " + source["authoredSourcePath"])
        entry = next((e for e in fixtures.get(authored, []) if e.get("changeId") == source["changeId"]), None)
        check(entry is not None and entry.get("changeId") == source["changeId"] and entry.get("engineeringVerified") is True,
              "capture entry: " + source["path"])
        check(entry is not None and source["sha256"] in {export["sha256"] for export in entry.get("exports", [])}, "capture export: " + source["path"])
        check(source["sha256"] in receipts, "native read receipt: " + source["path"])
        check(source["builderUsed"] is False and source["runtimeVerified"] is False, "source flags: " + source["path"])
        sources[source["sha256"]] = (source, entry)
    for profile in catalog["profiles"]:
        name = profile["id"]
        source, entry = sources.get(profile["sourceSha256"], (None, None))
        check(source is not None and source["path"] == profile["provenance"], "profile source binding: " + name)
        if source is None:
            continue
        document = parse(ROOT / profile["provenance"])
        native = elements(document)
        check(shape(safe_xml(profile["nativeTemplateXml"])) in native, "instruction fragment: " + name)
        unit = next(node for node in document.iter() if local(node.tag) == "SW.Blocks.CompileUnit")
        check(shape(safe_xml(profile["networkTemplateXml"])) == shape(unit), "network envelope: " + name)
        check(profile["route"] == entry["route"] and profile["cpuFamily"] == cpu, "route/cpu: " + name)
        languages = {node.text for node in document.iter() if local(node.tag) == "ProgrammingLanguage"}
        check(profile["language"] in languages, "language: " + name)
        check(all(profile[gate] is False for gate in GATES) and profile["nativeFormatValidated"] is True, "qualification flags: " + name)
        if "globalInstanceTemplateXml" in profile:
            check(profile["globalInstanceSourceSha256"] in sources, "global storage source: " + name)
            storage = ROOT / sources.get(profile["globalInstanceSourceSha256"], ({"path": ""}, None))[0]["path"]
            check(storage.is_file() and shape(safe_xml(profile["globalInstanceTemplateXml"])) == shape(parse(storage)), "global storage document: " + name)
    base = pathlib.Path(catalog_path).resolve().parent
    for collection in ("creationTemplates", "globalInstanceTemplates", "multiInstanceTemplates"):
        for template in catalog.get(collection, []):
            check(sha(base / template["path"]) == template["sha256"] and template["sha256"] in sources, "template: " + template["id"])
            if "referenceCalleePath" in template:
                check(sha(base / template["referenceCalleePath"]) == template["referenceCalleeSha256"]
                      and template["referenceCalleeSha256"] in sources, "reference callee: " + template["id"])
    check(not catalog.get("unresolvedTemplates"), "unresolved templates remain")
    equivalence = []
    for committed in prerequisites:
        staged = base / "native" / pathlib.Path(committed).name
        # Re-export differs only in culture of empty text items, export metadata and TIA-allocated block numbers.
        same = (shape(without_auto_numbers(parse(staged)), ignore=("DocumentInfo", "Culture"))
                == shape(without_auto_numbers(parse(committed)), ignore=("DocumentInfo", "Culture")))
        check(same, "prerequisite re-export differs from committed export: " + str(committed))
        equivalence.append({"committed": str(pathlib.Path(committed).as_posix()), "committedSha256": sha(committed),
                            "reexport": str(staged.relative_to(ROOT).as_posix()), "reexportSha256": sha(staged), "equivalent": same})
    return {"schemaVersion": 1, "catalog": str(pathlib.Path(catalog_path).resolve().relative_to(ROOT).as_posix()), "catalogSha256": sha(catalog_path),
            **({"capture": str(pathlib.Path(capture_path).resolve().relative_to(ROOT).as_posix()), "captureSha256": sha(capture_path)}
               if not isinstance(capture_path, (list, tuple)) else
               {"captures": [{"capture": str(pathlib.Path(path).resolve().relative_to(ROOT).as_posix()), "captureSha256": sha(path)} for path in capture_path]}),
            "counts": {"sources": len(catalog["sources"]), "profiles": len(catalog["profiles"]),
                       "templates": sum(len(catalog.get(k, [])) for k in ("creationTemplates", "globalInstanceTemplates", "multiInstanceTemplates"))},
            "prerequisiteEquivalence": equivalence, "findings": findings, "passed": not findings}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=pathlib.Path, required=True)
    parser.add_argument("--capture", type=pathlib.Path, action="append", required=True, help="Capture report; repeat for several runs of one CPU.")
    parser.add_argument("--prerequisite", type=pathlib.Path, action="append", default=[], help="Committed export a staged re-export must match.")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    result = audit(args.catalog, args.capture[0] if len(args.capture) == 1 else args.capture, args.prerequisite)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": result["passed"], "findings": result["findings"][:10], **result["counts"]}))
    raise SystemExit(0 if result["passed"] else 1)
