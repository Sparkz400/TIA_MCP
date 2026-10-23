"""Inspect installed V21 XML API metadata without loading assemblies or starting TIA.

Metadata presence is deliberately separate from bridge support and native qualification.
Use the recorded exact signatures to check adapters against the installed assembly build.
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

GROUPS = {
    "global_access": ("Siemens.Engineering.Base", (
        "M:Siemens.Engineering.Library.GlobalLibraryComposition.Open(",
        "M:Siemens.Engineering.Library.GlobalLibraryComposition.Retrieve(",
        "M:Siemens.Engineering.Library.GlobalLibraryComposition.OpenWithUpgrade(",
        "M:Siemens.Engineering.Library.GlobalLibraryComposition.RetrieveWithUpgrade(")),
    "global_persistence": ("Siemens.Engineering.Base", (
        "M:Siemens.Engineering.Library.UserGlobalLibrary.Save",
        "M:Siemens.Engineering.Library.UserGlobalLibrary.Archive(",
        "M:Siemens.Engineering.Library.UserGlobalLibrary.Close")),
    "version_metadata": ("Siemens.Engineering.Base", (
        "P:Siemens.Engineering.Library.Types.LibraryTypeVersion.Guid",
        "P:Siemens.Engineering.Library.Types.LibraryTypeVersion.VersionNumber",
        "P:Siemens.Engineering.Library.Types.LibraryTypeVersion.State",
        "P:Siemens.Engineering.Library.Types.LibraryTypeVersion.Dependencies")),
    "project_update": ("Siemens.Engineering.Base", (
        "M:Siemens.Engineering.Library.GlobalLibrary.UpdateProject(",
        "M:Siemens.Engineering.Library.GlobalLibrary.UpdateLibrary(",
        "M:Siemens.Engineering.Library.GlobalLibrary.UpdateCheck(")),
    "authoring": ("Siemens.Engineering.Base", (
        "M:Siemens.Engineering.Library.Types.LibraryTypeComposition.CreateFromDocuments(",
        "M:Siemens.Engineering.Library.Types.LibraryTypeVersion.Edit",
        "M:Siemens.Engineering.Library.Types.LibraryTypeVersion.Release(",
        "M:Siemens.Engineering.Library.Types.LibraryTypeVersion.Discard")),
    "plc_types": ("Siemens.Engineering.Step7", (
        "M:Siemens.Engineering.SW.Blocks.PlcBlockComposition.CreateFrom(Siemens.Engineering.SW.Blocks.CodeBlockLibraryTypeVersion",
        "M:Siemens.Engineering.SW.Types.PlcTypeComposition.CreateFrom(Siemens.Engineering.SW.Types.PlcTypeLibraryTypeVersion")),
    "plc_master_copies": ("Siemens.Engineering.Step7", (
        "M:Siemens.Engineering.SW.Blocks.PlcBlockComposition.CreateFrom(Siemens.Engineering.Library.MasterCopies.MasterCopy",
        "M:Siemens.Engineering.SW.Types.PlcTypeComposition.CreateFrom(Siemens.Engineering.Library.MasterCopies.MasterCopy")),
    "classic_screens": ("Siemens.Engineering.WinCC", (
        "M:Siemens.Engineering.Hmi.Screen.ScreenComposition.CreateFrom(",)),
    "unified_containers": ("Siemens.Engineering.WinCCUnified", (
        "T:Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer",)),
}


def inspect_api(api_directory: Path, groups=None) -> dict:
    groups = GROUPS if groups is None else groups
    assembly_names = sorted({group[0] for group in groups.values()})
    files, members = {}, {}
    for name in assembly_names:
        path = api_directory / (name + ".xml")
        dll = api_directory / (name + ".dll")
        entry = {"xmlPath": str(path), "assemblyPath": str(dll), "xmlAvailable": path.is_file(),
                 "assemblyAvailable": dll.is_file(), "signatures": 0}
        files[name] = entry
        members[name] = {}
        if not path.is_file():
            continue
        data = path.read_bytes()
        entry["xmlSha256"] = hashlib.sha256(data).hexdigest()
        try:
            root = ET.fromstring(data)
        except ET.ParseError as error:
            entry["metadataError"] = str(error)
            continue
        for member in root.findall("./members/member"):
            signature = member.get("name")
            if signature:
                summary = " ".join(" ".join(member.findtext("summary", default="").split()).split())
                members[name][signature] = summary[:1000]
        entry["signatures"] = len(members[name])
        if dll.is_file():
            with dll.open("rb") as stream:
                entry["assemblySha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
    checks = []
    for group, (assembly, required) in groups.items():
        observations = []
        for prefix in required:
            matches = [{"signature": key, "summary": value} for key, value in members[assembly].items()
                       if key.startswith(prefix)]
            observations.append({"requestedPrefix": prefix, "found": bool(matches), "matches": matches})
        checks.append({"group": group, "assembly": assembly,
                       "metadataStatus": "available" if all(item["found"] for item in observations) else "incomplete",
                       "observations": observations, "nativeQualified": False})
    return {"schemaVersion": 1, "origin": "Installed XML API documentation and assembly file hashes",
            "nativeCodeExecuted": False, "nativeQualified": False, "apiDirectory": str(api_directory.resolve()),
            "assemblies": files, "groups": checks,
            "limitations": ["Metadata presence does not prove target compatibility, license availability or successful execution.",
                            "An incomplete prefix check can mean a different documented signature; inspect exact installed metadata before classifying an API as absent.",
                            "Library updates, master copies, Classic WinCC and Unified need separate native fixture qualification."]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-dir", type=Path, default=DEFAULT_API)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = inspect_api(args.api_dir)
    destination = args.report or ROOT / "artifacts" / ("library-api-" + uuid.uuid4().hex + ".json")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
    print(f"Library API metadata: {sum(x['metadataStatus'] == 'available' for x in result['groups'])}/{len(result['groups'])} signature groups observed; native qualification not performed.")
    print(f"Report: {destination}")
    return 0 if result["assemblies"].get("Siemens.Engineering.Base", {}).get("xmlAvailable") else 2


if __name__ == "__main__":
    raise SystemExit(main())
