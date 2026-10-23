"""Author typed requests independently from the native serializer under test."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"


def edge(source, target):
    def endpoint(text):
        node, _, port = text.partition(":")
        return {"node": node, "port": port or ("value" if node.startswith("$") else None)}
    return {"from": endpoint(source), "to": endpoint(target)}


def authored_case(language, family, native_suffix, operands, nodes, wires, instances=()):
    return {"fixtureId": "typed-" + family + "-" + language.lower(), "family": family, "language": language,
        "cpuTypeIdentifier": CPU, "blockName": "Tgp_" + native_suffix + "_" + language,
        "referenceXml": "fixtures/typed-graphical/native/Tgp_" + native_suffix + "_" + language + ".xml",
        "interfaceDependencies": ["TiaMcp_EnableGate"] if family == "fb-multi" else [],
        "payload": {"model": {"schemaVersion": 1, "language": language, "operation": "replaceNetwork", "networkId": "$first",
            "operands": [{"key": name, "scope": "local", "components": [name]} for name in operands],
            "nodes": nodes, "connections": [edge(a, b) for a, b in wires]}, "instances": list(instances)}}


def cases():
    result = []
    for language in ("LAD", "FBD"):
        lad = language == "LAD"
        rail = [{"key": "rail", "kind": "powerrail"}] if lad else []
        assign = "coil" if lad else "assign"
        if lad:
            nodes = rail + [{"key": "a", "kind": "contact", "operand": "A"},
                           {"key": "b", "kind": "contact", "operand": "B"},
                           {"key": "join", "kind": "branch", "inputCount": 2},
                           {"key": "inhibit", "kind": "contact", "operand": "Inhibit", "negated": True},
                           {"key": "active", "kind": "coil", "operand": "Active"}]
            wires = [("rail:out", "a:in"), ("rail:out", "b:in"), ("a:out", "join:in1"), ("b:out", "join:in2"),
                     ("join:out", "inhibit:in"), ("inhibit:out", "active:in")]
        else:
            nodes = [{"key": "join", "kind": "or", "inputCount": 2}, {"key": "invert", "kind": "not"},
                     {"key": "gate", "kind": "and", "inputCount": 2}, {"key": "active", "kind": "assign", "operand": "Active"}]
            wires = [("$A", "join:in1"), ("$B", "join:in2"), ("$Inhibit", "invert:in1"),
                     ("join:out", "gate:in1"), ("invert:out", "gate:in2"), ("gate:out", "active:in")]
        result.append(authored_case(language, "boolean", "Boolean", ("A", "B", "Inhibit", "Active"), nodes, wires))
        for comparison in ("eq", "ne", "lt", "le", "gt", "ge"):
            nodes = rail + [{"key": "compare", "kind": "compare", "comparison": comparison, "dataType": "Int"},
                            {"key": "result", "kind": assign, "operand": "Result"}]
            wires = [("$Left", "compare:in1"), ("$Right", "compare:in2"), ("compare:out", "result:in")]
            if lad:
                wires.insert(0, ("rail:out", "compare:pre"))
            result.append(authored_case(language, comparison, comparison.title(), ("Left", "Right", "Result"), nodes, wires))
        for instruction in ("TON", "TOF", "TP", "CTU", "CTD", "CTUD"):
            timer = instruction in ("TON", "TOF", "TP")
            first = "IN" if timer else "CD" if instruction == "CTD" else "CU"
            output = "QU" if instruction == "CTUD" else "Q"
            nodes = rail + ([{"key": "enable", "kind": "contact", "operand": "Enable"}] if lad else [])
            nodes += [{"key": "instruction", "kind": "timer" if timer else "counter", "instruction": instruction,
                       "dataType": "Time" if timer else "Int", "instance": "storage"},
                      {"key": "done", "kind": assign, "operand": "Done"}]
            operands = ["Enable", "Preset", "Done", "Elapsed" if timer else "Count"]
            wires = [("enable:out" if lad else "$Enable", "instruction:" + first),
                     ("$Preset", "instruction:" + ("PT" if timer else "PV")),
                     ("instruction:" + output, "done:in"),
                     ("instruction:" + ("ET" if timer else "CV"), "$Elapsed" if timer else "$Count")]
            if lad:
                wires.insert(0, ("rail:out", "enable:in"))
            for variable, port in {"CTU": [("Reset", "R")], "CTD": [("Load", "LD")],
                    "CTUD": [("Down", "CD"), ("Reset", "R"), ("Load", "LD")]}.get(instruction, []):
                operands.append(variable)
                wires.append(("$" + variable, "instruction:" + port))
            if instruction == "CTUD":
                operands.append("Lower")
                wires.append(("instruction:QD", "$Lower"))
            result.append(authored_case(language, instruction.lower(), instruction, operands, nodes, wires,
                [{"key": "storage", "kind": "existingMulti", "components": ["Storage"]}]))
        for kind in ("fc", "fb-global", "fb-multi"):
            fc = kind == "fc"
            node = {"key": "call", "kind": "call", "calleePath": "$block:" + ("TiaMcp_IsEnabled" if fc else "TiaMcp_EnableGate")}
            instances = []
            if not fc:
                node["instance"] = "instance"
                instances = [{"key": "instance", "kind": "existingGlobal", "blockPath": "$block:TiaMcp_EnableGate_DB"}] if kind == "fb-global" else [
                    {"key": "instance", "kind": "existingMulti", "components": ["Child"]}]
            wires = [("rail:out" if lad else "$True", "call:en"), ("$Enable", "call:Enable"),
                     ("call:" + ("Ret_Val" if fc else "Active"), "$Active")]
            case = authored_case(language, kind, "FC" if fc else "FB_" + kind.split("-")[1], ("Enable", "Active"), rail + [node], wires, instances)
            if not lad:
                case["payload"]["model"]["operands"].append({"key": "True", "scope": "constant", "dataType": "Bool", "value": "true"})
            result.append(case)
    return result


if __name__ == "__main__":
    (ROOT / "cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": cases()}, indent=2) + "\n", encoding="utf-8")
