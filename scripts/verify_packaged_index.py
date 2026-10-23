"""Read-only packaged-index regression on an exact completed owned fixture report.

Starts one new owned TIA instance, opens only the report-backed saved scratch,
checks pagination/verified freshness/expiry, then detaches. No engineering changes,
compile, save, simulator, user-session attachment or process termination are used.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import time
import uuid

from smoke_mcp import Client, ROOT
from qualify_project_index import decode, initialize, require, validate_exhaustive, validate_owned_status, validate_page


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=pathlib.Path, required=True)
    parser.add_argument("--fixture-report", type=pathlib.Path, default=ROOT / "artifacts/project-index-native.json")
    parser.add_argument("--report", type=pathlib.Path, default=ROOT / "artifacts/project-index-packaged.json")
    args = parser.parse_args()
    fixture_report = args.fixture_report.resolve(strict=True)
    require(fixture_report.is_relative_to((ROOT / "artifacts").resolve()), "Fixture report must be under repository artifacts")
    fixture_hash = digest(fixture_report)
    fixture = json.loads(fixture_report.read_text(encoding="utf-8"))
    require(fixture.get("completed") is True and fixture.get("generatedFunctionCount", 0) >= 201, "A completed paging fixture is required")
    fixture_root = pathlib.Path(fixture["artifactRoot"]).resolve(strict=True)
    project_path = pathlib.Path(fixture["projectPath"]).resolve(strict=True)
    require(fixture_root.is_relative_to((ROOT / "artifacts").resolve()) and fixture_root.name.startswith("ix_"), "Fixture artifact root is not an owned index run")
    require(project_path.is_relative_to(fixture_root) and project_path.stem.startswith("TiaMcpScratch_") and project_path.suffix == ".ap21", "Project is not the report's owned scratch")
    host = (args.bundle / "host/TiaMcp.Host.exe").resolve(strict=True)
    worker = (args.bundle / "workers/openness/TiaMcp.Openness.V21.exe").resolve(strict=True)
    run_root = ROOT / "artifacts" / ("pkg_ix_" + uuid.uuid4().hex[:8])
    run_root.mkdir(exist_ok=False)
    report = {"schemaVersion": 1, "completed": False, "sourceFixtureReport": str(fixture_report), "sourceFixtureReportSha256": fixture_hash,
              "projectPath": str(project_path), "artifactRoot": str(run_root), "hostPath": str(host), "workerPath": str(worker),
              "hostSha256": digest(host), "hostAssemblySha256": digest(host.with_suffix(".dll")), "workerSha256": digest(worker), "steps": [],
              "scope": "Read-only native metadata paging, verification freshness and expiry on the prior saved owned fixture; no engineering edits."}
    client = None
    def save():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    def call(name, arguments=None, label=None):
        record = {"name": label or name, "tool": name, "arguments": arguments or {}, "passed": False}
        report["steps"].append(record)
        save()
        started = time.perf_counter()
        try:
            value = client.tool(name, arguments)
            record["result"], record["passed"] = value, True
            return value
        except Exception as error:
            record["failure"] = str(error)
            raise
        finally:
            record["elapsedSeconds"] = round(time.perf_counter() - started, 6)
            save()
            print(("PASS " if record["passed"] else "FAIL ") + record["name"], flush=True)
    def check(name, condition, details=None):
        require(condition, name)
        report["steps"].append({"name": name, "passed": True, "assertion": details or {}})
        save()
        print("PASS " + name, flush=True)
    def index(request, label):
        return call("project_index", {"request": request}, label)
    try:
        client = Client(host, 300, ["--worker", str(worker), "--artifacts", str(run_root)])
        initialize(client)
        started = call("tia_start", label="start new owned TIA instance")
        pid = started["processId"]
        require(isinstance(pid, int) and pid > 0, "No owned PID")
        validate_owned_status(started, pid, require_owner=True)
        call("project_open", {"path": str(project_path)}, "open exact report-backed saved scratch")
        validate_owned_status(call("tia_session_status", label="verify exact owned session and project"), pid, project_path, require_owner=True)
        captured = index({"action": "capture", "lifetimeSeconds": 30}, "capture metadata with 30-second lifetime")
        require(pathlib.Path(captured["projectPath"]).resolve() == project_path, "Index project identity differs from source fixture")
        check("capture row count matches completed fixture", captured["rowCount"] == fixture["rowCount"], {"rowCount": captured["rowCount"]})
        rows, cursor, total = [], None, None
        while True:
            request = {"action": "query", "indexId": captured["indexId"], "maxItems": 43}
            if cursor:
                request["cursor"] = cursor
            page = index(request, "page offset " + str(len(rows)))
            total = validate_page(page, len(rows), captured["indexId"], total)
            rows.extend(page["rows"])
            cursor = page["nextCursor"]
            if cursor is None:
                break
            require(len(rows) <= captured["rowCount"], "Paging did not terminate")
        validate_exhaustive(rows, total)
        check("exhaustive unique sorted native metadata", len(rows) == fixture["rowCount"], {"rows": len(rows)})
        status = index({"action": "status", "indexId": captured["indexId"], "verifyCurrent": True}, "status with current metadata verification")
        check("verified status reports correct freshness", status["freshness"] == "metadataVerifiedAtResponse" and status["valid"] is True and status["metadataRevision"] == captured["metadataRevision"])
        verified = index({"action": "verify", "indexId": captured["indexId"]}, "verify metadata unchanged")
        check("native recapture matches saved metadata", verified["matchesCurrentMetadata"] is True and verified["valid"] is True and verified["freshness"] == "metadataVerifiedAtResponse")
        expires = datetime.datetime.fromisoformat(captured["expiresAtUtc"].replace("Z", "+00:00"))
        wait_seconds = max(0.0, (expires - datetime.datetime.now(datetime.timezone.utc)).total_seconds() + 0.75)
        require(wait_seconds <= 35, "Unexpected expiry wait; refusing unbounded delay")
        print("Waiting " + str(round(wait_seconds, 2)) + " seconds for explicit snapshot expiry", flush=True)
        wait_started = time.monotonic()
        time.sleep(wait_seconds)
        report["expiryWaitSeconds"] = round(time.monotonic() - wait_started, 6)
        raw = client.request("tools/call", {"name": "project_index", "arguments": {"request": {"action": "query", "indexId": captured["indexId"]}}})
        value = decode(raw)
        check("expired snapshot rejected without reuse", raw.get("isError") and value.get("error", {}).get("code") == "IndexExpired", value)
        require(digest(fixture_report) == fixture_hash, "Source fixture report changed during qualification")
        report["rowCount"] = len(rows)
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
        print("FAIL " + str(error), flush=True)
    finally:
        if client is not None:
            try:
                call("tia_detach", label="detach owned fixture session")
            except Exception as error:
                report["cleanupFailure"], report["completed"] = str(error), False
            finally:
                client.close()
                report["hostExitCode"] = client.process.returncode
                report["hostLogTail"] = client.errors[-20:]
        report["passedSteps"] = sum(step["passed"] for step in report["steps"])
        save()
        print("Report: " + str(args.report), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
