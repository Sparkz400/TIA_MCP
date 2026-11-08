"""Real vendor workload: the seven retained PAL/DMC distributions through intake, and every DMC global-library archive
(V14 SP1 to V19) upgraded into V21 in one owned Portal, with wall time, item counts and Portal memory per step.

Inputs are the downloads named in docs/verification/vendor-intake-review-20261025.json, checked byte for byte against
its hashes before anything runs. Intake stages and classifies each package through the Host; nothing is extracted by
the Host into a project. Each library archive is copied out of its ZIP to an own file (member names are never used as
paths) and upgraded through change kind library_upgrade, which upgrades an owned copy and never changes the source.
An archive V21 refuses is recorded with its typed error. Vendor executables are never run. Observations on this host,
not budgets.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import time
import zipfile

from library_native_harness import Harness, require
from qualify_cpu_memory_bytes import ENABLED
from qualify_library_vendor import all_items

TABLE = "Open Library"  # DMC's tag table master copy

ARCHIVE = re.compile(r"\.zal1[4-9]$")


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def portal_memory():
    """Working set of every TIA Portal process, in MiB; observation only."""
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "(Get-Process Siemens.Automation.Portal -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum"],
                         capture_output=True, text=True).stdout.strip()
    return round(int(out) / (1 << 20), 1) if out.isdigit() else None


def timed(operation):
    started = time.monotonic()
    result = operation()
    return result, round(time.monotonic() - started, 3)


def body(h):
    receipt = json.loads(Path(h.args.receipt).read_text(encoding="utf-8"))
    packages = []
    for package in receipt["packages"]:
        path = Path(h.args.vendor_dir) / package["file"]
        require(path.is_file() and path.stat().st_size == package["bytes"] and sha256(path) == package["sha256"],
                package["file"] + " differs from its receipt")
        packages.append((package, path))
    h.report["inputs"] = [{"file": p["file"], "bytes": p["bytes"], "sha256": p["sha256"], "url": p["url"]} for p, _ in packages]

    intake = {}
    for package, path in packages:
        def stage(path=path, package=package):
            staged, seconds = timed(lambda: h.tool("library_package_stage", {"hostPath": str(path)}))
            status = h.tool("library_package_status", {"packageId": staged["packageId"]})
            require(json.dumps(status).count(package["sha256"]) >= 1, package["file"] + " staged hash differs from its receipt")
            items = h.tool("library_package_inspect", {"packageId": staged["packageId"], "count": 200})["items"]
            kinds = {}
            for item in items:
                kinds[item["kind"]] = kinds.get(item["kind"], 0) + 1
            return {"packageId": staged["packageId"], "bytes": package["bytes"], "stageSeconds": seconds, "candidates": len(items), "kinds": kinds}
        intake[package["file"]] = h.stage("stage and classify " + package["file"], stage)
    h.report["intake"] = intake
    h.report["intakeTotals"] = {"bytes": sum(p["bytes"] for p, _ in packages), "seconds": round(sum(r["stageSeconds"] for r in intake.values()), 3)}

    # --compile: the upgrades run with an owned scratch CPU 1511 open, which then takes the named library's default code blocks.
    plc = h.owned_scratch("vendor workload") if h.args.compile else (h.owned_portal("vendor workload") or None)
    kept = None
    h.report["portalMemoryStartMiB"] = portal_memory()
    upgrades, nonlocal_kept = {}, {}
    for package, path in packages:
        if not package["file"].startswith("Siemens-Open-Library"):
            continue
        with zipfile.ZipFile(path) as archive:
            for member in sorted(n for n in archive.namelist() if ARCHIVE.search(n)):
                name = PurePosixPath(member).name
                require(name and ".." not in PurePosixPath(member).parts, "Unsafe archive member")
                if h.args.compile and name != h.args.compile:
                    continue  # the compile run upgrades only its own library
                target = h.directory / "dmc" / re.sub(r"\W+", "_", package["file"][:-4]) / re.sub(r"\W+", "_", name[:-6]) / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(member))

                def upgrade(target=target):
                    before = sha256(target)
                    asked = h.client.request("tools/call", {"name": "change_prepare", "arguments": {"kind": "library_upgrade",
                                             "payload": {"schemaVersion": 1, "path": str(target), "expectedSha256": before}}})
                    if asked.get("isError"):  # a typed refusal before any copy, such as LibraryPathTooLong
                        error = (asked.get("structuredContent") or {}).get("error") or {}
                        return {"archiveBytes": target.stat().st_size, "applySeconds": 0.0, "portalMemoryMiB": portal_memory(), "upgraded": False,
                                "refusedAtPrepare": True, "error": {k: error.get(k) for k in ("code", "message")}}
                    prepared = asked["structuredContent"]
                    result, seconds = timed(lambda: h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)}))
                    content = result.get("structuredContent") or {}
                    require(sha256(target) == before, "The source archive changed")
                    row = {"archiveBytes": target.stat().st_size, "applySeconds": seconds, "portalMemoryMiB": portal_memory()}
                    if result.get("isError"):
                        return {**row, "upgraded": False, "error": {k: (content.get("error") or {}).get(k) for k in ("code", "message")}}
                    library = content["journal"]["result"]["library"]
                    items, read_seconds = timed(lambda: all_items(h, library["libraryId"]))
                    versions = [x for x in items["items"] if x.get("kind") == "version"]
                    keep = h.args.compile == name
                    closed = None if keep else h.tool("library_close", {"libraryId": library["libraryId"]})
                    if keep:
                        nonlocal_kept.update(library=library["libraryId"], items=items["items"])
                    return {**row, "upgraded": True, "queryComplete": items["complete"], "querySeconds": read_seconds,
                            "types": sum(x.get("kind") == "type" for x in items["items"]), "versions": len(versions),
                            "masterCopies": sum(x.get("kind") == "masterCopy" for x in items["items"]),
                            "unavailable": sum(1 for x in items["items"] if x.get("unavailable")), "closed": bool(closed)}
                upgrades[package["file"] + "/" + name] = h.stage("upgrade " + package["file"] + " " + name, upgrade)
    h.report["upgrades"] = upgrades
    if h.args.compile:
        compile_vendor_blocks(h, plc, nonlocal_kept)
    done = [r for r in upgrades.values() if r["upgraded"]]  # in a compile run only the --compile archive
    require(done, "No vendor library archive upgraded")
    h.report["upgradeTotals"] = {"archives": len(upgrades), "upgraded": len(done), "archiveBytes": sum(r["archiveBytes"] for r in upgrades.values()),
                                 "applySeconds": round(sum(r["applySeconds"] for r in upgrades.values()), 3),
                                 "types": sum(r["types"] for r in done), "versions": sum(r["versions"] for r in done),
                                 "maxPortalMemoryMiB": max((r["portalMemoryMiB"] or 0) for r in upgrades.values())}


def compile_vendor_blocks(h, plc, kept):
    """DMC's prerequisites (system and clock memory bytes, its tag table), then every default code-block version of the kept
    library except --exclude matches, in batches of at most 100 per library_instantiate with compile; each batch's time is
    recorded and a refused batch is recorded with its error, not retried."""
    require(kept.get("library"), "The --compile library did not upgrade")
    cpu = plc.rsplit("/plc/", 1)[0]
    h.apply("hardware_cpu_memory_bytes", {"hardwarePath": cpu, "attributes": ENABLED})
    masters = [x for x in kept["items"] if x.get("kind") == "masterCopy" and x.get("name") == TABLE and not x.get("unavailable")]
    require(len(masters) == 1, "Expected one readable '%s' master copy" % TABLE)
    h.apply("library_instantiate", {"schemaVersion": 1, "source": h.source_ref(kept["library"]), "selection": [{"masterCopyPath": masters[0]["masterCopyPath"]}],
                                    "destinationComposition": "tagTables", "target": {"plcPath": plc}})
    excluded = lambda name: any(text.lower() in name.lower() for text in h.args.exclude or [])
    versions = [x for x in kept["items"] if x.get("kind") == "version" and x.get("isDefault") and "CodeBlockLibraryTypeVersion" in x.get("engineeringType", "")]
    chosen = [x for x in versions if not excluded(x["typeName"])]
    h.report["vendorCompile"] = {"defaultCodeBlocks": len(versions), "excluded": sorted(x["typeName"] for x in versions if excluded(x["typeName"])),
                                 "selected": len(chosen), "batches": []}
    for start in range(0, len(chosen), 100):
        installed = {x["versionGuid"] for x in all_items(h, h.project_library())["items"] if x.get("kind") == "version"}
        batch = [x for x in chosen[start:start + 100] if x["versionGuid"] not in installed]
        if not batch:
            continue
        def run(batch=batch):
            prepared = h.tool("change_prepare", {"kind": "library_instantiate", "payload": h.selected(kept["library"], batch, target={"plcPath": plc})})
            result, seconds = timed(lambda: h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)}))
            content = result.get("structuredContent") or {}
            row = {"versions": len(batch), "applySeconds": seconds, "portalMemoryMiB": portal_memory(), "applied": not result.get("isError")}
            if result.get("isError"):
                error = content.get("error") or {}
                row["error"] = {k: error.get(k) for k in ("code", "message")}
                row["compileErrors"] = [m for m in json.dumps(error.get("details") or {}).split('"message": ') if "rror" in m][:40]
            else:
                row["compile"] = {k: (content.get("compile") or {}).get(k) for k in ("errorCount", "warningCount")}
            return row
        h.report["vendorCompile"]["batches"].append(h.stage("instantiate and compile DMC default blocks %d-%d" % (start + 1, start + len(batch)), run))
    rows = h.report["vendorCompile"]["batches"]
    h.report["vendorCompile"]["compiledVersions"] = sum(r["versions"] for r in rows if r["applied"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--vendor-dir", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)  # the Host accepts 5..900
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--compile", help="File name of a DMC archive (such as 'Open Library V19 PLC.zal19') whose upgraded default code blocks are instantiated and compiled")
    parser.add_argument("--exclude", action="append", help="Skip default code blocks whose type name contains this text (repeatable)")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "vw_", "Real vendor workload: seven PAL/DMC packages through intake and every DMC library archive upgraded into V21", [
        "Observations on this host, not budgets: one owned TIA Portal V21 Update 3 on PCNico.",
        "Upgrade and listing only; vendor types are not instantiated or compiled here (DMC compilation has its own record).",
        "Vendor executables (DMC toolbox) are never run."], [args.receipt], scratch=bool(args.compile)).run(body)
