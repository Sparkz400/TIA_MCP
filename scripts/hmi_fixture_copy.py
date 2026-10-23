"""File-only copies of closed, independently owned HMI fixture projects."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def safe_path(path):
    path = Path(path)
    require(path.is_absolute() and ".." not in path.parts, "Fixture copy paths must be exact absolute paths")
    for candidate in (path, *path.parents):
        try:
            metadata = candidate.lstat()
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(metadata.st_mode) and not getattr(metadata, "st_file_attributes", 0) & 0x400,
                "Fixture copy rejects links/reparse points")
    return path


def project_tree(root):
    """Hash all regular files and retain empty directories; fail on read errors."""
    root = safe_path(root)
    require(root.is_dir(), "Closed fixture project directory is required")
    directories, files = [], []
    def enumeration_error(error):
        raise error
    for current, folders, names in os.walk(root, followlinks=False, onerror=enumeration_error):
        for name in sorted(folders):
            directory = safe_path(Path(current) / name)
            require(directory.is_dir(), "Fixture directory changed during enumeration")
            directories.append(directory.relative_to(root).as_posix())
        for name in sorted(names):
            path = safe_path(Path(current) / name)
            before = path.lstat()
            require(stat.S_ISREG(before.st_mode), "Fixture copy requires regular files")
            with path.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            after = path.lstat()
            require((before.st_size, before.st_mtime_ns, before.st_ino) == (after.st_size, after.st_mtime_ns, after.st_ino),
                    "Fixture source changed during manifest capture")
            files.append({"path": path.relative_to(root).as_posix(), "bytes": after.st_size, "sha256": digest})
    payload = {"directories": sorted(directories), "files": sorted(files, key=lambda row: row["path"])}
    return {**payload, "sha256": hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()).hexdigest()}


def copy_project(source, destination, receipt):
    source, destination = safe_path(source), safe_path(destination)
    require(source.suffix.lower() == ".ap21" and source.is_file() and source.stem.startswith("TiaMcpHmi_")
            and source.parent.name == source.stem, "Only the complete named seeder-owned project tree is eligible")
    return copy_closed_project(source, destination, receipt)


def copy_closed_project(source, destination, receipt):
    source, destination = safe_path(source), safe_path(destination)
    require(source.suffix.lower() == '.ap21' and source.is_file() and source.parent.name == source.stem,
            'An exact closed native project directory is required')
    require(not destination.exists() and destination.name == source.parent.name, "Inspection copy requires a fresh complete project directory")
    require(not destination.is_relative_to(source.parent) and not source.parent.is_relative_to(destination),
            "Inspection copy destination overlaps source project")
    receipt.update({"sourceProject": str(source), "sourceRoot": str(source.parent), "copyProject": str(destination / source.name),
                    "copyRoot": str(destination), "copyVerified": False, "sourcePreserved": False})
    before = receipt["sourceBefore"] = project_tree(source.parent)
    destination.mkdir(parents=True, exist_ok=False)
    for relative in before["directories"]:
        safe_path(destination / relative).mkdir(parents=True, exist_ok=False)
    for row in before["files"]:
        target = safe_path(destination / row["path"])
        require(not target.exists(), "Inspection file destination unexpectedly exists")
        shutil.copyfile(safe_path(source.parent / row["path"]), target)
    receipt["copyBeforeOpen"] = project_tree(destination)
    receipt["sourceAfterCopy"] = project_tree(source.parent)
    require(before == receipt["copyBeforeOpen"] == receipt["sourceAfterCopy"], "Source/copy tree changed during fixture copy")
    receipt["copyVerified"] = True
    return destination / source.name


def verify_source_preserved(receipt):
    require("sourceBefore" in receipt, "Source baseline missing from inspection copy receipt")
    receipt["sourceAfterCleanup"] = project_tree(Path(receipt["sourceRoot"]))
    receipt["sourcePreserved"] = receipt["sourceBefore"] == receipt["sourceAfterCleanup"]
    require(receipt["sourcePreserved"], "Original HMI fixture project changed during copied-project inspection")
