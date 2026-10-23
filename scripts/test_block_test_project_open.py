"""Exact known project-lock retries with an injected clock and MCP double."""
import copy
import pathlib
import unittest

from block_test_runtime import NativeToolError
import run_block_tests as runner


PROJECT = str((runner.ROOT / "artifacts/scratch/qualified/qualified.ap21").resolve())


def lock_error(path=PROJECT, user="test-user", computer="TEST-HOST", *, mojibake=False):
    message = ("Error when calling method 'Open' of type 'Siemens.Engineering.ProjectComposition'.\r\n\r\n"
        + "Unable to open the project under path '" + path + "'.\r\n\r\nAn error occurred while opening the project\r\n"
        + "Auf das Projekt/die Bibliothek " + path + " kann nicht zugegriffen werden. Es/sie wurde bereits vom Benutzer "
        + user + " auf dem Computer " + computer + " geöffnet. Hinweis: Wenn die Anwendung nicht korrekt geschlossen wurde, "
        + "können die geöffneten Projekte und Bibliotheken erst nach einer Verzögerung von 2 Minuten wieder geöffnet werden.")
    if mojibake:
        message = message.replace("ö", "\u00c3\u00b6")
    return {"error": {"code": "EngineeringError", "message": message,
                      "details": {"exceptionType": "Siemens.Engineering.EngineeringTargetInvocationException"}}}


class ProjectOpenTests(unittest.TestCase):
    def run_open(self, responses, **options):
        owner = self
        self.calls, self.records, self.evidence, self.now = [], [], {}, 0.0
        class Client:
            def request(self, method, params):
                owner.calls.append(copy.deepcopy(params))
                response = responses.pop(0)
                return {"structuredContent": response, "isError": "error" in response}
        def sleep(seconds):
            self.now += seconds
        audit = runner.CaseSessionAudit(Client(), self.records, lambda: None, PROJECT)
        return runner.open_qualified_project(audit, PROJECT, self.evidence, lambda: None,
            user="test-user", computer="TEST-HOST", clock=lambda: self.now, sleep=sleep, **options)

    def test_known_rejections_remain_in_native_audit_before_success(self):
        result = self.run_open([lock_error(), lock_error(mojibake=True),
                               {"opened": True, "saved": False, "project": {"path": PROJECT}}])
        self.assertTrue(result["opened"])
        self.assertEqual(["failed", "failed", "passed"], [row["status"] for row in self.records])
        self.assertEqual(["rejected", "rejected", "passed"], [row["status"] for row in self.evidence["projectOpenRetry"]["attempts"]])
        self.assertEqual(10, self.now)
        self.assertTrue(all(row["arguments"] == {"path": PROJECT} for row in self.calls))

    def test_other_user_host_path_code_or_native_exception_never_retries(self):
        for result in (lock_error(user="other"), lock_error(computer="OTHER-HOST"), lock_error(path=PROJECT + ".other"),
                       {"error": {**lock_error()["error"], "code": "TimeoutOutcomeUnknown"}},
                       {"error": {**lock_error()["error"], "details": {"exceptionType": "Other"}}},
                       {"error": {"code": "EngineeringError", "message": "Project locked", "details": {}}}):
            with self.subTest(result=result), self.assertRaises(NativeToolError):
                self.run_open([result])
            self.assertEqual(1, len(self.calls))
            self.assertEqual(0, self.now)

    def test_monotonic_budget_does_not_admit_another_open_after_expiry(self):
        with self.assertRaises(NativeToolError):
            self.run_open([lock_error()] * 5, budget_ms=10, interval_ms=5)
        self.assertEqual(2, len(self.calls))
        self.assertTrue(self.evidence["projectOpenRetry"]["exhausted"])

    def test_attempt_limit_is_independent_of_time_budget(self):
        with self.assertRaises(NativeToolError):
            self.run_open([lock_error()] * 5, budget_ms=1000, interval_ms=1, max_attempts=2)
        self.assertEqual(2, len(self.calls))
        self.assertEqual(0.001, self.now)

    def test_wrong_success_identity_or_save_is_not_retried(self):
        for result in ({"opened": True, "saved": True, "project": {"path": PROJECT}},
                       {"opened": True, "saved": False, "project": {"path": PROJECT + ".other"}}):
            with self.subTest(result=result), self.assertRaises(RuntimeError):
                self.run_open([result])
            self.assertEqual(1, len(self.calls))

    def test_observed_corpus_error_matches_current_account_without_broadening(self):
        path = runner.ROOT / "artifacts/block-tests/runtime-all-20261008-owned-tls-02/runtime-boolean/live-result.json"
        if not path.exists():
            self.skipTest("Task-local native observation unavailable")
        import json
        report = json.loads(path.read_text())
        audit = next(row for row in report["runtime"] if row["caseId"] == "truth-3")
        call = next(row for row in audit["calls"] if row["status"] == "failed")
        self.assertTrue(runner.same_user_project_lock(NativeToolError(call["tool"], call["result"]), report["revision"]["project"]["path"]))


if __name__ == "__main__":
    unittest.main()
