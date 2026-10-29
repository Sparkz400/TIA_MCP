"""Wave 9 typed families: handwritten SIMATIC SD references, their standard-route capture packet and typed cases.

DInt/Real comparisons, MOVE and ADD/SUB/MUL/DIV on Int/DInt/Real, TONR and R_TRIG/F_TRIG with local and global
IEC storage, LAD and FBD, for CPU 1511 V4.1. The repository had no SIMATIC SD for MOVE, arithmetic or edge
boxes, so those sources stay unverified until the probe in docs/verification/typed-wave9-families-runbook.md
accepts them. Writes sources/, wave9-capture-packet.v1.json and wave9-cases.v1.json; enables nothing.
Rerun after editing.
"""
import copy
import hashlib
import json
from pathlib import Path

from author_cases import CPU, authored_case
from author_extended_cases import binding, transitive_hosts
from author_iec_effect_sources import ARCHIVE, ARCHIVE_SHA256
from author_reference_sources import source

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
COMPARISONS = ("Eq", "Ne", "Lt", "Le", "Gt", "Ge")
ARITHMETIC = ("ADD", "SUB", "MUL", "DIV")
EDGES = ("R_TRIG", "F_TRIG")
# One dedicated global DB per instruction, so no committed reference is re-exported or shared.
STORAGE = {"TONR": ("Tgp_GlobalTonr", "IEC_TIMER"), "R_TRIG": ("Tgp_GlobalRTrig", "R_TRIG"), "F_TRIG": ("Tgp_GlobalFTrig", "F_TRIG")}
TRUE = {"key": "True", "scope": "constant", "dataType": "Bool", "value": "true"}
HOST = "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe"
WORKER = "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe"
CAPTURE, PROBE, STAGE = "artifacts/w9c", "artifacts/w9p", "artifacts/w9s"  # short: TIA limits project paths to 143 characters


def slug(instruction):
    return instruction.lower().replace("_", "")


def var(section, members):
    return section + "\n" + "".join("  " + name + " : " + data_type + ";\n" for name, data_type in members) + "END_VAR"


def storage_source(instruction):
    name, system = STORAGE[instruction]
    if instruction in EDGES:
        # SCL DATA_BLOCKs of R_TRIG/F_TRIG vanish after compile and save (wave 10 native slot); instance_db_create builds them.
        return "global-" + slug(instruction) + "-storage.instancedb.json", name, json.dumps({"name": name, "instanceOfName": system}) + "\n"
    return ("global-" + slug(instruction) + "-storage.scl", name,
            'DATA_BLOCK "' + name + '"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"' + system + '"\nBEGIN\nEND_DATA_BLOCK\n')


def references():
    """(file name, block name, text); each storage DB before its global callers. The edge storage DBs follow the
    first multi-instance use, which makes TIA copy the R_TRIG/F_TRIG system FB into the project."""
    rows = [storage_source("TONR")]
    for language in ("LAD", "FBD"):
        lad = language == "LAD"
        plain = "RUNG wire#powerrail" if lad else "RUNG"
        # Boxes with EN take the power flow in LAD and the TRUE constant in FBD, like the captured calls.
        enabled = "RUNG wire#powerrail" if lad else "RUNG TRUE"
        start = "RUNG wire#powerrail\n  Contact( #Enable )" if lad else "RUNG\n  #Enable"

        def add(file, block, declarations, body):
            rows.append((file + "-" + language.lower() + ".s7dcl", block + "_" + language, source(block + "_" + language, language, declarations, body)))
        for data_type in ("DInt", "Real"):
            for op in COMPARISONS:
                add(op.lower() + "-" + data_type.lower(), "Tgp_" + op + data_type,
                    var("VAR_INPUT", (("Left", data_type), ("Right", data_type))) + "\n" + var("VAR_OUTPUT", (("Result", "Bool"),)),
                    plain + '\n  { S7_Templates := "SrcType := ' + data_type + '" }\n  ' + op + "( in1 := #Left, in2 := #Right )\n  Coil( #Result )\nEND_RUNG")
        for data_type in ("Int", "DInt", "Real"):
            add("move-" + data_type.lower(), "Tgp_Move" + data_type,
                var("VAR_INPUT", (("Source", data_type),)) + "\n" + var("VAR_OUTPUT", (("Target", data_type),)),
                enabled + "\n  Move( in := #Source, out1 => #Target )\nEND_RUNG")
            for op in ARITHMETIC:
                add(op.lower() + "-" + data_type.lower(), "Tgp_" + op.title() + data_type,
                    var("VAR_INPUT", (("Left", data_type), ("Right", data_type))) + "\n" + var("VAR_OUTPUT", (("Result", data_type),)),
                    enabled + '\n  { S7_Templates := "SrcType := ' + data_type + '" }\n  ' + op.title() + "( in1 := #Left, in2 := #Right, out => #Result )\nEND_RUNG")
        timer = var("VAR_INPUT", (("Enable", "Bool"), ("Reset", "Bool"), ("Preset", "Time"))) + "\n" + var("VAR_OUTPUT", (("Done", "Bool"), ("Elapsed", "Time")))
        body = start + '\n  { S7_Templates := "time_type := Time" }\n  %s.TONR( r := #Reset, pt := #Preset, et => #Elapsed )\n  Coil( #Done )\nEND_RUNG'
        add("tonr", "Tgp_TONR", timer + "\n" + var("VAR", (("Storage", "TONR_TIME"),)), body % "#Storage")
        add("tonr-global", "Tgp_TONR_Global", timer, body % ('"' + STORAGE["TONR"][0] + '"'))
        for instruction in EDGES:
            declarations = var("VAR_INPUT", (("Signal", "Bool"),)) + "\n" + var("VAR_OUTPUT", (("Pulse", "Bool"),))
            body = enabled + "\n  %s." + instruction + "( clk := #Signal, q => #Pulse )\nEND_RUNG"
            add(slug(instruction), "Tgp_" + instruction, declarations + "\n" + var("VAR", (("Storage", instruction),)), body % "#Storage")
            if lad:
                rows.append(storage_source(instruction))
            add(slug(instruction) + "-global", "Tgp_" + instruction + "_Global", declarations, body % ('"' + STORAGE[instruction][0] + '"'))
    return rows


def cases():
    result = []
    multi = {"key": "storage", "kind": "existingMulti", "components": ["Storage"]}
    for language in ("LAD", "FBD"):
        lad = language == "LAD"
        rail = [{"key": "rail", "kind": "powerrail"}] if lad else []
        assign = "coil" if lad else "assign"

        def boxed(family, suffix, operands, node, wires, instances=()):
            case = authored_case(language, family, suffix, operands, rail + [node],
                                 [("rail:out" if lad else "$True", "instruction:en")] + wires, instances)
            if not lad:
                case["payload"]["model"]["operands"].append(dict(TRUE))
            return case
        for data_type in ("DInt", "Real"):
            for op in COMPARISONS:
                nodes = rail + [{"key": "compare", "kind": "compare", "comparison": op.lower(), "dataType": data_type},
                                {"key": "result", "kind": assign, "operand": "Result"}]
                wires = ([("rail:out", "compare:pre")] if lad else []) + [("$Left", "compare:in1"), ("$Right", "compare:in2"), ("compare:out", "result:in")]
                result.append(authored_case(language, op.lower() + "-" + data_type.lower(), op + data_type, ("Left", "Right", "Result"), nodes, wires))
        for data_type in ("Int", "DInt", "Real"):
            result.append(boxed("move-" + data_type.lower(), "Move" + data_type, ("Source", "Target"),
                                {"key": "instruction", "kind": "move", "dataType": data_type},
                                [("$Source", "instruction:in"), ("instruction:out1", "$Target")]))
            for op in ARITHMETIC:
                result.append(boxed(op.lower() + "-" + data_type.lower(), op.title() + data_type, ("Left", "Right", "Result"),
                                    {"key": "instruction", "kind": "math", "instruction": op, "dataType": data_type},
                                    [("$Left", "instruction:in1"), ("$Right", "instruction:in2"), ("instruction:out", "$Result")]))
        nodes = rail + ([{"key": "enable", "kind": "contact", "operand": "Enable"}] if lad else []) + [
            {"key": "instruction", "kind": "timer", "instruction": "TONR", "dataType": "Time", "instance": "storage"},
            {"key": "done", "kind": assign, "operand": "Done"}]
        wires = ([("rail:out", "enable:in"), ("enable:out", "instruction:IN")] if lad else [("$Enable", "instruction:IN")]) + [
            ("$Reset", "instruction:R"), ("$Preset", "instruction:PT"), ("instruction:Q", "done:in"), ("instruction:ET", "$Elapsed")]
        result.append(authored_case(language, "tonr", "TONR", ("Enable", "Reset", "Preset", "Done", "Elapsed"), nodes, wires, [multi]))
        for instruction in EDGES:
            result.append(boxed(slug(instruction), instruction, ("Signal", "Pulse"),
                                {"key": "instruction", "kind": "edge", "instruction": instruction, "instance": "storage"},
                                [("$Signal", "instruction:CLK"), ("instruction:Q", "$Pulse")], [multi]))
    # Existing global storage: engineering only, like the TON/CTU existing-global cases.
    for original in [case for case in result if case["family"] in ("tonr", "rtrig", "ftrig")]:
        case = copy.deepcopy(original)
        instruction = next(node["instruction"] for node in case["payload"]["model"]["nodes"] if node["key"] == "instruction")
        storage, block = STORAGE[instruction][0], "Tgp_" + instruction + "_Global_" + case["language"]
        case.update(fixtureId=case["fixtureId"].replace("typed-", "typed-existingglobal-", 1), engineeringOnly=True, blockName=block,
                    referenceXml="fixtures/typed-graphical/native/" + block + ".xml",
                    dependencyXmls=[{"path": "fixtures/typed-graphical/native/" + storage + ".xml", "names": [storage]}])
        case["payload"]["instances"] = [{"key": "storage", "kind": "existingGlobal", "blockPath": "$block:" + storage}]
        result.append(case)
    # Created storage, engineering only like the standard created cells: a new multi-instance member, also with the caller two
    # parent levels deep, and a new global DB for TONR (no R_TRIG/F_TRIG global storage document is captured).
    for original in [case for case in result if case["family"] in ("tonr", "rtrig", "ftrig")]:
        name = "Fresh_" + original["family"] + "_" + original["language"]
        global_storage = original["payload"]["instances"][0]["kind"] == "existingGlobal"
        if global_storage and original["family"] != "tonr":
            continue
        case = copy.deepcopy(original)
        case.update(fixtureId="typed-" + ("createglobal-" if global_storage else "createmulti-") + original["family"] + "-" + original["language"].lower(),
                    engineeringOnly=True, referenceBindings=[binding("Instance", STORAGE["TONR"][0], name, "GlobalVariable") if global_storage
                                                             else binding("Instance", "Storage", name)])
        case["payload"]["instances"] = [{"key": "storage", "kind": "createGlobal" if global_storage else "createMulti", "name": name}]
        result.append(case)
        if not global_storage:
            nested = copy.deepcopy(case)
            nested.update(fixtureId="typed-transitive-" + case["fixtureId"][len("typed-"):], dependentSources=transitive_hosts(case["blockName"]))
            result.append(nested)
    return result


def main():
    sources = []
    for file, block, text in references():
        path = ROOT / "sources" / file
        path.write_bytes(text.encode("utf-8"))
        form = "SCL" if file.endswith(".scl") else "instance_db_spec" if file.endswith(".instancedb.json") else "SIMATIC_SD"
        sources.append({"path": path.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "route": "standard",
                        "authoredFormat": form, "names": [block], "namespace": ""})
    paths = [row["path"] for row in sources]
    document = {"schemaVersion": 1, "status": "authored-not-executed",
                "purpose": "Standard-route native references for the wave 9 typed families (DInt/Real compare, MOVE, ADD/SUB/MUL/DIV, TONR, "
                           "R_TRIG/F_TRIG; LAD and FBD; local and global IEC storage). Unobserved until captured; enables nothing.",
                "unit": "", "cpuTypeIdentifier": CPU, "archive": {"path": ARCHIVE, "sha256": ARCHIVE_SHA256},
                "sources": sources, "argsFile": paths,
                "probeCommand": ["python", "-B", "scripts/capture_typed_graphical_profiles.py", *paths, "--host", HOST, "--worker", WORKER,
                                 "--output", PROBE, "--cpu-type-identifier", CPU],
                "command": ["python", "-B", "scripts/capture_typed_routes_owned.py", "@" + CAPTURE + ".args", "--host", HOST, "--worker", WORKER,
                            "--output", CAPTURE, "--archive", ARCHIVE, "--sha256", ARCHIVE_SHA256, "--unit", ""],
                "afterCapture": ["python -B scripts/extract_typed_graphical_profiles.py " + CAPTURE + "/capture.json --stage-output " + STAGE,
                                 "python -B scripts/audit_typed_profile_candidates.py --catalog " + STAGE + "/profiles.candidate.v1.json --capture "
                                 + CAPTURE + "/capture.json --output " + STAGE + "/audit.json"]}
    (ROOT / "wave9-capture-packet.v1.json").write_bytes((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    authored = cases()
    (ROOT / "wave9-cases.v1.json").write_bytes((json.dumps({"schemaVersion": 1, "cases": authored}, indent=2) + "\n").encode("utf-8"))
    print(str(len(sources)) + " wave 9 references and " + str(len(authored)) + " cases authored; packet status authored-not-executed.")


if __name__ == "__main__":
    main()
