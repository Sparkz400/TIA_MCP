"""Qualify V21 library source handling: read-only and locked sources, newer/corrupt formats, companion directories and mixed packages."""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import os
from pathlib import Path
import shutil
import stat
import zipfile

from library_native_harness import Harness, require

GENERIC_READ, OPEN_EXISTING = 0x80000000, 3


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree(directory):
    """Content and read-only state of every file: what 'source untouched' means here."""
    return sorted((p.relative_to(directory).as_posix(), sha256(p), not os.access(p, os.W_OK))
                  for p in directory.rglob("*") if p.is_file())


def make_read_only(path):
    for p in [path] if path.is_file() else [x for x in path.rglob("*") if x.is_file()]:
        p.chmod(stat.S_IREAD)


class ExclusiveLock:
    """Hold a Windows handle with no sharing, as another process holding the library would."""
    def __init__(self, path):
        create = ctypes.windll.kernel32.CreateFileW
        create.restype = wintypes.HANDLE
        self.handle = create(str(path), GENERIC_READ, 0, None, OPEN_EXISTING, 0, None)
        require(self.handle not in (None, wintypes.HANDLE(-1).value), "Could not take the exclusive lock")

    def __enter__(self):
        return self

    def __exit__(self, *_):
        ctypes.windll.kernel32.CloseHandle(self.handle)


def body(h):
    inputs = h.directory / "inputs"
    inputs.mkdir()
    h.owned_portal("formats")
    created = h.apply("library_create", {"schemaVersion": 1, "name": "FormatQualification"})["journal"]["result"]["library"]
    archive = Path(h.archive(created["libraryId"])["path"])
    marker = Path(created["path"])
    require(marker.suffix.lower() == ".al21", "Created library is not an .al21 directory")
    h.tool("library_close", {"libraryId": created["libraryId"]})
    libraries_before = len(h.tool("library_list")["libraries"])

    def opened_writable(path, label):
        """Open a copy writable, persist a folder change into the working copy, and leave the source untouched."""
        source = path if path.suffix.lower() == ".zal21" else path.parent
        before = tree(source) if source.is_dir() else [(source.name, sha256(source), not os.access(source, os.W_OK))]

        def check():
            library = h.tool("library_open", {"request": {"path": str(path), "expectedSha256": sha256(path), "writableWorkingCopy": True}})
            require(library["originalSourceModified"] is False and library["sourceSnapshotOwned"] is True, "Open did not use an owned snapshot")
            h.apply("library_organize", {"schemaVersion": 1, "source": h.source_ref(library["libraryId"]), "folderKind": "types",
                    "action": "create", "name": "WritableProof"})
            h.tool("library_close", {"libraryId": library["libraryId"]})
            after = tree(source) if source.is_dir() else [(source.name, sha256(source), not os.access(source, os.W_OK))]
            require(before == after, "The read-only source changed")
            return {"source": str(source), "files": len(before), "readOnly": all(x[2] for x in before)}
        return h.stage(label, check)

    def refused_open(path, code, label, expected_sha=None):
        def check():
            error = h.call_error("library_open", {"request": {"path": str(path), "expectedSha256": expected_sha or sha256(path)}})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(len(h.tool("library_list")["libraries"]) == libraries_before, "A refused open registered a library")
            return error
        return h.stage(label, check)

    # Read-only archive and read-only directory library.
    read_only_archive = inputs / "ro" / archive.name
    read_only_archive.parent.mkdir()
    shutil.copy2(archive, read_only_archive)
    make_read_only(read_only_archive)
    opened_writable(read_only_archive, "read-only archive yields a writable working copy")
    read_only_directory = inputs / "rodir"
    shutil.copytree(marker.parent, read_only_directory)
    make_read_only(read_only_directory)
    opened_writable(read_only_directory / marker.name, "read-only directory yields a writable working copy")

    # A source another process holds exclusively is refused before any native call; it stays intact.
    locked_directory = inputs / "locked"
    shutil.copytree(marker.parent, locked_directory)
    locked = locked_directory / marker.name
    expected = sha256(locked)
    before = tree(locked_directory)

    def locked_open():
        with ExclusiveLock(locked):
            error = h.call_error("library_open", {"request": {"path": str(locked), "expectedSha256": expected}})
        # The shared admission scope hashes the source before the library adapter; either refusal names the lock.
        require(error.get("code") == "SourceLocked" or "being used by another process" in (error.get("message") or ""),
                "Locked source was not refused for its lock: %s" % error)
        require(len(h.tool("library_list")["libraries"]) == libraries_before, "A locked source was registered")
        require(tree(locked_directory) == before, "The locked source changed")
        return error
    h.stage("refuse an exclusively locked source", locked_open)

    # Newer and corrupt formats.
    newer = inputs / "newer" / (archive.stem + ".zal22")
    newer.parent.mkdir()
    shutil.copy2(archive, newer)
    refused_open(newer, "LibraryUpgradeRequired", "refuse opening a newer .zal22")
    h.refuse("refuse upgrading a newer .zal22", "library_upgrade",
             {"schemaVersion": 1, "path": str(newer), "expectedSha256": sha256(newer), "name": "NewerUpgrade"}, "LibraryVersionUnsupported", project=False)
    corrupt = inputs / "corrupt" / "Corrupt.zal21"
    corrupt.parent.mkdir()
    corrupt.write_bytes(hashlib.sha256(b"not a TIA archive").digest() * 64)

    def corrupt_open():
        error = h.call_error("library_open", {"request": {"path": str(corrupt), "expectedSha256": sha256(corrupt)}})
        require(len(h.tool("library_list")["libraries"]) == libraries_before, "A corrupt archive was registered")
        return error
    h.stage("refuse a corrupt .zal21", corrupt_open)

    # A lone marker is not a complete directory library.
    lone = inputs / "lone" / marker.name
    lone.parent.mkdir()
    shutil.copy2(marker, lone)
    refused_open(lone, "PackageInvalid", "refuse a lone .al21 marker")

    # Mixed package: archive, complete directory, lone marker, loose source and documentation.
    mixed = inputs / "mixed.zip"
    with zipfile.ZipFile(mixed, "w", zipfile.ZIP_DEFLATED) as package:
        package.write(archive, "archive/" + archive.name)
        for path in marker.parent.rglob("*"):
            if path.is_file():
                package.write(path, "directory/" + path.relative_to(marker.parent).as_posix())
        package.write(marker, "lone/" + marker.name)
        package.writestr("source/Loose.scl", 'FUNCTION "Loose" : Void\nBEGIN\nEND_FUNCTION\n')
        package.writestr("README.md", "Mixed package fixture.\n")

    def mixed_package():
        staged = h.tool("library_package_stage", {"hostPath": str(mixed)})
        candidates = h.tool("library_package_inspect", {"packageId": staged["packageId"]})["items"]
        top = lambda c: next(p for p in c["relativePath"].split("/") if p in ("archive", "directory", "lone", "source"))  # Staged paths carry a managed prefix.
        observed = sorted((c["kind"], c.get("completeness"), top(c)) for c in candidates)
        require(observed == sorted([("globalLibraryArchive", "nativeVerificationRequired", "archive"),
                                    ("globalLibraryDirectory", "nativeVerificationRequired", "directory"),
                                    ("globalLibraryDirectory", "missingCompanionFiles", "lone"),
                                    ("sourceFile", "sourceImportRequired", "source")]), "Mixed package classification differs: %s" % observed)
        opened = []
        for candidate in candidates:
            if top(candidate) in ("archive", "directory"):
                library = h.tool("library_open", {"request": {"packageId": staged["packageId"], "candidateId": candidate["candidateId"]}})
                opened.append(library["name"])
                h.tool("library_close", {"libraryId": library["libraryId"]})
        require(len(opened) == 2, "Both complete library candidates must open natively")
        return {"candidates": candidates, "opened": opened}
    h.stage("mixed package classification and native open", mixed_package)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "fq_", "V21 library source formats, read-only and locked sources, companion directories and mixed packages", [
        "Inputs are libraries created in this run; no V14-V20 library exists on this PC, so upgrade into V21 is not exercised.",
        "The locked source is simulated by an exclusive Windows handle on the marker, not by a second TIA instance.",
        "No vendor package; classification does not establish vendor compatibility."], []).run(body)
