"""Bind typed qualification to independently captured unit/namespace references."""
import copy
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ARCHIVE = "artifacts/changes/change-12d1d3b9e3a74cd3be6f53b9f2f35cf4/before.zap21"
ARCHIVE_SHA256 = "39cea56a73b8e58263e256c2f27b2402e1d29989fb12cb471f66970e1cce74d6"


def main():
    baseline = json.loads((ROOT / "cases.v1.json").read_text())
    cases = []
    for route, namespace in (("Unit", "TiaMcp_UnitA"), ("Namespace", "TiaMcp_UnitA.Alternate")):
        for language in ("LAD", "FBD"):
            case = copy.deepcopy(next(c for c in baseline["cases"] if c["family"] == "boolean" and c["language"] == language))
            case.update({"fixtureId": "typed-" + route.lower() + "-" + language.lower(),
                "engineeringOnly": True, "archivePath": ARCHIVE, "archiveSha256": ARCHIVE_SHA256,
                "unitName": "TiaMcp_UnitA", "namespace": namespace,
                "blockName": "Tgp_" + route + "_" + language,
                "referenceXml": "fixtures/typed-graphical/native/Tgp_" + route + "_" + language + ".xml"})
            cases.append(case)
            for block_type in ("FC", "FB"):
                created = copy.deepcopy(case)
                created["fixtureId"] += "-create-" + block_type.lower()
                created["payload"]["model"].pop("networkId", None)
                created["payload"]["model"].update({"operation": "createBlock",
                    "creation": {"name": "TgpCreated_" + route + "_" + block_type + "_" + language,
                        "blockType": block_type,
                        "interface": [{"name": name, "section": section, "dataType": "Bool"}
                                      for name, section in (("A", "Input"), ("B", "Input"), ("Inhibit", "Input"), ("Active", "Output"))]}})
                # Existing fixture gives independent topology only; creation's
                # full expected native envelope comes from the captured route.
                created["createdBlockName"] = created["payload"]["model"]["creation"]["name"]
                cases.append(created)
    (ROOT / "route-cases.v1.json").write_text(json.dumps({"schemaVersion": 1, "cases": cases}, indent=2) + "\n")


if __name__ == "__main__":
    main()
