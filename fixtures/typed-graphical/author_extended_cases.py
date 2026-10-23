"""Independent edit/creation/storage requests and explicit native binding oracles."""
import copy
import json
from pathlib import Path

from author_cases import cases

ROOT = Path(__file__).resolve().parent


def binding(element, source, target, scope="LocalVariable"):
    return {"element": element, "fromScope": scope, "toScope": scope,
            "fromComponents": [source], "toComponents": [target], "expectedCount": 1}


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
    return result


if __name__ == "__main__":
    (ROOT / "extended-cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": extended_cases()}, indent=2) + "\n", encoding="utf-8")
