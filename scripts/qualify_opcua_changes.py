"""Qualify the D04 OPC UA engineering kinds natively in one owned scratch CPU 1511-1 PN V4.1: server interface import
(create and replace), enable and delete, standard OPC UA role mappings, and the opcua_engineering_export read tool.

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
            "opcua_server_settings_set": "Qualified by scripts/qualify_opcua_server_settings.py (local.opcua.server-settings.20261028)."}
V1, V2 = ROOT / "fixtures/opcua/server-interface-v1.xml", ROOT / "fixtures/opcua/server-interface-v2.xml"
OTHER = ROOT / "fixtures/opcua/server-interface-other.xml"  # A second interface in its own namespace, deleted before the reopen.
NAME, ROLE, UA = "TiaMcpQ", "Operator", "http://opcfoundation.org/UA/"  # V21 creates custom roles only in a SIMATIC interface's namespace.
NAMESPACE, OTHER_NAMESPACE = "urn:tiamcp:interface:1", "urn:tiamcp:interface:2"  # The fixtures' namespaces.
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
    "opcua_role_mapping_set maps the standard roles Operator (with a project role) and Observer (without one). V21 creates custom roles only in "
    "the namespace of a SIMATIC server interface, which no change kind creates; a custom role elsewhere is refused before anything is written.",
    "opcua_engineering_export: the whole-server export and each server interface; SIMATIC interfaces and reference namespaces are empty in a scratch.",
    _CRASH,
    "Interface lastModified times are not compared: importing one interface moves the others' times; content is compared by export hash.",
    "TIA derives the interface namespace's row in the namespace access restrictions and in every role mapping from the interface: it appears on "
    "import and vanishes on delete, and a copy recovered from a checkpoint lacks it until TIA rebuilds it; those rows are not compared.",
    "TIA caches OPC UA role mappings per Portal process, keyed by an object id a recovered copy keeps: the Portal that restored a role "
    "change shows the mappings from before the restore. Role restores are therefore compared after a fresh Portal opens the recovered "
    "copy, and the worker refuses OPC UA changes in the restoring Portal (StaleRevision).",
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
    # TIA derives the interface namespace's rows from the interface itself: they appear on import, vanish on delete and are
    # missing from a checkpoint copy until TIA rebuilds them (29 October 2026), so they are left out of the compared state.
    derived = lambda rows: [row for row in rows if row.get("namespaceUri") not in (NAMESPACE, OTHER_NAMESPACE)]
    state = {"opcua:namespaceAccess": derived(inspected["namespaceAccessRestrictions"])}
    for row in inspected["serverSettings"]:
        state["opcua:settings:" + row["hardwarePath"]] = {a["name"]: a.get("value") for a in row["attributes"]}
    for key, rows in (("serverInterface", "serverInterfaces"), ("simaticInterface", "simaticInterfaces"), ("referenceNamespace", "referenceNamespaces"), ("roleMapping", "roleMappings")):
        for row in inspected[rows] or []:
            # TIA moves every interface's lastModified when another one is imported; content is compared by its export hash instead.
            kept = {k: v for k, v in row.items() if k != "lastModified"}
            state["opcua:%s:%s" % (key, row["name"])] = {**kept, **({"namespacePermissions": derived(row["namespacePermissions"])} if key == "roleMapping" else {})}
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

    def restore(self, label, kind, prepared, recovered=None):
        """The restoring Portal keeps showing the role mappings from before the restore (TIA caches them per process), so a role
        restore is compared after a fresh Portal opens the recovered copy; until then OPC UA changes are refused."""
        if kind != "opcua_role_mapping_set":
            return super().restore(label, kind, prepared, recovered)
        h = self.h
        recovered = h.stage("restore " + label, lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
        self.refuse(kind, "a change in the Portal that restored " + label, role_payload(self, "Engineer"), "StaleRevision")
        h.stage("close the restoring Portal", lambda: h.tool("tia_detach"))
        h.owned_portal("restored " + label)
        h.stage("open the recovered copy of " + label, lambda: h.reopen_project(recovered["recoveredProject"]["path"]))
        return super().restore(label, kind, prepared, recovered)


def interface(name):
    return "opcua:serverInterface:" + name


def import_payload(path, mode, name=NAME):
    return {"interfaceName": name, "mode": mode, "path": str(path.resolve()), "expectedSha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def import_(run, label, path, mode, restore=False, name=NAME):
    payload = import_payload(path, mode, name)
    effect = {"action": "opcua_server_interface_import", "name": name, "mode": mode, "sourceSha256": payload["expectedSha256"]}
    key = interface(name)
    if mode == "create":
        return run.apply(label, "opcua_server_interface_import", payload, effect, added=[key], rows={key: {"name": name, "enabled": True}}, restore=restore)
    before = run.current["view"][key]["exportSha256"]
    return run.apply(label, "opcua_server_interface_import", payload, effect, changed=[key], restore=restore,
                     check=lambda applied, state: require(state[key]["exportSha256"] != before, "Replacing did not change the interface content"))


def enable(run, enabled, restore=False):
    key = interface(NAME)
    return run.apply("%s the interface" % ("enable" if enabled else "disable"), "opcua_server_interface_enable", {"interfaceName": NAME, "enabled": enabled},
                     {"action": "opcua_server_interface_enable", "name": NAME, "enabled": enabled}, changed=[key], rows={key: {"enabled": enabled}}, restore=restore)


def delete(run, restore=False, name=NAME):
    return run.apply("delete the interface " + name, "opcua_server_interface_delete", {"interfaceName": name},
                     {"action": "opcua_server_interface_delete", "name": name}, removed=[interface(name)], restore=restore)


def role_payload(run, name=ROLE, project_role=True):
    """A standard role with Browse and Read in the OPC UA and the PLC namespace, mapped to the scratch's runtime role if project_role."""
    rows = [{"namespaceUri": uri, **{p: p in ("Browse", "Read") for p in PERMISSIONS}} for uri in (UA, run.plc_namespace)]
    return {"action": "create", "roleName": name, "definedInNamespace": UA, **({"projectRole": run.runtime_role} if project_role else {}), "namespacePermissions": rows}


def role_create(run, name=ROLE, project_role=True, restore=False):
    payload, key = role_payload(run, name, project_role), "opcua:roleMapping:" + name
    def granted(applied, state):
        row = state[key]
        require(row["definedInNamespace"] == UA and row["projectRole"] == (run.runtime_role if project_role else ""), "Role mapping differs: %s" % row)
        rights = {p["namespaceUri"]: tuple(p[k] for k in ("browse", "read", "write", "call", "receiveEvents", "readRolePermissions")) for p in row["namespacePermissions"]}
        require(all(rights.get(uri) == (True, True, False, False, False, False) for uri in (UA, run.plc_namespace)), "Namespace permissions differ: %s" % rights)
    return run.apply("map the %s role" % name, "opcua_role_mapping_set", payload, dict(payload), added=[key], check=granted, restore=restore)


def role_remove(run, name=ROLE, restore=False):
    return run.apply("remove the %s role mapping" % name, "opcua_role_mapping_set", {"action": "remove", "roleName": name},
                     {"action": "remove", "roleName": name}, removed=["opcua:roleMapping:" + name], restore=restore)


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
                           "interfaces": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (V1, V2, OTHER)}}


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
    run.refuse(k, "removing the required Anonymous mapping", {"action": "remove", "roleName": "Anonymous"}, "ProtectedObject")
    run.refuse(k, "an unknown standard role", role_payload(run, "TiaMcpRole"), "InvalidRequest")
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
    missing = h.stage("refuse exporting a missing interface name", lambda: h.call_error("opcua_engineering_export", {"plcPath": run.plc, "category": "serverInterface", "name": "TiaMcpMissing"}))
    require(missing.get("code") == "ObjectNotFound", "Expected ObjectNotFound for a missing interface name: %s" % missing)


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
    run.refuse("opcua_role_mapping_set", "a custom role in the imported interface's namespace", {**role_payload(run, "TiaMcpRole"), "definedInNamespace": NAMESPACE}, "UnsupportedCapability")
    run.refuse("opcua_server_interface_import", "creating an existing interface", import_payload(V1, "create"), "ObjectExists")
    run.refuse("opcua_server_interface_enable", "no explicit enabled flag", {"interfaceName": NAME}, "InvalidRequest")
    stale = run.prepare("disable the interface (made stale below)", "opcua_server_interface_enable", {"interfaceName": NAME, "enabled": False})
    import_(run, "replace the interface", V2, "replace")
    run.refuse_apply("opcua_server_interface_enable", "a preview made stale by a later change", stale, "StaleRevision")
    run.refuse_apply("opcua_server_interface_enable", "a wrong previewHash", {**run.prepare("disable the interface", "opcua_server_interface_enable", {"interfaceName": NAME, "enabled": False}), "previewHash": "0" * 64}, "PreviewMismatch")
    enable(run, False)
    import_(run, "import a second interface", OTHER, "create", name="TiaMcpQ2")
    delete(run, name="TiaMcpQ2")
    role_create(run)
    export(run)
    reopen(run)
    # Phase B: one more change per kind from the reopened state, each restored from its checkpoint.
    enable(run, True, restore=True)
    import_(run, "replace the interface again", V1, "replace", restore=True)
    role_create(run, "Observer", project_role=False, restore=True)
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
    Harness(args, "oq_", "D04 OPC UA server interfaces, role mappings and export in one owned scratch CPU 1511-1 PN V4.1", LIMITATIONS, [V1, V2, OTHER]).run(body)
