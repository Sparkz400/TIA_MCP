"""Qualify composite_engineering_capture natively in one owned scratch CPU 1511-1 PN with a second CPU and an S7 connection.

Phase A builds the fixture with verified change kinds (a second CPU on one subnet, an S7 connection, PID_Compact and a
virtual TO_PositioningAxis), saves a project review with project_snapshot_capture, and captures one composite phase from it.
The capture must report each domain against an independent read of the same project: the AutomationML document retained
through automationml_inspect, one connection review whose rows equal connection_inspect, drive_observe's result (no drives
here) and one technology-profile record per technology object of the saved review whose relationships equal technology_read's
exposed connection slots. The capture must change nothing in the project. Typed refusals cover a malformed and an unknown
reviewId. A fresh Portal reopens the saved project, which captures again with the same records per domain. Phase B deletes the
connection and restores its checkpoint: the recovered copy, at another path, refuses the original review as ProjectMismatch
and captures from its own new review with the same connection and technology content.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from library_native_harness import Harness, require
from qualify_connection_changes import Run as ConnectionRun, add_cpu, network, create, own, delete as delete_connection
from qualify_plc_object_mutations import diff

PID = ("PID_Compact", "3.0")
AXIS = ("TO_PositioningAxis", "10.0")
CRITERIA = {
    "AC-identity": "refused: a malformed and an unknown reviewId, and a saved review of another project path (the recovered copy) as ProjectMismatch, each producing no phase and leaving the project unchanged.",
    "AC-completeness": "Every domain reports native or unavailable with its records: the AML capture, one connection review whose rows equal connection_inspect, drive_observe's (empty) result, "
                       "and one technology record per saved technology object whose relationships equal technology_read's exposed slots; the reopened and the recovered project capture the same content.",
    "AC-native": "Captures from a native owned scratch after compile and save, after a fresh owned Portal reopen and from a checkpoint-recovered copy, each leaving the project state unchanged.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, each capture's domain summary and limitations.",
}
LIMITATIONS = [
    "Two CPU 1511-1 PN V4.1 on one subnet with one S7 connection, PID_Compact 3.0 and a virtual TO_PositioningAxis 10.0 whose hardware slots are not connected; "
    "no drives (drive capture: drive_observe's empty result), no TO hardware-module or channel connection and no more than 32 records, so truncation is not reached.",
    "The capture is not an atomic project revision; freshness and source authenticity remain unverified, as the result itself states.",
    "composite_engineering_rebind_plan is not qualified here: it plans only declared TO hardware relationships, and no verified flow creates one yet.",
    "Engineering only; nothing is downloaded.",
]


class Run(ConnectionRun):
    plc_compiled = ("to_create",)

    def __init__(self, h):
        super().__init__(h)
        h.report.update(criteria=CRITERIA, captures={})

    def args(self, kind, payload, **top):
        return {"kind": kind, "payload": payload, **({"plcPath": self.plc} if kind.startswith("to_") else {}), **top}

    def to(self, name):
        return self.plc + "/technology/" + name


def generated(run):
    """TIA adds a technology object's instructions and types to the system groups and generates protected motion OBs
    with their system constants for an axis (as in the technology runner)."""
    before = run.current["view"]
    def tolerated(category, key):
        row = (run.current["view"] if category == "added" else before).get(key) or {}
        family = key.split(":", 1)[0]
        return category in ("added", "removed") and (family in ("block", "type") and ("/systemGroups/" in key or row.get("isKnowHowProtected") is True)
                                                      or family == "member" and "/systemConstants/" in key)
    return tolerated


def create_to(run, name, family, parameters=None):
    payload = {"name": name, "typeIdentifier": family[0], "version": family[1], **({"parameters": parameters} if parameters else {})}
    prepared = run.prepare("create %s %s" % (family[0], name), "to_create", payload,
                           {"effects": [{"createTechnologyObject": name, "typeIdentifier": family[0], "version": family[1]}]})
    run.finish("create %s %s" % (family[0], name), "to_create", prepared, tolerated=generated(run))


def review(run, label):
    saved = run.h.stage("save project review " + label, lambda: run.h.tool("project_snapshot_capture", {"request": {"maxBlocks": 0}}))
    require(len(saved["reviewId"]) == 64, "No content-hash reviewId: %s" % saved)
    return saved["reviewId"]


def capture(run, label, review_id):
    """composite_engineering_capture checked domain by domain against independent reads of the same open project."""
    h = run.h
    def check():
        before = run.state()
        result = h.tool("composite_engineering_capture", {"reviewId": review_id})
        status = h.tool("tia_session_status")["projects"][0]
        require(result["schemaVersion"] == "composite-engineering-capture.v1" and result["reviewId"] == review_id
                and Path(result["projectPath"]).resolve() == Path(status["path"]).resolve(), "Capture identity differs: %s" % {k: result.get(k) for k in ("reviewId", "projectPath")})
        require(result["composable"] is True and result["nativeQualified"] is False and result["wholeProjectCaptured"] is False, "Capture claims differ")
        domains = {row["domain"]: row for row in result["captures"]}
        require(sorted(domains) == ["automationMl", "connections", "drives", "technology"], "Capture domains differ: %s" % sorted(domains))
        phase = result["phase"]
        retained = lambda domain: [r["recordRef"] for r in domains[domain]["records"] if r["status"] == "retained"]
        for domain in domains.values():
            require(domain["truncated"] is False and all(r["status"] == "retained" for r in domain["records"]), "Domain %s failed or truncated: %s" % (domain["domain"], domain))

        aml = domains["automationMl"]
        require(aml["status"] == "native" and retained("automationMl") == [phase["automationMlRef"]], "AML capture differs: %s" % aml)
        reopened = h.tool("automationml_reopen", {"recordRef": phase["automationMlRef"]})
        require(reopened.get("recordRef", phase["automationMlRef"]) == phase["automationMlRef"], "The AML record does not reopen")

        inspected = h.tool("connection_inspect", {"maxItems": 512})["reviewSnapshot"]["connections"]
        connections = domains["connections"]
        require(connections["status"] == "native" and retained("connections") == phase["connections"] and len(phase["connections"]) == 1, "Connection capture differs: %s" % connections)
        rows = h.tool("connection_review_page", {"recordId": phase["connections"][0], "section": "observations", "count": 200})["rows"]
        require(len(rows) == 2 * len(inspected), "The connection review holds %d observation rows for %d connections" % (len(rows), len(inspected)))

        drives = domains["drives"]
        try:
            observed = h.tool("drive_observe", {"maxItems": 2000})
            require(drives["status"] == "native" and not observed["drives"] and phase["drives"] == [], "Drive capture differs from drive_observe: %s" % drives)
        except RuntimeError as error:
            require(drives["status"] == "unavailable" and phase["drives"] == [] and drives.get("errorCode") in str(error), "Drive capture differs from the drive_observe refusal: %s" % drives)

        technology = domains["technology"]
        objects = sorted(o["path"] for o in h.tool("technology_read", {"plcPath": run.plc, "maxItems": 500})["technologyObjects"])
        sources = sorted(r["source"] for r in technology["records"])
        require(technology["status"] == "native" and sources == objects and len(phase["technology"]) == len(objects) == 2, "Technology capture differs: %s / %s" % (sources, objects))
        for record in technology["records"]:
            slots = [s for s in h.tool("technology_read", {"plcPath": run.plc, "technologyPath": record["source"], "maxItems": 2000})["hardwareConnections"] if s.get("status") != "notExposed"]
            page = h.tool("technology_profile_report", {"recordId": record["recordRef"], "section": "relationships", "count": 200})["rows"]
            require(len(page) == 2 * len(slots), "%s: %d relationship rows for %d exposed slots" % (record["source"], len(page), len(slots)))
        require(run.state() == before, "The capture changed the project")
        summary = {d: {"status": domains[d]["status"], "records": len(domains[d]["records"])} for d in sorted(domains)}
        summary["technologyObjects"] = objects
        return {"result": summary, "phase": phase}
    out = h.stage("capture composite phase " + label, check)
    h.report["captures"][label] = out["result"]
    return out


def refuse_capture(run, label, review_id, code):
    def check():
        before = run.state()
        error = run.h.call_error("composite_engineering_capture", {"reviewId": review_id})
        require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
        require(run.state() == before, "Refused capture changed the project")
        return error
    run.h.stage("refuse composite_engineering_capture: " + label, check)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("composite capture")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.current = run.snap("baseline")
    add_cpu(run, "add CPU HwPeer", "HwPeer")
    network(run)
    s7 = own(run, create(run, "S7Connection"))
    create_to(run, "CoPid", PID)
    create_to(run, "CoAxis", AXIS, {"VirtualAxis.Mode": 1})
    first_review = review(run, "of the fixture")
    refuse_capture(run, "a malformed reviewId", first_review.upper(), "InvalidRequest")
    refuse_capture(run, "an unknown reviewId", "0" * 64, "ArtifactUnavailable")
    first = capture(run, "of the fixture", first_review)

    final = run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.b0 = run.snap("reopened")
    h.stage("reopened state equals the fixture", lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or len(final["view"]))
    again = capture(run, "after the fresh reopen", first_review)
    h.stage("the reopened project captures the same content", lambda: require(h.report["captures"]["after the fresh reopen"] == h.report["captures"]["of the fixture"], "Captures differ") or True)

    delete_connection(run, s7, restore=True)
    refuse_capture(run, "the original review on the recovered copy", first_review, "ProjectMismatch")
    recovered = capture(run, "of the recovered copy", review(run, "of the recovered copy"))
    h.stage("the recovered copy captures the same content", lambda: require(h.report["captures"]["of the recovered copy"] == h.report["captures"]["of the fixture"], "Captures differ") or True)
    plan = h.stage("rebind plan over the fixture and recovered phases (observation only)", lambda: rebind(run, first["phase"], recovered["phase"]))
    h.report["rebindObservation"] = plan


def rebind(run, before, after):
    """Records what the plan says for two native phases without declared TO hardware relationships; not a qualification."""
    import base64, hashlib, json
    data = json.dumps({"schemaVersion": "composite-engineering-request.v3", "before": before, "after": after, "mappings": [], "correspondences": [],
                       "allowDifferentProjectCopy": True}).encode("utf-8")
    begun = run.h.tool("artifact_upload_begin", {"fileName": "composite.json", "expectedBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    run.h.tool("artifact_upload_chunk", {"uploadId": begun["uploadId"], "offset": 0, "data": base64.b64encode(data).decode("ascii")})
    upload = run.h.tool("artifact_upload_commit", {"uploadId": begun["uploadId"]})["artifactRef"]
    record = run.h.tool("composite_engineering_review", {"uploadRef": upload})["recordRef"]
    plan = run.h.tool("composite_engineering_rebind_plan", {"recordRef": record})
    return {k: plan[k] for k in ("brokenConnectionCount", "notCapturedAfterCount", "truncated", "applied")}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "cc_", "C02 composite_engineering_capture in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
