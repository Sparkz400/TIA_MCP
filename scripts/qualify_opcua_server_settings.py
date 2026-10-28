"""Native check of opcua_server_settings_set on one owned scratch CPU 1511-1 PN V4.1.

Refusals change nothing. V21 refuses to compile an active OPC UA server unless a user holds the
"OPC UA server access" device right, so umac_role_set first grants it to the scratch's own runtime role
(its identifier comes from a refused grant, which lists the CPU's available rights). Then:
  A. server on with the Small runtime license (checkpoint, device compile, save, native readback), then
     change_restore in the applying Portal back to the exact before state;
  B. the same change again on the restored copy, then C. port and automatic certificate acceptance,
     which the CPU exposes only while its server is on;
  a fresh Portal reopen keeps B and C. Observations of this scratch only.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from compilation_evidence import require_clean_compile
from library_native_harness import Harness, require
from qualify_plc_object_mutations import STAGES, in_order

KIND = "opcua_server_settings_set"
PINNED = {"serverEnabled": ("opcua", "OpcUaServer"), "port": ("opcua", "OpcUaPort"),
          "autoAcceptClientCertificates": ("opcua", "OpcUaAutoAcceptAllClientCertificates"), "runtimeLicense": ("cpu", "OpcUaPurchasedLicense")}
SMALL = "SimaticOpcUaS71500Small"
ENABLE = {"serverEnabled": {"expected": False, "value": True}, "runtimeLicense": {"expected": "NoLicense", "value": SMALL}}
TUNE = {"port": {"expected": 4840, "value": 4841}, "autoAcceptClientCertificates": {"expected": False, "value": True}}
# Attributes activation itself changes besides the requested ones: TIA assigns a server certificate on compile.
ACTIVATION_SIDE_EFFECTS = {"opcua": {"OpcUaServerCertificate"}, "cpu": set()}


def body(h):
    plc = h.owned_scratch("opcua server settings")
    cpu = plc.rsplit("/plc/", 1)[0]
    h.report.update(plc=plc, cpu=cpu, scratchCpu=h.scratch_cpu)

    def items():
        """Every attribute value of the CPU and of its sub-item exposing OpcUaServer, by owner role; types go to the report."""
        settings = h.tool("opcua_engineering_inspect", {"plcPath": plc, "maxItems": 2000})["serverSettings"]
        server = [row["hardwarePath"] for row in settings if any(a["name"] == "OpcUaServer" for a in row["attributes"])]
        require(len(server) == 1, "Expected one OPC UA sub-item: %s" % server)
        result = {}
        for role, path in (("cpu", cpu), ("opcua", server[0])):
            rows = h.tool("hardware_inspect", {"hardwarePath": path, "maxItems": 2000})["items"]
            require(len(rows) == 1, "Expected one item at " + path)
            result[role] = {a["name"]: a.get("value") for a in rows[0]["attributes"]}
            h.report.setdefault("attributeTypes", {}).setdefault(role, {}).update({a["name"]: a.get("type") for a in rows[0]["attributes"]})
        return result

    def pinned(state):
        return {field: state[role].get(name) for field, (role, name) in PINNED.items()}

    def changed(old, new):
        return {role: sorted(n for n in set(old[role]) | set(new[role]) if old[role].get(n) != new[role].get(n)) for role in old}

    def refuse(label, payload, code, **top):
        def check():
            state = items()
            error = h.call_error("change_prepare", {"kind": KIND, "plcPath": plc, "payload": payload, **top})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(items() == state, "Refused preparation changed the CPU")
            return error
        return h.stage("refuse " + label, check)

    def apply(label, payload, effects):
        prepared = h.stage("prepare " + label, lambda: h.tool("change_prepare", {"kind": KIND, "plcPath": plc, "payload": payload}))
        preview = prepared["preview"]
        require(preview["payload"] == payload and {e["setting"] for e in preview["effects"]} == effects
                and all(preview["opcUaServerSettingsBefore"][f]["value"] == v["expected"] and preview["opcUaServerSettingsAfter"][f]["value"] == v["value"]
                        for f, v in payload.items()), "Preview of %s differs: %s" % (label, preview))
        applied = h.apply_prepared(KIND, prepared)
        require(in_order(applied["stages"], STAGES) and applied.get("recoveryArchiveSha256"), "Stages differ: %s" % applied["stages"])
        require(applied["compile"].get("targetIdentityPolicy") == "device-path-v1", "Expected the all-devices compile receipt")
        require_clean_compile(applied["compile"])
        readback = next(r for r in applied["readbacks"] if r.get("role") == "opcua-server-settings-readback")
        require(all(readback[f] == v["value"] for f, v in payload.items()), "Readback of %s differs: %s" % (label, readback))
        return prepared

    before0 = h.stage("inspect before", items)
    require(pinned(before0) == {"serverEnabled": False, "port": None, "autoAcceptClientCertificates": None, "runtimeLicense": 0},
            "Expected the server off without a license: %s" % pinned(before0))
    inventory = h.inventory()
    refuse("an unknown field", {**ENABLE, "userPassword": "x"}, "InvalidRequest")
    refuse("an empty payload", {}, "InvalidRequest")
    refuse("a bare value", {"serverEnabled": True}, "InvalidRequest")
    refuse("a stale expected", {"serverEnabled": {"expected": True, "value": False}}, "ExpectedValueMismatch")
    refuse("an unknown license", {"runtimeLicense": {"expected": "NoLicense", "value": "Unlimited"}}, "InvalidRequest")
    refuse("a scopePath", ENABLE, "InvalidRequest", scopePath=plc)
    refuse("guest access", {"guestAccess": {"expected": False, "value": True}}, "UnsupportedCapability")
    refuse("port while the server is off", {"port": TUNE["port"]}, "UnsupportedCapability")
    refuse("certificate acceptance while the server is off", {"autoAcceptClientCertificates": TUNE["autoAcceptClientCertificates"]}, "UnsupportedCapability")

    umac = h.tool("access_inspect", {"maxItems": 2000})["projectUmac"]
    roles, scopes = [n for n in umac["roleNames"].values() if n.startswith("TiaMcpRuntime_")], umac["deviceRightScopes"]
    require(len(roles) == 1 and len(scopes) == 1, "Expected one scratch runtime role and one UMAC device: %s %s" % (roles, scopes))
    grant = {"action": "grant", "roleName": roles[0], "deviceRights": [{"device": scopes[0], "identifier": "TiaMcpUnknownRight"}]}
    listed = h.stage("list the CPU's device rights", lambda: h.call_error("change_prepare", {"kind": "umac_role_set", "payload": grant}))
    require(listed.get("code") == "ObjectNotFound", "Expected ObjectNotFound: %s" % listed)
    h.report["availableDeviceRights"] = listed["details"]["availableRights"]
    rights = [r for r in h.report["availableDeviceRights"] if re.search(r"opc ?ua", r["name"] or "", re.I) and re.search(r"server", r["name"] or "", re.I)]
    require(len(rights) == 1, "Expected one OPC UA server access right: %s" % h.report["availableDeviceRights"])
    h.report["opcUaServerAccessRight"] = rights[0]
    h.apply("umac_role_set", {**grant, "deviceRights": [{"device": scopes[0], "identifier": rights[0]["identifier"]}]})
    before = h.stage("inspect after the UMAC grant", items)
    require(before == before0, "The UMAC grant changed OPC UA settings")

    a = apply("enable the server", ENABLE, {"OpcUaServer", "OpcUaPurchasedLicense"})
    after = h.stage("inspect after enabling", items)
    require(pinned(after) == {"serverEnabled": True, "port": 4840, "autoAcceptClientCertificates": False, "runtimeLicense": 1}, "Native values differ: %s" % pinned(after))
    h.report["exposedWhileServerOn"] = {role: {n: after[role][n] for n in sorted(set(after[role]) - set(before[role]))} for role in after}
    side = {role: [n for n in names if n in before[role] and n not in {PINNED[f][1] for f in ENABLE}] for role, names in changed(before, after).items()}
    require(all(set(names) <= ACTIVATION_SIDE_EFFECTS[role] for role, names in side.items()), "Unexpected attribute changes: %s" % side)
    h.report["activationSideEffects"] = side
    require(h.inventory() == inventory, "Enabling the server changed blocks, types or tag tables")
    recovered = h.stage("restore in the applying Portal", lambda: h.tool("change_restore", {"changeId": a["changeId"], "previewHash": a["previewHash"]}))
    require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"], "Restore differs: %s" % recovered)
    project = recovered["recoveredProject"]["path"]
    require(h.stage("inspect after restore", items) == before, "Restored attributes differ from the before state")

    b = apply("enable the server again", ENABLE, {"OpcUaServer", "OpcUaPurchasedLicense"})
    on = h.stage("inspect after enabling again", items)
    refuse("port 0", {"port": {"expected": 4840, "value": 0}}, "InvalidRequest")
    refuse("port 65536", {"port": {"expected": 4840, "value": 65536}}, "InvalidRequest")
    refuse("a stale port", {"port": {"expected": 4841, "value": 4842}}, "ExpectedValueMismatch")
    c = apply("set port and certificate acceptance", TUNE, {"OpcUaPort", "OpcUaAutoAcceptAllClientCertificates"})
    tuned = h.stage("inspect after tuning", items)
    require(changed(on, tuned) == {"cpu": [], "opcua": ["OpcUaAutoAcceptAllClientCertificates", "OpcUaPort"]}, "Tuning changed more: %s" % changed(on, tuned))

    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(project))
    require(h.stage("inspect after reopen", items) == tuned, "Reopened attributes differ")
    for label, prepared in (("enable again", b), ("tune", c)):
        h.stage("change_status after reopen: " + label, lambda p=prepared: require(h.tool("change_status", {"changeId": p["changeId"]})["state"] == "applied", "Journal is not applied") or "applied")
    h.report["qualified"] = [KIND]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "uq_", "Native check of opcua_server_settings_set on an owned scratch CPU 1511-1 PN V4.1",
            ["guestAccess is refused: V21 models it as the UMAC anonymous user's OPCUAServerAccess right.",
             "Offline project settings; nothing is downloaded and no OPC UA client connects."], []).run(body)
