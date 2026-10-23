"""Root-only sequential category packet with retained terminal lifetimes; default is offline prepare-only.

Imports the pinned original coordinator for source/gate validation and decoding.
It does not modify the shared store-window module used by other frozen packets.
"""
import argparse
import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys

import qualify_translation_category_probe as base
from component_exact_lifetime import worker_receipt_bound, hold_unknown_startup
from component_window_lifetime import scan_component_reservations
from created_process_lifetime import CreatedProcess, await_created_terminal
from retained_native_lifecycle import RetainedNativeTree, close_host_without_kill

require, save, ref, sha, utc, load = base.require, base.save, base.ref, base.sha, base.utc, base.load

# Exact direct-child collector used by production-host-20261016/startup-02.
# Historical packets keep their frozen lifecycle04 source and original outcomes.
REVIEWED_LIFECYCLE_SHA256 = "dff10491a60f497c7f0b96eac0d19f966390b0ea1269b5fb9d4b9f9c47a8c891"


def verify_lifecycle_source(scripts):
    require(sha(scripts / "retained_native_lifecycle.py") == REVIEWED_LIFECYCLE_SHA256,
            "Exact reviewed direct-child lifecycle source required; unknown startup must hold cleanup")


def verify_resources_before_start(prepared, receipt_prefix):
    """Refuse unresolved source ownership before constructing any Host/controller.

    This short preflight window is released; run_helper independently reacquires
    and holds the same stores through the exact collector's terminal proof.
    """
    roots = [Path(p) for p in prepared["resourceRoots"] if Path(p) != Path(prepared["root"])]
    prefix = Path(receipt_prefix)
    prefix.parent.mkdir(parents=True, exist_ok=True)
    with base.StoreReadWindow(roots, Path(prepared["projectBefore"]["path"]), prefix) as held:
        for root in roots:
            scan_component_reservations(root, held.keys)
    return {"completed": True, "nativeInvoked": False, "admissionHeldForDispatch": False,
            "intent": ref(str(prefix) + "-intent.json"), "release": ref(str(prefix) + "-close.json")}


class ExactCoordinator(base.Coordinator):
    def __init__(self, args, prepared):
        super().__init__(args, prepared)
        self.tree = self.native_row = None; self.helper_terminal = False
        self.report["lifetimePolicy"] = "exact-retained-terminal-no-kill-v4"
        sources = []
        for row in prepared["exactCoordinatorSources"]:
            source = Path(row["path"]); destination = self.root / "coordinator-source" / source.name
            shutil.copyfile(source, destination); sources.append(ref(destination))
        save(self.root / "exact-source-manifest.json", sources)

    def status(self, opened):
        value = self.call("tia_session_status")
        require(value["ownsPortalProcess"] is True and value["processId"] == self.native_row["identity"]["pid"], "Owned native identity changed")
        projects = value["projects"]
        require(projects == [] if not opened else len(projects) == 1 and base.safe_path(projects[0]["path"]) == Path(self.prepared["projectBefore"]["path"]), "Selected saved project changed")
        watcher = self.native_row["watcher"]
        require(watcher.api.wait(watcher._handle, 0) == 258, "Owned native process exited before status")
        return value

    def run_helper(self):
        for package in ("hostPackage", "workerPackage", "helperPackage"): base.verify_package(self.prepared[package])
        require(sha(self.prepared["template"]["path"]) == self.prepared["template"]["sha256"], "Prepared template changed")
        with base.StoreReadWindow([Path(p) for p in self.prepared["resourceRoots"]], Path(self.prepared["projectBefore"]["path"]), self.root / "resource-window") as held:
            for root in self.prepared["resourceRoots"]: scan_component_reservations(root, held.keys)
            watcher = self.native_row["watcher"]; info = self.native_row["identity"]
            require(watcher.api.wait(watcher._handle, 0) == 258, "Native lifetime ended before collector admission")
            admission = copy.deepcopy(load(self.prepared["template"]["path"]))
            admission.update(requestStatus="admitted-current-sequential-read-window", processId=info["pid"], processBirthEpoch=info["created"],
                processStartedAtUtc=watcher.evidence["processStartedAtUtc"], projectPath=self.prepared["projectBefore"]["path"], plcName=self.prepared["sourceControl"]["plcName"],
                outputDirectory=str(self.root / "native"), artifactRoot=str(self.root), resourceAdmission=copy.deepcopy(held.evidence),
                recoveryGate=ref(self.root / "preflight.json"), hostIdentity=self.tree.anchor["identity"], workerBinary=self.prepared["worker"], admittedAtUtc=utc())
            path = self.root / "native-admission.json"; save(path, admission)
            command = [self.prepared["helper"]["path"], base.NATIVE_MODE, str(path)]
            save(self.root / "helper-dispatch-intent.json", {"command": command, "admission": ref(path), "atUtc": utc(), "automaticReplay": False})
            reports = []
            def report(packet):
                reports.append(packet); save(self.root / f"helper-unknown-{len(reports):03}.json", packet)
                print("BLOCKED_HELPER_CLEANUP " + json.dumps(packet), flush=True)
            with (self.root / "helper.stdout.raw").open("xb") as stdout, (self.root / "helper.stderr.raw").open("xb") as stderr:
                self.helper_process = subprocess.Popen(command, cwd=base.REPO, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
                lifetime = None; capture_error = None
                try:
                    lifetime = CreatedProcess(self.helper_process, self.prepared["helper"]["path"])
                    save(self.root / "helper-process-start.json", lifetime.proof)
                except BaseException as error:
                    capture_error = type(error).__name__ + ": " + str(error)
                    try: report({"failure": capture_error, "storeLocksRetained": True, "originalCreateProcessHandleRetained": True, "automaticTermination": False})
                    except BaseException: pass
                finally:
                    try: self.helper_proof = await_created_terminal(self.helper_process, lifetime, self.args.timeout, report)
                    finally: self.helper_terminal = self.helper_process.returncode is not None
                save(self.root / "helper-process-exit.json", self.helper_proof)
                require(capture_error is None and self.helper_proof.get("completed") and self.helper_proof.get("exitCode") == 0, "Collector identity/outcome/lifetime is unqualified")
            packet = load(self.root / "native/result.json")
            require(packet["processId"] == admission["processId"] and base.safe_path(packet["projectPath"]) == Path(admission["projectPath"]) and packet["beforeModified"] is False and packet["afterModified"] is False
                    and packet["readOnly"] is True and packet["saved"] is False and packet["completed"] is True and packet["onlineConnected"] is False, "Saved collector readback/identity differs")
            self.report["collectorOutcome"] = packet["translations"]["outcome"]
            self.report["savedNativeReadback"] = {"beforeModified": False, "afterModified": False, "saveInvoked": False, "evidence": ref(self.root / "native/result.json")}

    def execute(self):
        try:
            require(all(sha(row["path"]) == row["sha256"] for row in self.prepared["exactCoordinatorSources"]), "Admitted exact coordinator source changed")
            for package in ("hostPackage", "workerPackage", "helperPackage"): base.verify_package(self.prepared[package])
            require(sha(self.prepared["projectBefore"]["path"]) == self.prepared["projectBefore"]["sha256"], "Source project changed since admission")
            self.client = base.Client.__new__(base.Client); constructor_error = None
            try: self.client.__init__(Path(self.prepared["host"]["path"]), self.args.timeout, ["--worker", self.prepared["worker"]["path"], "--artifacts", str(self.root)])
            except BaseException as error: constructor_error = error
            if getattr(self.client, "process", None) is None: hold_unknown_startup("Translation Client constructor retained no exact Popen")
            self.tree = RetainedNativeTree.__new__(RetainedNativeTree); self.tracker = self.tree
            try: self.tree.__init__(self.client.process, self.prepared["host"]["path"], self.prepared["worker"]["path"], self.root / "process-tree")
            except BaseException: self.tree.unknown = True; raise
            if constructor_error is not None:
                self.tree.unknown = True; self.tree.await_capture_complete(); raise constructor_error
            initialized = self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "translation-category-exact", "version": "2"}})
            save(self.root / "initialize-raw.json", initialized); self.client.notify("notifications/initialized")
            before = self.call("tia_discover"); self.report["preexistingProcesses"] = before["processes"]
            self.tree.one("worker"); self.tree.begin_start()
            started = self.call("tia_start"); require(started["ownsPortalProcess"] is True and started["projects"] == [], "Fresh empty owned startup required")
            self.native_row = self.tree.accept_start(started["processId"]); self.status(False)
            opened = self.call("project_open", {"path": self.prepared["projectBefore"]["path"]})
            require(opened["opened"] is True and opened["saved"] is False and base.safe_path(opened["project"]["path"]) == Path(self.prepared["projectBefore"]["path"]), "Exact saved source was not opened")
            self.status(True); self.run_helper(); self.status(True)
            self.tree.await_capture_complete()
            detached = self.call("tia_detach"); require(detached.get("disposedOwnedPortalConnection") is True and detached.get("attached") is False, "Owned detach failed")
            self.detached = True; proof = self.tree.close_record(self.native_row); save(self.root / "owned-native-exit.json", proof)
            require(proof["completed"] and proof["exitCode"] == 0 and not proof["deadlineOrCancellationObserved"], "Native exact terminal outcome unqualified")
            after = self.call("tia_discover")
            require(sorted(before["processes"], key=lambda p: p["processId"]) == sorted(after["processes"], key=lambda p: p["processId"]), "Preexisting native processes changed")
            self.report["preexistingPreserved"] = True
        except BaseException as error:
            self.report["failure"] = {"type": type(error).__name__, "message": str(error)}
        finally:
            self.cleanup()
        if not self.report.get("failure") and self.report["cleanup"].get("normalLifecyclePassed"):
            try:
                after = ref(self.prepared["projectBefore"]["path"]); require(after["sha256"] == self.prepared["projectBefore"]["sha256"], "Read-only source project changed")
                self.report["projectAfter"] = after; self.decode_after_close(); self.report["completed"] = True
            except BaseException as error: self.report["failure"] = {"type": type(error).__name__, "message": str(error)}
        self.report["finishedAtUtc"] = utc(); save(self.root / "result.json", self.report)
        return 0 if self.report["completed"] else 1

    def cleanup(self):
        cleanup = self.report["cleanup"]
        require(self.helper_process is None or self.helper_terminal, "Helper terminal hold must finish inside the store window before Host close")
        if self.client is None: return
        if self.native_row is not None and not self.detached and not self.request_failed:
            try:
                self.tree.await_capture_complete(); detached = self.call("tia_detach")
                require(detached.get("disposedOwnedPortalConnection") is True and detached.get("attached") is False, "Owned detach failed")
                self.detached = True
            except BaseException as error: cleanup["detachFailure"] = type(error).__name__ + ": " + str(error)
        try: close = close_host_without_kill(self.client, self.tree)
        except BaseException:
            self.tree.close_all()
            raise
        cleanup.update(hostClose=close, helperProof=self.helper_proof, workerShutdownReceiptBoundToBirth=worker_receipt_bound(close, self.tree))
        exits = close["tree"]["exits"]
        cleanup["allExactExitsVerified"] = close["tree"]["completed"] and bool(self.helper_proof) and self.helper_proof.get("completed") is True
        cleanup["normalLifecyclePassed"] = (cleanup["allExactExitsVerified"] and close["hostExitCode"] == 0 and close["closeInterrupted"] is None
            and cleanup["workerShutdownReceiptBoundToBirth"] and not cleanup.get("detachFailure") and len([e for e in exits if e["role"] == "native"]) == 1)
        save(self.root / "cleanup.json", cleanup)


def main():
    # Reuse exactly the old packet's CLI and preflight; only its execution class changes.
    original, preflight = base.Coordinator, base.preflight
    def exact_preflight(args):
        result = preflight(args)
        scripts = Path(__file__).resolve().parent
        files = sorted({Path(m.__file__).resolve() for m in sys.modules.values() if getattr(m, "__file__", None)
                        and Path(m.__file__).resolve().parent == scripts and Path(m.__file__).suffix == ".py"})
        verify_lifecycle_source(scripts)
        result["exactCoordinatorSources"] = [ref(path) for path in files]
        prefix = str(args.preflight_output or (Path(args.root).parent / (Path(args.root).name + "-preflight"))) + "-resources"
        result["resourcePreflight"] = verify_resources_before_start(result, prefix)
        return result
    try:
        base.Coordinator, base.preflight = ExactCoordinator, exact_preflight
        return base.main()
    finally:
        base.Coordinator, base.preflight = original, preflight


if __name__ == "__main__": raise SystemExit(main())
