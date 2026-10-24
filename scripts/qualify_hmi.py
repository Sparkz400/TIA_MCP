"""Qualify HMI inspection against independent native fixture archives.

Uses exact host/worker binaries and only retrieves seeder-owned archives into
new owned copies. Native stages never replay writes. A report's observed or
blocked result is never counted as a passed acceptance gate.
"""
from __future__ import annotations

import argparse
from collections import Counter
import copy
import datetime
import hashlib
import json
from pathlib import Path
import uuid
from xml.dom import Node, minidom
import xml.etree.ElementTree as ET

from probe_hmi_api import PROFILES
from smoke_mcp import Client, ROOT
from block_test_process import OwnedTiaProcessWatcher, ProcessExitNotVerified
from hmi_fixture_copy import copy_project, safe_path, verify_source_preserved

GATES = ("nativeFixture", "deviceDiscovery", "inspectionAccuracy", "explorerIntegration", "duplicateNames", "brokenReferences", "dynamicReferences", "scriptBehaviorInspection", "alarmTagReferences",
         "missingPrerequisites", "protectedContent", "wrongFamily", "staleTarget", "boundedInspection",
         "integratedConnectionMapping", "plcDeclarationMapping", "dbDeclarationMapping", "screenTagPlcChain", "scalarChange", "tagCreation", "itemProperties", "placement", "explicitBinding",
         "unrelatedPreservation", "compile", "cleanBaselineCompile", "reopenedCompile", "recoveredCompile", "saveReopen", "archiveRecovery", "interruptedOperation", "visualInspection")
SECTIONS = ("connections", "tags", "screens", "alarms", "scripts", "resources")
NUMERIC_SCALARS = {"Left", "Top", "Width", "Height", "TabIndex", "ScreenNumber", "Number", "Index", "MaxLength", "Timeout", "UpdateId"}


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def gate(status="unverified", reason="Not exercised in this run", **evidence):
    require(status in {"passed", "blocked", "failed", "unverified"}, "Invalid acceptance gate status")
    return {"status": status, "reason": reason, **evidence}


def summarize_gates(gates):
    require(set(GATES).issubset(gates), "Every required gate must be represented")
    remaining = [key for key in GATES if gates[key]["status"] != "passed"]
    return {"accepted": not remaining, "remainingAcceptanceCriteria": remaining,
            "passed": sum(gates[key]["status"] == "passed" for key in GATES), "total": len(GATES)}


def native_payload(data: bytes):
    """Narrow observation comparison: remove only exporter wall-clock metadata.

    Object IDs, links, unknown nodes, translations and payload order remain.
    This is evidence comparison, not a mutation authorization policy.
    """
    require(len(data) <= 32 * 1024 * 1024, "Native export exceeds preservation bound")
    document = minidom.parseString(data)
    require(document.doctype is None, "DTD is not permitted in native preservation evidence")
    root = document.documentElement
    require(root.tagName == "Document" and root.namespaceURI is None, "Expected native Classic export document")
    infos = [node for node in root.childNodes if node.nodeType == Node.ELEMENT_NODE and node.tagName == "DocumentInfo"]
    require(len(infos) <= 1, "Ambiguous native DocumentInfo metadata")
    created = [node for info in infos for node in info.childNodes if node.nodeType == Node.ELEMENT_NODE and node.tagName == "Created"]
    require(len(created) <= 1, "Ambiguous native Created metadata")
    for node in created:
        require(not node.hasAttributes() and len(node.childNodes) == 1 and node.firstChild.nodeType == Node.TEXT_NODE,
                "Unexpected payload inside native Created metadata")
        try:
            datetime.datetime.fromisoformat(node.firstChild.data.replace("Z", "+00:00"))
        except ValueError as error:
            raise AssertionError("Unrecognized native export timestamp") from error
        node.firstChild.data = "export-time-metadata"
    # Keep comments and processing instructions, including siblings of the root.
    # The earlier ElementTree parse silently discarded them before comparison.
    return document.toxml()


def native_compile_gate(profile, field):
    results = profile.get(field)
    if not results:
        return gate("unverified", "No native HMI compile evidence for " + field)
    owners = profile.get("nativeBefore", [])
    expected = Counter(owner.get("name") for owner in owners)
    valid_owners = bool(owners) and all(str(owner.get("type", "")).startswith(("Siemens.Engineering.Hmi.", "Siemens.Engineering.HmiUnified.")) for owner in owners)
    exact_owners = valid_owners and Counter(result.get("name") for result in results) == expected
    native_targets = all(str(result.get("compileTargetType", "")).startswith(("Siemens.Engineering.Hmi.", "Siemens.Engineering.HmiUnified.", "Siemens.Engineering.HW.Device")) for result in results)
    def inspect_messages(messages, explicit_complete, depth=0):
        if not isinstance(messages, list):
            return False, []
        # Older independent seeders silently took 500 siblings. A saturated
        # legacy list cannot establish completeness; retain its original report.
        if depth >= 32 and messages:
            return False, []
        valid, errors = explicit_complete is True or len(messages) < 500, []
        for message in messages:
            if not isinstance(message, dict) or message.get("diagnosticCaptureIncomplete") is True or message.get("truncated") is True or message.get("state") not in {"Success", "Information", "Warning", "Error"}:
                valid = False
                continue
            if message["state"] == "Error":
                errors.append(message.get("description", ""))
            child_valid, child_errors = inspect_messages(message.get("children"), explicit_complete, depth + 1)
            valid = valid and child_valid
            errors.extend(child_errors)
        return valid, errors

    diagnostic_rows = [{"name": result.get("name"), "complete": valid and result.get("messagesComplete", True) is True, "errors": errors}
                       for result in results for valid, errors in [inspect_messages(result.get("messages"), result.get("messagesComplete"))]]
    clean_diagnostics = all(row["complete"] and not row["errors"] for row in diagnostic_rows)
    successful = exact_owners and native_targets and clean_diagnostics and all("error" not in result and type(result.get("errorCount")) is int and result["errorCount"] == 0
        and result.get("state") in {"Success", "Information", "Warning"} for result in results)
    return gate("passed" if successful else "failed", "Native HMI compile requires every exact HMI owner, successful zero-error summary and a complete diagnostic tree without Error messages",
                results=results, exactOwners=exact_owners, nativeTargets=native_targets, cleanDiagnostics=clean_diagnostics, diagnostics=diagnostic_rows)


def semantic_snapshot(value):
    """Compare independent scalar native readbacks without export locations."""
    if isinstance(value, list):
        return [semantic_snapshot(item) for item in value]
    if isinstance(value, dict):
        return {key: semantic_snapshot(item) for key, item in value.items() if key not in {"export", "remainingBudget"}}
    return value


def validate_fixture(profile, report_path):
    require(profile.get("profile") in PROFILES, "Unknown native profile")
    require(profile.get("selectedTarget", {}).get("typeIdentifier"), "Native catalog identity required")
    archive = Path(profile.get("archive", {}).get("path", "")).resolve()
    require(archive.is_relative_to(report_path.parent.resolve()), "Fixture archive escapes its independent evidence directory")
    require(archive.suffix.lower() == ".zap21" and archive.is_file(), "Native V21 archive is required")
    require(sha256(archive) == profile["archive"]["sha256"], "Native fixture archive hash changed")
    require(Path(profile["projectPath"]).stem.startswith("TiaMcpHmi_"), "Only independent seeder-owned fixtures are eligible")
    require(profile.get("nativeBefore"), "Independent native baseline is required")
    return archive


def validate_owned_derived_project(profile, native, report_path):
    source = native.get("sourceNativeReport", {})
    source_path = Path(source.get("path", "")).resolve()
    require(source_path.is_relative_to(report_path.parent.resolve()) and source_path.is_file()
            and sha256(source_path) == source.get("sha256"), "Derived native source report is missing or changed")
    require(profile.get("fixtureMode") == "positive" and profile.get("archiveRetrievalVerified") is True
            and native_compile_gate(profile, "compileRecovered")["status"] == "passed", "Owned fallback requires independently compiled positive archive retrieval")
    path = Path(profile.get("recoveredProjectPath", "")).resolve()
    require(path.is_relative_to(report_path.parent.resolve()) and path.suffix.lower() == ".ap21"
            and path.stem.startswith("TiaMcpHmi_") and path.is_file(), "Fallback must be the exact independent seeder-owned recovered project")
    return path


def inspection_copy_destination(profile, artifacts):
    project = Path(profile.get("recoveredProjectPath", ""))
    return safe_path(artifacts.absolute()) / ("h_" + uuid.uuid4().hex[:8]) / project.parent.name


def validate_native_copy_directory(destination):
    require(len(str(destination)) <= 143, "Inspection copy exceeds the native project directory limit")


def prepare_owned_inspection_copy(profile, report_path, expected_report_hash, artifacts, receipt, destination=None):
    """Inspection eligibility is separate from clean compile or recovery acceptance."""
    report_path = safe_path(report_path.absolute())
    require(sha256(report_path) == expected_report_hash, "Independent native reference report changed")
    recorded = json.loads(report_path.read_text(encoding="utf-8-sig"))
    require(recorded.get("completed") is True and recorded.get("ownsPortalProcess") is True
            and recorded.get("cleanup") == "owned-portal-disposed", "Copied inspection requires completed owned native fixture disposal")
    require(profile in recorded.get("profiles", []), "Inspection profile must be the exact recorded independent native profile")
    archive = validate_fixture(profile, report_path)
    require(profile.get("archiveRetrievalVerified") is True and profile.get("nativeRecovered")
            and semantic_snapshot(profile.get("nativeBefore")) == semantic_snapshot(profile["nativeRecovered"]),
            "Copied inspection requires independently recovered scalar baseline equality")
    project = safe_path(Path(profile.get("recoveredProjectPath", "")))
    require(project.is_relative_to(report_path.parent) and project.suffix.lower() == ".ap21",
            "Copied inspection requires the exact independently recovered fixture project")
    destination = safe_path(destination) if destination is not None else inspection_copy_destination(profile, artifacts)
    require(not destination.is_relative_to(report_path.parent), "Inspection copy must be outside original evidence directory")
    receipt.update({"nativeReport": {"path": str(report_path), "sha256": expected_report_hash},
                    "sourceArchive": {"path": str(archive), "sha256": profile["archive"]["sha256"]},
                    "scope": "Read-only inspection of copied native fixture; no clean-compile, save/reopen or bridge archive-retrieval acceptance",
                    "nativeCompilePerformed": False, "projectSaveRequested": False, "productionMutations": 0})
    return copy_project(project, destination, receipt)


def verify_copied_project_identity(copied_project, start, opened, status):
    expected = {"name": copied_project.stem, "path": str(copied_project)}
    require(type(start.get("processId")) is int and start.get("ownsPortalProcess") is True and start.get("projects") == [],
            "Copied inspection requires the exact newly owned empty startup session")
    require(opened.get("processId") == start["processId"] and opened.get("opened") is True and opened.get("saved") is False
            and opened.get("project") == expected, "Project-open receipt differs from exact copied project or owned process")
    require(status.get("attached") is True and status.get("ownsPortalProcess") is True and status.get("processId") == start["processId"]
            and status.get("projects") == [expected], "Owned session status does not contain only the exact copied project")


def import_native_evidence(profile):
    gates = {name: gate() for name in GATES}
    if profile.get("status") == "blocked":
        reason = profile.get("reason") or json.dumps(profile.get("error"), ensure_ascii=False)
        for name in GATES:
            gates[name] = gate("blocked", reason, source="native fixture prerequisite")
        return gates
    if profile.get("status") == "failed":
        gates["nativeFixture"] = gate("failed", "Independent fixture generation failed", error=profile.get("error"))
        return gates
    if profile.get("nativeBefore") and profile.get("selectedTarget"):
        gates["nativeFixture"] = gate("passed", "Independent native target and baseline recorded", target=profile["selectedTarget"])
    gates["compile"] = native_compile_gate(profile, "compile")
    for key, field in (("cleanBaselineCompile", "compileBeforeTrial"), ("reopenedCompile", "compileReopened"), ("recoveredCompile", "compileRecovered")):
        gates[key] = native_compile_gate(profile, field)
    if profile.get("fixtureMode") != "positive" or profile.get("deliberateInvalidReferences") is not False:
        gates["cleanBaselineCompile"] = gate("unverified", "A separately labelled positive fixture without deliberate invalid references is required")
    for flag, key, observed in (("reopenVerified", "saveReopen", "nativeReopened"), ("archiveRetrievalVerified", "archiveRecovery", "nativeRecovered")):
        if profile.get(flag):
            equal = semantic_snapshot(profile.get("nativeBefore")) == semantic_snapshot(profile.get(observed))
            gates[key] = gate("passed" if equal else "failed", "Independent scalar baseline comparison after native lifecycle; not whole-content preservation", baselineEqual=equal)
    gates["visualInspection"] = gate("unverified", "No representative native screen appearance assessment is attached to this fixture report. A separate native UI attempt must be completed; no renderer or scripts executed.")
    return gates


def decode(raw):
    return raw.get("structuredContent") or json.loads("\n".join(item["text"] for item in raw.get("content", []) if item.get("type") == "text"))


def blocking_startup_failure(error):
    # Native access denial may be local to one new portal (root-i observed a
    # successful independent next launch). A faulted/unknown host outcome is
    # different: no new request may be sent through that worker.
    return error.get("code") in {"BridgeFaulted", "TimeoutOutcomeUnknown"}


def block_pending_profiles(native_profiles, report_profiles, after_index, failure):
    """Preserve independent native/earlier results; stop a shared failed worker."""
    for profile in native_profiles[after_index + 1:]:
        if not profile.get("archive") or not profile.get("nativeBefore"):
            continue
        result = next(row for row in report_profiles if row["profile"] == profile["profile"])
        result["bridgeBlockedByStartup"] = copy.deepcopy(failure)
        for name, value in list(result["gates"].items()):
            if value["status"] == "unverified":
                result["gates"][name] = gate("blocked", "Shared worker is faulted or startup outcome is unknown; no further fixture calls or write replays were dispatched", sourceFailure=copy.deepcopy(failure))


def native_section_entries(native, section, family):
    # Classic screen inspection spans every screen role; older reports lack the added sections.
    keys = ({"connections": ("Connections",), "tags": ("TagFolder",),
             "screens": ("ScreenFolder", "ScreenTemplateFolder", "ScreenPopupFolder", "ScreenSlideinFolder", "ScreenGlobalElements", "ScreenOverview"),
             "scripts": ("VBScriptFolder",), "resources": ("TextLists", "GraphicLists")} if family == "classic" else
            {"connections": ("Connections",), "tags": ("Tags", "TagTables"), "screens": ("Screens",),
             "scripts": ("Scripts",), "resources": ("HmiTextLists", "HmiGraphicLists"), "alarms": ("DiscreteAlarms", "AnalogAlarms")})
    if section not in keys:
        return None
    allowed = {"connections": {"Connection", "HmiConnection"}, "tags": {"Tag", "TagTable", "HmiTag", "HmiTagTable"},
               "screens": {"Screen", "HmiScreen", "ScreenTemplate", "ScreenPopup", "ScreenSlidein", "ScreenGlobalElements", "ScreenOverview"}, "scripts": {"VBScript", "HmiScriptModule"},
               "resources": {"TextList", "GraphicList", "HmiTextList", "HmiGraphicList"}, "alarms": {"HmiDiscreteAlarm", "HmiAnalogAlarm"}}
    def descend(value):
        if isinstance(value, list):
            for child in value: yield from descend(child)
        elif isinstance(value, dict):
            require(not value.get("error") and not value.get("truncated"), "Independent native section contains errors/truncation")
            native_type = value.get("nativeType", "")
            if native_type.rsplit(".", 1)[-1] in allowed[section]:
                yield value
            for key, child in value.items():
                if key not in {"attributes", "export", "exportError"}: yield from descend(child)
    entries = [item for key in keys[section] for item in descend(native["sections"].get(key))]
    if family == "unified" and section == "tags":
        # HmiSoftware.Tags is the flattened view of TagTables.Tags. Remove
        # only a proved alias with the exact native table scope AND equal
        # attributes, never a repeated name in another scope.
        tables = native["sections"]["TagTables"]
        table_entries = [item for item in descend(tables)]
        flat_entries = [item for item in descend(native["sections"]["Tags"])]
        unmatched = list(flat_entries)
        for table in tables:
            if not isinstance(table, dict):
                continue
            table_name = table.get("attributes", {}).get("Name")
            for child in table.get("Tags", []):
                attrs = child.get("attributes", {})
                if not table_name or attrs.get("TagTableName") != table_name:
                    continue
                aliases = [index for index, flat in enumerate(unmatched)
                           if flat.get("nativeType") == child.get("nativeType")
                           and flat.get("attributes") == attrs]
                if aliases:
                    unmatched.pop(aliases[0])
        entries = table_entries + unmatched
    return entries


def compare_section(native, section, family, observed):
    expected = native_section_entries(native, section, family)
    status = observed["lastPage"]["coverage"]["status"]
    if expected is None:
        require(family == "classic" and section == "alarms" and status == "unsupported", "Only documented Classic alarm omission is expected")
        return {"status": "unsupported", "reason": "Classic alarm composition is not exposed by installed public API"}
    require(status == "complete", "Section cannot pass inspection accuracy with " + status + " coverage: " + section)
    def identity(row, native_row=False):
        return (row.get("nativeType"), row.get("attributes", {}).get("Name") if native_row else row.get("name"))
    require(Counter(identity(row, True) for row in expected) == Counter(identity(row) for row in observed["items"]),
            "Independent native type/name inventory differs for " + section)
    # Match each native row to a distinct observation. An existential check can
    # reuse one correct duplicate-name row and conceal a changed second row.
    groups = {}
    for row in observed["items"]:
        groups.setdefault(identity(row), []).append(row)
    for key in groups:
        originals = [row for row in expected if identity(row, True) == key]
        require(match_native_scalars(originals, groups[key]),
                "Native scalar readback differs for " + section + " / " + str(key))
    return {"status": "passed", "expectedCount": len(expected), "actualCount": len(observed["items"])}


def match_native_scalars(expected, observed):
    """One-to-one matching, allowing different independently exposed attributes.

    Iterative augmenting paths avoid both greedy ambiguity and recursion limits.
    Unknown native getter values remain outside this scalar-only comparison.
    """
    candidates = []
    for row in expected:
        scalars = {key: value for key, value in row.get('attributes', {}).items()
                   if value is None or isinstance(value, (str, bool, float))
                   or key in NUMERIC_SCALARS and isinstance(value, int)}
        candidates.append([index for index, actual in enumerate(observed)
            if all(key in actual.get('properties', {})
                   and isinstance(value, bool) == isinstance(actual['properties'][key], bool)
                   and actual['properties'][key] == value for key, value in scalars.items())])
    left_match, right_match = {}, {}
    for start in range(len(expected)):
        queue, seen_left, parent_right = [start], {start}, {}
        free = None
        for left in queue:
            for right in candidates[left]:
                if right in parent_right:
                    continue
                parent_right[right] = left
                if right not in right_match:
                    free = right
                    break
                other = right_match[right]
                if other not in seen_left:
                    seen_left.add(other); queue.append(other)
            if free is not None:
                break
        if free is None:
            return False
        while free is not None:
            left = parent_right[free]
            previous = left_match.get(left)
            left_match[left] = free; right_match[free] = left
            free = previous
    return len(left_match) == len(observed)


def compare_screen_items(native_screen, family, observed):
    coverage = observed["lastPage"]["coverage"]
    limitations = []
    if coverage["status"] != "complete":
        # A deliberately broken native tag retains the item but makes some
        # behavior getters unavailable. Prove the precise native diagnostic
        # matches before comparing the independently observed item inventory.
        expected_errors = set()
        for item in native_screen.get("ScreenItems", []):
            for behavior in item.get("Dynamizations", []):
                for name, value in behavior.get("attributes", {}).items():
                    error = value.get("error") if isinstance(value, dict) else None
                    if error:
                        expected_errors.add((name, error.get("type"), error.get("message")))
        limitations = coverage.get("reasons", [])
        require(family == "unified" and coverage["status"] == "unavailable"
                and not coverage.get("truncated") and coverage.get("pageComplete") is True
                and expected_errors and limitations,
                "Screen item inspection must be complete or match independently observed getter failures")
        require(all(reason.get("code") == "HmiNativeReadFailed"
                    and (reason.get("operation", "").rsplit(".", 1)[-1], reason.get("nativeException"), reason.get("reason")) in expected_errors
                    for reason in limitations), "Screen item coverage contains unexpected native failures")
    if family == "classic":
        source = Path(native_screen["export"]["path"])
        require(sha256(source) == native_screen["export"]["sha256"], "Independent screen export changed")
        root = ET.fromstring(source.read_bytes())
        expected = []
        for item in root.iter():
            if not item.tag.startswith("Hmi.Screen.") or item.tag == "Hmi.Screen.Screen":
                continue
            attrs = item.find("AttributeList")
            if attrs is None:
                continue
            properties = {value.tag: value.text or "" for value in attrs if not list(value)}
            name = properties.get("ObjectName") or properties.get("Name")
            if name:
                expected.append({"nativeType": item.tag, "name": name, "properties": properties})
    else:
        expected = [{"nativeType": row["nativeType"], "name": row["attributes"]["Name"],
                     "properties": {key: value for key, value in row["attributes"].items()
                                    if value is None or isinstance(value, (str, bool, float)) or key in NUMERIC_SCALARS and isinstance(value, int)}}
                    for row in native_screen.get("ScreenItems", [])]
    key = lambda row: (row["nativeType"], row["name"])
    require(Counter(map(key, expected)) == Counter(map(key, observed["items"])), "Independent native screen item inventory differs")
    for row in expected:
        require(any(key(actual) == key(row) and all(actual.get("properties", {}).get(k) == v for k, v in row["properties"].items())
                    for actual in observed["items"]), "Independent native item scalar properties differ: " + row["name"])
    return {"status": "partial" if limitations else "passed", "inventoryAndScalarComparison": "passed",
            "expectedCount": len(expected), "actualCount": len(observed["items"]),
            "nativeCoverage": copy.deepcopy(coverage), "limitations": limitations}


def compare_resource_sources(native, observed):
    expected = native.get("textListEvidence", {})
    require(expected.get("status") == "observed" and expected.get("fixtureTextsPresent") is True,
            "Independent nonempty bilingual text-list export is required")
    rows = [row for row in observed["items"] if row.get("name") == "QualificationStates"]
    require(len(rows) == 1, "Exact named nonempty native text list must be retained")
    sources = rows[0].get("nativeSources", [])
    expected_files = expected.get("files", [])
    require(len(sources) == len(expected_files) == 2, "Both native text-list configuration and text-library exports are required")
    exports = expected.get("export", [])
    require(len(exports) == 2 and Counter(row.get("sha256") for row in exports) == Counter(row.get("sha256") for row in expected_files),
            "Independent resource exports must match both retained native file hashes")
    require(len({Path(row.get("path", "")).resolve() for row in exports}) == 2,
            "Independent paired resource exports require distinct native files")
    for export in exports:
        path = Path(export.get("path", ""))
        require(path.is_file() and sha256(path) == export.get("sha256"), "Independent native resource export is missing or changed")
        recorded = next(row for row in expected_files if row.get("sha256") == export["sha256"])
        require(path.read_bytes().decode("utf-8-sig") == recorded.get("text"), "Independent recorded resource text differs from retained export")
    require(len({Path(source.get("artifact", {}).get("path", "")).resolve() for source in sources}) == 2,
            "Bridge paired resource exports require distinct native files")
    for source in sources:
        artifact = source.get("artifact", {})
        require(source.get("executed") is False and isinstance(source.get("text"), str)
                and Path(artifact.get("path", "")).is_file() and sha256(Path(artifact["path"])) == artifact.get("sha256"),
                "Native resource source artifact/text evidence is incomplete or changed")
        require(Path(artifact["path"]).read_bytes().decode("utf-8-sig") == source["text"], "Returned resource text differs from its retained native file")
    require(Counter(source["artifact"]["sha256"] for source in sources) == Counter(row["sha256"] for row in expected_files),
            "Native paired resource export content differs from independent native baseline")
    coverage = rows[0].get("nativeSourceCoverage", {})
    require(coverage.get("complete") is False and coverage.get("nativeComposition") == "HmiTextLists",
            "Scoped text-list export must retain its documented incomplete composition-preservation boundary")
    return {"status": "passed", "fileCount": 2, "nonemptyList": "QualificationStates", "languages": ["en-US", "de-DE"],
            "scope": "This fixture's exact paired native export bytes; not complete formatted-text or whole-HMI preservation"}


def compare_device_profile(profile, row):
    require(row.get("profile") == profile["profile"], "Native bridge target profile differs from independent catalog selection")
    selected = profile["selectedTarget"]
    require(row.get("catalogIdentifier") == selected["typeIdentifier"], "Exact native catalog identifier differs")
    evidence = row.get("catalogEvidence") or {}
    require(evidence.get("status") == "nativeConfirmed" and evidence.get("scopeComplete") is True
            and any(item.get("sourcePath") and item.get("catalogIdentifier") == selected["typeIdentifier"] for item in evidence.get("candidates", [])),
            "Catalog identity lacks complete native source provenance")
    runtime = row.get("runtimeVersion")
    if runtime is not None:
        require(runtime == selected["version"], "Exposed native runtime version differs")
    return {"profile": row["profile"], "catalogIdentifier": row["catalogIdentifier"], "catalogEvidence": evidence,
            "runtimeVersion": runtime, "runtimeVersionAvailability": "unavailable" if runtime is None else "nativeConfirmed",
            "limitation": "Native Version attribute unavailable; catalog identifier anchors the selected version" if runtime is None else None}


def qualify_alarm_references(profile, references, inspections, gates):
    proofs = []
    for native in profile.get("nativeBefore", []):
        rows = [row for row in inspections if row["target"]["name"] == native["name"]]
        if not rows or rows[0]["target"]["family"] != "unified":
            continue
        owner = rows[0]["target"]
        refs = [row for row in references if row["target"]["objectPath"] == owner["objectPath"] and row.get("requestedAlarms") is True]
        if not refs:
            continue
        edges = [edge for row in refs for edge in row["result"]["relationships"]]
        tags = [item for row in rows if row["section"] == "tags" for item in row["result"]["items"]]
        alarms = [item for row in rows if row["section"] == "alarms" for item in row["result"]["items"]]
        for alarm in native_section_entries(native, "alarms", "unified"):
            attrs = alarm["attributes"]
            if attrs.get("Name") != "QualificationAlarm":
                continue
            actual = next(item for item in alarms if item["name"] == attrs["Name"])
            targets = [item for item in tags if item["name"] == attrs["RaisedStateTag"]]
            require(len(targets) == 1, "Alarm fixture requires one independently scoped scalar tag")
            matches = [edge for edge in edges if edge["relation"] == "hmiAlarmTag" and edge["sourcePath"] == actual["identity"]["objectPath"] and edge.get("sourceProperty") == "RaisedStateTag"]
            require(matches and all(edge["resolution"] == "resolved" and edge.get("nativeSpelling") == attrs["RaisedStateTag"]
                    and edge.get("target", {}).get("objectPath") == targets[0]["identity"]["objectPath"]
                    and edge.get("target", {}).get("hmiPath") == owner["hmiPath"]
                    and edge.get("bitNumber") == attrs["RaisedStateTagBitNumber"] for edge in matches), "Alarm tag edge differs from exact native same-HMI binding")
            proofs.append(matches[0])
    if proofs:
        gates["alarmTagReferences"] = gate("passed", "Native discrete alarm RaisedStateTag and bit matched exact same-HMI tag identity", proofs=proofs)


def compare_explorer_relationships(reference, observed, references):
    """Compare populated tag/alarm graph evidence to exact HMI-tool observations."""
    path = reference["objectPath"]
    def key(edge):
        return (edge.get("relation"), edge.get("sourcePath"), (edge.get("target") or {}).get("objectPath"),
                edge.get("sourceProperty"), edge.get("nativeSpelling"), edge.get("resolution"))
    expected = {key(edge) for entry in references for edge in entry["result"]["relationships"]
                if edge.get("relation") in {"hmiAlarmTag", "hmiPlcDeclaration", "hmiTagConnection"}
                and edge.get("resolution") == "resolved"
                and (edge.get("sourcePath") == path or (edge.get("target") or {}).get("objectPath") == path)}
    require(expected, "Selected tag/alarm explorer fixture has no independently observed resolved relationship")
    actual = {key(edge.get("evidence", {}).get("native", {})) for edge in observed.get("edges", [])}
    require(expected.issubset(actual), "Explorer omitted or changed exact native tag/alarm relationships")
    captured_nodes = {node.get("nodeId"): node.get("objectPath") for node in observed.get("nodes", [])}
    require(reference.get("nodeId") and captured_nodes.get(reference["nodeId"]) == path,
            "Explorer graph omitted or substituted the selected captured node")
    populated = 0
    for edge in observed.get("edges", []):
        native = edge.get("evidence", {}).get("native", {})
        if key(native) not in expected:
            continue
        source = edge.get("source") or {}
        require(source.get("objectPath") == native["sourcePath"] and source.get("nodeId")
                and source.get("resolution") == "resolved" and captured_nodes.get(source["nodeId"]) == source["objectPath"],
                "Explorer native relationship lacks its exact populated source node")
        if native["relation"] in {"hmiAlarmTag", "hmiTagConnection"}:
            target = edge.get("target") or {}
            require(target.get("objectPath") == native["target"]["objectPath"] and target.get("nodeId")
                    and target.get("resolution") == "resolved" and edge["evidence"].get("resolution") == "resolved"
                    and captured_nodes.get(target["nodeId"]) == target["objectPath"] and edge.get("unresolved") is None,
                    "Explorer HMI relationship lacks its exact populated target node")
            populated += 1
    require(observed.get("scriptsExecuted") is False, "Explorer must not execute project scripts")
    return {"status": "passed", "expectedRelationshipCount": len(expected), "actualEdgeCount": len(observed["edges"]),
            "populatedHmiRelationshipCount": populated,
            "scope": "Exact captured source nodes and HMI tag/connection/alarm endpoints; PLC member targets retain their native capture limits"}


def qualify_explorer(client, devices, references=(), evidence=None):
    evidence = evidence if evidence is not None else {}
    def model(action, **request):
        return client.tool("project_model", {"request": {"action": action, **request}})
    capture = model("capture", maxVisited=50000, maxBytes=8388608)
    evidence["capture"] = capture
    snapshot = capture["snapshotId"]
    roots = model("children", snapshotId=snapshot, maxItems=100)
    search = model("search", snapshotId=snapshot, kind="hmiScreen", maxItems=200)
    rows = search.get("items", search.get("rows", search.get("nodes", [])))
    if not rows:
        search = model("search", snapshotId=snapshot, kind="hmi", maxItems=200)
        rows = search.get("items", search.get("rows", search.get("nodes", [])))
    require(rows, "Explorer must contain exact native HMI nodes")
    node = rows[0]
    reference = node.get("reference") or {"schemaVersion": "project-model.v1", "snapshotId": snapshot,
        "sessionGeneration": capture["sessionGeneration"], "projectPath": capture["project"]["projectPath"],
        "nodeId": node["nodeId"], "objectPath": node["objectPath"]}
    details = model("details", reference=reference, facet="hmiDetails")
    evidence.update(roots=roots, search=search, details=details)
    require(details.get("status") in {"available", "partial"}, "Explorer outer details unavailable")
    require(details.get("details", {}).get("provider") == "hmi.v21", "Explorer did not dispatch the HMI provider")
    require(details["details"].get("status") in {"available", "partial"}, "Explorer provider content unavailable")
    relationships = model("relationships", reference=reference, maxEdges=200)
    evidence["relationships"] = relationships
    populated = evidence["populatedRelationships"] = []
    if references and any(row["family"] == "unified" for row in devices["items"]):
        required_paths = {path for entry in references for edge in entry["result"]["relationships"]
                          if edge.get("relation") in {"hmiAlarmTag", "hmiPlcDeclaration", "hmiTagConnection"} and edge.get("resolution") == "resolved"
                          for path in ((edge.get("sourcePath"), (edge.get("target") or {}).get("objectPath"))
                                       if edge["relation"] == "hmiAlarmTag" else (edge.get("sourcePath"),)) if path}
        expected_paths = {path for entry in references for edge in entry["result"]["relationships"]
                          if edge.get("relation") in {"hmiAlarmTag", "hmiPlcDeclaration", "hmiTagConnection"} and edge.get("resolution") == "resolved"
                          for path in (edge.get("sourcePath"), (edge.get("target") or {}).get("objectPath")) if path}
        for kind, names in (("hmiTag", {"SharedValue", "LinkedSignal", "LinkedDbValue"}), ("hmiAlarm", {"QualificationAlarm"})):
            selected = model("search", snapshotId=snapshot, kind=kind, maxItems=200)
            candidates = [row for row in selected.get("nodes", []) if row["name"] in names and row["objectPath"] in expected_paths]
            for row in candidates:
                exact = row.get("reference") or {"schemaVersion": "project-model.v1", "snapshotId": snapshot,
                    "sessionGeneration": capture["sessionGeneration"], "projectPath": capture["project"]["projectPath"],
                    "nodeId": row["nodeId"], "objectPath": row["objectPath"]}
                observed = model("relationships", reference=exact, maxEdges=200)
                entry = {"reference": exact, "kind": kind, "name": row["name"], "result": observed}
                populated.append(entry)
                entry["comparison"] = compare_explorer_relationships(exact, observed, references)
        require(required_paths.issubset({row["reference"]["objectPath"] for row in populated}), "Explorer is missing exact populated tag/alarm fixture nodes")
    exported = model("export", snapshotId=snapshot, format="html")
    evidence["htmlExport"] = exported
    require("artifact" in json.dumps(exported), "Explorer did not produce an HTML artifact")
    evidence["runtimeVerified"] = False
    return evidence


def qualify_behavior_references(profile, references, inspections, gates):
    """Check independent screen behavior even when PLC fixture authoring failed."""
    counts = {"dynamicReferences": 0, "brokenReferences": 0, "scriptBehaviorInspection": 0}
    for native in profile.get("nativeBefore", []):
        owned_inspections = [row for row in inspections if row["target"]["name"] == native["name"]]
        if not owned_inspections or owned_inspections[0]["target"]["family"] != "unified":
            continue
        owner = owned_inspections[0]["target"]
        tag_rows = [item for row in owned_inspections if row["section"] == "tags" for item in row["result"]["items"]]
        native_tags = [row for row in native_section_entries(native, "tags", "unified") if row["nativeType"].endswith(".HmiTag")]
        for screen in native_section_entries(native, "screens", "unified"):
            observed = next(row for row in owned_inspections if row["section"] == "items" and row["screen"]["name"] == screen["attributes"]["Name"])
            sources = [row for row in references if row["target"]["objectPath"] == owner["objectPath"] and row.get("screen", {}).get("objectPath") == observed["screen"]["objectPath"]]
            require(sources and all(isinstance(row["result"].get("relationships"), list) for row in sources), "Selected screen relationship observations required")
            edges = [edge for row in sources for edge in row["result"]["relationships"]]
            for event in screen.get("EventHandlers", []):
                script = event.get("Script", {}).get("attributes", {})
                if not isinstance(script.get("ScriptCode"), str):
                    continue
                matches = [edge for edge in edges if edge["relation"] == "hmiScriptBehavior" and edge["sourcePath"] == observed["screen"]["objectPath"]]
                require(any(edge.get("eventMetadata", {}).get("EventType") == event["attributes"]["EventType"]
                            and edge.get("rawScript", {}).get("scriptCode") == script["ScriptCode"]
                            and edge["rawScript"].get("globalDefinitionAreaScriptCode") == script["GlobalDefinitionAreaScriptCode"]
                            and edge["rawScript"].get("async") == script["Async"]
                            and edge["rawScript"].get("scriptCodeSha256") == hashlib.sha256(script["ScriptCode"].encode("utf-8")).hexdigest()
                            and edge["rawScript"].get("globalDefinitionAreaScriptCodeSha256") == hashlib.sha256(script["GlobalDefinitionAreaScriptCode"].encode("utf-8")).hexdigest()
                            and edge["rawScript"].get("scriptsExecuted") is False and edge["resolution"] == "dynamic"
                            for edge in matches), "Exact native screen event source/hash/metadata differs")
                counts["scriptBehaviorInspection"] += 1
            for item in screen.get("ScreenItems", []):
                actual = next(row for row in observed["result"]["items"] if row["name"] == item["attributes"]["Name"])
                for dynamic in item.get("Dynamizations", []):
                    attrs = dynamic.get("attributes", {})
                    if not isinstance(attrs.get("Tag"), str):
                        continue
                    spelling = attrs["Tag"]
                    converter = dynamic.get("ValueConverter", {}).get("attributes", {})
                    is_dynamic = attrs.get("UseIndirectAddressing") is True or converter.get("IsFormulaSelected") is True
                    candidates = [row for row in native_tags if row["attributes"]["Name"] == spelling]
                    resolution = "dynamic" if is_dynamic else "resolved" if len(candidates) == 1 else "unresolved"
                    matches = [edge for edge in edges if edge["relation"] == "hmiScreenItemTag" and edge["sourcePath"] == actual["identity"]["objectPath"]
                               and edge.get("sourceProperty") == "Dynamizations/" + attrs["PropertyName"] + "/Tag"]
                    require(matches and all(edge["resolution"] == resolution and edge["nativeSpelling"] == spelling for edge in matches), "Native behavior classification/spelling differs")
                    require(all(edge.get("rawProperties", {}).get("UseIndirectAddressing") == attrs["UseIndirectAddressing"]
                                and edge.get("rawProperties", {}).get("ValueConverter", {}).get("IsFormulaSelected") == converter["IsFormulaSelected"]
                                and edge.get("rawProperties", {}).get("ValueConverter", {}).get("Formula") == converter["Formula"] for edge in matches),
                            "Native behavior indirect/formula metadata differs")
                    if resolution != "resolved":
                        require(all(edge.get("target") is None for edge in matches), "Dynamic/unresolved behavior promoted to exact target")
                        counts["dynamicReferences" if is_dynamic else "brokenReferences"] += 1
                    else:
                        scope = candidates[0]["attributes"]["TagTableName"]
                        tags = [tag for tag in tag_rows if tag["name"] == spelling and tag["properties"].get("TagTableName") == scope]
                        require(len(tags) == 1 and all(edge.get("target", {}).get("objectPath") == tags[0]["identity"]["objectPath"] for edge in matches), "Direct behavior resolved outside independent exact tag-table scope")
    for name, count in counts.items():
        if count:
            gates[name] = gate("passed", "Selected native screen behavior compared independently, including source/metadata and unresolved endpoints; no script execution", count=count)


def qualify_relationships(profile, references, inspections, gates):
    qualify_behavior_references(profile, references, inspections, gates)
    links = profile.get("connectionFixture", {}).get("links", [])
    if not links:
        return
    proofs, dynamic_count, broken_count, script_count = [], 0, 0, 0
    for link in links:
        if link.get("isValid") is not True:
            gates["integratedConnectionMapping"] = gate("failed", "Independent native integrated connection fixture was not created validly", nativeFixture=copy.deepcopy(link))
        require(link.get("isValid") is True, "Native fixture must have a valid integrated connection")
        owner = next(row["target"] for row in inspections if row["target"]["name"] == link["hmiSoftware"])
        owned = [entry for entry in references if entry["target"]["objectPath"] == owner["objectPath"]]
        require(all(isinstance(entry["result"].get("relationships"), list) for entry in owned),
                "Native hmi_references must expose the relationships array")
        edges = [edge for entry in owned for edge in entry["result"]["relationships"]]
        connection = next((edge for edge in edges if edge["relation"] == "hmiConnectionPlc" and edge.get("resolution") == "resolved"
                           and edge.get("target", {}).get("name") == link["plcSoftware"]), None)
        require(connection is not None, "Integrated connection did not resolve to independently configured PLC owner")
        require(any(e.get("localTargetMatchesHmiOwner") is True and e.get("isValid") is True
                    for e in connection.get("nativeConnectionEvidence", [])), "Connection resolution lacks exact native endpoint evidence")
        proofs.append({"hmiSoftware": link["hmiSoftware"], "plcOwner": connection["target"], "connection": connection})
        if owner["family"] != "unified":
            continue
        authored = next((row for row in profile.get("fixtureAuthoring", []) if row["software"] == link["hmiSoftware"]), {})
        script = authored.get("richContents", {}).get("screenEvent")
        if script:
            matches = [edge for edge in edges if edge["relation"] == "hmiScriptBehavior"]
            require(matches, "Native screen event source omitted from relationship observation")
            require(any(edge.get("rawScript", {}).get("scriptCode") == script["scriptCode"]
                        and edge["rawScript"].get("globalDefinitionAreaScriptCode") == script["globalCode"]
                        and edge["rawScript"].get("scriptCodeSha256") == hashlib.sha256(script["scriptCode"].encode("utf-8")).hexdigest()
                        and edge["rawScript"].get("globalDefinitionAreaScriptCodeSha256") == hashlib.sha256(script["globalCode"].encode("utf-8")).hexdigest()
                        and edge.get("eventMetadata", {}).get("EventType") == script["event"]
                        and edge["rawScript"].get("scriptsExecuted") is False and edge["resolution"] == "dynamic" for edge in matches),
                    "Native event script/global source did not match independent author readback")
            script_count += 1
        tag_rows = [item for inspection in inspections if inspection["target"]["objectPath"] == owner["objectPath"] and inspection["section"] == "tags" for item in inspection["result"]["items"]]
        linked = next(row for row in tag_rows if row["name"] == "LinkedSignal")
        declaration = next((edge for edge in edges if edge["relation"] == "hmiPlcDeclaration" and edge["sourcePath"] == linked["identity"]["objectPath"] and edge["resolution"] == "resolved"), None)
        require(declaration is not None, "Configured LinkedSignal did not resolve to its exact PLC declaration")
        require(declaration["target"]["name"] == "SharedSignal" and declaration["target"]["plcPath"] == connection["target"]["objectPath"], "Same-name PLC declaration leaked across owners")
        proofs[-1]["declaration"] = declaration
        if link.get("LinkedDbValue"):
            db_tag = next(row for row in tag_rows if row["name"] == "LinkedDbValue")
            db_edge = next((edge for edge in edges if edge["relation"] == "hmiPlcDeclaration" and edge["sourcePath"] == db_tag["identity"]["objectPath"] and edge["resolution"] == "resolved"), None)
            require(db_edge is not None, "Configured DB scalar declaration did not resolve")
            require(db_edge["target"]["kind"] == "plcDeclaration" and db_edge["target"]["name"] == "Value" and db_edge["target"]["plcPath"] == connection["target"]["objectPath"], "DB declaration resolved outside exact native PLC owner")
            require(db_edge["target"].get("pathComponents") == ["Value"] and db_edge.get("sourceArtifact", {}).get("sha256"), "DB declaration lacks native exported member evidence")
            proofs[-1]["dbDeclaration"] = db_edge
        for name, resolution, spelling in (("DirectReference", "resolved", None), ("IndirectReference", "dynamic", "SharedValue"),
                                            ("FormulaReference", "dynamic", "SharedValue"), ("BrokenReference", "unresolved", "MissingTag_Qualification")):
            items = [item for inspection in inspections if inspection["target"]["objectPath"] == owner["objectPath"] and inspection["section"] == "items" for item in inspection["result"]["items"] if item["name"] == name]
            if not items:
                continue
            for item in items:
                matches = [edge for edge in edges if edge["relation"] == "hmiScreenItemTag" and edge["sourcePath"] == item["identity"]["objectPath"]]
                require(matches and all(edge["resolution"] == resolution for edge in matches), "Incorrect reference classification: " + name)
                if spelling:
                    require(all(edge["nativeSpelling"] == spelling and edge.get("target") is None for edge in matches), "Dynamic/broken spelling or unresolved endpoint was lost")
                    if name == "IndirectReference":
                        require(all(edge.get("rawProperties", {}).get("UseIndirectAddressing") is True for edge in matches), "Indirect native flag was lost")
                    if name == "FormulaReference":
                        require(all(edge.get("rawProperties", {}).get("ValueConverter", {}).get("IsFormulaSelected") is True
                                    and edge["rawProperties"]["ValueConverter"].get("Formula") == "'SharedValue' + 2" for edge in matches),
                                "Native fixture formula text or selection flag was lost")
                else:
                    require(all(edge["target"]["hmiPath"] == owner["hmiPath"] for edge in matches), "Screen item resolved outside its HMI tag scope")
                    proofs[-1]["directItemTag"] = matches[0]
                if resolution == "dynamic": dynamic_count += 1
                if resolution == "unresolved": broken_count += 1
    gates["integratedConnectionMapping"] = gate("passed", "Independent two-PLC native endpoint ownership verified", proofs=proofs)
    if proofs and all("declaration" in proof for proof in proofs):
        gates["plcDeclarationMapping"] = gate("passed", "Unified scalar tag declarations scoped to exact PLC despite duplicated native declaration names", proofs=proofs)
        if all(proof.get("directItemTag", {}).get("target", {}).get("objectPath") == proof["declaration"]["sourcePath"] for proof in proofs):
            gates["screenTagPlcChain"] = gate("passed", "Native direct screen item -> HMI tag -> integrated connection -> exact PLC declaration verified", proofs=proofs)
        if all("dbDeclaration" in proof for proof in proofs):
            gates["dbDeclarationMapping"] = gate("passed", "Explicit native DB scalar foreign key resolved using exact owner plus native exported member/type evidence", proofs=proofs)
    if dynamic_count: gates["dynamicReferences"] = gate("passed", "Native indirect and formula references retained as dynamic with null resolved endpoint", count=dynamic_count)
    if broken_count: gates["brokenReferences"] = gate("passed", "Native missing tag spelling retained with unresolved null endpoint", count=broken_count)
    if script_count: gates["scriptBehaviorInspection"] = gate("passed", "Nonempty native event/global script text matched independent readback without execution or invented semantic bindings", count=script_count)


def reassess_captured(native, captured):
    """Re-evaluate immutable native observations; never invoke the bridge."""
    results = []
    for original in captured.get("profiles", []):
        baseline = next((p for p in native.get("profiles", []) if p["profile"] == original["profile"]), None)
        if not baseline or not original.get("inspections"):
            continue
        row = {"profile": original["profile"], "originalGates": copy.deepcopy(original["gates"]),
               "gates": {name: copy.deepcopy(original["gates"].get(name, gate())) for name in GATES}, "comparisons": []}
        native_gates = import_native_evidence(baseline)
        for name in ("compile", "cleanBaselineCompile", "reopenedCompile", "recoveredCompile", "saveReopen", "archiveRecovery"):
            row["gates"][name] = native_gates[name]
        if captured.get("qualificationMode") == "owned-recovered-copy-inspection":
            row["qualificationMode"] = captured["qualificationMode"]
            row["nativeArchiveRecoveryGate"] = native_gates["archiveRecovery"]
            row["gates"]["archiveRecovery"] = copy.deepcopy(original["gates"].get("archiveRecovery", gate()))
        failures, limitations = [], []
        if captured.get("qualificationMode") == "owned-recovered-copy-inspection" and original.get("inspectionCopy", {}).get("sourcePreserved") is not True:
            failures.append({"section": "sourcePreservation", "reason": "Copied-project inspection lacks verified original fixture preservation"})
        for inspection in original["inspections"]:
            owner = next(p for p in baseline["nativeBefore"] if p["name"] == inspection["target"]["name"])
            family = inspection["target"]["family"]
            evidence = {"owner": inspection["target"]["objectPath"], "section": inspection["section"]}
            try:
                if inspection["section"] == "items":
                    evidence["screen"] = inspection["screen"]
                    screen = next(p for p in native_section_entries(owner, "screens", family) if p["attributes"]["Name"] == inspection["screen"]["name"])
                    evidence["comparison"] = compare_screen_items(screen, family, inspection["result"])
                else:
                    evidence["comparison"] = compare_section(owner, inspection["section"], family, inspection["result"])
                if evidence["comparison"]["status"] == "partial":
                    limitations.append(evidence)
            except Exception as error:
                evidence["comparison"] = {"status": "failed", "reason": str(error)}
                failures.append(evidence)
            row["comparisons"].append(evidence)
        # Resource source acceptance includes exact paired bytes, not merely the
        # resource's inventory row. Never erase a failed or absent source proof
        # when independently reassessing a completed bridge report.
        row["resourceSourceComparisons"] = []
        for owner in baseline["nativeBefore"]:
            if not owner.get("textListEvidence"):
                continue
            evidence = {"owner": owner["name"], "section": "resources"}
            try:
                sources = [item for item in original.get("resourceSources", []) if item.get("target", {}).get("name") == owner["name"]]
                require(len(sources) == 1 and isinstance(sources[0].get("result"), dict),
                        "Each independent resource owner requires one captured paired source response")
                evidence["comparison"] = compare_resource_sources(owner, sources[0]["result"])
            except Exception as error:
                evidence["comparison"] = {"status": "failed", "reason": str(error)}
                failures.append(evidence)
            row["resourceSourceComparisons"].append(evidence)
        row["gates"]["inspectionAccuracy"] = gate("failed" if failures else "unverified" if limitations else "passed",
            "Captured native inventories/scalars reassessed; expected getter failures remain incomplete coverage", failures=failures, limitations=limitations)
        # These are recomputed assertions, not inherited failed harness results.
        for name in ("integratedConnectionMapping", "plcDeclarationMapping", "dbDeclarationMapping", "screenTagPlcChain", "dynamicReferences", "brokenReferences", "scriptBehaviorInspection"):
            row["gates"][name] = gate()
        try:
            qualify_relationships(baseline, original.get("references", []), original["inspections"], row["gates"])
        except Exception as error:
            row["gates"]["plcDeclarationMapping"] = gate("failed", str(error))
        try:
            qualify_alarm_references(baseline, original.get("references", []), original["inspections"], row["gates"])
        except Exception as error:
            row["gates"]["alarmTagReferences"] = gate("failed", str(error))
        row["acceptance"] = summarize_gates(row["gates"])
        results.append(row)
    return results


def inspect_all(client, request):
    items, offset, revision = [], 0, None
    while True:
        args = {"schemaVersion": 1, "limit": 37, "offset": offset, **request}
        if revision:
            args["expectedMetadataRevision"] = revision
        result = client.tool("hmi_inspect", {"request": args})
        require(result["coverage"]["status"] in {"complete", "truncated", "unsupported", "unavailable", "failed"}, "Invalid coverage status")
        page = result.get("items", [])
        require(len(page) <= 37, "HMI page exceeded requested bound")
        items.extend(page)
        revision = result.get("metadataRevision")
        next_offset = result.get("nextOffset")
        if next_offset is None:
            break
        require(next_offset > offset and page, "HMI pagination did not progress")
        offset = next_offset
        require(offset <= 50000, "HMI pagination exceeded traversal bound")
    paths = [item["identity"]["objectPath"] for item in items]
    require(len(paths) == len(set(paths)), "HMI inspection returned duplicate exact identities")
    return {"items": items, "lastPage": result}


def qualify_bridge(args, native, report, persist):
    copy_mode = getattr(args, "inspect_owned_recovered_copy", False)
    copy_destinations = {}
    if copy_mode:
        for index, profile in enumerate(native.get("profiles", [])):
            if profile.get("archive") and profile.get("nativeBefore"):
                destination = inspection_copy_destination(profile, args.artifacts)
                # Native admission precedes even host/client construction. The
                # file-only copier is independent of Siemens' path restriction.
                validate_native_copy_directory(destination)
                copy_destinations[index] = destination
    client = Client(args.host.resolve(), args.timeout, ["--worker", str(args.worker.resolve()), "--artifacts", str(args.artifacts.resolve())])
    watchers = []
    inspection_copies = []
    started = False
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "hmi-qualification", "version": "1"}})
        client.notify("notifications/initialized")
        names = {item["name"] for item in client.request("tools/list")["tools"]}
        require({"hmi_inspect", "hmi_references", "hmi_capabilities", "project_open" if copy_mode else "project_retrieve"}.issubset(names), "Current HMI tools absent from exact binary")
        report["toolsDiscovered"] = sorted(names.intersection({"hmi_inspect", "hmi_references", "hmi_capabilities", "project_model", "project_children", "project_details", "project_references"}))
        for profile_index, profile in enumerate(native.get("profiles", [])):
            result = next(item for item in report["profiles"] if item["profile"] == profile["profile"])
            if not profile.get("archive") or not profile.get("nativeBefore"):
                continue
            active_gate = "deviceDiscovery"
            terminal_startup_failure = None
            try:
                archive = validate_fixture(profile, args.native_report)
                copied_project = None
                if copy_mode:
                    receipt = result["inspectionCopy"] = {}
                    inspection_copies.append((result, receipt))
                    result["bridgeArchiveRetrieved"] = False
                    result["bridgeArchiveRetrieval"] = {"status": "notAttempted", "reason": "Separate copied-project inspection mode"}
                    result["nativeArchiveRecoveryGate"] = copy.deepcopy(result["gates"]["archiveRecovery"])
                    if result["gates"]["archiveRecovery"]["status"] == "passed":
                        result["gates"]["archiveRecovery"] = gate("unverified", "Independent native lifecycle observation retained separately; this copied-project inspection never calls bridge archive retrieval")
                    persist()
                    copied_project = prepare_owned_inspection_copy(profile, args.native_report, report["nativeReportSha256"], args.artifacts, receipt, copy_destinations[profile_index])
                    persist()
                raw_start = client.request("tools/call", {"name": "tia_start", "arguments": {}})
                start = decode(raw_start)
                result["startupObservation"] = copy.deepcopy(start)
                persist()
                if raw_start.get("isError"):
                    failure = start.get("error", {})
                    result["startupFailure"] = failure
                    if blocking_startup_failure(failure):
                        terminal_startup_failure = {"profile": profile["profile"], "operation": "tia_start", "error": copy.deepcopy(failure)}
                    raise AssertionError("tia_start: " + json.dumps(failure, ensure_ascii=False))
                require(start.get("ownsPortalProcess") is True, "Qualification requires new owned TIA process")
                started = True
                watcher = OwnedTiaProcessWatcher(start["processId"])
                watchers.append((result, watcher))
                if copy_mode:
                    raw_open = client.request("tools/call", {"name": "project_open", "arguments": {"path": str(copied_project)}})
                    opened = result["inspectionCopy"]["open"] = decode(raw_open)
                    if raw_open.get("isError") and blocking_startup_failure(opened.get("error", {})):
                        terminal_startup_failure = {"profile": profile["profile"], "operation": "project_open", "error": copy.deepcopy(opened["error"])}
                    persist()
                    require(not raw_open.get("isError") and opened.get("opened") is True and opened.get("saved") is False,
                            "Read-only project_open: " + json.dumps(opened, ensure_ascii=False))
                    status = result["inspectionCopy"]["ownedSessionAfterOpen"] = client.tool("tia_session_status")
                    verify_copied_project_identity(copied_project, start, opened, status)
                    result["inspectionCopy"]["exactOpenIdentityVerified"] = True
                    persist()
                    raw_retrieval = None
                else:
                    raw_retrieval = client.request("tools/call", {"name": "project_retrieve", "arguments": {"archivePath": str(archive), "expectedSha256": profile["archive"]["sha256"]}})
                retrieved = decode(raw_retrieval) if raw_retrieval is not None else {}
                if raw_retrieval is not None:
                    result["bridgeArchiveRetrieved"] = not raw_retrieval.get("isError", False) and retrieved.get("retrieved") is True
                if raw_retrieval is not None and raw_retrieval.get("isError"):
                    failure = result["retrievalFailure"] = retrieved.get("error", {})
                    if blocking_startup_failure(failure):
                        terminal_startup_failure = {"profile": profile["profile"], "operation": "project_retrieve", "error": copy.deepcopy(failure)}
                    persist()
                    require(getattr(args, "allow_owned_derived_open", False) and failure.get("code") == "ArchiveRetrieveFailed"
                            and failure.get("details", {}).get("retrieved") is False,
                            "project_retrieve: " + json.dumps(failure, ensure_ascii=False))
                    fallback_path = validate_owned_derived_project(profile, native, args.native_report)
                    status = client.tool("tia_session_status")
                    result["fallbackEmptySession"] = status
                    require(status.get("ownsPortalProcess") is True and status.get("processId") == start["processId"]
                            and status.get("projects") == [], "Read-only fallback requires the same exact owned empty process after a known retrieval failure")
                    result["readOnlyFallback"] = {"path": str(fallback_path), "projectFileSha256BeforeOpen": sha256(fallback_path),
                        "basis": "Explicitly allowed read-only opening of independently recovered and compiled seeder-owned project; failed bridge archive retrieval is not qualified"}
                    persist()
                    raw_open = client.request("tools/call", {"name": "project_open", "arguments": {"path": str(fallback_path)}})
                    opened = result["readOnlyFallback"]["open"] = decode(raw_open)
                    if raw_open.get("isError") and blocking_startup_failure(opened.get("error", {})):
                        terminal_startup_failure = {"profile": profile["profile"], "operation": "project_open", "error": copy.deepcopy(opened["error"])}
                    persist()
                    require(not raw_open.get("isError") and opened.get("opened") is True, "project_open: " + json.dumps(opened, ensure_ascii=False))
                elif raw_retrieval is not None:
                    result["retrieval"] = retrieved
                    require(result["bridgeArchiveRetrieved"], "Native archive retrieval did not report a retrieved project")
                devices = inspect_all(client, {"section": "devices"})
                result["devices"] = devices
                expected_names = {row["name"] for row in profile["nativeBefore"]}
                actual_names = {row["name"] for row in devices["items"]}
                require(expected_names == actual_names, "HMI software names/count differ from independent native fixture")
                expected_family = "unified" if profile["profile"].startswith("unified") else "classic"
                require(all(row["family"] == expected_family for row in devices["items"]), "Native HMI family differs")
                result["deviceProfileEvidence"] = [compare_device_profile(profile, row) for row in devices["items"]]
                result["gates"]["deviceDiscovery"] = gate("passed", "Exact software names/count and native family matched independent fixture")
                persist()
                active_gate = "inspectionAccuracy"
                inspections = []
                inspection_failures = []
                result["inspections"] = inspections
                result["references"] = []
                result["capabilities"] = []
                result["resourceSources"] = []
                for target in devices["items"]:
                    native_target = next(row for row in profile["nativeBefore"] if row["name"] == target["name"])
                    require(not target["identity"].get("plcPath"), "HMI identity fabricated PLC ownership")
                    result["capabilities"].append(client.tool("hmi_capabilities", {"request": {"target": target["identity"]}}))
                    for section in SECTIONS:
                        result["currentObservation"] = {"target": target["identity"]["objectPath"], "section": section}
                        persist()
                        observed = inspect_all(client, {"section": section, "target": target["identity"]})
                        entry = {"target": target["identity"], "section": section, "result": observed}
                        inspections.append(entry)
                        try:
                            entry["comparison"] = compare_section(native_target, section, expected_family, observed)
                        except Exception as error:
                            entry["comparison"] = {"status": "failed", "reason": str(error)}
                            inspection_failures.append(str(error))
                        if section == "resources" and native_target.get("textListEvidence"):
                            resource = next((row for row in observed["items"] if row.get("name") == "QualificationStates"), None)
                            source_entry = {"target": target["identity"], "section": "resources"}; result["resourceSources"].append(source_entry)
                            try:
                                require(resource is not None, "Documented bilingual fixture list was not found in native bridge inventory")
                                source_entry["result"] = inspect_all(client, {"section": "resources", "target": target["identity"],
                                    "objectPath": resource["identity"]["objectPath"], "includeSource": True})
                                source_entry["comparison"] = compare_resource_sources(native_target, source_entry["result"])
                            except Exception as error:
                                source_entry["comparison"] = {"status": "failed", "reason": str(error)}
                                inspection_failures.append(str(error))
                        if section == "screens":
                            for screen in observed["items"]:
                                items = inspect_all(client, {"section": "items", "target": target["identity"], "screenPath": screen["identity"]["objectPath"]})
                                item_entry = {"target": target["identity"], "section": "items", "screen": screen["identity"], "result": items}
                                inspections.append(item_entry)
                                native_screen = next(row for row in native_section_entries(native_target, "screens", expected_family) if row["attributes"]["Name"] == screen["name"])
                                try:
                                    item_entry["comparison"] = compare_screen_items(native_screen, expected_family, items)
                                except Exception as error:
                                    item_entry["comparison"] = {"status": "failed", "reason": str(error)}
                                    inspection_failures.append(str(error))
                                references = client.tool("hmi_references", {"request": {"schemaVersion": 1, "target": target["identity"], "screenPath": screen["identity"]["objectPath"], "includeAlarms": True, "limit": 200, "maxVisited": 50000}})
                                result["references"].append({"target": target["identity"], "screen": screen["identity"], "requestedAlarms": True, "result": references})
                    result["references"].append({"target": target["identity"], "requestedAlarms": True, "result": client.tool("hmi_references", {"request": {"schemaVersion": 1, "target": target["identity"], "includeAlarms": True, "limit": 200, "maxVisited": 50000}})})
                limitations = [entry["comparison"] for entry in inspections if entry.get("comparison", {}).get("status") == "partial"]
                result["gates"]["inspectionAccuracy"] = gate("failed" if inspection_failures else "unverified" if limitations else "passed", "Per-section and screen-item inventories/scalars compared independently; expected getter gaps remain explicit and prevent a complete inspection claim", failures=inspection_failures, limitations=limitations)
                repeated_items = [item for inspection in inspections if inspection["section"] == "items" for item in inspection["result"]["items"] if item["name"] == "RepeatedItem"]
                require(len(repeated_items) >= 4 and len({item["identity"]["objectPath"] for item in repeated_items}) == len(repeated_items), "Repeated screen-member names must remain distinct in four valid scopes")
                result["gates"]["duplicateNames"] = gate("passed", "Native RepeatedItem members in two screens on two HMI targets retained distinct exact identities")
                result["gates"]["boundedInspection"] = gate("passed", "Requested page bounds and unique exact object identities verified")
                active_gate = "plcDeclarationMapping"
                try:
                    qualify_relationships(profile, result["references"], inspections, result["gates"])
                except Exception as error:
                    result["gates"]["plcDeclarationMapping"] = gate("failed", str(error))
                try:
                    qualify_alarm_references(profile, result["references"], inspections, result["gates"])
                except Exception as error:
                    result["gates"]["alarmTagReferences"] = gate("failed", str(error))
                if devices["items"]:
                    target = devices["items"][0]["identity"]
                    for key, field, replacement in (("wrongFamily", "family", "unified" if expected_family == "classic" else "classic"), ("staleTarget", "sessionGeneration", "expired-qualification-generation")):
                        active_gate = key
                        invalid = {**target, field: replacement}
                        raw = client.request("tools/call", {"name": "hmi_inspect", "arguments": {"request": {"schemaVersion": 1, "section": "tags", "target": invalid}}})
                        require(raw.get("isError") is True, key + " selection must be rejected")
                        result["gates"][key] = gate("passed", "Invalid exact target rejected", response=decode(raw))
                active_gate = "explorerIntegration"
                result["explorer"] = {}
                result["currentObservation"] = {"section": "explorerIntegration"}
                persist()
                qualify_explorer(client, devices, result["references"], result["explorer"])
                result["gates"]["explorerIntegration"] = gate("passed", "Actual project-model capture, children, exact HMI details, populated tag/alarm relationships and HTML export exercised")
                result["bridgeObserved"] = True
                result.pop("currentObservation", None)
            except Exception as error:
                result["bridgeError"] = str(error)
                result["gates"][active_gate] = gate("failed", str(error))
            finally:
                if started:
                    try: result["cleanup"] = client.tool("tia_detach")
                    except Exception as error: result["cleanupError"] = str(error)
                    started = False
                persist()
            if terminal_startup_failure:
                report["stoppedAfterStartupFailure"] = terminal_startup_failure
                block_pending_profiles(native.get("profiles", []), report["profiles"], profile_index, terminal_startup_failure)
                persist()
                break
    finally:
        if started:
            try: report["cleanup"] = client.tool("tia_detach")
            except Exception as error: report["cleanupError"] = str(error)
        report["launcherShutdown"] = client.close()
        report["hostExitCode"] = client.process.returncode
        report["hostLogs"] = client.errors[-100:]
        for result, watcher in watchers:
            try:
                result["ownedProcessExit"] = watcher.wait_for_exit(60000)
            except ProcessExitNotVerified as error:
                result["ownedProcessExit"] = error.evidence
        for result, receipt in inspection_copies:
            try:
                verify_source_preserved(receipt)
                require(sha256(Path(receipt["nativeReport"]["path"])) == receipt["nativeReport"]["sha256"], "Independent native report changed during inspection")
                require(sha256(Path(receipt["sourceArchive"]["path"])) == receipt["sourceArchive"]["sha256"], "Independent source archive changed during inspection")
            except Exception as error:
                receipt["sourcePreserved"] = False
                receipt["preservationError"] = str(error)
                result["gates"]["inspectionAccuracy"] = gate("failed", str(error))
        if inspection_copies:
            report["originalFixturesPreserved"] = all(receipt.get("sourcePreserved") is True for _, receipt in inspection_copies)
            persist()
        if watchers:
            shutdown = report.get("launcherShutdown") or {}
            receipts = shutdown.get("workerExits", [])
            report["cleanupVerified"] = (shutdown.get("hostExitCode") == 0 and shutdown.get("forcedHostStop") is False and bool(receipts)
                and all(row.get("processExitVerified") is True and row.get("exitCode") == 0 for row in receipts)
                and all(result.get("ownedProcessExit", {}).get("completed") is True for result, _ in watchers))
            persist()
            require(report["cleanupVerified"], "HMI native process/worker cleanup was not completely verified; retained process receipts require reconciliation")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-report", type=Path, required=True)
    parser.add_argument("--host", type=Path)
    parser.add_argument("--worker", type=Path)
    parser.add_argument("--captured-report", type=Path, help="Offline supplemental reassessment of an immutable captured bridge report; no native calls")
    parser.add_argument("--artifacts", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--allow-owned-derived-open", action="store_true", help="After a known failed retrieval in the same owned empty session, allow read-only inspection of the exact independently recovered positive seeder project; preserves the failed bridge recovery result")
    parser.add_argument("--inspect-owned-recovered-copy", action="store_true", help="Inspect a hash-verified fresh copy of the exact independently recovered fixture project; no bridge retrieval/compile/save, and existing failed compile gates remain failed")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    require(bool(args.host) == bool(args.worker), "Pass both exact host and worker binaries")
    require(not args.captured_report or not args.host, "Captured reassessment cannot start native binaries")
    require(not args.inspect_owned_recovered_copy or args.host and not args.captured_report and not args.allow_owned_derived_open,
            "Copied-project inspection requires exact native binaries and is separate from captured reassessment or clean-derived fallback")
    require(not args.report.exists(), "Prior qualification evidence cannot be overwritten")
    native = json.loads(args.native_report.read_text(encoding="utf-8-sig"))
    report = {"schemaVersion": 1, "observedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "qualifierSha256": sha256(Path(__file__)),
              "nativeReport": str(args.native_report.resolve()), "nativeReportSha256": sha256(args.native_report),
              "completed": False, "runtimeVerified": False, "mutationReplays": 0, "transport": "local-stdio" if args.host else "evidence-analysis-only", "profiles": []}
    report["qualificationMode"] = "owned-recovered-copy-inspection" if args.inspect_owned_recovered_copy else "archive-retrieval" if args.host else "evidence-analysis-only"
    known = {row["profile"]: row for row in native.get("profiles", [])}
    for name in PROFILES:
        profile = known.get(name, {"profile": name, "status": "blocked", "reason": "No independent native fixture evidence supplied for this profile"})
        report["profiles"].append({"profile": name, "selectedTarget": profile.get("selectedTarget"), "gates": import_native_evidence(profile)})
    args.report.parent.mkdir(parents=True, exist_ok=True)
    def persist():
        for row in report["profiles"]:
            row["acceptance"] = summarize_gates(row["gates"])
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    persist()
    try:
        if args.captured_report:
            captured = json.loads(args.captured_report.read_text(encoding="utf-8-sig"))
            require(captured.get("completed") is True, "Captured report must be complete and immutable")
            require(captured.get("nativeReportSha256") == sha256(args.native_report), "Captured report native fixture hash differs")
            report["capturedReport"] = {"path": str(args.captured_report.resolve()), "sha256": sha256(args.captured_report)}
            report["assessmentKind"] = "supplemental-offline-reassessment"
            report["nativeCallsDispatched"] = 0
            report["profiles"] = reassess_captured(native, captured)
        elif args.host:
            report["binaries"] = {name: {"path": str(path.resolve()), "sha256": sha256(path)} for name, path in (("host", args.host), ("worker", args.worker))}
            host_dll = args.host.with_suffix(".dll")
            if host_dll.is_file(): report["binaries"]["hostAssembly"] = {"path": str(host_dll.resolve()), "sha256": sha256(host_dll)}
            qualify_bridge(args, native, report, persist)
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        persist()
    print(json.dumps({"report": str(args.report), "completed": report["completed"], "acceptedProfiles": [row["profile"] for row in report["profiles"] if row["acceptance"]["accepted"]]}))
    if not report["completed"] or any(value["status"] == "failed" for row in report["profiles"] for value in row["gates"].values()): return 1
    return 0 if all(row["acceptance"]["accepted"] for row in report["profiles"]) else 2


if __name__ == "__main__":
    raise SystemExit(main())
