"""Native probe of the wave 7-10 offline change kinds and reads on one owned scratch CPU 1511-1 PN.

Exploratory: each probe records its exact outcome and the run continues. A probe that fails after its
apply was dispatched restores that change's checkpoint before the next probe. Passing probes are
native observations of this scratch only, not qualification of their features.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
from pathlib import Path

from library_native_harness import Harness, require

PROBE_SOURCE = """FUNCTION_BLOCK "TiaMcpProbeFb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      enable : Bool;
   END_VAR
   VAR
      count : Int;
   END_VAR
BEGIN
	IF #enable THEN #count := #count + 1; END_IF;
END_FUNCTION_BLOCK

DATA_BLOCK "TiaMcpProbeFb_DB"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
"TiaMcpProbeFb"
BEGIN
END_DATA_BLOCK

FUNCTION "TiaMcpProbeCaller" : Void
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
BEGIN
	"TiaMcpProbeFb_DB"(enable := TRUE);
END_FUNCTION
"""


def error_of(result):
    return (result.get("structuredContent") or {}).get("error") or {"message": str(result.get("content"))}


def attempt(h, kind, payload, scope=None):
    """Prepare; if prepared, apply. Returns the refusal or the applied result; raises on a failed apply."""
    result = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": kind, "payload": payload, **(scope or {})}})
    if result.get("isError"):
        return {"prepared": False, "error": error_of(result)}
    prepared = result["structuredContent"]
    h.last = (kind, prepared)
    applied = h.apply_prepared(kind, prepared)
    h.last = None
    return {"prepared": True, "preview": prepared.get("preview"), "applied": {k: applied.get(k) for k in ("state", "stages", "readbacks", "verification")}}


def probe(h, name, operation):
    row = {"name": name}
    h.report.setdefault("probes", []).append(row)
    h.last = None
    print("PROBE " + name, flush=True)
    try:
        row.update(outcome="observed", result=operation())
    except Exception as error:
        row.update(outcome="failed", failure=str(error))
        if h.last:
            kind, prepared = h.last
            restore = h.client.request("tools/call", {"name": "change_restore", "arguments": {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}})
            row["restore"] = error_of(restore) if restore.get("isError") else (restore.get("structuredContent") or {}).get("state")
    print("  -> " + row["outcome"] + (": " + row.get("failure", "")[:300] if row["outcome"] == "failed" else ""), flush=True)
    h.persist()


def body(h):
    plc = h.owned_scratch("wave probe")
    cpu = plc.rsplit("/plc/", 1)[0]
    h.report["plc"], h.report["cpu"] = plc, cpu

    def web():
        inspected = h.tool("web_inspect", {"hardwarePath": cpu})
        rows = [r for r in inspected.get("cpus", inspected.get("items", [])) if isinstance(r, dict)]
        enabled = next((r.get("webEnabled") for r in rows if r.get("webEnabled") is not None), None)
        refused = attempt(h, "webserver_page_set", {"systemWebPages": {"expected": False, "value": True}}, {"plcPath": plc})
        result = {"webEnabled": enabled, "inspect": inspected, "pagesWhileInactive": refused}
        if enabled is not True:
            require(not refused["prepared"] and refused["error"].get("code") == "WebServerNotActivated", "Expected WebServerNotActivated: %s" % refused)
            result["activateWithPages"] = attempt(h, "webserver_page_set", {"webServerActive": {"expected": False, "value": True},
                                                  "systemWebPages": {"expected": False, "value": True}}, {"plcPath": plc})
            result["deactivate"] = attempt(h, "webserver_page_set", {"webServerActive": {"expected": True, "value": False}}, {"plcPath": plc})
        return result
    probe(h, "web server inspect and page set", web)

    def umac():
        before = h.tool("access_inspect", {"hardwarePath": cpu})
        created = attempt(h, "umac_role_set", {"action": "create", "roleName": "TiaMcpProbeRole"})
        result = {"accessBefore": before, "create": created}
        if created["prepared"]:
            umac = created["preview"]["umacBefore"]
            right = next(r for role in umac["roles"] if not role["system"] for r in role["deviceRights"])
            user = next(p["name"] for p in umac["principals"] if p["kind"] == "projectUser" and p["active"])
            for action, extra in (("grant", {"deviceRights": [right]}), ("assign", {"userName": user}), ("unassign", {"userName": user}), ("revoke", {"deviceRights": [right]})):
                result[action] = attempt(h, "umac_role_set", {"action": action, "roleName": "TiaMcpProbeRole", **extra})
            result["accessAfter"] = h.tool("access_inspect", {"hardwarePath": cpu})
        return result
    probe(h, "UMAC custom role create", umac)

    def certificates():
        before = h.tool("certificate_inspect", {"hardwarePath": cpu})
        now = datetime.now(timezone.utc).replace(microsecond=0)
        created = attempt(h, "certificate_create", {"hardwarePath": cpu, "usage": "WebServer", "subjectCommonName": "TiaMcpProbe",
                          "subjectAlternativeNames": [{"type": "Dns", "value": "tiamcp-probe.local"}],
                          "validFromUtc": now.isoformat().replace("+00:00", "Z"), "validUntilUtc": (now + timedelta(days=365)).isoformat().replace("+00:00", "Z")})
        result = {"before": before, "create": created}
        if created["prepared"]:
            new = created["applied"]["readbacks"][0]["certificate"]["certificateId"]
            old = created["preview"]["effects"][0]["storeCertificateIds"]
            result["newId"] = new
            if old == [1]:  # The scratch's PLC communication certificate is the store's only certificate.
                swap = {"hardwarePath": cpu, "targetPath": cpu, "attributeName": "PlcCommunicationCertificate"}
                result["assign"] = attempt(h, "certificate_assign", {**swap, "certificateId": new, "expectedCertificateId": 1})
                result["assignBack"] = attempt(h, "certificate_assign", {**swap, "certificateId": 1, "expectedCertificateId": new})
            result["delete"] = attempt(h, "certificate_delete", {"hardwarePath": cpu, "certificateId": new})
        return result
    probe(h, "certificate create, assign and delete", certificates)

    def instances():
        upsert = attempt(h, "source_upsert", {"scopePath": plc, "language": "SCL", "source": PROBE_SOURCE,
                         "expectedBlockNames": ["TiaMcpProbeFb", "TiaMcpProbeFb_DB", "TiaMcpProbeCaller"]})
        require(upsert["prepared"], "Probe blocks were refused: %s" % upsert)
        lookup = h.tool("instance_lookup", {"plcPath": plc, "blockName": "TiaMcpProbeFb"})
        return {"upsert": upsert["applied"], "lookup": lookup}
    probe(h, "instance lookup of an FB with a global instance and caller", instances)

    def vci():
        inspected = h.tool("vci_workspace_inspect")
        groups = [g for g in inspected.get("groups", []) if g.get("kind") == "user"] or inspected.get("groups", [])
        require(groups, "No workspace group: %s" % inspected)
        root = h.directory.parent / ("vci-" + h.directory.name)  # Outside the artifact store and the project.
        result = {"inspect": inspected, "create": attempt(h, "vci_workspace_create", {"groupPath": groups[0]["path"], "name": "TiaMcpProbe",
                  "rootPath": str(root), "language": h.args.language})}
        if result["create"]["prepared"]:
            after = h.tool("vci_workspace_inspect")
            result["after"] = after
            created = [w for g in [after] for w in g.get("workspaces", []) if w.get("name") == "TiaMcpProbe"]
            if len(created) == 1:
                result["delete"] = attempt(h, "vci_workspace_delete", {"workspacePath": created[0]["path"]})
        return result
    probe(h, "VCI workspace create and delete", vci)

    h.report["summary"] = {p["name"]: p["outcome"] for p in h.report["probes"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--language", default="en-US")
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "wp_", "Exploratory native probe of wave 7-10 change kinds on an owned scratch CPU 1511-1 PN",
            ["Exploratory probe: observations of one scratch project, not feature qualification.",
             "Web server activation is not writable through any change kind, so page settings are probed only for their refusal."], []).run(body)
