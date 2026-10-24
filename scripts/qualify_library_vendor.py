"""Qualify the two retained vendor distributions: intake, DMC V19 library upgrade into V21, PAL V16 XML import into V21."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import xml.etree.ElementTree as ET
import zipfile

from library_native_harness import Harness, identity, require

KIND_BY_ELEMENT = {"SW.Types.PlcStruct": ("plc_type_import_xml", "expectedTypeNames"),
                   "SW.Tags.PlcTagTable": ("tag_table_import_xml", "expectedTableNames")}
# Import order: types, tags, code, global then instance data, cyclic OBs last.
ORDER = ["SW.Types.PlcStruct", "SW.Tags.PlcTagTable", "SW.Blocks.FC", "SW.Blocks.FB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB", "SW.Blocks.OB"]


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def pal_object(text):
    """Name, element, number and language of one vendor SimaticML document; entities are refused, not expanded."""
    require("<!DOCTYPE" not in text and "<!ENTITY" not in text, "Vendor XML declares a DTD or entity")
    root = ET.fromstring(text.encode("utf-8"))
    element = next(child for child in root if child.tag.startswith("SW."))
    attributes = element.find("AttributeList")
    return {"element": element.tag, "name": attributes.findtext("Name"), "number": attributes.findtext("Number"),
            "language": attributes.findtext("ProgrammingLanguage"), "engineering": root.find("Engineering").get("version")}


def extract(archive, member, destination):
    """Copy one named archive member to an own file; member names are never used as paths."""
    require(PurePosixPath(member).name and ".." not in PurePosixPath(member).parts, "Unsafe archive member")
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(archive.read(member))
    return destination


def all_items(h, library):
    """Every element page of one library under a single metadata revision."""
    first = h.query(library)
    items, offset = list(first["items"]), first.get("nextOffset")
    while offset is not None:
        page = h.tool("library_query", {"request": {"libraryId": library, "maxItems": 200, "offset": offset, "revision": first["revision"]}})
        items += page["items"]
        offset = page.get("nextOffset")
    return {"items": items, "complete": first["completeness"]["scopeComplete"] and len(items) == first["totalCaptured"]}


def body(h):
    receipts = json.loads(Path(h.args.receipts).read_text(encoding="utf-8"))
    packages = {p["id"]: p for p in receipts["packages"]}
    zips = {"pal": Path(h.args.pal_zip), "dmc": Path(h.args.dmc_zip)}
    for key, path in zips.items():
        require(sha256(path.read_bytes()) == packages[key]["sha256"], key + " download differs from its receipt")
    h.report["receipts"] = receipts

    # Intake without TIA: staged copies, classification and hashes.
    def intake(key):
        staged = h.tool("library_package_stage", {"hostPath": str(zips[key])})
        status = h.tool("library_package_status", {"packageId": staged["packageId"]})
        items = h.tool("library_package_inspect", {"packageId": staged["packageId"], "count": 200})["items"]
        require(json.dumps(status).count(packages[key]["sha256"]) >= 1, key + " staged source hash differs from the receipt")
        return {"packageId": staged["packageId"], "candidates": items, "status": status}
    staged = {key: h.stage("stage and classify " + key, lambda k=key: intake(k)) for key in zips}
    dmc_kinds = sorted(c["kind"] for c in staged["dmc"]["candidates"] if c["relativePath"].endswith(".zal19"))
    require(dmc_kinds == ["globalLibraryArchive"] * 5, "DMC must classify five V19 global-library archives")
    require(sum(c["kind"] == "sourceFile" for c in staged["pal"]["candidates"]) == 55, "PAL must classify 55 XML sources")

    # DMC: upgrade every V19 archive on an owned copy. The PLC-only archive must pass; HMI variants are recorded.
    h.owned_portal("dmc upgrade")
    dmc = zipfile.ZipFile(zips["dmc"])
    upgraded = {}
    for member in sorted(n for n in dmc.namelist() if n.endswith(".zal19")):
        name = PurePosixPath(member).name
        source = extract(dmc, member, h.directory / "dmc" / re.sub(r"\W+", "_", name[:-6]) / name)
        before = sha256(source.read_bytes())
        payload = {"schemaVersion": 1, "path": str(source), "expectedSha256": before}
        required = name == "Open Library V19 PLC.zal19"

        def upgrade(payload=payload, source=source, before=before):
            prepared = h.tool("change_prepare", {"kind": "library_upgrade", "payload": payload})
            result = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
            content = result.get("structuredContent") or {}
            require(sha256(source.read_bytes()) == before, "The V19 source archive changed")
            if result.get("isError"):
                return {"upgraded": False, "error": content.get("error")}
            library = content["journal"]["result"]["library"]
            items = all_items(h, library["libraryId"])
            versions = [x for x in items["items"] if x.get("kind") == "version"]
            return {"upgraded": True, "library": library, "changeId": content["changeId"], "complete": items["complete"],
                    "types": sum(x.get("kind") == "type" for x in items["items"]), "versions": len(versions),
                    "committed": sum(x["state"] == "Committed" for x in versions),
                    "masterCopies": sorted(x["name"] for x in items["items"] if x.get("kind") == "masterCopy"),
                    "unavailable": [(x["kind"], x.get("name"), x["reason"]) for x in items["items"] if x.get("unavailable")],
                    "versionIdentities": sorted((x["typeName"], x["version"], x["versionGuid"]) for x in versions)}
        outcome = h.stage(("upgrade " if required else "upgrade (observed) ") + name, upgrade)
        upgraded[name] = outcome
        if required:
            require(outcome["upgraded"] and outcome["complete"] and outcome["versions"] > 0 and outcome["committed"] == outcome["versions"],
                    "PLC library upgrade or readback is incomplete")
    plc = upgraded["Open Library V19 PLC.zal19"]
    h.report["dmcUpgrades"] = {k: {x: v.get(x) for x in ("upgraded", "types", "versions", "committed", "unavailable", "error")} for k, v in upgraded.items()}
    archive = h.archive(plc["library"]["libraryId"])
    h.stage("close dmc upgrade session", lambda: h.tool("tia_detach"))

    # Persistence: the upgraded V21 archive reopens with the same native identities; one dependency-free PLC type is used.
    consumer = h.owned_scratch("dmc consumer")
    reopened = h.open_archive(archive)
    rows = all_items(h, reopened)["items"]
    versions = [x for x in rows if x.get("kind") == "version"]
    require(sorted((x["typeName"], x["version"], x["versionGuid"]) for x in versions) == plc["versionIdentities"],
            "Reopened V21 archive identities differ from the upgraded library")
    candidates = []
    for version in sorted((x for x in versions if x["isDefault"] and "PlcType" in x["engineeringType"]), key=lambda x: x["typeName"]):
        closure = h.query(reopened, query="dependencies", **identity(version))["items"]
        if len(closure) == 1:
            candidates.append(version)
    require(candidates, "No dependency-free DMC PLC data type was found")
    chosen = candidates[0]
    h.report["dmcInstantiatedType"] = chosen
    h.apply("library_instantiate", h.selected(reopened, [chosen], target={"plcPath": consumer}))
    h.stage("dmc type exact link in consumer", lambda: h.verify_use(h.project_library(), chosen, 1))
    h.stage("close dmc consumer session", lambda: h.tool("tia_detach"))

    # PAL: import the V16 exports into a V21 scratch PLC, compile once and read back.
    plc_path = h.owned_scratch("pal")
    pal = zipfile.ZipFile(zips["pal"])
    objects = []
    for member in sorted(n for n in pal.namelist() if n.endswith(".xml")):
        text = pal.read(member).decode("utf-8-sig")
        objects.append({**pal_object(text), "member": member, "text": text})
    require(len(objects) == 55 and {o["engineering"] for o in objects} == {"V16"}, "PAL inventory differs from the receipt")
    # V21 refuses SimaticML whose Engineering version is V16; one document proves the refusal and that nothing was imported.
    first = sorted(objects, key=lambda o: (ORDER.index(o["element"]), o["name"]))[0]
    kind, field = KIND_BY_ELEMENT.get(first["element"], ("block_import_xml", "expectedBlockNames"))
    before = h.tool("project_inventory", {"maxItems": 2000})
    prepared = h.prepare(kind, {"scopePath": plc_path, "xml": first["text"], field: [first["name"]]})

    def refused_import():
        error = h.call_error("change_apply", {**h.apply_args(prepared), "compile": False, "save": False})
        require("engineering version 'V16'" in (error.get("message") or "") and "not supported" in error.get("message", ""),
                "V16 import was not refused for its Engineering version: %s" % error.get("message"))
        after = h.tool("project_inventory", {"maxItems": 2000})
        require(before.get("types") == after.get("types") and before.get("blocks") == after.get("blocks"), "A refused PAL import changed the PLC")
        return {"object": first["name"], "error": error}
    h.report["palImport"] = h.stage("V21 refuses PAL's V16 SimaticML (" + first["name"] + ")", refused_import)
    # The engineering route may finish its own admission; only an unresolved one needs a fresh-session reconciliation.
    h.report["palUnresolvedAdmissions"] = h.unresolved_admissions()
    if h.report["palUnresolvedAdmissions"]:
        h.stage("close pal session", lambda: h.tool("tia_detach"))
        h.owned_portal("pal reconciliation")
        h.reconcile_admission("reconcile the refused import's admission in a fresh session")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--receipts", type=Path, required=True)
    parser.add_argument("--pal-zip", type=Path, required=True)
    parser.add_argument("--dmc-zip", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "vq_", "Vendor distributions: PAL V16 XML import and DMC V19 library upgrade into V21", [
        "DMC HMI variants are upgraded and recorded only; HMI behavior belongs to lane D.",
        "DMC prerequisites (system/clock memory bytes, 'Open Library' tag table master copy) are not applied, so only a dependency-free data type is instantiated.",
        "PAL is a V16 VCI export; TIA V21 refuses V16 SimaticML, so PAL native import needs an intermediate V17-V20 installation that is not on this PC.",
        "Vendor executables (DMC toolbox) are never run."], [args.receipts, args.pal_zip, args.dmc_zip]).run(body)
