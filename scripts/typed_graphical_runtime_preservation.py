"""Read-only receipts for one typed runtime diagnostic; simulator guards are unchanged."""
import copy
import hashlib
import os
import pathlib

from block_test_runtime import McpRuntimeBackend, utc_now, verify_memory_card


def file_inventory(folder):
    """Hash every file, including .info, without traversing reparse points or retrying reads."""
    root = pathlib.Path(folder)
    if not root.is_absolute():
        raise ValueError("An exact absolute owned directory is required.")
    for ancestor in (root, *root.parents):
        if ancestor.is_symlink() or getattr(ancestor.lstat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("Runtime evidence cannot traverse a reparse point.")
    if not root.is_dir():
        raise ValueError("The exact owned directory is absent.")
    result = {"root": str(root), "observedAtUtc": utc_now(), "files": {}, "errors": [], "complete": False}
    def enumeration_error(error):
        result["errors"].append({"path": str(error.filename), "type": type(error).__name__, "message": str(error)})
    for directory, folders, files in os.walk(root, followlinks=False, onerror=enumeration_error):
        for name in list(folders):
            path = pathlib.Path(directory) / name
            try:
                if path.is_symlink() or getattr(path.lstat(), "st_file_attributes", 0) & 0x400:
                    raise ValueError("Reparse directory rejected.")
            except Exception as error:
                folders.remove(name)
                result["errors"].append({"path": path.relative_to(root).as_posix(), "type": type(error).__name__, "message": str(error)})
        for name in sorted(files):
            path = pathlib.Path(directory) / name
            relative = path.relative_to(root).as_posix()
            try:
                before = path.lstat()
                if path.is_symlink() or getattr(before, "st_file_attributes", 0) & 0x400:
                    raise ValueError("Reparse file rejected.")
                digest = hashlib.sha256()
                with path.open("rb") as stream:
                    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                        digest.update(chunk)
                after = path.stat()
                if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                    raise ValueError("File changed during its single read.")
                result["files"][relative] = {"bytes": after.st_size, "sha256": digest.hexdigest()}
            except Exception as error:
                result["errors"].append({"path": relative, "type": type(error).__name__, "message": str(error)})
    result["complete"] = not result["errors"]
    return result


def inventory_difference(before, after):
    old, new = before["files"], after["files"]
    added, removed = sorted(set(new) - set(old)), sorted(set(old) - set(new))
    changed = sorted(path for path in set(old) & set(new) if old[path] != new[path])
    return {"preserved": before["complete"] and after["complete"] and not (added or removed or changed),
            "added": added, "removed": removed, "changed": changed,
            "addedInfoFiles": [path for path in added if path.lower().endswith(".info")]}


class RuntimePreservation:
    def __init__(self, project_path, allowed_root, report, persist, plc_readback):
        project = pathlib.Path(project_path).absolute()
        if (project.suffix.lower() != ".ap21" or not project.is_file()
                or not project.resolve().is_relative_to(pathlib.Path(allowed_root).resolve())):
            raise ValueError("Runtime project evidence must name the exact recorded owned project.")
        self.project, self.persist, self.plc_readback = project, persist, plc_readback
        self.download, self.card_before, self.project_active_before = None, None, None
        self.before_cleanup_attempted = self.finalized = False
        self.evidence = {"scope": "One fresh typed parameter-flow diagnostic; no historical discovery-origin or corpus qualification.",
            "readOnly": True, "simulatorGuardsChanged": False, "historicalZeroAddressOriginResolved": False,
            "observations": [], "preservationVerified": False}
        report["runtimePreservation"] = self.evidence
        self.project_before = file_inventory(project.parent)
        self.evidence["projectBeforeRuntimeReopen"] = self.project_before
        self.persist()
        if not self.project_before["complete"]:
            raise ValueError("A complete closed project/companion inventory is required before runtime admission.")

    def capture_active_project(self):
        if self.project_active_before is not None:
            raise RuntimeError("The active project baseline cannot be replaced or replayed.")
        self.project_active_before = file_inventory(self.project.parent)
        self.evidence["projectActiveBeforeRuntime"] = self.project_active_before
        self.evidence["closedToActiveTransition"] = inventory_difference(self.project_before, self.project_active_before)
        self.persist()
        if not self.project_active_before["complete"]:
            raise ValueError("A complete active project/companion inventory is required before runtime admission.")

    def capture_download(self, download, owned):
        if self.download is not None:
            raise RuntimeError("One runtime diagnostic cannot replace or replay its download receipt.")
        if (not owned or owned.get("owned") is not True or download.get("completed") is not True
                or type(owned.get("id")) is not int or type(download.get("instanceId")) is not int
                or download["instanceId"] != owned["id"]
                or download.get("instanceName") != owned.get("name")):
            raise ValueError("Memory-card observation lacks the exact newly owned simulator identity.")
        self.download = copy.deepcopy(download)
        self.evidence["generatedCardVerification"] = verify_memory_card(download)
        self.card_before = file_inventory(pathlib.Path(download["memoryCardPath"]))
        self.evidence["memoryCardBeforeRun"] = self.card_before
        self.persist()
        if not self.card_before["complete"]:
            raise ValueError("A complete generated-card inventory is required before provisioning/RUN.")

    def observe_files(self, phase, target, folder, before):
        row = {"phase": phase, "target": target, "status": "failed", "observedAtUtc": utc_now()}
        self.evidence["observations"].append(row)
        try:
            row["inventory"] = file_inventory(folder)
            row["difference"] = inventory_difference(before, row["inventory"])
            row["status"] = "passed" if row["difference"]["preserved"] else "failed"
        except Exception as error:
            row["error"] = {"type": type(error).__name__, "message": str(error)}
        self.persist()

    def observe_card(self, phase):
        if self.download is None or self.card_before is None:
            self.evidence["observations"].append({"phase": phase, "target": "memoryCard", "status": "not_observed",
                "reason": "No complete exact owned generated-card baseline was established."})
            self.persist()
            return
        self.observe_files(phase, "memoryCard", pathlib.Path(self.download["memoryCardPath"]), self.card_before)
        row = {"phase": phase, "target": "memoryCardGenerationManifest", "status": "failed"}
        self.evidence["observations"].append(row)
        try:
            row["verification"] = verify_memory_card(self.download)
            row["status"] = "passed"
        except Exception as error:
            row["error"] = {"type": type(error).__name__, "message": str(error)}
        self.persist()

    def before_cleanup(self):
        if self.before_cleanup_attempted:
            raise RuntimeError("Runtime preservation observations cannot be replayed.")
        self.before_cleanup_attempted = True
        # A failed card check must not suppress the independent project and PLC observations.
        self.observe_card("beforeOwnedRuntimeCleanup")
        if self.project_active_before is not None:
            self.observe_files("beforeOwnedRuntimeCleanup", "activeProjectAndAllCompanions", self.project.parent, self.project_active_before)
        else:
            self.evidence["observations"].append({"phase": "beforeOwnedRuntimeCleanup", "target": "activeProjectAndAllCompanions",
                "status": "not_observed", "reason": "No complete active project baseline was established before runtime admission."})
        row = {"phase": "beforeOwnedRuntimeCleanup", "target": "completePlcXml", "status": "failed"}
        self.evidence["observations"].append(row)
        try:
            row["readback"] = self.plc_readback()  # The caller verifies the exact owned TIA session first.
            row["status"] = "passed"
        except Exception as error:
            row["error"] = {"type": type(error).__name__, "message": str(error)}
        self.persist()

    def after_cleanup(self, cleanup_verified):
        if self.finalized:
            raise RuntimeError("Closed-file preservation observations cannot be replayed.")
        self.finalized = True
        verified = cleanup_verified is True
        self.evidence["ownedTiaCleanupVerified"] = verified
        phase = "afterVerifiedOwnedTiaCleanup" if verified else "afterUnverifiedOwnedTiaCleanupAttempt"
        self.observe_card(phase)
        self.observe_files(phase, "projectAndAllCompanions", self.project.parent, self.project_before)
        self.evidence["preservationVerified"] = (verified and self.before_cleanup_attempted and self.card_before is not None
            and self.project_active_before is not None
            and bool(self.evidence["observations"]) and all(row["status"] == "passed" for row in self.evidence["observations"]))
        self.persist()
        return self.evidence["preservationVerified"]


class ObservedRuntimeBackend(McpRuntimeBackend):
    def __init__(self, native, *args, preservation, **kwargs):
        self.preservation, self.close_attempted = preservation, False
        def observed(tool, arguments):
            result = native(tool, arguments)
            if tool == "simulation_download":
                preservation.capture_download(result, self.instance)
            return result
        super().__init__(observed, *args, **kwargs)

    def close(self):
        if self.close_attempted:
            raise RuntimeError("Owned runtime cleanup cannot be replayed.")
        self.close_attempted = True
        try:
            self.preservation.before_cleanup()
        finally:
            result = super().close()  # Always perform the unchanged exact owned cleanup, once.
        return result
