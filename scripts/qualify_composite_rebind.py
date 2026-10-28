"""Qualify composite_engineering_rebind_plan natively in one owned scratch CPU 1511-1 PN with a declared module replacement.

The fixture uses verified change kinds plus hardware_channel_attributes OperatingMode: a TM PosInput 2 (RbEncA) whose channel 0
runs in MotionControl mode and a virtual TO_PositioningAxis (RbAxis) whose sensor 0 is connected to that channel with to_connect,
so the before phase holds one declared TO hardware relationship. The replacement plugs a second TM PosInput 2 (RbEncB) in the
same mode and disconnects the sensor with to_disconnect; the after phase then shows the relationship disconnected. A
composite_engineering_review over both phases declares the model correspondence RbEncA -> RbEncB, and
composite_engineering_rebind_plan must list exactly that broken connection with a to_connect payload naming RbEncB and the
carried-over channel. That payload is prepared unchanged and applied (checkpoint, compile, save); a third capture resolves the
sensor to RbEncB and the plan from the before phase to it reports no broken connection. A review without the correspondence
plans no payload, and malformed or altered composite handles are refused.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path

from library_native_harness import Harness, require
from qualify_composite_capture import AXIS, Run as CaptureRun, capture, generated, review
from qualify_hardware_changes import under
from qualify_technology_changes import portable

# TM PosInput 2 V2.0, the module of fixtures/encoder-hardware; channel 0 is its technology channel.
ENCODER = "OrderNumber:6ES7 551-1AB01-0AB0/V2.0"
CHANNEL = {"channelType": "Technology", "channelIoType": "Complex", "channelNumber": 0}
CONFIGURED = ("hardware_plug", "hardware_channel_attributes")
CRITERIA = {
    "AC-identity": "Each planned entry names the exact technology record, technologyPath, relationship ID, before target and correspondence ID of the native captures; "
                   "a malformed and an altered composite handle are refused with no plan.",
    "AC-completeness": "Over the before and after phases the plan lists exactly the one broken declared relationship (sensor 0, disconnected after) with its single declared "
                       "replacement; without the correspondence it stays explicit as noDeclaredReplacement without a payload; after the rebind no connection is broken.",
    "AC-preview": "The planned to_connect payload is prepared unchanged, its preview asserted, applied with checkpoint, compile and save, and read back natively; "
                  "a third capture resolves the relationship to the replacement module.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, the three captures, each plan and its review record.",
}
LIMITATIONS = [
    "One CPU 1511-1 PN V4.1 with two TM PosInput 2 V2.0 (channel 0 in MotionControl mode) and one virtual TO_PositioningAxis 10.0 (VirtualAxis.Mode = 1) whose "
    "sensor 0 is the only connected slot; one model correspondence. AML correspondences through hardware mappings, actor, torque, encoder and measuring-input "
    "roles, conflicting replacements and truncation are not exercised natively.",
    "The replacement is declared, not inferred: the plan carries the before channel selector over to the replacement module, which this fixture shares by type.",
    "TIA couples native effects that are recorded under sideEffects: OperatingMode applies to both channels and shrinks the module's output range (24 to 8 bytes); "
    "a mode change may renumber the submodule's hardware identifier (Local~<module>); a sensor connection puts the item's addresses into the servo process image "
    "and adds RbAxis_Sensor1_Interface_AddressIn/Out tags (PD_TEL83) with their system types; to_disconnect keeps those tags, so the next connection adds "
    "copies suffixed (1).",
    "Engineering only; nothing is downloaded.",
]


class Run(CaptureRun):
    plc_compiled = ("to_create", "to_connect", "to_disconnect")

    def __init__(self, h):
        super().__init__(h)
        h.report.update(criteria=CRITERIA, plans={})

    def domains(self):
        """The capture runner's state plus each technology object's native connection readback."""
        state = super().domains()
        if getattr(self, "plc", None):
            for row in self.h.tool("technology_read", {"plcPath": self.plc, "maxItems": 500})["technologyObjects"]:
                detail = self.h.tool("technology_read", {"plcPath": self.plc, "technologyPath": row["path"], "maxItems": 2000})
                state["to:" + row["path"]] = portable(detail["hardwareConnections"])
        return state

    def apply(self, label, kind, payload, preview=None, **expected):
        prepared = self.prepare(label, kind, payload, preview, expected.pop("shown", None))
        applied = self.finish(label, kind, prepared, **expected)
        require(("configured_hardware_preservation_verified" in applied["stages"]) == (kind in CONFIGURED),
                "Configured-hardware preservation stage differs for %s: %s" % (kind, applied["stages"]))
        return prepared


def plug(run, name, slot):
    rail = run.path("ScratchStation", "items", "Rail_0")
    module = run.path("ScratchStation", "items", name)
    run.apply("plug " + name, "hardware_plug", {"hardwarePath": rail, "typeIdentifier": ENCODER, "name": name, "positionNumber": slot},
              {"effects": [{"parentPath": rail, "createItem": name, "positionNumber": slot, "typeIdentifier": ENCODER}]},
              added=under(module), rows={"hw:" + module: {"typeIdentifier": ENCODER, "positionNumber": slot}},
              tolerated=lambda category, key: category == "added" and "/systemConstants/" in key)
    return owner(run, module)


def owner(run, module):
    """The item under a module that owns the technology channel (TIA gives the module and its submodule one name)."""
    rows = [key[3:] for key, row in run.current["view"].items() if key.startswith("hw:" + module) and any(
        (c["type"], c["ioType"], c["number"]) == tuple(CHANNEL.values()) for c in row.get("channels") or [])]
    require(len(rows) == 1, "Expected one technology channel owner under %s: %s" % (module, rows))
    return rows[0]


def identifier(run, item):
    """TIA may give the reconfigured submodule a new hardware identifier, which its system constant Local~<module> reports."""
    constant = "member:" + run.plc + "/tags/Default%20tag%20table/systemConstants/Local~" + item.rsplit("/", 1)[1]
    return lambda category, key: category == "changed" and key == constant


def motion(run, item):
    """hardware_channel_attributes OperatingMode = MotionControl, in the native representation hardware_inspect reports."""
    channel = next(c for c in run.current["view"]["hw:" + item]["channels"] if (c["type"], c["ioType"], c["number"]) == tuple(CHANNEL.values()))
    attribute = next(a for a in channel["attributes"] if a["name"] == "OperatingMode")
    value = next(v["value"] for v in attribute["nativeEnumValues"] if v["name"] == "MotionControl")
    run.apply("set %s channel 0 to MotionControl" % item.rsplit("/", 1)[1], "hardware_channel_attributes", {"hardwarePath": item, **CHANNEL, "attributes": {"OperatingMode": value}},
              {"attributeChanges": [{"name": "OperatingMode", "after": value}]}, changed=["hw:" + item], tolerated=identifier(run, item), check=lambda applied, state: require(
                  [a["value"] for c in state["hw:" + item]["channels"] for a in c["attributes"] if a["name"] == "OperatingMode"] == [value] * len(state["hw:" + item]["channels"]),
                  "TIA does not report MotionControl on every channel of %s" % item))
    return {"before": attribute.get("value"), "after": value}


def create_axis(run):
    payload = {"name": "RbAxis", "typeIdentifier": AXIS[0], "version": AXIS[1], "parameters": {"VirtualAxis.Mode": 1}}
    run.apply("create virtual TO_PositioningAxis RbAxis", "to_create", payload,
              {"effects": [{"createTechnologyObject": "RbAxis", "typeIdentifier": AXIS[0], "version": AXIS[1]}]},
              added=["to:" + run.to("RbAxis")], tolerated=generated(run))


def coupled(run, item):
    """TIA puts the connected item's addresses into the servo process image (ProcessImage) and keeps the sensor's PROFIdrive
    interface tags RbAxis_Sensor<n>_Interface_AddressIn/Out (and so their tag table) in step, besides generated()."""
    table = run.plc + "/tags/Default%20tag%20table"
    system, renumbered = generated(run), identifier(run, item)
    return lambda category, key: system(category, key) or renumbered(category, key) or category == "changed" and key in ("hw:" + item, "tagTable:" + table) or (
        category in ("added", "removed") and key.startswith("member:" + table + "/tags/RbAxis_Sensor"))


def link(run, kind, payload, item):
    axis = run.to("RbAxis")
    def connected(applied, state):
        row = next(r for r in state["to:" + axis] if r["role"] == "sensor" and r["observationPath"].endswith("/0"))
        require(row["isConnected"] is (kind == "to_connect"), "technology_read sensor 0 of RbAxis: %s" % row)
        if kind == "to_connect":
            # An axis sensor reports its channel as an attribute value reference.
            channels = [a["value"] for a in row.get("attributes") or [] if isinstance(a.get("value"), dict) and a["value"].get("kind") == "channel"]
            require([(c["parent"]["hardwarePath"], c["type"], c["ioType"], c["number"]) for c in channels] == [(payload["hardwarePath"], *CHANNEL.values())],
                    "Sensor 0 is not on the requested channel: %s" % row)
    module = item.rsplit("/", 1)[1]
    return run.apply("connect RbAxis sensor 0 to " + module if kind == "to_connect" else "disconnect RbAxis sensor 0 from " + module, kind, payload,
                     {"effects": [{"technologyObject": "RbAxis", "technologyPath": axis, "action": kind}]},
                     shown={**payload, "name": "RbAxis"}, changed=["to:" + axis], check=connected, tolerated=coupled(run, item))


def sensor(run, phase):
    """The axis record's sensor-0 relationship row of one composite phase."""
    require(len(phase["technology"]) == 1, "Expected one technology record: %s" % phase["technology"])
    rows = [r["value"] for r in run.h.tool("technology_profile_report", {"recordId": phase["technology"][0], "section": "relationships", "count": 200})["rows"]]
    rows = [r for r in rows if r["phase"] == "after" and r["relationship"]["metadata"].get("role") == "sensor" and r["relationship"]["metadata"].get("sensorIndex") == 0]
    require(len(rows) == 1, "Expected one sensor-0 relationship: %s" % rows)
    return phase["technology"][0], rows[0]


def node(run, review_id, path):
    """The saved review's unique node for an object path, read from the Host store as the composite oracles do."""
    raw = (run.h.directory / "project-reviews" / (review_id + ".json")).read_bytes()
    require(hashlib.sha256(raw).hexdigest() == review_id, "Saved review content differs from its reviewId")
    rows = [n for n in json.loads(raw)["model"]["nodes"] if n.get("objectPath") == path and n.get("origin") != "virtual"]
    require(len(rows) == 1, "Expected one review node for %s: %d" % (path, len(rows)))
    return {"kind": "model", "reviewId": review_id, "nodeId": rows[0]["nodeId"], "objectPath": path}


def compose(run, label, before, after, correspondences):
    def submit():
        data = json.dumps({"schemaVersion": "composite-engineering-request.v3", "before": before, "after": after, "mappings": [],
                           "correspondences": correspondences, "allowDifferentProjectCopy": False}).encode("utf-8")
        begun = run.h.tool("artifact_upload_begin", {"fileName": "composite.json", "expectedBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        run.h.tool("artifact_upload_chunk", {"uploadId": begun["uploadId"], "offset": 0, "data": base64.b64encode(data).decode("ascii")})
        upload = run.h.tool("artifact_upload_commit", {"uploadId": begun["uploadId"]})["artifactRef"]
        record = run.h.tool("composite_engineering_review", {"uploadRef": upload})["recordRef"]
        claims = [r["value"] for r in run.h.tool("composite_engineering_page", {"recordRef": record, "section": "correspondences", "count": 50})["rows"]]
        require([c["state"] for c in claims] == ["declaredCorrespondence"] * len(correspondences), "Correspondences are not declared: %s" % claims)
        return record
    return run.h.stage("composite review " + label, submit)


def plan(run, label, record, check):
    def observe():
        result = run.h.tool("composite_engineering_rebind_plan", {"recordRef": record})
        require(result["schemaVersion"] == "composite-engineering-rebind-plan.v1" and result["recordRef"] == record and result["toConnectAvailable"] is True
                and result["applied"] is False and result["truncated"] is False and result["notCapturedAfterCount"] == 0, "Plan envelope differs: %s" % result)
        check(result)
        run.h.report["plans"][label] = result
        return {k: result[k] for k in ("brokenConnectionCount", "notCapturedAfterCount")} | {"states": [e["state"] for e in result["entries"]]}
    return run.h.stage("rebind plan " + label, observe)


def refuse_plan(run, label, record, code):
    def check():
        error = run.h.call_error("composite_engineering_rebind_plan", {"recordRef": record})
        require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
        return error
    run.h.stage("refuse composite_engineering_rebind_plan: " + label, check)


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("composite rebind")
    status = h.tool("tia_session_status")["projects"][0]
    run.project, run.base = status["path"], "/projects/%s/devices/" % status["name"]
    run.current = run.snap("baseline")
    old = plug(run, "RbEncA", 2)
    h.report["operatingMode"] = motion(run, old)
    create_axis(run)
    axis = run.to("RbAxis")
    link(run, "to_connect", {"technologyPath": axis, "role": "sensor", "sensorIndex": 0, "hardwarePath": old, **CHANNEL}, old)
    first = capture(run, "before the replacement", review(run, "before the replacement"))["phase"]
    record, declared = sensor(run, first)
    relation = declared["relationship"]
    require(relation["state"] == "declared" and relation["targetPath"] == old and declared["targetResolution"] == "capturedObject",
            "The before phase does not declare sensor 0 on %s: %s" % (old, declared))

    new = plug(run, "RbEncB", 3)
    motion(run, new)
    link(run, "to_disconnect", {"technologyPath": axis, "role": "sensor", "sensorIndex": 0}, old)
    second = capture(run, "after the replacement", review(run, "after the replacement"))["phase"]
    require(sensor(run, second)[1]["relationship"]["state"] == "disconnected", "The after phase does not show sensor 0 disconnected")

    claim = {"id": "RbEncA-replaced-by-RbEncB", "kind": "replacement", "reason": "Declared module replacement in the rebind fixture.",
             "before": {**node(run, first["reviewId"], old), "phase": "before"}, "after": {**node(run, second["reviewId"], new), "phase": "after"}}
    expected = {"kind": "to_connect", "payload": {"technologyPath": axis, "role": "sensor", "hardwarePath": new, "sensorIndex": 0, **CHANNEL}}
    def broken(result):
        require(result["brokenConnectionCount"] == 1 and len(result["entries"]) == 1, "Expected one broken connection: %s" % result["entries"])
        entry = result["entries"][0]
        want = {"technologyRecordRef": record, "technologyPath": axis, "relationshipId": relation["id"], "role": "sensor", "beforeTargetPath": old,
                "afterState": "disconnected", "replacementHardwarePaths": [new], "correspondenceIds": [claim["id"]], "selectorCarriedOver": True,
                "state": "planned", "change": expected}
        require(entry == want, "Planned entry differs: %s" % {k: (entry.get(k), v) for k, v in want.items() if entry.get(k) != v})
    replaced = compose(run, "with the declared replacement", first, second, [claim])
    plan(run, "with the declared replacement", replaced, broken)
    def undeclared(result):
        require(result["brokenConnectionCount"] == 1 and [(e["state"], e["change"], e["replacementHardwarePaths"]) for e in result["entries"]] == [("noDeclaredReplacement", None, [])],
                "Without a correspondence the plan differs: %s" % result["entries"])
    plan(run, "without a correspondence", compose(run, "without a correspondence", first, second, []), undeclared)
    refuse_plan(run, "a malformed handle", replaced[:-1], "CompositeInvalidReference")
    refuse_plan(run, "an altered receipt digest", replaced[:-1] + ("0" if replaced[-1] != "0" else "1"), "CompositeIntegrityFailure")

    planned = h.report["plans"]["with the declared replacement"]["entries"][0]["change"]
    link(run, planned["kind"], planned["payload"], new)
    third = capture(run, "after the rebind", review(run, "after the rebind"))["phase"]
    rebound = sensor(run, third)[1]
    require(rebound["relationship"]["state"] == "declared" and rebound["relationship"]["targetPath"] == new and rebound["targetResolution"] == "capturedObject",
            "The rebind does not resolve sensor 0 to %s: %s" % (new, rebound))
    after_claim = {**claim, "after": {**node(run, third["reviewId"], new), "phase": "after"}}
    def resolved(result):
        require(result["brokenConnectionCount"] == 0 and result["entries"] == [], "The rebound phase still plans: %s" % result["entries"])
    plan(run, "after the rebind", compose(run, "after the rebind", first, third, [after_claim]), resolved)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "rb_", "C02 composite_engineering_rebind_plan in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
