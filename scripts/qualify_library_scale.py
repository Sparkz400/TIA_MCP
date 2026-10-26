"""Native library scale measurement: a 10,000-type project library in an owned scratch, built 100 types per change.

Flat FB types plus a dependency chain (each chain FC calls the previous one). Every change records wall time, the
checkpoint archive size and TIA/worker memory; every --probe-every changes the library is listed and paged completely,
with truncation flags. A stop file ends the run between changes. Measurements are observations on this host, not budgets.
"""
import argparse
import json
import time
from pathlib import Path

from library_native_harness import Harness, require

BATCH = 100


def flat_source(name):
    return (f'FUNCTION_BLOCK "{name}"\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\nVAR_INPUT\n    Enable : Bool;\nEND_VAR\n'
            f'VAR_OUTPUT\n    Active : Bool;\nEND_VAR\nBEGIN\n    #Active := #Enable;\nEND_FUNCTION_BLOCK\n')


def chain_source(name, previous):
    call = f'    #Chain := "{previous}"(Value := #Value);\n' if previous else '    #Chain := #Value;\n'
    return (f'FUNCTION "{name}" : Int\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\nVAR_INPUT\n    Value : Int;\nEND_VAR\n'
            f'VAR_TEMP\n    Chain : Int;\nEND_VAR\nBEGIN\n{call}    #"{name}" := #Chain;\nEND_FUNCTION\n')


def memory():
    try:
        import psutil
    except ImportError:
        return {"available": False}
    rows = {}
    for process in psutil.process_iter(["name", "memory_info"]):
        name = (process.info["name"] or "").lower()
        if name.startswith("siemens.automation.portal") or name.startswith("tiamcp."):
            rows.setdefault(process.info["name"], []).append(process.info["memory_info"].rss)
    return {"available": True, "rssBytes": rows}


def page_all(h, library):
    """Complete library_query paging with timing; truncation stays visible, never hidden."""
    started, pages, items, offset, revision, completeness = time.perf_counter(), 0, 0, 0, None, None
    while True:
        request = {"libraryId": library, "maxItems": 200, "offset": offset, **({"revision": revision} if revision else {})}
        result = h.tool("library_query", {"request": request})
        pages += 1; items += len(result["items"]); revision = result.get("revision", revision); completeness = result.get("completeness")
        if result.get("nextOffset") is None:
            break
        offset = result["nextOffset"]
    return {"pages": pages, "items": items, "seconds": round(time.perf_counter() - started, 3), "completeness": completeness,
            "totalCaptured": result.get("totalCaptured")}


def probe(h, library, label):
    row = {"label": label, "memory": memory()}
    started = time.perf_counter()
    listed = h.tool("library_list")
    row["listSeconds"] = round(time.perf_counter() - started, 3)
    row["libraries"] = len(listed["libraries"])
    try:
        row["query"] = page_all(h, library)
    except Exception as error:  # a traversal limit is a scale observation, not a harness failure
        row["queryRefusal"] = str(error)
    h.report["probes"].append(row)
    h.persist()
    print("PROBE " + label + " " + json.dumps({k: v for k, v in row.items() if k != "memory"}), flush=True)


def run(args):
    total, chain = args.types, args.chain
    require(total % BATCH == 0 and chain % BATCH == 0 and chain <= total, "Type and chain counts must be multiples of 100.")
    stop = args.stop_file.resolve()
    h = Harness(args, "scl", "library-scale-" + str(total), ["Owned scratch only; observed timings on this host, not cross-machine budgets.",
                "Types stay InWork; release, global publication and component apply are not measured."], [])
    h.report.update(types=total, chain=chain, batches=[], probes=[], stopFile=str(stop))
    names = ["Scale_" + str(i).zfill(5) for i in range(total - chain)] + ["Chain_" + str(i).zfill(4) for i in range(chain)]

    def body(h):
        plc = h.owned_scratch("scale")
        library = h.project_library()
        probe(h, library, "empty")
        for start in range(0, total, BATCH):
            if stop.exists():
                h.report["stoppedByRequest"] = start
                break
            documents = h.directory / "docs" / str(start // BATCH).zfill(3)
            documents.mkdir(parents=True)
            batch = names[start:start + BATCH]
            for name in batch:
                index = int(name.split("_")[1])
                source = chain_source(name, "Chain_" + str(index - 1).zfill(4) if index else None) if name.startswith("Chain_") else flat_source(name)
                (documents / (name + ".scl")).write_bytes(source.encode("utf-8"))
            row = {"batch": start // BATCH, "first": batch[0], "last": batch[-1]}
            started = time.perf_counter()
            prepared = h.prepare("library_create_type", {"schemaVersion": 1, "destinationLibraryId": library, "documentDirectory": str(documents),
                                 "documentNames": batch, "targetEnvironment": "selectedPlc",  # native document names omit the extension
                                 "destinationComposition": "blocks", "target": {"plcPath": plc}})
            row["prepareSeconds"] = round(time.perf_counter() - started, 3)
            started = time.perf_counter()
            applied = h.apply_prepared("library_create_type", prepared)
            row["applySeconds"] = round(time.perf_counter() - started, 3)
            created = applied["journal"]["result"]["objects"]
            require(len(created) == len(batch), "A batch did not create exactly its documented types.")
            # The worker archives the project (and written libraries) into the change directory before mutating.
            archives = [p for p in h.directory.rglob("*" + prepared["changeId"] + "*/before*") if p.is_file()]
            row["checkpointArchives"] = {p.name: p.stat().st_size for p in archives}
            row["memory"] = memory()
            # Large step payloads would make every persist quadratic; the batch row keeps the measurements.
            h.report["steps"][-1]["result"] = {"state": applied["state"], "created": len(created)}
            h.report["batches"].append(row)
            h.persist()
            print("BATCH " + json.dumps({k: v for k, v in row.items() if k != "memory"}), flush=True)
            if (start // BATCH + 1) % args.probe_every == 0:
                probe(h, library, str(start + BATCH) + " types")
        probe(h, library, "final")
        last = [x for x in page_all_items(h, library) if x.get("kind") == "version" and x.get("typeName") == names[-1]] if names[-1].startswith("Chain_") else []
        if last:
            started = time.perf_counter()
            try:
                result = h.tool("library_query", {"request": {"libraryId": library, "query": "dependencies", "maxItems": 200,
                                "typeGuid": last[0]["typeGuid"], "versionGuid": last[0]["versionGuid"]}})
                h.report["chainDependencies"] = {"seconds": round(time.perf_counter() - started, 3), "items": len(result["items"]),
                                                 "completeness": result.get("completeness"), "nextOffset": result.get("nextOffset")}
            except Exception as error:
                h.report["chainDependencies"] = {"refusal": str(error)}

    h.run(body)


def page_all_items(h, library):
    items, offset, revision = [], 0, None
    while True:
        result = h.tool("library_query", {"request": {"libraryId": library, "maxItems": 200, "offset": offset, **({"revision": revision} if revision else {})}})
        items += result["items"]; revision = result.get("revision", revision)
        if result.get("nextOffset") is None:
            return items
        offset = result["nextOffset"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--types", type=int, default=10000)
    parser.add_argument("--chain", type=int, default=1000)
    parser.add_argument("--probe-every", type=int, default=10)
    parser.add_argument("--stop-file", type=Path, default=Path("artifacts/scale.stop"))
    arguments = parser.parse_args()
    arguments.runner = __file__
    run(arguments)
