"""Native explorer relationship-provider and collection-scope qualification.

Default mode builds a fresh owned offline scratch with one PROFINET subnet, IO
system and ET 200SP head through prepared changes, then checks configured-network
edges against an independent hardware_inspect oracle whose explorer paths are
derived here from native names. It also plugs a TM PosInput 2 module through a
prepared change; the native UnitSeeder then adds two software units with one
relation and an external-encoder TO connected to that module's channel, because
neither has a prepared change kind. Both providers are checked against
software_inspect and technology_read. --verify-report reopens that saved fixture
read-only. --project audits another saved project read-only. Never run while
another native corpus owns the TIA machine.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import queue
import subprocess
import time
import uuid
from urllib.parse import quote

from smoke_mcp import Client, ROOT
from qualify_project_index import initialize, require, validate_owned_status
from qualify_project_explorer import exact_reference, transfer

CPU = "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"
HEAD = "OrderNumber:6ES7 155-6AU02-0BN0/V6.4"
SERVER = "OrderNumber:6ES7 193-6PA00-0AA0/V1.2"
SERVER_MODULE_NAMES = {"PROFINET interface": "Server module_1", "PROFINET-Schnittstelle": "Servermodul_1"}
SUBNET, SYSTEM = "ExplorerProofSubnet", "ExplorerProofSystem"
NETWORK_KINDS = ["connectedSubnet", "ioSystemController", "ioSystemDevice"]
# Exact module of fixtures/encoder-hardware/cpu1511-fw41-tm-posinput2-v10.json.
ENCODER_MODULE, ENCODER_MODULE_NAME, ENCODER = "OrderNumber:6ES7 551-1AB01-0AB0/V2.0", "ExplorerEncoderHardware", "ExplorerEncoder"
UNIT_A, UNIT_B = "ExplorerUnitA", "ExplorerUnitB"
SEEDED_PROVIDERS = ["technologyConnections", "softwareUnitRelations"]


def segment(value):
    """Uri.EscapeDataString equivalent for explorer path segments."""
    return quote(value, safe="-_.~")


def expected_network_edges(items, project_name):
    """Oracle: resolved (kind, hardware, target) triples from hardware_inspect rows."""
    root = "/projects/" + segment(project_name) + "/subnets/"
    expected = set()
    for item in items:
        network = item.get("network") or {}
        for node in network.get("nodes", []):
            subnet = node.get("subnet") or {}
            if subnet.get("state") == "connected" and subnet.get("resolution") == "resolved":
                expected.add(("connectedSubnet", item["path"], root + segment(subnet["name"])))
        rows = [("ioSystemController", row.get("ioSystem") or {}, row) for row in network.get("ioControllers", [])]
        rows += [("ioSystemDevice", row.get("connectedToIoSystem") or {}, row) for row in network.get("ioConnectors", [])]
        for kind, system, row in rows:
            subnet = system.get("subnet") or {}
            if system.get("state") != "assigned" or subnet.get("resolution") != "resolved":
                continue
            if kind == "ioSystemController" and (system.get("controllerOwner") or {}).get("ownerHardwarePath") != item["path"]:
                continue
            expected.add((kind, item["path"], root + segment(subnet["name"]) + "/ioSystems/" + segment(system["name"])))
    return expected


def resolved_triples(graph, kinds):
    triples = set()
    for edge in graph.get("edges", []):
        if edge.get("kind") not in kinds:
            continue
        evidence = edge.get("evidence") or {}
        require(evidence.get("capturedAtUtc") and (evidence.get("contentRevision") or {}).get("status") == "unavailable",
                "Provider evidence must keep its own capture time and no content revision")
        if evidence.get("resolution") == "resolved":
            require(edge.get("unresolved") is None and edge.get("source") and edge.get("target"), "Resolved edge lacks exact endpoints")
            triples.add((edge["kind"], edge["source"]["objectPath"], edge["target"]["objectPath"]))
        else:
            require(edge.get("unresolved") and edge["unresolved"].get("reason"), "Unresolved edge lacks its reason")
    return triples


def require_complete_coverage(graph, provider, scope):
    rows = [row for row in graph.get("providerCoverage", []) if row.get("provider") == provider and row.get("scope") == scope]
    require(len(rows) == 1 and rows[0].get("status") == "complete", "%s coverage for %s is not complete: %r" % (provider, scope, rows))
    require(provider in (graph.get("providerScope") or {}).get("providers", []), "Graph must declare the %s provider" % provider)


def validate_hardware_graph(graph, hardware_path, expected):
    observed = resolved_triples(graph, NETWORK_KINDS)
    wanted = {triple for triple in expected if triple[1] == hardware_path}
    require(observed == wanted, "Network edges for %s differ from the hardware_inspect oracle: %r != %r" % (hardware_path, sorted(observed), sorted(wanted)))
    scope = graph.get("providerScope") or {}
    require("configuredNetwork" in scope.get("providers", []), "Hardware graph must declare the configured-network provider")
    return observed


def expected_technology_targets(read):
    """Oracle: hardware paths of resolved hardware/channel references in technology_read."""
    targets = set()

    def walk(value, nested=False):
        if isinstance(value, dict):
            if value.get("valueSemantics") == "engineeringReference" and not nested:
                if value.get("resolution") == "resolved":
                    path = value.get("hardwarePath") if value.get("kind") == "hardware" else (value.get("parent") or {}).get("hardwarePath") if value.get("kind") == "channel" else None
                    if path:
                        targets.add(path)
                for child in value.values():
                    walk(child, True)
                return
            for child in value.values():
                walk(child, nested)
        elif isinstance(value, list):
            for child in value:
                walk(child, nested)

    walk(read.get("hardwareConnections", []))
    return targets


def validate_unit_edges(graph, unit_path, relations, nodes):
    """Returns [relatedObject, resolved target path or None] per native relation."""
    edges = [edge for edge in graph.get("edges", []) if edge.get("kind") == "unitRelation"]
    require(len(edges) == len(relations), "Every native unit relation must appear once as an edge")
    by_path = {node.get("objectPath"): node for node in nodes}
    observed = []
    for edge in edges:
        require(edge["source"]["objectPath"] == unit_path, "Unit relation must originate at the selected unit")
        native = edge["evidence"]["native"]
        require({"relationType": native.get("relationType"), "relatedObject": native.get("relatedObject")} in relations, "Edge does not match a native relation")
        if edge["evidence"]["resolution"] == "resolved":
            target = by_path[edge["target"]["objectPath"]]
            require(target["name"] == native["relatedObject"], "Resolved unit relation names another object")
            require(target["kind"] in ("softwareUnit", "safetyUnit") if native["relationType"] == "SoftwareUnit" else target["kind"] == "block",
                    "Resolved unit relation target has the wrong native kind")
        observed.append([native["relatedObject"], edge["target"]["objectPath"] if edge["evidence"]["resolution"] == "resolved" else None])
    return sorted(observed, key=lambda row: (row[0], row[1] or ""))


def read_only_mode(args):
    return bool(args.verify_report or args.project)


def validate_seeded_providers(nodes, technology, units):
    """The seeded fixture's exact expectation: A -> B resolves and the encoder reaches its module."""
    def one(kind, name):
        rows = [node for node in nodes if node["kind"] == kind and node["name"] == name]
        require(len(rows) == 1, "Seeded %s %s must appear exactly once" % (kind, name))
        return rows[0]
    unit_a, unit_b = one("softwareUnit", UNIT_A), one("softwareUnit", UNIT_B)
    module = one("hardware", ENCODER_MODULE_NAME)["objectPath"]
    require(units.get(unit_a["objectPath"]) == [[UNIT_B, unit_b["objectPath"]]], "Seeded unit relation did not resolve to unit B")
    require(units.get(unit_b["objectPath"]) == [], "Unit B must have no relation")
    targets = technology.get(one("technologyObject", ENCODER)["objectPath"], [])
    require(targets and all(target == module or target.startswith(module + "/") for target in targets),
            "Seeded encoder connection must resolve inside its TM PosInput module: %r" % targets)


def run(args):
    prior = json.loads(args.verify_report.read_text(encoding="utf-8")) if args.verify_report else None
    if prior:
        require(prior.get("completed") is True and not prior.get("unknownOutcome") and prior.get("projectPath"),
                "Read-only verification requires a completed fixture report")
    release = pathlib.Path(args.release_file.read_text(encoding="utf-8-sig").strip()).resolve()
    host, worker = release / "host/TiaMcp.Host.exe", release / "workers/openness/TiaMcp.Openness.V21.exe"
    require(host.is_file() and worker.is_file(), "Immutable release host/worker are missing")
    seeder = args.seeder.resolve()
    require(read_only_mode(args) or seeder.is_file(), "Build the UnitSeeder first: " + str(seeder))
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    run_root = args.report.parent / ("ex_rel_" + uuid.uuid4().hex[:8])
    run_root.mkdir(exist_ok=False)
    read_only = read_only_mode(args)
    report = {"schemaVersion": 1, "completed": False, "status": "running", "steps": [], "readOnly": read_only,
              "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "release": str(release), "artifactRoot": str(run_root),
              "qualificationScope": "Configured subnet/IO-system membership, technology-object hardware references, software-unit relations and the subnet/watch/external-source collections of one offline project. No cables, ports, physical topology, online or runtime claim.",
              "binaries": {"hostAssembly": {"path": str(host.with_suffix(".dll")), "sha256": hashlib.sha256(host.with_suffix(".dll").read_bytes()).hexdigest()},
                           "worker": {"path": str(worker), "sha256": hashlib.sha256(worker.read_bytes()).hexdigest()},
                           "seeder": None if read_only else {"path": str(seeder), "sha256": hashlib.sha256(seeder.read_bytes()).hexdigest()}},
              "fixtureSourceSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(), "providers": {}}
    client, started, uncertain = None, False, False

    def save():
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def call(name, arguments=None):
        nonlocal uncertain
        step = {"tool": name, "arguments": arguments or {}, "passed": False}
        report["steps"].append(step)
        begun = time.perf_counter()
        save()
        try:
            value = client.tool(name, arguments or {})
            step.update(passed=True, result=value)
            return value
        except Exception as error:
            step["failure"] = type(error).__name__ + ": " + str(error)
            uncertain = isinstance(error, (queue.Empty, TimeoutError)) or any(
                token in str(error).lower() for token in ("timeout", "timed out", "workerfault", "bridgefault", "host exited"))
            if uncertain:
                report["unknownOutcome"] = {"tool": name, "retried": False, "reason": "Native outcome is unknown; nothing is replayed."}
            raise
        finally:
            step["milliseconds"] = (time.perf_counter() - begun) * 1000
            save()
            print(("PASS " if step["passed"] else "FAIL ") + name, flush=True)

    def apply(kind, payload, compile_save=True):
        require(not read_only, "Read-only modes cannot prepare or apply fixture changes")
        prepared = call("change_prepare", {"kind": kind, "payload": payload, "plcPath": report["plcPath"]})
        result = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                                       "checkpointSave": True, "compile": compile_save, "save": compile_save})
        require(result.get("state") == "applied" and (not compile_save or result.get("compile", {}).get("errorCount") == 0),
                "Prepared fixture operation did not apply cleanly")
        return result

    def model(action, **arguments):
        return call("project_model_" + action, {"request": {"action": action, **arguments}})

    def hardware_rows():
        rows = []
        for item in call("project_inventory", {"maxItems": 2000})["hardware"]:
            inspected = call("hardware_inspect", {"hardwarePath": item["path"], "maxItems": 2000})
            require(not inspected.get("completeness", {}).get("truncated"), "Oracle hardware read was truncated")
            rows.extend(inspected["items"])
        return rows

    def build_network_fixture():
        catalog = call("hardware_catalog_search", {"filter": "IM 155-6 PN ST", "maxItems": 500})
        require(any(row.get("typeIdentifier") == HEAD for row in catalog["entries"]), "Contracted IM 155-6AU02 V6.4 profile is not installed")
        apply("hardware_add", {"typeIdentifier": HEAD, "deviceName": "ExplorerRemoteIo", "itemName": "ExplorerRemoteHead"}, compile_save=False)
        rows = hardware_rows()
        cpu = next(row for row in rows if (row.get("network") or {}).get("ioControllers"))
        remote = next(row for row in rows if "ExplorerRemoteIo" in row["path"] and (row.get("network") or {}).get("ioConnectors"))
        rail = [row for row in call("project_inventory", {"maxItems": 2000})["hardware"] if "ExplorerRemoteIo" in row["path"]
                and row["classification"] == "None" and row["positionNumber"] == 0 and row["path"].count("/items/") == 1]
        head = [row for row in rows if row["name"] == "ExplorerRemoteHead" and row["path"].count("/items/") == 1]
        require(len(rail) == 1 and len(head) == 1, "Remote head or rack identity is ambiguous")
        server = SERVER_MODULE_NAMES.get(remote["name"])
        require(server, "No contracted server-module name for interface " + repr(remote["name"]))
        author = next(value["value"] for value in head[0]["attributes"] if value["name"] == "Author" and "error" not in value)
        for index, target in enumerate((cpu, remote), start=10):
            apply("network_configure", {"hardwarePath": target["path"], "nodeName": target["network"]["nodes"][0]["name"], "subnetName": SUBNET,
                                        "attributes": {"Address": "192.0.2." + str(index), "SubnetMask": "255.255.255.0"}}, compile_save=False)
        apply("network_io_assign", {"hardwarePath": remote["path"], "connectorIndex": 0, "controllerHardwarePath": cpu["path"], "controllerIndex": 0,
                                    "createIoSystemName": SYSTEM, "expectedServerModule": {"name": server, "typeIdentifier": SERVER,
                                    "containerHardwarePath": rail[0]["path"], "positionNumber": 1, "attributes": {"AdditionalInformation": "", "Author": author,
                                    "Comment": "", "LocationIdentifier": "", "Name": server, "PlantDesignation": ""}}})
        report["fixture"] = {"controllerInterface": cpu["path"], "deviceInterface": remote["path"], "subnet": SUBNET, "ioSystem": SYSTEM}

    def build_provider_fixture():
        nonlocal uncertain
        catalog = call("hardware_catalog_search", {"filter": "TM PosInput 2", "maxItems": 500})
        require(any(row.get("typeIdentifier") == ENCODER_MODULE for row in catalog["entries"]), "Contracted TM PosInput 2 V2.0 profile is not installed")
        inventory = call("project_inventory", {"maxItems": 2000})
        device = inventory["plcs"][0]["hardwarePath"].split("/items/")[0] + "/items/"
        rail = [row for row in inventory["hardware"] if row["path"].startswith(device) and row["positionNumber"] == 0 and row["path"].count("/items/") == 1]
        require(len(rail) == 1, "PLC rack identity is ambiguous")
        apply("hardware_plug", {"hardwarePath": rail[0]["path"], "typeIdentifier": ENCODER_MODULE, "name": ENCODER_MODULE_NAME, "positionNumber": 2})
        authorization = run_root / "seeder-authorization.json"
        authorization.write_text(json.dumps({"ownsPortalProcess": True, "creationTool": "tia_create_scratch", "processId": report["processId"],
                                             "projectPath": report["projectPath"], "moduleName": ENCODER_MODULE_NAME}, indent=2), encoding="utf-8")
        step = {"tool": "UnitSeeder explorer-relations", "passed": False}
        report["steps"].append(step)
        save()
        # The seeder attaches to the owned process and saves; a timeout leaves its outcome unknown.
        try:
            completed = subprocess.run([str(seeder), "explorer-relations", str(authorization)], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", timeout=600)
        except subprocess.TimeoutExpired:
            uncertain = True
            report["unknownOutcome"] = {"tool": step["tool"], "retried": False, "reason": "Seeder outcome is unknown; nothing is replayed."}
            raise
        lines = completed.stdout.strip().splitlines()
        step.update(exitCode=completed.returncode, result=json.loads(lines[-1]) if lines else None, stderr=completed.stderr[-4000:])
        step["passed"] = completed.returncode == 0 and (step["result"] or {}).get("completed") is True
        save()
        print(("PASS " if step["passed"] else "FAIL ") + step["tool"], flush=True)
        require(step["passed"], "Native explorer relation seeding failed")
        report["fixture"].update(encoderModule=ENCODER_MODULE_NAME, technologyObject=ENCODER, units=[UNIT_A, UNIT_B])

    try:
        client = Client(host, args.timeout, ["--worker", str(worker), "--artifacts", str(run_root)])
        initialize(client)
        session = call("tia_start")
        report["processId"], started = session["processId"], True
        validate_owned_status(session, report["processId"], require_owner=True)
        opened = (prior or {}).get("projectPath") or (str(args.project.resolve()) if args.project else None)
        if opened:
            call("project_open", {"path": opened})
            report["projectPath"] = opened
        else:
            scratch = call("tia_create_scratch", {"cpuTypeIdentifier": CPU})
            require(scratch.get("completed") is True and scratch.get("compile", {}).get("errorCount") == 0, "Fresh scratch creation/compile failed")
            report["projectPath"] = scratch["project"]["path"]
        inventory = call("project_inventory", {"maxItems": 2000})
        report["plcPath"] = inventory["plcs"][0]["path"] if inventory["plcs"] else None
        project_name = inventory["project"]["name"]
        if not opened:
            build_network_fixture()
            build_provider_fixture()
        oracle_rows = hardware_rows()
        expected = expected_network_edges(oracle_rows, project_name)
        report["oracle"] = {"networkEdges": sorted(map(list, expected))}

        snapshot = model("capture", lifetimeSeconds=900)
        report["nativeCapture"] = snapshot["usage"]
        nodes, cursor = [], None
        while True:
            page = model("search", snapshotId=snapshot["snapshotId"], maxItems=200, cursor=cursor)
            nodes.extend(page["nodes"])
            cursor = page.get("nextCursor")
            if cursor is None:
                require(len(nodes) == page["totalMatches"], "Snapshot paging omissions")
                break
        by_path = {node.get("objectPath"): node for node in nodes if node.get("objectPath")}
        kinds = sorted({node["kind"] for node in nodes})
        report["collectionScope"] = {kind: sum(1 for node in nodes if node["kind"] == kind) for kind in kinds}
        hierarchy = next(row for row in snapshot["coverage"] if row["provider"] == "nativeHierarchy")
        require(hierarchy["status"] == "complete", "Qualification requires a complete native hierarchy capture")
        for _, _, target in expected:
            require(target in by_path and by_path[target]["kind"] in ("subnet", "ioSystem"), "Configured network object missing from the tree: " + target)
        if report["plcPath"]:
            require("watchTableGroup" in kinds and "externalSourceGroup" in kinds, "PLC watch/force and external-source system groups must be captured")

        network = {}
        for path in sorted({triple[1] for triple in expected}):
            graph = model("relationships", reference=exact_reference(snapshot, by_path[path]), kinds=NETWORK_KINDS, direction="out", depth=1, maxNodes=50, maxEdges=100)
            network[path] = sorted(map(list, validate_hardware_graph(graph, path, expected)))
        for node in (node for node in nodes if node["kind"] in ("subnet", "ioSystem")):
            graph = model("relationships", reference=exact_reference(snapshot, node), kinds=NETWORK_KINDS, direction="in", depth=1, maxNodes=100, maxEdges=200)
            observed = resolved_triples(graph, NETWORK_KINDS)
            require(observed == {triple for triple in expected if triple[2] == node["objectPath"]}, "Incoming network edges differ for " + node["objectPath"])
        report["providers"]["configuredNetwork"] = {"edges": network, "positive": bool(expected)}
        if not read_only:
            require({triple[0] for triple in expected} == set(NETWORK_KINDS), "Fixture must produce subnet, controller and device associations")

        technology = {}
        for node in [node for node in nodes if node["kind"] == "technologyObject"][:args.max_objects]:
            read = call("technology_read", {"plcPath": node["plcPath"], "technologyPath": node["objectPath"], "maxItems": 2000})
            graph = model("relationships", reference=exact_reference(snapshot, node), kinds=["technologyConnection"], direction="out", depth=1, maxNodes=50, maxEdges=200)
            require_complete_coverage(graph, "technologyConnections", node["objectPath"])
            observed = {triple[2] for triple in resolved_triples(graph, ["technologyConnection"])}
            wanted = expected_technology_targets(read) & set(by_path)
            require(observed == wanted, "Technology connection targets differ for " + node["objectPath"])
            technology[node["objectPath"]] = sorted(observed)
        report["providers"]["technologyConnections"] = {"objects": technology, "positive": any(technology.values())}

        units = {}
        if report["plcPath"]:
            software = call("software_inspect", {"plcPath": report["plcPath"], "maxItems": 2000})
            for unit in [row for row in software.get("units", []) if row.get("relations") is not None][:args.max_objects]:
                graph = model("relationships", reference=exact_reference(snapshot, by_path[unit["path"]]), kinds=["unitRelation"], direction="out", depth=1, maxNodes=50, maxEdges=200)
                require_complete_coverage(graph, "softwareUnitRelations", unit["path"])
                units[unit["path"]] = validate_unit_edges(graph, unit["path"], unit["relations"], nodes)
        report["providers"]["softwareUnitRelations"] = {"units": units, "positive": any(target for rows in units.values() for _, target in rows)}
        if not args.project:
            validate_seeded_providers(nodes, technology, units)
        for provider in sorted(set(args.require) | (set() if args.project else set(SEEDED_PROVIDERS))):
            require(report["providers"][provider]["positive"], "Required positive provider evidence is absent: " + provider)

        exported = model("export", snapshotId=snapshot["snapshotId"])
        portable = json.loads(transfer(client, exported["artifact"], args.report.with_suffix(".snapshot.json")))
        retained = {(edge["kind"], edge["source"]["objectPath"], edge["target"]["objectPath"]) for edge in portable["edges"]
                    if edge["kind"] in NETWORK_KINDS and edge["evidence"]["resolution"] == "resolved"}
        require(expected <= retained, "Portable export omitted observed network evidence")
        report["completed"], report["status"] = True, "passed"
    except Exception as error:
        report["failure"] = type(error).__name__ + ": " + str(error)
        report["status"] = "unknownOutcome" if uncertain else "blocked"
        raise
    finally:
        if client is not None:
            if started and not uncertain:
                try:
                    call("tia_detach")
                except Exception as error:
                    report["detachFailure"] = str(error)
            client.close()
            report["hostExitCode"] = client.process.returncode
        save()
        print("Report: " + str(args.report), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-file", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-release.txt")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-native-relationships.json")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--seeder", type=pathlib.Path, default=ROOT / "scripts/native/UnitSeeder/bin/Debug/net48/UnitSeeder.exe",
                        help="Native seeder for the unit relation and TO connection of the fresh fixture.")
    parser.add_argument("--max-objects", type=int, default=20, help="Bound on technology objects and units checked per run.")
    parser.add_argument("--require", nargs="*", default=[], choices=["configuredNetwork", "technologyConnections", "softwareUnitRelations"],
                        help="Providers that must show positive evidence (fixture and --verify-report modes always require all three).")
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--verify-report", type=pathlib.Path, help="Read-only reopen of this qualifier's completed fixture.")
    source.add_argument("--project", type=pathlib.Path, help="Read-only audit of another saved project.")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
