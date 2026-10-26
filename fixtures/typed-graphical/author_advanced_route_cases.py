"""Route-specific typed requests; native expected graphs remain independent.

Unavailable independently captured references are recorded explicitly and are
not silently represented as qualified cases. Rerun after additional captures.
"""
import copy
import json
from pathlib import Path

from author_cases import cases as standard_cases
from author_numeric_cases import cases as numeric_cases
from author_existing_global_cases import cases as existing_global_cases
from author_extended_cases import binding, extended_cases
from author_route_cases import ARCHIVE, ARCHIVE_SHA256

ROOT = Path(__file__).resolve().parent
IEC_FAMILIES = ("ton", "tof", "tp", "ctu", "ctd", "ctud")
REPO = ROOT.parent.parent


def replace_names(value, prefix):
    if isinstance(value, str):
        return value.replace("Tgp_", prefix)
    if isinstance(value, list):
        return [replace_names(item, prefix) for item in value]
    if isinstance(value, dict):
        return {key: replace_names(item, prefix) for key, item in value.items()}
    return value


def main():
    cases, missing = [], []
    for route, namespace in (("Unit", "TiaMcp_UnitA"), ("Namespace", "TiaMcp_UnitA.Alternate")):
        prefix = "Tgp" + route + "_"
        source_cases = [c for c in standard_cases() if c["family"] in ("eq", "ne", "lt", "le", "gt", "ge", "ton", "tof", "tp", "ctu", "ctd", "ctud")]
        source_cases += numeric_cases() + existing_global_cases()
        source_cases += [c for c in extended_cases() if c["family"] in IEC_FAMILIES
                         and c["payload"]["instances"][0]["kind"] in ("createMulti", "createGlobal")]
        scoped_cases = []
        for original in source_cases:
            case = replace_names(copy.deepcopy(original), prefix)
            case.update(fixtureId="typed-" + route.lower() + "-" + original["fixtureId"].removeprefix("typed-"),
                engineeringOnly=True, archivePath=ARCHIVE, archiveSha256=ARCHIVE_SHA256,
                unitName="TiaMcp_UnitA", namespace=namespace)
            instance = (case["payload"].get("instances") or [{}])[0]
            if case["family"] in IEC_FAMILIES and instance.get("kind") == "createGlobal":
                # Scoped global instances are referenced by their namespace-qualified name.
                system_db = case["dependencyXmls"][0]["names"][0]
                case["referenceBindings"] = [binding("Instance", namespace + "." + system_db, namespace + "." + instance["name"], "GlobalVariable")]
            scoped_cases.append(case)
            if case["family"] == "numeric-fb":
                global_case = copy.deepcopy(case)
                global_name = prefix + "NumericFB_DB"
                global_case["fixtureId"] += "-global"
                global_case["blockName"] = prefix + "NumericFB_Global_" + case["language"]
                global_case["referenceXml"] = "fixtures/typed-graphical/native/" + global_case["blockName"] + ".xml"
                global_case["dependencyXmls"].append({"path": "fixtures/typed-graphical/native/" + global_name + ".xml", "names": [global_name]})
                global_case["payload"]["instances"] = [{"key": "child", "kind": "existingGlobal", "blockPath": "$block:" + global_name}]
                # Both harness callers would share the one global callee DB; use the single-instance oracle.
                global_case.update(runtimeInstances=["dut"], behaviorFamily="numeric-fb-single")
                scoped_cases.append(global_case)
                for storage, reference in (("createMulti", case), ("createGlobal", global_case)):
                    created = copy.deepcopy(reference)
                    name = "FreshNumeric_" + route + "_" + case["language"]
                    created["fixtureId"] = case["fixtureId"] + "-" + storage.lower()
                    created["payload"]["instances"] = [{"key": "child", "kind": storage, "name": name}]
                    created["referenceBindings"] = [binding("Instance", "Child", name)] if storage == "createMulti" else [
                        binding("Instance", namespace + "." + global_name, namespace + "." + name, "GlobalVariable")]
                    scoped_cases.append(created)
        for case in scoped_cases:
            required = [case["referenceXml"]] + [d["path"] for d in case.get("dependencyXmls", [])]
            absent = [path for path in required if not (REPO / path).is_file()]
            if absent:
                missing.append({"fixtureId": case["fixtureId"], "missingIndependentNativeReferences": absent,
                                "engineeringVerified": False, "runtimeVerified": False})
            else:
                cases.append(case)
    (ROOT / "route-advanced-cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": cases}, indent=2) + "\n", encoding="utf-8")
    (ROOT / "route-pending-references.v1.json").write_text(json.dumps({"schemaVersion": 1, "unqualifiedCases": missing}, indent=2) + "\n", encoding="utf-8")
    print(str(len(cases)) + " executable route cases; " + str(len(missing)) + " explicitly pending independent native references.")


if __name__ == "__main__":
    main()
