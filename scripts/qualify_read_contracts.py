"""Qualify the identity and completeness contracts of read tools natively in one owned scratch project.

Every covered feature must show, in this run, three things: a passed call whose response names exactly the requested
target (checked against identities this run created or read independently), a typed refusal for an unknown, ambiguous,
stale or invalid selector, and a response that tells an empty or complete result apart from a truncated, partial or
unobserved one. The project holds a CPU 1511-1 PN scratch with seeded blocks, a UDT, an empty group, a software unit,
a released project-library type and an empty global library; a CPU 1518-4 PN/DP is added later so that unqualified
selectors become ambiguous and loadable generation has a supported target. Scratch creation is checked in its own
Portal first, because a refused CPU identifier leaves the partially created project open by design.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
import shutil

from library_native_harness import Harness, require
from qualify_cpu_memory_bytes import add_cpu

ROOT = Path(__file__).resolve().parents[1]
READ_GATES = {"AC-identity", "AC-completeness", "AC-evidence"}
# Catalog features this run verifies and the MCP tools each one binds (scripts/test_qualify_read_contracts.py keeps
# these equal to the catalog).
FEATURES = {
    "tia_create_scratch": ("tia_create_scratch",), "project_properties": ("project_properties",),
    "software_inspect": ("software_inspect",), "plc.document_formats": ("block_read", "type_read"),
    "plc_document_inventory": ("plc_document_inventory",), "plc_loadable_generate": ("plc_loadable_generate",),
    "hardware_inspect": ("hardware_inspect",), "plc_alarm_inspect": ("plc_alarm_inspect",),
    "instance_lookup": ("instance_lookup",), "engineering_read": ("engineering_read",),
    "change_prepare": ("change_prepare",), "change_apply": ("change_apply",), "change_status": ("change_status",),
    "change_restore": ("change_restore",), "target_capabilities": ("target_capabilities",), "object_resolve": ("object_resolve",),
    "library_open": ("library_open",), "library_list": ("library_list",), "library_query": ("library_query",),
    "library_compare": ("library_compare",), "library_update_check": ("library_update_check",), "library_close": ("library_close",),
    "library_capabilities": ("library_capabilities",), "library_export": ("library_export",),
    "device_inventory": ("device_inventory",), "device_inventory_export": ("device_inventory_export",),
    "opcua_engineering_inspect": ("opcua_engineering_inspect",), "project_text_export": ("project_text_export",),
    "vci_workspace_inspect": ("vci_workspace_inspect",), "connection_inspect": ("connection_inspect",),
    "access_inspect": ("access_inspect",), "certificate_inspect": ("certificate_inspect",), "web_inspect": ("web_inspect",),
    "plc_compare": ("plc_compare",),
}
NEEDS = ("identity", "refusal", "completeness")
# Tools without a target selector have nothing to refuse; their identity is the exact set they report.
NO_SELECTOR = {"library_list": "Takes no selector; identity is the exact set of registered library IDs.",
               "library_capabilities": "Takes no selector; identity is the exact native change-kind registry."}
LIMITATIONS = [
    "One owned scratch project: CPU 1511-1 PN V4.1 with one software unit, plus CPU 1518-4 PN/DP added by hardware_add; no "
    "safety, technology objects, HMI, connections, VCI workspaces, PLC documents or know-how protection, so those content "
    "families are observed only as empty, complete results.",
    "Truncation is shown with maxItems/pageSize 1 where the fixture has more than one row; connection_inspect, "
    "plc_document_inventory and vci_workspace_inspect have no rows to truncate here, and library_compare has a fixed "
    "200-row bound that this fixture does not reach.",
    "A refused cpuTypeIdentifier is reported after the scratch project was created, because Siemens needs an open project "
    "for catalog access; the partial project is named in the refusal and its Portal is discarded.",
    "plc_loadable_generate is positive only on the CPU 1518; on the CPU 1511 it is refused (UnsupportedCapability), as "
    "Siemens documents loadables only for CPU 1518 and its subtypes from firmware V2.8.",
    "Engineering reads only; no runtime, online or physical-device observation.",
]

FB_SOURCE = '''FUNCTION_BLOCK "RcFb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      In : Int;
   END_VAR
   VAR_OUTPUT
      Out : Int;
   END_VAR

BEGIN
\t#Out := #In + 1;
END_FUNCTION_BLOCK

FUNCTION_BLOCK "RcHost"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR
      Inner : "RcFb";
      Many : Array[0..1] of "RcFb";
   END_VAR

BEGIN
\t#Inner(In := 1);
\t#Many[0](In := 2);
END_FUNCTION_BLOCK

DATA_BLOCK "RcFbDb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
"RcFb"

BEGIN

END_DATA_BLOCK

DATA_BLOCK "RcHostDb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
"RcHost"

BEGIN

END_DATA_BLOCK

FUNCTION "RcCaller" : Void
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
BEGIN
\t"RcFbDb"(In := 3);
\t"RcHostDb"();
END_FUNCTION

'''
BLOCKS = ["RcFb", "RcHost", "RcFbDb", "RcHostDb", "RcCaller"]
UDT = 'TYPE "RcUdt"\nVERSION : 0.1\n   STRUCT\n      Value : Int;\n   END_STRUCT;\n\nEND_TYPE\n\n'
UNIT_FC = ('FUNCTION "RcUnitFc" : Void\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n   VAR_TEMP\n      X : Int;\n   END_VAR\n'
           'BEGIN\n\t#X := 1;\nEND_FUNCTION\n\n')
SCRATCH_CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
UNKNOWN_CPU = "OrderNumber:6ES7 999-9XX99-9XX9/V9.9"
LOADABLE_CPU = "CPU 1518-4 PN/DP"
ZERO_GUID = "00000000-0000-0000-0000-000000000001"


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def verify_artifacts(artifacts, directory=None):
    """Every artifact exists with its reported hash; with a directory, the artifacts are exactly its files."""
    require(artifacts, "No artifacts were reported")
    for artifact in artifacts:
        require(sha256(artifact["path"]) == artifact["sha256"], "Artifact hash differs: " + artifact["path"])
    if directory is not None:
        on_disk = sorted(str(p.resolve()) for p in Path(directory).rglob("*") if p.is_file())
        require(on_disk == sorted(str(Path(a["path"]).resolve()) for a in artifacts), "Reported artifacts are not the directory's files")
    return len(artifacts)


def bounded(completeness, truncated):
    """Completeness flags of a bounded inspection: a full read is untruncated; a 1-item read with more rows is truncated."""
    require(completeness.get("truncated") is truncated, "Expected truncated=%s: %s" % (truncated, completeness))
    if truncated:
        require(completeness.get("scopeComplete", completeness.get("complete")) is False, "A truncated result claims completeness")


def explicit(result):
    """An untruncated inspection is either scope-complete or names what it did not observe."""
    completeness = result["completeness"]
    bounded(completeness, False)
    require(completeness.get("scopeComplete", completeness.get("complete")) is True or result.get("warnings"),
            "An incomplete scope must name its warnings: %s" % completeness)
    return {"scopeComplete": completeness.get("scopeComplete", completeness.get("complete")), "warnings": len(result.get("warnings") or [])}


def coverage_gaps(coverage):
    gaps = {}
    for feature in FEATURES:
        needs = [n for n in NEEDS if not (n == "refusal" and feature in NO_SELECTOR)]
        missing = [n for n in needs if not coverage.get(feature, {}).get(n)]
        if missing:
            gaps[feature] = missing
    return gaps


class Run:
    def __init__(self, h):
        self.h, self.coverage = h, {}
        h.report["coverage"] = self.coverage
        h.report["noSelector"] = NO_SELECTOR

    def mark(self, feature, needs, label):
        require(feature in FEATURES, "Unknown feature " + feature)
        for need in needs:
            self.coverage.setdefault(feature, {}).setdefault(need, []).append(label)

    def ok(self, feature, needs, label, tool, args, check):
        """A call that must pass; check(result) asserts the contract and returns its recorded summary."""
        def call():
            result = self.h.tool(tool, args)
            return check(result)
        summary = self.h.stage(label, call)
        self.mark(feature, needs, label)
        return summary

    def refuse(self, feature, label, tool, args, codes, needs=("refusal",)):
        def call():
            error = self.h.call_error(tool, args)
            require(error.get("code") in codes, "Expected one of %s, observed %s: %s" % (codes, error.get("code"), error.get("message")))
            return {"code": error["code"], "message": error.get("message"), "details": error.get("details")}
        result = self.h.stage(label, call)
        self.mark(feature, needs, label)
        return result

    def apply(self, label, kind, payload, scope=None):
        prepared = self.h.stage("prepare " + label, lambda: self.h.tool("change_prepare", {"kind": kind, "payload": payload, **(scope or {})}))
        return self.h.apply_prepared(kind, prepared)


def scratch_creation(r):
    """In one Portal an unknown CPU is refused with its partial state; a second Portal creates the exact requested CPU."""
    h = r.h
    h.owned_portal("unknown cpu")

    def unknown_cpu():
        error = h.call_error("tia_create_scratch", {"cpuTypeIdentifier": UNKNOWN_CPU})
        details = error.get("details") or {}
        require(error.get("code") == "CatalogItemNotFound", "Unexpected refusal: %s" % error)
        require(details.get("completed") is False and details.get("stages") == ["project_created"], "Partial creation not reported: %s" % details)
        projects = [p["path"] for p in h.tool("tia_session_status")["projects"]]
        require(projects == [details["project"]["path"]], "The refusal does not name the partial project that is open")
        return {"code": error["code"], "stages": details["stages"], "project": details["project"]["path"]}
    h.stage("unknown cpuTypeIdentifier is refused and reports the partial project", unknown_cpu)
    r.mark("tia_create_scratch", ("refusal", "completeness"), "unknown cpuTypeIdentifier")
    h.stage("discard the partial Portal", lambda: h.tool("tia_detach"))
    h.owned_portal("read contracts")

    def create(result):
        cpu = result["selectedCpu"]
        require(cpu["typeIdentifier"] == SCRATCH_CPU and cpu["selection"] == "Explicit catalog identifier.", "Selected CPU differs: %s" % cpu)
        require(result["completed"] is True and result["stages"][-1] == "blocks_exported" and result["compile"]["errorCount"] == 0, "Scratch incomplete")
        require(len(result["exports"]) == 4 and all(sha256(e["artifact"]["path"]) == e["artifact"]["sha256"] for e in result["exports"]), "Fixture exports differ")
        h.scratch = result
        return {"selectedCpu": cpu, "stages": result["stages"]}
    r.ok("tia_create_scratch", ("identity", "completeness"), "create a scratch with an exact CPU identifier", "tia_create_scratch",
         {"cpuTypeIdentifier": SCRATCH_CPU}, create)
    r.refuse("tia_create_scratch", "a second scratch while a project is open", "tia_create_scratch", {}, ("ScratchIsolationRequired",))
    r.project = h.tool("tia_session_status")["projects"][0]["path"]
    plcs = h.tool("project_inventory", {"maxItems": 1000})["plcs"]
    require(len(plcs) == 1, "Expected one PLC")
    r.p1 = plcs[0]["path"]
    r.cpu1 = r.p1.split("/plc/")[0]
    r.device1 = r.cpu1.split("/items/")[0]
    hardware = h.tool("hardware_inspect", {"hardwarePath": r.cpu1})["items"]
    require([i["typeIdentifier"] for i in hardware if i["path"] == r.cpu1] == [SCRATCH_CPU], "The created CPU is not the requested one")


def seed(r):
    p1 = r.p1
    r.unit = p1 + "/units/RcUnit"
    r.apply("activate de-DE", "project_languages_set", {"addActive": ["de-DE"]})
    r.apply("create RcUdt", "type_source_upsert", {"source": UDT, "expectedTypeNames": ["RcUdt"]})
    r.apply("create blocks", "source_upsert", {"language": "SCL", "source": FB_SOURCE, "expectedBlockNames": BLOCKS})
    r.apply("create the empty group RcEmpty", "group_create", {"groupKind": "blocks", "groupPath": p1 + "/blocks", "name": "RcEmpty"})
    r.apply("create software unit RcUnit", "software_unit_create", {"name": "RcUnit"})
    r.apply("create RcUnitFc", "source_upsert", {"language": "SCL", "source": UNIT_FC, "expectedBlockNames": ["RcUnitFc"], "scopePath": r.unit})
    documents = r.h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    r.library = r.h.project_library()
    r.gate = r.h.release(r.library, r.h.create_type(r.library, documents, "TiaMcpLibraryGate", "blocks", p1), "1.0.0")
    r.global_library = r.h.apply("library_create", {"schemaVersion": 1, "name": "ReadContracts"})["journal"]["result"]["library"]["libraryId"]


def project_reads(r):
    h, p1 = r.h, r.p1

    def properties(result):
        require(result["project"]["path"] == r.project, "Project identity differs")
        require(result["completeness"]["complete"] is True and not any(result["completeness"]["truncated"].values()), "Full read is incomplete")
        require("de-DE" in result["languageSettings"]["active"] and "en-US" in result["languageSettings"]["active"], "Active languages differ")
        r.properties = result
        return {"active": result["languageSettings"]["active"]}
    r.ok("project_properties", ("identity",), "project_properties of the scratch", "project_properties", {}, properties)

    def properties_one(result):
        full, truncated = r.properties, result["completeness"]["truncated"]
        lists = {"supportedLanguages": full["languageSettings"]["supported"], "usedProducts": full["usedProducts"],
                 "historyEntries": full["historyEntries"], "textCategories": full["textCategories"]}
        require(result["completeness"]["complete"] is False, "A 1-item read claims completeness")
        for key, rows in lists.items():
            require(truncated[key] is (len(rows) > 1), "%s truncation differs from its %d rows" % (key, len(rows)))
        return truncated
    r.ok("project_properties", ("completeness",), "project_properties with maxItems 1 marks each longer list", "project_properties", {"maxItems": 1}, properties_one)
    r.refuse("project_properties", "project_properties of an unknown project", "project_properties", {"projectName": "NoSuchProject"}, ("ProjectNotFound",))

    def software(result):
        require(result["plcPath"] == p1, "PLC differs")
        blocks = {b["name"]: b for b in result["blocks"]}
        for name in BLOCKS + ["RcUnitFc"]:
            require(name in blocks and blocks[name]["identity"]["objectPath"] == blocks[name]["path"]
                    and blocks[name]["identity"]["projectPath"] == r.project, "Block identity differs: " + name)
        require(blocks["RcUnitFc"]["scopePath"] == r.unit and blocks["RcCaller"]["scopePath"] == p1, "Block scopes differ")
        require([t["name"] for t in result["types"] if t["name"] == "RcUdt"] == ["RcUdt"], "RcUdt missing")
        require(p1 + "/blocks/groups/RcEmpty" in next(s for s in result["softwareScopes"] if s["path"] == p1)["groups"], "Empty group missing")
        require(sorted(s["path"] for s in result["softwareScopes"]) == sorted([p1, r.unit]), "Software scopes differ")
        return explicit(result)
    r.ok("software_inspect", ("identity", "completeness"), "software_inspect of the PLC", "software_inspect", {"plcPath": p1}, software)

    def unit(result):
        require([s["path"] for s in result["softwareScopes"]] == [r.unit], "Scope filter differs")
        require([b["name"] for b in result["blocks"]] == ["RcUnitFc"], "Unit blocks differ")
        return explicit(result)
    r.ok("software_inspect", ("identity",), "software_inspect of the software unit", "software_inspect", {"plcPath": p1, "scopePath": r.unit}, unit)
    r.ok("software_inspect", ("completeness",), "software_inspect with maxItems 1 is truncated", "software_inspect", {"plcPath": p1, "maxItems": 1},
         lambda result: bounded(result["completeness"], True))
    r.refuse("software_inspect", "software_inspect of an unknown PLC", "software_inspect", {"plcPath": p1 + "Missing"}, ("PlcNotFound",))
    r.refuse("software_inspect", "software_inspect of an unknown unit", "software_inspect", {"plcPath": p1, "scopePath": p1 + "/units/NoSuchUnit"}, ("ObjectNotFound",))

    def document(path, kind):
        def check(result):
            require(result["identity"]["objectPath"] == path and result["identity"]["kind"] == kind, "Identity differs")
            require(result["completeness"]["complete"] is True, "Export not complete")
            artifacts = result["export"]["artifacts"]
            directory = Path(artifacts[0]["path"]).parent
            if result["format"] == "generatedSource":
                require(result["export"]["removedExternalSources"] == [], "Source generation left external sources")
            return {"format": result["format"], "files": verify_artifacts(artifacts, directory)}
        return check
    caller, udt = p1 + "/blocks/RcCaller", p1 + "/types/RcUdt"
    for fmt in ("simaticSd", "generatedSource"):
        r.ok("plc.document_formats", ("identity", "completeness"), "block_read %s of RcCaller" % fmt, "block_read", {"blockPath": caller, "format": fmt}, document(caller, "block"))
        r.ok("plc.document_formats", ("identity", "completeness"), "type_read %s of RcUdt" % fmt, "type_read", {"typePath": udt, "format": fmt}, document(udt, "type"))
    r.refuse("plc.document_formats", "block_read of an unknown format", "block_read", {"blockPath": caller, "format": "pdf"}, ("InvalidRequest",))
    r.refuse("plc.document_formats", "type_read of an unknown type", "type_read", {"typeName": "NoSuchUdt", "plcPath": p1, "format": "simaticSd"}, ("ObjectNotFound",))

    def documents(result):
        require(result["plcPath"] == p1 and result["documents"] == [] and result["exports"] == [], "Document inventory differs")
        c = result["completeness"]
        require(c["complete"] is True and c["truncated"] is False and c["documentCount"] == 0, "Empty inventory is not complete")
        return c
    r.ok("plc_document_inventory", ("identity", "completeness"), "plc_document_inventory of the PLC is empty and complete", "plc_document_inventory", {"plcPath": p1}, documents)
    r.ok("plc_document_inventory", ("identity",), "plc_document_inventory of the software unit", "plc_document_inventory", {"plcPath": p1, "scopePath": r.unit}, documents)
    r.refuse("plc_document_inventory", "plc_document_inventory export of an unknown document", "plc_document_inventory",
             {"plcPath": p1, "exportPaths": [udt + "/documents/NoSuchDocument"]}, ("ObjectNotFound",))
    r.refuse("plc_document_inventory", "plc_document_inventory of an unknown unit", "plc_document_inventory", {"plcPath": p1, "scopePath": p1 + "/units/NoSuchUnit"}, ("ObjectNotFound",))

    def alarms(scopes):
        def check(result):
            require(result["plcPath"] == p1 and sorted(s["path"] for s in result["scopes"]) == sorted(scopes), "Alarm scopes differ")
            return explicit(result)
        return check
    r.ok("plc_alarm_inspect", ("identity", "completeness"), "plc_alarm_inspect of the PLC", "plc_alarm_inspect", {"plcPath": p1}, alarms([p1, r.unit]))
    r.ok("plc_alarm_inspect", ("identity",), "plc_alarm_inspect of the software unit", "plc_alarm_inspect", {"plcPath": p1, "scopePath": r.unit}, alarms([r.unit]))
    r.ok("plc_alarm_inspect", ("completeness",), "plc_alarm_inspect with maxItems 1 is truncated", "plc_alarm_inspect", {"plcPath": p1, "maxItems": 1},
         lambda result: bounded(result["completeness"], True))
    r.refuse("plc_alarm_inspect", "plc_alarm_inspect of an unknown unit", "plc_alarm_inspect", {"plcPath": p1, "scopePath": p1 + "/units/NoSuchUnit"}, ("ObjectNotFound",))

    def lookup(result):
        fb = p1 + "/blocks/RcFb"
        require(result["block"]["path"] == fb, "Looked-up block differs")
        instances = {(i["kind"], i["dbPath"]) for i in result["instances"]}
        require(("singleInstanceDb", p1 + "/blocks/RcFbDb") in instances and ("multiInstance", p1 + "/blocks/RcHostDb") in instances, "Instances differ: %s" % instances)
        require(sorted(m["member"] for m in result["multiInstanceDeclarations"]) == ["Inner", "Many"], "Multi-instance members differ")
        callers = {c["callerPath"] for c in result["callSites"]}
        require({p1 + "/blocks/RcCaller", p1 + "/blocks/RcHost"} <= callers, "Call sites differ: %s" % callers)
        require(result["completeness"]["scopeComplete"] is True and result["completeness"]["truncated"] is False and result["unresolved"] == [], "Lookup incomplete")
        return {"instances": sorted(instances), "callers": sorted(callers)}
    r.ok("instance_lookup", ("identity", "completeness"), "instance_lookup of RcFb", "instance_lookup", {"plcPath": p1, "blockName": "RcFb"}, lookup)
    r.ok("instance_lookup", ("completeness",), "instance_lookup with maxItems 1 is truncated", "instance_lookup", {"plcPath": p1, "blockName": "RcFb", "maxItems": 1},
         lambda result: bounded(result["completeness"], True))
    r.ok("instance_lookup", ("completeness",), "instance_lookup with maxXmlBlocks 1 is incomplete", "instance_lookup", {"plcPath": p1, "blockName": "RcFb", "maxXmlBlocks": 1},
         lambda result: bounded(result["completeness"], True))

    def unused(result):
        require(result["block"]["path"] == r.unit + "/blocks/RcUnitFc", "Unit block differs")
        require(result["instances"] == result["callSites"] == result["multiInstanceDeclarations"] == [] and result["instanceApplicability"] == "notApplicableForFc", "An unused FC has rows")
        require(result["completeness"]["scopeComplete"] is True, "The empty lookup is not complete")
        return result["completeness"]
    r.ok("instance_lookup", ("identity", "completeness"), "instance_lookup of an uncalled unit FC is empty and complete", "instance_lookup",
         {"plcPath": p1, "blockName": "RcUnitFc", "scopePath": r.unit}, unused)
    r.refuse("instance_lookup", "instance_lookup of an unknown block", "instance_lookup", {"plcPath": p1, "blockName": "NoSuchFb"}, ("BlockNotFound",))

    def calls(result):
        require(result["scope"] == "calls" and result["blockPath"] == caller and result["truncated"] is False, "Calls read differs")
        callees = {c.get("callee") or c.get("calleeName") or c.get("instance") for c in result["calls"]}
        require(len(result["calls"]) == 2, "Expected the two RcCaller calls: %s" % result["calls"])
        require(sha256(result["nativeXml"]["path"]) == result["nativeXml"]["sha256"], "Native XML hash differs")
        return {"calls": result["calls"], "callees": sorted(map(str, callees))}
    r.ok("engineering_read", ("identity", "completeness"), "engineering_read calls of RcCaller", "engineering_read", {"selectors": {"scope": "calls", "blockPath": caller}}, calls)

    def hardware_one(result):
        require([i["path"] for i in result["items"]] == [r.cpu1] and result["truncated"] is False, "Hardware item differs")
        return {"attributes": len(result["items"][0]["attributes"])}
    r.ok("engineering_read", ("identity",), "engineering_read of the CPU item", "engineering_read", {"selectors": {"scope": "hardware", "hardwarePath": r.cpu1}}, hardware_one)
    r.ok("engineering_read", ("completeness",), "engineering_read hardware with maxItems 1 is truncated", "engineering_read", {"selectors": {"scope": "hardware", "maxItems": 1}},
         lambda result: require(len(result["items"]) == 1 and result["truncated"] is True, "Truncation not reported"))
    r.refuse("engineering_read", "engineering_read of an unknown hardware item", "engineering_read", {"selectors": {"scope": "hardware", "hardwarePath": r.device1 + "/items/NoSuchItem"}}, ("HardwareNotFound",))
    r.refuse("engineering_read", "engineering_read of an unknown scope", "engineering_read", {"selectors": {"scope": "bogus"}}, ("InvalidRequest",))

    def capabilities(result):
        require(result["identity"]["objectPath"] == caller and result["identity"]["kind"] == "block" and result["identity"]["projectPath"] == r.project, "Target identity differs")
        return explicit(result)
    r.ok("target_capabilities", ("identity", "completeness"), "target_capabilities of RcCaller", "target_capabilities", {"selectors": {"kind": "block", "objectPath": caller}}, capabilities)
    r.ok("target_capabilities", ("completeness",), "target_capabilities with maxItems 1 is truncated", "target_capabilities", {"selectors": {"kind": "plc", "plcPath": p1, "maxItems": 1}},
         lambda result: bounded(result["completeness"], True))
    r.refuse("target_capabilities", "target_capabilities of an unknown block", "target_capabilities", {"selectors": {"kind": "block", "objectPath": p1 + "/blocks/NoSuchBlock"}}, ("ObjectNotFound",))

    def resolved(path, kind, name):
        def check(result):
            i = result["identity"]
            require((i["objectPath"], i["kind"], i["name"], i["projectPath"]) == (path, kind, name, r.project), "Resolved identity differs: %s" % i)
            require(result["completeness"]["scopeComplete"] is True and result["completeness"]["truncated"] is False, "Resolution not complete")
            return i
        return check
    r.ok("object_resolve", ("identity", "completeness"), "object_resolve of RcCaller by path", "object_resolve", {"selectors": {"kind": "block", "objectPath": caller}}, resolved(caller, "block", "RcCaller"))
    r.ok("object_resolve", ("identity",), "object_resolve of RcCaller by name", "object_resolve", {"selectors": {"name": "RcCaller", "plcPath": p1}}, resolved(caller, "block", "RcCaller"))
    r.ok("object_resolve", ("identity",), "object_resolve of the software unit", "object_resolve", {"selectors": {"kind": "softwareUnit", "objectPath": r.unit}}, resolved(r.unit, "softwareUnit", "RcUnit"))

    def ambiguous_name():
        error = h.call_error("object_resolve", {"selectors": {"name": r.cpu1.rsplit("/", 1)[1], "plcPath": p1}})
        candidates = (error.get("details") or {}).get("candidates") or []
        require(error.get("code") == "AmbiguousTarget" and {c["kind"] for c in candidates} >= {"hardware", "plc"}, "Expected hardware and PLC candidates: %s" % error)
        require(error["details"]["candidateCount"] == len(candidates) and error["details"]["candidatesTruncated"] is False, "Candidate list is not complete")
        return {"candidates": [(c["kind"], c["objectPath"]) for c in candidates]}
    h.stage("object_resolve of a name shared by the CPU and its PLC lists every candidate", ambiguous_name)
    r.mark("object_resolve", ("refusal", "completeness"), "ambiguous name")
    r.refuse("object_resolve", "object_resolve of an unsupported kind", "object_resolve", {"selectors": {"kind": "bogus", "name": "RcCaller"}}, ("UnsupportedObjectKind",))
    r.refuse("object_resolve", "object_resolve of an unknown name", "object_resolve", {"selectors": {"name": "NoSuchObject", "plcPath": p1}}, ("ObjectNotFound",))
    r.refuse("object_resolve", "object_resolve with another project path", "object_resolve",
             {"selectors": {"kind": "block", "name": "RcCaller", "plcPath": p1, "projectPath": r"C:\NoSuch\Project.ap21"}}, ("ProjectMismatch",))


def hardware_reads(r):
    h = r.h
    r.ok("hardware_inspect", ("identity", "completeness"), "hardware_inspect of the CPU", "hardware_inspect", {"hardwarePath": r.cpu1},
         lambda result: (require(all(i["path"] == r.cpu1 or i["path"].startswith(r.cpu1 + "/") for i in result["items"]) and r.cpu1 in [i["path"] for i in result["items"]],
                                 "Items outside the CPU"), explicit(result))[1])
    r.ok("hardware_inspect", ("completeness",), "hardware_inspect with maxItems 1 is truncated", "hardware_inspect", {"maxItems": 1}, lambda result: bounded(result["completeness"], True))
    r.refuse("hardware_inspect", "hardware_inspect of an unknown item", "hardware_inspect", {"hardwarePath": r.device1 + "/items/NoSuchItem"}, ("HardwareNotFound",))

    def inventory(result):
        snapshot = result["snapshot"]
        require(snapshot["projectPath"] == r.project and snapshot["coverage"]["scopeComplete"] is True and snapshot["coverage"]["truncated"] is False, "Inventory incomplete")
        require(result["nextCursor"] is None and len(result["rows"]) == snapshot["totalRows"], "A full page has a continuation")
        r.inventory = result
        return {"snapshotId": snapshot["snapshotId"], "rows": snapshot["totalRows"]}
    r.ok("device_inventory", ("identity", "completeness"), "device_inventory of the project", "device_inventory", {"pageSize": 200}, inventory)

    def pages():
        page = h.tool("device_inventory", {"pageSize": 1})
        snapshot, rows = page["snapshot"]["snapshotId"], list(page["rows"])
        r.cursor = page["nextCursor"]
        while page["nextCursor"]:
            page = h.tool("device_inventory", {"snapshotId": snapshot, "cursor": page["nextCursor"], "pageSize": 1})
            require(len(page["rows"]) == 1 and page["snapshot"]["snapshotId"] == snapshot, "Page differs")
            rows.extend(page["rows"])
        ids = [row["rowId"] for row in rows]
        require(len(ids) == len(set(ids)) and sorted(ids) == sorted(row["rowId"] for row in r.inventory["rows"]), "Paging omitted or duplicated rows")
        return {"pages": len(ids), "snapshotId": snapshot}
    r.paged = h.stage("device_inventory pages of 1 return every row once", pages)
    r.mark("device_inventory", ("completeness",), "paging")
    r.ok("device_inventory", ("identity",), "device_inventory of one device", "device_inventory", {"devicePath": r.device1, "pageSize": 200},
         lambda result: require(result["snapshot"]["coverage"]["devicePath"] == r.device1
                                and all(row["path"] == r.device1 or row["path"].startswith(r.device1 + "/") for row in result["rows"]), "Rows outside the device"))
    r.refuse("device_inventory", "device_inventory of an unknown device", "device_inventory", {"devicePath": r.device1 + "Missing"}, ("DeviceNotFound",))
    r.refuse("device_inventory", "device_inventory of an unknown snapshot", "device_inventory", {"snapshotId": "0" * 32}, ("InventorySnapshotUnavailable",))
    r.refuse("device_inventory", "device_inventory with a tampered cursor", "device_inventory",
             {"snapshotId": r.paged["snapshotId"], "cursor": r.cursor[:-2] + ("AA" if not r.cursor.endswith("AA") else "BB")}, ("InvalidCursor",))

    def export(fmt):
        def check(result):
            snapshot = r.inventory["snapshot"]
            require(result["snapshot"]["snapshotId"] == snapshot["snapshotId"] and result["rowsExported"] == snapshot["totalRows"], "Exported snapshot differs")
            for artifact in (result["artifact"], result["metadataArtifact"]):
                require(sha256(artifact["path"]) == artifact["sha256"], "Export hash differs")
            if fmt == "csv":
                with open(result["artifact"]["path"], encoding="utf-8-sig", newline="") as handle:
                    rows = list(csv.DictReader(handle))
                require(sorted(row["Object path"] for row in rows) == sorted(row["path"] for row in r.inventory["rows"])
                        and {row["Snapshot ID"] for row in rows} == {snapshot["snapshotId"]}, "CSV rows differ from the snapshot")
            return {"rowsExported": result["rowsExported"], "artifact": result["artifact"]}
        return check
    for fmt in ("csv", "xlsx"):
        r.ok("device_inventory_export", ("identity", "completeness"), "device_inventory_export %s" % fmt, "device_inventory_export",
             {"snapshotId": r.inventory["snapshot"]["snapshotId"], "format": fmt}, export(fmt))
    r.refuse("device_inventory_export", "device_inventory_export of an unknown snapshot", "device_inventory_export", {"snapshotId": "0" * 32}, ("InventorySnapshotUnavailable",))

    def cpu_devices(tool):
        def check(result):
            require([d["objectPath"] for d in result["devices"]] == [r.cpu1], tool + " devices differ: %s" % [d["objectPath"] for d in result["devices"]])
            return explicit(result)
        return check
    for feature in ("access_inspect", "certificate_inspect", "web_inspect"):
        r.ok(feature, ("identity", "completeness"), feature + " of the CPU", feature, {"hardwarePath": r.cpu1}, cpu_devices(feature))
        r.ok(feature, ("completeness",), feature + " with maxItems 1 is truncated", feature, {"maxItems": 1}, lambda result: bounded(result["completeness"], True))
        r.refuse(feature, feature + " of an unknown item", feature, {"hardwarePath": r.device1 + "/items/NoSuchItem"}, ("HardwareNotFound",))

    def connections(result):
        require(result["owners"] and all(o == r.cpu1 or o.startswith(r.cpu1 + "/") for o in result["owners"]), "Connection owners outside the CPU")
        require(not result.get("connections"), "The scratch has no configured connections")
        require(result["completeness"]["scopeComplete"] is True and result["completeness"]["truncated"] is False, "Empty result not complete")
        return {"owners": len(result["owners"])}
    r.ok("connection_inspect", ("identity", "completeness"), "connection_inspect of the CPU is empty and complete", "connection_inspect", {"hardwarePath": r.cpu1}, connections)
    r.refuse("connection_inspect", "connection_inspect of an unknown item", "connection_inspect", {"hardwarePath": r.device1 + "/items/NoSuchItem"}, ("HardwareNotFound",))

    def opcua(result):
        require((result["hardwarePath"], result["plcPath"]) == (r.cpu1, r.p1), "OPC UA target differs")
        return explicit(result)
    r.ok("opcua_engineering_inspect", ("identity", "completeness"), "opcua_engineering_inspect by PLC", "opcua_engineering_inspect", {"plcPath": r.p1}, opcua)
    r.ok("opcua_engineering_inspect", ("identity",), "opcua_engineering_inspect by CPU", "opcua_engineering_inspect", {"hardwarePath": r.cpu1}, opcua)
    r.ok("opcua_engineering_inspect", ("completeness",), "opcua_engineering_inspect with maxItems 1 is truncated", "opcua_engineering_inspect", {"plcPath": r.p1, "maxItems": 1},
         lambda result: bounded(result["completeness"], True))
    r.refuse("opcua_engineering_inspect", "opcua_engineering_inspect of an unknown item", "opcua_engineering_inspect", {"hardwarePath": r.device1 + "/items/NoSuchItem"}, ("ObjectNotFound",))

    def vci(result):
        require(result["project"]["path"] == r.project and result["workspaces"] == [] and [g["kind"] for g in result["groups"]] == ["system"], "VCI inventory differs")
        require(result["completeness"]["scopeComplete"] is True and result["completeness"]["truncated"] is False, "Empty VCI inventory not complete")
        return {"groups": result["groups"]}
    r.ok("vci_workspace_inspect", ("identity", "completeness"), "vci_workspace_inspect has no workspaces and is complete", "vci_workspace_inspect", {}, vci)
    r.refuse("vci_workspace_inspect", "vci_workspace_inspect of an unknown project", "vci_workspace_inspect", {"projectName": "NoSuchProject"}, ("ProjectNotFound",))

    def texts(mode, owners):
        def check(result):
            require((result["projectPath"], result["mode"], result["owners"]) == (r.project, mode, owners), "Text export target differs")
            require(result["modifiedAfter"] is result["modifiedBefore"] and result["nativeImportExecuted"] is False, "Text export changed the project")
            artifacts = [w["artifact"] for w in result["workbooks"]]
            return {"workbooks": [w["role"] for w in result["workbooks"]], "files": verify_artifacts(artifacts)}
        return check
    r.ok("project_text_export", ("identity", "completeness"), "project_text_export of the whole project", "project_text_export",
         {"sourceCulture": "en-US", "targetCultures": ["de-DE"]}, texts("wholeProject", []))
    r.ok("project_text_export", ("identity",), "project_text_export of the PLC", "project_text_export",
         {"sourceCulture": "en-US", "targetCultures": ["de-DE"], "owners": [r.p1]}, texts("owners", [r.p1]))
    r.refuse("project_text_export", "project_text_export to an inactive culture", "project_text_export", {"sourceCulture": "en-US", "targetCultures": ["fr-FR"]}, ("LanguageInactive",))
    r.refuse("project_text_export", "project_text_export of an unknown owner", "project_text_export",
             {"sourceCulture": "en-US", "targetCultures": ["de-DE"], "owners": [r.p1 + "Missing"]}, ("OwnerNotFound",))


def library_reads(r):
    h, gate = r.h, r.gate

    def capabilities(result):
        require("library_create_type" in result["changeKinds"] and len(result["changeKinds"]) == len(set(result["changeKinds"])), "Change-kind registry differs")
        require(result["qualification"] and result["limits"]["pageSize"] == 200, "Limits are not stated")
        return {"changeKinds": len(result["changeKinds"]), "limits": result["limits"]}
    r.ok("library_capabilities", ("identity", "completeness"), "library_capabilities", "library_capabilities", {}, capabilities)

    def listed(expected):
        def check(result):
            ids = {row["libraryId"]: row for row in result["libraries"]}
            require(set(expected) <= ids.keys(), "Registered libraries differ: %s" % sorted(ids))
            require(ids[r.library]["kind"] == "project" and ids[r.library]["path"] == r.project, "Project library identity differs")
            require(all(row["revision"] for row in result["libraries"]), "A library has no revision")
            r.listed = ids
            return sorted(ids)
        return check
    r.ok("library_list", ("identity", "completeness"), "library_list names the project and global library", "library_list", {}, listed([r.library, r.global_library]))

    def opened(result):
        require((result["libraryId"], result["kind"], result["path"]) == (r.library, "project", r.project), "Reopening the project library gave another identity")
        # The open states what its revision covers and that the project library is the project itself, not an owned copy.
        require(result["revision"] == r.listed[r.library]["revision"] and result["revisionScope"], "Revision or its scope differs")
        require(result["ownedWorkingCopy"] is False and result["writable"] is True and result["workingDirectory"] is None, "Ownership differs")
        return {k: result[k] for k in ("libraryId", "revision", "revisionScope", "ownedWorkingCopy", "writable")}
    r.ok("library_open", ("identity", "completeness"), "library_open of the project library returns its registered identity", "library_open", {"request": {"kind": "project"}}, opened)
    missing = str(h.directory / "NoSuch.al21")
    r.refuse("library_open", "library_open of a path without its hash", "library_open", {"request": {"path": missing}}, ("InvalidRequest",))
    r.refuse("library_open", "library_open of a path that does not exist", "library_open", {"request": {"path": missing, "expectedSha256": "0" * 64}}, ("PackageInvalid",))

    def query(result):
        types = [i for i in result["items"] if i["kind"] == "type"]
        versions = [i for i in result["items"] if i["kind"] == "version"]
        require([t["typeGuid"] for t in types] == [gate["typeGuid"]] and [v["versionGuid"] for v in versions] == [gate["versionGuid"]], "Library content differs")
        require(result["completeness"]["scopeComplete"] is True and result["nextOffset"] is None, "Full query has a continuation")
        r.items, r.revision = result["items"], result["revision"]
        r.formats = types[0]["supportedExportFormats"]
        return {"items": len(result["items"]), "revision": result["revision"]}
    r.ok("library_query", ("identity", "completeness"), "library_query of the project library", "library_query", {"request": {"libraryId": r.library, "maxItems": 200}}, query)

    def paged():
        first = h.tool("library_query", {"request": {"libraryId": r.library, "maxItems": 1}})
        rows, offset = list(first["items"]), first["nextOffset"]
        require(offset == 1 and first["revision"] == r.revision, "First page has no continuation")
        while offset is not None:
            page = h.tool("library_query", {"request": {"libraryId": r.library, "maxItems": 1, "offset": offset, "revision": r.revision}})
            rows.extend(page["items"])
            offset = page["nextOffset"]
        require(rows == r.items, "Pages differ from the full query")
        return {"pages": len(rows)}
    h.stage("library_query pages of 1 equal the full query", paged)
    r.mark("library_query", ("completeness",), "paging")
    r.refuse("library_query", "library_query of an unknown library", "library_query", {"request": {"libraryId": "0" * 32, "maxItems": 10}}, ("LibraryIdentityMismatch",))
    r.refuse("library_query", "library_query page with a stale revision", "library_query",
             {"request": {"libraryId": r.library, "maxItems": 1, "offset": 1, "revision": "0" * 64}}, ("PreviewStale",))
    r.refuse("library_query", "library_query page without its revision", "library_query",
             {"request": {"libraryId": r.library, "maxItems": 1, "offset": 1}}, ("InvalidRequest",))

    def exported(result):
        require(result["libraryId"] == r.library and result["files"] and result["filesTruncated"] is False, "Export is empty or truncated")
        manifest = json.loads(Path(result["manifest"]["path"]).read_text(encoding="utf-8-sig"))
        require((manifest["version"]["typeGuid"], manifest["version"]["versionGuid"]) == (gate["typeGuid"], gate["versionGuid"])
                and [f["sha256"] for f in manifest["files"]] == [f["sha256"] for f in result["files"]], "Export manifest differs")
        return {"documentName": result["documentName"], "files": verify_artifacts(result["files"] + [result["manifest"]], result["documentDirectory"])}
    fmt = "SimaticSD" if "SimaticSD" in r.formats else r.formats[0]
    r.ok("library_export", ("identity", "completeness"), "library_export of TiaMcpLibraryGate 1.0.0", "library_export",
         {"request": {"libraryId": r.library, "typeGuid": gate["typeGuid"], "versionGuid": gate["versionGuid"], "format": fmt}}, exported)
    r.refuse("library_export", "library_export without a format", "library_export", {"request": {"libraryId": r.library, "typeGuid": gate["typeGuid"], "versionGuid": gate["versionGuid"]}}, ("InvalidRequest",))
    r.refuse("library_export", "library_export of an unknown version", "library_export",
             {"request": {"libraryId": r.library, "typeGuid": gate["typeGuid"], "versionGuid": ZERO_GUID, "format": fmt}}, ("LibraryIdentityMismatch",))
    r.refuse("library_export", "library_export in an unsupported format", "library_export",
             {"request": {"libraryId": r.library, "typeGuid": gate["typeGuid"], "versionGuid": gate["versionGuid"], "format": "Pdf"}}, ("TargetIncompatible",))
    r.refuse("library_export", "library_export from the global library", "library_export",
             {"request": {"libraryId": r.global_library, "typeGuid": gate["typeGuid"], "versionGuid": gate["versionGuid"], "format": fmt}}, ("TargetIncompatible",))

    def compared(result):
        require(result["items"][0]["left"] == "Project library" and result["items"][0]["right"] == "ReadContracts" and result["truncated"] is False, "Compare roots differ")
        require(any(i["left"].startswith("TiaMcpLibraryGate") and i["status"] == "RightMissing" for i in result["items"]), "The released type is not reported missing on the right")
        return {"items": len(result["items"])}
    r.ok("library_compare", ("identity", "completeness"), "library_compare of the project and global library", "library_compare",
         {"request": {"leftLibraryId": r.library, "rightLibraryId": r.global_library}}, compared)
    r.refuse("library_compare", "library_compare with an unknown library", "library_compare", {"request": {"leftLibraryId": r.library, "rightLibraryId": "0" * 32}}, ("LibraryIdentityMismatch",))

    def update_check(result):
        require((result["libraryId"], result["projectPath"]) == (r.global_library, r.project) and result["projectModifiedByCheck"] is False, "Update check target differs")
        bounded(result["completeness"], False)
        return {"items": len(result["items"]), "mode": result["mode"]}
    r.ok("library_update_check", ("identity", "completeness"), "library_update_check of the global library", "library_update_check", {"request": {"libraryId": r.global_library}}, update_check)

    def all_messages(result):
        require(result["mode"] == "outOfDateAndUpToDate" and result["completeness"]["scopeComplete"] is True, "Update check incomplete")
        r.update_items = len(result["items"])
        return {"items": r.update_items}
    r.ok("library_update_check", ("completeness",), "library_update_check of the project library with up-to-date messages", "library_update_check",
         {"request": {"libraryId": r.library, "mode": "outOfDateAndUpToDate"}}, all_messages)
    if r.update_items > 1:
        r.ok("library_update_check", ("completeness",), "library_update_check with maxItems 1 is truncated", "library_update_check",
             {"request": {"libraryId": r.library, "mode": "outOfDateAndUpToDate", "maxItems": 1}}, lambda result: bounded(result["completeness"], True))
    r.refuse("library_update_check", "library_update_check of an unknown library", "library_update_check", {"request": {"libraryId": "0" * 32}}, ("LibraryIdentityMismatch",))

    def closed(native):
        def check(result):
            require(result["released"] is True and result["nativeClosed"] is native, "Close outcome differs: %s" % result)
            return result
        return check
    r.ok("library_close", ("identity", "completeness"), "library_close of the owned global library closes it", "library_close", {"libraryId": r.global_library}, closed(True))
    r.refuse("library_close", "library_query of the closed global library", "library_query", {"request": {"libraryId": r.global_library, "maxItems": 10}}, ("LibraryIdentityMismatch",))
    r.refuse("library_close", "library_close of an unknown library", "library_close", {"libraryId": "0" * 32}, ("LibraryIdentityMismatch",))
    r.ok("library_list", ("completeness",), "library_list no longer names the closed library", "library_list", {},
         lambda result: require(r.global_library not in {row["libraryId"] for row in result["libraries"]}, "The closed library is still listed"))


def two_plcs(r):
    """A CPU 1518 makes unqualified selectors ambiguous and is the documented target of loadable generation."""
    h, p1 = r.h, r.p1
    r.apply("add " + LOADABLE_CPU, "hardware_add", add_cpu(h, LOADABLE_CPU))
    plcs = [p["path"] for p in h.tool("project_inventory", {"maxItems": 1000})["plcs"]]
    require(len(plcs) == 2, "Expected two PLCs")
    r.p2 = next(p for p in plcs if p != p1)
    r.refuse("software_inspect", "software_inspect without a PLC selector", "software_inspect", {}, ("AmbiguousTarget",))
    r.refuse("plc.document_formats", "block_read of Main without a PLC selector", "block_read", {"blockName": "Main", "format": "simaticSd"}, ("AmbiguousTarget",))
    r.ok("plc.document_formats", ("identity",), "block_read of Main in the second PLC", "block_read", {"blockName": "Main", "plcPath": r.p2, "format": "simaticSd"},
         lambda result: require(result["identity"]["objectPath"] == r.p2 + "/blocks/Main", "Main of the second PLC not selected"))
    r.refuse("object_resolve", "object_resolve of Main without a PLC selector", "object_resolve", {"selectors": {"name": "Main"}}, ("AmbiguousTarget",))
    r.refuse("plc_alarm_inspect", "plc_alarm_inspect without a PLC selector", "plc_alarm_inspect", {}, ("AmbiguousTarget",))
    r.refuse("opcua_engineering_inspect", "opcua_engineering_inspect without a selector", "opcua_engineering_inspect", {}, ("AmbiguousTarget",))
    r.refuse("change_prepare", "change_prepare without a PLC selector", "change_prepare", {"kind": "block_rename", "payload": {"blockName": "Main", "newName": "Main2"}}, ("AmbiguousTarget",))
    r.refuse("change_prepare", "change_prepare of an unknown kind", "change_prepare", {"kind": "no_such_kind", "payload": {}}, ("UnsupportedChange",))

    def compared(result):
        require((result["plcPath"], result["target"]["plcPath"]) == (p1, r.p2) and result["project"]["path"] == r.project, "Compare targets differ")
        names = (p1.rsplit("/", 1)[1], r.p2.rsplit("/", 1)[1])
        require((result["root"]["left"], result["root"]["right"]) == names, "Compare root differs")
        require(result["completeness"]["complete"] is True and result["completeness"]["truncated"] is False, "Full compare incomplete")
        return {"visitedItems": result["completeness"]["visitedItems"]}
    r.ok("plc_compare", ("identity", "completeness"), "plc_compare of the two PLCs", "plc_compare", {"plcPath": p1, "targetPlcPath": r.p2}, compared)
    r.ok("plc_compare", ("completeness",), "plc_compare with maxItems 1 is truncated", "plc_compare", {"plcPath": p1, "targetPlcPath": r.p2, "maxItems": 1},
         lambda result: bounded(result["completeness"], True))
    r.refuse("plc_compare", "plc_compare with two targets", "plc_compare", {"plcPath": p1, "targetPlcPath": r.p2, "libraryId": r.library}, ("InvalidRequest",))
    r.refuse("plc_compare", "plc_compare with itself", "plc_compare", {"plcPath": p1, "targetPlcPath": p1}, ("InvalidRequest",))
    r.refuse("plc_compare", "plc_compare with an unknown PLC", "plc_compare", {"plcPath": p1, "targetPlcPath": p1 + "Missing"}, ("PlcNotFound",))
    r.refuse("plc_compare", "plc_compare with the closed library", "plc_compare", {"plcPath": p1, "libraryId": r.global_library}, ("LibraryIdentityMismatch",))

    main2 = r.p2 + "/blocks/Main"

    def loadable(result):
        require((result["plcPath"], result["target"], result["blockPaths"]) == (r.p2, "plc", [main2]), "Loadable target differs")
        require(result["completeness"]["complete"] is True, "Loadable not complete")
        return {"files": verify_artifacts(result["artifacts"], Path(result["artifacts"][0]["path"]).parent)}
    r.ok("plc_loadable_generate", ("identity", "completeness"), "plc_loadable_generate for Main of the CPU 1518", "plc_loadable_generate",
         {"target": "plc", "plcPath": r.p2, "blockPaths": [main2]}, loadable)
    r.refuse("plc_loadable_generate", "plc_loadable_generate on the CPU 1511", "plc_loadable_generate", {"target": "plc", "plcPath": p1, "blockPaths": [p1 + "/blocks/RcCaller"]},
             ("UnsupportedCapability",))
    r.refuse("plc_loadable_generate", "plc_loadable_generate of an unknown block", "plc_loadable_generate", {"target": "plc", "plcPath": r.p2, "blockPaths": [r.p2 + "/blocks/NoSuchBlock"]},
             ("ObjectNotFound",))
    r.refuse("plc_loadable_generate", "plc_loadable_generate for an unknown target", "plc_loadable_generate", {"target": "cloud", "plcPath": r.p2, "blockPaths": [main2]}, ("InvalidRequest",))


def change_tools(r):
    """The change tools' own identity and journal completeness, ending in a restore into a new directory."""
    h, p1 = r.h, r.p1
    scope = {"plcPath": p1}
    payload = {"groupKind": "blocks", "groupPath": p1 + "/blocks", "name": "RcRestore"}

    def prepared(result):
        require(result["preview"]["project"]["path"] == r.project, "Preview names another project")
        require(result["changeId"] and result["previewHash"], "Preview has no identity")
        r.prepared = result
        return {"changeId": result["changeId"], "effects": result["preview"].get("effects")}
    r.ok("change_prepare", ("identity", "completeness"), "change_prepare of group RcRestore", "change_prepare", {"kind": "group_create", "payload": payload, **scope}, prepared)
    change = {"changeId": r.prepared["changeId"]}
    r.ok("change_status", ("identity",), "change_status of the prepared change", "change_status", change,
         lambda result: require(result["changeId"] == change["changeId"] and result["state"] == "prepared", "Prepared status differs: %s" % result.get("state")))
    r.refuse("change_apply", "change_apply with another preview hash", "change_apply", {**h.apply_args(r.prepared), "previewHash": "0" * 64}, ("PreviewMismatch",))

    def applied(result):
        require(result.get("changeId") in (None, change["changeId"]) and result["state"] == "applied", "Apply outcome differs")
        stages = result["stages"]
        require([s for s in ("prechange_project_saved", "recovery_archive_created", "mutation_applied", "compile_finished", "changed_project_saved") if s in stages]
                == ["prechange_project_saved", "recovery_archive_created", "mutation_applied", "compile_finished", "changed_project_saved"], "Apply stages differ: %s" % stages)
        return {"stages": stages}
    r.ok("change_apply", ("identity", "completeness"), "change_apply of group RcRestore", "change_apply", h.apply_args(r.prepared), applied)

    def status(result):
        require(result["changeId"] == change["changeId"] and result["state"] == "applied" and result["recoveryArchiveSha256"], "Applied status differs")
        require("changed_project_saved" in result["stages"], "The journal lacks the save")
        return {"state": result["state"], "stages": result["stages"]}
    r.ok("change_status", ("completeness",), "change_status of the applied change", "change_status", change, status)
    r.refuse("change_status", "change_status of an unknown change", "change_status", {"changeId": "0" * 32}, ("ChangeNotFound",))
    r.refuse("change_restore", "change_restore of an unknown change", "change_restore", {"changeId": "0" * 32, "previewHash": "0" * 64}, ("ChangeNotFound",))

    def restored(result):
        require(result["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in result["stages"], "Restore outcome differs")
        path = result["recoveredProject"]["path"]
        require(Path(path).resolve() != Path(r.project).resolve(), "Recovery reused the project directory")
        require([p["path"] for p in h.tool("tia_session_status")["projects"]] == [path], "The recovered copy is not the open project")
        groups = next(s for s in h.tool("software_inspect", {"plcPath": p1})["softwareScopes"] if s["path"] == p1)["groups"]
        require(p1 + "/blocks/groups/RcEmpty" in groups and p1 + "/blocks/groups/RcRestore" not in groups, "Recovered groups differ")
        return {"recoveredProject": path}
    r.ok("change_restore", ("identity", "completeness"), "change_restore of group RcRestore into a new directory", "change_restore",
         {"changeId": change["changeId"], "previewHash": r.prepared["previewHash"]}, restored)


def body(h):
    r = Run(h)
    scratch_creation(r)
    seed(r)
    project_reads(r)
    hardware_reads(r)
    library_reads(r)
    two_plcs(r)
    change_tools(r)
    gaps = coverage_gaps(r.coverage)
    require(not gaps, "Features without identity, refusal and completeness evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "rc_", "Read-tool identity and completeness contracts in one owned scratch project", LIMITATIONS, []).run(body)
