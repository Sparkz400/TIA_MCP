"""Exact case-manifest admission, archival, and partial qualification boundaries."""
import copy
import hashlib
import json
import pathlib
import unittest
from unittest import mock

import run_block_tests as runner
from test_block_test_stages import NativeClient, ProcessWatcher, temporary_directory, write_binaries


class CaseSelectionTests(unittest.TestCase):
    def setUp(self):
        self.suite = runner.load_suites(["runtime-boolean"])[0]
        self.manifest = {"schemaVersion": 1, "suites": {self.suite["fixtureId"]: {
            "suiteSha256": runner.digest(self.suite),
            "cases": [{"caseId": "truth-7", "repetition": 1}, {"caseId": "truth-3", "repetition": 1}]}}}

    def test_manifest_preserves_raw_bytes_and_catalog_order(self):
        with temporary_directory() as directory:
            path = pathlib.Path(directory) / "selection.json"
            raw = (json.dumps(self.manifest, indent=3) + "\n").encode()
            path.write_bytes(raw)
            selected, archived = runner.load_case_selection(path, [self.suite])
        self.assertEqual(raw, archived)
        self.assertEqual({"runtime-boolean": [("truth-3", 1), ("truth-7", 1)]}, selected)

    def test_unknown_duplicate_bool_or_wrong_revision_is_rejected(self):
        mutations = []
        for field, value in (("suiteSha256", "0" * 64), ("cases", []),
                             ("cases", [{"caseId": "truth-3", "repetition": True}]),
                             ("cases", [{"caseId": "truth-3", "repetition": 2}]),
                             ("cases", [{"caseId": "unknown", "repetition": 1}]),
                             ("cases", [{"caseId": "truth-3", "repetition": 1}] * 2)):
            changed = copy.deepcopy(self.manifest)
            changed["suites"]["runtime-boolean"][field] = value
            mutations.append(json.dumps(changed))
        mutations.extend(['{"schemaVersion":1,"schemaVersion":1,"suites":{}}',
                          json.dumps({**self.manifest, "extra": "unsupported"})])
        with temporary_directory() as directory:
            path = pathlib.Path(directory) / "selection.json"
            for text in mutations:
                path.write_text(text)
                with self.subTest(text=text), self.assertRaises(ValueError):
                    runner.load_case_selection(path, [self.suite])

    def test_cli_requires_live_before_artifacts_or_native(self):
        for mode in ([], ["--engineering-only"]):
            with self.subTest(mode=mode), mock.patch.object(runner, "prepare_suite") as prepare, mock.patch.object(runner, "qualify_suite") as native:
                with self.assertRaises(SystemExit):
                    runner.main([*mode, "--case-selection", "absent.json"])
                prepare.assert_not_called()
                native.assert_not_called()

    def test_cli_archives_manifest_and_retains_partial_scope_after_all_selected_pass(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            selection_path = root / "selection.json"
            raw = (json.dumps(self.manifest, indent=3) + "\n").encode()
            selection_path.write_bytes(raw)
            output = root / "result"
            report = {"status": "passed", "engineeringVerified": True, "runtimeVerified": False,
                      "qualificationScope": "selected_cases", "selectedCasesPassed": True,
                      "caseCoverage": {"expectedCount": 8, "selectedCount": 2, "passedCount": 2}}
            with mock.patch.object(runner, "qualify_suite", return_value=report) as qualify:
                self.assertEqual(0, runner.main(["--live", "--host", str(host), "--worker", str(worker),
                    "--sim-worker", str(simulator), "--case-selection", str(selection_path), "--report-directory", str(output)]))
            index = json.loads((output / "index.json").read_text())
            self.assertEqual(raw, (output / "case-selection.json").read_bytes())
            self.assertEqual(hashlib.sha256(raw).hexdigest(), index["caseSelectionManifest"]["sha256"])
            self.assertEqual("selected_cases", index["qualificationScope"])
            self.assertTrue(index["selectedCasesPassed"])
            self.assertFalse(index["runtimeVerified"])
            self.assertEqual(1, len(index["results"]))
            self.assertEqual([("truth-3", 1), ("truth-7", 1)], qualify.call_args.kwargs["case_selection"])
            self.assertEqual(self.suite, qualify.call_args.args[0])
            self.assertEqual(self.suite, json.loads((output / "runtime-boolean/suite.json").read_text()))

    def test_partial_qualification_passes_exact_selection_without_promoting_runtime(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            native = NativeClient(self.suite, root / "native", runtime=True)
            output = root / "result"
            runner.prepare_suite(self.suite, output)
            execute = mock.Mock(return_value={"status": "passed", "runtimeQualified": False,
                "qualificationScope": "selected_cases", "selectedCasesPassed": True, "cases": [],
                "caseCoverage": {"expectedCount": 8, "selectedCount": 2, "passedCount": 2}})
            report = runner.qualify_suite(self.suite, output, host, 5, worker=worker, sim_worker=simulator,
                client_factory=native.factory, process_watcher_factory=ProcessWatcher, execute=execute,
                case_selection=[("truth-7", 1), ("truth-3", 1)])
            self.assertEqual("passed", report["status"], report.get("failure"))
            self.assertTrue(report["selectedCasesPassed"])
            self.assertFalse(report["runtimeVerified"])
            self.assertEqual("selected_cases", report["qualificationScope"])
            self.assertEqual([("truth-3", 1), ("truth-7", 1)], execute.call_args.kwargs["case_selection"])
            self.assertEqual(runner.digest(self.suite), report["revision"]["suiteSha256"])

    def test_invalid_selection_fails_before_creating_native_client(self):
        with temporary_directory() as directory:
            root = pathlib.Path(directory)
            host, worker, simulator = write_binaries(root)
            output = root / "result"
            runner.prepare_suite(self.suite, output)
            client = mock.Mock()
            report = runner.qualify_suite(self.suite, output, host, 5, worker=worker, sim_worker=simulator,
                client_factory=client, case_selection=[("truth-3", True)])
            self.assertNotEqual("passed", report["status"])
            client.assert_not_called()


if __name__ == "__main__":
    unittest.main()
