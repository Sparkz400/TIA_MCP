"""Qualify engineering_operation_status (F03) on the durable records that curated native runs retained.

Each --root is the artifact root of a native run whose report hash is listed in one of the --evidence summaries. The runner
copies the root's operation and admission stores with every artifact they reference, and starts the Host on that copy (never
on the retained root). Per copy: every operation and admission ID read through engineering_operation_status equals its newest
on-disk revision; the status results the native run recorded live (call-*-engineering_operation_status.json) equal the
on-disk revision of the same number; an admission's retained response matches its SHA-256; unknown and malformed IDs refuse;
the reads change no store file; and a second Host started on the same copy returns the same records. A further Host on a
tampered copy must refuse an earlier-revision edit and a missing revision as JournalInvalid and an altered admission response
as ArtifactChanged. No TIA, worker or PLCSIM is started.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import traceback
import uuid

from smoke_mcp import Client

STORES = ("engineering-operations", "engineering-admissions")


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def journals(root):
    """{store: {id: [revision paths in order]}} without staging folders or locks."""
    result = {}
    for store in STORES:
        folder = root / store
        result[store] = {d.name: sorted(d.glob("*.json")) for d in sorted(folder.iterdir()) if d.is_dir() and not d.name.startswith(".")} if folder.is_dir() else {}
    return result


def references(value):
    if isinstance(value, dict):
        if isinstance(value.get("relativePath"), str):
            yield value["relativePath"]
        for item in value.values():
            yield from references(item)
    elif isinstance(value, list):
        for item in value:
            yield from references(item)


def referenced(root):
    return {r for revisions in journals(root).values() for files in revisions.values() for f in files for r in references(load(f))}


def tree(root):
    """Content hash of every store file and referenced artifact; lock files are excluded."""
    files = [p for store in STORES if (root / store).is_dir() for p in (root / store).rglob("*") if p.is_file() and p.suffix != ".lock"]
    files += [root / r for r in referenced(root)]
    return {p.relative_to(root).as_posix(): sha256(p) for p in sorted(set(files))}


def copy(root, destination):
    """The stores plus every artifact their revisions reference, at the same relative paths."""
    for store in STORES:
        if (root / store).is_dir():
            shutil.copytree(root / store, destination / store, ignore=shutil.ignore_patterns("*.lock"))
    paths = referenced(root)
    for relative in sorted(paths):
        source = root / relative
        require(source.is_file(), "Referenced artifact is missing in the retained root: %s" % source)
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    return len(paths)


class Run:
    def __init__(self, args):
        self.args = args
        self.directory = args.output.resolve() / ("os_" + uuid.uuid4().hex[:8])
        self.directory.mkdir(parents=True)
        self.report = {"schemaVersion": 1, "profile": "F03 engineering_operation_status on retained native operation stores", "completed": False,
                       "host": {"path": str(args.host.resolve()), "sha256": sha256(args.host)}, "runner": {"path": __file__, "sha256": sha256(Path(__file__))},
                       "roots": [], "steps": [], "hostExits": []}
        self.cited = {}
        for path in args.evidence:
            summary = load(path)
            for run in summary["runs"]:
                self.cited[run["sha256"]] = summary["evidenceId"]

    def persist(self):
        (self.directory / "verification.json").write_text(json.dumps(self.report, indent=2), encoding="utf-8")

    def stage(self, name, operation):
        row = {"name": name, "status": "invoking"}
        self.report["steps"].append(row)
        print("START " + name, flush=True)
        try:
            row.update(status="passed", result=operation())
            print("PASS " + name, flush=True)
            return row["result"]
        except Exception as error:
            row.update(status="failed", failure=str(error))
            raise
        finally:
            self.persist()

    def host(self, root):
        missing = self.directory / "worker-not-used.exe"
        client = Client(self.args.host.resolve(), self.args.timeout, ["--worker", str(missing), "--artifacts", str(root), "--sim-worker", str(missing)])
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "operation-status-qualification", "version": "1"}})
        client.notify("notifications/initialized")
        return client

    def close(self, client):
        receipt = client.close()
        self.report["hostExits"].append(receipt)
        require(receipt["hostExitCode"] == 0 and not receipt["forcedHostStop"], "Host did not exit cleanly: %s" % receipt)

    @staticmethod
    def error(client, arguments):
        result = client.request("tools/call", {"name": "engineering_operation_status", "arguments": arguments})
        require(result.get("isError") is True, "engineering_operation_status accepted %s" % arguments)
        return ((result.get("structuredContent") or {}).get("error") or {}).get("code")

    def records(self, client, root):
        """Every journal ID read through the tool; each must equal its newest on-disk revision."""
        observed = {}
        for store, ids in journals(root).items():
            for identifier, files in ids.items():
                status = client.tool("engineering_operation_status", {"operationId": identifier})
                newest = load(files[-1])
                require(status == newest, "%s %s differs from its newest revision: %s" % (store, identifier,
                        sorted(k for k in set(status) | set(newest) if status.get(k) != newest.get(k))))
                observed[identifier] = status
        return observed

    def root(self, retained):
        report = next((retained / n for n in ("verification.json", "qualification.json") if (retained / n).is_file()), None)
        require(report is not None and sha256(report) in self.cited, "%s is not the root of a cited evidence run" % retained)
        label = retained.name
        work = self.directory / label
        referenced = self.stage("copy %s (%s)" % (label, self.cited[sha256(report)]), lambda: {"referencedArtifacts": copy(retained, work)})
        ids = journals(work)
        require(any(ids.values()), "%s retained no operation or admission" % retained)
        row = {"root": str(retained), "evidenceId": self.cited[sha256(report)], "report": report.name, "reportSha256": sha256(report), **referenced,
               "operations": {}, "admissions": {}, "liveReads": 0}
        self.report["roots"].append(row)
        before = tree(work)
        first = self.host(work)
        try:
            observed = {}
            self.stage("%s: every record equals its newest revision" % label,
                       lambda: {k: v["state"] for k, v in observed.update(self.records(first, work)) or observed.items()})
            for identifier, files in ids["engineering-operations"].items():
                newest = load(files[-1])
                row["operations"][identifier] = {"state": newest["state"], "revisions": len(files), "checkpoints": len(newest.get("checkpoints") or []),
                                                 "recoveryDestinations": len(newest.get("recoveryDestinations") or [])}
            for identifier, files in ids["engineering-admissions"].items():
                newest = load(files[-1])
                row["admissions"][identifier] = {"operation": newest["scope"]["operation"], "state": newest["state"], "revisions": len(files)}

            def responses():
                checked = 0
                for identifier, files in ids["engineering-admissions"].items():
                    response = load(files[-1]).get("response")
                    require(response is None or sha256(work / response["relativePath"]) == response["sha256"], "Admission %s response hash differs" % identifier)
                    require(load(files[-1])["state"] not in ("dispatchIntent", "outcomeUnknown"), "Admission %s is still unresolved" % identifier)
                    checked += response is not None
                return {"responses": checked}
            self.stage("%s: admission responses match and none is unresolved" % label, responses)

            def live():
                reads = sorted(retained.glob("call-*-engineering_operation_status.json"))
                for path in reads:
                    recorded = load(path)
                    require(recorded.get("isError") is False, "%s recorded an error" % path.name)
                    value = recorded["structuredContent"]
                    files = ids["engineering-operations"].get(value["id"]) or ids["engineering-admissions"][value["id"]]
                    require(value == load(files[value["revision"]]), "%s differs from revision %d" % (path.name, value["revision"]))
                row["liveReads"] = len(reads)
                return {"liveReads": len(reads), "states": sorted({load(p)["structuredContent"]["state"] for p in reads})}
            self.stage("%s: live native-run status reads equal their revisions" % label, live)

            self.stage("%s: unknown and malformed IDs refuse" % label, lambda: {
                "unknown": self.require_code(first, uuid.uuid4().hex, "OperationNotFound"),
                "uppercase": self.require_code(first, next(iter(observed)).upper(), "InvalidRequest"),
                "traversal": self.require_code(first, "..\\engineering-admissions", "InvalidRequest")})
            second = self.host(work)
            try:
                self.stage("%s: a cooperating Host on the same store returns the same records" % label,
                           lambda: require(self.records(second, work) == observed, "Second Host differs") or {"records": len(observed)})
            finally:
                self.close(second)
        finally:
            self.close(first)
        self.stage("%s: status reads changed no store file" % label, lambda: require(tree(work) == before, "Status reads changed the store") or {"files": len(before)})
        return work, ids

    def require_code(self, client, identifier, code):
        observed = self.error(client, {"operationId": identifier})
        require(observed == code, "Expected %s for %r, observed %s" % (code, identifier, observed))
        return observed

    def tampered(self, work, ids):
        """Edited, missing and altered-response journals on a separate copy refuse with typed codes."""
        bad = self.directory / (work.name + "-tampered")
        shutil.copytree(work, bad)
        cases = []
        operation = next(((i, f) for i, f in ids["engineering-operations"].items() if len(f) >= 3), None)
        if operation:
            identifier, files = operation
            first = bad / files[0].relative_to(work)
            record = load(first); record["idempotencyKey"] += "-edited"
            first.write_text(json.dumps(record), encoding="utf-8")
            cases.append((identifier, "JournalInvalid", "edited first revision"))
        missing = next(((i, f) for i, f in ids["engineering-operations"].items() if len(f) >= 3 and (not operation or i != operation[0])), None)
        if missing:
            (bad / missing[1][1].relative_to(work)).unlink()
            cases.append((missing[0], "JournalInvalid", "missing middle revision"))
        admission = next(((i, load(f[-1])["response"]) for i, f in ids["engineering-admissions"].items() if load(f[-1]).get("response")), None)
        if admission:
            target = bad / admission[1]["relativePath"]
            target.write_bytes(target.read_bytes() + b" ")
            cases.append((admission[0], "ArtifactChanged", "altered admission response"))
        require(cases, "No tamper case applies to %s" % work.name)
        client = self.host(bad)
        try:
            return self.stage("%s: tampered journals refuse" % work.name, lambda: {label: self.require_code(client, identifier, code) for identifier, code, label in cases})
        finally:
            self.close(client)

    def run(self):
        try:
            copies = [self.root(Path(r).resolve()) for r in self.args.root]
            for work, ids in copies:
                if any(len(f) >= 3 for f in ids["engineering-operations"].values()) or any(ids["engineering-admissions"].values()):
                    self.tampered(work, ids)
            states = {v["state"] for r in self.report["roots"] for v in r["operations"].values()}
            operations = {v["operation"] for r in self.report["roots"] for v in r["admissions"].values()}
            self.report["coverage"] = {"operationStates": sorted(states), "admissionOperations": sorted(operations),
                                       "liveReads": sum(r["liveReads"] for r in self.report["roots"])}
            self.report["completed"] = True
        except Exception as error:
            self.report["failure"] = str(error)
            self.report["traceback"] = traceback.format_exc()
            raise
        finally:
            self.persist()
            print("Report: " + str(self.directory / "verification.json"), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--root", action="append", required=True, help="Artifact root of a cited native run.")
    parser.add_argument("--evidence", type=Path, action="append", required=True, help="Curated evidence summary citing the roots' reports.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=120)
    Run(parser.parse_args()).run()
