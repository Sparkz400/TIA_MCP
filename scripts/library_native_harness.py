"""Shared MCP steps for native library qualifiers that run only in owned TIA sessions and scratch projects."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import traceback
import uuid

from smoke_mcp import Client


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def identity(version):
    for key in ("typeGuid", "versionGuid"):
        require(uuid.UUID(version[key]).int != 0, "Native identity is empty")
    return {key: version[key] for key in ("typeGuid", "versionGuid")}


class Harness:
    def __init__(self, args, prefix, profile, limitations, inputs):
        require(args.run_native, "Explicit --run-native is required")
        self.directory = args.artifacts.resolve() / (prefix + uuid.uuid4().hex[:8])
        scratch = "TiaMcpScratch_20000101_000000_12345678"
        require(len(str(self.directory / "scratch" / scratch / scratch)) <= 143,
                "Choose a shorter --artifacts path (for example artifacts/lq)")
        self.directory.mkdir(parents=True, exist_ok=False)
        self.args = args
        self.client = None
        self.report = {"schemaVersion": 1, "profile": profile, "completed": False, "runtimeVerified": False,
                       "steps": [], "mutations": [], "limitations": limitations}
        for name, path in (("host", args.host), ("worker", args.worker), ("runner", Path(args.runner))):
            self.report[name] = {"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        self.report["inputDocuments"] = [{"name": p.name, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in inputs]

    def persist(self):
        if self.client is not None:
            self.report["toolCalls"] = self.client.tool_calls
        (self.directory / "qualification.json").write_text(json.dumps(self.report, indent=2), encoding="utf-8")

    def run(self, body):
        try:
            self.client = Client(self.args.host.resolve(), self.args.timeout, ["--worker", str(self.args.worker.resolve()),
                                 "--artifacts", str(self.directory), "--sim-worker", str(self.directory / "sim-not-used.exe")])
            self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                "clientInfo": {"name": "native-library-qualification", "version": "1"}})
            self.client.notify("notifications/initialized")
            body(self)
            self.report["completed"] = True
        except Exception as error:
            self.report["failure"] = str(error)
            self.report["traceback"] = traceback.format_exc()
            raise
        finally:
            if self.client is not None:
                try:
                    self.client.tool("tia_detach")
                except Exception as error:
                    self.report["detachFailure"] = str(error)
                self.client.close()
                self.report["hostExitCode"] = self.client.process.returncode
                self.report["hostLogs"] = self.client.errors
            self.persist()
            print("Report: " + str(self.directory / "qualification.json"), flush=True)

    def tool(self, name, arguments=None):
        return self.client.tool(name, arguments)

    def stage(self, name, operation):
        row = {"name": name, "status": "invoking"}
        self.report["steps"].append(row)
        self.persist()
        print("START " + name, flush=True)
        try:
            result = operation()
            row.update(status="passed", result=result)
            print("PASS " + name, flush=True)
            return result
        except Exception as error:
            row.update(status="failed", failure=str(error))
            raise
        finally:
            self.persist()

    def prepare(self, kind, payload):
        return self.stage("prepare " + kind, lambda: self.tool("change_prepare", {"kind": kind, "payload": payload}))

    @staticmethod
    def apply_args(prepared):
        return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
                "idempotencyKey": "libqualify-" + uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}

    def apply_prepared(self, kind, prepared):
        dispatch = {"kind": kind, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        self.report["mutations"].append(dispatch)
        self.persist()
        result = self.stage("apply " + kind, lambda: self.tool("change_apply", self.apply_args(prepared)))
        require(result["state"] == "applied", "Native change did not reach applied")
        dispatch["state"] = "applied"
        self.persist()
        return result

    def apply(self, kind, payload):
        return self.apply_prepared(kind, self.prepare(kind, payload))

    def call_error(self, name, arguments):
        """Return the structured error of a tool call that must fail."""
        result = self.client.request("tools/call", {"name": name, "arguments": arguments})
        require(result.get("isError") is True, name + " unexpectedly succeeded")
        return (result.get("structuredContent") or {}).get("error") or {}

    def inventory(self):
        """Blocks, PLC data types and tag tables of every PLC (project_inventory lists blocks only)."""
        plcs = [p["path"] for p in self.tool("project_inventory", {"maxItems": 1000})["plcs"]]
        result = {}
        for plc in plcs:
            software = self.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})
            result[plc] = {key: sorted((x["path"], x.get("number")) for x in software.get(key, [])) for key in ("blocks", "types", "tagTables")}
        return result

    def refuse(self, label, kind, payload, code, text=None, project=True):
        """A refused preparation must change no block, type or tag table of any PLC."""
        def check():
            inventory = self.inventory if project else dict
            before = inventory()
            error = self.call_error("change_prepare", {"kind": kind, "payload": payload})
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(text is None or text in (error.get("message") or ""), "Refusal message lacks " + str(text))
            require(before == inventory(), "Refused preparation changed the native inventory")
            return error
        return self.stage(label, check)

    def unresolved_admissions(self):
        """Shared mutation admissions of this run still owning resources (dispatchIntent or outcomeUnknown)."""
        rows = []
        for folder in sorted((self.directory / "engineering-admissions").glob("[0-9a-f]*")):
            latest = sorted(folder.glob("*.json"))[-1]
            record = json.loads(latest.read_text(encoding="utf-8"))
            if record.get("state") in ("dispatchIntent", "outcomeUnknown"):
                rows.append(record["id"])
        return rows

    def reconcile_admission(self, label, admission_id=None):
        """Release a failed or interrupted mutation's admission through its fresh-session native verification."""
        def check():
            unresolved = self.unresolved_admissions()
            target = admission_id or (unresolved[0] if len(unresolved) == 1 else None)
            require(target in unresolved, "Expected exactly one unresolved admission to reconcile: %s" % unresolved)
            result = self.tool("engineering_admission_reconcile", {"admissionId": target})
            require(result.get("released") is True and result.get("automaticReplay") is False,
                    "Admission was held: %s" % result.get("reasons"))
            return result
        return self.stage(label, check)

    def owned_portal(self, label):
        self.stage("start owned TIA " + label, lambda: self.tool("tia_start"))
        status = self.tool("tia_session_status")
        require(status.get("ownsPortalProcess") is True and not status.get("projects"), "Session must be owned and empty")

    def owned_scratch(self, label):
        self.owned_portal(label)
        created = self.stage("create owned scratch " + label, lambda: self.tool("tia_create_scratch"))
        require(created.get("completed") is True and created.get("compile", {}).get("errorCount") == 0, "Scratch compile failed")
        self.scratch_cpu = created["selectedCpu"]["typeIdentifier"]
        inventory = self.tool("project_inventory", {"maxItems": 1000})
        require(len(inventory.get("plcs", [])) == 1, "Expected one owned PLC")
        return inventory["plcs"][0]["path"]

    def project_library(self):
        return self.tool("library_open", {"request": {"kind": "project"}})["libraryId"]

    def source_ref(self, identifier):
        matches = [x for x in self.tool("library_list")["libraries"] if x["libraryId"] == identifier]
        require(len(matches) == 1, "Library identity no longer resolves")
        return {"libraryId": identifier, "revision": matches[0]["revision"]}

    def query(self, identifier, **selector):
        return self.tool("library_query", {"request": {"libraryId": identifier, "maxItems": 200, **selector}})

    def versions(self, identifier, name):
        result = self.query(identifier)
        require(result["completeness"]["scopeComplete"] and result.get("nextOffset") is None, "Type metadata incomplete")
        return [x for x in result["items"] if x.get("kind") == "version" and x.get("typeName") == name]

    def selected(self, identifier, versions, **extra):
        return {"schemaVersion": 1, "source": self.source_ref(identifier), "selection": [identity(v) for v in versions], **extra}

    def verify_use(self, identifier, version, expected):
        result = self.query(identifier, query="uses", **identity(version))
        require(result["completeness"]["scopeComplete"] and result.get("nextOffset") is None, "Native instance coverage incomplete")
        require(len(result["items"]) == expected,
                "Native exact-version use count differs: expected %d, observed %d" % (expected, len(result["items"])))
        for row in result["items"]:
            require(identity(row["version"]) == identity(version), "Native use GUID differs")
        return result

    def create_type(self, library, documents, name, composition, plc):
        self.apply("library_create_type", {"schemaVersion": 1, "destinationLibraryId": library, "documentDirectory": str(documents),
                   "documentName": name, "targetEnvironment": "selectedPlc", "destinationComposition": composition, "target": {"plcPath": plc}})
        created = self.versions(library, name)
        require(len(created) == 1 and created[0]["state"] == "InWork", "New type must expose one native InWork version")
        return created[0]

    def release(self, library, version, number):
        applied = self.apply("library_release_version", self.selected(library, [version], versionNumber=number,
                             author="TIA MCP qualification", comment="Owned fixture release " + number))
        returned = applied["journal"]["result"]["objects"]
        require(len(returned) == 1 and returned[0]["typeGuid"] == version["typeGuid"], "Release changed the owning native type")
        committed = [x for x in self.versions(library, version["typeName"]) if identity(x) == identity(returned[0])]
        require(len(committed) == 1 and committed[0]["state"] == "Committed" and committed[0]["version"] == number,
                "Native release state/version/GUID differs")
        return committed[0]

    # Siemens refuses replacing an edited version's test instance by source generation, so a content
    # change creates the next version from documents. Which meta files must accompany authored SCL is
    # not documented; layouts are tried in this order and the first accepted one is reused.
    DOCUMENT_LAYOUTS = ("authored+libinfo", "authored+libinfo+libint", "authored")

    def export_documents(self, library, version):
        rows = [x for x in self.query(library)["items"] if x.get("kind") == "type" and x.get("typeGuid") == version["typeGuid"]]
        require(len(rows) == 1 and rows[0]["supportedExportFormats"], "No native export format")
        formats = rows[0]["supportedExportFormats"]
        preferred = next((x for x in formats if x.lower() in ("simaticml", "xml", ".xml")), formats[0])
        result = self.query(library, query="export", format=preferred, **identity(version))
        require(result["files"] and not result["filesTruncated"], "Native export is empty or truncated")
        for artifact in result["files"]:
            require(hashlib.sha256(Path(artifact["path"]).read_bytes()).hexdigest() == artifact["sha256"], "Native export hash differs")
        return result

    def edit_source(self, library, plc, previous, block, source, composition="blocks", extension=".scl"):
        """Create the next InWork version of a released type from authored source; returns that version."""
        exported = self.export_documents(library, previous)
        files = {Path(a["path"]).name: Path(a["path"]) for a in exported["files"]}
        stem = exported["documentName"]
        attempts = self.report.setdefault("documentVersionAttempts", [])
        layouts = [self.document_layout] if getattr(self, "document_layout", None) else self.DOCUMENT_LAYOUTS
        for layout in layouts:
            folder = self.directory / "version-documents" / uuid.uuid4().hex[:8]
            folder.mkdir(parents=True)
            (folder / (stem + extension)).write_text(source, encoding="utf-8")
            for meta in (".libinfo", ".libint"):
                if ("+" + meta[1:]) in layout and stem + meta in files:
                    (folder / (stem + meta)).write_bytes(files[stem + meta].read_bytes())
            prepared = self.prepare("library_edit_version", self.selected(library, [previous], documentDirectory=str(folder), documentName=stem,
                                    destinationComposition=composition, target={"plcPath": plc}))
            result = self.client.request("tools/call", {"name": "change_apply", "arguments": self.apply_args(prepared)})
            content = result.get("structuredContent") or {}
            row = {"layout": layout, "changeId": prepared["changeId"], "accepted": not result.get("isError"), "error": content.get("error")}
            attempts.append(row)
            self.persist()
            in_work = [x for x in self.versions(library, previous["typeName"]) if x["state"] == "InWork"]
            if row["accepted"]:
                require(len(in_work) == 1 and in_work[0]["versionGuid"] != previous["versionGuid"], "Document version is not one distinct InWork version")
                row["transfer"] = {k: content["journal"]["result"].get(k) for k in ("transferState", "transferMessages")}
                self.document_layout = layout
                self.persist()
                return in_work[0]
            for leftover in in_work:  # A rejected layout must not block the next attempt.
                self.apply("library_discard_version", self.selected(library, [leftover]))
        raise AssertionError("No document layout created a version: %s" % attempts[-len(layouts):])

    def archive(self, identifier):
        result = self.apply("library_archive", {"schemaVersion": 1, "source": self.source_ref(identifier)})["journal"]["result"]["archive"]
        require(hashlib.sha256(Path(result["path"]).read_bytes()).hexdigest() == result["sha256"], "Native archive hash differs")
        return result

    def open_archive(self, artifact):
        staged = self.tool("library_package_stage", {"hostPath": artifact["path"]})
        candidates = self.tool("library_package_inspect", {"packageId": staged["packageId"]})["items"]
        require(len(candidates) == 1 and candidates[0]["kind"] == "globalLibraryArchive", "Archive candidate differs")
        return self.stage("open staged archive", lambda: self.tool("library_open", {"request": {
            "packageId": staged["packageId"], "candidateId": candidates[0]["candidateId"]}}))["libraryId"]
