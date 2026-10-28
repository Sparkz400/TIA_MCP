"""Sequential owned watch qualification through actual stdio MCP Host processes."""
import argparse
import copy
import hashlib
import json
import shutil
import subprocess
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import psutil
from block_test_process import OwnedTiaProcessWatcher
from qualify_worker_startup_exact import identity
from smoke_mcp import Client
import verify_watch_expansion as oracle


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read_comment_input(path, mode):
    expected = {"comment-de": "de-DE", "comment-en": "en-GB", "comment-nl": "nl-NL"}
    if mode not in expected:
        raise ValueError("A comment input is valid only for one exact culture profile")
    value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or set(value) != {"culture", "text"} or value["culture"] != expected[mode] or not isinstance(value["text"], str) or not 0 < len(value["text"]) <= 65536:
        raise ValueError("Comment input must contain the exact profile culture and bounded text")
    return value


class Run:
    def __init__(self, args):
        self.args, self.root = args, args.root.resolve()
        self.root.mkdir(parents=True, exist_ok=False)
        self.clients, self.lifetimes, self.client, self.tool_calls = [], [], None, []
        self.comment_edit, comment_ref = None, None
        if getattr(args, "comment_input", None):
            self.comment_edit = read_comment_input(args.comment_input, args.mode)
            retained = self.root / "comment-input.json"
            shutil.copyfile(args.comment_input, retained)
            assert read_comment_input(retained, args.mode) == self.comment_edit
            comment_ref = {"path": str(retained), "sha256": digest(retained)}
        self.host_files = [{"path": str(p.resolve()), "sha256": digest(p)} for p in sorted(args.host.resolve().parent.iterdir())
                           if p.is_file() and p.suffix.lower() in (".exe", ".dll", ".json", ".config")]
        self.native_cleanup_helper = None
        if args.native_cleanup_helper:
            helper = args.native_cleanup_helper.resolve()
            helper_sources = helper.parent / "source"
            self.native_cleanup_helper = {"path": str(helper), "sha256": digest(helper), "method": "TerminateProcess(exactOwnedHandle)", "sources": [
                {"path": str(helper_sources / name), "sha256": digest(helper_sources / name)}
                for name in ("Program.cs", "OwnedPortalCleanup.csproj")]}
        sources = self.root / "controller-source"
        sources.mkdir()
        manifest = []
        for name in ("qualify_watch_mcp.py", "qualify_worker_startup_exact.py", "smoke_mcp.py", "block_test_process.py", "verify_watch_expansion.py", "verify_watch_workflow.py"):
            target = sources / name
            shutil.copyfile(Path(__file__).parent / name, target)
            manifest.append({"path": str(target), "sha256": digest(target)})
        self.save("controller-source-manifest", manifest)
        self.save("admission", {"mode": args.mode, "root": str(self.root), "worker": str(args.worker.resolve()),
            "workerSha256": digest(args.worker), "host": str(args.host.resolve()), "hostSha256": digest(args.host),
            "hostFiles": self.host_files,
            "commentInput": comment_ref,
            "nativeCleanupHelper": self.native_cleanup_helper,
            "nativeSequential": True, "actualMcpHost": True, "automaticReplay": False,
            "effects": "Fresh owned offline scratch only; one reviewed watch edit and explicit archive recovery."})

    def save(self, name, data):
        path = self.root / (name + ".json")
        with path.open("x", encoding="utf-8") as stream:
            json.dump(data, stream, indent=2, ensure_ascii=False)
        return data

    def start_host(self, point):
        assert self.host_files and all(digest(p["path"]) == p["sha256"] for p in self.host_files), "Frozen Host package changed"
        client = Client(self.args.host.resolve(), 240, ["--worker", str(self.args.worker.resolve()),
            "--artifacts", str(self.root), "--component-qualification", "--watch-qualification", point])
        self.client = client
        entry = {"identity": identity(psutil.Process(client.process.pid)), "point": point, "processes": []}
        self.clients.append(entry)
        self.save(f"host-{len(self.clients):02}-start", entry)
        stop = threading.Event()
        tracked = {}
        def monitor():
            while not stop.wait(.2):
                try:
                    parent = psutil.Process(client.process.pid)
                    if parent.create_time() != entry["identity"]["created"]:
                        return
                    for proc in parent.children(recursive=True):
                        key = proc.pid, proc.create_time()
                        if key in tracked: continue
                        info = identity(proc)
                        if info["name"] in ("TiaMcp.Openness.V21.exe", "Siemens.Automation.Portal.exe"):
                            watcher = OwnedTiaProcessWatcher(proc.pid, query_exit_code=True)
                            watcher.evidence["basis"] = "Retained handle captured while observing this exact Host descendant PID and creation time; never reopened for exit proof."
                            tracked[key] = (info, watcher)
                            entry["processes"].append(info)
                            (self.root / f"host-{len(self.clients):02}-processes.json").write_text(json.dumps(entry["processes"], indent=2), encoding="utf-8")
                except (psutil.NoSuchProcess, psutil.AccessDenied): pass
        thread = threading.Thread(target=monitor, daemon=True)
        thread.start()
        self.monitor = stop, thread, tracked
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "watch-native-matrix", "version": "1"}})
        client.notify("notifications/initialized")
        return client.process.pid

    def call(self, name, arguments=None, label=None):
        print(name, flush=True)
        response = self.client.request("tools/call", {"name": name, "arguments": arguments or {}})
        # An MCP server may report an unhandled tool exception as plain text.
        # Retain the exact reply before decoding; a malformed/error body must
        # not erase the only response or bypass owned failure reconciliation.
        self.save("call-" + str(time.time_ns()) + "-" + name, response)
        # Unit tests build runners with __new__, so the list may not exist yet.
        self.__dict__.setdefault("tool_calls", []).append({"name": name, "isError": bool(response.get("isError"))})
        packet = response.get("structuredContent")
        if packet is None:
            content = "\n".join(c["text"] for c in response.get("content", []) if c["type"] == "text")
            try:
                packet = json.loads(content)
            except json.JSONDecodeError:
                packet = {"error": {"code": "McpToolError" if response.get("isError") else "InvalidMcpPayload", "message": content}}
                raise ToolError(name, packet)
        if response.get("isError"):
            raise ToolError(name, packet)
        if label: self.save(label, packet)
        return packet

    def start_tia(self):
        result = self.call("tia_start")
        assert result["ownsPortalProcess"] and not result["projects"]
        info = identity(psutil.Process(result["processId"]))
        self.native = OwnedTiaProcessWatcher(result["processId"], query_exit_code=True)
        self.save("native-" + str(result["processId"]) + "-start", info)

    def close_tia(self):
        self.call("tia_detach")
        proof = self.native.wait_for_exit(timeout_ms=45000)
        self.lifetimes.append(proof)
        self.save("native-" + str(proof["processId"]) + "-exit", proof)
        self.native = None

    def reopen(self, path):
        self.close_tia()
        self.start_tia()
        self.call("project_open", {"path": path})

    def close_host(self, expected_loss=False):
        entry = self.clients[-1]
        cleanup_error = None
        if expected_loss and self.args.mode in oracle.NATIVE_POINTS and self.native_cleanup_helper:
            marker = json.loads((self.root / "interruption-marker.json").read_text())
            candidates = [(info, watcher) for info, watcher in self.monitor[2].values() if info["pid"] == marker["processId"]]
            assert len(candidates) == 1, "Interrupted native identity is not captured"
            info, watcher = candidates[0]
            # Do not consume or close the original monitor handle before cleanup.
            if watcher.api.wait(watcher._handle, 1) == 258:
                helper = self.native_cleanup_helper
                assert digest(helper["path"]) == helper["sha256"]
                try:
                    result = subprocess.run([helper["path"], str(self.root)], capture_output=True, text=True, timeout=60)
                    self.save("coordinator-native-close-call", {"returncode": result.returncode, "stdout": result.stdout, "stderr": result.stderr})
                    # The original process can exit between the non-consuming wait and helper lookup.
                    if result.returncode != 0 and not (not (self.root / "coordinator-native-close-intent.json").exists() and watcher.api.wait(watcher._handle, 1) == 0):
                        cleanup_error = "Coordinator native cleanup failed"
                except Exception as error:
                    cleanup_error = str(error)
        if expected_loss and self.args.mode in oracle.NATIVE_POINTS:
            workers = [(info, watcher) for info, watcher in self.monitor[2].values() if Path(info["exe"]).resolve() == self.args.worker.resolve()]
            assert len(workers) == 1, "Fault worker original handle missing"
            started = time.time()
            worker_wait = workers[0][1].api.wait(workers[0][1]._handle, 60000)
            self.save("fault-worker-preclose-wait", {"identity": workers[0][0], "sameOriginalMonitorHandle": True, "startedAt": started, "endedAt": time.time(), "waitResult": worker_wait, "timeoutMs": 60000})
            if worker_wait != 0: cleanup_error = cleanup_error or "Fault worker did not exit before Host close"
        close_requested_at = time.time()
        close = self.client.close()
        stop, thread, tracked = self.monitor
        stop.set(); thread.join(2)
        exits = []
        for info, watcher in tracked.values():
            try: proof = watcher.wait_for_exit(timeout_ms=45000)
            except Exception as error: proof = getattr(error, "evidence", {"error": str(error)})
            exits.append({"identity": info, "proof": proof})
        self.save(f"host-{len(self.clients):02}-close", {"identity": entry["identity"], "close": close, "exits": exits, "logs": self.client.errors, "expectedLoss": expected_loss, "closeRequestedAt": close_requested_at, "closedAt": time.time(), "cleanupError": cleanup_error})
        self.client = None
        if getattr(self, "native", None):
            proof = self.native.wait_for_exit(timeout_ms=1000)
            self.lifetimes.append(proof); self.native = None
        assert all(e["proof"].get("completed") for e in exits), "An exact owned process is still alive"
        assert cleanup_error is None, cleanup_error
        workers = [e for e in exits if Path(e["identity"]["exe"]).resolve() == self.args.worker.resolve()]
        assert len(workers) == 1, "Exact admitted worker lifecycle missing"
        expected_worker_code = 73 if expected_loss and self.args.mode in oracle.NATIVE_POINTS else 0
        assert workers[0]["proof"].get("exitCode") == expected_worker_code, "Unexpected worker terminal code"
        assert not close["forcedHostStop"], "Host close required forced termination"
        if expected_loss:
            assert close["hostExitCode"] == (74 if self.args.mode in oracle.HOST_POINTS else 1), "Unexpected interrupted Host terminal code"
        if not expected_loss: assert close["hostExitCode"] == 0

    def refuse(self, name, arguments, label, **fields):
        try: self.call(name, arguments)
        except ToolError as error:
            assert error.packet["error"]["code"] == "RecoveryRequired", error.packet
            return self.save(label, {"code": "RecoveryRequired", **fields})
        raise AssertionError("Replay unexpectedly accepted")

    def recover(self, operation):
        oid, original = operation["id"], operation["projectPath"]
        self.refuse("watch_table_apply", {"operationId": oid, "reviewedPlanSha256": operation["digest"]},
            "replay-refusal", operationId=oid, nativeDispatchInvoked=False)
        competitor = self.call("qualification_watch_compete", {"operationId": oid}, "competing-owner-refusal")
        assert competitor["code"] == "ResourceBusy" and competitor["nativeDispatchInvoked"] is False
        record = self.call("engineering_operation_status", {"operationId": oid})
        self.start_tia()
        if record["recoveryDestinations"]:
            self.refuse("watch_table_recover", {"operationId": oid}, "retrieve-replay-refusal", nativeRetrieveInvoked=False)
            dest = Path(record["recoveryDestinations"][0]["recoveryLocation"])
            projects = list(dest.rglob("*.ap21")) if dest.exists() else []
            if len(projects) == 1:
                restored = str(projects[0])
                self.call("project_open", {"path": restored})
                self.call("watch_table_reconcile_recovery", {"operationId": oid, "role": "restored"}, "located")
                self.reopen(restored)
                self.call("watch_table_reconcile_recovery", {"operationId": oid, "role": "restored"}, "restored-verified")
                self.reopen(original)
                self.call("watch_table_reconcile_recovery", {"operationId": oid, "role": "original"}, "reconciled")
            else:
                assert not projects, "Ambiguous recovery output"
                self.call("project_open", {"path": original})
                disposition = "retainedPartial" if dest.exists() and any(dest.iterdir()) else "verifiedAbsent"
                self.call("watch_table_reconcile_interrupted", {"operationId": oid, "destinationDisposition": disposition}, "reconciled")
        elif not record["checkpoints"]:
            self.call("project_open", {"path": original})
            self.call("watch_table_reconcile_interrupted", {"operationId": oid}, "reconciled")
        else:
            self.restore(oid, original)
        self.close_tia()
        self.save("matrix-reconciliation-result", {"completed": True, "operationId": oid,
            "hostProcessId": self.client.process.pid, "automaticReplay": False})

    def restore(self, oid, original):
        result = self.call("watch_table_recover", {"operationId": oid}, "recovered")
        restored = result["recovered"]["project"]["path"]
        self.reopen(restored)
        self.call("watch_table_reconcile_recovery", {"operationId": oid, "role": "restored"}, "restored-verified")
        self.reopen(original)
        self.call("watch_table_reconcile_recovery", {"operationId": oid, "role": "original"}, "reconciled")
        return restored

    def execute(self):
        pid = self.start_host(self.args.mode if self.args.mode in oracle.NATIVE_POINTS | oracle.HOST_POINTS else "none")
        admission = json.loads((self.root / "admission.json").read_text())
        admission["hostProcessId"] = pid
        # Admission is finalized before the first native request.
        (self.root / "admission.json").write_text(json.dumps(admission, indent=2), encoding="utf-8")
        self.start_tia()
        scratch = self.call("tia_create_scratch", label="scratch")
        original = scratch["project"]["path"]
        seed = self.root / "seed.xml"
        shutil.copyfile(self.args.seed, seed)
        if self.args.mode.startswith("comment-") or self.args.mode in ("clone", "delete", "reorder"):
            tree = ET.parse(seed); objects = tree.getroot().find("SW.WatchAndForceTables.PlcWatchTable/ObjectList")
            first = next(e for e in objects if isinstance(e.tag, str))
            for ordinal in (1, 2):
                row = copy.deepcopy(first)
                for node in row.iter():
                    if "ID" in node.attrib: node.set("ID", format(int(node.get("ID"), 16) + 5 * ordinal, "X"))
                row.find("AttributeList/ModifyValue").text = "TRUE" if ordinal == 1 else "FALSE"
                for item in row.findall(".//MultilingualTextItem/AttributeList"):
                    item.find("Text").text = f"Owned row {ordinal} / {item.findtext('Culture')} {{0}}"
                objects.append(row)
            tree.write(seed, encoding="utf-8", xml_declaration=True)
        self.call("qualification_watch_fixture_seed", {"inputPath": str(seed), "inputSha256": digest(seed)}, "seeded")
        self.reopen(original)
        self.call("watch_table_read", {"tableName": "MilestoneWatch"}, "durable-baseline")
        edit = {"tableName": "MilestoneWatch", "ordinal": 0, "modifyValue": "TRUE", "idempotencyKey": "exact-" + self.args.mode}
        if self.args.mode.startswith("comment-"):
            culture = {"comment-de": "de-DE", "comment-en": "en-GB", "comment-nl": "nl-NL"}[self.args.mode]
            comment = self.comment_edit or {"culture": culture, "text": {"de-DE": "Geprüfte Freigabe {0}\nÄnderung – Ω", "en-GB": "Reviewed enable {0}\nChange – Ω", "nl-NL": "Gecontroleerde vrijgave {0}\nWijziging – Ω"}[culture]}
            edit.update(editKind="comment", **comment)
        if self.args.mode == "clone": edit.update(editKind="clone", insertAt=3)
        if self.args.mode == "delete": edit.update(editKind="delete", ordinal=1)
        if self.args.mode == "reorder": edit.update(editKind="reorder", order=[2, 0, 1])
        prepared = self.call("watch_table_prepare", edit, "review")
        reviewed = oracle.reviewed_candidate(self.root)
        assert reviewed["delta"]["unknownPayloadPreserved"]
        self.save("independent-review", {**reviewed, "approved": True})
        (self.root / "approved-digest").write_text(reviewed["reviewedPlanSha256"], encoding="utf-8")
        operation = self.save("operation", {"id": prepared["operation"]["id"], "projectPath": original, "digest": reviewed["reviewedPlanSha256"]})
        oid = operation["id"]
        try:
            self.call("watch_table_apply", {"operationId": oid, "reviewedPlanSha256": operation["digest"]}, "applied")
            self.reopen(original)
            self.call("watch_table_verify_reopen", {"operationId": oid}, "verified")
            self.close_tia(); self.start_tia()
            restored = self.restore(oid, original)
            self.close_tia()
            self.save("result", {"completed": True, "projectPath": original, "recoveryPath": restored, "id": oid, "nativeOnline": False})
        except Exception as error:
            self.save("failure", {"error": str(error), "automaticReplay": False})
            if self.args.mode not in oracle.NATIVE_POINTS | oracle.HOST_POINTS or not (self.root / "interruption-marker.json").exists(): raise
            self.close_host(expected_loss=True)
            self.start_host("none")
            self.recover(operation)
        self.close_host()
        verdict = oracle.verify(self.root)
        self.save("independent-verification", verdict)
        assert verdict["completed"], verdict["issues"]
        intervention = (self.root / "coordinator-native-close.json").exists()
        self.save("exact-cleanup", {"completed": True, "lifetimes": self.lifetimes, "hostCount": len(self.clients), "explicitNativeTermination": intervention, "coordinatorInterventionRequired": intervention, "normalLifecyclePassed": self.args.mode not in oracle.NATIVE_POINTS | oracle.HOST_POINTS})
        return verdict

    def report(self, result):
        # Written only after the whole execute() passed; scripts/curate_evidence.py reads it.
        # Expected refusals stay listed as rejected calls.
        return self.save("verification", {"completed": result["completed"], "mode": self.args.mode, "toolCalls": self.tool_calls})


class ToolError(RuntimeError):
    def __init__(self, name, packet):
        self.packet = packet
        super().__init__(name + ": " + json.dumps(packet, ensure_ascii=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("host", "worker", "root", "seed"): parser.add_argument("--" + key, type=Path, required=True)
    parser.add_argument("--mode", required=True)
    parser.add_argument("--native-cleanup-helper", type=Path)
    parser.add_argument("--comment-input", type=Path, help="Exact UTF-8 culture/text JSON retained before native startup; default complex input remains unchanged")
    args = parser.parse_args()
    run = Run(args)
    try:
        result = run.execute()
        run.report(result)
        print(json.dumps({"completed": result["completed"], "mode": args.mode, "root": str(run.root)}), flush=True)
    except Exception as error:
        run.save("runner-failure", {"error": str(error), "automaticReplay": False})
        if run.client is not None:
            try: run.close_host(expected_loss=args.mode in oracle.NATIVE_POINTS | oracle.HOST_POINTS and (run.root / "interruption-marker.json").exists())
            except Exception as cleanup_error: run.save("runner-cleanup-failure", {"error": str(cleanup_error)})
        raise


if __name__ == "__main__": main()
