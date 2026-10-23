"""File-only tests; every process and Windows boundary is synthetic."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import reconcile_retained_tree_v2 as q


class Api:
    def __init__(self, controller, rows):
        self.controller = controller; self.opened = []; self.terminated = []; self.closed = []; self.live = {9000}
        self.infos = {9000: {"pid": controller["pid"], "imagePath": controller["exe"],
            "originalFileTimeTicks": int((controller["created"] + 11644473600) * 10000000)}}
        for n, row in enumerate(rows, 1):
            self.infos[n] = {"pid": row["identity"]["pid"], "imagePath": row["handle"]["imagePath"],
                "originalFileTimeTicks": row["handle"]["originalFileTimeTicks"]}
        self.candidates = [{"sourceHandle": n, "grantedAccess": 0x101000, "objectTypeIndex": 7} for n in range(1, 4)]
    def open_controller(self, pid): self.opened.append(pid); return 9000
    def handles(self, pid): assert pid == self.controller["pid"]; return self.candidates
    def duplicate(self, controller, source): assert controller == 9000; return source
    def process_info(self, handle): return self.infos.get(handle)
    def wait(self, handle, milliseconds=0): return 258 if handle in self.live else 0
    def exit_code(self, handle): return 125 if handle == 9000 else (1 if handle == 1 else 0)
    def terminate_controller(self, handle):
        assert handle == 9000; assert self.live == {9000}
        self.terminated.append(handle); self.live.remove(handle)
    def close(self, handle): self.closed.append(handle); return True


class Tests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.root = Path(self.tmp.name)
        self.controller = {"pid": 43076, "parentPid": 1, "created": 1000., "name": "python.exe", "exe": str(self.root / "python.exe"), "command": ["python.exe", "qualify_exact_worker_startup.py", "wr01"]}
        rows = []; previous = []
        for role, pid, parent, name in (("host", 21916, 43076, "TiaMcp.Host.exe"), ("worker", 23084, 21916, "TiaMcp.Openness.V21.exe"), ("native", 25216, 23084, "Siemens.Automation.Portal.exe")):
            info = {"pid": pid, "parentPid": parent, "created": 1100. + len(rows), "name": name, "exe": str(self.root / name), "command": [name]}
            row = {"identity": info, "role": role, "ancestry": list(reversed(previous)), "handle": {"processId": pid, "handleCaptured": True,
                "handleClosed": False, "birthAndImageVerified": True, "sameOsHandleRetained": True, "imagePath": info["exe"],
                "originalFileTimeTicks": int((info["created"] + 11644473600) * 10000000)}}
            rows.append(row); previous.append(info)
            q.emit(self.root / (role + ".json"), row)
        blocked = {"workerStartupCaptured": True, "nativeStartupInventoryComplete": True,
            "retained": [{"role": r["role"], "identity": r["identity"]} for r in rows]}
        q.emit(self.root / "blocked.json", blocked)
        self.rows = rows; self.api = Api(self.controller, rows)
        self.admission = {"kind": "reviewed-retained-tree-reconciliation", "automaticReplay": False, "utility": q.ref(q.__file__),
            "controller": self.controller, "startReceipts": [q.ref(self.root / (r["role"] + ".json")) for r in rows], "blockedReceipt": q.ref(self.root / "blocked.json")}
        q.emit(self.root / "admission.json", self.admission)
        self.admission_ref = q.ref(self.root / "admission.json")
    def tearDown(self): self.tmp.cleanup()
    def run_reconcile(self, **kw):
        return q.reconcile(self.admission, self.admission_ref, self.root / "reconciliation", api=self.api,
            observer=kw.pop("observer", lambda pid: copy.deepcopy(self.controller)), release_controller=kw.pop("release_controller", True), reason="Root reviewed failed wr01 cleanup", **kw)
    def test_three_original_objects_proven_before_only_controller_termination(self):
        original = self.api.terminate_controller
        def terminate(handle):
            for name in ("terminal-proof.json", "release-controller-intent.json"):
                self.assertTrue((self.root / "reconciliation" / name).exists())
            self.assertEqual(self.api.closed, [])
            original(handle)
        self.api.terminate_controller = terminate
        result = self.run_reconcile()
        self.assertEqual(self.api.opened, [43076]); self.assertEqual(self.api.terminated, [9000])
        self.assertTrue(result["reconciliationCompleted"]); self.assertFalse(result["normalLifecyclePassed"])
        self.assertFalse(result["qualificationPassed"]); self.assertFalse(result["nativePidReopened"])
        self.assertEqual({r["exitCode"] for r in result["exits"]}, {0, 1})
        self.assertCountEqual(self.api.closed, [1, 2, 3, 9000])
    def test_missing_original_handle_never_opens_native_pid_or_releases_owner(self):
        self.api.candidates.pop()
        with self.assertRaisesRegex(RuntimeError, "absent"): self.run_reconcile()
        self.assertEqual(self.api.opened, [43076]); self.assertEqual(self.api.terminated, [])
    def prior_native(self, **changes):
        row = self.rows[2]
        proof = dict(row['handle'], completed=True, processExitVerified=True, handleClosed=True,
                     exitCode=0, deadlineOrCancellationObserved=False, unknownReasons=[])
        proof.update(changes)
        path = self.root / 'native-exit.json'
        q.emit(path, {'identity': row['identity'], 'role': 'native', 'proof': proof})
        self.admission['exitReceipts'] = [q.ref(path)]
        self.api.candidates.pop()
    def test_prior_closed_native_proof_and_two_duplicated_terminal_objects(self):
        self.prior_native()
        result = self.run_reconcile()
        self.assertEqual(len(result['exits']), 3)
        self.assertIn('priorOriginalHandleExitReceipt', result['exits'][0])
        self.assertCountEqual(self.api.closed, [1, 2, 9000])
        self.assertEqual(self.api.opened, [43076])
    def test_corrupt_prior_birth_never_releases_controller(self):
        self.prior_native(originalFileTimeTicks=-1)
        with self.assertRaisesRegex(RuntimeError, 'incomplete or mismatched'):
            self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_unclosed_prior_native_does_not_substitute_for_retained_handle(self):
        self.prior_native(handleClosed=False)
        with self.assertRaisesRegex(RuntimeError, 'incomplete or mismatched'):
            self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_prior_native_does_not_authorize_release_of_live_worker(self):
        self.prior_native()
        self.api.live.add(2)
        with self.assertRaisesRegex(RuntimeError, 'not signaled'):
            self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_original_process_not_terminal_refuses_controller_release(self):
        self.api.live.add(3)
        with self.assertRaisesRegex(RuntimeError, "not signaled"): self.run_reconcile()
        self.assertEqual(self.api.terminated, []); self.assertIn(9000, self.api.live)
    def test_reused_pid_birth_refused(self):
        self.api.infos[3]["originalFileTimeTicks"] += 1
        with self.assertRaisesRegex(RuntimeError, "different original birth"): self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_wrong_image_refused(self):
        self.api.infos[3]["imagePath"] += ".foreign"
        with self.assertRaisesRegex(RuntimeError, "different original birth/image"): self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_terminal_missing_fresh_image_uses_exact_startup_image_binding(self):
        self.api.infos[3].update(imagePath=None, imageQueryError=31)
        result = self.run_reconcile()
        native = next(row for row in result["exits"] if row["role"] == "native")
        self.assertFalse(native["freshImageQuerySucceeded"]); self.assertTrue(native["imageBoundAtStartup"])
        self.assertEqual(native["startupVerifiedImagePath"], self.rows[2]["handle"]["imagePath"])
        self.assertEqual(native["originalProcessObject"]["imageQueryError"], 31)
    def test_live_missing_image_never_uses_startup_fallback(self):
        self.api.infos[3].update(imagePath=None, imageQueryError=31); self.api.live.add(3)
        with self.assertRaisesRegex(RuntimeError, "Image unavailable on nonterminal"): self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_terminal_missing_image_with_wrong_birth_is_refused(self):
        self.api.infos[3].update(imagePath=None, imageQueryError=31); self.api.infos[3]["originalFileTimeTicks"] += 1
        with self.assertRaisesRegex(RuntimeError, "different original birth"): self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_live_controller_fresh_image_is_always_required(self):
        self.api.infos[9000].update(imagePath=None, imageQueryError=31)
        with self.assertRaisesRegex(RuntimeError, "controller handle differs"): self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_controller_full_command_change_refused_before_enumeration(self):
        changed = copy.deepcopy(self.controller); changed["command"].append("foreign")
        with self.assertRaisesRegex(RuntimeError, "full command"): self.run_reconcile(observer=lambda _: changed)
        self.assertEqual(self.api.terminated, []); self.assertEqual(self.api.closed, [9000])
    def test_changed_start_receipt_refused_before_open(self):
        (self.root / "native.json").write_text("{}")
        with self.assertRaisesRegex(RuntimeError, "Bound artifact changed"): self.run_reconcile()
        self.assertEqual(self.api.opened, [])
    def test_duplicated_handles_and_nonprocess_candidates_cannot_expand_inventory(self):
        self.api.candidates += [{"sourceHandle": 4}, {"sourceHandle": 5}]
        self.api.infos[4] = copy.deepcopy(self.api.infos[3])
        result = self.run_reconcile()
        self.assertEqual(len(result["exits"]), 3); self.assertCountEqual(self.api.closed, [4, 5, 1, 2, 3, 9000])
    def test_false_release_flag_refuses_before_open(self):
        with self.assertRaisesRegex(RuntimeError, "Explicit"): self.run_reconcile(release_controller=False)
        self.assertEqual(self.api.opened, [])
    def test_durable_terminal_proof_failure_prevents_termination(self):
        original = q.emit
        def emit(path, value):
            if Path(path).name == "terminal-proof.json": raise OSError("disk unavailable")
            return original(path, value)
        with patch.object(q, "emit", side_effect=emit), self.assertRaises(OSError): self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_cancellation_before_termination_never_releases_owner(self):
        self.api.handles = lambda _: (_ for _ in ()).throw(KeyboardInterrupt())
        with self.assertRaises(KeyboardInterrupt): self.run_reconcile()
        self.assertEqual(self.api.terminated, [])
    def test_cancellation_after_explicit_release_retains_handle_until_terminal(self):
        original = self.api.wait; waits = 0
        def wait(handle, milliseconds=0):
            nonlocal waits
            if milliseconds:
                waits += 1; self.assertNotIn(9000, self.api.closed)
                if waits == 1: raise KeyboardInterrupt()
                if waits == 2: raise SystemExit()
            return original(handle, milliseconds)
        self.api.wait = wait
        result = self.run_reconcile(); self.assertTrue(result["reconciliationCompleted"]); self.assertEqual(waits, 3)


if __name__ == "__main__": unittest.main()
