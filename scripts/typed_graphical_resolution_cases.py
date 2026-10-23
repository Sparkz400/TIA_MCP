"""Bind authored resolution cases to exact observed identities; never start TIA.

The output is an MCP request packet, not a qualification report. Native gates
remain pending until an owned scratch run retains its actual tool responses.
"""
import argparse
import copy
import json
import pathlib
from xml.etree import ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "fixtures/typed-graphical/resolution-packet.v1.json"


def exact(rows, path):
    matches = [row for row in rows if row["path"] == path]
    if len(matches) != 1:
        raise ValueError("An exact observed identity is missing or ambiguous: " + str(path))
    return matches[0]


def bind_case(case, observations):
    inventory, bindings, exports = (observations[name] for name in ("inventory", "bindings", "exports"))
    coverage = inventory.get("completeness", {})
    if coverage.get("scopeComplete") is not True or coverage.get("truncated") is not False:
        raise ValueError("A complete software_inspect identity inventory is required.")
    caller_path = bindings[case.get("callerRole", "caller")]
    caller = exact(inventory["blocks"], caller_path)
    xml = exports[caller_path]
    if "<!DOCTYPE" in xml.upper() or "<!ENTITY" in xml.upper():
        raise ValueError("DTD/entity declarations are forbidden in native observations.")
    document = ET.fromstring(xml)
    blocks = [node for node in document if node.tag in ("SW.Blocks.FC", "SW.Blocks.FB")]
    if len(blocks) != 1 or blocks[0].findtext("AttributeList/Name") != caller["name"]:
        raise ValueError("The caller export does not match the exact observed block identity.")
    units = [node for node in document.iter() if node.tag == "SW.Blocks.CompileUnit"]
    if not units:
        raise ValueError("The caller has no observed native network.")
    unit = units[0]
    language = unit.findtext("AttributeList/ProgrammingLanguage")
    if language not in ("LAD", "FBD") or not unit.get("ID"):
        raise ValueError("An exact LAD/FBD native network identity is required.")
    operand = copy.deepcopy(case["operand"])
    owner_role = operand.pop("ownerRole", None)
    if owner_role:
        path = bindings[owner_role]
        owners = inventory["tagTables"] if operand["scope"] == "tag" else inventory["blocks"]
        exact(owners, path)
        operand["ownerPath"] = path
    operand["key"] = "input"
    output = {"key": "output", "scope": "local", "components": case.get("outputComponents", ["OutputBool"])}
    nodes = [{"key": "output", "kind": "assign", "operand": "output"}]
    wires = [{"from": {"node": "$input", "port": "value"}, "to": {"node": "output", "port": "in"}}]
    if language == "LAD":
        nodes = [{"key": "rail", "kind": "powerrail"}, {"key": "input", "kind": "contact", "operand": "input"},
                 {"key": "output", "kind": "coil", "operand": "output"}]
        wires = [{"from": {"node": "rail", "port": "out"}, "to": {"node": "input", "port": "in"}},
                 {"from": {"node": "input", "port": "out"}, "to": {"node": "output", "port": "in"}}]
    model = {"schemaVersion": 1, "operation": "replaceNetwork", "language": language, "networkId": unit.get("ID"),
             "nodes": nodes, "operands": [operand, output], "connections": wires}
    expectation = copy.deepcopy(case["expect"])
    if expectation["outcome"] == "prepared":
        owner = operand.get("ownerPath", caller_path)
        expectation["operandIdentity"] = owner + "::" + "".join(str(len(part.encode("utf-16-le")) // 2) + ":" + part for part in operand["components"])
        expectation["typeDependencyPaths"] = []
        for role in expectation.pop("typeDependencyRoles", []):
            path = bindings[role]
            exact(inventory["types"], path)
            expectation["typeDependencyPaths"].append(path)
    return {"id": case["id"], "tool": "change_prepare", "arguments": {"kind": "graphical_typed_edit",
            "payload": {"blockPath": caller_path, "model": model}}, "expect": expectation,
            "scopePath": caller["scopePath"], "prerequisites": case.get("prerequisites", []), "executed": False}


def packet(manifest, observations, identifiers):
    if len(identifiers) != len(set(identifiers)):
        raise ValueError("Case IDs must be unique.")
    cases = {case["id"]: case for case in manifest["cases"]}
    if len(cases) != len(manifest["cases"]):
        raise ValueError("Manifest contains duplicate case IDs.")
    return {"schemaVersion": 1, "status": "prepared-not-executed",
            "engineeringVerified": False, "preservationVerified": False, "persistenceVerified": False,
            "recoveryVerified": False, "runtimeVerified": False,
            "cases": [bind_case(cases[identifier], observations) for identifier in identifiers]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observations", type=pathlib.Path, required=True)
    parser.add_argument("--case", action="append", required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    result = packet(json.loads(MANIFEST.read_bytes()), json.loads(args.observations.read_bytes()), args.case)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
