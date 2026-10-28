"""Qualify the real explorer MCP flow using only a newly owned scratch project.

Native capture, cached MCP queries and content extraction are timed separately.
The optional config uses the actual local/SSH launcher, never a silent fallback.
"""
import argparse
import base64
import datetime
import hashlib
import json
import pathlib
import platform
import statistics
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from qualify_project_index import initialize, require, validate_owned_status, generated_functions, source_for
from qualify_deployment import McpClient, load_configuration
from qualify_cpu_memory_bytes import version_key


def transfer(client, artifact, destination):
    chunks, offset = [], 0
    while True:
        part = client.tool("artifact_read", {"path": artifact["path"], "offset": offset})
        require(part["offset"] == offset, "Artifact offset mismatch")
        data = base64.b64decode(part["data"], validate=True)
        chunks.append(data)
        offset = part["nextOffset"]
        require(offset <= 64 * 1024 * 1024, "Export exceeds bounded transfer")
        if part["complete"]:
            break
    data = b"".join(chunks)
    require(hashlib.sha256(data).hexdigest() == artifact["sha256"], "Artifact hash mismatch")
    destination.write_bytes(data)
    return data


def exact_reference(snapshot, node):
    """Preserve all six reference fields; display names never resolve a target."""
    require(node.get("origin") == "native" and node.get("objectPath"), "Only native objects have agent references")
    return {"schemaVersion": "project-model.v1", "snapshotId": snapshot["snapshotId"], "sessionGeneration": snapshot["sessionGeneration"],
            "projectPath": snapshot["project"]["projectPath"], "nodeId": node["nodeId"], "objectPath": node["objectPath"]}


def validate_fixture_relationships(graph, plc_path):
    """Expected direction comes from the authored scratch program, not analyzer output."""
    block = lambda name: plc_path + "/blocks/" + name
    actual_calls = {(e["source"]["objectPath"], e["target"]["objectPath"]) for e in graph["edges"]
                    if e["kind"] == "calls" and e.get("source") and e.get("target")}
    expected = {(block("Main"), block("TiaMcp_EnableGate")), (block("TiaMcp_EnableGate"), block("TiaMcp_IsEnabled"))}
    require(expected <= actual_calls, "Authored OB->FB->FC call directions are missing")
    require(not any((target, source) in actual_calls for source, target in expected), "Call graph reverses authored call direction")
    require(any(e["kind"] == "instanceOf" and (e.get("source") or {}).get("objectPath") == block("TiaMcp_EnableGate_DB")
                and (e.get("target") or {}).get("objectPath") == block("TiaMcp_EnableGate") for e in graph["edges"]),
            "Native global instance DB must point to its FB type")
    require(all(e.get("evidence", {}).get("sourcePath") and e["evidence"].get("location") for e in graph["edges"] if e["kind"] == "calls"),
            "Each native call must link to source path and location evidence")
    for kind, member in (("reads", "Enable"), ("writes", "Active")):
        require(any(e["kind"] == kind and (e.get("source") or {}).get("objectPath") == block("TiaMcp_EnableGate")
                    and (e.get("unresolved") or {}).get("target", {}).get("name") == member for e in graph["edges"]),
                "Authored FB member " + kind + " evidence missing or incorrectly substituted with a project object")


FIXTURE_BLOCKS = ("Main", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcp_IsEnabled")
# Authored from the fixed scratch SCL source: Main calls the FB through its
# instance DB, the FB calls the FC, the DB is an instance of the FB.
REQUIRED_RESOLVED = {("calls", "Main", "TiaMcp_EnableGate"), ("calls", "TiaMcp_EnableGate", "TiaMcp_IsEnabled"),
                     ("instanceOf", "TiaMcp_EnableGate_DB", "TiaMcp_EnableGate")}
SEED_RESOLVED = REQUIRED_RESOLVED | {("usesType", "TiaMcp_EnableGate_DB", "TiaMcp_EnableGate"), ("dependsOn", "Main", "TiaMcp_EnableGate"),
                                     ("dependsOn", "TiaMcp_EnableGate", "TiaMcp_IsEnabled"), ("dependsOn", "TiaMcp_EnableGate_DB", "TiaMcp_EnableGate")}
# FB member access, both as local and as instance-qualified native names.
SEED_MEMBERS = {(kind, name) for kind, names in (("reads", ("Enable", '"TiaMcp_EnableGate_DB".Enable')), ("writes", ("Active", '"TiaMcp_EnableGate_DB".Active')),
                ("dependsOn", ("Enable", '"TiaMcp_EnableGate_DB".Enable', "Active", '"TiaMcp_EnableGate_DB".Active'))) for name in names}
RELATIONSHIP_KINDS = {"calls", "reads", "writes", "instanceOf", "usesType", "dependsOn"}


def _endpoint(edge, side, plc_path):
    """Resolved fixture block name, or the unresolved native member identity."""
    resolved = edge.get(side) or {}
    if resolved.get("objectPath"):
        prefix = plc_path + "/blocks/"
        require(resolved["objectPath"].startswith(prefix) and resolved["objectPath"][len(prefix):] in FIXTURE_BLOCKS,
                "Relationship endpoint outside the authored fixture: " + resolved["objectPath"])
        return ("block", resolved["objectPath"][len(prefix):])
    member = ((edge.get("unresolved") or {}).get(side)) or {}
    identity = member.get("nativeIdentity") or {}
    owner = (identity.get("nativePath") or "").rsplit("\\", 1)[-1]
    name = member.get("name") or ""
    require(owner in FIXTURE_BLOCKS or any(name.startswith('"' + block + '".') for block in FIXTURE_BLOCKS),
            "Unresolved endpoint is not owned by an authored fixture block: " + name)
    return ("member", name)


def relationship_sets(graph, plc_path):
    resolved, members = set(), set()
    require(len({e["edgeId"] for e in graph["edges"]}) == len(graph["edges"]), "Duplicate relationship edge identities")
    for edge in graph["edges"]:
        require(edge["kind"] in RELATIONSHIP_KINDS, "Unexpected native relationship kind " + edge["kind"])
        source, target = _endpoint(edge, "source", plc_path), _endpoint(edge, "target", plc_path)
        if source[0] == target[0] == "block":
            resolved.add((edge["kind"], source[1], target[1]))
        else:
            members.add((edge["kind"], source[1], target[1]))
    return resolved, members


def validate_relationship_scope(seed, full, truncated, plc_path):
    """Exact authored edges at depth 1; every retained edge within the maximum budget; visible truncation."""
    resolved, members = relationship_sets(seed, plc_path)
    require(resolved == SEED_RESOLVED, "Seed resolved relationships differ from the authored source: " + repr(sorted(resolved ^ SEED_RESOLVED)))
    seed_members = {(kind, name) for kind, source, name in members if source == "TiaMcp_EnableGate"}
    require(seed_members == SEED_MEMBERS and len(seed_members) == len(members),
            "Seed member relationships differ from the authored FB interface: " + repr(sorted(seed_members ^ SEED_MEMBERS)))
    scope = full["relationshipScope"]
    require(scope["status"] == "complete" and scope["stopReasons"] == [] and full["truncated"] is False
            and scope["returnedEdges"] == scope["retainedEdges"] == len(full["edges"]) and scope["projectCompleteness"] == "notEstablished",
            "Bounded traversal did not return every retained edge with a complete scope")
    require(full["nativeCoverage"]["scopeComplete"] is True and full["nativeCoverage"]["truncated"] is False, "Native acquisition scope is incomplete")
    full_resolved, full_members = relationship_sets(full, plc_path)
    require(REQUIRED_RESOLVED <= full_resolved and resolved <= full_resolved and members <= full_members,
            "Whole fixture graph lost authored or seed relationships")
    cut = truncated["relationshipScope"]
    require(truncated["truncated"] is True and cut["status"] != "complete" and "edges" in cut["stopReasons"]
            and len(truncated["edges"]) == cut["returnedEdges"] <= truncated["limits"]["maxEdges"] < len(seed["edges"]),
            "Edge budget truncation is not visible")
    return {"seedEdges": len(seed["edges"]), "retainedEdges": scope["retainedEdges"], "resolved": sorted(map(list, full_resolved)),
            "unresolvedMembers": sorted(map(list, full_members)), "truncatedReturnedEdges": cut["returnedEdges"], "truncatedStopReasons": cut["stopReasons"]}


def validate_typed_preview(details, reference, language):
    providers = [p for p in details.get("previews", []) if p.get("provider") == "typedLadFbd.v21"]
    require(len(providers) == 1, "Exactly one native typed preview provider must respond")
    provider = providers[0]
    require(provider["status"] in ("available", "partial") and provider["reference"] == reference, "Typed provider must preserve exact selection")
    revision = provider["contentRevision"]
    require(revision.get("status") == "available" and revision.get("algorithm") == "sha256" and len(revision.get("value", "")) == 64,
            "Preview needs a separate immutable content hash")
    require(provider["sourceArtifact"]["sha256"] == revision["value"], "Preview revision must be original native artifact SHA-256")
    attachments = provider.get("attachments", [])
    require(attachments, "Native fixture must produce a usable structured preview")
    for attachment in attachments:
        require(attachment["kind"] == "ladFbdNetwork" and attachment["source"]["objectPath"] == reference["objectPath"], "Wrong preview kind or source owner")
        require(attachment["source"]["contentRevision"] == revision and attachment["source"].get("capturedAtUtc"), "Missing source revision/timestamp")
        require(attachment["content"]["language"] == language and attachment.get("coverage"), "Preview must retain language and coverage")
        require('"nativeXml"' not in json.dumps(attachment["content"]), "Raw native XML must stay in its source artifact")
    # Independently authored fixed fixture has one LAD Contact+Coil, or one FBD Coil.
    observed_names = {n.get("nativeName") for a in attachments for n in a["content"].get("nodes", [])}
    expected = {"Contact", "Coil"} if language == "LAD" else {"Coil"}
    require(expected <= observed_names, "Expected native fixture instruction names are absent from preview")
    require(provider.get("runtimeVerified") is False, "Inspection must not claim runtime qualification")
    return provider


def memory_sample(client, tia_pid):
    """Resident memory of the owned TIA process, the Host and its worker children."""
    import psutil
    sample = {"tiaPortalRss": psutil.Process(tia_pid).memory_info().rss}
    host = getattr(getattr(client, "process", None), "pid", None)
    if host:
        process = psutil.Process(host)
        sample["hostRss"] = process.memory_info().rss
        sample["workerRss"] = {str(child.pid): child.memory_info().rss for child in process.children(recursive=True)}
    return sample


def native_performance(steps, client, tia_pid, model, snapshot_id, output, stem):
    """Cold capture comes from the first timed capture step; a warm recapture runs on the same session."""
    timed = lambda name: [step["milliseconds"] for step in steps if step.get("name") == name and "milliseconds" in step]
    before = memory_sample(client, tia_pid)
    warm = model("capture", lifetimeSeconds=900)
    warm_ms = timed("capture")[-1]
    model("release", snapshotId=warm["snapshotId"])
    relationships = [step["result"].get("payloadBytes") for step in steps if step.get("name") == "relationships" and step.get("passed")]
    return {"captureMilliseconds": {"cold": timed("capture")[0], "warm": warm_ms, "warmVisited": warm.get("usage", {}).get("visited")},
            "detailsMilliseconds": timed("details"), "relationshipsMilliseconds": timed("relationships"),
            "payloadBytes": {"relationships": relationships, **{fmt: (output / (stem + suffix)).stat().st_size
                             for fmt, suffix in (("json", ".snapshot.json"), ("html", ".html")) if (output / (stem + suffix)).exists()}},
            "memoryBytes": {"afterQueries": before, "afterWarmCapture": memory_sample(client, tia_pid)},
            "snapshotId": snapshot_id, "method": "Single owned session; cold is the first capture after fixture edits, warm repeats it without edits."}


def qualify(args):
    require(1 <= args.batch_size <= 1000, "batch-size must be 1..1000")
    native_batch_size = min(args.batch_size, 100)
    output = args.report.resolve().parent
    output.mkdir(parents=True, exist_ok=True)
    run_root = args.artifacts.resolve() / ("ex_" + uuid.uuid4().hex[:8])
    run_root.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "steps": [], "clientMachine": platform.platform(), "python": platform.python_version(),
              "fixtureBatching": {"requested": args.batch_size, "effective": native_batch_size, "nativeMaximum": 100,
                                   "reason": "Existing prepared-change declaration/name limits remain unchanged; larger requested batches are split."},
              "qualificationScope": "Owned offline native fixture only; no physical topology, online/runtime or universal project-size claim."}
    client = None
    def save():
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    def call(tool, arguments=None, label=None):
        start = time.perf_counter()
        step = {"name": label or tool, "tool": tool, "passed": False}
        report["steps"].append(step)
        try:
            result = client.tool(tool, arguments or {})
            step.update(passed=True, result=result)
            return result
        except Exception as error:
            step["failure"] = str(error)
            raise
        finally:
            step["milliseconds"] = (time.perf_counter() - start) * 1000
            save()
            print(("PASS " if step["passed"] else "FAIL ") + step["name"], flush=True)
    def model(action, **kw):
        return call("project_model_" + action if action != "status" else "project_model", {"request": {"action": action, **kw}}, action)
    def check(name, ok):
        require(ok, name)
        report["steps"].append({"name": name, "passed": True})
        save()
        print("PASS " + name, flush=True)
    def rejected(request, accepted_codes, name):
        raw = client.request("tools/call", {"name": "project_model", "arguments": {"request": request}})
        result = raw.get("structuredContent") or json.loads(raw["content"][0]["text"])
        check(name, raw.get("isError") is True and result.get("error", {}).get("code") in accepted_codes)
    def page_all(snapshot, action="search", **filters):
        result, cursor = [], None
        while True:
            page = model(action, snapshotId=snapshot["snapshotId"], maxItems=17, cursor=cursor, **filters)
            result.extend(page["nodes"])
            cursor = page.get("nextCursor")
            if cursor is None:
                require(len(result) == page["totalMatches"], "Paging omissions")
                break
            require(len(result) <= 50000, "Nonterminating paging")
        require(len({n["nodeId"] for n in result}) == len(result), "Duplicate page nodes")
        return result
    try:
        if args.config:
            config = load_configuration(args.config, args.server)
            report["transport"] = config.get("transport", "configured")
            client = McpClient(config, args.timeout)
        else:
            client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(run_root)])
            report.update(transport="local-stdio", hostPath=str(args.host.resolve()), workerPath=str(args.worker.resolve()),
                          hostAssemblySha256=hashlib.sha256(args.host.with_suffix(".dll").read_bytes()).hexdigest(),
                          workerSha256=hashlib.sha256(args.worker.read_bytes()).hexdigest())
        initialize(client)
        tools = client.request("tools/list")["tools"]
        check("new tools are read-only and available", all(next(t for t in tools if t["name"] == name)["annotations"]["readOnlyHint"]
              for name in ("project_model", "project_model_capture", "project_model_details", "project_model_export")))
        check("unnegotiated clients receive no app UI metadata", all("resourceUri" not in t.get("_meta", {}).get("ui", {}) for t in tools))
        started = call("tia_start")
        pid = started["processId"]
        validate_owned_status(started, pid, require_owner=True)
        scratch = call("tia_create_scratch")
        require(scratch["completed"] and scratch["compile"]["errorCount"] == 0, "Scratch fixture failed")
        project_path = pathlib.Path(scratch["project"]["path"])
        report["projectPath"] = str(project_path)
        report["selectedCpu"] = scratch.get("selectedCpu")
        inventory = call("project_inventory", {"maxItems": 2000})
        plc_path = inventory["plcs"][0]["path"]
        # Every fixture edit goes through the existing prepared-change workflow.
        for parent, name in ((plc_path + "/blocks", "ExplorerEmpty"), (plc_path + "/blocks", "ExplorerNested"), (plc_path + "/blocks/groups/ExplorerNested", "EmptyLeaf")):
            validate_owned_status(call("tia_session_status"), pid, project_path, require_owner=True)
            prepared = call("change_prepare", {"kind": "group_create", "payload": {"groupKind": "blocks", "groupPath": parent, "name": name}, "plcPath": plc_path})
            applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                          "checkpointSave": True, "compile": True, "save": True})
            require(applied["state"] == "applied", "Fixture group creation failed")
        if args.seeder:
            require(not args.config, "Local fixture helper cannot run against remote host paths")
            auth = run_root / "authorization.json"
            auth.write_text(json.dumps({"ownsPortalProcess": True, "creationTool": "tia_create_scratch", "projectPath": str(project_path), "processId": pid}), encoding="utf-8")
            report["seederSha256"] = hashlib.sha256(args.seeder.read_bytes()).hexdigest()
            for mode in ("seed", "add-alternate-namespace") if args.namespace_collision else ("seed",):
                seeded = subprocess.run([str(args.seeder.resolve()), mode, str(auth)], capture_output=True, text=True, timeout=180)
                require(seeded.returncode == 0, seeded.stdout + seeded.stderr)
                report["softwareUnitFixture" if mode == "seed" else "namespaceCollisionFixture"] = json.loads(seeded.stdout)
                require(json.loads(seeded.stdout).get("completed") is True, "Software unit fixture did not compile: " + mode)
        if args.two_plcs:
            validate_owned_status(call("tia_session_status"), pid, project_path, require_owner=True)
            selected = scratch["selectedCpu"]
            cpu = selected if isinstance(selected, str) else selected.get("typeIdentifier")
            if args.second_cpu:
                entries = [e for e in call("hardware_catalog_search", {"filter": args.second_cpu.replace("CPU ", ""), "maxItems": 200})["entries"] if e.get("typeName") == args.second_cpu]
                require(entries, "No catalog entry for " + args.second_cpu)
                cpu = max(entries, key=version_key)["typeIdentifier"]
                report["secondCpu"] = cpu
            require(cpu, "Scratch response has no selected native CPU identifier")
            prepared = call("change_prepare", {"kind": "hardware_add", "payload": {"typeIdentifier": cpu,
                            "itemName": "ExplorerPLC_B", "deviceName": "ExplorerStation_B", "configureScratchSecurity": True}})
            applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                          "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
            require(applied["state"] == "applied", "Second owned PLC fixture failed")
        if args.count:
            names = generated_functions(args.count, "TiaMcp_Index_Explorer")
            for offset in range(0, len(names), native_batch_size):
                validate_owned_status(call("tia_session_status"), pid, project_path, require_owner=True)
                batch = names[offset:offset + native_batch_size]
                source = source_for(batch)
                prepared = call("change_prepare", {"kind": "source_upsert", "plcPath": plc_path,
                                "payload": {"language": "SCL", "source": source, "expectedBlockNames": batch}})
                applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                              "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True})
                require(applied["state"] == "applied", "Medium/large native fixture import failed")
            report["generatedFunctionCount"] = len(names)
        if args.typed_previews:
            for language in ("LAD", "FBD"):
                validate_owned_status(call("tia_session_status"), pid, project_path, require_owner=True)
                fixture = call("fixture_sources", {"language": language, "blockName": "ExplorerPreview_" + language})
                require(fixture["kind"] == "block_import_xml", "Graphical fixture must use the existing XML preparation route")
                prepared = call("change_prepare", {"kind": fixture["kind"], "payload": fixture["payload"], "plcPath": plc_path})
                applied = call("change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": uuid.uuid4().hex,
                              "checkpointSave": True, "compile": True, "save": True})
                require(applied["state"] == "applied", language + " native preview fixture import/compile failed")
        old_index = call("project_index", {"request": {"action": "capture", "lifetimeSeconds": 900}})
        snapshot = model("capture", lifetimeSeconds=900)
        report["nativeCapture"] = snapshot.get("usage")
        nodes = page_all(snapshot)
        report["nodeCount"] = len(nodes)
        by_id = {n["nodeId"]: n for n in nodes}
        check("all children have captured parents", all(n.get("parentId") is None or n["parentId"] in by_id for n in nodes))
        check("native and virtual identities stay separate", all((bool(n.get("objectPath"))) == (n["origin"] == "native") for n in nodes))
        if args.two_plcs:
            check("two independently owned PLC scopes captured", len({n["plcPath"] for n in nodes if n.get("plcPath")}) == 2)
        if args.count:
            check("all independently generated native blocks captured", {n["name"] for n in nodes if n["name"].startswith("TiaMcp_Index_Explorer_")} == set(names))
        if args.seeder:
            repeated = [n for n in nodes if n["kind"] == "block" and n["name"] == "TiaMcp_IsEnabled"]
            check("duplicate names across native units keep exact identities", len(repeated) >= 3 and len({n["objectPath"] for n in repeated}) == len(repeated))
        expected = {n["name"]: n for n in nodes if n["name"] in ("ExplorerEmpty", "ExplorerNested", "EmptyLeaf")}
        check("native empty groups and exact ancestry", len(expected) == 3 and all(n["origin"] == "native" for n in expected.values())
              and expected["EmptyLeaf"]["parentId"] == expected["ExplorerNested"]["nodeId"])
        status = call("project_model", {"request": {"action": "status", "snapshotId": snapshot["snapshotId"]}}, "project_model status")
        check("status names the captured snapshot and its metadata revision", status["snapshotId"] == snapshot["snapshotId"]
              and status["metadataRevision"] == snapshot["metadataRevision"])
        # E1: the tree must agree with independent native reads, not only with its own pages.
        def same(label, captured, expected):
            require(captured == expected, "%s: missing %s, extra %s" % (label, sorted(expected - captured)[:5], sorted(captured - expected)[:5]))
            check(label + " (%d)" % len(expected), True)
        plcs = sorted({n["objectPath"] for n in nodes if n["kind"] == "plc"})
        report["independentTree"] = {}
        for plc in plcs:
            inspected = call("software_inspect", {"plcPath": plc, "maxItems": 2000}, "independent software_inspect")
            require(inspected["completeness"]["scopeComplete"] is True, "Independent software inspection incomplete")
            name = plc.rsplit("/", 1)[1]
            for kind, rows in (("block", inspected["blocks"]), ("type", inspected["types"]), ("tagTable", inspected["tagTables"])):
                same("%s %s paths equal software_inspect" % (name, kind), {n["objectPath"] for n in nodes if n["kind"] == kind and n.get("plcPath") == plc},
                     {row["path"] for row in rows})
            same("%s group paths equal software_inspect" % name,
                 {n["objectPath"] for n in nodes if n["kind"] in ("blockGroup", "typeGroup", "tagGroup", "technologyGroup") and n.get("plcPath") == plc},
                 {g for scope in inspected["softwareScopes"] for g in scope["groups"]})
            unit_groups = [n for n in nodes if n["kind"] == "softwareUnitGroup" and n.get("plcPath") == plc]
            unsupported = [c for c in snapshot["coverage"] if c["provider"] == "softwareUnits" and c["scope"] == plc and c["status"] == "unsupported"]
            check(name + " has a native unit group or an explicit unsupported unit service", len(unit_groups) + len(unsupported) == 1)
            report["independentTree"][plc] = {"blocks": len(inspected["blocks"]), "types": len(inspected["types"]), "tagTables": len(inspected["tagTables"]),
                                              "unitService": "unsupported" if unsupported else "native"}
        rows, cursor = [], None
        while True:
            page = call("device_inventory", {"pageSize": 200, **({"snapshotId": inventory_id, "cursor": cursor} if cursor else {})}, "independent device_inventory")
            inventory_id, cursor = page["snapshot"]["snapshotId"], page["nextCursor"]
            rows.extend(page["rows"])
            if cursor is None:
                break
        require(page["snapshot"]["coverage"]["scopeComplete"] is True, "Independent device inventory incomplete")
        same("device and device-item paths equal device_inventory", {n["objectPath"] for n in nodes if n["kind"] in ("device", "hardware")}, {row["path"] for row in rows})
        if args.second_cpu:
            check("the second CPU's missing unit service is reported unsupported, not empty",
                  any(v["unitService"] == "unsupported" for v in report["independentTree"].values()))
        if args.namespace_collision:
            colliding = [n for n in nodes if n["kind"] == "block" and n["name"] == "TiaMcp_IsEnabled"]
            by_scope = {}
            for n in colliding:
                by_scope.setdefault(n["scopePath"], set()).add(n["metadata"].get("namespace"))
            check("same block name in two namespaces of one unit keeps both exact identities",
                  len({n["objectPath"] for n in colliding}) == len(colliding) and any(len(spaces) == 2 for spaces in by_scope.values()))
        parents = {None, *(n.get("parentId") for n in nodes)}
        all_children = []
        for parent in parents:
            children = page_all(snapshot, "children", parentId=parent)
            require({n["nodeId"] for n in children} == {n["nodeId"] for n in nodes if n.get("parentId") == parent}, "Independent child partition mismatch")
            all_children.extend(children)
        check("all native child pages exact without duplication", len(all_children) == len(nodes))
        legacy = call("project_index", {"request": {"action": "query", "indexId": old_index["indexId"], "maxItems": 200}})
        check("explorer reads preserve legacy project_index snapshot", legacy["valid"] and legacy["rows"])
        block = next(n for n in nodes if n["name"] == "TiaMcp_EnableGate" and n["kind"] == "block")
        reference = exact_reference(snapshot, block)
        report["selectedReference"] = reference
        details = model("details", reference=reference)
        independent = call("block_read", {"blockPath": block["objectPath"]})
        report["independentInspection"] = independent
        check("exact selected path survives independent native inspection", block["objectPath"] in json.dumps(independent))
        graph = model("relationships", reference=reference, depth=1, maxNodes=100, maxEdges=200)
        validate_fixture_relationships(graph, plc_path)
        check("independent authored calls, instance direction and unresolved member reads/writes match", True)
        if not args.count and not args.seeder and not args.two_plcs:
            full_graph = model("relationships", reference=reference, depth=5, maxNodes=500, maxEdges=1000)
            cut_graph = model("relationships", reference=reference, depth=1, maxNodes=100, maxEdges=3)
            report["relationshipScopeQualification"] = validate_relationship_scope(graph, full_graph, cut_graph, plc_path)
            check("exact authored relationships, complete bounded traversal and visible edge truncation", True)
        hardware = next(n for n in nodes if n["kind"] == "hardware" and n["objectPath"] == plc_path.rsplit("/plc/", 1)[0])
        hardware_reference = exact_reference(snapshot, hardware)
        hardware_details = model("details", reference=hardware_reference)
        independent_hardware = call("hardware_inspect", {"hardwarePath": hardware["objectPath"], "maxItems": 2000})
        require(hardware_details["status"] == "available", "Exact hardware details unavailable")
        direct_item = next(i for i in independent_hardware["items"] if i["path"] == hardware["objectPath"])
        detail_item = next(i for i in hardware_details["details"]["items"] if i["path"] == hardware["objectPath"])
        check("hardware details agree with independent native position/address evidence", direct_item["positionNumber"] == detail_item["positionNumber"]
              and direct_item["addresses"] == detail_item["addresses"] and hardware["metadata"]["topologyVerified"] is False)
        hardware_graph = model("relationships", reference=hardware_reference, kinds=["configuredIo"], depth=1, maxNodes=100, maxEdges=200)
        require(hardware_graph.get("status") not in ("unsupported", "unavailable"), "Hardware reverse configured-I/O provider unavailable")
        for edge in hardware_graph["edges"]:
            require(edge["kind"] == "configuredIo" and edge["target"]["objectPath"] == hardware["objectPath"], "Hardware graph invented another relationship/target")
            if edge["evidence"]["resolution"] == "resolved":
                require(edge["evidence"]["native"]["plcOwnershipVerified"] is True, "Resolved I/O requires native controller ownership")
        check("hardware reverse query is bounded and preserves configured-I/O ownership uncertainty", len(hardware_graph["nodes"]) <= 100 and len(hardware_graph["edges"]) <= 200
              and hardware_graph.get("candidateBounds") is not None)
        report["configuredIoQualification"] = {"observedEdges": len(hardware_graph["edges"]),
            "knownPositiveBindingVerified": bool(hardware_graph["edges"]), "limitation": "Empty configured-I/O results do not qualify a positive native module/tag binding."}
        if args.typed_previews:
            report["typedPreviewQualification"] = []
            for language in ("LAD", "FBD"):
                graphical = next(n for n in nodes if n["kind"] == "block" and n["name"] == "ExplorerPreview_" + language and n.get("plcPath") == plc_path)
                graphical_reference = exact_reference(snapshot, graphical)
                preview = model("details", reference=graphical_reference)
                provider = validate_typed_preview(preview, graphical_reference, language)
                data = transfer(client, provider["sourceArtifact"], output / (args.report.stem + "." + language.lower() + ".native.xml"))
                native = ET.fromstring(data)
                native_names = {n.attrib.get("Name") for n in native.iter() if n.tag.rsplit("}", 1)[-1] == "Part"}
                expected_names = {"Contact", "Coil"} if language == "LAD" else {"Coil"}
                check(language + " preview matches independently read original native instructions and SHA-256", expected_names <= native_names
                      and hashlib.sha256(data).hexdigest() == provider["contentRevision"]["value"])
                report["typedPreviewQualification"].append({"language": language, "status": provider["status"], "attachments": len(provider["attachments"]),
                    "sourceArtifactSha256": provider["sourceArtifact"]["sha256"], "nativeInspectionVerified": True, "runtimeVerified": False})
        verified = model("verify", snapshotId=snapshot["snapshotId"])
        check("metadata verification explicitly excludes content", verified["matchesCurrentMetadata"] and verified["contentVerified"] is False)
        timings = []
        for _ in range(25):
            start = time.perf_counter()
            client.tool("project_model_search", {"request": {"snapshotId": snapshot["snapshotId"], "text": "TiaMcp", "maxItems": 200}})
            timings.append((time.perf_counter() - start) * 1000)
        report["cachedQueryMilliseconds"] = {"count": len(timings), "median": statistics.median(timings), "p95": sorted(timings)[int(len(timings) * .95)], "includesMcpTransport": True}
        for format in ("json", "html"):
            exported = model("export", snapshotId=snapshot["snapshotId"], format=format)
            target = output / (args.report.stem + (".snapshot.json" if format == "json" else ".html"))
            transfer(client, exported["artifact"], target)
            report.setdefault("exports", {})[format] = str(target)
            save()
        report["nativePerformance"] = native_performance(report["steps"], client, pid, model, snapshot["snapshotId"], output, args.report.stem)
        save()
        invalid_ref = {**reference, "sessionGeneration": "different-session"}
        rejected({"action": "details", "reference": invalid_ref}, {"SessionMismatch", "StaleSelection", "InvalidSelection"}, "session mismatch cannot resolve selection")
        partial = model("capture", maxVisited=12, lifetimeSeconds=900)
        check("bounded capture reports partial coverage", partial["usage"]["visited"] <= 12 and any(c["status"] in ("partial", "truncated") for c in partial["coverage"]))
        model("release", snapshotId=partial["snapshotId"])
        rejected({"action": "search", "snapshotId": partial["snapshotId"]}, {"SnapshotNotFound", "SnapshotReleased", "ModelNotFound"}, "released snapshot cannot be read")
        rejected({"action": "change_apply"}, {"InvalidRequest"}, "viewer endpoint rejects mutation action")
        call("tia_detach")
        rejected({"action": "details", "reference": reference}, {"SnapshotInvalidated", "SessionMismatch", "SnapshotNotFound", "StaleSelection"}, "disconnect invalidates exact selection")
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error), flush=True)
    finally:
        if client:
            try:
                client.tool("tia_detach")
            except Exception as error:
                report["cleanupNote"] = str(error)
            client.close()
            report["toolCalls"] = getattr(client, "tool_calls", [])
        report["finishedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        save()
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, default=ROOT / "src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--worker", type=pathlib.Path, default=ROOT / "src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe")
    parser.add_argument("--artifacts", type=pathlib.Path, default=ROOT / "artifacts")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-explorer-native.json")
    parser.add_argument("--config", type=pathlib.Path)
    parser.add_argument("--server", default="tia-v21")
    parser.add_argument("--seeder", type=pathlib.Path)
    parser.add_argument("--two-plcs", action="store_true", help="Create a second PLC only in this newly owned fixture using prepared changes.")
    parser.add_argument("--second-cpu", help="Catalog type name of the second PLC (newest version), e.g. 'CPU 1214C DC/DC/DC', whose PLC has no software-unit service; default: the scratch CPU.")
    parser.add_argument("--namespace-collision", action="store_true", help="After --seeder, add a same-named block in a second namespace of one unit.")
    parser.add_argument("--typed-previews", action="store_true", help="Import/compile fixed LAD and FBD fixtures through prepared changes and verify exact native preview attachments.")
    parser.add_argument("--count", type=int, default=0, help="0 or 201..2000 native generated functions for measured medium/large profiles.")
    parser.add_argument("--batch-size", type=int, default=100, help="Requested batch 1..1000; split at the existing 100-declaration prepared-change ceiling, recorded in the report.")
    parser.add_argument("--timeout", type=int, default=600)
    raise SystemExit(qualify(parser.parse_args()))
