"""File/fake-controller tests only. No Host or Siemens process starts."""
import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

import component_window_recovery as module


class Checks(unittest.TestCase):
    def fixture(self, folder):
        root = Path(folder).resolve(); source = root / "failed.json"; identifier = "a" * 32; capability = "b" * 64
        manifest = {"workspace": str(root / "q/caaaaaaaaaaaa"), "projectPath": str(root / "p.ap21"), "resources": []}
        owner = {"id": identifier, "capability": capability, "manifestSha256": "c" * 64, "workspace": manifest["workspace"]}
        record = {"manifest": manifest, "manifestSha256": owner["manifestSha256"], "ownerCapabilitySha256": hashlib.sha256(capability.encode()).hexdigest(), "state": "outcomeUnknown", "children": [{"step": "start", "state": "returned"}, {"step": "applyLibrary", "state": "outcomeUnknown"}]}
        ledger = root / "engineering-component-windows" / identifier; ledger.mkdir(parents=True)
        journal = ledger / "00000000.json"; journal.write_text(json.dumps(record))
        previous = {"status": "failed", "componentOwner": owner, "nativeArtifactRoot": str(root)}; source.write_text(json.dumps(previous))
        run = SimpleNamespace(store_root=root, native=root / "new-fixture", output=root / "output", report={}, persist=lambda: None,
                              args=SimpleNamespace(host="host", worker="worker", timeout=60, known_store_root=[]), session=None)
        return run, source, journal, previous, record

    def test_exact_parent_input_is_read_only(self):
        with tempfile.TemporaryDirectory() as folder:
            run, source, journal, _, record = self.fixture(folder); before = source.read_bytes(), journal.read_bytes()
            result = module.read_inputs(run, source)
            self.assertEqual(record, result[2]); self.assertEqual(before, (source.read_bytes(), journal.read_bytes()))

    def test_legacy_wrong_capability_terminal_or_foreign_root_refuse_before_dispatch(self):
        for mutation in (lambda p, r: p.pop("componentOwner"), lambda p, r: p["componentOwner"].update(capability="e" * 64),
                         lambda p, r: r.update(state="recoveryReconciled"), lambda p, r: p.update(nativeArtifactRoot=str(Path(p["nativeArtifactRoot"]) / "elsewhere"))):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as folder:
                run, source, journal, previous, record = self.fixture(folder); mutation(previous, record)
                source.write_text(json.dumps(previous)); journal.write_text(json.dumps(record)); factory = mock.Mock()
                with self.assertRaises(RuntimeError): module.run_recovery(run, source, factory, None, ())
                factory.assert_not_called()

    def test_lost_recovery_response_closes_exact_session_before_foreign_locks_without_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            run, source, _, previous, record = self.fixture(folder); events = []
            class Window:
                def __init__(self, *args, **kwargs): self.keys, self.evidence = [], {}
                def __enter__(self): events.append("lock-enter"); return self
                def __exit__(self, *args): events.append("lock-exit")
            class Lifetime:
                def __init__(self, run): self.held = None
                def resume(self): events.append("resume-only"); return record
                def control(self, action):
                    events.append(action)
                    if action == "recover": raise TimeoutError("lost exact retrieval response")
                    return record
                def close(self): events.append("foreign-lock-close")
            class Session:
                def __init__(self, *args, **kwargs): self.current_project = None; self.known_paths = set()
                def start(self): events.append("fresh-owned-start")
                def close(self): events.append("same-retained-tree-terminal")
            with mock.patch.object(module, "StoreExclusiveWindow", Window), mock.patch.object(module, "ComponentLifetime", Lifetime), mock.patch.object(module, "scan_component_reservations", return_value=[]):
                with self.assertRaisesRegex(TimeoutError, "lost exact retrieval"):
                    module.run_recovery(run, source, Session, None, ())
            self.assertEqual(1, events.count("recover")); self.assertLess(events.index("same-retained-tree-terminal"), events.index("foreign-lock-close"))
            self.assertNotIn("installationAccepted", previous)


if __name__ == "__main__": unittest.main()
