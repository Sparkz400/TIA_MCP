"""Qualify library create, copy, instantiate and update with know-how protected blocks, and the exact refusals where V21 cannot read protected content."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil

from library_native_harness import Harness, require

ROOT = Path(__file__).resolve().parents[1]
FB, PLAIN = "TiaMcpLibraryGate", "TiaMcpProtectedPlain"
FIXTURES = ROOT / "fixtures/libraries/protected"
# Typed refusals the worker may raise where Openness cannot read protected content; anything else fails the run.
PROTECTED_REFUSALS = ("ProtectedObject", "LibraryContentFingerprintUnavailable", "FingerprintUnavailable", "LibraryUpdateCleanupRisk")


def consumer_source(callees):
    """Main keeps the scratch call and calls one instance DB of each protected FB that reached the consumer."""
    dbs = "".join('DATA_BLOCK "%s_DB"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"%s"\nBEGIN\nEND_DATA_BLOCK\n\n' % (n, n) for n in callees)
    calls = "".join('    "%s_DB"(Enable := TRUE);\n' % n for n in callees)
    return (dbs + 'ORGANIZATION_BLOCK "Main"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nBEGIN\n'
            + '    "TiaMcp_EnableGate_DB"(Enable := TRUE);\n' + calls + "END_ORGANIZATION_BLOCK\n")


def body(h):
    documents = h.directory / "documents"
    shutil.copytree(ROOT / "fixtures/libraries/type-lifecycle", documents)
    findings = h.report.setdefault("findings", {})

    def blocks(plc):
        return {x["name"]: x for x in h.tool("software_inspect", {"plcPath": plc, "maxItems": 2000})["blocks"]}

    def protected(plc, name):
        row = blocks(plc)[name]
        require(row["isKnowHowProtected"] is True, name + " is not know-how protected")
        return row

    def refused_read(plc, name):
        error = h.call_error("block_read", {"blockPath": blocks(plc)[name]["path"]})
        require(error.get("code") == "ProtectedObject", "block_read did not refuse the protected block: %s" % error)
        return error

    # Authoring: one plain protected FB (master copy route) and one protected type test instance (type route).
    plc = h.owned_scratch("authoring")
    library = h.project_library()
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": (FIXTURES / (PLAIN + ".scl")).read_text(encoding="utf-8"), "expectedBlockNames": [PLAIN]})
    h.seed("protect plain FB", "protect", PLAIN)
    h.stage("plain FB is protected", lambda: protected(plc, PLAIN))
    h.stage("block_read refuses the protected FB", lambda: refused_read(plc, PLAIN))
    global_library = h.apply("library_create", {"schemaVersion": 1, "name": "ProtectedQualification"})["journal"]["result"]["library"]["libraryId"]
    findings["masterCopyCreate"] = h.apply_or_refuse("master copy of the protected FB", "library_create_master_copy", {
        "schemaVersion": 1, "destinationLibraryId": global_library, "target": {"plcPath": plc},
        "object": {"kind": "block", "objectPath": blocks(plc)[PLAIN]["path"]}}, PROTECTED_REFUSALS)

    in_work = h.create_type(library, documents, FB, "blocks", plc)
    protection = h.seed("protect the in-work test instance", "protect", FB, required=False)
    findings["testInstanceProtection"] = protection
    released = None
    if protection.get("completed"):
        h.stage("test instance is protected", lambda: protected(plc, FB))
        release = h.apply_or_refuse("release the protected in-work version", "library_release_version", h.selected(
            library, [in_work], versionNumber="1.0.0", author="TIA MCP qualification", comment="Protected fixture release 1.0.0"), PROTECTED_REFUSALS)
        findings["protectedRelease"] = release
        if release["outcome"] == "applied":
            committed = [x for x in h.versions(library, FB) if x["state"] == "Committed"]
            require(len(committed) == 1 and committed[0]["version"] == "1.0.0", "Protected release readback differs")
            released = committed[0]

    def export_probe(version):
        """Read-only: record what native version export returns for protected content."""
        rows = [x for x in h.query(library)["items"] if x.get("kind") == "type" and x.get("typeGuid") == version["typeGuid"]]
        formats = rows[0]["supportedExportFormats"] if rows else []
        result = h.client.request("tools/call", {"name": "library_query", "arguments": {"request": {
            "libraryId": library, "maxItems": 200, "query": "export", "format": formats[0] if formats else "SimaticML",
            "typeGuid": version["typeGuid"], "versionGuid": version["versionGuid"]}}})
        content = result.get("structuredContent") or {}
        return {"formats": formats, "isError": bool(result.get("isError")), "error": content.get("error"), "files": content.get("files")}

    generations = {}
    if released:
        findings["protectedVersionExport"] = h.stage("probe export of the protected version", lambda: export_probe(released))
        generations["1.0.0"] = released
        publish = h.apply_or_refuse("publish protected 1.0.0", "library_publish", h.selected(library, [released], destinationLibraryId=global_library), PROTECTED_REFUSALS)
        findings["protectedPublish"] = publish
        if publish["outcome"] == "applied":
            gen_one = h.archive(global_library)
            # A metadata-only next version from the protected test instance: the password is gone, so the content stays.
            h.apply("library_edit_version", h.selected(library, [released], testInstanceName=FB, target={"plcPath": plc}))
            second = [x for x in h.versions(library, FB) if x["state"] == "InWork"]
            require(len(second) == 1, "Edit did not create one InWork version")
            h.stage("edited test instance stays protected", lambda: protected(plc, FB))
            second = h.release(library, second[0], "1.1.0")
            h.apply("library_set_default", h.selected(library, [second]))
            h.apply("library_publish", h.selected(library, [second], destinationLibraryId=global_library))
            h.apply("library_set_default", h.selected(global_library, [second]))
            generations.update({"1.1.0": second, "archive1": gen_one, "archive2": h.archive(global_library)})
    masters = [x for x in h.query(global_library)["items"] if x.get("kind") == "masterCopy"]
    master_archive = h.archive(global_library) if masters and "archive2" not in generations else generations.get("archive2")
    h.tool("library_close", {"libraryId": global_library})
    h.stage("close authoring session", lambda: h.tool("tia_detach"))

    # Consumer: instantiate the protected contents, call them from Main, then update the protected type.
    if not masters and "archive1" not in generations:
        h.report["consumerSkipped"] = "No protected master copy or type version could be created."
        return
    plc = h.owned_scratch("consumer")
    if masters:
        source = h.open_archive(master_archive)
        h.apply("library_instantiate", {"schemaVersion": 1, "source": h.source_ref(source), "destinationComposition": "blocks",
                "selection": [{"masterCopyPath": masters[0]["masterCopyPath"]}], "target": {"plcPath": plc}})
        h.stage("instantiated master copy stays protected", lambda: protected(plc, PLAIN))
        h.stage("block_read refuses the instantiated master copy", lambda: refused_read(plc, PLAIN))
        h.tool("library_close", {"libraryId": source})
    if "archive1" in generations:
        source = h.open_archive(generations["archive1"])
        h.apply("library_instantiate", h.selected(source, [generations["1.0.0"]], target={"plcPath": plc}))
        h.stage("instantiated type stays protected", lambda: protected(plc, FB))
        h.stage("protected FB 1.0.0 used once", lambda: h.verify_use(h.project_library(), generations["1.0.0"], 1))
        h.tool("library_close", {"libraryId": source})
    callees = ([PLAIN] if masters else []) + ([FB] if "archive1" in generations else [])
    calls = consumer_source(callees)
    h.apply("source_upsert", {"scopePath": plc, "language": "SCL", "source": calls, "expectedBlockNames": [n + "_DB" for n in callees] + ["Main"]})
    h.report["consumerSource"] = calls

    if "archive2" in generations:
        source = h.open_archive(generations["archive2"])
        update = h.apply_or_refuse("update the protected FB to 1.1.0", "library_update_project", h.selected(
            source, [generations["1.1.0"]], updateScope="entireSelectedPlc", target={"plcPath": plc}), PROTECTED_REFUSALS)
        findings["protectedUpdate"] = update
        expected = generations["1.1.0" if update["outcome"] == "applied" else "1.0.0"]
        h.stage("FB at the expected version after the update", lambda: h.verify_use(h.project_library(), expected, 1))
        h.stage("updated FB stays protected", lambda: protected(plc, FB))
        h.tool("library_close", {"libraryId": source})
    project_path = h.tool("tia_session_status")["projects"][0]["path"]
    h.stage("close consumer session", lambda: h.tool("tia_detach"))

    h.owned_portal("reopen")
    h.stage("reopen consumer", lambda: h.reopen_project(project_path))
    for name in callees:
        h.stage("reopened: %s still protected" % name, lambda n=name: protected(plc, n))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--unit-seeder", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "pr_", "V21 library operations with know-how protected blocks (plain FB master copy and FB type)", [
        "Protection is applied by scripts/native/LibraryUnitSeeder (protect mode) with a random password that is never stored.",
        "V21 Openness has no know-how protection service for PLC data types; protected UDTs are not exercised.",
        "The 1.1.0 type version is a metadata-only release of the protected test instance; protected content cannot be edited without the password.",
        "No runtime execution."], [args.unit_seeder, FIXTURES / (PLAIN + ".scl")]).run(body)
