"""Existing IEC global storage cases with independently captured DB bindings."""
import copy
import json
from pathlib import Path
from author_extended_cases import extended_cases

ROOT = Path(__file__).resolve().parent


def cases():
    result = []
    for original in extended_cases():
        if "createglobal" not in original["fixtureId"] or original["family"] == "fb-multi":
            continue
        case = copy.deepcopy(original)
        case["fixtureId"] = case["fixtureId"].replace("createglobal", "existingglobal")
        key = case["payload"]["instances"][0]["key"]
        name = case["dependencyXmls"][0]["names"][0]
        case["payload"]["instances"] = [{"key": key, "kind": "existingGlobal", "blockPath": "$block:" + name}]
        case.pop("referenceBindings")
        result.append(case)
    return result


if __name__ == "__main__":
    (ROOT / "existing-global-cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": cases()}, indent=2) + "\n", encoding="utf-8")
