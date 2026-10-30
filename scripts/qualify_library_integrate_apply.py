"""Native qualification of the production library_integrate combined apply, on by default since 29 October 2026.

Starts the frozen Host in owned TIA sessions and owned scratch projects only: with the former
opt-in flag --enable-library-integrate-apply (still accepted, now a no-op) for the installs and
refusals, and without it to prove the default. Reuses the component qualification fixture (FB->UDT global
library, peer and existing FBD FC callers, mapped tags) and its independent oracles.
Each eligible plan is applied once; a failed or uncertain apply is never replayed or
restored automatically. A failed step is retained as failed, never converted to success.
"""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import traceback
import uuid

from block_test_runtime import NativeToolError, utc_now
from compilation_evidence import require_compile_only
from qualification_owned_scratch import OwnedScratch, assert_snapshot, require, snapshot
from qualify_plc_component import (FIXTURES, ComponentRun, first_available_db_number, installed_objects, step_request,
                                   terminal_network, verify_component_call, verify_preserved_installation, verify_standalone_caller)
from run_conformance import safe_xml
from smoke_mcp import Client
from typed_graphical_transport import artifact_bytes

FLAG = "--enable-library-integrate-apply"
REFUSED = "PlcIntegrationNotQualified"
LAD_REFUSAL = "Only a new or existing FBD FC caller is verified"
MODES = ("createStandaloneFc", "existing")
EXTRA_TOOLS = ("library_open", "library_close", "library_list", "library_query", "library_capabilities")
OB_NAME, OB_NUMBER = "TiaMcpComponentOb", 123
HARNESS = ("qualify_plc_component.py", "qualification_owned_scratch.py", "compilation_evidence.py", "smoke_mcp.py",
           "block_test_runtime.py", "run_block_tests.py", "typed_graphical_transport.py")


def host_client(enabled):
    """Client factory for the frozen Host, with or without the former opt-in flag."""
    return lambda host, timeout, arguments: Client(host, timeout, [*arguments, *([FLAG] if enabled else [])])


def german_only_scratch(apply):
    """Make a new scratch exactly de-DE, the qualification Host's state: every other active language adds an empty
    MultilingualTextItem (and shifts IDs) in the FB/UDT exports bound to the captured storage profile by exact content.
    The current reference language cannot be removed in the same change, so the others go in a second one."""
    def languages(result): return result["preview"]["effects"][0]["languageSettings"]["after"]
    state = languages(apply("project_languages_set", {"addActive": ["de-DE"], "referenceLanguage": "de-DE", "editingLanguage": "de-DE"}))
    others = [culture for culture in state["active"] if culture != "de-DE"]
    if others: state = languages(apply("project_languages_set", {"removeActive": others}))
    require(state == {"active": ["de-DE"], "reference": "de-DE", "editing": "de-DE"}, "Scratch languages are not exactly de-DE: " + str(state))


def require_capabilities(capabilities):
    """Every Host, with or without the former flag, reports combined apply."""
    plc = capabilities.get("plcIntegration") or {}
    require(plc.get("applySupported") is True and "combinedApply" in plc, "library_capabilities does not report the default combined apply.")
    return plc


def require_combined_preview(preview, eligible=False, refusal=None):
    """Every PLC recipe preview carries combinedApply, so the reviewed previewHash states whether change_apply installs."""
    block = preview.get("combinedApply")
    require(isinstance(block, dict) and block.get("enabled") is True and block.get("eligible") is eligible
            and preview.get("applySupported") is eligible, "combinedApply eligibility differs: " + str(block))
    if eligible:
        steps = [e for e in preview.get("effects", []) if e.get("action") == "applyPlcComponentSteps"]
        require(block.get("refusal") is None and steps == [{"action": "applyPlcComponentSteps", "nativeMutation": True, "steps": 2}],
                "Eligible preview lacks its exact two-step combined effect.")
    else:
        require(isinstance(block.get("refusal"), str) and block["refusal"].startswith(refusal),
                "Unexpected combined-apply refusal: " + str(block.get("refusal")))
    return block


def refusal(error, code=REFUSED):
    detail = error.result.get("error") if isinstance(error, NativeToolError) and isinstance(error.result, dict) else None
    require(isinstance(detail, dict) and detail.get("code") == code, "Expected " + code + ", observed: " + str(error))
    return detail


def verify_combined_receipt(applied, prepared, project):
    """One verified installation whose single recovery checkpoint is the import step's complete-project archive."""
    journal = applied.get("journal") or {}
    summary = journal.get("combinedApply") or {}
    require(applied.get("state") == "applied" and applied.get("changeId") == prepared["changeId"]
            and applied.get("previewHash") == prepared["previewHash"] and summary.get("state") == "installation_verified"
            and summary.get("failedStep") is None and journal.get("engineeringVerified") is True and "failure" not in journal,
            "Combined apply did not report a verified installation.")
    checkpoints = journal.get("checkpoints")
    require(isinstance(checkpoints, list) and len(checkpoints) == 1 and checkpoints[0].get("kind") == "project"
            and checkpoints[0].get("originalPath") == project, "Combined apply must record exactly one initial project checkpoint.")
    checkpoint = checkpoints[0]
    require(hashlib.sha256(Path(checkpoint["archivePath"]).read_bytes()).hexdigest() == checkpoint["sha256"],
            "Initial checkpoint archive bytes differ from the journal.")
    return checkpoint


def version_key(name, version):
    return name, str(uuid.UUID(version["typeGuid"])), str(uuid.UUID(version["versionGuid"]))


def planned_objects(plan, existing):
    """Block keys the reviewed plan creates; the UDT is pinned by version, not path."""
    return {"block:" + plan["fb"]["path"], "block:" + plan["instanceDb"]["path"]} | (set() if existing else {"block:" + plan["caller"]["blockPath"]})


def verify_instance_db(xml, manifest, number):
    blocks = [node for node in safe_xml(xml) if node.tag.startswith("SW.Blocks.")]
    require(len(blocks) == 1 and blocks[0].tag == "SW.Blocks.InstanceDB", "Installed instance DB export must contain one instance DB.")
    attributes = blocks[0].find("AttributeList")
    require(attributes is not None and all(attributes.findtext(key) == value for key, value in {
        "Name": manifest["instance"], "InstanceOfName": manifest["root"], "InstanceOfType": "FB", "Number": str(number)}.items()),
        "Installed instance DB identity or first available number differs.")


def fbd_ob(main_xml, peer_xml, source_tag, target_tag):
    """FBD program-cycle OB fixture from two native exports: Main's OB envelope with the peer FBD FC's coil network,
    rebound from the peer's locals to two global tags."""
    networks = re.findall(r"<NetworkSource>.*?</NetworkSource>", peer_xml, re.S)
    require(len(networks) == 1, "Peer caller must export exactly one network.")
    body = networks[0]
    for local, tag in (("PreserveInput", source_tag), ("PreserveOutput", target_tag)):
        body, count = re.subn(r'Scope="LocalVariable"([^>]*>\s*<Symbol>\s*<Component Name=")' + local + '"',
                              lambda match: 'Scope="GlobalVariable"' + match.group(1) + tag + '"', body)
        require(count == 1, "Peer network must access " + local + " exactly once.")
    xml, count = re.subn(r"<NetworkSource>.*?</NetworkSource>", lambda _: body, main_xml, flags=re.S)
    require(count == 1, "Main must export exactly one network.")
    for old, new in (("<Name>Main</Name>", "<Name>" + OB_NAME + "</Name>"), ("<Number>1</Number>", "<Number>" + str(OB_NUMBER) + "</Number>")):
        require(xml.count(old) == 1, "Main export lacks exactly one " + old)
        xml = xml.replace(old, new)
    require(xml.count("<ProgrammingLanguage>SCL</ProgrammingLanguage>") == 2, "Main must be one SCL block with one SCL network.")
    return xml.replace("<ProgrammingLanguage>SCL</ProgrammingLanguage>", "<ProgrammingLanguage>FBD</ProgrammingLanguage>")


class IntegrateApplyRun(ComponentRun):
    def __init__(self, args):
        require(args.run_native, "Explicit --run-native is required.")
        self.args, self.manifest = args, json.loads((FIXTURES / "manifest.json").read_text(encoding="utf-8"))
        # Report, fixture documents and the Host's own artifacts share one fresh short run directory.
        self.output = self.native = args.artifacts.resolve() / ("i" + uuid.uuid4().hex[:6])
        require(len(str(self.native / "scratch" / ("TiaMcpScratch_" + "0" * 24) / ("TiaMcpScratch_" + "0" * 24))) <= 143,
                "Choose a short native --artifacts path.")
        self.output.mkdir(parents=True, exist_ok=False)
        self.session = None
        self.report = {"schemaVersion": 1, "qualification": "library_integrate_combined_apply", "status": "running",
            "startedAtUtc": utc_now(), "hostFlag": FLAG, "fixture": copy.deepcopy(self.manifest), "workspace": str(self.output),
            "steps": [], "cases": {}, "events": [], "fixtureChanges": [],
            "combinedApplyVerified": False, "refusalsVerified": False, "defaultHostVerified": False,
            "runtimeVerified": False, "automaticReplayAllowed": False,
            "harnessFiles": [{"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                             for path in (Path(__file__), *(Path(__file__).with_name(name) for name in HARNESS))]}
        self.persist()

    def call(self, tool, arguments=None):
        result = super().call(tool, arguments)
        # The fixtures and captured storage references are de-DE only; only the qualification Host forces that itself.
        if tool == "tia_create_scratch":
            german_only_scratch(self.apply)
        return result

    def step(self, name, operation):
        row = {"name": name, "status": "running", "startedAtUtc": utc_now()}
        self.report["steps"].append(row); self.persist()
        print("START " + name, flush=True)
        try:
            result = operation()
            row["status"] = "passed"
            return result
        except Exception as error:
            row.update(status="failed", failure=str(error))
            raise
        finally:
            row["completedAtUtc"] = utc_now(); self.persist()

    def case(self, name, enabled):
        case = self.report["cases"][name] = {"libraryIntegrateApplyFlag": enabled}
        self.persist()
        return case

    def project_versions(self):
        library = self.call("library_open", {"request": {"kind": "project"}})["libraryId"]
        names = {self.manifest["root"], self.manifest["dependency"]}
        return sorted(version_key(v["typeName"], v) for v in self.query(library) if v.get("kind") == "version" and v["typeName"] in names)

    def prepare(self, scratch, library, released, plc, baseline, caller, caller_recipe):
        payload = self.recipe(scratch, library, released, plc, baseline, caller_recipe)
        prepared = self.call("change_prepare", {"kind": "library_integrate", "payload": payload})
        return prepared, self.review_plan(prepared, payload, caller, caller_recipe["mode"] == "existing")

    def refused_apply(self, prepared):
        """One change_apply that must be refused before any claim; the plan stays prepared."""
        try:
            result = self.call("change_apply", step_request(prepared))
        except NativeToolError as error:
            detail = refusal(error)
        else:
            raise RuntimeError("change_apply installed a plan that must be refused: " + str(result.get("state")))
        status = self.call("change_status", {"changeId": prepared["changeId"]})
        require(status.get("state") == "prepared" and not (status.get("journal") or {}).get("idempotencyKeyHash"),
                "A refused apply changed the plan's durable state.")
        return detail

    def install(self, mode, archive, released):
        self.manifest["callerMode"] = mode
        existing, out = mode == "existing", self.output / mode
        case = self.case(mode, True)
        scratch, opened, plc = self.stage_consumer(archive, released)
        case["capabilities"] = require_capabilities(self.call("library_capabilities"))
        baseline = snapshot(self.call, out / "before")
        caller, caller_recipe, network = self.select_caller(baseline, plc)
        prepared, plan = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
        case["plan"] = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}
        case["combinedApply"] = require_combined_preview(prepared["preview"], eligible=True)
        request = step_request(prepared)
        self.retain("combined_apply_dispatching", request)
        try:
            applied = self.call("change_apply", request)
        except Exception:
            # Inspection only: a failed or uncertain apply is never replayed or restored automatically.
            try: case["failureStatus"] = self.call("change_status", {"changeId": prepared["changeId"]})
            except Exception as inspection: case["failureStatusError"] = str(inspection)
            self.persist()
            raise
        self.retain("combined_apply_result", applied)
        checkpoint = case["initialCheckpoint"] = verify_combined_receipt(applied, prepared, baseline["inventory"]["project"]["path"])
        record = json.loads(Path(applied["journal"]["combinedApply"]["coordinatorJournal"]).read_text(encoding="utf-8-sig"))["record"]
        require(record.get("state") == "installation_verified", "Coordinator journal is not at verified installation.")
        after = snapshot(self.call, out / "after")
        added = installed_objects(after, self.manifest, plc, existing)
        require(planned_objects(plan, existing) == {key for key in added if key.startswith("block:")}, "Installed blocks differ from the reviewed plan.")
        verify_preserved_installation(baseline, after, caller, added)
        caller_xml = Path(after["objects"]["block:" + caller]["file"]).read_text(encoding="utf-8-sig")
        verify_component_call(caller_xml, self.manifest)
        if existing:
            proof = case["callerInsertion"] = record["installationEvidence"]["callerInsertion"]
            require(proof["verified"] is True and proof["insertAfterNetworkId"] == network, "Native caller insertion proof differs.")
        else:
            verify_standalone_caller(caller_xml, self.manifest)
        verify_instance_db(Path(after["objects"]["block:" + plan["instanceDb"]["path"]]["file"]).read_text(encoding="utf-8-sig"),
                           self.manifest, first_available_db_number(baseline["inventory"]["blocks"]))
        require(self.project_versions() == sorted(version_key(name, pin) for name, pin in released.items()),
                "Project library does not hold exactly the released FB/UDT versions.")
        case["installationVerified"] = True; self.persist()
        self.call("library_close", {"libraryId": opened["libraryId"]})
        self.session.reopen(self.session.current_project)
        assert_snapshot(after, snapshot(self.call, out / "reopened"))
        case["freshReopenVerified"] = True; self.persist()
        compile_prepared = self.call("change_prepare", {"kind": "plc_compile", "plcPath": plc, "payload": {}})
        require_compile_only(compile_prepared, self.call("change_apply", step_request(compile_prepared)), plc)
        case["compileVerified"] = True; self.persist()
        self.restore(case, prepared, checkpoint, baseline, out)

    def restore(self, case, prepared, checkpoint, baseline, out):
        self.session.close()
        self.session.start()
        status = self.session.audit("tia_session_status")
        require(status.get("ownsPortalProcess") is True and status.get("processId") == self.session.session["processId"]
                and status.get("projects") == [], "Restore requires one fresh empty owned Portal.")
        request = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}
        self.retain("change_restore_dispatching", request)
        # Library restore needs an empty Portal, so it bypasses the selected-project guard; dispatched once.
        restored = self.session.audit("change_restore", request)
        self.retain("change_restore_result", restored)
        resources = restored.get("resources", [])
        require(restored.get("state") == "restored_to_new_locations" and len(resources) == 1 and resources[0].get("kind") == "project"
                and resources[0].get("retrieved") is True and resources[0].get("archiveSha256") == checkpoint["sha256"],
                "Initial checkpoint restore receipt differs.")
        recovered = resources[0]["project"]["path"]
        require(Path(recovered).resolve() != Path(checkpoint["originalPath"]).resolve(), "Restore overwrote the original project.")
        self.session.remember(recovered)
        self.session.guard()
        assert_snapshot(baseline, snapshot(self.call, out / "restored"))
        require(self.project_versions() == [], "Restored project library still holds component versions.")
        self.session.reopen(recovered)
        assert_snapshot(baseline, snapshot(self.call, out / "restored-reopened"))
        case["restoreVerified"] = True; self.persist()
        self.session.close()

    def refusals(self, archive, released):
        """Flag on: an FBD OB caller and a new LAD FC caller are refused at apply with no project change."""
        self.manifest["callerMode"] = "createStandaloneFc"
        case, out = self.case("refusals", True), self.output / "refusals"
        scratch, opened, plc = self.stage_consumer(archive, released)
        exports = [artifact_bytes(self.call, self.call("block_read", {"blockPath": plc + "/blocks/" + name})["artifact"]).decode("utf-8-sig")
                   for name in ("Main", self.manifest["unrelatedCaller"])]
        first, second = self.manifest["signals"]
        self.apply("block_import_xml", {"xml": fbd_ob(*exports, first["name"], second["name"]), "expectedBlockNames": [OB_NAME]})
        baseline = snapshot(self.call, out / "before")
        ob = plc + "/blocks/" + OB_NAME
        network, _ = terminal_network(Path(baseline["objects"]["block:" + ob]["file"]).read_text(encoding="utf-8-sig"), "FBD")
        for name, caller, caller_recipe, reason in (
                ("obCaller", ob, {"mode": "existing", "blockPath": ob, "insertAfterNetworkId": network}, "OB insertion is refused"),
                ("ladCaller", plc + "/blocks/" + self.manifest["caller"], {"mode": "createStandaloneFc", "name": self.manifest["caller"],
                    "language": "LAD", "numberPolicy": "firstAvailable"}, LAD_REFUSAL)):
            prepared, _ = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
            case[name] = {"changeId": prepared["changeId"], "combinedApply": require_combined_preview(prepared["preview"], refusal=reason)}
            case[name]["refusal"] = self.refused_apply(prepared); self.persist()
        assert_snapshot(baseline, snapshot(self.call, out / "after"))
        case["projectUnchanged"] = True; self.persist()
        self.session.close()

    def default_host(self, archive, released):
        """Host without the former flag: combined apply is the default, so the standalone FBD plan previews eligible (the
        install steps already applied it; it is not applied again) and an unverified LAD FC plan is refused with no change."""
        self.manifest["callerMode"] = "createStandaloneFc"
        self.session.client_factory = host_client(False)
        case, out = self.case("defaultHost", False), self.output / "defaultHost"
        scratch, opened, plc = self.stage_consumer(archive, released)
        case["capabilities"] = require_capabilities(self.call("library_capabilities"))
        baseline = snapshot(self.call, out / "before")
        caller, caller_recipe, _ = self.select_caller(baseline, plc)
        prepared, _ = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
        case["combinedApply"] = require_combined_preview(prepared["preview"], eligible=True)
        prepared, _ = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, dict(caller_recipe, language="LAD"))
        case["ladCaller"] = {"changeId": prepared["changeId"], "combinedApply": require_combined_preview(prepared["preview"], refusal=LAD_REFUSAL),
                             "refusal": self.refused_apply(prepared)}
        assert_snapshot(baseline, snapshot(self.call, out / "after"))
        case["projectUnchanged"] = True; self.persist()
        self.session.close()

    def run(self):
        primary = None
        try:
            self.session = OwnedScratch(self.args.host, self.args.worker, self.output, self.report, self.persist,
                                        timeout=self.args.timeout, client_factory=host_client(True), extra_tools=EXTRA_TOOLS)
            archive, released = self.step("author", self.author)
            for mode in MODES:
                self.step("install:" + mode, lambda: self.install(mode, archive, released))
            self.report["combinedApplyVerified"] = True
            self.step("refusals", lambda: self.refusals(archive, released))
            self.report["refusalsVerified"] = True
            self.step("defaultHost", lambda: self.default_host(archive, released))
            self.report["defaultHostVerified"] = True
            self.report["status"] = "passed"
        except Exception as error:
            primary = error
            self.report.update(status="failed", failure=str(error), traceback=traceback.format_exc())
            raise
        finally:
            cleanup_error = None
            try:
                if self.session is not None: self.session.close()
            except Exception as error:
                cleanup_error = error
                self.report.update(status="failed", cleanupFailure=str(error))
            self.report["completedAtUtc"] = utc_now(); self.persist()
            print("Report: " + str(self.output / "result.json"), flush=True)
            if primary is None and cleanup_error is not None: raise cleanup_error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    require(5 <= args.timeout <= 900, "Timeout must be 5..900 seconds.")
    IntegrateApplyRun(args).run()


if __name__ == "__main__": main()
