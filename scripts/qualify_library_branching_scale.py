"""Native library scale with a branching dependency graph, a global library and a component install from it.

A complete binary tree of --nodes FC types (default 1,023, depth 10): each FC calls its two children. The types are
created in an owned scratch's project library 100 per change (callees first), released 100 per change, and the root's
dependency closure is queried and compared with the authored tree. The root is published into a new global library,
which must then hold every version Committed with the same closure; the global library is archived. A second owned
scratch opens the archive and library_instantiate installs the root, which brings the whole closure, with compile.
Every change records wall time and TIA/worker memory. Measurements are observations on this host, not budgets.
"""
import argparse
import time
from pathlib import Path

from library_native_harness import Harness, identity, require
from qualify_library_dependencies import closure_edges
from qualify_library_scale import memory

BATCH = 100


def name(k):
    return "Tree_" + str(k).zfill(4)


def children(k, nodes):
    """Creation index k is heap index nodes-1-k; its heap children have larger heap and so smaller creation indexes."""
    heap = nodes - 1 - k
    return [nodes - 1 - c for c in (2 * heap + 1, 2 * heap + 2) if c < nodes]


def source(k, nodes):
    calls = "".join(f'    #Sum := #Sum + "{name(c)}"(Value := #Value);\n' for c in children(k, nodes))
    return (f'FUNCTION "{name(k)}" : Int\n{{ S7_Optimized_Access := \'TRUE\' }}\nVERSION : 0.1\nVAR_INPUT\n    Value : Int;\nEND_VAR\n'
            f'VAR_TEMP\n    Sum : Int;\nEND_VAR\nBEGIN\n    #Sum := #Value;\n{calls}    #"{name(k)}" := #Sum;\nEND_FUNCTION\n')


def expected_edges(nodes):
    return {name(k): {name(c) for c in children(k, nodes)} for k in range(nodes)}


def timed(h, rows, label, kind, payload):
    started = time.perf_counter()
    prepared = h.prepare(kind, payload)
    prepare_seconds = time.perf_counter() - started
    started = time.perf_counter()
    applied = h.apply_prepared(kind, prepared)
    rows.append({"label": label, "prepareSeconds": round(prepare_seconds, 3), "applySeconds": round(time.perf_counter() - started, 3), "memory": memory()})
    h.report["steps"][-1]["result"] = {"state": applied["state"]}  # keep persisted steps small
    h.persist()
    print("TIMED " + label + " " + str(rows[-1]["applySeconds"]), flush=True)
    return applied


def all_versions(h, library, prefix):
    items, offset, revision = [], 0, None
    while True:
        result = h.tool("library_query", {"request": {"libraryId": library, "maxItems": 200, "offset": offset, **({"revision": revision} if revision else {})}})
        items += [x for x in result["items"] if x.get("kind") == "version" and x.get("typeName", "").startswith(prefix)]
        revision = result.get("revision", revision)
        if result.get("nextOffset") is None:
            return items
        offset = result["nextOffset"]


def closure(h, library, root, nodes, label):
    def check():
        started = time.perf_counter()
        rows, offset, revision = [], 0, None
        while True:
            result = h.tool("library_query", {"request": {"libraryId": library, "query": "dependencies", "maxItems": 200, "offset": offset,
                                                          **identity(root), **({"revision": revision} if revision else {})}})
            require(result["completeness"]["scopeComplete"], "Closure scope incomplete")
            rows += result["items"]; revision = result.get("revision", revision)
            if result.get("nextOffset") is None:
                break
            offset = result["nextOffset"]
        require(closure_edges(rows) == expected_edges(nodes), "The closure differs from the authored tree")
        return {"versions": len(rows), "seconds": round(time.perf_counter() - started, 3)}
    h.report.setdefault("closures", {})[label] = h.stage(label, check)


def body(h):
    nodes = h.args.nodes
    rows = h.report.setdefault("timings", [])
    plc = h.owned_scratch("branching scale")
    library = h.project_library()
    for start in range(0, nodes, BATCH):
        documents = h.directory / "docs" / str(start // BATCH).zfill(3)
        documents.mkdir(parents=True)
        batch = [name(k) for k in range(start, min(start + BATCH, nodes))]
        for k in range(start, start + len(batch)):
            (documents / (name(k) + ".scl")).write_bytes(source(k, nodes).encode("utf-8"))
        applied = timed(h, rows, "create %s..%s" % (batch[0], batch[-1]), "library_create_type", {
            "schemaVersion": 1, "destinationLibraryId": library, "documentDirectory": str(documents), "documentNames": batch,
            "targetEnvironment": "selectedPlc", "destinationComposition": "blocks", "target": {"plcPath": plc}})
        require(len(applied["journal"]["result"]["objects"]) == len(batch), "A batch did not create exactly its types")

    # TIA records dependencies between released versions only; names sort callee first, which is the release order.
    in_work = sorted((x for x in all_versions(h, library, "Tree_") if x["state"] == "InWork"), key=lambda x: x["typeName"])
    require(len(in_work) == nodes, "Expected %d InWork tree versions, found %d" % (nodes, len(in_work)))
    for start in range(0, nodes, BATCH):
        batch = in_work[start:start + BATCH]
        timed(h, rows, "release %s..%s" % (batch[0]["typeName"], batch[-1]["typeName"]), "library_release_version",
              h.selected(library, batch, versionNumber="1.0.0", author="TIA MCP qualification", comment="Branching scale release"))
    released = {x["typeName"]: x for x in all_versions(h, library, "Tree_") if x["state"] == "Committed"}
    require(len(released) == nodes, "Not every tree version was released")
    root = released[name(nodes - 1)]
    closure(h, library, root, nodes, "project library closure of the root")

    global_library = timed(h, rows, "create global library", "library_create", {"schemaVersion": 1, "name": "BranchingScale"})["journal"]["result"]["library"]["libraryId"]
    timed(h, rows, "publish the root with its closure", "library_publish", h.selected(library, [root], destinationLibraryId=global_library))
    published = {x["typeName"]: x for x in all_versions(h, global_library, "Tree_")}
    require(len(published) == nodes and all(identity(published[n]) == identity(v) and published[n]["state"] == "Committed" for n, v in released.items()),
            "The global library does not hold the exact released closure")
    closure(h, global_library, root, nodes, "global library closure of the root")
    started = time.perf_counter()
    archive = h.archive(global_library)
    h.report["globalArchive"] = {"bytes": Path(archive["path"]).stat().st_size, "seconds": round(time.perf_counter() - started, 3)}
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close the authoring session", lambda: h.tool("tia_detach"))

    consumer = h.owned_scratch("branching consumer")
    source_library = h.open_archive(archive)
    timed(h, rows, "instantiate the root with its closure and compile", "library_instantiate", h.selected(source_library, [root], target={"plcPath": consumer}))
    installed = [path for path, _ in h.stage("consumer inventory", h.inventory)[consumer]["blocks"] if path.rsplit("/", 1)[-1].startswith("Tree_")]
    h.report["consumerTreeBlocks"] = len(installed)
    require(len(installed) == nodes, "The consumer holds %d tree blocks, not %d" % (len(installed), nodes))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--nodes", type=int, default=1023)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    require(3 <= args.nodes <= 3000, "--nodes must be 3..3000")
    args.runner = __file__
    Harness(args, "lb_", "Library branching scale: a %d-node binary FC tree, global publication and a component install" % args.nodes, [
        "Owned scratches only; observed timings on this host, not cross-machine budgets.",
        "One FC tree with two callees per node; FB, UDT and diamond-shaped sharing are not part of the graph."], []).run(body)
