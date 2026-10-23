"""Record installed Classic and Unified API evidence without starting Siemens.

Signatures prove API exposure only. Target, license, native preservation and
operation qualification remain separate gates, including when both DLLs exist.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_API = Path(r"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48")
FAMILIES = {
    "classic": ("Siemens.Engineering.WinCC", "Siemens.Engineering.Hmi.HmiTarget"),
    "unified": ("Siemens.Engineering.WinCCUnified", "Siemens.Engineering.HmiUnified.HmiSoftware"),
}
PROFILES = ("classic-comfort", "classic-rt-advanced", "classic-basic", "classic-rt-professional", "unified-comfort", "unified-pc")
REFERENCES = [
    {"title": "V21 Unified license access", "url": "https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/checking-license-to-access-unified-device", "observation": "Vendor wording is inconsistent; null Software and license exceptions must be observed independently."},
    {"title": "V21 Classic screen faceplate import", "url": "https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/importing-a-screen-with-a-faceplate-instance", "observation": "Import has faceplate and dependency prerequisites; does not prove exact-version assignment."},
    {"title": "V21 Unified faceplate UDT assignment", "url": "https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/faceplate-container/assigning-udt-to-faceplate-container", "observation": "Distinct Unified container binding route; type/version lifecycle belongs to the library subsystem."},
]


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inspect_api(api_directory: Path) -> dict:
    families = []
    for family, (assembly, root_type) in FAMILIES.items():
        xml, dll = (api_directory / (assembly + extension) for extension in (".xml", ".dll"))
        entry = {"family": family, "assembly": assembly, "rootType": root_type,
                 "xmlPath": str(xml), "assemblyPath": str(dll), "xmlAvailable": xml.is_file(),
                 "assemblyAvailable": dll.is_file(), "apiAvailability": "unavailable",
                 "bridgeImplementation": "recorded-separately", "nativeQualification": "unverified",
                 "currentTargetAvailability": "not-probed", "members": []}
        if dll.is_file():
            entry["assemblySha256"] = digest(dll)
        if xml.is_file():
            entry["xmlSha256"] = digest(xml)
            try:
                for member in ET.parse(xml).getroot().findall("./members/member"):
                    signature = member.get("name", "")
                    if "#" in signature:  # Generic self-description is not a typed creation promise.
                        continue
                    if (signature == "T:" + root_type or signature.startswith("P:" + root_type + ".") or
                        signature.startswith("M:") and any(key in signature for key in
                            (".Create(", ".Create``", ".CreateFrom(", ".Export(", ".Import(", ".GetReferences", ".GetService", ".Compile", ".Assign")) or
                        signature.startswith("P:") and any(key in signature for key in
                            (".ScreenItems", ".Dynamizations", ".EventHandlers", ".PropertyEventHandlers", ".ScriptCode", ".GlobalDefinitionAreaScriptCode", ".Connection", ".PlcTag", ".DataType", ".TagName", ".Faceplate", ".ContainedType"))):
                        entry["members"].append({"signature": signature, "summary": " ".join(member.findtext("summary", "").split())[:600]})
                root_found = any(member["signature"] == "T:" + root_type for member in entry["members"])
                entry["apiAvailability"] = "available" if root_found and dll.is_file() else "incomplete"
            except ET.ParseError as error:
                entry["metadataError"] = str(error)
                entry["apiAvailability"] = "invalid-metadata"
        families.append(entry)
    return {"schemaVersion": 1, "source": "Installed public XML API documentation and binary SHA-256",
            "nativeCodeExecuted": False, "runtimeVerified": False, "families": families,
            "profiles": [{"profile": profile, "catalogIdentifier": None, "runtimeVersion": None,
                          "nativeQualification": "unverified", "currentTargetAvailability": "not-probed"} for profile in PROFILES],
            "vendorReferences": REFERENCES,
            "limitations": ["Assembly presence is not installation option, license, device or native qualification evidence.",
                            "Missing typed metadata does not prove that all vendor export or dynamic API routes are absent.",
                            "No private project database was read and no project script was executed."]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-dir", type=Path, default=DEFAULT_API)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = inspect_api(args.api_dir)
    path = args.report or ROOT / "artifacts" / ("hmi-api-" + uuid.uuid4().hex + ".json")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
    print(json.dumps({"report": str(path), "families": [{"family": x["family"], "apiAvailability": x["apiAvailability"], "signatures": len(x["members"])} for x in result["families"]]}))
    return 0 if all(x["apiAvailability"] == "available" for x in result["families"]) else 2


if __name__ == "__main__":
    raise SystemExit(main())
