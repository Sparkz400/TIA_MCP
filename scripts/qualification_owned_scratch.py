"""Evidence-preserving local qualification lifetime; no attach or process kill API."""
import copy
import hashlib
import json
import pathlib
import traceback

from block_test_process import OwnedTiaProcessWatcher
from block_test_runtime import NativeAudit, utc_now
from engineering_preservation import PAYLOAD_HASH_POLICY, native_payload_hash
from run_block_tests import binary_manifest, detach_owned, wait_owned_tia_exit
from smoke_mcp import Client
from typed_graphical_transport import artifact_bytes


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


class QualificationAudit(NativeAudit):
    ALLOWED_TOOLS = frozenset({"tia_start", "tia_create_scratch", "tia_session_status", "tia_detach",
        "change_prepare", "change_apply", "change_status", "change_restore", "software_inspect",
        "block_read", "type_read", "tag_table_read", "artifact_read", "project_open"})
    OPTIONAL_TOOLS = frozenset()


class OwnedScratch:
    """Each reopen owns a fresh host and TIA handle; only recorded paths reopen."""
    def __init__(self, host, worker, artifacts, report, persist, *, timeout=300,
                 client_factory=Client, watcher_factory=OwnedTiaProcessWatcher,
                 manifest_factory=binary_manifest, extra_tools=()):
        self.host, self.worker = pathlib.Path(host).resolve(), pathlib.Path(worker).resolve()
        self.artifacts, self.report, self.persist = pathlib.Path(artifacts).resolve(), report, persist
        self.timeout, self.client_factory, self.watcher_factory = timeout, client_factory, watcher_factory
        self.manifest_factory = manifest_factory
        self.binaries = manifest_factory(self.host, self.worker)
        self.report["binaries"] = self.binaries
        self.extra_tools = frozenset(extra_tools)
        self.known_paths = set()
        self.client = self.audit = self.watcher = self.current_project = None
        self.session = None
        self.poisoned = False

    def client_diagnostics(self):
        """Retain all currently observed stderr; EOF, rather than truncation, marks completeness."""
        process = getattr(self.client, "process", None)
        reader = getattr(self.client, "error_reader", None)
        eof = not reader.is_alive() if reader is not None else None
        return {"observedAtUtc": utc_now(), "hostProcessId": getattr(process, "pid", None),
            "hostExitCode": getattr(process, "returncode", None),
            "stderr": list(getattr(self.client, "errors", [])),
            "stderrEofObserved": eof}

    def start(self):
        require(self.client is None and not self.poisoned, "Previous owned session did not finish cleanly.")
        require(self.manifest_factory(self.host, self.worker) == self.binaries, "Selected binaries changed.")
        self.session = {"startedAtUtc": utc_now(), "calls": [], "startAttempted": False}
        self.report.setdefault("sessions", []).append(self.session)
        self.persist()
        try:
            self.client = self.client_factory(self.host, self.timeout, ["--worker", str(self.worker), "--artifacts", str(self.artifacts)])
        except Exception as error:
            self.session["clientConstructionFailure"] = {"type": type(error).__name__, "message": str(error),
                "traceback": traceback.format_exc(), "hostProcessId": None,
                "limitation": "The factory did not return a client; host identity and stderr are unavailable."}
            self.poisoned = True
            self.persist()
            raise
        self.session["hostProcessId"] = getattr(getattr(self.client, "process", None), "pid", None)
        self.session["clientDiagnosticsAtStart"] = self.client_diagnostics()
        self.persist()
        self.audit = QualificationAudit(self.client, self.session["calls"], self.persist)
        self.audit.ALLOWED_TOOLS = self.audit.ALLOWED_TOOLS | self.extra_tools
        self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "owned-engineering-qualification", "version": "1.0.0"}})
        self.client.notify("notifications/initialized")
        self.session["startAttempted"] = True
        started = self.audit("tia_start")
        require(started.get("attached") is True and started.get("ownsPortalProcess") is True
                and type(started.get("processId")) is int, "Startup did not identify an exact owned TIA process.")
        self.session["processId"] = started["processId"]
        self.watcher = self.watcher_factory(started["processId"])
        self.session["handleCapture"] = copy.deepcopy(self.watcher.evidence)
        require(started.get("projects") == [], "Fresh owned TIA unexpectedly contained a project.")
        self.persist()
        return started

    def remember(self, path):
        selected = pathlib.Path(path).resolve()
        require(selected.suffix.lower() == ".ap21" and selected.is_relative_to(self.artifacts),
                "Owned project receipt escaped the exact run artifact directory.")
        self.known_paths.add(str(selected))
        self.current_project = str(selected)

    def guard(self):
        require(self.current_project in self.known_paths and self.audit is not None, "No owned scratch is selected.")
        status = self.audit("tia_session_status")
        require(status.get("attached") is True and status.get("ownsPortalProcess") is True
                and status.get("processId") == self.session["processId"], "Owned TIA attachment changed.")
        projects = status.get("projects", [])
        paths = {str(pathlib.Path(item["path"]).resolve()) for item in projects}
        require(paths == {self.current_project}, "The current project differs from the owned scratch receipt.")

    def call(self, name, arguments=None):
        arguments = arguments or {}
        require(self.audit is not None and not self.poisoned, "No usable owned qualification session.")
        require(name not in {"tia_start", "tia_detach"}, "Use the tracked owned session start/close methods.")
        if name == "tia_create_scratch":
            require(self.current_project is None, "A new scratch requires an empty owned session.")
        if name in {"change_prepare", "change_apply", "change_restore"} or name.startswith("library_change_"):
            self.guard()
        if name == "project_open":
            require(self.current_project is None, "A reopen requires a fresh owned session.")
            path = str(pathlib.Path(arguments.get("path", "")).resolve())
            require(path in self.known_paths and set(arguments) == {"path"}, "Reopen requires a recorded owned scratch path.")
        result = self.audit(name, arguments)
        if name == "tia_create_scratch":
            require(result.get("completed") is True and result.get("compile", {}).get("errorCount") == 0, "Scratch creation/compile failed.")
            self.remember(result["project"]["path"])
        elif name == "change_restore":
            require(result.get("state") == "restored", "Checkpoint restore did not complete.")
            self.remember(result["recoveredProject"]["path"])
        elif name == "project_open":
            self.current_project = str(pathlib.Path(arguments["path"]).resolve())
            self.guard()
        return result

    def reopen(self, path):
        require(str(pathlib.Path(path).resolve()) in self.known_paths, "Project was not created or recovered by this run.")
        self.close()
        self.start()
        return self.call("project_open", {"path": path})

    def close(self):
        if self.client is None:
            return
        cleanup = {"completed": False, "errors": []}
        self.session["cleanup"] = cleanup
        errors = cleanup["errors"]
        if self.session["startAttempted"]:
            try:
                cleanup["detach"] = detach_owned(self.audit)
            except Exception as error:
                errors.append({"action": "detach", "message": str(error)})
        try:
            shutdown = self.client.close()
            cleanup["launcherShutdown"] = shutdown
            require(shutdown.get("hostExitCode") == 0 and shutdown.get("forcedHostStop") is False,
                    "Host shutdown was not clean.")
            receipts = shutdown.get("workerExits", [])
            require(not self.session["startAttempted"] or receipts, "Native worker exit receipt is absent.")
            require(all(row.get("processExitVerified") is True and row.get("exitCode") == 0 for row in receipts),
                    "Native worker shutdown was not verified.")
        except Exception as error:
            errors.append({"action": "host_close", "message": str(error)})
            cleanup["launcherCloseException"] = {"type": type(error).__name__, "message": str(error),
                "traceback": traceback.format_exc()}
        finally:
            # Keep diagnostics even when close raises or its receipt fails acceptance.
            # An active stderr reader remains explicitly incomplete; never widen shutdown bounds here.
            try:
                cleanup["clientDiagnostics"] = self.client_diagnostics()
            except Exception as error:
                errors.append({"action": "client_diagnostics", "message": str(error)})
                cleanup["clientDiagnosticsFailure"] = {"type": type(error).__name__, "message": str(error),
                    "traceback": traceback.format_exc()}
        try:
            wait_owned_tia_exit(self.watcher, self.session["startAttempted"], cleanup, 60000)
        except Exception as error:
            errors.append({"action": "owned_process_exit", "message": str(error)})
        try:
            require(self.manifest_factory(self.host, self.worker) == self.binaries, "Selected binaries changed during qualification.")
            cleanup["binaryHashesVerified"] = True
        except Exception as error:
            errors.append({"action": "binary_hashes", "message": str(error)})
        cleanup["completed"] = not errors
        self.session["completedAtUtc"] = utc_now()
        self.client = self.audit = self.watcher = self.current_project = None
        self.poisoned = bool(errors)
        self.persist()
        require(not errors, "Owned qualification cleanup failed: " + ", ".join(row["action"] for row in errors))


def complete_inventory(call):
    result = call("software_inspect", {"maxItems": 2000})
    coverage = result.get("completeness", {})
    require(coverage.get("scopeComplete") is True and coverage.get("truncated") is False,
            "Complete block, tag and type identity coverage is required.")
    return result


def native_hash(xml):
    # ElementTree's default parser drops XML comments and processing instructions.
    # Use the shared strict native comparator, including QName value bindings.
    return native_payload_hash(xml)


def snapshot(call, destination):
    """All block/type/table XML, keyed by exact paths; duplicate names stay distinct."""
    destination = pathlib.Path(destination)
    destination.mkdir(parents=True, exist_ok=False)
    inventory = complete_inventory(call)
    result = {"inventory": inventory, "objects": {}, "payloadHashPolicy": PAYLOAD_HASH_POLICY}
    for collection, kind, tool, selector in (("blocks", "block", "block_read", "blockPath"),
            ("types", "type", "type_read", "typePath"), ("tagTables", "tagTable", "tag_table_read", "tagTablePath")):
        for row in sorted(inventory[collection], key=lambda item: item["path"]):
            key = kind + ":" + row["path"]
            require(key not in result["objects"], "Duplicate exact identity in native inventory.")
            if kind == "block" and row.get("isKnowHowProtected") is True and "/systemGroups/" in row["path"]:
                # TIA copies protected system FBs such as R_TRIG into System blocks; they cannot be exported, so their identity is kept.
                identity = json.dumps({k: row.get(k) for k in ("path", "name", "number", "kind", "programmingLanguage", "isConsistent")}, sort_keys=True).encode()
                result["objects"][key] = {"kind": "protectedSystemBlock", "path": row["path"], "name": row["name"],
                    "sha256": hashlib.sha256(identity).hexdigest(), "payloadSha256": hashlib.sha256(identity).hexdigest()}
                continue
            exported = call(tool, {selector: row["path"]})
            raw = artifact_bytes(call, exported["artifact"])
            xml = raw.decode("utf-8-sig")
            file = destination / (str(len(result["objects"])).zfill(4) + ".xml")
            file.write_bytes(raw)
            result["objects"][key] = {"kind": kind, "path": row["path"], "name": row["name"],
                "file": str(file), "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest(), "payloadSha256": native_hash(xml)}
    (destination / "manifest.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


def assert_snapshot(expected, actual, except_paths=()):
    require(set(expected["objects"]) == set(actual["objects"]), "Native object inventory changed.")
    changed = [key for key, row in expected["objects"].items() if row["path"] not in except_paths
               and row["payloadSha256"] != actual["objects"][key]["payloadSha256"]]
    require(not changed, "Complete native XML changed: " + ", ".join(changed))
