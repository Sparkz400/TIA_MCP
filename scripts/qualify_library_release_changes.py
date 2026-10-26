"""Qualify implementation-changing and breaking-interface FB releases through MCP in owned scratch projects only."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import uuid
import xml.etree.ElementTree as ET  # Native exports from this run's owned scratch only.

from block_test_runtime import (save_generated_scratch_project, verify_credential_provisioning, verify_download,
                                verify_memory_card, verify_owned_instance)
from engineering_preservation import native_payload_hash
from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FB = "TiaMcpLibraryGate"
UDT = "TiaMcpLibraryState"


def local(tag):
    return tag.rsplit("}", 1)[-1]


def fb_shape(raw):
    """Independent reading of a native FB export: output member names and SCL body tokens."""
    root = ET.fromstring(raw)
    outputs = [m.get("Name") for s in root.iter() if local(s.tag) == "Section" and s.get("Name") == "Output"
               for m in s if local(m.tag) == "Member"]
    tokens = [e.get("Text") for e in root.iter() if local(e.tag) == "Token"]
    return {"outputs": outputs, "negated": "NOT" in tokens}


# Expected shapes are authored from the fixture documents, not read back from the implementation.
EXPECTED = {"1.0.0": {"outputs": ["Active"], "negated": False},
            "1.1.0": {"outputs": ["Active"], "negated": True},
            "2.0.0": {"outputs": ["Ready"], "negated": True}}


def instance_shape(raw):
    """Independent reading of a native instance DB export: its FB and the members of each non-empty section."""
    root = ET.fromstring(raw)
    interface = next(e for e in root.iter() if local(e.tag) == "Sections")  # The first is the interface; later ones nest in members.
    sections = {s.get("Name"): [m.get("Name") for m in s if local(m.tag) == "Member"] for s in interface}
    return {"instanceOf": next((e.text for e in root.iter() if local(e.tag) == "InstanceOfName"), None),
            "sections": {name: members for name, members in sections.items() if members}}


# Authored from the fixture SCL, never read back from the builder: instance DB members of each release and the
# truth table Enable -> FB output. Main's call keeps Enable := TRUE, so its instance and UDT field follow row TRUE.
INSTANCE = {number: {"instanceOf": FB, "sections": {"Input": ["Enable"], "Output": [output]}}
            for number, output in (("1.0.0", "Active"), ("1.1.0", "Active"), ("2.0.0", "Ready"))}
BEHAVIOR = {"1.0.0": {True: True, False: False}, "1.1.0": {True: False, False: True}, "2.0.0": {True: False, False: True}}
INSTANCE_DBS = ("TiaMcpLibraryGate_DB", "TiaMcpReleaseProbe_Gate")
STALE_CALLERS = ("Main", "TiaMcpReleaseProbe_Cycle")


def compile_errors(compile_result):
    return [m for t in compile_result.get("targets", []) for m in t.get("messages", []) if m.get("state") == "Error"]


def require_breaking_diagnostics(errors):
    """The breaking release must fail at every stale caller, on the removed output, and nowhere unrelated."""
    text = json.dumps(errors)
    require(errors and all(name in text for name in STALE_CALLERS), "Compiler errors do not identify every stale caller %s" % (STALE_CALLERS,))
    require(any("Active" in (m.get("description") or "") for m in errors), "No compiler error names the removed output Active")
    require(not any(n in text for n in ("TiaMcp_EnableGate", "TiaMcp_IsEnabled")), "Compiler errors reach unrelated fixture blocks")


def behavior_steps(number):
    """sim_test steps of one release: write the probe input, assert probe output, instance outputs and Main's UDT field."""
    output = INSTANCE[number]["sections"]["Output"][0]
    steps = []
    for enable in (True, False, True):
        expected, constant = BEHAVIOR[number][enable], BEHAVIOR[number][True]
        steps.append({"delayMs": 500, "writes": [{"symbol": "TiaMcpReleaseProbe.Enable", "type": "bool", "value": enable}],
                      "assertions": [{"symbol": symbol, "type": "bool", "operator": "eq", "expected": value} for symbol, value in (
                          ("TiaMcpReleaseProbe.Output", expected), ("TiaMcpReleaseProbe_Gate." + output, expected),
                          ("TiaMcpLibraryGate_DB." + output, constant), ("TiaMcpLibraryState_DB.State.Active", constant))]})
    return steps


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    changes = ROOT / "fixtures/libraries/release-changes"

    def exported_version_shape(library, version):
        result = h.export_documents(library, version)
        shapes = [fb_shape(Path(a["path"]).read_bytes()) for a in result["files"] if a["path"].lower().endswith(".xml")]
        require(len(shapes) == 1, "Expected exactly one native XML FB document")
        return {"files": result["files"], "shape": shapes[0]}

    def block_shape(name):
        exported = h.tool("block_read", {"blockName": name})
        raw = Path(exported["artifact"]["path"]).read_bytes()
        require(hashlib.sha256(raw).hexdigest() == exported["artifact"]["sha256"], "Native block export hash differs")
        return {"artifact": exported["artifact"], "shape": fb_shape(raw)}

    def instance_dbs(number, where):
        rows = {}
        for name in INSTANCE_DBS:
            exported = h.tool("block_read", {"blockName": name})
            raw = Path(exported["artifact"]["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == exported["artifact"]["sha256"], "Native instance DB export hash differs")
            rows[name] = instance_shape(raw)
            require(rows[name] == INSTANCE[number], "%s instance DB %s does not match independent %s members: %s" % (where, name, number, rows[name]))
        return rows

    def runtime(number):
        """Download the saved consumer to a fresh owned PLCSIM instance and drive the independent truth table."""
        instance = None
        row = h.report.setdefault("runtime", {})[number] = {}
        try:
            created = h.tool("sim_create", {"cpuType": "CPU1511", "creationId": uuid.uuid4().hex})
            instance = verify_owned_instance(created["instance"])
            require(instance.get("operatingState") == "Off", "A fresh owned OFF instance is required")
            cpus = h.scratch["scratchSecurity"]["cpus"]
            require(len(cpus) == 1, "Exactly one generated scratch credential is required")
            ticket = {"downloadTicket": created["downloadTicket"], "credentialMetadataPath": cpus[0]["credentialMetadataPath"]}
            download = h.tool("simulation_download", ticket)
            verify_download(download, instance)
            row["memoryCard"] = verify_memory_card(download)
            row["generatedProjectSave"] = save_generated_scratch_project(h.tool, download)
            provisioned = h.tool("simulation_provision_scratch_credential", {**ticket, "trustOwnedEmptyCpuCertificate": True})
            verify_credential_provisioning(provisioned, download, trust_owned_empty_cpu_certificate=True)
            verify_memory_card(download)
            h.tool("sim_state", {"instanceId": instance["id"], "action": "powerOn", "timeoutMs": 60000})
            running = h.tool("sim_state", {"instanceId": instance["id"], "action": "run", "timeoutMs": 30000})
            require(running.get("instance", {}).get("operatingState") == "Run", "Simulator did not enter RUN")
            steps = behavior_steps(number)
            # sim_tags loads the instance tag list; each probe symbol must resolve to exactly one native tag.
            tags = [t["symbol"] for t in h.tool("sim_tags", {"instanceId": instance["id"], "maxItems": 2000})["tags"]]
            stale = "Ready" if INSTANCE[number]["sections"]["Output"] == ["Active"] else "Active"
            require(not any(t.replace('"', "") == db + "." + stale for t in tags for db in INSTANCE_DBS),
                    "An instance DB exposes the other release's output " + stale)
            for item in (x for step in steps for x in step["writes"] + step["assertions"]):
                found = [t for t in tags if t.replace('"', "") == item["symbol"]]
                require(len(found) == 1, "Probe symbol is missing or ambiguous in the simulator: " + item["symbol"])
                item["symbol"] = found[0]
            signals = [{"symbol": s, "type": "bool"} for s in sorted({x["symbol"] for step in steps for x in step["writes"] + step["assertions"]})]
            behavior = h.tool("sim_test", {"instanceId": instance["id"], "signals": signals, "intervalMs": 100, "steps": steps})
            row["behavior"] = behavior
            require(behavior.get("passed") is True, "Release %s runtime truth table failed" % number)
            return {"steps": steps, "passed": True}
        finally:
            if instance is not None:
                row["release"] = h.tool("sim_release", {"instanceId": instance["id"], "deleteOwned": True})
                require(row["release"].get("unregistered") is True, "Owned simulator instance was not released")

    def require_shape(observed, number, where):
        require(observed["shape"] == EXPECTED[number], "%s does not match independent %s expectation: %s" % (where, number, observed["shape"]))
        return observed

    def edit_and_release(library, plc, previous, source_name, number):
        released = h.release(library, h.edit_source(library, plc, previous, FB, (changes / source_name).read_text()), number)
        h.stage("export released " + number, lambda: require_shape(exported_version_shape(library, released), number, "Released export"))
        return released

    def publish_generation(project_library, global_library, version):
        h.apply("library_publish", h.selected(project_library, [version], destinationLibraryId=global_library))
        h.apply("library_set_default", h.selected(global_library, [version]))
        return h.archive(global_library)

    def unrelated_snapshot(plc):
        inventory = h.tool("project_inventory", {"maxItems": 1000})
        names = {"TiaMcp_IsEnabled", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcpLibraryState_DB", "TiaMcpReleaseProbe"}
        blocks = sorted((x for x in inventory["blocks"] if x["name"] in names and x["plcPath"] == plc), key=lambda x: x["path"])
        require(len(blocks) == len(names), "Unrelated fixture blocks are missing or ambiguous")
        exported = [(b["path"], h.tool("block_read", {"blockPath": b["path"]})) for b in blocks]
        exported.append(("type:" + UDT, h.tool("type_read", {"plcPath": plc, "typeName": UDT})))
        rows = []
        for path, result in exported:
            raw = Path(result["artifact"]["path"]).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == result["artifact"]["sha256"], "Unrelated native export hash differs")
            rows.append({"path": path, "nativeDocumentWithoutDocumentInfoSha256": native_payload_hash(raw.decode("utf-8-sig"))})
        return rows

    def expect_breaking_compile_failure(payload):
        prepared = h.prepare("library_update_project", payload)
        dispatch = {"kind": "library_update_project", "changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                    "state": "dispatching", "expectedOutcome": "CompileFailed"}
        h.report["mutations"].append(dispatch)
        h.persist()
        error = h.call_error("change_apply", h.apply_args(prepared))
        details = error.get("details") or {}
        dispatch.update(state=details.get("state"), errorCode=error.get("code"))
        h.report["breakingUpdate"] = {"error": error}
        require(error.get("code") == "CompileFailed" and details.get("state") == "recovery_required", "Breaking update did not fail compilation for recovery")
        events = details["journal"].get("events", [])
        require(any(e.get("stage") == "native_mutation" and e.get("state") == "completed" for e in events), "Native update did not complete before compilation")
        require(not any(e.get("stage", "").startswith("save") for e in events), "Failed breaking update was saved")
        errors = compile_errors(details["journal"]["compile"])
        h.report["breakingUpdate"]["errors"] = errors
        require_breaking_diagnostics(errors)
        return prepared

    # Authoring: three FB generations of one native type identity.
    plc = h.owned_scratch("authoring")
    project_library = h.project_library()
    initial = {name: h.release(project_library, h.create_type(project_library, documents, name, composition, plc), "1.0.0")
               for name, composition in ((UDT, "types"), (FB, "blocks"))}
    h.stage("export released 1.0.0", lambda: require_shape(exported_version_shape(project_library, initial[FB]), "1.0.0", "Released export"))
    implementation = edit_and_release(project_library, plc, initial[FB], "TiaMcpLibraryGate.implementation.scl", "1.1.0")
    h.apply("library_set_default", h.selected(project_library, [implementation]))
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "ReleaseQualification"})["journal"]["result"]["library"]["libraryId"]
    h.apply("library_publish", h.selected(project_library, [initial[UDT]], destinationLibraryId=global_library))
    generation = {"1.1.0": publish_generation(project_library, global_library, implementation)}
    breaking = edit_and_release(project_library, plc, implementation, "TiaMcpLibraryGate.breaking.scl", "2.0.0")
    h.apply("library_set_default", h.selected(project_library, [breaking]))
    generation["2.0.0"] = publish_generation(project_library, global_library, breaking)
    fb_versions = {"1.0.0": initial[FB], "1.1.0": implementation, "2.0.0": breaking}
    require({v["typeGuid"] for v in fb_versions.values()} == {initial[FB]["typeGuid"]}, "A release changed the FB type identity")
    require(len({v["versionGuid"] for v in fb_versions.values()}) == 3, "Release version GUIDs are not distinct")
    h.report["nativeVersions"] = {"udt": initial[UDT], "fb": fb_versions}
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close owned authoring session", lambda: h.tool("tia_detach"))

    def consumer(label, archive, fb_version, references, probe):
        """Fresh owned consumer with exact UDT/FB versions, the reachable Main caller and the runtime probe."""
        plc = h.owned_scratch(label)
        h.apply("project_simulation_enable", {"enabled": True})
        source = h.open_archive(archive)
        for version in (initial[UDT], fb_version):
            h.apply("library_instantiate", h.selected(source, [version], target={"plcPath": plc}))
        h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": references.read_text(),
                "expectedBlockNames": ["TiaMcpLibraryGate_DB", "TiaMcpLibraryState_DB", "Main"]})
        h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": probe.read_text(),
                "expectedBlockNames": ["TiaMcpReleaseProbe", "TiaMcpReleaseProbe_Gate", "TiaMcpReleaseProbe_Cycle"]})
        return plc, source

    # Consumer: the 1.1.0 generation (which also carries 1.0.0) is consumed at 1.0.0, then updated.
    consumer_plc, source = consumer("consumer", generation["1.1.0"], initial[FB], documents / "ConsumerReferences.scl", changes / "ReleaseProbe.scl")
    consumer_library = h.project_library()
    h.stage("consumer FB at 1.0.0", lambda: h.verify_use(consumer_library, initial[FB], 1))
    h.stage("consumer FB body 1.0.0", lambda: require_shape(block_shape(FB), "1.0.0", "Consumer FB"))
    h.stage("consumer instance DBs 1.0.0", lambda: instance_dbs("1.0.0", "Consumer"))
    h.stage("runtime behavior 1.0.0", lambda: runtime("1.0.0"))
    before = h.stage("capture unrelated content before implementation update", lambda: unrelated_snapshot(consumer_plc))
    applied = h.apply("library_update_project", h.selected(source, [implementation], updateScope="entireSelectedPlc", target={"plcPath": consumer_plc}))
    require(applied["journal"]["compile"]["errorCount"] == 0, "Implementation update compile failed")
    h.tool("library_close", {"libraryId": source})
    h.stage("consumer FB at 1.1.0", lambda: h.verify_use(consumer_library, implementation, 1))
    h.stage("consumer FB has no 1.0.0 uses", lambda: h.verify_use(consumer_library, initial[FB], 0))
    h.stage("consumer UDT unaffected", lambda: h.verify_use(consumer_library, initial[UDT], 1))
    h.stage("consumer FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Updated consumer FB"))
    h.stage("consumer instance DBs keep their interface at 1.1.0", lambda: instance_dbs("1.1.0", "Updated consumer"))
    after = h.stage("capture unrelated content after implementation update", lambda: unrelated_snapshot(consumer_plc))
    require(before == after, "Implementation update changed unrelated native content")
    h.report["implementationUpdatePreservedUnrelated"] = True
    h.stage("runtime behavior 1.1.0", lambda: runtime("1.1.0"))

    source = h.open_archive(generation["2.0.0"])
    failed = h.stage("breaking update fails compile at stale caller", lambda: expect_breaking_compile_failure(
        h.selected(source, [breaking], updateScope="entireSelectedPlc", target={"plcPath": consumer_plc})))
    consumer_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("discard failed consumer session", lambda: h.tool("tia_detach"))

    # Recovery: the checkpoint and the unsaved original both keep the exact 1.1.0 link.
    h.owned_portal("recovery")
    h.reconcile_admission("reconcile the failed update's admission in a fresh session",
                          ((h.report["breakingUpdate"]["error"].get("details") or {}).get("mutationAdmission") or {}).get("admissionId"))
    restored = h.stage("restore breaking-update checkpoint", lambda: h.tool("change_restore", {"changeId": failed["changeId"], "previewHash": failed["previewHash"]}))
    require(restored["state"] == "restored_to_new_locations" and restored.get("originalsPreserved") is True, "Checkpoint was not restored")
    recovered = h.project_library()
    h.stage("recovered FB at 1.1.0", lambda: h.verify_use(recovered, implementation, 1))
    h.stage("recovered FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Recovered FB"))
    h.stage("close recovery session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen original consumer", lambda: h.tool("project_open", {"path": consumer_path}))
    reopened = h.project_library()
    h.stage("reopened FB at 1.1.0", lambda: h.verify_use(reopened, implementation, 1))
    h.stage("reopened UDT at 1.0.0", lambda: h.verify_use(reopened, initial[UDT], 1))
    h.stage("reopened FB body 1.1.0", lambda: require_shape(block_shape(FB), "1.1.0", "Reopened FB"))
    h.stage("reopened instance DBs 1.1.0", lambda: instance_dbs("1.1.0", "Reopened"))
    require(h.stage("capture unrelated content after reopen", lambda: unrelated_snapshot(consumer_plc)) == before,
            "Reopened original changed unrelated native content")
    h.stage("close reopened consumer", lambda: h.tool("tia_detach"))

    # Adaptation: the same two callers, adapted to the renamed output, compile against exactly 2.0.0 and run.
    _, source = consumer("adapted", generation["2.0.0"], breaking, changes / "ConsumerReferences.adapted.scl", changes / "ReleaseProbe.adapted.scl")
    h.tool("library_close", {"libraryId": source})
    adapted = h.project_library()
    h.stage("adapted FB at 2.0.0", lambda: h.verify_use(adapted, breaking, 1))
    h.stage("adapted UDT at 1.0.0", lambda: h.verify_use(adapted, initial[UDT], 1))
    h.stage("adapted FB body 2.0.0", lambda: require_shape(block_shape(FB), "2.0.0", "Adapted FB"))
    h.stage("adapted instance DBs 2.0.0", lambda: instance_dbs("2.0.0", "Adapted"))
    h.stage("runtime behavior 2.0.0", lambda: runtime("2.0.0"))
    h.report["runtimeVerified"] = True


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--sim-worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    inputs = sorted(p for folder in (ROOT / "fixtures/libraries/type-lifecycle", ROOT / "fixtures/libraries/release-changes")
                    for p in folder.iterdir() if p.suffix in (".scl", ".udt"))
    Harness(args, "rc_", "V21 standard FB implementation-changing and breaking-interface releases with PLCSIM behavior", [
        "Behavior runs on fresh owned PLCSIM Advanced V8 CPU1511 instances downloaded from the saved scratch; no physical CPU.",
        "The FB is stateless: the truth table is asserted, instance data retention across an update/download is not.",
        "New versions are created from documents: TIA refuses replacing an edited version's test instance by source generation.",
        "The breaking update is qualified as an expected compile failure followed by checkpoint recovery; caller migration in the same transaction is not offered; adapted callers are qualified in a fresh 2.0.0 consumer.",
        "One PLC, standard groups, no software units, protected/F types or crash injection."], inputs).run(body)
