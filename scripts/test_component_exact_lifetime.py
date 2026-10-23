"""Controller tests with fake processes only; no Host, worker or native launch."""
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest

from component_exact_lifetime import ComponentOwnedScratch, worker_receipt_bound


class FakeTree:
    events = None
    def __init__(self, process, host, worker, directory):
        self.events.append("tree-captured-before-initialize")
        self.unknown = False; self.pending_start = None
        def row(pid, role):
            return {"identity": {"pid": pid, "parentPid": 11 if role == "native" else 10, "created": 0}, "role": role,
                    "watcher": SimpleNamespace(evidence={"processId": pid, "handleCaptured": True}), "proof": None}
        self.anchor, self.worker, self.native = row(10, "host"), row(11, "worker"), row(12, "native")
    def one(self, role): return self.worker
    def await_capture_complete(self): self.events.append("startup-capture-complete-before-detach")
    def begin_start(self): self.events.append("armed-before-start"); self.pending_start = True
    def accept_start(self, pid):
        assert pid == 12
        self.pending_start = None; return self.native
    def snapshot(self): return [self.anchor, self.worker, self.native]
    def close_record(self, row):
        self.events.append("same-native-handle-terminal")
        return {"completed": True, "exitCode": 0, "deadlineOrCancellationObserved": False}
    def close_all(self): self.events.append("same-tree-terminal"); return {"completed": True}


class FakeClient:
    def __init__(self, events):
        self.events = events; self.process = SimpleNamespace(pid=10, returncode=0); self.errors = []
        self.error_reader = SimpleNamespace(is_alive=lambda: False)
    def request(self, name, arguments):
        self.events.append(name if name != "tools/call" else arguments["name"])
        if name == "initialize": return {}
        tool = arguments["name"]
        result = {"processes": []} if tool == "tia_discover" else {"attached": True, "ownsPortalProcess": True, "processId": 12, "projects": []} if tool == "tia_start" else {"attached": False, "disposedOwnedPortalConnection": True}
        return {"structuredContent": result}
    def notify(self, *args): pass
    def close(self): raise AssertionError("Automatic-kill Client.close must never be used")


def shutdown(tree):
    return {"hostExitCode": 0, "forcedHostStop": False, "stderrEofObserved": True, "closeInterrupted": None, "tree": tree.close_all(),
            "workerExits": [{"processId": 11, "startedAtUtc": "1970-01-01T00:00:00Z", "processExitVerified": True, "exitCode": 0, "forcedTermination": False}]}


class Checks(unittest.TestCase):
    def session(self, directory, closer=None):
        events = []; FakeTree.events = events
        client = FakeClient(events)
        def close(client, tree): events.append("host-eof-no-kill"); return shutdown(tree)
        return ComponentOwnedScratch("host.exe", "worker.exe", directory, {}, lambda: None,
            lifecycle_directory=Path(directory) / "lifetimes", client_factory=lambda *args: client,
            manifest_factory=lambda *args: {"same": True}, tree_factory=FakeTree, closer=closer or close), events

    def test_capture_precedes_initialize_and_start_and_terminal_precedes_host_eof(self):
        with tempfile.TemporaryDirectory() as directory:
            session, events = self.session(directory); session.start(); session.close()
            self.assertLess(events.index("tree-captured-before-initialize"), events.index("initialize"))
            self.assertLess(events.index("armed-before-start"), events.index("tia_start"))
            self.assertLess(events.index("same-native-handle-terminal"), events.index("host-eof-no-kill"))
            self.assertTrue(session.report["sessions"][0]["cleanup"]["completed"])

    def test_unknown_request_closes_only_by_eof_and_holds_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            session, events = self.session(directory); session.start()
            session.session["calls"].append({"tool": "change_apply", "status": "failed"})
            session.close()
            self.assertNotIn("tia_detach", events); self.assertIn("same-tree-terminal", events)

    def test_wrong_worker_birth_rejects_cleanup_and_followup_start(self):
        def bad_close(client, tree):
            value = shutdown(tree); value["workerExits"][0]["startedAtUtc"] = "1970-01-02T00:00:00Z"; return value
        with tempfile.TemporaryDirectory() as directory:
            session, events = self.session(directory, bad_close); session.start()
            with self.assertRaises(RuntimeError): session.close()
            with self.assertRaises(RuntimeError): session.start()
            self.assertGreaterEqual(events.count("same-tree-terminal"), 2)

    def test_cleanup_cancellation_returns_to_same_retained_tree_before_unwind(self):
        def interrupted(client, tree): raise KeyboardInterrupt()
        with tempfile.TemporaryDirectory() as directory:
            session, events = self.session(directory, interrupted); session.start()
            with self.assertRaises(RuntimeError): session.close()
            self.assertIn("same-tree-terminal", events); self.assertTrue(session.poisoned)

    def test_worker_receipt_requires_one_exact_birth_bound_terminal(self):
        FakeTree.events = []; tree = FakeTree(None, None, None, None)
        value = shutdown(tree); self.assertTrue(worker_receipt_bound(value, tree))
        value["workerExits"][0]["forcedTermination"] = True; self.assertFalse(worker_receipt_bound(value, tree))
        value["workerExits"] = []; self.assertFalse(worker_receipt_bound(value, tree))


if __name__ == "__main__": unittest.main()
