"""Qualify the D04 OPC UA engineering kinds natively in one owned scratch CPU 1511-1 PN V4.1: server interface import
(create and replace), enable and delete, OPC UA role mappings, and the opcua_engineering_export read tool.

Setup grants the scratch's runtime role the OPC UA server access right and switches the server on with the Small runtime
license (opcua_server_settings_set, qualified by scripts/qualify_opcua_server_settings.py). Phase A applies every kind
with checkpoint, PLC compile and save; each preview is asserted against the request, and a complete state (every block,
type and tag-table export plus the opcua_engineering_inspect configuration and a content hash of every server interface's
native export) must differ from the previous one in exactly the intended keys. A fresh Portal then reopens the saved
project, which must show the identical state. Phase B applies one more change per kind and restores its checkpoint; the
recovered copy must equal the pre-change state. Typed refusals must change nothing. Phase C exports the whole server and
each interface with opcua_engineering_export.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import re

from compilation_evidence import require_plc_compile
from library_native_harness import Harness, require
from qualify_access_changes import Run as StateRun, reopen
from qualify_plc_object_mutations import coverage_gaps, view
from qualification_owned_scratch import complete_inventory

ROOT = Path(__file__).resolve().parents[1]
KINDS = ("opcua_server_interface_import", "opcua_server_interface_enable", "opcua_server_interface_delete", "opcua_role_mapping_set")
_CRASH = ("V21 Update 3: ReferenceNamespaces.Create terminates TIA (\"Unexpected exception\", Portal disposed) for every NodeSet tried, "
          "including the OPC Foundation DI 1.05.0 NodeSet, headless and with TIA's user interface, with the OPC UA server on or off "
          "(29 October 2026); this run reproduces it last with fixtures/opcua/machine-v1.xml.")
# D04 kinds this run does not qualify; scripts/test_qualify_opcua_changes.py keeps KINDS plus these equal to the catalog's opcua_* change kinds.
EXCLUDED = {"opcua_reference_namespace_import": _CRASH, "opcua_reference_namespace_delete": "Needs an imported reference namespace. " + _CRASH,
            "opcua_server_settings_set": "Qualified by scripts/qualify_opcua_server_settings.py (local.opcua_server_settings.20261028)."}
V1, V2 = ROOT / "fixtures/opcua/server-interface-v1.xml", ROOT / "fixtures/opcua/server-interface-v2.xml"
NAME, ROLE, UA = "TiaMcpQ", "Operator", "http://opcfoundation.org/UA/"
NAMESPACE = "urn:tiamcp:interface:1"  # The fixtures' namespace.
SMALL = "SimaticOpcUaS71500Small"
PERMISSIONS = ("Browse", "Read", "Write", "Call", "ReceiveEvents", "ReadRolePermissions")
CRITERIA = {
    "AC-identity": "refused: typed refusals of unknown fields, scopePath, invalid modes, relative or changed import files, missing or existing interfaces and role mappings, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request before apply; a preview made stale by a later change refuses at apply.",
    "AC-preserve": "applied: every program export, the inspected OPC UA configuration and each interface's export hash differ in exactly the intended keys.",
    "AC-native": "applied + persisted: checkpoint, PLC compile and save stages, change_status and a fresh owned Portal reopen with identical state and applied journals.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, fixture and limitations.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN V4.1 with the OPC UA server on and the Small runtime license; other CPUs and licenses are not exercised.",
    "Server interfaces hold authored objects without PLC variable mappings (fixtures/opcua/server-interface-v1.xml and -v2.xml); mapped variables, methods and SiOME exports are not imported.",
    "Role mappings: one OPC UA standard role (Operator) mapped to the scratch's runtime project role with namespace permissions; custom namespace roles are not created.",
    "opcua_engineering_export: the whole-server export and each server interface; SIMATIC interfaces and reference namespaces are empty in a scratch.",
    _CRASH,
    "Importing or deleting an interface makes TIA add or remove its namespace row in the namespace access restrictions and in every role mapping; "
    "these are recorded under sideEffects and checked to differ only by that namespace.",
    "Engineering only; nothing is downloaded and no OPC UA client connects.",
]


def normalized_hash(path):
    """SHA-256 of a native export without its export-time LastModified attribute."""
    text = Path(path).read_text(encoding="utf-8-sig")
    return hashlib.sha256(re.sub(r'\sLastModified="[^"]*"', "", text, count=1).encode("utf-8")).hexdigest()


def opcua_state(h, plc):
    """The inspected OPC UA configuration, keyed per object, plus each server interface's normalized export hash."""
    inspected = h.tool("opcua_engineering_inspect", {"plcPath": plc, "maxItems": 2000})
    # With the server on, TIA renders the server certificate attribute display-only; any other warning is a gap.
    warnings = [w for w in inspected.get("warnings") or [] if w.get("valueStatus") != "displayOnly"]
    require(not inspected["completeness"].get("truncated") and not warnings, "OPC UA inspection is truncated or warned: %s" % warnings)
    state = {"opcua:namespaceAccess": inspected["namespaceAccessRestrictions"]}
    for row in inspected["serverSettings"]:
        state["opcua:settings:" + row["hardwarePath"]] = {a["name"]: a.get("value") for a in row["attributes"]}
    for key, rows in (("serverInterface", "serverInterfaces"), ("simaticInterface", "simaticInterfaces"), ("referenceNamespace", "referenceNamespaces"), ("roleMapping", "roleMappings")):
        for row in inspected[rows] or []:
            state["opcua:%s:%s" % (key, row["name"])] = dict(row)
    if inspected["serverInterfaces"]:
        exported = h.tool("opcua_engineering_export", {"plcPath": plc, "category": "serverInterface", "maxItems": 32})
        require(not exported["warnings"] and len(exported["files"]) == len(inspected["serverInterfaces"]), "Server interface export is incomplete: %s" % exported["warnings"])
        for row in exported["files"]:
            state["opcua:serverInterface:" + row["name"]]["exportSha256"] = normalized_hash(row["artifact"]["path"])
    return state


class Run(StateRun):
    """The access runner's apply/refuse/restore gates over the PLC program and the OPC UA configuration."""
    plc_compiled = KINDS

    def __init__(self, h):
        super().__init__(h)
        for key in ("certificateIdentity", "notQualified"):
            h.report.pop(key)
        h.report.update(criteria=CRITERIA, excluded=EXCLUDED)

    def args(self, kind, payload, **top):
        return {"kind": kind, "plcPath": self.plc, "payload": payload, **top}

    def domains(self):
        return opcua_state(self.h, self.plc)

    def state(self):
        return {**view(complete_inventory(self.h.tool)), **self.domains()}

    def apply(self, label, kind, payload, effect, **expected):
        prepared = self.prepare(label, kind, payload, {"effects": [effect]})
        self.finish(label, kind, prepared, **expected)
        return prepared


def namespace_rows(category, key):
    """Adding or deleting an interface adds or removes its namespace in the access restrictions and in every role mapping."""
    return category == "changed" and (key == "opcua:namespaceAccess" or key.startswith("opcua:roleMapping:"))


def only_namespace_rows(before, state):
    """Those keys may differ only by rows of the fixtures' namespace."""
    def strip(value):
        if isinstance(value, list):
            return [row for row in value if row.get("namespaceUri") != NAMESPACE]
        return {**value, "namespacePermissions": strip(value["namespacePermissions"])}
    for key in before.keys() & state.keys():
        if namespace_rows("changed", key) and before[key] != state[key]:
            require(strip(before[key]) == strip(state[key]), "%s changed beyond the %s rows" % (key, NAMESPACE))


def interface(name):
    return "opcua:serverInterface:" + name


def import_payload(path, mode, name=NAME):
    return {"interfaceName": name, "mode": mode, "path": str(path.resolve()), "expectedSha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def import_(run, label, path, mode, restore=False):
    payload = import_payload(path, mode)
    effect = {"action": "opcua_server_interface_import", "name": NAME, "mode": mode, "sourceSha256": payload["expectedSha256"]}
    key, previous = interface(NAME), run.current["view"]
    if mode == "create":
        return run.apply(label, "opcua_server_interface_import", payload, effect, added=[key], rows={key: {"name": NAME, "enabled": True}}, restore=restore,
                         tolerated=namespace_rows, check=lambda applied, state: only_namespace_rows(previous, state))
    before = run.current["view"][key]["exportSha256"]
    return run.apply(label, "opcua_server_interface_import", payload, effect, changed=[key], restore=restore,
                     check=lambda applied, state: require(state[key]["exportSha256"] != before, "Replacing did not change the interface content"))


def enable(run, enabled, restore=False):
    key = interface(NAME)
    return run.apply("%s the interface" % ("enable" if enabled else "disable"), "opcua_server_interface_enable", {"interfaceName": NAME, "enabled": enabled},
                     {"action": "opcua_server_interface_enable", "name": NAME, "enabled": enabled}, changed=[key], rows={key: {"enabled": enabled}}, restore=restore)


def delete(run, restore=False):
    previous = run.current["view"]
    return run.apply("delete the interface", "opcua_server_interface_delete", {"interfaceName": NAME},
                     {"action": "opcua_server_interface_delete", "name": NAME}, removed=[interface(NAME)], restore=restore,
                     tolerated=namespace_rows, check=lambda applied, state: only_namespace_rows(previous, state))


def role_payload(run):
    rows = [{"namespaceUri": uri, **{p: p in ("Browse", "Read") for p in PERMISSIONS}} for uri in (UA, run.plc_namespace)]
    return {"action": "create", "roleName": ROLE, "definedInNamespace": UA, "projectRole": run.runtime_role, "namespacePermissions": rows}


def role_create(run, restore=False):
    payload, key = role_payload(run), "opcua:roleMapping:" + ROLE
    def granted(applied, state):
        row = state[key]
        require(row["definedInNamespace"] == UA and row["projectRole"] == run.runtime_role, "Role mapping differs: %s" % row)
        rights = {p["namespaceUri"]: (p["browse"], p["read"], p["write"], p["call"]) for p in row["namespacePermissions"]}
        require(all(rights.get(uri) == (True, True, False, False) for uri in (UA, run.plc_namespace)), "Namespace permissions differ: %s" % rights)
    return run.apply("map the Operator role", "opcua_role_mapping_set", payload,
                     {"action": "create", "roleName": ROLE, "definedInNamespace": UA, "projectRole": run.runtime_role}, added=[key], check=granted, restore=restore)


def role_remove(run, restore=False):
    return run.apply("remove the Operator mapping", "opcua_role_mapping_set", {"action": "remove", "roleName": ROLE},
                     {"action": "remove", "roleName": ROLE}, removed=["opcua:roleMapping:" + ROLE], restore=restore)


def setup(run):
    """Grant the runtime role the OPC UA server access right and switch the server on with the Small license."""
    h = run.h
    umac = h.tool("access_inspect", {"maxItems": 2000})["projectUmac"]
    roles, scopes = [n for n in umac["roleNames"].values() if n.startswith("TiaMcpRuntime_")], umac["deviceRightScopes"]
    require(len(roles) == 1 and len(scopes) == 1, "Expected one scratch runtime role and one UMAC device: %s %s" % (roles, scopes))
    grant = {"action": "grant", "roleName": roles[0], "deviceRights": [{"device": scopes[0], "identifier": "TiaMcpUnknownRight"}]}
    listed = h.stage("list the CPU's device rights", lambda: h.call_error("change_prepare", {"kind": "umac_role_set", "payload": grant}))
    rights = [r for r in listed["details"]["availableRights"] if re.search(r"opc ?ua", r["name"] or "", re.I) and re.search(r"server", r["name"] or "", re.I)]
    require(len(rights) == 1, "Expected one OPC UA server access right: %s" % listed["details"]["availableRights"])
    h.apply("umac_role_set", {**grant, "deviceRights": [{"device": scopes[0], "identifier": rights[0]["identifier"]}]})
    on = {"serverEnabled": {"expected": False, "value": True}, "runtimeLicense": {"expected": "NoLicense", "value": SMALL}}
    prepared = h.stage("prepare the OPC UA server on", lambda: h.tool("change_prepare", {"kind": "opcua_server_settings_set", "plcPath": run.plc, "payload": on}))
    h.apply_prepared("opcua_server_settings_set", prepared)
    run.runtime_role = roles[0]
    anonymous = next(r for r in h.tool("opcua_engineering_inspect", {"plcPath": run.plc})["roleMappings"] if r["name"] == "Anonymous")
    run.plc_namespace = next(p["namespaceUri"] for p in anonymous["namespacePermissions"] if p["namespaceUri"].startswith("urn:SIMATIC:"))
    h.report["fixture"] = {"plc": run.plc, "runtimeRole": run.runtime_role, "plcNamespace": run.plc_namespace, "opcUaServerAccessRight": rights[0],
                           "interfaces": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (V1, V2)}}


def refusals(run):
    k = "opcua_server_interface_import"
    good = import_payload(V1, "create")
    run.refuse(k, "an unsupported field", {**good, "password": "x"}, "InvalidRequest")
    run.refuse(k, "a scopePath", good, "InvalidRequest", scopePath=run.plc)
    run.refuse(k, "an unknown mode", {**good, "mode": "merge"}, "InvalidRequest")
    run.refuse(k, "a relative path", {**good, "path": V1.name}, "InvalidRequest")
    run.refuse(k, "a changed file", {**good, "expectedSha256": "0" * 64}, "ArtifactChanged")
    run.refuse(k, "replacing a missing interface", import_payload(V1, "replace"), "ObjectNotFound")
    run.refuse("opcua_server_interface_enable", "a missing interface", {"interfaceName": NAME, "enabled": False}, "ObjectNotFound")
    run.refuse("opcua_server_interface_delete", "a missing interface", {"interfaceName": NAME}, "ObjectNotFound")
    k = "opcua_role_mapping_set"
    run.refuse(k, "creating the existing Anonymous mapping", {**role_payload(run), "roleName": "Anonymous"}, "ObjectExists")
    run.refuse(k, "removing a missing mapping", {"action": "remove", "roleName": ROLE}, "ObjectNotFound")
    run.refuse(k, "a permission row without explicit booleans", {**role_payload(run), "namespacePermissions": [{"namespaceUri": UA, "Browse": True}]}, "InvalidRequest")
    run.refuse(k, "an unknown action", {"action": "rename", "roleName": ROLE}, "InvalidRequest")


def export(run):
    """Phase C: opcua_engineering_export of the whole server and of each server interface."""
    h = run.h
    def server():
        result = h.tool("opcua_engineering_export", {"plcPath": run.plc, "category": "server"})
        files = result["files"]
        require(len(files) == 1 and not result["warnings"] and Path(files[0]["artifact"]["path"]).is_file(), "Whole-server export differs: %s" % result)
        require(hashlib.sha256(Path(files[0]["artifact"]["path"]).read_bytes()).hexdigest() == files[0]["artifact"]["sha256"], "Export hash differs")
        require(not files[0].get("registrationError"), "The server export did not register as a model: %s" % files[0].get("registrationError"))
        return {"bytes": files[0]["artifact"]["bytes"], "sha256": files[0]["artifact"]["sha256"]}
    h.report["exports"] = {"server": h.stage("export the whole OPC UA server", server)}
    def one():
        result = h.tool("opcua_engineering_export", {"plcPath": run.plc, "category": "serverInterface", "name": NAME})
        require([f["name"] for f in result["files"]] == [NAME] and not result["warnings"], "Interface export differs: %s" % result)
        return {"sha256": result["files"][0]["artifact"]["sha256"], "normalized": normalized_hash(result["files"][0]["artifact"]["path"])}
    h.report["exports"]["serverInterface"] = h.stage("export the server interface", one)
    missing = h.stage("export a missing interface name", lambda: h.tool("opcua_engineering_export", {"plcPath": run.plc, "category": "serverInterface", "name": "TiaMcpMissing"}))
    require(missing["files"] == [], "A missing name exported files: %s" % missing)


def reference_boundary(run):
    """Last, because TIA terminates: the reference-namespace import, recorded once and never retried."""
    h, path = run.h, ROOT / "fixtures/opcua/machine-v1.xml"
    payload = {"namespaceName": "TiaMcpRef", "mode": "create", "path": str(path.resolve()), "expectedSha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    prepared = run.prepare("import a reference namespace", "opcua_reference_namespace_import", payload,
                           {"effects": [{"action": "opcua_reference_namespace_import", "name": "TiaMcpRef", "mode": "create", "sourceSha256": payload["expectedSha256"]}]})
    def dispatch():
        outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        error = (outcome.get("structuredContent") or {}).get("error") or {}
        session = h.client.request("tools/call", {"name": "tia_session_status", "arguments": {}})
        require(outcome.get("isError") is True and "Unexpected exception" in error.get("message", ""), "Expected TIA's unexpected exception: %s" % outcome)
        require(session.get("isError") is True, "TIA is still running after the reference-namespace import")
        return {"error": error, "sessionAfter": (session.get("structuredContent") or {}).get("error")}
    h.report["referenceNamespaceBoundary"] = h.stage("apply the reference-namespace import (TIA terminates)", dispatch)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("opcua changes")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    setup(run)
    run.current = run.snap("baseline")
    refusals(run)
    # Phase A: applied, then persisted by a fresh Portal reopen.
    import_(run, "import the interface", V1, "create")
    run.refuse("opcua_server_interface_import", "creating an existing interface", import_payload(V1, "create"), "ObjectExists")
    run.refuse("opcua_server_interface_enable", "no explicit enabled flag", {"interfaceName": NAME}, "InvalidRequest")
    stale = run.prepare("disable the interface (made stale below)", "opcua_server_interface_enable", {"interfaceName": NAME, "enabled": False})
    import_(run, "replace the interface", V2, "replace")
    run.refuse_apply("opcua_server_interface_enable", "a preview made stale by a later change", stale, "StaleRevision")
    run.refuse_apply("opcua_server_interface_enable", "a wrong previewHash", {**run.prepare("disable the interface", "opcua_server_interface_enable", {"interfaceName": NAME, "enabled": False}), "previewHash": "0" * 64}, "PreviewMismatch")
    enable(run, False)
    role_create(run)
    export(run)
    reopen(run)
    # Phase B: one more change per kind from the reopened state, each restored from its checkpoint.
    enable(run, True, restore=True)
    import_(run, "replace the interface again", V1, "replace", restore=True)
    role_remove(run, restore=True)
    delete(run, restore=True)
    gaps = coverage_gaps(run.coverage, KINDS)
    h.report["qualified"] = [kind for kind in KINDS if kind not in gaps]
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)
    reference_boundary(run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "oq_", "D04 OPC UA server interfaces, role mappings and export in one owned scratch CPU 1511-1 PN V4.1", LIMITATIONS, [V1, V2]).run(body)
