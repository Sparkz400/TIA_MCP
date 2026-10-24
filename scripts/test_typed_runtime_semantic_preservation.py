"""Offline checks of the typed semantic runtime preservation rule; no native evidence."""
import copy
import unittest

from typed_runtime_semantic_preservation import evaluate


def passing():
    card = {"verifiedBeforePowerOn": True, "programRevision": "a" * 64, "files": [{"path": "S7_JOB.S7S", "bytes": 7, "sha256": "b" * 64}]}
    inventory = {"complete": True, "files": {}}
    def call(tool, at, **arguments):
        return {"tool": tool, "startedAtUtc": at, "arguments": arguments, "status": "passed"}
    return {"ownedCleanupVerified": True,
        "runtime": {"status": "passed", "caseCoverage": {"fullSuitePassed": True}, "cases": [{"isolation": {
            "memoryCard": card, "restoredMemoryCard": copy.deepcopy(card),
            "credentialProvisioning": {"originalProgramCardRestored": True, "operatingState": "Off"},
            "generatedProjectSave": {"prepared": {"changeId": "c1", "preview": {"kind": "plc_compile"}}, "applied": {"state": "applied"}}}}]},
        "calls": [call("change_apply", "1", changeId="engineering"), call("sim_create", "3"), call("simulation_download", "3"),
            call("change_prepare", "4", kind="plc_compile"), call("change_apply", "5", changeId="c1"),
            call("simulation_provision_scratch_credential", "6"), call("sim_read", "7"), call("block_read", "8"), call("tia_detach", "9")],
        "runtimePreservation": {"ownedTiaCleanupVerified": True, "generatedCardVerification": {"files": []},
            "projectActiveBeforeRuntime": {"observedAtUtc": "2", "complete": True},
            "closedToActiveTransition": {"addedInfoFiles": ["P.info"]},
            "observations": [
                {"phase": "beforeOwnedRuntimeCleanup", "target": "memoryCard", "status": "failed", "inventory": {"complete": True, "files": {"RData/retain_user": {}}}},
                {"phase": "beforeOwnedRuntimeCleanup", "target": "activeProjectAndAllCompanions", "status": "failed",
                 "inventory": inventory, "difference": {"added": [], "removed": [], "changed": ["P.ap21", "P.info"]}},
                {"phase": "beforeOwnedRuntimeCleanup", "target": "completePlcXml", "status": "passed"},
                {"phase": "afterVerifiedOwnedTiaCleanup", "target": "projectAndAllCompanions", "status": "failed",
                 "inventory": inventory, "difference": {"added": ["P.info"], "removed": [], "changed": ["P.ap21"]}}]}}


class SemanticRuntimePreservationTests(unittest.TestCase):
    def test_receipted_changes_and_post_run_card_state_pass(self):
        verdict = evaluate(passing())
        self.assertEqual([], verdict["failures"])
        self.assertEqual(["RData/retain_user"], list(verdict["postRunCardInventories"][0]["files"]))

    def test_each_condition_fails_closed(self):
        def broken(change):
            report = passing(); change(report); return evaluate(report)["failures"]
        isolation = lambda r: r["runtime"]["cases"][0]["isolation"]
        observation = lambda r, target: next(o for o in r["runtimePreservation"]["observations"] if o["target"] == target and o["phase"].startswith("before"))
        self.assertTrue(broken(lambda r: isolation(r)["restoredMemoryCard"].update(programRevision="c" * 64)))
        self.assertTrue(broken(lambda r: isolation(r)["credentialProvisioning"].update(originalProgramCardRestored=False)))
        self.assertTrue(broken(lambda r: observation(r, "completePlcXml").update(status="failed")))
        self.assertTrue(broken(lambda r: r["calls"].append({"tool": "block_import_xml", "startedAtUtc": "8", "arguments": {}, "status": "passed"})))
        self.assertTrue(broken(lambda r: r["calls"].append({"tool": "change_apply", "startedAtUtc": "8", "arguments": {"changeId": "other"}, "status": "passed"})))
        self.assertTrue(broken(lambda r: r["runtimePreservation"]["observations"][-1]["difference"]["changed"].append("System/PEData.plf")))
        self.assertTrue(broken(lambda r: r["runtimePreservation"]["observations"][-1]["difference"]["removed"].append("P.ap21")))
        self.assertTrue(broken(lambda r: r.update(ownedCleanupVerified=False)))


if __name__ == "__main__":
    unittest.main()
