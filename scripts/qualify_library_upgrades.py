"""Qualify V14 SP1-V18 library upgrades into V21 on owned copies: archives, a companion directory and a read-only directory."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import stat
import zipfile

from library_native_harness import Harness, require
from qualify_library_vendor import all_items, extract

# PLC-only archives must upgrade; archives with HMI faceplates are recorded for lane D.
REQUIRED = ["Open Library V14 SP1 (S7-300).zal14", "Open Library V15 PLC.zal15", "Open Library V16 PLC.zal16",
            "Open Library V17 PLC.zal17", "Open Library V18 PLC.zal18"]
OBSERVED = ["Open Library V14 SP1.zal14", "Open Library V18 Comfort.zal18", "Open Library V18 Professional.zal18",
            "Open Library V18 Unified.zal18"]


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def tree(directory):
    return sorted((p.relative_to(directory).as_posix(), sha256(p.read_bytes()), not os.access(p, os.W_OK))
                  for p in directory.rglob("*") if p.is_file())


def unpack_directory(archive_bytes, destination):
    """A .zalXX is a ZIP of the native directory library; unpack it member by member without trusting member paths."""
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as inner:
        for info in inner.infolist():
            parts = PurePosixPath(info.filename).parts
            require(parts and ".." not in parts and not PurePosixPath(info.filename).is_absolute(), "Unsafe library member")
            if info.is_dir():
                continue
            target = destination.joinpath(*parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(inner.read(info))
    markers = [p for p in destination.iterdir() if p.suffix.lower().startswith(".al")]
    require(len(markers) == 1, "Unpacked library must have exactly one root marker")
    return markers[0]


def body(h):
    receipts = json.loads(Path(h.args.receipts).read_text(encoding="utf-8"))
    downloads = {}
    for package in receipts["packages"]:
        if package["id"].startswith("dmc-v"):
            path = next(Path(h.args.vendor_root).glob(package["id"] + "-dl/*.zip"))
            require(sha256(path.read_bytes()) == package["sha256"], package["id"] + " download differs from its receipt")
            downloads[package["id"]] = path
    h.report["receipts"] = [p for p in receipts["packages"] if p["id"] in downloads]

    def stage_release(key):
        staged = h.tool("library_package_stage", {"hostPath": str(downloads[key])})
        items = h.tool("library_package_inspect", {"packageId": staged["packageId"], "count": 200})["items"]
        return {"packageId": staged["packageId"], "kinds": sorted({(c["kind"], c.get("tiaMajorVersion")) for c in items})}
    for key in sorted(downloads):
        h.stage("stage and classify mixed package " + key, lambda k=key: stage_release(k))

    archives = {}
    for path in downloads.values():
        with zipfile.ZipFile(path) as package:
            for member in package.namelist():
                if PurePosixPath(member).name in REQUIRED + OBSERVED:
                    archives[PurePosixPath(member).name] = extract(package, member, h.directory / "inputs" / PurePosixPath(member).stem.replace(" ", "_") / PurePosixPath(member).name)
    require(sorted(archives) == sorted(REQUIRED + OBSERVED), "Expected DMC archives are missing")

    h.owned_portal("upgrades")

    def upgrade(path, expected_files=None):
        before = tree(path.parent) if path.suffix.lower().startswith(".al") else sha256(path.read_bytes())
        payload = {"schemaVersion": 1, "path": str(path), "expectedSha256": sha256(path.read_bytes())}
        prepared = h.tool("change_prepare", {"kind": "library_upgrade", "payload": payload})
        result = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        content = result.get("structuredContent") or {}
        after = tree(path.parent) if path.suffix.lower().startswith(".al") else sha256(path.read_bytes())
        require(before == after, "The older source changed during upgrade")
        if result.get("isError"):
            return {"upgraded": False, "error": content.get("error")}
        library = content["journal"]["result"]["library"]
        items = all_items(h, library["libraryId"])
        versions = [x for x in items["items"] if x.get("kind") == "version"]
        return {"upgraded": True, "libraryId": library["libraryId"], "complete": items["complete"], "versions": len(versions),
                "committed": sum(x["state"] == "Committed" for x in versions),
                "masterCopies": sum(x.get("kind") == "masterCopy" for x in items["items"]),
                "identities": sorted((x["typeName"], x["version"], x["versionGuid"]) for x in versions)}

    def required_upgrade(name, path):
        outcome = upgrade(path)
        require(outcome["upgraded"] and outcome["complete"] and outcome["versions"] > 0 and outcome["committed"] == outcome["versions"],
                "Upgrade or readback of %s is incomplete: %s" % (name, outcome.get("error")))
        archive = h.archive(outcome["libraryId"])
        reopened = h.tool("library_open", {"request": {"path": archive["path"], "expectedSha256": archive["sha256"]}})["libraryId"]
        identities = sorted((x["typeName"], x["version"], x["versionGuid"]) for x in all_items(h, reopened)["items"] if x.get("kind") == "version")
        require(identities == outcome["identities"], "Reopened V21 archive identities differ for " + name)
        for library in (reopened, outcome["libraryId"]):
            h.tool("library_close", {"libraryId": library})
        return {k: v for k, v in outcome.items() if k != "identities"} | {"identityCount": len(identities), "archiveSha256": archive["sha256"]}

    results = {}
    for name in REQUIRED:
        results[name] = h.stage("upgrade " + name, lambda n=name: required_upgrade(n, archives[n]))
    for name in OBSERVED:
        def observed(n=name):
            outcome = upgrade(archives[n])
            if outcome.get("upgraded"):
                h.tool("library_close", {"libraryId": outcome["libraryId"]})
            return {k: v for k, v in outcome.items() if k != "identities"}
        results[name] = h.stage("upgrade (observed) " + name, observed)

    # Complete native directory with companion files, and a read-only directory: both upgrade on owned copies.
    v17 = unpack_directory(archives["Open Library V17 PLC.zal17"].read_bytes(), h.directory / "inputs" / "dir-v17")
    directory_outcome = h.stage("upgrade companion directory .al17", lambda: upgrade(v17))
    require(directory_outcome["upgraded"] and directory_outcome["committed"] == directory_outcome["versions"] > 0, "Directory upgrade failed")
    h.tool("library_close", {"libraryId": directory_outcome["libraryId"]})
    v16 = unpack_directory(archives["Open Library V16 PLC.zal16"].read_bytes(), h.directory / "inputs" / "dir-v16-readonly")
    for p in v16.parent.rglob("*"):
        if p.is_file():
            p.chmod(stat.S_IREAD)
    readonly_outcome = h.stage("upgrade read-only directory .al16", lambda: upgrade(v16))
    require(readonly_outcome["upgraded"] and readonly_outcome["committed"] == readonly_outcome["versions"] > 0, "Read-only directory upgrade failed")
    h.tool("library_close", {"libraryId": readonly_outcome["libraryId"]})
    h.report["upgrades"] = {k: {x: v.get(x) for x in ("upgraded", "versions", "committed", "masterCopies", "error")} for k, v in results.items()}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--receipts", type=Path, required=True)
    parser.add_argument("--vendor-root", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "uq_", "V14 SP1-V18 DMC library upgrades into V21 on owned copies", [
        "No V20 library is available; V19 is covered by the vendor runner.",
        "HMI-bearing archives are upgraded and recorded only; their content behavior belongs to lane D.",
        "Upgrade preserves native identities into a reopened V21 archive; content equality against the older format cannot be read without that older TIA version."],
        [args.receipts]).run(body)
