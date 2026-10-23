"""Typed scalar-call requests bound to independently imported SIMATIC SD callers."""
import json
from pathlib import Path
from author_cases import CPU, edge

ROOT = Path(__file__).resolve().parent


def cases():
    result = []
    for language in ("LAD", "FBD"):
        for kind in ("FC", "FB"):
            name = "Tgp_Numeric" + kind
            fc = kind == "FC"
            parameters = ("Value", "Result", "Returned", "Accumulator") if fc else ("Enable", "Delta", "Reset", "Accumulator", "Total")
            node = {"key": "call", "kind": "call", "calleePath": "$block:" + name}
            nodes = [{"key": "rail", "kind": "powerrail"}] if language == "LAD" else []
            instances = []
            if not fc:
                node["instance"] = "child"
                instances = [{"key": "child", "kind": "existingMulti", "components": ["Child"]}]
            nodes.append(node)
            wires = [("rail:out" if language == "LAD" else "$True", "call:en")]
            wires += [("$Value", "call:Value"), ("$Accumulator", "call:Accumulator"), ("call:Result", "$Result"), ("call:Ret_Val", "$Returned")] if fc else [
                ("$Enable", "call:Enable"), ("$Delta", "call:Delta"), ("$Reset", "call:Reset"), ("$Accumulator", "call:Accumulator"), ("call:Total", "$Total")]
            operands = [{"key": p, "scope": "local", "components": [p]} for p in parameters]
            if language == "FBD":
                operands.append({"key": "True", "scope": "constant", "dataType": "Bool", "value": "true"})
            result.append({"fixtureId": "typed-numeric-" + kind.lower() + "-" + language.lower(), "family": "numeric-" + kind.lower(),
                "language": language, "cpuTypeIdentifier": CPU, "blockName": name + "_" + language,
                "referenceXml": "fixtures/typed-graphical/native/" + name + "_" + language + ".xml",
                "dependencyXmls": [{"path": "fixtures/typed-graphical/native/" + name + ".xml", "names": [name]}],
                "interfaceDependencies": [name] if not fc else [], "runtimeInstances": ["dut"] if fc else ["left", "right"],
                "payload": {"model": {"schemaVersion": 1, "language": language, "operation": "replaceNetwork", "networkId": "$first",
                    "nodes": nodes, "operands": operands, "connections": [edge(a, b) for a, b in wires]}, "instances": instances}})
    return result


if __name__ == "__main__":
    (ROOT / "numeric-cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": cases()}, indent=2) + "\n", encoding="utf-8")
