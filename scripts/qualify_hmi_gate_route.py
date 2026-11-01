"""Native qualification of change kinds hmi_classic_import and hmi_external_tag_create, the HMI writes that pass the closed
preservation gate (the text-list precedent, scripts/qualify_hmi_text_list_route.py).

One profile per run, on a qualification Host (--hmi-change-qualification <profile>) in a copy its own worker retrieves:
- classic-comfort (hmi_classic_import) and unified-comfort (hmi_external_tag_create) retrieve the desk-configured fixture
  (--fixture <configured.zap21> of scripts/prepare_hmi_fixture_ui.py, checked by qualify_engineering_gaps.prepared_archive);
- unified-pc has no configured fixture yet (ruling R4): --seeded-report names the HmiSeeder qualify report
  (artifacts/q/hps/native-report.json) whose positive baseline (two HMIs, two PLCs, integrated connections, LinkedSignal and
  LinkedDbValue) is retrieved as that report recorded it. Its two encrypted-transfer password errors stay; the kind's compile
  rule only requires them unchanged. A configured archive replaces it once one exists. HmiSeeder textlist-fixture, which the
  text-list route seeds, has no PLC or integrated connection, so it cannot carry an external tag.

1. Refusals before any write (plan section 5); hmi_inspect of every HMI stays unchanged.
2. Applies, each prepared in two steps and applied with checkpoint, compile and save: every apply stage, compiler errors and
   warnings as the baseline compile found them (none on a configured fixture), the target's independent readback, hmi_inspect
   of every HMI unchanged outside the intended delta, a same-key replay that returns the recorded outcome and another key
   refused RecoveryRequired. compile=false or save=false is refused; a preview prepared before the first apply is refused
   StaleRevision and stays prepared.
3. change_restore of the last change: the recovered copy equals the state before it.
4. Fresh reopen of the saved original on a production Host: hmi_inspect equals the last applied state, and the kind is refused
   with HmiPreservationUnavailable and admission null.
Interruption at each durable boundary is qualify_interruption.py with the ci-*/et-* scenarios.
"""
import argparse
import hashlib
import json
import pathlib
import re
import types
import uuid
import xml.etree.ElementTree as ET

from qualify_engineering_gaps import differences, hmi_snapshot, prepared_archive, retrieve_fits
from qualify_hmi import inspect_all
from qualify_reliability import ReliabilityClient, decoded, require

PROFILES = {"classic-comfort": ("classic", "hmi_classic_import"), "unified-comfort": ("unified", "hmi_external_tag_create"),
            "unified-pc": ("unified", "hmi_external_tag_create")}
HMIS = ("HMI_RT_1", "HMI_RT_2")
# As journaled by a complete apply of either kind (OpennessSession.HmiEngineering.cs ApplyHmiEngineeringMutation and ChangeApply).
APPLIED_STAGES = ["prechange_project_saved", "recovery_archive_created", "hmi_baseline_compiled", "hmi_native_write_started",
                  "hmi_precise_readback_verified", "mutation_applied", "compile_finished", "hmi_unrelated_content_preserved", "changed_project_saved"]
PLC_KEYS = ("projectPath", "plcPath", "scopePath", "objectPath", "kind", "name", "namespace")  # The worker's plcTag allowlist.
BACKCOLOR, ALTERNATE, ADDED_TAG, EXTERNAL = "200, 210, 220", "210, 220, 230", "SharedValueAdded", "QualificationExternal"


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def resolve_fixture(profile, fixture=None, seeded_report=None):
    """The configured archive, or (unified-pc only, R4) the seeded baseline its HmiSeeder qualify report recorded."""
    require((fixture is None) != (seeded_report is None), "Give exactly one of --fixture and --seeded-report")
    if fixture is not None:
        found = prepared_archive(fixture)
        require(found["profile"] == profile, "The configured fixture was prepared for " + str(found["profile"]))
        return {"mode": "configured", **found}
    require(profile == "unified-pc", "Only unified-pc has no configured fixture; use --fixture for " + profile)
    report_path = pathlib.Path(seeded_report).resolve(strict=True)
    raw = report_path.read_bytes()
    report = json.loads(raw.decode("utf-8-sig"))
    rows = [p for p in report.get("profiles", []) if p.get("profile") == profile]
    require(report.get("completed") is True and report.get("fixtureMode") == "positive" and len(rows) == 1,
            "The seeded report is not a completed positive HmiSeeder qualify run with one " + profile + " row")
    row, recorded = rows[0], rows[0].get("archive") or {}
    require(row.get("archiveRetrievalVerified") is True and all(link.get("isValid") is True for link in (row.get("connectionFixture") or {}).get("links") or [{}]),
            "The seeded baseline lacks its verified archive retrieval or valid integrated connections")
    archive = pathlib.Path(recorded.get("path") or "").resolve(strict=True)
    sha = digest(archive.read_bytes())
    require(sha == recorded.get("sha256"), "The seeded baseline differs from the SHA-256 its report recorded")
    return {"mode": "seeded", "path": str(archive), "sha256": sha, "profile": profile,
            "seededReport": {"path": str(report_path), "sha256": digest(raw)}}


# --- hmi_inspect reads -------------------------------------------------------------------------------------------------------

def hmi_target(device):
    return {"family": device["family"], "hmiPath": device["hmiPath"]}


def devices(client, family):
    rows = [d for d in client.tool("hmi_inspect", {"request": {"section": "devices"}})["items"] if d.get("family") == family]
    found = {d["name"]: d for d in rows}
    require(len(rows) == 2 and sorted(found) == list(HMIS), "The fixture holds exactly %s of family %s: %s" % (HMIS, family, sorted(found)))
    return found


def rows(client, device, section, **request):
    return {r["identity"]["objectPath"]: r for r in inspect_all(client, {"section": section, "target": hmi_target(device), **request})["items"]}


def snapshots(client, hmis, project):
    view = types.SimpleNamespace(client=client)  # qualify_engineering_gaps.hmi_snapshot reads through h.client.
    return {d["hmiPath"]: hmi_snapshot(view, d, project) for d in hmis.values()}


def outside(found, family, roots):
    """differences() rows that are not the intended delta: for Classic the target object's rows in any section and its own items,
    for Unified the created tag's own tags and model rows. Paths are segment-escaped, so a space separates label and path."""
    def intended(text):
        label, _, rest = text.partition(" ")
        if not rest:
            return False
        parts = rest.split(" ")
        if family == "classic" and label == "items" and parts[0] in roots:
            return True
        if family == "unified" and label not in ("tags", "model"):
            return False
        return any(parts[-1] == root or parts[-1].startswith(root + "/") for root in roots)
    return [row for row in found if not intended(row)]


def unintended(before, after, family, hmi_path=None, roots=()):
    """Per HMI, every difference outside the intended delta of hmi_path; every other HMI must be identical."""
    found = {}
    for path in sorted(set(before) | set(after)):
        rows_ = differences(before.get(path, {}), after.get(path, {}))
        extra = outside(rows_, family, roots) if path == hmi_path else rows_
        if extra:
            found[path] = extra[:20]
    return found


# --- Classic documents -------------------------------------------------------------------------------------------------------

def export(client, device, row):
    """The native export of one Classic screen of any role (section=items) or tag table (section=tags with includeSource)."""
    path = row["identity"]["objectPath"]
    request = {"section": "items", "screenPath": path} if row["identity"]["kind"] == "hmiScreen" else {"section": "tags", "objectPath": path, "includeSource": True}
    result = client.tool("hmi_inspect", {"request": {"schemaVersion": 1, "target": hmi_target(device), **request}})
    files = [a for a in result.get("artifacts", []) if a["path"].lower().endswith(".xml")]
    require(len(files) == 1, "Expected one native export of " + path)
    raw = pathlib.Path(files[0]["path"]).read_bytes()
    require(digest(raw) == files[0]["sha256"], "Native export changed after hmi_inspect: " + path)
    return raw.decode("utf-8-sig")


def root_object(xml):
    roots = [e for e in ET.fromstring(xml.encode("utf-8")) if e.tag not in ("Engineering", "DocumentInfo")]
    require(len(roots) == 1, "A Classic export holds exactly one engineering object")
    return roots[0]


def root_attribute(xml, field):
    return root_object(xml).findtext("AttributeList/" + field)


def normalized(xml):
    """The readback rule of the import (EngineeringSources.RequireNativePreservation): DocumentInfo dropped, IDs renumbered in
    document order, formatting ignored. Export IDs are local to one export (HmiPreservationRules), so they are no identity."""
    document = ET.fromstring(xml.encode("utf-8"))
    for info in document.findall("DocumentInfo"):
        document.remove(info)
    for number, element in enumerate(e for e in document.iter() if "ID" in e.attrib):
        element.set("ID", str(number))
    return ET.canonicalize(ET.tostring(document, encoding="unicode"), strip_text=True)


def set_root_attribute(xml, field, value):
    """The export with its root object's scalar replaced; the root AttributeList precedes every nested one."""
    old = root_attribute(xml, field)
    require(old is not None and old != value, "The root object has no %s to change" % field)
    edited, count = re.subn(re.escape("<%s>%s</%s>" % (field, old, field)), lambda _: "<%s>%s</%s>" % (field, value, field), xml, count=1)
    require(count == 1 and root_attribute(edited, field) == value, "The first %s element is not the root object's" % field)
    return edited


def add_tag(xml, name):
    """The tag-table export with a copy of its last tag under a new name and fresh export-local IDs, inserted after it."""
    start = xml.rfind("<Hmi.Tag.Tag ")
    end = xml.find("</Hmi.Tag.Tag>", start) + len("</Hmi.Tag.Tag>")
    require(start >= 0 and end > start, "The tag table exports no tag to copy")
    copy = xml[start:end]
    old = re.search(r"<Name>([^<]*)</Name>", copy).group(1)
    ids = iter(range(max(int(i) for i in re.findall(r' ID="(\d+)"', xml)) + 1, 10 ** 9))
    copy = re.sub(r' ID="\d+"', lambda _: ' ID="%d"' % next(ids), copy).replace("<Name>%s</Name>" % old, "<Name>%s</Name>" % name, 1)
    edited = xml[:end] + copy + xml[end:]
    names = [t.findtext("AttributeList/Name") for t in root_object(edited).iter("Hmi.Tag.Tag")]
    require(names[-1] == name and len(set(names)) == len(names), "The added tag is not unique: %s" % names)
    return edited


def with_dtd(xml):
    head, _, body = xml.partition("?>")
    return head + '?><!DOCTYPE Document [<!ENTITY x "y">]>' + body


def with_second_root(xml):
    return xml.replace("</Document>", '<Hmi.Screen.Screen ID="999"><AttributeList><Name>Second</Name></AttributeList></Hmi.Screen.Screen></Document>')


def scalar(row, value, **target):
    return {"schemaVersion": 1, "operation": "scalar_property_set", "target": {**row["identity"], **target}, "properties": {"BackColor": value}}


def classic_import(row, xml):
    return {"schemaVersion": 1, "operation": "classic_xml_import", "target": row["identity"], "xml": xml}


def classic_fixture(client, hmis):
    """Per HMI: both regular screens, the template, popup and global elements, and the QualificationTags table."""
    fx = {}
    for name, device in hmis.items():
        screens = list(rows(client, device, "screens").values())
        regular = {r["name"]: r for r in screens if r.get("screenRole") == "regular"}
        roles = {role: [r for r in screens if r.get("screenRole") == role] for role in ("template", "popup", "globalElements")}
        tables = {r["name"]: r for r in rows(client, device, "tags").values() if r["identity"]["kind"] == "hmiTagTable"}
        require({"Overview_A", "Overview_B"} <= set(regular) and all(len(found) == 1 for found in roles.values()) and "QualificationTags" in tables,
                "%s lacks a fixture screen, template, popup, global elements or QualificationTags" % name)
        fx[name] = {**regular, **{role: found[0] for role, found in roles.items()}, "QualificationTags": tables["QualificationTags"]}
    return fx


def classic_refusals(client, hmis, fx):
    rt1, kind = fx["HMI_RT_1"], "hmi_classic_import"
    device, screen = hmis["HMI_RT_1"], rt1["Overview_A"]
    own = export(client, device, screen)
    table = rt1["QualificationTags"]["identity"]
    cases = [("the same BackColor payload as hmi_engineering", "hmi_engineering", scalar(screen, BACKCOLOR), "HmiPreservationUnavailable"),
             ("an operation outside the kind (tag_create)", kind, {"schemaVersion": 1, "operation": "tag_create", "target": table, "name": "QualificationRefused", "dataType": "Int"}, "InvalidRequest"),
             ("a Unified family under the Classic kind", kind, {"schemaVersion": 1, "operation": "scalar_property_set", "target": {**screen["identity"], "family": "unified"},
                                                               "properties": {"Visible": True}}, "InvalidRequest"),
             ("a non-canonical color", kind, scalar(screen, "204,204,204"), "InvalidRequest")]
    cases += [("import of the " + label, kind, classic_import(rt1[role], export(client, device, rt1[role])), "HmiPreservationUnavailable")
              for role, label in (("template", "screen template"), ("popup", "popup"), ("globalElements", "global elements"))]
    cases += [("another screen's document", kind, classic_import(screen, export(client, device, rt1["Overview_B"])), "XmlScopeMismatch"),
              ("another root type", kind, classic_import(screen, export(client, device, rt1["QualificationTags"])), "XmlScopeMismatch"),
              ("a DTD", kind, classic_import(screen, with_dtd(own)), "InvalidRequest"),
              ("two roots", kind, classic_import(screen, with_second_root(own)), "InvalidRequest")]
    return cases


def classic_cases(fx):
    """In order: BackColor on each HMI (identity separation), no-op and GridColor imports, a no-op import of the other screen
    (both screens imported, one is the start screen), then QualificationTags with one added internal tag."""
    def compare(expected, device, row):
        def verify(client):
            actual = normalized(export(client, device, row))
            if actual == expected:
                return True, None
            at = next((i for i, (a, b) in enumerate(zip(expected, actual)) if a != b), min(len(expected), len(actual)))
            return False, {"firstDifference": at, "expected": expected[max(0, at - 80):at + 160], "actual": actual[max(0, at - 80):at + 160]}
        return verify

    def backcolor(hmi):
        def build(client, hmis):
            row = fx[hmi]["Overview_A"]
            before = export(client, hmis[hmi], row)
            value = BACKCOLOR if root_attribute(before, "BackColor") != BACKCOLOR else ALTERNATE
            return {"label": "BackColor of %s Overview_A" % hmi, "hmi": hmi, "payload": scalar(row, value), "roots": [row["identity"]["objectPath"]],
                    "verify": compare(normalized(set_root_attribute(before, "BackColor", value)), hmis[hmi], row)}
        return build

    def imported(hmi, name, edit, label):
        def build(client, hmis):
            row = fx[hmi][name]
            xml = edit(export(client, hmis[hmi], row))
            return {"label": label, "hmi": hmi, "payload": classic_import(row, xml), "roots": [row["identity"]["objectPath"]],
                    "verify": compare(normalized(xml), hmis[hmi], row)}
        return build

    return [backcolor("HMI_RT_1"), backcolor("HMI_RT_2"),
            imported("HMI_RT_1", "Overview_B", lambda xml: xml, "no-op import of HMI_RT_1 Overview_B"),
            imported("HMI_RT_1", "Overview_B", lambda xml: set_root_attribute(xml, "GridColor", "16, 32, 48"), "GridColor import of HMI_RT_1 Overview_B"),
            imported("HMI_RT_1", "Overview_A", lambda xml: xml, "no-op import of HMI_RT_1 Overview_A"),
            imported("HMI_RT_1", "QualificationTags", lambda xml: add_tag(xml, ADDED_TAG), "QualificationTags import with one added internal tag")]


# --- Unified external tags ---------------------------------------------------------------------------------------------------

def unified_fixture(client, hmis):
    """Per HMI: the table holding LinkedSignal, its connection, and the PLC identities LinkedSignal and LinkedDbValue resolve to
    (hmi_references hmiPlcDeclaration), the latter as a root global DB identity with its memberPath."""
    fx = {}
    for name, device in hmis.items():
        tags = rows(client, device, "tags")
        linked = {r["name"]: r for r in tags.values() if r["identity"]["kind"] == "hmiTag" and r["name"] in ("LinkedSignal", "LinkedDbValue")}
        require(sorted(linked) == ["LinkedDbValue", "LinkedSignal"], name + " lacks LinkedSignal or LinkedDbValue")
        signal, member = linked["LinkedSignal"], linked["LinkedDbValue"]
        connection = signal["properties"]["Connection"]
        connections = [r["identity"] for r in rows(client, device, "connections").values() if r["name"] == connection]
        table = tags[signal["identity"]["objectPath"].rsplit("/tags/", 1)[0]]["identity"]
        require(len(connections) == 1 and table["kind"] == "hmiTagTable", name + " has no unique connection " + connection + " or LinkedSignal table")
        edges = client.tool("hmi_references", {"request": {"schemaVersion": 1, "target": device["identity"], "limit": 200, "maxVisited": 50000}})["relationships"]

        def declaration(tag):
            found = [e["target"] for e in edges if e.get("relation") == "hmiPlcDeclaration" and e.get("sourcePath") == tag["identity"]["objectPath"] and e.get("resolution") == "resolved"]
            require(len(found) == 1, "%s %s has no resolved PLC declaration" % (name, tag["name"]))
            return found[0]
        root = {k: v for k, v in declaration(signal).items() if k in PLC_KEYS}
        declared = declaration(member)
        require(root.get("kind") == "tag" and declared.get("blockPath") and declared.get("pathComponents"), name + ": unexpected declaration shapes")
        block = {k: v for k, v in declared.items() if k in PLC_KEYS and k not in ("objectPath", "kind", "name")}
        block.update(kind="block", objectPath=declared["blockPath"], memberPath=list(declared["pathComponents"]))
        fx[name] = {"table": table, "connection": connections[0], "connectionName": connection, "root": root, "member": block,
                    "types": {"root": signal["properties"]["DataType"], "member": member["properties"]["DataType"]},
                    "spellings": {"root": signal["properties"]["PlcTag"], "member": member["properties"]["PlcTag"]}}
    return fx


def external(f, name, source, **changes):
    payload = {"schemaVersion": 1, "operation": "external_tag_create", "target": f["table"], "name": name, "dataType": f["types"][source],
               "connection": f["connection"], "plcTag": f["root" if source == "root" else "member"]}
    payload.update(changes)
    return payload


def unified_refusals(client, hmis, fx):
    f, other, kind = fx["HMI_RT_1"], fx["HMI_RT_2"], "hmi_external_tag_create"
    return [("the same payload as hmi_engineering", "hmi_engineering", external(f, EXTERNAL, "root"), "HmiPreservationUnavailable"),
            ("an operation outside the kind (tag_create)", kind, {"schemaVersion": 1, "operation": "tag_create", "target": f["table"], "name": "QualificationInternal", "dataType": "Int"}, "InvalidRequest"),
            ("an existing HMI tag name", kind, external(f, "LinkedSignal", "root"), "HmiNameConflict"),
            ("a type other than the PLC declaration", kind, external(f, EXTERNAL, "root", dataType="Bool" if f["types"]["root"] != "Bool" else "Int"), "TargetIncompatible"),
            ("a PLC tag of the other HMI's PLC", kind, external(f, EXTERNAL, "root", plcTag=other["root"]), "DependencyUnresolved"),
            ("the other HMI's connection", kind, external(f, EXTERNAL, "root", connection=other["connection"]), "InvalidRequest"),
            ("a software-unit scopePath", kind, external(f, EXTERNAL, "root", plcTag={**f["root"], "scopePath": f["root"]["plcPath"] + "/units/U"}), "InvalidRequest"),
            ("an array member path", kind, external(f, EXTERNAL, "member", plcTag={**f["member"], "memberPath": ["Value[0]"]}), "InvalidRequest"),
            ("a quoted member path", kind, external(f, EXTERNAL, "member", plcTag={**f["member"], "memberPath": ['"Value"']}), "InvalidRequest")]


def unified_cases(fx):
    """A root tag and a DB member on HMI_RT_1, then the same root-tag name on HMI_RT_2 (its own PLC)."""
    def created(hmi, name, source, label):
        def build(client, hmis):
            f = fx[hmi]
            path = f["table"]["objectPath"] + "/tags/" + name

            def verify(client):
                properties = (rows(client, hmis[hmi], "tags").get(path) or {}).get("properties") or {}
                observed = {key: properties.get(key) for key in ("Connection", "PlcTag", "DataType", "AccessMode")}
                expected = {"Connection": f["connectionName"], "PlcTag": f["spellings"][source], "DataType": f["types"][source], "AccessMode": "SymbolicAccess"}
                return observed == expected, None if observed == expected else {"expected": expected, "observed": observed}
            return {"label": label, "hmi": hmi, "payload": external(f, name, source), "roots": [path], "verify": verify, "absent": path}
        return build
    return [created("HMI_RT_1", EXTERNAL, "root", "root tag on HMI_RT_1"), created("HMI_RT_1", "QualificationDbExternal", "member", "DB member on HMI_RT_1"),
            created("HMI_RT_2", EXTERNAL, "root", "root tag on HMI_RT_2 (its own PLC)")]


# --- Change calls ------------------------------------------------------------------------------------------------------------

def prepare(client, kind, payload):
    """Two-step prepare: the first StaleRevision names the capture's contentHash, which the second binds."""
    payload = {**payload, "expectedContentHash": "0" * 64}
    stale = decoded(client.raw_tool("change_prepare", {"kind": kind, "payload": payload}))
    require(stale.get("error", {}).get("code") == "StaleRevision", "An unbound prepare must stop at StaleRevision: " + json.dumps(stale, default=str)[:600])
    payload["expectedContentHash"] = stale["error"]["details"]["baseline"]["contentHash"]
    return client.tool("change_prepare", {"kind": kind, "payload": payload})


def refusal(client, kind, payload):
    return decoded(client.raw_tool("change_prepare", {"kind": kind, "payload": {**payload, "expectedContentHash": "0" * 64}})).get("error") or {}


def apply_arguments(prepared, key=None, **flags):
    return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": key or uuid.uuid4().hex,
            "checkpointSave": True, "compile": True, "save": True, **flags}


def summary(value):
    """Change views carry whole HMI manifests; the report keeps what the gates read and the journal path for the rest."""
    if not isinstance(value, dict) or "changeId" not in value:
        return value
    evidence = value.get("hmiEvidence") or {}
    return {"changeId": value["changeId"], "state": value.get("state"), "stages": value.get("stages"), "failure": value.get("failure"),
            "journalPath": value.get("journalPath"), "effects": (value.get("preview") or {}).get("effects"), "compile": value.get("compile"),
            "gate": evidence.get("gate"), "baselineCompile": evidence.get("baselineCompile"), "createdTagReadback": evidence.get("createdTagReadback"),
            "baselineCompileChangedCapture": evidence.get("baselineCompileChangedCapture"), "recoveredProject": value.get("recoveredProject")}


def problems(compilation):
    """Sorted error and warning rows with text, summary rows left out; None without a message tree. The counts are no oracle: the
    first compile after a retrieve lists the seeded fixture's two password errors yet ends "errors: 0" (route run hr4)."""
    targets = (compilation or {}).get("targets")
    if not targets or any(t.get("messagesTruncated") is not False for t in targets):
        return None
    return sorted((m["state"], m["description"]) for t in targets for m in t.get("messages") or []
                  if m.get("state") in ("Error", "Warning") and m.get("description") and not m["description"].startswith("Compiling finished"))


def run(args, connect):
    """One profile; connect(store, hostArguments, toolCalls) returns an MCP client (ReliabilityClient on a real run)."""
    profile = args.profile
    family, kind = PROFILES[profile]
    report = {"schemaVersion": 1, "completed": False, "nativeExecuted": True, "runtimeVerified": False, "kind": kind, "profile": profile,
              "checks": [], "failures": [], "steps": [], "replays": [], "toolCalls": []}

    def gate(value, label, detail=None):
        (report["checks"] if value else report["failures"]).append(label if value or detail is None else label + ": " + json.dumps(detail, ensure_ascii=False, default=str)[:1500])
        return value

    def step(name, value):
        report["steps"].append({"name": name, "result": summary(value)})
        print("  " + name, flush=True)
        return value

    fixture = report["fixture"] = resolve_fixture(profile, args.fixture, args.seeded_report)
    store = args.base / "h"
    require(retrieve_fits(store), "Choose a shorter --base: project_retrieve refuses a destination over 80 characters")
    fixture_of, refusals_of, cases_of = (classic_fixture, classic_refusals, classic_cases) if family == "classic" else (unified_fixture, unified_refusals, unified_cases)
    client = connect(store, ["--hmi-change-qualification", profile], report["toolCalls"])
    project, history = None, []
    try:
        client.initialize()
        step("tia_start", client.tool("tia_start"))
        project = step("project_retrieve fixture", client.tool("project_retrieve", {"archivePath": fixture["path"], "expectedSha256": fixture["sha256"]}))["project"]["path"]
        hmis = devices(client, family)
        gate(all(d.get("profile") == profile for d in hmis.values()), "Both fixture HMIs have the native profile " + profile, {n: d.get("profile") for n, d in hmis.items()})
        fx = fixture_of(client, hmis)
        reference = snapshots(client, hmis, project)

        # 1. Refusals before any write.
        for label, refused_kind, payload, code in refusals_of(client, hmis, fx):
            error = refusal(client, refused_kind, payload)
            gate(error.get("code") == code, "Refused before any write: %s (%s)" % (label, code), {"code": error.get("code"), "message": error.get("message")})
        changed = unintended(reference, snapshots(client, hmis, project), family)
        gate(not changed, "Refusals leave hmi_inspect of every HMI unchanged", changed)

        # 2. Applies; the last change, prepared first, is the stale probe for the first apply.
        cases = cases_of(fx)
        stale = step("change_prepare stale probe", prepare(client, kind, cases[-1](client, hmis)["payload"]))
        for index, build in enumerate(cases):
            case = build(client, hmis)
            label, device = case["label"], hmis[case["hmi"]]
            if "absent" in case:
                gate(case["absent"] not in rows(client, device, "tags"), "Absent before the change: " + label)
            prepared = step("change_prepare " + label, prepare(client, kind, case["payload"]))
            admission = prepared["preview"]["hmiEvidence"]["gate"]
            gate(admission.get("available") is True and admission.get("admission") == "qualification" and admission.get("targetProfile") == profile,
                 "Gate admits this kind and profile by qualification only: " + label, admission)
            if index == 0:
                for flags in ({"compile": False, "save": False}, {"compile": True, "save": False}):
                    refused = decoded(client.raw_tool("change_apply", apply_arguments(prepared, **flags))).get("error") or {}
                    gate(refused.get("code") == "InvalidRequest", "Apply with compile=%s save=%s is refused" % (flags["compile"], flags["save"]), refused.get("code"))
            key = uuid.uuid4().hex
            result = step("change_apply " + label, client.tool("change_apply", apply_arguments(prepared, key)))
            evidence = result.get("hmiEvidence") or {}
            gate(result.get("state") == "applied" and result.get("stages") == APPLIED_STAGES, "Applied with every stage: " + label,
                 {"state": result.get("state"), "stages": result.get("stages"), "failure": result.get("failure")})
            baseline, compiled = problems(evidence.get("baselineCompile")), problems(result.get("compile"))
            gate(compiled is not None and compiled == baseline and (fixture["mode"] == "seeded" or compiled == []),
                 "Compiler errors and warnings as the baseline compile found them%s: %s" % ("" if fixture["mode"] == "seeded" else " (none)", label), {"baseline": baseline, "after": compiled})
            # Same key: the recorded outcome without a second write; another key: RecoveryRequired before admission.
            again = decoded(client.raw_tool("change_apply", apply_arguments(prepared, key)))
            other = decoded(client.raw_tool("change_apply", apply_arguments(prepared)))
            journal = json.loads(pathlib.Path(result["journalPath"]).read_text(encoding="utf-8"))
            report["replays"].append({"label": label, "sameKey": "recordedOutcome" if "error" not in again else again["error"].get("code"),
                                      "otherKey": (other.get("error") or {}).get("code") or other.get("state")})
            gate("error" not in again and again.get("state") == "applied" and again.get("stages") == result.get("stages") and journal.get("stages") == result.get("stages"),
                 "Replay with the same key returns the recorded outcome: " + label, again.get("error") or again.get("state"))
            gate((other.get("error") or {}).get("code") == "RecoveryRequired", "Another key is refused RecoveryRequired: " + label, other.get("error") or other.get("state"))
            ok, detail = case["verify"](client)
            gate(ok, "Independent readback of the target: " + label, detail)
            after = snapshots(client, hmis, project)
            extra = unintended(reference, after, family, device["hmiPath"], case["roots"])
            if index == 0 and evidence.get("baselineCompileChangedCapture") is True:
                # The worker compares with its compiled capture; differences of the first compile are recorded, not attributed.
                report["firstCompileChangedRows"] = extra
            else:
                gate(not extra, "hmi_inspect of every HMI unchanged outside the intended delta: " + label, extra)
            if index == 0:
                refused = decoded(client.raw_tool("change_apply", apply_arguments(stale))).get("error") or {}
                gate(refused.get("code") == "StaleRevision", "A preview prepared before another saved change is refused", refused.get("code"))
                gate(json.loads(pathlib.Path(stale["journalPath"]).read_text(encoding="utf-8")).get("state") == "prepared", "The stale preview stays prepared and unclaimed")
            history.append((label, prepared, reference, after))
            reference = after

        # 3. Restore the last change's checkpoint into a new directory; the saved original keeps the last state.
        label, last, before_last, _ = history[-1]
        restored = step("change_restore " + label, client.tool("change_restore", {"changeId": last["changeId"], "previewHash": last["previewHash"]}))
        gate(restored.get("state") == "restored" and "hmi_recovered_content_equality_verified" in (restored.get("stages") or []),
             "Checkpoint restored with HMI content equality", restored.get("stages"))
        changed = unintended(before_last, snapshots(client, hmis, restored["recoveredProject"]["path"]), family)
        gate(not changed, "The restored copy equals the state before the last change", changed)
        client.tool("tia_detach")
    except Exception as error:
        gate(False, "Route run completed", repr(error))
    finally:
        client.close()
        report["hostExitCode"] = client.process.returncode

    # 4. Fresh reopen of the saved original on a production Host (no qualification profile).
    if project is None or not history:
        return report
    fresh = connect(store, [], report["toolCalls"])
    try:
        fresh.initialize()
        step("fresh tia_start", fresh.tool("tia_start"))
        step("project_open saved original", fresh.tool("project_open", {"path": project}))
        hmis = devices(fresh, family)
        changed = unintended(history[-1][3], snapshots(fresh, hmis, project), family)
        gate(not changed, "Fresh reopen: hmi_inspect equals the last applied state", changed)
        fx = fixture_of(fresh, hmis)
        payload = scalar(fx["HMI_RT_1"]["Overview_B"], ALTERNATE) if family == "classic" else external(fx["HMI_RT_1"], "QualificationReopen", "root")
        error = refusal(fresh, kind, payload)
        admission = (error.get("details") or {}).get("gate") or {}
        gate(error.get("code") == "HmiPreservationUnavailable" and admission.get("available") is False and admission.get("admission") is None,
             "A production Host refuses the kind with admission null", {"code": error.get("code"), "gate": {k: admission.get(k) for k in ("available", "admission", "reasonCode")}})
        fresh.tool("tia_detach")
    except Exception as error:
        gate(False, "Fresh reopen completed", repr(error))
    finally:
        fresh.close()
        report["freshHostExitCode"] = fresh.process.returncode
    report["completed"] = not report["failures"]
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--base", type=pathlib.Path, required=True, help="Short new artifact base; TIA limits project path length.")
    parser.add_argument("--profile", choices=sorted(PROFILES), required=True)
    parser.add_argument("--fixture", type=pathlib.Path, help="configured.zap21 of a completed scripts/prepare_hmi_fixture_ui.py run")
    parser.add_argument("--seeded-report", type=pathlib.Path, help="unified-pc only: the HmiSeeder qualify native-report.json (artifacts/q/hps)")
    args = parser.parse_args(argv)
    args.base = args.base.resolve()
    require(not args.base.exists(), "Fresh evidence directory required")
    require(len(str(args.base)) <= 50, "TIA refuses long project paths; choose a shorter base")
    args.base.mkdir(parents=True)
    host, worker = args.host.resolve(strict=True), args.worker.resolve(strict=True)
    report = run(args, lambda store, extra, calls: ReliabilityClient(host, worker, store, timeout=900, record=calls, extra=extra))
    report.update(hostSha256=digest(host.read_bytes()), workerSha256=digest(worker.read_bytes()))
    (args.base / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"profile": args.profile, "completed": report["completed"], "checks": len(report["checks"]), "failures": report["failures"]}, ensure_ascii=False))
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
