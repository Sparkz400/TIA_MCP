"""Package smoke exercises external launcher configuration without native calls."""
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

import smoke_package


TOOLS = ["project_retrieve", "call_graph", "change_apply", "safety_inspect", "simulation_download",
         "sim_test", "artifact_read", "feature_catalog", "object_resolve", "target_capabilities"]
FEATURES = [{"featureId": name} for name in TOOLS]


class FakeClient:
    def __init__(self, *args, **kwargs):
        self.process = SimpleNamespace(returncode=0)
        self.errors = []

    def request(self, method, params=None):
        if method == "initialize":
            return {"protocolVersion": "2025-11-25"}
        if method == "tools/list":
            return {"tools": [{"name": name} for name in TOOLS]}
        raise AssertionError(method)

    def notify(self, *args):
        pass

    def tool(self, name, args=None):
        if name == "feature_catalog":
            offset=args.get('offset',0); rows=FEATURES[offset:offset+args.get('maxItems',100)]; end=offset+len(rows)
            return {"features": rows, "catalogVersion": "fixture", "targetAvailabilityProbed": False,
                    "pagination": {"total":len(FEATURES),"complete":end==len(FEATURES),"nextOffset":end}}
        if name in ("tia_capabilities", "tia_discover", "sim_status"):
            return {"fixtureOnly": True}
        raise AssertionError(name)

    def close(self):
        return {"hostExitCode": 0, "forcedHostStop": False, "stderrEofObserved": True,
                "workerExits": [{"processId": pid, "processExitVerified": True, "exitCode": 0,
                                 "forcedTermination": False} for pid in (123, 456)]}


class PackageSmokeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.bundle = self.root / "bundle"
        contracts = self.bundle / "contracts"
        contracts.mkdir(parents=True)
        catalog = contracts / "feature-catalog.v1.json"
        catalog.write_text(json.dumps({"catalogVersion":"fixture", "features": FEATURES}), encoding="utf-8")
        manifest = {"files": [{"path": "contracts/feature-catalog.v1.json", "bytes": catalog.stat().st_size,
                               "sha256": hashlib.sha256(catalog.read_bytes()).hexdigest()}]}
        (self.bundle / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        self.configuration = self.root / "bundle.mcp.json"
        self.configuration.write_text(json.dumps({"mcpServers": {"tia-v21": {
            "command": "fixture-launcher", "args": ["argument with spaces"]}}}), encoding="utf-8")
        self.report = self.root / "report.json"

    def run_smoke(self, extra=(), client=FakeClient):
        argv = ["smoke_package.py", "--bundle", str(self.bundle), "--report", str(self.report), *extra]
        with patch("sys.argv", argv), patch.object(smoke_package, "Client", side_effect=client) as factory:
            code = smoke_package.main()
        return code, factory

    def test_generated_external_configuration_and_direct_host_are_both_exercised(self):
        code, factory = self.run_smoke()
        self.assertEqual(code, 0)
        self.assertEqual(factory.call_count, 2)
        self.assertEqual(factory.call_args_list[0].kwargs["command"], ["fixture-launcher", "argument with spaces"])
        self.assertEqual(factory.call_args_list[1].args[0], self.bundle / "host/TiaMcp.Host.exe")
        report = json.loads(self.report.read_text())
        self.assertTrue(report["completed"])
        self.assertEqual(report["configuration"]["sha256"], hashlib.sha256(self.configuration.read_bytes()).hexdigest())
        self.assertFalse((self.bundle / "mcp.local.json").exists())

    def test_explicit_relocated_configuration_is_used(self):
        relocated = self.root / "relocated.json"
        self.configuration.rename(relocated)
        code, _ = self.run_smoke(["--configuration", str(relocated)])
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(self.report.read_text())["configuration"]["path"], str(relocated.resolve()))

    def test_unverified_worker_cleanup_keeps_smoke_failed(self):
        class UnverifiedWorker(FakeClient):
            def close(self):
                result = super().close()
                result["workerExits"][0]["processExitVerified"] = False
                return result
        code, factory = self.run_smoke(client=UnverifiedWorker)
        self.assertEqual(code, 1)
        self.assertEqual(factory.call_count, 1)
        self.assertFalse(json.loads(self.report.read_text())["completed"])

    def test_existing_report_is_preserved_without_launch(self):
        self.report.write_bytes(b"original outcome")
        with self.assertRaises(SystemExit):
            self.run_smoke(client=lambda *args, **kwargs: self.fail("No client may start"))
        self.assertEqual(self.report.read_bytes(), b"original outcome")

    def test_missing_or_duplicate_worker_receipts_never_pass_cleanup(self):
        for process_ids in ((123,), (123, 123), (123, True), (123, 0), (123, 456, 789)):
            receipt = FakeClient().close()
            receipt["workerExits"] = [{**receipt["workerExits"][0], "processId": pid} for pid in process_ids]
            with self.subTest(process_ids=process_ids):
                self.assertFalse(smoke_package.clean_shutdown(receipt))

    def test_direct_constructor_failure_retains_launcher_evidence(self):
        code, _ = self.run_smoke(client=[FakeClient(), RuntimeError("direct construction failed")])
        self.assertEqual(code, 1)
        report = json.loads(self.report.read_text())
        self.assertFalse(report["completed"])
        self.assertTrue(smoke_package.clean_shutdown(report["launcherShutdown"]))
        self.assertEqual(report["directHostFailure"], "direct construction failed")
        self.assertEqual(len(report["steps"]), 6)

    def test_launcher_constructor_failure_retains_failure_report(self):
        code, _ = self.run_smoke(client=[RuntimeError("launcher construction failed")])
        self.assertEqual(code, 1)
        report = json.loads(self.report.read_text())
        self.assertFalse(report["completed"])
        self.assertEqual(report["failure"], "launcher construction failed")

    def test_cleanup_exception_retains_failure_report(self):
        class FailedCleanup(FakeClient):
            def close(self):
                raise RuntimeError("cleanup receipt unavailable")
        code, factory = self.run_smoke(client=FailedCleanup)
        self.assertEqual(code, 1)
        self.assertEqual(factory.call_count, 1)
        report = json.loads(self.report.read_text())
        self.assertFalse(report["completed"])
        self.assertEqual(report["launcherCleanupFailure"], "cleanup receipt unavailable")


if __name__ == "__main__":
    unittest.main()
