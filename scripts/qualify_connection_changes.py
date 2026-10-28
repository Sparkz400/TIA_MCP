"""Qualify the D08 connection change kinds natively in one owned scratch CPU 1511-1 PN with a second CPU on one subnet.

Phase A adds the second CPU and the subnet (the hardware runner's fixture), then applies connection_create (S7 and TCP),
connection_set and connection_delete with checkpoint, device compile and save. Each preview is asserted against the request
and connection_inspect, and a complete state (the hardware runner's hardware, device inventory and program state plus every
connection_inspect row) must differ from the previous one in exactly the intended keys. A fresh Portal then reopens the saved
project, which must show the identical state. Phase B applies one more change per kind from that state and restores its
checkpoint in the applying Portal; the recovered copy must equal the pre-change state. Typed refusals must change nothing.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from qualify_hardware_changes import Run as HardwareRun, add_cpu, network
from qualify_plc_object_mutations import coverage_gaps, diff

KINDS = ("connection_create", "connection_set", "connection_delete")
CRITERIA = {
    "AC-identity": "refused: typed refusals of unknown connection types, missing owners and connections, attributes outside the allowlist and stale owner tables, each leaving the complete state unchanged.",
    "AC-preview": "Every preview is asserted against the request and connection_inspect before apply; a wrong previewHash refuses at apply.",
    "AC-preserve": "applied: every connection_inspect row, the configured hardware, device inventory and every PLC's program exports differ in exactly the intended keys.",
    "AC-native": "applied + persisted: checkpoint, device compile and save stages, change_status, connection_inspect readback and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "Two CPU 1511-1 PN V4.1 on one subnet; S7 and TCP connections between their X1 nodes only. ISO, ISO-on-TCP, UDP, FDL, PtP and HMI connections, "
    "unspecified partners and program instructions or HMI tags that use a connection are not exercised.",
    "connection_set writes one allowlisted attribute per change; resource and capacity rules are left to the device compile. TIA keeps the partner "
    "CPU's counterpart in step (changed on set, removed on delete); this is recorded under sideEffects.",
    "Every device item of these CPUs, the rail and the ports included, exposes CommunicationManagement natively, so no owner-capability refusal exists here.",
    "Engineering only; nothing is downloaded and no communication is observed.",
]


def connections(call):
    """Every configured connection by its connection_inspect id, without the per-call observation envelope."""
    result = call("connection_inspect", {"maxItems": 2000})
    require(result["completeness"]["truncated"] is False and result["reviewSnapshot"]["connectionsComplete"] is True, "connection_inspect is incomplete")
    return {"conn:" + row["id"]: row for row in result["reviewSnapshot"]["connections"]}


class Run(HardwareRun):
    def __init__(self, h):
        super().__init__(h)
        h.report.update(criteria=CRITERIA)

    def domains(self):
        return {**super().domains(), **connections(self.h.tool)}


def ends(run):
    cpu = run.path("ScratchStation", "items", "ScratchPLC")
    peer = run.path("HwPeer", "items", "HwPeerCpu")
    return cpu, peer, cpu + "/items/PROFINET%20interface_1", peer + "/items/PROFINET%20interface_1"


def create(run, kind, restore=False):
    cpu, peer, local, partner = ends(run)
    payload = {"hardwarePath": cpu, "connectionType": kind, "localInterfacePath": local, "localNodeName": "X1",
               "partnerTargetPath": peer, "partnerInterfacePath": partner, "partnerNodeName": "X1"}
    before = set(run.current["view"])
    run.apply("create a %s" % kind, "connection_create", payload,
              {"effects": [{"createConnection": kind, "localInterfacePath": local, "localNodeName": "X1", "partnerTargetPath": peer, "partnerInterfacePath": partner, "partnerNodeName": "X1"}]},
              added=lambda applied, state: [key for key in state if key.startswith("conn:") and key not in before], restore=restore)
    added = sorted(key for key in run.current["view"] if key.startswith("conn:") and key not in before)
    require(added, "connection_inspect shows no new %s" % kind)
    return [run.current["view"][key] for key in added]


def own(run, rows):
    cpu = ends(run)[0]
    return next(row for row in rows if row["id"].startswith(cpu + "/connections/"))


def counterpart(run, row):
    """The partner CPU's side of an owner's connection, which TIA keeps in step with it."""
    return "conn:" + ends(run)[1] + "/connections/" + row["id"].rsplit("/", 1)[1]


def set_attribute(run, row, name, value, restore=False):
    cpu = ends(run)[0]
    payload = {"hardwarePath": cpu, "connectionName": row["properties"]["LocalConnectionName"], "attributes": {name: value}}
    run.apply("set %s's %s" % (row["properties"]["LocalConnectionName"], name), "connection_set", payload,
              {"effects": [{"connection": row["id"], "attributes": [{"name": name, "after": value}]}]},
              changed=["conn:" + row["id"]], rows={"conn:" + row["id"]: {"properties": {name: value}}},
              tolerated=lambda category, key: category == "changed" and key == counterpart(run, row), restore=restore)


def delete(run, row, restore=False):
    cpu = ends(run)[0]
    run.apply("delete " + row["properties"]["LocalConnectionName"], "connection_delete", {"hardwarePath": cpu, "connectionName": row["properties"]["LocalConnectionName"]},
              {"effects": [{"deleteConnection": row}]}, removed=["conn:" + row["id"]],
              tolerated=lambda category, key: category == "removed" and key == counterpart(run, row), restore=restore)


def phase_a(run):
    add_cpu(run, "add CPU HwPeer", "HwPeer")
    network(run)
    cpu, peer, local, partner = ends(run)
    base = {"hardwarePath": cpu, "localInterfacePath": local, "localNodeName": "X1", "partnerTargetPath": peer, "partnerInterfacePath": partner, "partnerNodeName": "X1"}
    run.refuse("connection_create", "an unknown connection type", {**base, "connectionType": "ModbusConnection"}, "InvalidRequest")
    run.refuse("connection_create", "a missing owner", {**base, "hardwarePath": cpu + "X", "connectionType": "S7Connection"}, "HardwareNotFound")
    s7 = own(run, create(run, "S7Connection"))
    tcp = own(run, create(run, "TcpConnection"))
    name = tcp["properties"]["LocalConnectionName"]
    run.refuse("connection_set", "an attribute outside the allowlist", {"hardwarePath": cpu, "connectionName": name, "attributes": {"LocalConnectionName": "X"}}, "UnsupportedAttribute")
    run.refuse("connection_set", "no attributes", {"hardwarePath": cpu, "connectionName": name, "attributes": {}}, "InvalidRequest")
    run.refuse("connection_set", "a missing connection", {"hardwarePath": cpu, "connectionName": "TiaMcpMissing", "attributes": {"LocalPort": 2001}}, "ObjectNotFound")
    run.refuse("connection_delete", "a missing connection", {"hardwarePath": cpu, "connectionName": "TiaMcpMissing"}, "ObjectNotFound")
    stale = run.prepare("delete %s (made stale below)" % name, "connection_delete", {"hardwarePath": cpu, "connectionName": name})
    run.refuse_apply("connection_delete", "a wrong previewHash", {**stale, "previewHash": "0" * 64}, "PreviewMismatch")
    set_attribute(run, tcp, "LocalPort", 2001)
    run.refuse_apply("connection_delete", "an owner table changed since preparation", stale, "StaleRevision")
    delete(run, s7)
    run.h.report["fixture"] = {"tcp": run.current["view"]["conn:" + tcp["id"]]}


def reopen(run):
    h, final = run.h, run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.b0 = run.snap("reopened")
    h.stage("reopened state equals every verified change",
            lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or len(final["view"]))
    for kind, rows in list(run.coverage.items()):
        if rows.get("applied"):
            run.mark(kind, "persisted", "fresh Portal reopen")


def phase_b(run):
    tcp = run.h.report["fixture"]["tcp"]
    create(run, "S7Connection", restore=True)
    set_attribute(run, tcp, "LocalPort", 2002, restore=True)
    delete(run, tcp, restore=True)


def body(h):
    run = Run(h)
    h.owned_scratch("connection changes")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.current = run.snap("baseline")
    phase_a(run)
    reopen(run)
    phase_b(run)
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "cn_", "D08 connection changes between two owned scratch CPUs 1511-1 PN", LIMITATIONS, []).run(body)
