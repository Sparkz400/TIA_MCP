"""Native 100-component planned import and a vendor-sized package through intake, staging, preview and apply.

Owned scratch only. Session 1 authors and releases --components flat types (half FBs, half PLC data types), publishes
them in one change to a new global library and archives it. The archive is wrapped in a package with the byte volume and
shape of the largest retained vendor distribution (DMC Open Library V17: about 150 MB, five library archives, a nested
toolbox ZIP with executables, documentation). Session 2 uploads that package in chunks, stages it, refuses its corrupt
decoy archive, opens the real one, refuses a 101-component selection, then prepares and applies all components in one
library_instantiate change. Session 3 applies the same components as separate single-component changes. Every change
records wall time, preview bytes, checkpoint archive bytes and TIA/worker memory. Observations on this host, not budgets.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import time
import zipfile

from library_native_harness import Harness, identity, require
from qualify_library_vendor import all_items

DMC_V17_BYTES = 149_771_810  # Siemens-Open-Library-V17-V5.0.zip, docs/verification/vendor-intake-review-20261025.json
CHUNK = 65_536  # Server MaxChunkBytes.
LIBRARY = "2_Library/Open Scale Library V21.zal21"
DECOY = "2_Library/Open Scale Library V21 Unified.zal21"


def component_names(count):
    require(count >= 2 and count % 2 == 0, "--components must be an even number of at least 2")
    return [("Imp_FB_%03d" % i, "blocks") for i in range(count // 2)] + [("Imp_UDT_%03d" % i, "types") for i in range(count // 2)]


def document(name, composition):
    if composition == "types":
        return f'TYPE "{name}"\nVERSION : 0.1\nSTRUCT\n    Active : Bool;\n    Count : DInt;\nEND_STRUCT;\nEND_TYPE\n', ".udt"
    return (f'FUNCTION_BLOCK "{name}"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\nVAR_INPUT\n    Enable : Bool;\nEND_VAR\n'
            f'VAR_OUTPUT\n    Active : Bool;\nEND_VAR\nBEGIN\n    #Active := #Enable;\nEND_FUNCTION_BLOCK\n'), ".scl"


def _random(archive, name, size):
    with archive.open(name, "w", force_zip64=True) as stream:
        while size > 0:
            block = os.urandom(min(size, 1 << 20))
            stream.write(block)
            size -= len(block)


def vendor_package(path, library_archive, total_bytes):
    """Write a DMC-V17-shaped ZIP around one real native archive; padding is incompressible and never a valid candidate.

    Proportions follow the reviewed V17 inventory: library archives dominate, a nested toolbox ZIP holds 25 executables
    and two notices, documentation PDFs and licenses complete it. Returns the layout with member sizes.
    """
    real = Path(library_archive).read_bytes()
    budget = total_bytes - len(real)
    require(budget >= 1 << 20, "--package-bytes must exceed the library archive by at least 1 MiB")
    toolbox, decoy, docs = budget * 4 // 100, budget * 35 // 100, budget * 3 // 100
    padding = budget - toolbox - decoy - docs
    layout = {"1_Documentation/1- Siemens Open Library - Overview.pdf": docs // 2,
              "1_Documentation/2- Siemens Open Library - Initial Setup.pdf": docs - docs // 2,
              DECOY: decoy, "2_Library/Open Scale Library V21 Professional.bin": padding}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as archive:
        archive.writestr("1_Documentation/License.txt", "Synthetic vendor-shaped package for TIA MCP scale qualification. Not from Siemens or DMC.\n")
        archive.writestr("4_Modbus/License.txt", "Synthetic notice placeholder.\n")
        archive.writestr(LIBRARY, real)
        for name, size in layout.items():
            _random(archive, name, size)
        with archive.open("3_Alarm Generator/Scale_Toolbox_v1.0.zip", "w", force_zip64=True) as stream:
            with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as nested:
                for index in range(25):  # Never executed; intake must inventory them only.
                    _random(nested, "Toolbox/%s%02d.%s" % ("Tool" if index < 5 else "Lib", index, "exe" if index < 5 else "dll"), toolbox // 25)
                nested.writestr("Toolbox/README.txt", "Never executed.\n")
                nested.writestr("Toolbox/Config.ini", "[config]\n")  # .xml would classify as a source candidate
        for index in range(21):  # The reviewed outer archive has 29 entries in total.
            archive.writestr("4_Modbus/Examples/Example%02d.txt" % index, "Example %d\n" % index)
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(all(".." not in PurePosixPath(n).parts and not n.startswith("/") for n in names), "Generated unsafe member")
        require(archive.read(LIBRARY) == real, "Library archive bytes differ inside the package")
    with open(path, "rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return {"path": str(path), "bytes": Path(path).stat().st_size, "sha256": digest, "entries": len(names),
            "libraryMember": LIBRARY, "librarySha256": hashlib.sha256(real).hexdigest(), "libraryBytes": len(real),
            "decoyMember": DECOY, "targetBytes": total_bytes}


def candidate(candidates, member):
    """The one global-library candidate extracted from an outer package member (intake prefixes content/<n>/)."""
    rows = [c for c in candidates if c["relativePath"].endswith("/" + member)]
    require(len(rows) == 1 and rows[0]["kind"] == "globalLibraryArchive", "Expected one global-library candidate for " + member)
    return rows[0]


def memory():
    import psutil
    rows = {}
    for process in psutil.process_iter(["name", "memory_info"]):
        name = process.info["name"] or ""
        if name.lower().startswith(("siemens.automation.portal", "tiamcp.")) and process.info["memory_info"]:
            rows.setdefault(name, []).append(process.info["memory_info"].rss)
    return rows


def checkpoint_bytes(h, change_id):
    return {p.name: p.stat().st_size for p in h.directory.rglob("change-" + change_id + "/before*") if p.is_file()}


def timed_change(h, kind, payload, label, compact=True):
    """Prepare and apply one change; record wall time, preview size, checkpoint bytes and memory in report['changes']."""
    started = time.perf_counter()
    prepared = h.prepare(kind, payload)
    row = {"label": label, "kind": kind, "prepareSeconds": round(time.perf_counter() - started, 3),
           "previewBytes": len(json.dumps(prepared, separators=(",", ":")))}
    started = time.perf_counter()
    applied = h.apply_prepared(kind, prepared)
    row.update(applySeconds=round(time.perf_counter() - started, 3), checkpoints=checkpoint_bytes(h, prepared["changeId"]),
               memory=memory(), changeId=prepared["changeId"])
    if compact:  # Large step payloads would make every persist quadratic; the change row keeps the measurements.
        h.report["steps"][-2]["result"] = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}
        h.report["steps"][-1]["result"] = {"state": applied["state"], "objects": len(applied["journal"]["result"].get("objects", []))}
    h.report["changes"].append(row)
    h.persist()
    print("CHANGE " + json.dumps({k: v for k, v in row.items() if k != "memory"}), flush=True)
    return applied


def upload(h, package):
    data = Path(package["path"]).read_bytes()
    started = time.perf_counter()
    begun = h.tool("artifact_upload_begin", {"fileName": Path(package["path"]).name, "expectedBytes": len(data), "sha256": package["sha256"]})
    for offset in range(0, len(data), CHUNK):
        h.client.tool("artifact_upload_chunk", {"uploadId": begun["uploadId"], "offset": offset,
                                                 "data": base64.b64encode(data[offset:offset + CHUNK]).decode("ascii")})
    committed = h.tool("artifact_upload_commit", {"uploadId": begun["uploadId"]})
    h.client.tool_calls = [c for c in h.client.tool_calls if c["name"] != "artifact_upload_chunk"] + [
        {"name": "artifact_upload_chunk", "isError": False, "count": -(-len(data) // CHUNK)}]
    return {"uploadId": begun["uploadId"], "artifactRef": committed["artifactRef"], "chunks": -(-len(data) // CHUNK),
            "seconds": round(time.perf_counter() - started, 3)}


def body(h):
    args = h.args
    names = component_names(args.components)
    h.report.update(components=len(names), changes=[])

    # Session 1: author, release, publish and archive the source components.
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    documents = h.directory / "documents"
    documents.mkdir()
    released = []
    for name, composition in names:
        text, suffix = document(name, composition)
        (documents / (name + suffix)).write_text(text, encoding="utf-8")
        timed_change(h, "library_create_type", {"schemaVersion": 1, "destinationLibraryId": library, "documentDirectory": str(documents),
                     "documentName": name, "targetEnvironment": "selectedPlc", "destinationComposition": composition, "target": {"plcPath": plc}},
                     "create " + name)
        version = [x for x in h.versions(library, name) if x["state"] == "InWork"]
        require(len(version) == 1, "Expected one InWork version of " + name)
        timed_change(h, "library_release_version", h.selected(library, version, versionNumber="1.0.0", author="TIA MCP qualification",
                     comment="Scale fixture"), "release " + name)
        committed = [x for x in h.versions(library, name) if x["state"] == "Committed"]
        require(len(committed) == 1 and committed[0]["version"] == "1.0.0", "Release of " + name + " differs")
        released.append(committed[0])
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "OpenScaleLibrary"})["journal"]["result"]["library"]["libraryId"]
    timed_change(h, "library_publish", h.selected(library, released, destinationLibraryId=global_library), "publish all components")
    archive = h.archive(global_library)
    h.report["sourceArchive"] = archive
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    # The vendor-sized package; generated outside the intake root.
    (h.directory / "vendor-input").mkdir()
    package = h.stage("generate vendor-sized package", lambda: vendor_package(h.directory / "vendor-input" / "Open-Scale-Library-V21.zip",
                                                                                archive["path"], args.package_bytes))
    h.report["package"] = package

    # Session 2: intake, staging and one combined planned import.
    uploaded = h.stage("chunked upload of the package", lambda: upload(h, package))

    def stage():
        started = time.perf_counter()
        staged = h.tool("library_package_stage", {"artifactRef": uploaded["artifactRef"]})
        seconds = round(time.perf_counter() - started, 3)
        status = h.tool("library_package_status", {"packageId": staged["packageId"]})
        require(status["state"] == "ready" and status["sourceSha256"] == package["sha256"], "Staged package differs from the upload")
        candidates, offset, pages = [], 0, 0
        started = time.perf_counter()
        while offset is not None:
            page = h.tool("library_package_inspect", {"packageId": staged["packageId"], "count": 200, "offset": offset})
            candidates += page["items"]
            pages += 1
            offset = page["nextOffset"]
        documents = h.tool("library_package_inspect", {"packageId": staged["packageId"], "section": "documents", "count": 200})
        return {"packageId": staged["packageId"], "stageSeconds": seconds, "inspectSeconds": round(time.perf_counter() - started, 3),
                "pages": pages, "status": status, "candidates": candidates, "documents": documents["items"]}
    staged = h.stage("stage and classify the package", stage)
    by_path = {member: candidate(staged["candidates"], member) for member in (LIBRARY, DECOY)}
    require(not any(c["relativePath"].lower().endswith((".exe", ".dll")) for c in staged["candidates"]), "Toolbox executables became candidates")

    def over_limit():
        limit = staged["status"].get("limits", {}).get("maxInputBytes", 1 << 30)  # PackageIntakeLimits default
        large = vendor_package(h.directory / "vendor-input" / "Over-Limit.zip", archive["path"], limit + (8 << 20))
        started = time.perf_counter()
        error = h.call_error("library_package_stage", {"hostPath": large["path"]})
        require(error.get("code") == "PackageLimitExceeded", "Over-limit package was not refused: %s" % error)
        Path(large["path"]).unlink()  # Generated input only; the refused staging state stays retained.
        return {"limitBytes": limit, "inputBytes": large["bytes"], "refusalSeconds": round(time.perf_counter() - started, 3), "error": error}
    h.report["overLimit"] = h.stage("refuse a package above the input byte limit", over_limit)

    consumer = h.owned_scratch("combined import")
    def opened(member):
        return {"packageId": staged["packageId"], "candidateId": by_path[member]["candidateId"]}

    def corrupt():
        error = h.call_error("library_open", {"request": opened(DECOY)})
        require(error.get("code"), "Corrupt decoy refusal lacks a structured code")
        return error
    h.report["decoyRefusal"] = h.stage("refuse the corrupt decoy archive", corrupt)

    def open_real():
        started = time.perf_counter()
        source = h.tool("library_open", {"request": opened(LIBRARY)})["libraryId"]
        seconds = round(time.perf_counter() - started, 3)
        started = time.perf_counter()
        items = all_items(h, source)
        return {"libraryId": source, "openSeconds": seconds, "querySeconds": round(time.perf_counter() - started, 3),
                "complete": items["complete"], "items": len(items["items"]),
                "versions": [x for x in items["items"] if x.get("kind") == "version"]}
    source = h.stage("open the staged library and read all metadata", open_real)
    require(source["complete"] and sorted(v["typeName"] for v in source["versions"]) == sorted(n for n, _ in names),
            "Staged library metadata differs from the published components")
    selection = h.selected(source["libraryId"], sorted(source["versions"], key=lambda v: v["typeName"]), target={"plcPath": consumer})
    # The worker counts selections before resolving or de-duplicating them, so cycling fills 101 at any --components.
    over = [selection["selection"][i % len(selection["selection"])] for i in range(101)]
    h.refuse("refuse a 101-component selection", "library_instantiate", {**selection, "selection": over}, "PackageLimitExceeded")
    before = h.inventory()
    applied = timed_change(h, "library_instantiate", selection, "combined import of %d components" % len(names), compact=False)
    rows = applied["journal"]["result"]["objects"]
    after = h.inventory()

    def verify():
        require(len(rows) == len(names) and all(r["linkVerified"] for r in rows), "Not every component carries its exact native link")
        added = {k: sorted(set(map(tuple, after[consumer][k])) - set(map(tuple, before[consumer][k]))) for k in ("blocks", "types")}
        require(len(added["blocks"]) == len(added["types"]) == len(names) // 2, "Inventory delta differs from the selection")
        require(all(set(map(tuple, before[consumer][k])) <= set(map(tuple, after[consumer][k])) for k in before[consumer]), "Existing content was not preserved")
        project = h.project_library()
        for version in (source["versions"][0], source["versions"][-1]):
            h.verify_use(project, version, 1)
        return {"objects": len(rows), "added": {k: len(v) for k, v in added.items()}, "compile": applied["journal"].get("compile", {}).get("errorCount")}
    h.report["combinedImport"] = h.stage("verify the combined import", verify)
    h.report["steps"][-2]["result"] = {"state": applied["state"], "objects": len(rows)}  # The full journal stays in the change directory.
    h.stage("close combined import session", lambda: h.tool("tia_detach"))

    # Session 3: the same components as separate single-component changes.
    separate = h.owned_scratch("separate imports")
    reopened = h.tool("library_open", {"request": opened(LIBRARY)})["libraryId"]
    versions = sorted((x for x in all_items(h, reopened)["items"] if x.get("kind") == "version"), key=lambda v: v["typeName"])
    for version in versions[:args.separate]:
        timed_change(h, "library_instantiate", h.selected(reopened, [version], target={"plcPath": separate}), "separate import " + version["typeName"])
    h.stage("close separate import session", lambda: h.tool("tia_detach"))
    # Applied changes' recovery journals keep the package as their source of record.
    h.refuse("keep the applied package retained", "library_package_release", {"packageId": staged["packageId"]}, "PackageInUse")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--components", type=int, default=100)
    parser.add_argument("--separate", type=int, default=100, help="Components applied again as separate single-component changes.")
    parser.add_argument("--package-bytes", type=int, default=DMC_V17_BYTES)
    arguments = parser.parse_args()
    arguments.runner = __file__
    Harness(arguments, "is_", "Native %d-component planned import and vendor-sized package intake" % arguments.components, [
        "Owned scratch only; observed timings on this host, not cross-machine budgets.",
        "The package reproduces the byte volume and shape of the retained DMC V17 distribution; its only valid native payload is the owned archive. Vendor bytes are not on this PC.",
        "library_instantiate copies exact released types; PLC library_integrate (call/instance/signal) combined apply stays disabled and is not exercised.",
        "Toolbox executables are generated random bytes and never executed."], []).run(body)
