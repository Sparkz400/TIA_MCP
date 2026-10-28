"""Native check of opcua_server_settings_set on one owned scratch CPU 1511-1 PN V4.1.

Refusals change nothing. V21 refuses to compile an active OPC UA server unless a user holds the
"OPC UA server access" device right, so umac_role_set first grants it to the scratch's own runtime role
(its identifier comes from a refused grant, which lists the CPU's available rights). Then the server is turned on with the Small runtime license through
change_apply (checkpoint, device compile, save), read back natively, kept across a fresh Portal reopen and
restored by change_restore to the exact before state. Attribute names the CPU exposes only after
activation are recorded for pinning. Observations of this scratch only.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re

from compilation_evidence import require_clean_compile
from library_native_harness import Harness, require
from qualify_plc_object_mutations import STAGES, in_order

KIND = "opcua_server_settings_set"
PINNED = {"serverEnabled": ("opcua", "OpcUaServer"), "runtimeLicense": ("cpu", "OpcUaPurchasedLicense")}
UNPINNED = ("port", "guestAccess", "autoAcceptClientCertificates")
LICENSES = {0: "NoLicense", 1: "SimaticOpcUaS71500Small"}


def body(h):
    plc = h.owned_scratch("opcua server settings")
    cpu = plc.rsplit("/plc/", 1)[0]
    project = h.tool("tia_session_status")["projects"][0]["path"]
    h.report.update(plc=plc, cpu=cpu, scratchCpu=h.scratch_cpu)

    def items():
        """Every attribute of the CPU and of its sub-item exposing OpcUaServer, by owner role."""
        settings = h.tool("opcua_engineering_inspect", {"plcPath": plc, "maxItems": 2000})["serverSettings"]
        server = [row["hardwarePath"] for row in settings if any(a["name"] == "OpcUaServer" for a in row["attributes"])]
        require(len(server) == 1, "Expected one OPC UA sub-item: %s" % server)
        result = {}
        for role, path in (("cpu", cpu), ("opcua", server[0])):
            rows = h.tool("hardware_inspect", {"hardwarePath": path, "maxItems": 2000})["items"]
            require(len(rows) == 1, "Expected one item at " + path)
            result[role] = {a["name"]: a.get("value") for a in rows[0]["attributes"]}
        return result

    def pinned(state):
        return {field: state[role][name] for field, (role, name) in PINNED.items()}

    before = h.stage("inspect before", items)
    require(pinned(before) == {"serverEnabled": False, "runtimeLicense": 0}, "Expected the server off without a license: %s" % pinned(before))

    def refuse(label, payload, code, **top):
        def check():
            error = h.call_error("change_prepare", {"kind": KIND, "plcPath": plc, "payload": payload, **top})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(items() == before, "Refused preparation changed the CPU")
            return error
        return h.stage("refuse " + label, check)

    refuse("an unknown field", {"serverEnabled": {"expected": False, "value": True}, "userPassword": "x"}, "InvalidRequest")
    refuse("an empty payload", {}, "InvalidRequest")
    refuse("a bare value", {"serverEnabled": True}, "InvalidRequest")
    refuse("a stale expected", {"serverEnabled": {"expected": True, "value": False}}, "ExpectedValueMismatch")
    refuse("an unknown license", {"runtimeLicense": {"expected": "NoLicense", "value": "Unlimited"}}, "InvalidRequest")
    refuse("a scopePath", {"serverEnabled": {"expected": False, "value": True}}, "InvalidRequest", scopePath=plc)
    for field in UNPINNED:
        refuse("unpinned " + field, {field: {"expected": 4840 if field == "port" else False, "value": 4841 if field == "port" else True}}, "UnsupportedCapability")

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
    require(pinned(before) == {"serverEnabled": False, "runtimeLicense": 0}, "The UMAC grant changed OPC UA settings: %s" % pinned(before))

    payload = {"serverEnabled": {"expected": False, "value": True}, "runtimeLicense": {"expected": "NoLicense", "value": LICENSES[1]}}
    prepared = h.stage("prepare " + KIND, lambda: h.tool("change_prepare", {"kind": KIND, "plcPath": plc, "payload": payload}))
    preview = prepared["preview"]
    require(preview["opcUaServerSettingsBefore"]["serverEnabled"]["value"] is False and preview["opcUaServerSettingsAfter"]["runtimeLicense"]["value"] == LICENSES[1]
            and {e["setting"] for e in preview["effects"]} == {"OpcUaServer", "OpcUaPurchasedLicense"}, "Preview differs: %s" % preview)
    applied = h.apply_prepared(KIND, prepared)
    require(in_order(applied["stages"], STAGES) and applied.get("recoveryArchiveSha256"), "Stages differ: %s" % applied["stages"])
    require(applied["compile"].get("targetIdentityPolicy") == "device-path-v1", "Expected the all-devices compile receipt")
    require_clean_compile(applied["compile"])
    readback = next(r for r in applied["readbacks"] if r.get("role") == "opcua-server-settings-readback")
    require(readback["serverEnabled"] is True and readback["runtimeLicense"] == LICENSES[1], "Readback differs: %s" % readback)

    after = h.stage("inspect after apply", items)
    require(pinned(after) == {"serverEnabled": True, "runtimeLicense": 1}, "Native values differ: %s" % pinned(after))
    h.report["exposedAfterActivation"] = {role: {name: after[role][name] for name in sorted(set(after[role]) - set(before[role]))} for role in after}
    h.report["changedByActivation"] = {role: {name: [before[role][name], after[role][name]] for name in sorted(set(after[role]) & set(before[role]))
                                              if before[role][name] != after[role][name]} for role in after}

    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(project))
    reopened = h.stage("inspect after reopen", items)
    require(pinned(reopened) == pinned(after), "Reopened values differ: %s" % pinned(reopened))
    h.stage("change_status after reopen", lambda: require(h.tool("change_status", {"changeId": prepared["changeId"]})["state"] == "applied", "Journal is not applied") or "applied")

    recovered = h.stage("restore", lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
    require(recovered["state"] == "restored" and Path(recovered["recoveredProject"]["path"]).resolve() != Path(project).resolve(), "Restore differs: %s" % recovered)
    restored = h.stage("inspect after restore", items)
    require(restored == before, "Restored attributes differ from the before state")
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
            ["Pinned fields only: serverEnabled and runtimeLicense; port, guest access and automatic client-certificate acceptance are refused.",
             "Offline project settings; nothing is downloaded and no OPC UA client connects."], []).run(body)
