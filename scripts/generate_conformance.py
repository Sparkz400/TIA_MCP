"""Generate deterministic fixture manifests. No TIA or simulator process is started."""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = ROOT / "artifacts/conformance/generated"
LANGUAGES = {"SCL", "STL", "LAD", "FBD"}
ALLOWED_TOOLS = {"fixture_sources", "change_prepare", "change_apply", "block_read", "block_interface",
                 "type_read", "call_graph", "change_restore", "change_status"}


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n"


def reference(step, pointer):
    return {"$ref": step + pointer}


def check(path, value, operation="equals"):
    return {"path": path, "op": operation, "value": value}


def step(identifier, tool, arguments, assertions=None, evidence="observation"):
    return {"stepId": identifier, "tool": tool, "arguments": arguments,
            "expect": {"assertions": assertions or [], "evidenceKind": evidence}}


def change_steps(identifier, kind, payload):
    prepare = identifier + "_prepare"
    return [step(prepare, "change_prepare", {"kind": kind, "payload": payload},
                 [check("/changeId", None, "nonempty"), check("/previewHash", None, "nonempty")]),
            step(identifier + "_apply", "change_apply", {
                "changeId": reference(prepare, "/changeId"), "previewHash": reference(prepare, "/previewHash"),
                "idempotencyKey": {"$runKey": identifier}, "checkpointSave": True, "compile": True, "save": True},
                [check("/state", "applied"), check("/compile/errorCount", 0)], "native_compile")]


def load_specifications(prefix):
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,19}", prefix):
        raise ValueError("Prefix must be 1..20 ASCII identifier characters, beginning with a letter.")
    path = ROOT / "fixtures/catalog.py"
    spec = importlib.util.spec_from_file_location("tia_conformance_catalog", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.specifications(prefix)


def build_manifest(specification):
    language = specification["language"]
    steps = []
    features = {"change_prepare", "change_apply", "block_read", "block_interface"}
    if specification.get("typeSource"):
        features.update(("change.type_source_upsert", "type_read"))
        steps.extend(change_steps("types", "type_source_upsert", {
            "source": specification["typeSource"], "expectedTypeNames": specification["typeNames"]}))
        for index, name in enumerate(specification["typeNames"]):
            steps.append(step("type_" + str(index), "type_read", {"typeName": name},
                              [check("/sections", {"path": ["Samples"], "dataType": "Array[-2..2] of Int"}, "member"),
                               check("/sections", {"path": ["Meta", "CountValue"], "dataType": "DInt"}, "member")], "native_interface"))
    if specification.get("graphical"):
        features.update(("fixture_sources", "change.block_import_xml", "change.graphical_operand_set"))
        steps.append(step("fixture", "fixture_sources", {"language": language, "blockName": specification["names"][0]},
                          [check("/language", language), check("/kind", "block_import_xml")]))
        steps.extend(change_steps("program", "block_import_xml", reference("fixture", "/payload")))
    else:
        features.add("change.source_upsert")
        steps.extend(change_steps("program", "source_upsert", {
            "language": language, "source": specification["source"], "expectedBlockNames": specification["names"]}))
    for index, name in enumerate(specification["names"]):
        # DB exports use programmingLanguage=DB; verify language only for executable blocks.
        is_db = specification.get("source", "").lstrip().startswith("DATA_BLOCK")
        assertions = [check("/block/name", name), check("/xml", None, "nonempty")]
        if not is_db:
            assertions.append(check("/block/programmingLanguage", language))
        steps.append(step("read_" + str(index), "block_read", {"blockName": name}, assertions, "native_export"))
        steps.append(step("interface_" + str(index), "block_interface", {"blockName": name},
                          [check("/sections", None, "nonempty")], evidence="native_interface"))
    if specification.get("callEdge"):
        features.add("call_graph")
        steps.append(step("call_graph", "call_graph", {"maxItems": 2000, "includeXmlEvidence": True, "maxXmlBlocks": 25},
                          [check("/edges", specification["callEdge"], "callEdge")], "native_reference"))
    if specification.get("graphical"):
        steps.extend(change_steps("operand", "graphical_operand_set", {
            "blockName": specification["names"][0],
            "networkId": {"$xmlAccess": {"stepId": "read_0", "symbol": "Enable", "attribute": "networkId"}},
            "accessUid": {"$xmlAccess": {"stepId": "read_0", "symbol": "Enable", "attribute": "accessUid"}},
            "components": ["EnableAlternate"]}))
        steps.append(step("edited_read", "block_read", {"blockName": specification["names"][0]},
                          [check("/xml", "EnableAlternate", "symbolAccess"),
                           check("/xml", {"baselineStep": "read_0", "from": "Enable", "to": "EnableAlternate"}, "operandPreserved")], "native_preservation"))
    return {
        "schemaVersion": 1, "fixtureId": specification["fixtureId"], "title": specification["title"],
        "featureIds": sorted(features),
        "profile": {"tiaVersion": 21, "cpuFamilies": ["S7-1500"] if language == "STL" else ["S7-1200", "S7-1500"],
                    "languages": [language], "requires": ["TIA.Openness.V21", "owned-scratch-project", *specification.get("requires", [])]},
        "steps": steps, "expectedBehavior": specification["expectedBehavior"],
        "coverage": {"qualification": "unverified", "engineering": "source/import, compile and native export",
                     "instructions": specification["instructions"], "runtime": "specified_not_executed",
                     "preservation": ("Whole native XML including interfaces, comments, titles, network structure and unknown elements; excludes export DocumentInfo provenance only."
                                      if specification.get("graphical") else "Native exports retained; no general edit-preservation claim."),
                     "limitations": (["Only the built-in one-input network and existing symbolic operand substitution are covered; arbitrary graphical wiring is not covered."]
                                     if specification.get("graphical") else [])}}


def validate_manifest(manifest):
    required = {"schemaVersion", "fixtureId", "title", "featureIds", "profile", "steps", "expectedBehavior", "coverage"}
    if set(manifest) != required or manifest.get("schemaVersion") != 1:
        raise ValueError("Invalid fixture schema or top-level fields.")
    if not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", manifest["fixtureId"]):
        raise ValueError("Invalid fixtureId.")
    if not manifest["featureIds"] or len(manifest["featureIds"]) != len(set(manifest["featureIds"])):
        raise ValueError("featureIds must be unique and nonempty.")
    profile = manifest["profile"]
    if profile.get("tiaVersion") != 21 or not set(profile.get("languages", [])) <= LANGUAGES or not profile.get("languages"):
        raise ValueError("Invalid target profile.")
    if not profile.get("cpuFamilies") or not isinstance(profile.get("requires"), list):
        raise ValueError("Incomplete target profile.")
    if not isinstance(manifest["expectedBehavior"], dict) or not manifest["expectedBehavior"]:
        raise ValueError("Independent expected behavior is required.")
    seen = set()
    def references(value):
        if isinstance(value, dict):
            if "$ref" in value and (set(value) != {"$ref"} or value["$ref"].split("/", 1)[0] not in seen):
                raise ValueError("Reference must identify an earlier step.")
            if "$xmlAccess" in value and value["$xmlAccess"].get("stepId") not in seen:
                raise ValueError("XML access must identify an earlier step.")
            for child in value.values():
                references(child)
        elif isinstance(value, list):
            for child in value:
                references(child)
    if not manifest["steps"]:
        raise ValueError("Fixture has no steps.")
    for item in manifest["steps"]:
        if set(item) != {"stepId", "tool", "arguments", "expect"} or item["stepId"] in seen:
            raise ValueError("Invalid or duplicate step.")
        if item["tool"] not in ALLOWED_TOOLS or not isinstance(item["arguments"], dict):
            raise ValueError("Tool is outside the fixture runner's offline allowlist.")
        references(item["arguments"])
        for assertion in item["expect"].get("assertions", []):
            if set(assertion) != {"path", "op", "value"} or assertion["op"] not in {
                "equals", "nonempty", "contains", "callEdge", "symbolAccess", "operandPreserved", "member"}:
                raise ValueError("Unsupported expectation.")
        seen.add(item["stepId"])
    return manifest


def generate(output=DEFAULT_OUTPUT, prefix="Tcf", selected=None):
    specifications = load_specifications(prefix)
    available = {item["fixtureId"] for item in specifications}
    if selected and set(selected) - available:
        raise ValueError("Unknown fixtures: " + ", ".join(sorted(set(selected) - available)))
    output = pathlib.Path(output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    entries = []
    for specification in specifications:
        if selected and specification["fixtureId"] not in selected:
            continue
        manifest = validate_manifest(build_manifest(specification))
        path = output / (manifest["fixtureId"] + ".json")
        encoded = canonical(manifest).encode("utf-8")
        path.write_bytes(encoded)
        sources = []
        for key, suffix in (("typeSource", ".udt.scl"), ("source", ".scl" if specification["language"] == "SCL" else ".awl")):
            if key in specification:
                source_path = output / (manifest["fixtureId"] + suffix)
                source_path.write_bytes(specification[key].encode("utf-8"))
                sources.append({"path": source_path.name, "sha256": hashlib.sha256(source_path.read_bytes()).hexdigest()})
        entries.append({"fixtureId": manifest["fixtureId"], "path": path.name,
                        "sha256": hashlib.sha256(encoded).hexdigest(), "sources": sources})
    index = {"schemaVersion": 1, "generator": "tia-mcp-conformance/1", "prefix": prefix, "fixtures": entries}
    (output / "index.json").write_text(canonical(index), encoding="utf-8")
    return index


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=pathlib.Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--prefix", default="Tcf")
    parser.add_argument("--fixture", action="append", help="Repeat to generate a subset.")
    args = parser.parse_args()
    index = generate(args.output, args.prefix, args.fixture)
    print(f"Generated {len(index['fixtures'])} fixtures in {args.output.resolve()}")


if __name__ == "__main__":
    main()
