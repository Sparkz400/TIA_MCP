"""Independent edit/creation/storage requests and explicit native binding oracles."""
import copy
import hashlib
import json
import re
from pathlib import Path

from author_cases import cases

ROOT = Path(__file__).resolve().parent


def binding(element, source, target, scope="LocalVariable"):
    return {"element": element, "fromScope": scope, "toScope": scope,
            "fromComponents": [source], "toComponents": [target], "expectedCount": 1}


def network_free_source(language, name):
    """Derive a network-free caller from the native Boolean export: no compile unit, title IDs closed up."""
    native = ROOT / "native" / ("Tgp_Boolean_" + language + ".xml")
    text = native.read_bytes().decode("utf-8-sig")
    units = re.findall(r"\s*<SW\.Blocks\.CompileUnit .*?</SW\.Blocks\.CompileUnit>", text, re.S)
    assert len(units) == 1 and re.findall(r'ID="([0-9A-F]+)"', units[0]) == ["3", "4", "5", "6", "7"]
    derived = text.replace(units[0], "", 1)
    assert sorted(re.findall(r'ID="([0-9A-F]+)"', derived)) == ["0", "1", "2", "8", "9"]
    derived = derived.replace('ID="8"', 'ID="3"').replace('ID="9"', 'ID="4"')
    derived = derived.replace("<Name>Tgp_Boolean_" + language + "</Name>", "<Name>" + name + "</Name>", 1)
    target = ROOT / "sources" / ("network-free-" + language.lower() + ".xml")
    target.write_bytes(derived.encode("utf-8"))
    (ROOT / "sources" / ("network-free-" + language.lower() + ".provenance.json")).write_bytes((json.dumps({
        "schemaVersion": 1, "builderUsed": False, "authoredFormat": "native_xml_template_derivation", "requiresNativeAcceptance": True,
        "references": [{"path": native.relative_to(ROOT.parent.parent).as_posix(), "sha256": hashlib.sha256(native.read_bytes()).hexdigest()}],
        "adaptedFields": ["CompileUnit removed", "Title IDs 8/9 -> 3/4", "Name"],
        "sourceSha256": hashlib.sha256(target.read_bytes()).hexdigest()}, indent=2) + "\n").encode("utf-8"))
    return target.relative_to(ROOT.parent.parent).as_posix()


def extended_cases():
    baseline = cases()
    result = []
    for language in ("LAD", "FBD"):
        boolean = next(c for c in baseline if c["family"] == "boolean" and c["language"] == language)
        for block_type in ("FC", "FB"):
            case = copy.deepcopy(boolean)
            name = "TgpCreated_" + block_type + "_" + language
            case.update(fixtureId="typed-create-" + block_type.lower() + "-" + language.lower(), engineeringOnly=True, createdBlockName=name)
            model = case["payload"]["model"]
            model.pop("networkId")
            model.update(operation="createBlock", creation={"name": name, "blockType": block_type,
                "interface": [{"name": n, "section": s, "dataType": "Bool"} for n, s in
                              (("A", "Input"), ("B", "Input"), ("Inhibit", "Input"), ("Active", "Output"))]})
            result.append(case)
        case = copy.deepcopy(boolean)
        case.update(fixtureId="typed-insert-" + language.lower(), engineeringOnly=True)
        case["payload"]["model"].pop("networkId")
        case["payload"]["model"].update(operation="insertNetwork", insertAfterNetworkId="$first")
        result.append(case)
        case = copy.deepcopy(boolean)
        name = "Tgp_NetworkFree_" + language
        case.update(fixtureId="typed-insert-first-" + language.lower(), engineeringOnly=True, blockName=name,
            referenceXml=network_free_source(language, name),
            topologyXml="fixtures/typed-graphical/native/Tgp_Boolean_" + language + ".xml")
        case["payload"]["model"].pop("networkId")
        case["payload"]["model"].update(operation="insertNetwork")
        result.append(case)
        case = copy.deepcopy(boolean)
        case.update(fixtureId="typed-edit-" + language.lower(), engineeringOnly=True,
            blockName="Tgp_TemplateFC_" + language,
            referenceXml="fixtures/typed-graphical/native/Tgp_TemplateFC_" + language + ".xml")
        original = "InputBool" if language == "LAD" else "OutputBool"
        case["referenceBindings"] = [binding("Access", original, "InOutBool")]
        case["payload"]["model"] = {"schemaVersion": 1, "language": language, "operation": "editNetwork", "networkId": "$first",
            "nodes": [{"key": "edited", "kind": "contact" if language == "LAD" else "assign", "operand": "replacement",
                       "nativeUid": "$part:" + ("Contact" if language == "LAD" else "Coil") + ":0"}],
            "operands": [{"key": "replacement", "scope": "local", "components": ["InOutBool"]}], "connections": []}
        result.append(case)
        for family in ("fb-multi", "ton", "tof", "tp", "ctu", "ctd", "ctud"):
            original = next(c for c in baseline if c["family"] == family and c["language"] == language)
            for storage in ("createMulti", "createGlobal"):
                case = copy.deepcopy(original)
                case.update(fixtureId="typed-" + storage.lower() + "-" + family + "-" + language.lower(), engineeringOnly=True)
                instance = case["payload"]["instances"][0]
                name = "Fresh_" + family.replace("-", "_") + "_" + language
                case["payload"]["instances"] = [{"key": instance["key"], "kind": storage, "name": name}]
                if storage == "createMulti":
                    case["referenceBindings"] = [binding("Instance", "Child" if family == "fb-multi" else "Storage", name)]
                elif family == "fb-multi":
                    case.update(blockName="Tgp_FB_global_" + language,
                        referenceXml="fixtures/typed-graphical/native/Tgp_FB_global_" + language + ".xml")
                    case["referenceBindings"] = [binding("Instance", "TiaMcp_EnableGate_DB", name, "GlobalVariable")]
                else:
                    system_db = "Tgp_GlobalTimer" if family in ("ton", "tof", "tp") else "Tgp_GlobalCounter"
                    case.update(blockName="Tgp_" + family.upper() + "_Global_" + language,
                        referenceXml="fixtures/typed-graphical/native/Tgp_" + family.upper() + "_Global_" + language + ".xml",
                        dependencyXmls=[{"path": "fixtures/typed-graphical/native/" + system_db + ".xml", "names": [system_db]}])
                    case["referenceBindings"] = [binding("Instance", system_db, name, "GlobalVariable")]
                result.append(case)
                if family == "ton":
                    # Composite: the same storage creation, inserted as a new network after the first one.
                    composite = copy.deepcopy(case)
                    composite["fixtureId"] = "typed-insert-" + storage.lower() + "-ton-" + language.lower()
                    composite["payload"]["model"].pop("networkId")
                    composite["payload"]["model"].update(operation="insertNetwork", insertAfterNetworkId="$first")
                    result.append(composite)
    return result


if __name__ == "__main__":
    (ROOT / "extended-cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": extended_cases()}, indent=2) + "\n", encoding="utf-8")
