"""Audit public V21 OPC UA documentation and schema files without executing Siemens code.

This is installation evidence, never a target capability or qualification result.
Optionally merge a hash-bound report from Inspect-OpcUaApi.ps1 to check accessors.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_API = Path(r"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48")
FAMILIES = {
    "Siemens.Engineering.Step7": ("Siemens.Engineering.SW.OpcUa.",),
    "Siemens.Engineering.Base": (
        "Siemens.Engineering.HW.OpcUa", "Siemens.Engineering.HW.Certificate",
        "Siemens.Engineering.HW.Features.OpcUaUserManagement",
        "Siemens.Engineering.HW.Features.CertificateManagementConfiguration",
        "Siemens.Engineering.HW.Utilities.OpcUaExportProvider",
        "Siemens.Engineering.Security.Certificate", "Siemens.Engineering.Security.LocalCertificate",
        "Siemens.Engineering.Security.SubjectAlternative", "Siemens.Engineering.Security.SignatureAlgorithm"),
    "Siemens.Engineering.WinCCUnified": (
        "Siemens.Engineering.HmiUnified.RuntimeSettings.HmiOpcUa",
        "Siemens.Engineering.HmiUnified.HmiOpcUaAlarm.",),
    "Siemens.Engineering.TestSuite": ("Siemens.Engineering.TestSuite.SystemTest.SystemTestCase.OPCUA",),
}
NOISE = ("Siemens#", ".GetAttribute", ".SetAttribute", ".Equals(", ".GetHashCode", ".ToString",
         ".GetEnumerator", ".CopyTo", ".Contains(", ".IndexOf(")


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def xml_read(path: Path, maximum: int = 64 << 20) -> ET.Element:
    if path.stat().st_size > maximum:
        raise ValueError("Metadata exceeds the 64 MiB limit")
    data = path.read_bytes()
    # Inspecting vendor documentation needs no DTD, entity expansion or external resolver.
    folded = data.replace(b"\0", b"").upper()
    if b"<!DOCTYPE" in folded or b"<!ENTITY" in folded:
        raise ValueError("DTD/entity declarations are not supported")
    return ET.fromstring(data)


def inspect_api(api_directory: Path, accessor_report: dict | None = None) -> dict:
    api_directory = api_directory.resolve()
    if (api_directory / "net48").is_dir():
        api_directory /= "net48"
    assemblies, diagnostics = {}, []
    for assembly, prefixes in FAMILIES.items():
        path, dll = api_directory / (assembly + ".xml"), api_directory / (assembly + ".dll")
        row = {"xmlPath": str(path), "assemblyPath": str(dll), "xmlAvailable": path.is_file(),
               "assemblyAvailable": dll.is_file(), "members": []}
        assemblies[assembly] = row
        if dll.is_file():
            row["assemblySha256"] = digest(dll)
        if not path.is_file():
            diagnostics.append({"code": "DocumentationMissing", "assembly": assembly})
            continue
        row["xmlSha256"] = digest(path)
        try:
            root = xml_read(path)
            if digest(path) != row["xmlSha256"]:
                raise ValueError("Documentation changed during inspection")
            for member in root.findall("./members/member"):
                name = member.get("name", "")
                if not name[2:].startswith(prefixes) or any(token in name for token in NOISE):
                    continue
                row["members"].append({"signature": name,
                    "summary": " ".join(" ".join(member.itertext()).split())[:2000]})
            row["members"].sort(key=lambda item: item["signature"])
        except (ValueError, ET.ParseError) as error:
            diagnostics.append({"code": "DocumentationInvalid", "assembly": assembly, "message": str(error)})
    schemas = []
    for path in sorted((api_directory.parent / "Schemas").glob("*.xsd")):
        row = {"name": path.name, "path": str(path), "sha256": digest(path)}
        try:
            root = xml_read(path)
            if digest(path) != row["sha256"]:
                raise ValueError("Schema changed during inspection")
            row["targetNamespace"] = root.get("targetNamespace")
            row["dependencies"] = [child.get("schemaLocation") for child in root
                if child.tag in ("{http://www.w3.org/2001/XMLSchema}include", "{http://www.w3.org/2001/XMLSchema}import")]
        except (ValueError, ET.ParseError) as error:
            row["error"] = str(error)
        schemas.append(row)
    accessors = None
    if accessor_report is not None:
        if (accessor_report.get("schemaVersion") != 1 or accessor_report.get("inspectionMode") != "metadataOnly"
                or accessor_report.get("nativeCodeExecuted") is not False or accessor_report.get("targetProbed") is not False):
            raise ValueError("Expected a metadata-only accessor report version 1")
        if not accessor_report.get("assemblies"):
            raise ValueError("Accessor report has no assembly identities")
        for assembly in accessor_report["assemblies"]:
            installed = assemblies.get(assembly["name"])
            if installed is None or installed.get("assemblySha256") != assembly.get("sha256"):
                raise ValueError("Accessor report does not match installed assembly: " + assembly["name"])
        accessors = accessor_report
    return {"schemaVersion": 1, "observedAtUtc": datetime.now(timezone.utc).isoformat(),
        "origin": "installedPublicApiFiles", "apiDirectory": str(api_directory),
        "nativeCodeExecuted": False, "targetProbed": False, "nativeQualified": False,
        "assemblies": assemblies, "schemas": schemas, "accessorEvidence": accessors,
        "diagnostics": diagnostics,
        "limits": {"metadataFileBytes": 64 << 20},
        "limitations": ["XML property entries do not establish writable accessors.",
            "Assembly metadata does not establish service availability, licensing or behavior on a target.",
            "The schema inventory lists installed files only; it does not certify a supported OPC UA import dialect.",
            "Optional products have separate installation, target and qualification states."]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-dir", type=Path, default=DEFAULT_API)
    parser.add_argument("--accessor-report", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    report = json.loads(args.accessor_report.read_text(encoding="utf-8-sig")) if args.accessor_report else None
    result = inspect_api(args.api_dir, report)
    destination = args.report or ROOT / "artifacts" / ("opcua-api-" + uuid.uuid4().hex + ".json")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
    print(f"Report: {destination}")
    print("File/metadata inspection only; no native or target qualification.")
    required = [result["assemblies"][name] for name in ("Siemens.Engineering.Base", "Siemens.Engineering.Step7")]
    return 0 if all(row["xmlAvailable"] and row["assemblyAvailable"] and row["members"] for row in required) else 2


if __name__ == "__main__":
    raise SystemExit(main())
