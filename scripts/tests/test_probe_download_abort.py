"""Offline checks of probe_download_abort.py: its pure helpers, and body() against a fake Host and PLCSIM; nothing here
starts TIA or PLCSIM."""
import argparse
import contextlib
import copy
import io
import pathlib
import re
import tempfile
import unittest
from unittest import mock

import probe_download_abort as p


def node(left, state, *children):
    return {"left": left, "right": left, "state": state, "detail": None, "children": list(children)}


DIFFERING = node("PLC_1", "FolderContentsDifferent", node("AbortDb [DB1]", "ObjectsDifferent"), node("Main [OB1]", "ObjectsIdentical"))
IDENTICAL = node("PLC_1", "FolderContentsIdentical", node("AbortDb [DB1]", "ObjectsIdentical"), node("Main [OB1]", "ObjectsIdentical"))
WATCH_QUIET = {"kinds": ["led"], "events": [{"kind": "led"}]}


def configuration(phase, kind, selection=None, exception=None):
    entry = {"phase": phase, "type": kind, "message": kind + " message"}
    if selection:
        entry["selection"] = selection
    if exception:
        entry["exceptionType"] = exception
    return entry


REFUSED_LATER = [configuration("pre", "ConsistentBlocksDownload", "ConsistentDownload"),
                 configuration("pre", "DataBlockReinitialization", exception="TiaMcp.Openness.V21.BridgeException")]


def watch(*kinds):
    return {"kinds": list(kinds), "events": [{"kind": k} for k in kinds]}


def aborted(configurations=REFUSED_LATER, code="SimulationDownloadReinitializationRequired", after=None, compare_after=DIFFERING, during=None, baseline=None):
    """An attempt row as attempt() records it, by default an abort that loaded nothing."""
    return {"offlineChange": {"members": ["Added"], "newMember": "Added", "programVersion": 2},
            "compareBefore": {"differences": p.differences(DIFFERING)},
            "compareAfter": {"differences": p.differences(compare_after)},
            "before": {"values": {"Kept": 7, "Cycles": 10, "ProgramVersion": 1}, "operatingState": "Run", "members": ["Cycles", "Kept", "ProgramVersion"]},
            "after": after or {"values": {"Kept": 7, "Cycles": 90, "ProgramVersion": 1}, "cyclesAdvanced": True, "operatingState": "Run",
                               "members": ["Cycles", "Kept", "ProgramVersion"]},
            "watchBaseline": baseline or WATCH_QUIET,
            "watch": during or WATCH_QUIET,
            "download": {"isError": True, "error": {"code": code, "message": "refused"}, "configurations": configurations}}


class BinaryAndArtifactTests(unittest.TestCase):
    def test_bundle_layout_and_conflicting_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = pathlib.Path(directory)
            for relative in p.BUNDLE.values():
                (bundle / relative).parent.mkdir(parents=True, exist_ok=True)
                (bundle / relative).write_bytes(b"x")
            paths = p.resolve_binaries(bundle)
            self.assertEqual(paths["sim_worker"], (bundle / "workers/simulator/TiaMcp.Simulator.V8.exe").resolve())
            with self.assertRaisesRegex(AssertionError, "not both"):
                p.resolve_binaries(bundle, host=bundle / p.BUNDLE["host"])
            (bundle / p.BUNDLE["worker"]).unlink()
            with self.assertRaisesRegex(AssertionError, "worker not found"):
                p.resolve_binaries(bundle)

    def test_explicit_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            files = {name: pathlib.Path(directory) / (name + ".exe") for name in ("host", "worker", "sim_worker")}
            for path in files.values():
                path.write_bytes(b"x")
            self.assertEqual(p.resolve_binaries(None, files["host"], files["worker"], files["sim_worker"])["worker"], files["worker"].resolve())

    def test_artifacts_must_stay_inside_artifacts(self):
        with tempfile.TemporaryDirectory() as directory:
            roots = [pathlib.Path(directory) / "repo" / "artifacts", pathlib.Path(directory) / "main" / "artifacts"]
            self.assertTrue(p.inside_artifacts(roots[0] / "da", roots))
            self.assertTrue(p.inside_artifacts(roots[1] / "da", roots))
            self.assertFalse(p.inside_artifacts(pathlib.Path(directory) / "repo" / "da", roots))
            self.assertFalse(p.inside_artifacts(roots[0] / ".." / ".." / "elsewhere", roots))

    def test_this_checkout_artifacts_is_a_root(self):
        self.assertIn(p.ROOT / "artifacts", p.artifact_roots())

    def test_cases_keep_their_order_and_reject_unknown_names(self):
        self.assertEqual(p.parse(["--artifacts", "a", "--cases", "control,first"]).cases, ["first", "control"])
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            p.parse(["--artifacts", "a", "--cases", "first,bogus"])


class ClassificationTests(unittest.TestCase):
    def test_refusal_index_counts_pre_download_callbacks(self):
        refusal = p.refusal_of([configuration("pre", "PlcMasterSecretPassword", "generatedScratchCredential")] + REFUSED_LATER)
        self.assertEqual((refusal["index"], refusal["preIndex"], refusal["type"]), (2, 2, "DataBlockReinitialization"))
        self.assertIsNone(p.refusal_of([configuration("pre", "ConsistentBlocksDownload", "ConsistentDownload")]))

    def test_differences_ignore_identical_rows(self):
        self.assertEqual(p.differences(DIFFERING), sorted([["PLC_1", "PLC_1", "FolderContentsDifferent"], ["AbortDb [DB1]", "AbortDb [DB1]", "ObjectsDifferent"]], key=str))
        self.assertEqual(p.differences(IDENTICAL), [])

    def test_compare_shift_is_directional(self):
        before = [["A", "A", "ObjectsDifferent"], ["B", "B", "ObjectsDifferent"]]
        after = [["B", "B", "LeftOnly"], ["C", "C", "ObjectsDifferent"]]
        self.assertEqual(p.compare_shift(before, after), ([["A", "A"]], [["B", "B", "ObjectsDifferent", "LeftOnly"]], [["C", "C", "ObjectsDifferent"]]))

    def test_abort_that_loads_nothing_is_no_gap(self):
        verdict = p.classify(aborted(), "later", ["ConsistentBlocksDownload", "DataBlockReinitialization"], same_change_set=True)
        self.assertEqual((verdict["verdict"], verdict["abortedAt"], verdict["intentMet"], verdict["completeness"]), ("noGap", "later", True, "complete"))

    def test_first_callback_abort(self):
        row = aborted(configurations=[configuration("pre", "DataBlockReinitialization", exception="X")])
        verdict = p.classify(row, "first", ["ConsistentBlocksDownload", "DataBlockReinitialization"])
        self.assertEqual((verdict["verdict"], verdict["abortedAt"], verdict["completeness"]), ("noGap", "first", "notComparable"))

    def test_loaded_abort_is_a_gap(self):
        loaded = {"values": {"Kept": 0, "Cycles": 3, "ProgramVersion": 2}, "cyclesAdvanced": True, "operatingState": "Run", "members": ["Added", "Cycles", "Kept", "ProgramVersion"]}
        verdict = p.classify(aborted(after=loaded, compare_after=IDENTICAL), "later")
        self.assertEqual(verdict["verdict"], "gap")
        self.assertEqual(len(verdict["loadEvidence"]), 4)

    def test_stop_or_configuration_event_is_a_gap_unless_going_online_raises_it(self):
        self.assertEqual(p.classify(aborted(during=watch("operatingState")), "later")["verdict"], "gap")
        self.assertEqual(p.classify(aborted(during=watch("softwareConfiguration")), "later")["verdict"], "gap")
        online = p.classify(aborted(during=watch("softwareConfiguration"), baseline=watch("softwareConfiguration")), "later")
        self.assertEqual(online["verdict"], "inconclusive")
        no_baseline = aborted(during=watch("operatingState"))
        del no_baseline["watchBaseline"]
        self.assertEqual(p.classify(no_baseline, "later")["verdict"], "inconclusive")

    def test_new_or_changed_compare_rows_are_inconclusive_not_a_gap(self):
        changed = node("PLC_1", "FolderContentsDifferent", node("AbortDb [DB1]", "ObjectsDifferent"), node("Main [OB1]", "ObjectsDifferent"))
        verdict = p.classify(aborted(compare_after=changed), "later")
        self.assertEqual((verdict["verdict"], verdict["loadEvidence"]), ("inconclusive", []))

    def test_missing_version_before_is_inconclusive_not_a_gap(self):
        row = aborted()
        del row["before"]["values"]
        self.assertEqual(p.classify(row, "later")["verdict"], "inconclusive")

    def test_missing_configurations_of_the_same_change_set_are_a_gap(self):
        verdict = p.classify(aborted(), "later", ["ConsistentBlocksDownload", "DataBlockReinitialization", "UserManagementDownload"], same_change_set=True)
        self.assertEqual((verdict["verdict"], verdict["missingConfigurations"]), ("gap", ["UserManagementDownload"]))

    def test_post_download_refusal_is_a_gap(self):
        row = aborted(configurations=[configuration("pre", "ConsistentBlocksDownload", "ConsistentDownload"), configuration("post", "Unknown", exception="X")],
                      code="UnsupportedDownloadConfiguration")
        self.assertEqual(p.classify(row, "later")["verdict"], "gap")

    def test_applied_other_failure_or_missing_checks_are_inconclusive(self):
        applied = aborted()
        applied["download"] = {"isError": False, "configurations": [configuration("pre", "DataBlockReinitializationOrKeepActualValues", "KeepActualValues")]}
        self.assertEqual(p.classify(applied, "first")["verdict"], "inconclusive")
        self.assertEqual(p.classify(aborted(code="SimulationDownloadFailed"), "later")["verdict"], "inconclusive")
        unchecked = aborted()
        unchecked["compareAfter"] = {"error": {"code": "RecoveryRequired"}}
        self.assertEqual(p.classify(unchecked, "later")["verdict"], "inconclusive")
        skipped = aborted()
        skipped["download"] = {"skipped": "ticket"}
        self.assertEqual(p.classify(skipped, "later")["verdict"], "inconclusive")
        self.assertEqual(p.classify({}, "first")["verdict"], "inconclusive")

    def test_control_and_overall(self):
        control_row = {"compareBefore": {"differences": p.differences(DIFFERING)}, "compareAfter": {"differences": []},
                       "after": {"values": {"Kept": 0, "Cycles": 5, "ProgramVersion": 2}, "operatingState": "Run", "members": ["Added", "Cycles", "Kept", "ProgramVersion"]},
                       "watch": {"kinds": ["softwareConfiguration"], "softwareChanges": ["x"]},
                       "download": {"isError": False, "configurations": [configuration("pre", "ConsistentBlocksDownload", "ConsistentDownload"),
                                                                         configuration("pre", "DataBlockReinitialization", "StopPlcAndReinitialize"),
                                                                         configuration("post", "StartModules", "StartModule")]}}
        control = p.control_check(control_row)
        self.assertTrue(control["sensitive"])
        self.assertEqual(control["preDownloadConfigurations"], ["ConsistentBlocksDownload", "DataBlockReinitialization"])
        first = p.classify(aborted(configurations=[configuration("pre", "DataBlockReinitialization", exception="X")]), "first")
        later = p.classify(aborted(), "later", control["preDownloadConfigurations"], same_change_set=True)
        self.assertEqual(p.overall({"first": first, "later": later}, control)["result"], "noGap")
        self.assertEqual(p.overall({"later": later}, control)["result"], "inconclusive")
        self.assertEqual(p.overall({"first": first, "later": later}, {**control, "sensitive": False})["result"], "inconclusive")
        insensitive = p.control_check({**control_row, "compareAfter": {"differences": p.differences(DIFFERING)}})
        self.assertFalse(insensitive["sensitive"])


class FakeHost:
    """Stands in for Harness, the Host and one PLCSIM instance per lifecycle. A download TIA would refuse records the
    refused callback and loads nothing; an answered one loads the offline program. or_keep: the first DB change is offered
    DataBlockReinitializationOrKeepActualValues, as the optimized DB of 29 October was."""

    def __init__(self, sim_worker, cases, or_keep=False, db_first=False):
        self.args = argparse.Namespace(cases=cases, sim_worker=sim_worker, trust_owned_empty_cpu_certificate=True)
        self.report = {"steps": [], "mutations": []}
        self.client, self.calls, self.prepared, self.states, self.or_keep, self.instances = self, [], {}, {}, or_keep, 0
        self.db_first = db_first

    def persist(self):
        pass

    def stage(self, name, operation):
        self.report["steps"].append(name)
        return operation()

    def unresolved_admissions(self):
        return []

    @staticmethod
    def apply_args(prepared):
        return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": "k", "checkpointSave": True, "compile": True, "save": True}

    def owned_scratch(self, label):
        self.calls.append("tia_start")
        self.world = {"offline": {"members": [], "version": 1}, "online": None, "kept": 0, "cycles": 0, "watch": None}
        self.scratch = {"scratchSecurity": {"cpus": [{"credentialMetadataPath": "secret", "runtimeUser": {"supported": True, "credentialMetadataPath": "login"}}]}}
        return "/PLC_1/plc/PLC_1"

    def apply(self, kind, payload):
        self.calls.append("apply " + kind)
        if kind == "source_upsert":
            source = payload["source"]
            self.world["offline"]["members"] = [m for m in re.findall(r"(\w+) : Int;", source) if m != "ProgramVersion"]
            version = re.search(r"ProgramVersion := (\d+);", source)
            if version:
                self.world["offline"]["version"] = int(version.group(1))
        return {"state": "applied"}

    def tool(self, name, arguments=None):
        result = self.request("tools/call", {"name": name, "arguments": arguments or {}})
        if result.get("isError"):
            raise RuntimeError("%s: %s" % (name, result))
        return result["structuredContent"]

    def request(self, method, params):
        name, a = params["name"], params["arguments"]
        self.calls.append(name)
        ok = lambda content: {"isError": False, "structuredContent": content}
        w = self.world
        if name == "tia_detach":
            return ok({})
        if name == "simulation_discovery_baseline":
            return ok({"baseline": {}})
        if name == "sim_create":
            self.instances += 1
            return ok({"instance": {"id": self.instances, "name": "TiaMcp_x", "owned": True}, "downloadTicket": "ticket"})
        if name == "simulation_download":
            w["online"] = copy.deepcopy(w["offline"])
            return ok({"completed": True})
        if name in ("simulation_provision_scratch_credential", "sim_write"):
            if name == "sim_write":
                w["kept"] = a["writes"][0]["value"]
            return ok({})
        if name == "sim_state":
            return ok({"instance": {"operatingState": "Run"}})
        if name == "sim_tags":
            return ok({"tags": [{"symbol": '"AbortDb".%s' % m} for m in ["Kept", "Cycles", "ProgramVersion"] + w["online"]["members"]]})
        if name == "sim_read":
            values = []
            for signal in a["signals"]:
                member = signal["symbol"].split(".")[1]
                if member == "Cycles":
                    w["cycles"] += 1
                values.append({"quality": "good", "value": {"Kept": w["kept"], "Cycles": w["cycles"], "ProgramVersion": w["online"]["version"]}[member]})
            return ok({"values": values})
        if name == "sim_observe":
            if a.get("watch") == "start":
                w["watch"] = []
                return ok({"watchStartedAtUtc": "now"})
            if a.get("watch") == "collect":
                events, w["watch"] = w["watch"] or [], None
                return ok({"events": events, "truncated": False})
            return ok({"after": {"operatingState": "Run"}})
        if name == "plc_online_compare":
            db = "ObjectsIdentical" if w["online"]["members"] == w["offline"]["members"] else "ObjectsDifferent"
            main = "ObjectsIdentical" if w["online"]["version"] == w["offline"]["version"] else "ObjectsDifferent"
            root = "FolderContentsIdentical" if db == main == "ObjectsIdentical" else "FolderContentsDifferent"
            return ok({"compareTruncated": False, "compare": node("PLC_1", root, node("AbortDb [DB1]", db), node("Main [OB1]", main))})
        if name == "change_prepare":
            change = "c%d" % len(self.prepared)
            self.prepared[change] = a["payload"]
            return ok({"changeId": change, "previewHash": "h"})
        if name == "change_apply":
            result = self.download(self.prepared[a["changeId"]])
            # A refused apply ends recovery_required (OpennessSession.Changes.cs).
            self.states[a["changeId"]] = "recovery_required" if result["isError"] else "applied"
            return result
        if name == "change_status":
            return ok({"state": self.states[a["changeId"]]})
        if name == "sim_release":
            return ok({"unregistered": True})
        raise AssertionError("unexpected tool " + name)

    def download(self, payload):
        w, configurations, code = self.world, [], None
        keep = payload["dataBlockValues"] == "keepActualValues"
        if payload["softwareScope"] == "all" and not payload["allowStop"]:
            configurations.append(configuration("pre", "StopModules", exception="X"))
            code = "SimulationDownloadStopRequired"
        elif self.db_first and keep and w["online"]["members"] != w["offline"]["members"]:
            configurations.append(configuration("pre", "DataBlockReinitialization", exception="X"))
            code = "SimulationDownloadReinitializationRequired"
        else:
            if w["online"]["version"] != w["offline"]["version"]:
                configurations.append(configuration("pre", "ConsistentBlocksDownload", "ConsistentDownload"))
            if w["online"]["members"] != w["offline"]["members"]:
                if self.or_keep:
                    self.or_keep = False
                    configurations.append(configuration("pre", "DataBlockReinitializationOrKeepActualValues", "KeepActualValues"))
                elif keep:
                    configurations.append(configuration("pre", "DataBlockReinitialization", exception="X"))
                    code = "SimulationDownloadReinitializationRequired"
                else:
                    configurations.append(configuration("pre", "DataBlockReinitialization", "StopPlcAndReinitialize"))
        readbacks = [{"role": "owned-simulation-download", "report": {"downloadConfigurations": configurations}}]
        if code:
            return {"isError": True, "structuredContent": {"error": {"code": code, "message": "refused", "details": {"readbacks": readbacks}}}}
        configurations.append(configuration("post", "StartModules", "StartModule"))
        w["online"] = copy.deepcopy(w["offline"])
        if not keep:
            w["kept"] = 0
        if w["watch"] is not None:
            w["watch"].append({"kind": "softwareConfiguration", "changeType": "x"})
        return {"isError": False, "structuredContent": {"state": "applied", "readbacks": readbacks}}


class BodyTests(unittest.TestCase):
    def run_body(self, **options):
        with tempfile.TemporaryDirectory() as directory:
            sim_worker = pathlib.Path(directory) / "sim.exe"
            sim_worker.write_bytes(b"x")
            h = FakeHost(sim_worker, ["first", "later", "control"], **options)
            identity = lambda value, *rest, **keywords: value
            with mock.patch.object(p, "verify_owned_instance", identity), mock.patch.object(p, "verify_download", identity), \
                    mock.patch.object(p, "verify_memory_card", identity), mock.patch.object(p, "save_generated_scratch_project", lambda call, value: {}), \
                    mock.patch.object(p, "verify_credential_provisioning", identity), mock.patch.object(p.time, "sleep", lambda seconds: None):
                p.body(h)
            return h

    def test_three_lifecycles_answer_the_question(self):
        h = self.run_body()
        self.assertEqual(h.report["outcome"]["result"], "noGap")
        self.assertEqual((h.calls.count("tia_start"), h.calls.count("tia_detach"), h.calls.count("sim_release")), (3, 2, 3))
        verdicts = h.report["verdicts"]
        self.assertEqual((verdicts["first"]["abortedAt"], verdicts["later"]["abortedAt"], verdicts["later"]["completeness"]), ("first", "later", "complete"))
        self.assertTrue(h.report["control"]["sensitive"] and h.report["control"]["keptReinitialized"] and h.report["control"]["newMemberInPlcsim"])
        self.assertEqual([m["state"] for m in h.report["mutations"]], ["recovery_required", "recovery_required", "applied"])

    def test_or_keep_runs_the_full_download_fallback(self):
        h = self.run_body(or_keep=True)
        verdicts = h.report["verdicts"]
        self.assertEqual(verdicts["first"]["verdict"], "inconclusive")
        self.assertEqual((verdicts["first-fallback"]["verdict"], verdicts["first-fallback"]["refusal"]["type"]), ("noGap", "StopModules"))
        self.assertEqual(h.report["outcome"]["result"], "noGap")

    def test_a_refusal_before_other_questions_hides_them(self):
        h = self.run_body(db_first=True)
        later = h.report["verdicts"]["later"]
        self.assertEqual((later["abortedAt"], later["verdict"], later["missingConfigurations"]), ("first", "gap", ["ConsistentBlocksDownload"]))
        self.assertEqual(h.report["outcome"]["result"], "gap")


if __name__ == "__main__":
    unittest.main()
