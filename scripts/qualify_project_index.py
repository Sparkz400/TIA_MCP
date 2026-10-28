"""Native metadata-index qualification on one newly created owned scratch project.

Never attaches to an existing user project. An observer may attach only to the PID
created by this runner and must report the exact scratch project identity before
each mutation. No simulator or physical-PLC operations are used.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import time
import uuid

from smoke_mcp import Client, ROOT


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def generated_functions(count, prefix):
    require(201 <= count <= 2000, "count must be 201..2000 to exercise multiple full pages")
    require(prefix.startswith("TiaMcp_Index_") and prefix.replace("_", "").isalnum(), "Invalid fixture prefix")
    return [prefix + "_" + str(index).zfill(4) for index in range(count)]


def source_for(names):
    require(1 <= len(names) <= 100 and len(set(names)) == len(names), "A batch must have 1..100 unique names")
    require(all(name.startswith("TiaMcp_Index_") and name.replace("_", "").isalnum() for name in names), "Only generated fixture names may be imported")
    return "\n\n".join('FUNCTION "' + name + '" : Bool\nVERSION : 0.1\nVAR_INPUT\n    Enable : Bool;\nEND_VAR\nBEGIN\n    #' + name + ' := #Enable;\nEND_FUNCTION' for name in names) + "\n"


def validate_owned_status(status, pid, project_path=None, require_owner=False):
    require(status.get("attached") is True and status.get("processId") == pid, "Session is not attached to this runner's exact TIA instance")
    if require_owner:
        require(status.get("ownsPortalProcess") is True, "Primary session does not own its TIA instance")
    projects = status.get("projects", [])
    if project_path is None:
        require(projects == [], "New owned instance must be empty")
    else:
        require(len(projects) == 1 and pathlib.Path(projects[0]["path"]).resolve() == project_path.resolve(), "Session project identity changed; refusing fixture mutation")


def validate_page(page, offset, index_id, expected_total=None):
    require(page["indexId"] == index_id and page["offset"] == offset, "Page identity or offset changed")
    require(page["freshness"] == "snapshotOnly", "Unverified query claimed fresh native metadata")
    require(page["valid"] is True, "Query returned an invalid snapshot")
    if expected_total is not None:
        require(page["totalMatches"] == expected_total, "Total changed within immutable snapshot")
    require(page["rows"] or page["nextCursor"] is None, "Empty page cannot return a continuation cursor")
    require(page["rowPayloadBytes"] <= 512 * 1024, "Page exceeds payload bound")
    return page["totalMatches"]


def validate_exhaustive(rows, total):
    paths = [row["path"] for row in rows]
    require(len(rows) == total and len(set(paths)) == total, "Pagination omitted or duplicated rows")
    # Canonical paths are URI-escaped ASCII; this is the same order as .NET Ordinal.
    require(paths == sorted(paths), "Snapshot pages are not in stable canonical order")


def decode(raw):
    return raw.get("structuredContent") or json.loads("\n".join(item["text"] for item in raw.get("content", []) if item.get("type") == "text"))


def initialize(client):
    client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "project-index-qualification", "version": "1"}})
    client.notify("notifications/initialized")


def run(args):
    require(args.host.is_file() and args.worker.is_file(), "Host and worker executables are required")
    require(1 <= args.batch_size <= 100, "batch-size must be 1..100")
    names = generated_functions(args.count, "TiaMcp_Index_" + uuid.uuid4().hex[:8])
    run_root = args.artifacts.resolve() / ("ix_" + uuid.uuid4().hex[:8])
    run_root.mkdir(parents=True, exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "hostPath": str(args.host.resolve()), "workerPath": str(args.worker.resolve()),
              "hostAssemblySha256": hashlib.sha256(args.host.with_suffix(".dll").read_bytes()).hexdigest(),
              "workerSha256": hashlib.sha256(args.worker.read_bytes()).hexdigest(),
              "artifactRoot": str(run_root), "generatedFunctionCount": len(names), "steps": [],
              "qualificationScope": "Native offline metadata for one new standard CPU fixture; no logic freshness, runtime, remote transport, or whole-project completeness claim."}
    owner = observer = None
    project_path = None
    owned_pid = None

    def save():
        # Curated evidence lists the tools that answered without error (scripts/curate_evidence.py).
        report["toolCalls"] = [c for client in (owner, observer) if client is not None for c in client.tool_calls]
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    def call(client, name, arguments=None, label=None):
        record = {"name": label or name, "tool": name, "arguments": arguments or {}, "passed": False}
        report["steps"].append(record)
        save()
        start = time.perf_counter()
        try:
            value = client.tool(name, arguments)
            record["result"] = value
            record["passed"] = True
            return value
        except Exception as error:
            record["failure"] = str(error)
            raise
        finally:
            record["elapsedSeconds"] = round(time.perf_counter() - start, 6)
            save()
            print(("PASS " if record["passed"] else "FAIL ") + record["name"], flush=True)

    def check(label, condition, details=None):
        require(condition, label)
        report["steps"].append({"name": label, "passed": True, "assertion": details or {}})
        save()
        print("PASS " + label, flush=True)

    def rejected(client, request, expected, label):
        start = time.perf_counter()
        raw = client.request("tools/call", {"name": "project_index", "arguments": {"request": request}})
        value = decode(raw)
        require(raw.get("isError") and value.get("error", {}).get("code") == expected, f"{label}: expected {expected}: {raw}")
        report["steps"].append({"name": label, "passed": True, "result": value, "elapsedSeconds": round(time.perf_counter() - start, 6)})
        save()
        print("PASS " + label, flush=True)

    def index(request, label=None, client=None):
        return call(client or owner, "project_index", {"request": request}, label)

    def capture(label, **options):
        return index({"action": "capture", "lifetimeSeconds": 900, **options}, label)

    def pages(snapshot, label, max_items=37, **filters):
        cursor = None
        rows = []
        total = None
        first_cursor = None
        while True:
            request = {"action": "query", "indexId": snapshot["indexId"], "maxItems": max_items, **filters}
            if cursor:
                request["cursor"] = cursor
            page = index(request, label + " offset " + str(len(rows)))
            total = validate_page(page, len(rows), snapshot["indexId"], total)
            rows.extend(page["rows"])
            cursor = page["nextCursor"]
            first_cursor = first_cursor or cursor
            if cursor is None:
                break
            require(len(rows) <= snapshot["rowCount"], "Paging failed to terminate")
        validate_exhaustive(rows, total)
        return rows, first_cursor

    def mutate(client, kind, payload, label, primary=False):
        status = call(client, "tia_session_status", label=label + " ownership")
        validate_owned_status(status, owned_pid, project_path, require_owner=primary)
        require(project_path.is_relative_to(run_root) and project_path.stem.startswith("TiaMcpScratch_"), "Mutation target is not this run's scratch project")
        prepared = call(client, "change_prepare", {"kind": kind, "payload": payload, "projectName": project_path.stem}, label + " preview")
        require(pathlib.Path(prepared["preview"]["project"]["path"]).resolve() == project_path, "Preview project differs from owned scratch")
        applied = call(client, "change_apply", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                       "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}, label + " apply")
        require(applied["state"] == "applied" and applied["compile"]["errorCount"] == 0, "Fixture mutation failed or did not compile")
        return applied

    try:
        owner = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(run_root)])
        initialize(owner)
        tools = owner.request("tools/list")
        require("project_index" in {tool["name"] for tool in tools["tools"]}, "Build has no project_index tool")
        start = call(owner, "tia_start", label="new owned instance")
        owned_pid = start["processId"]
        require(isinstance(owned_pid, int) and owned_pid > 0, "Missing owned PID")
        validate_owned_status(start, owned_pid, require_owner=True)
        scratch = call(owner, "tia_create_scratch", {"cpuTypeIdentifier": args.cpu}, "new isolated scratch")
        require(scratch["completed"] is True and scratch["compile"]["errorCount"] == 0, "Scratch setup failed")
        project_path = pathlib.Path(scratch["project"]["path"]).resolve()
        require(project_path.is_relative_to(run_root) and project_path.stem.startswith("TiaMcpScratch_"), "Unexpected scratch project path")
        report["projectPath"], report["ownedProcessId"], report["selectedCpu"] = str(project_path), owned_pid, scratch["selectedCpu"]
        for offset in range(0, len(names), args.batch_size):
            batch = names[offset:offset + args.batch_size]
            source = source_for(batch)
            (run_root / ("functions-" + str(offset) + ".scl")).write_text(source, encoding="utf-8")
            mutate(owner, "source_upsert", {"language": "SCL", "source": source, "expectedBlockNames": batch}, "seed functions " + str(offset), primary=True)

        full = capture("capture full fixture")
        check("full capture reports metadata scope complete only", full["completeness"]["scopeComplete"] is True and full["completeness"]["complete"] is False)
        rows, cursor = pages(full, "exhaustive metadata pages")
        check("every captured metadata row appears once", len(rows) == full["rowCount"], {"rows": len(rows)})
        fixture_rows = [row for row in rows if row.get("objectKind") == "block" and row.get("name") in names]
        check("all generated functions indexed", {row["name"] for row in fixture_rows} == set(names))
        repeated, _ = pages(full, "repeat metadata pages", max_items=113)
        check("page size does not alter snapshot order or values", repeated == rows)
        filtered, _ = pages(full, "filter generated blocks", kind="block", text=names[0].rsplit("_", 1)[0], max_items=53)
        check("kind and text filters isolate exact generated fixtures", {row["name"] for row in filtered} == set(names) and all(row["objectKind"] == "block" for row in filtered))
        exact_scope = fixture_rows[0]["scopePath"]
        scoped, _ = pages(full, "filter PLC scope", kind="block", plcPath=fixture_rows[0]["plcPath"], scopePath=exact_scope, max_items=200)
        check("scope filtering preserves exact boundaries", all(row["scopePath"] == exact_scope and row["plcPath"] == fixture_rows[0]["plcPath"] for row in scoped))
        require(cursor is not None, "Fixture did not produce continuation cursors")
        rejected(owner, {"action": "query", "indexId": full["indexId"], "cursor": cursor, "kind": "block"}, "InvalidCursor", "cursor rejects changed filters")
        rejected(owner, {"action": "query", "indexId": full["indexId"], "cursor": cursor[:-1] + "!"}, "InvalidCursor", "cursor rejects tampering")
        second = capture("capture same metadata again")
        check("stable metadata revision across recapture", second["metadataRevision"] == full["metadataRevision"])
        rejected(owner, {"action": "query", "indexId": second["indexId"], "cursor": cursor}, "InvalidCursor", "cursor rejects different snapshot")
        verified = index({"action": "verify", "indexId": second["indexId"]}, "explicit stable metadata verification")
        check("stable verification matches and remains valid", verified["matchesCurrentMetadata"] is True and verified["valid"] is True and verified["freshness"] == "metadataVerifiedAtResponse")
        partial = capture("capture budget 100", maxVisited=100)
        check("partial capture declares traversal limit", partial["completeness"]["scopeComplete"] is False and partial["visitedItems"] <= 100 and bool(partial["warnings"]))
        partial_rows, _ = pages(partial, "partial snapshot pages")
        check("partial snapshot paging is internally exhaustive", len(partial_rows) == partial["rowCount"] < full["rowCount"])
        released = index({"action": "release", "indexId": partial["indexId"]}, "release partial snapshot")
        check("release acknowledged", released["released"] is True)
        rejected(owner, {"action": "query", "indexId": partial["indexId"]}, "IndexNotFound", "released snapshot rejected")

        before_local = capture("capture before local mutation")
        local_old = fixture_rows[0]
        mutate(owner, "block_rename", {"blockPath": local_old["path"], "newName": local_old["name"] + "_Local"}, "same-session rename", primary=True)
        rejected(owner, {"action": "query", "indexId": before_local["indexId"]}, "IndexInvalidated", "local mutation invalidates prior index")
        before_external = capture("capture before external metadata mutation")
        observer = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(run_root / "observer")])
        initialize(observer)
        attached = call(observer, "tia_attach", {"processId": owned_pid}, "observer attach only owned fixture")
        validate_owned_status(attached, owned_pid, project_path)
        external_old = fixture_rows[1]
        mutate(observer, "block_rename", {"blockPath": external_old["path"], "newName": external_old["name"] + "_External"}, "observer metadata rename")
        cached = index({"action": "query", "indexId": before_external["indexId"], "kind": "block", "text": external_old["name"]}, "snapshot remains explicit about external freshness")
        check("cached query preserves captured metadata", cached["freshness"] == "snapshotOnly" and any(row["name"] == external_old["name"] for row in cached["rows"]))
        changed = index({"action": "verify", "indexId": before_external["indexId"]}, "verify detects external metadata edit")
        check("external edit invalidates verified snapshot", changed["matchesCurrentMetadata"] is False and changed["valid"] is False and changed["currentMetadataRevision"] != before_external["metadataRevision"])
        rejected(owner, {"action": "query", "indexId": before_external["indexId"]}, "IndexInvalidated", "externally changed snapshot rejected after verification")
        after_external = capture("capture external metadata result")
        current = index({"action": "query", "indexId": after_external["indexId"], "kind": "block", "text": external_old["name"], "verifyCurrent": True}, "query with current metadata verification")
        check("verified current query includes renamed object", current["freshness"] == "metadataVerifiedAtResponse" and any(row["name"] == external_old["name"] + "_External" for row in current["rows"]))

        # The observer retains only this runner-created instance while the primary
        # attachment is released and reacquired. Never reconnect to a discovered PID.
        validate_owned_status(call(observer, "tia_session_status", label="observer identity before owner detach"), owned_pid, project_path)
        call(owner, "tia_detach", label="owner detaches with fixture observer retained")
        rejected(owner, {"action": "query", "indexId": after_external["indexId"]}, "IndexInvalidated", "detaching invalidates session index")
        validate_owned_status(call(observer, "tia_session_status", label="observer retained exact fixture"), owned_pid, project_path)
        reattached = call(owner, "tia_attach", {"processId": owned_pid}, "reattach same runner-created instance")
        validate_owned_status(reattached, owned_pid, project_path)
        rejected(owner, {"action": "query", "indexId": after_external["indexId"]}, "IndexInvalidated", "reattachment cannot revive old snapshot")
        final = capture("fresh index after reattachment")
        check("reattached capture still identifies same fixture", pathlib.Path(final["projectPath"]).resolve() == project_path and final["metadataRevision"] == after_external["metadataRevision"])
        report["rowCount"] = full["rowCount"]
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL qualification: " + str(error), flush=True)
    finally:
        for role, client in (("observer", observer), ("owner", owner)):
            if client is not None:
                try:
                    call(client, "tia_detach", label="cleanup " + role + " attachment")
                except Exception as error:
                    report.setdefault("cleanupErrors", []).append({"role": role, "failure": str(error)})
                    report["completed"] = False
                finally:
                    client.close()
                    report[role + "HostExitCode"] = client.process.returncode
                    report[role + "HostLogTail"] = client.errors[-30:]
        report["passedSteps"] = sum(step["passed"] for step in report["steps"])
        report["timings"] = {"querySeconds": [step["elapsedSeconds"] for step in report["steps"] if step.get("arguments", {}).get("request", {}).get("action") == "query"],
                             "captureSeconds": [step["elapsedSeconds"] for step in report["steps"] if step.get("arguments", {}).get("request", {}).get("action") == "capture"]}
        save()
        print("Report: " + str(args.report), flush=True)
    return 0 if report["completed"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--artifacts", type=pathlib.Path, required=True, help="Short artifact base; this runner creates a new unique child")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-index-native.json")
    parser.add_argument("--count", type=int, default=250)
    parser.add_argument("--batch-size", type=int, default=100)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--cpu", default="OrderNumber:6ES7 511-1AL03-0AB0/V4.0")
    return run(parser.parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
