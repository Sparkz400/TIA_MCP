"""Run typed operand resolution qualification in a new owned disposable scratch.

No PLC/simulator access; no mutation replay. Positive cases retain separate
compile, complete XML, reopen and checkpoint recovery gates. Duplicate-tag
fixture rejection is reported separately from native ambiguity qualification.
"""
import argparse
import copy
import hashlib
import json
import pathlib
import subprocess
import stat
import uuid
from xml.dom import minidom
from xml.etree import ElementTree as ET

from block_test_runtime import NativeToolError, canonical, utc_now
from qualification_owned_scratch import OwnedScratch, assert_snapshot, complete_inventory, native_hash, require, snapshot
from run_block_tests import save_json
from run_conformance import native_document, safe_xml, structural_xml
from smoke_mcp import ROOT
from typed_graphical_resolution_cases import MANIFEST, bind_case
from typed_graphical_transport import artifact_bytes
from typed_graphical_native_expectations import adapt_reference_culture, project_culture
from engineering_preservation import PAYLOAD_HASH_POLICY, native_payload

STANDARD = ("tag-exact-owner", "tag-wrong-table", "db-first-owner", "db-second-owner", "db-wrong-kind", "db-missing-member", "db-wrong-type")
NAMESPACE = ("nested-udt-namespace", "nested-udt-wrong-type", "nested-udt-missing-member")
STALE = ("tag-duplicate-setup-guard", "stale-tag-address", "stale-db-ambiguity")
SOURCES = ROOT / "fixtures/typed-graphical/sources"


def artifact_policy(artifacts, policy_root):
    require(artifacts.is_absolute() and policy_root.is_absolute(), "Native artifact policy requires absolute paths.")
    allowed = policy_root.resolve()
    selected = artifacts.resolve()
    require(allowed.parent != allowed and selected.is_relative_to(allowed), "Native artifact directory escapes its explicit policy root.")
    for path in (policy_root, artifacts):
        cursor = path
        while True:
            if cursor.exists():
                require(not cursor.is_symlink() and not (getattr(cursor.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT),
                        "Native artifact policy rejects reparse-point paths.")
            if cursor.parent == cursor:
                break
            cursor = cursor.parent
    return {"root": str(allowed), "selectedDirectory": str(selected), "policy": "fresh child only; reopen requires owned receipt"}


def select(rows, name, scope=None, namespace=None):
    matches = [row for row in rows if row["name"] == name and (scope is None or row.get("scopePath") == scope)
               and (namespace is None or ("/namespaces/" + namespace + "/") in row["path"])]
    require(len(matches) == 1, "Observed identity is missing/ambiguous: " + name)
    return matches[0]


def namespace_type_bindings(inventory, scope):
    """Require native identities for the selected UDT chain and its Int decoy."""
    coverage = inventory.get("completeness", {})
    require(coverage.get("scopeComplete") is True and coverage.get("truncated") is False,
            "Namespace fixture requires complete observed identities.")
    return {role: select(inventory["types"], name, scope, namespace)["path"] for role, name, namespace in (
        ("outerType", "Tgp_ResolutionOuter", "TiaMcp_UnitA.Alternate"),
        ("innerType", "Tgp_ResolutionInner", "TiaMcp_UnitA.Alternate"),
        ("decoyType", "Tgp_ResolutionInner", "TiaMcp_UnitA"))}


def unqualified_outer_fixture(xml):
    """One diagnostic reference-spelling edit; retain all native opaque content."""
    observed = safe_xml(xml)
    document = minidom.parseString(xml)
    blocks = document.getElementsByTagName("SW.Types.PlcStruct")
    require(len(blocks) == 1, "Namespace probe requires exactly one native UDT.")
    block = observed.find("SW.Types.PlcStruct")
    require(block.findtext("AttributeList/Name") == "Tgp_ResolutionOuter"
            and block.findtext("AttributeList/Namespace") == "TiaMcp_UnitA.Alternate",
            "Namespace probe requires the exact observed Alternate Outer type.")
    members = [node for node in document.getElementsByTagName("Member") if node.getAttribute("Name") == "Child"]
    require(len(members) == 1 and members[0].getAttribute("Datatype") == '"TiaMcp_UnitA.Alternate.Tgp_ResolutionInner"',
            "Namespace probe requires one exact independently captured qualified Child declaration.")
    members[0].setAttribute("Datatype", '"Tgp_ResolutionInner"')
    return document.toxml()


def empty_tag_template(inventory, scope):
    """System constants are inventory metadata, not evidence of user content.

    Native default-table exports omit generated hardware constants. The XML is
    independently checked by tag_fixture; no constant is stripped or copied.
    """
    tables = [row for row in inventory["tagTables"] if row["scopePath"] == scope
              and not row["tags"] and all(value.get("kind") == "system" for value in row["constants"])]
    require(len(tables) == 1, "Scratch requires one exact standard table with no user tags/constants.")
    require(not any(row["name"] in {"Tgp_ResolutionTags", "Tgp_ResolutionDuplicate"}
                    for row in inventory["tagTables"]), "Resolution fixture table already exists.")
    return tables[0]


def tag_fixture(empty_xml, name="Tgp_ResolutionTags", tag_name="Ready", address="%M100.0"):
    document = safe_xml(empty_xml)
    table = document.find("SW.Tags.PlcTagTable")
    require(table is not None and not list(document.iter("SW.Tags.PlcTag"))
            and not list(document.iter("SW.Tags.PlcUserConstant")) and not list(document.iter("SW.Tags.PlcSystemConstant")),
            "Tag fixture requires an independently observed empty native table.")
    table.find("AttributeList/Name").text = name
    objects = table.find("ObjectList")
    if objects is None:
        objects = ET.SubElement(table, "ObjectList")
    ids = [int(node.get("ID"), 16) for node in document.iter() if node.get("ID")]
    tag = ET.SubElement(objects, "SW.Tags.PlcTag", {"ID": format(max(ids, default=0) + 1, "X"), "CompositionName": "Tags"})
    attributes = ET.SubElement(tag, "AttributeList")
    for key, value in (("Name", tag_name), ("DataTypeName", "Bool"), ("LogicalAddress", address)):
        ET.SubElement(attributes, key).text = value
    return ET.tostring(document, encoding="unicode")


def nested_caller_fixture(xml, language):
    safe_xml(xml)
    document = minidom.parseString(xml)
    block = document.getElementsByTagName("SW.Blocks.FB")
    require(len(block) == 1, "Nested fixture requires the retained unit-native FB.")
    attrs = next(node for node in block[0].childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName == "AttributeList")
    for field, value in (("Name", "Tgp_ResolutionNested_" + language), ("Namespace", "TiaMcp_UnitA")):
        node = next(node for node in attrs.childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName == field)
        while node.firstChild:
            node.removeChild(node.firstChild)
        node.appendChild(document.createTextNode(value))
    sections = document.getElementsByTagName("Section")
    inputs = [node for node in sections if node.getAttribute("Name") == "Input"]
    require(len(inputs) == 1, "Caller must have one native Input section.")
    member = document.createElementNS(inputs[0].namespaceURI, "Member")
    for key, value in (("Name", "State"), ("Datatype", '"TiaMcp_UnitA.Alternate.Tgp_ResolutionOuter"'), ("Accessibility", "Public")):
        member.setAttribute(key, value)
    inputs[0].appendChild(member)
    return document.toxml()


def new_autonumber_fixture(xml, expected_name, inventory):
    """New fixture creation only: omit the captured block Number, retain IDs/XML.

    UnitSeeder allocates FC2/FC3 before standard fixture creation. A retained
    export's historical Number is not a requested identity for a new fixture.
    """
    coverage = inventory.get("completeness", {})
    require(coverage.get("scopeComplete") is True and coverage.get("truncated") is False,
            "Auto-numbered fixture creation requires a complete observed inventory.")
    require(not any(row["name"].casefold() == expected_name.casefold() for row in inventory["blocks"]),
            "Auto-number fixture creation cannot replace an existing same-name block.")
    safe_xml(xml)
    document = minidom.parseString(xml)
    blocks = [node for node in document.documentElement.childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName in {"SW.Blocks.FC", "SW.Blocks.FB"}]
    require(len(blocks) == 1, "Auto-number fixture requires one new FC or FB.")
    attrs = [node for node in blocks[0].childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName == "AttributeList"]
    require(len(attrs) == 1, "Fixture requires one native attribute list.")
    def fields(name):
        return [node for node in attrs[0].childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName == name]
    def value(node):
        return "".join(child.data for child in node.childNodes if child.nodeType == child.TEXT_NODE)
    require(len(fields("Name")) == 1 and value(fields("Name")[0]) == expected_name,
            "Fixture native name differs from its exact expected creation name.")
    require(len(fields("AutoNumber")) == 1 and value(fields("AutoNumber")[0]) == "true",
            "Only independently captured AutoNumber=true fixtures can omit Number.")
    require(len(fields("Number")) == 1 and value(fields("Number")[0]).isdigit(),
            "Fixture requires one captured numeric block Number.")
    attrs[0].removeChild(fields("Number")[0])
    return document.toxml()


def apply_arguments(prepared):
    return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"],
            "idempotencyKey": uuid.uuid4().hex, "checkpointSave": True, "compile": True, "save": True}


def expect_rejection(call, tool, arguments, codes):
    try:
        result = call(tool, arguments)
    except NativeToolError as error:
        require(error.result.get("error", {}).get("code") in codes, "Unrelated rejection: " + str(error))
        return error.result
    raise RuntimeError("Expected rejection, but " + tool + " returned " + canonical(result))


def verify_prepared(prepared, expected):
    typed = prepared["preview"]["typed"]
    inputs = [row for row in typed["resolvedOperands"] if row["key"] == "input"]
    require(len(inputs) == 1 and inputs[0]["identity"] == expected["operandIdentity"]
            and inputs[0]["dataType"] == expected["dataType"], "Resolved operand differs from independently selected identity/type.")
    dependencies = {row["path"] for row in prepared["preview"]["declarationDependencies"] if row["kind"] == "type"}
    require(dependencies == set(expected.get("typeDependencyPaths", [])), "Nested UDT dependency selection differs from the explicit namespace oracle.")


def verify_boolean_native(xml, network_id, language, expected):
    """Independent Boolean wiring oracle; full native XML is compared separately."""
    document = safe_xml(xml)
    units = [node for node in document.iter() if node.tag == "SW.Blocks.CompileUnit" and node.get("ID") == network_id]
    require(len(units) == 1, "Selected native network missing or ambiguous.")
    local = lambda node: node.tag.rsplit("}", 1)[-1]
    nets = [node for node in units[0].iter() if local(node) == "FlgNet"]
    require(len(nets) == 1, "Expected one Boolean native network.")
    parts = next(node for node in nets[0] if local(node) == "Parts")
    ids = {}
    for node in parts:
        key = None
        if local(node) == "Access":
            components = [child.get("Name") for child in node.iter() if local(child) == "Component"]
            if node.get("Scope") == expected["nativeScope"] and components == expected["nativeComponents"]:
                key = "input"
            elif node.get("Scope") == "LocalVariable" and components == expected["outputComponents"]:
                key = "output"
        elif local(node) == "Part" and node.get("Name") in {"Contact", "Coil"}:
            require(not list(node), "Boolean instruction has unexpected modifiers.")
            key = node.get("Name")
        require(key is not None and key not in ids and node.get("UId"), "Unexpected/duplicate native Boolean access or instruction.")
        ids[key] = node.get("UId")
    require(set(ids) == ({"input", "output", "Contact", "Coil"} if language == "LAD" else {"input", "output", "Coil"}), "Boolean instruction inventory differs.")
    reverse = {value: key for key, value in ids.items()}
    require(len(reverse) == len(ids), "Native Boolean identifiers collide.")
    observed = []
    for wire in next(node for node in nets[0] if local(node) == "Wires"):
        ends = []
        for end in wire:
            if local(end) == "Powerrail":
                ends.append(("rail", "out"))
            else:
                require(local(end) in {"IdentCon", "NameCon"} and end.get("UId") in reverse, "Unresolved native Boolean connector.")
                ends.append((reverse[end.get("UId")], end.get("Name", "value")))
        observed.append(tuple(sorted(ends)))
    wanted = [(('output', 'value'), ('Coil', 'operand'))]
    wanted += [(('input', 'value'), ('Contact', 'operand')), (('rail', 'out'), ('Contact', 'in')), (('Contact', 'out'), ('Coil', 'in'))] if language == "LAD" else [(('input', 'value'), ('Coil', 'in'))]
    require(sorted(observed) == sorted(tuple(sorted(wire)) for wire in wanted), "Independent native Boolean wiring differs.")


def verify_untouched_caller(before_xml, prepared_xml, network_id):
    def outside_selected_network(xml):
        native_payload(xml)
        document = minidom.parseString(xml)
        units = [node for node in document.getElementsByTagName("SW.Blocks.CompileUnit") if node.getAttribute("ID") == network_id]
        require(len(units) == 1, "Exact original network disappeared during replacement.")
        networks = [node for node in units[0].getElementsByTagName("*") if node.localName == "FlgNet"]
        require(len(networks) == 1, "Expected one selected native network payload.")
        networks[0].parentNode.removeChild(networks[0])
        return native_payload(document.toxml())
    require(outside_selected_network(before_xml) == outside_selected_network(prepared_xml),
            "Prepared caller changed native content outside the selected FlgNet.")


def verify_tag_address_change(before, after, owner_path, member_name, old_address, new_address):
    """Independently allow only the intended native tag address to differ."""
    assert_snapshot(before, after, except_paths={owner_path})
    key = "tagTable:" + owner_path
    require(key in before["objects"] and key in after["objects"], "Exact changed tag table is absent.")
    before_xml = pathlib.Path(before["objects"][key]["file"]).read_bytes().decode("utf-8-sig")
    native_payload(before_xml)
    expected = minidom.parseString(before_xml)
    tables = list(expected.getElementsByTagName("SW.Tags.PlcTagTable"))
    require(len(tables) == 1, "Expected one native table in the deliberate tag fixture.")
    def field(node, name):
        attributes = [child for child in node.childNodes if child.nodeType == child.ELEMENT_NODE and child.tagName == "AttributeList"]
        require(len(attributes) == 1, "Expected one exact native AttributeList.")
        fields = [child for child in attributes[0].childNodes if child.nodeType == child.ELEMENT_NODE and child.tagName == name]
        require(len(fields) == 1, "Expected one exact native field: " + name)
        return fields[0]
    def text(node):
        return "".join(child.data for child in node.childNodes if child.nodeType in (child.TEXT_NODE, child.CDATA_SECTION_NODE))
    members = [node for node in tables[0].getElementsByTagName("SW.Tags.PlcTag") if text(field(node, "Name")) == member_name]
    require(len(members) == 1, "Exact native tag member is missing or ambiguous.")
    address = field(members[0], "LogicalAddress")
    require(text(address) == old_address, "Original native tag address differs from the fixture oracle.")
    values = [node for node in address.childNodes if node.nodeType in (node.TEXT_NODE, node.CDATA_SECTION_NODE)]
    require(len(values) == 1, "Expected one exact native address text node.")
    values[0].data = new_address
    actual_xml = pathlib.Path(after["objects"][key]["file"]).read_bytes().decode("utf-8-sig")
    require(native_payload(expected.toxml()) == native_payload(actual_xml), "Deliberate tag edit changed native XML beyond the exact address.")


def verify_competing_db_change(before, after, scope_path):
    """The ambiguity fixture may add only its exact namespaced competing DB."""
    added = set(after["objects"]) - set(before["objects"])
    require(len(added) == 1 and set(before["objects"]) < set(after["objects"]),
            "Competing DB setup must add exactly one native object and delete none.")
    key = next(iter(added))
    row = after["objects"][key]
    observed = select(after["inventory"]["blocks"], "Tgp_ResolutionOne", scope_path, "TiaMcp_UnitB")
    require(row["kind"] == "block" and row["path"] == observed["path"], "The added object is not the exact competing DB identity.")
    assert_snapshot(before, {"objects": {name: value for name, value in after["objects"].items() if name != key}})
    document = native_document(pathlib.Path(row["file"]).read_bytes().decode("utf-8-sig"))
    block = document.find("SW.Blocks.GlobalDB")
    require(block is not None and block.findtext("AttributeList/Name") == "Tgp_ResolutionOne"
            and block.findtext("AttributeList/Namespace") == "TiaMcp_UnitB", "Competing DB native kind/name/namespace differs.")
    local = lambda node: node.tag.rsplit("}", 1)[-1]
    members = [node for node in block.find("AttributeList/Interface").iter() if local(node) == "Member"]
    require(len(members) == 2, "Competing DB declaration inventory differs from authored source.")
    for name, datatype, value in (("Flag", "Bool", "false"), ("RetainedMarker", "Int", "91")):
        matches = [node for node in members if node.get("Name") == name and node.get("Datatype") == datatype]
        require(len(matches) == 1 and [node.text.lower() for node in matches[0] if local(node) == "StartValue"] == [value],
                "Competing DB declaration differs from authored source: " + name)
    return observed["path"]


class ResolutionRun:
    def __init__(self, args):
        self.args = args
        policy = artifact_policy(args.artifacts, getattr(args, "artifact_policy_root", None) or (ROOT / "artifacts"))
        self.output = args.output.resolve()
        self.output.mkdir(parents=True, exist_ok=False)
        self.native = args.artifacts.resolve() / ("r" + uuid.uuid4().hex[:6])
        self.native.mkdir(parents=True, exist_ok=False)
        self.report = {"schemaVersion": 1, "status": "running", "startedAtUtc": utc_now(), "phase": args.phase,
            "language": args.language, "physicalPlcAccess": False, "runtimeVerified": False, "cases": [], "fixtureChanges": [],
            "nativeArtifactRoot": str(self.native), "fixtureInputs": [], "qualificationScope": "Owned local engineering only; runtime not run.",
            "payloadHashPolicy": PAYLOAD_HASH_POLICY}
        self.report["artifactPolicy"] = policy
        self.report["harnessFiles"] = [{"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
            for path in (pathlib.Path(__file__), pathlib.Path(__file__).with_name("qualification_owned_scratch.py"),
                         pathlib.Path(__file__).with_name("typed_graphical_resolution_cases.py"),
                         pathlib.Path(__file__).with_name("engineering_preservation.py"),
                         pathlib.Path(__file__).with_name("run_block_tests.py"), MANIFEST)]
        self.session = None
        self.pending_mutation = None
        self.unresolved_stale_apply = None
        self.restore_attempts = set()
        self.baseline = None
        self.manifest = {case["id"]: case for case in json.loads(MANIFEST.read_bytes())["cases"]}
        self.persist()

    def persist(self):
        save_json(self.output / "result.json", self.report)

    def fixture_text(self, path):
        data = path.read_bytes()
        self.report["fixtureInputs"].append({"path": str(path.resolve()), "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)})
        self.persist()
        return data.decode("utf-8-sig")

    def apply(self, kind, payload):
        if kind == "block_import_xml":
            # Fixtures were captured in a de-DE project; only empty text items follow the observed project culture.
            if getattr(self, "culture", None) is None:
                self.culture = project_culture(self.session.call("block_read", {"blockName": "Main"})["xml"])
            xml, retargeted = adapt_reference_culture(payload["xml"], self.culture)
            self.report.setdefault("cultureAdaptations", []).append({"projectCulture": self.culture, "itemsRetargeted": retargeted,
                "expectedBlockNames": payload.get("expectedBlockNames")})
            payload = {**payload, "xml": xml}
        prepared = self.session.call("change_prepare", {"kind": kind, "payload": payload})
        self.pending_mutation = prepared
        row = {"kind": kind, "prepared": prepared, "applyDispatched": True}
        self.report["fixtureChanges"].append(row)
        self.persist()
        applied = self.session.call("change_apply", apply_arguments(prepared))
        row["applied"] = applied
        require(applied.get("state") == "applied" and applied.get("compile", {}).get("errorCount") == 0
                and "changed_project_saved" in applied.get("stages", []), "Fixture change did not apply/compile/save.")
        self.pending_mutation = None
        self.persist()
        return prepared, applied

    def setup(self):
        self.session.start()
        scratch = self.session.call("tia_create_scratch", {"cpuTypeIdentifier": "OrderNumber:6ES7 511-1AL03-0AB0/V4.1"})
        self.report["scratch"] = scratch
        needs_units = self.args.phase == "namespace" or "stale-db-ambiguity" in self.args.case
        if needs_units:
            self.seed_units()
        inventory = complete_inventory(self.session.call)
        self.scope = inventory["plcPath"]
        name = "Tgp_TemplateFC_" + self.args.language
        initial_caller = self.fixture_text(ROOT / "fixtures/typed-graphical/native" / (name + ".xml"))
        if needs_units:
            initial_caller = new_autonumber_fixture(initial_caller, name, inventory)
            (self.output / "authored-standard-caller.xml").write_text(initial_caller, encoding="utf-8")
        self.apply("block_import_xml", {"xml": initial_caller, "expectedBlockNames": [name]})
        self.apply("source_upsert", {"language": "SCL", "source": self.fixture_text(SOURCES / "resolution-dbs.scl"),
                                   "expectedBlockNames": ["Tgp_ResolutionOne", "Tgp_ResolutionTwo"]})
        inventory = complete_inventory(self.session.call)
        self.empty_table = empty_tag_template(inventory, self.scope)
        self.empty_xml = artifact_bytes(self.session.call, self.session.call("tag_table_read", {"tagTablePath": self.empty_table["path"]})["artifact"]).decode("utf-8-sig")
        template = self.output / "observed-empty-tag-table.xml"
        template.write_text(self.empty_xml, encoding="utf-8")
        self.report["tagTemplate"] = {"inventory": self.empty_table, "file": str(template),
            "sha256": hashlib.sha256(template.read_bytes()).hexdigest(), "policy": "Empty exported XML; generated system constants remain in the untouched source table."}
        self.persist()
        self.apply("tag_table_import_xml", {"xml": tag_fixture(self.empty_xml), "expectedTableNames": ["Tgp_ResolutionTags"]})
        if self.args.phase == "namespace":
            unit = select(complete_inventory(self.session.call)["softwareScopes"], "TiaMcp_UnitA")
            for source, names in (("resolution-decoy-type.scl", ["Tgp_ResolutionInner"]),
                                  ("resolution-nested-types.scl", ["Tgp_ResolutionInner", "Tgp_ResolutionOuter"])):
                self.apply("type_source_upsert", {"scopePath": unit["path"], "source": self.fixture_text(SOURCES / source), "expectedTypeNames": names})
            self.report["namespaceFixtureIdentities"] = namespace_type_bindings(complete_inventory(self.session.call), unit["path"])
            self.report["namespaceFixtureScope"] = "Fully qualified nested UDT chain; unqualified nested namespace propagation not qualified."
            self.persist()
            if getattr(self.args, "namespace_unqualified", False):
                self.probe_unqualified_namespace(unit["path"])
            xml = self.fixture_text(ROOT / "fixtures/typed-graphical/native" / ("Tgp_Unit_" + self.args.language + ".xml"))
            authored = nested_caller_fixture(xml, self.args.language)
            authored = new_autonumber_fixture(authored, "Tgp_ResolutionNested_" + self.args.language, complete_inventory(self.session.call))
            (self.output / "authored-nested-caller.xml").write_text(authored, encoding="utf-8")
            self.apply("block_import_xml", {"scopePath": unit["path"], "xml": authored,
                "expectedBlockNames": ["Tgp_ResolutionNested_" + self.args.language]})
        self.persist()

    def probe_unqualified_namespace(self, scope):
        outer = self.report["namespaceFixtureIdentities"]["outerType"]
        before = snapshot(self.session.call, self.output / "namespace-probe-before")
        self.baseline = before
        observed = artifact_bytes(self.session.call, self.session.call("type_read", {"typePath": outer})["artifact"]).decode("utf-8-sig")
        authored = unqualified_outer_fixture(observed)
        path = self.output / "authored-unqualified-outer.xml"
        path.write_text(authored, encoding="utf-8")
        probe = {"status": "authored-not-executed", "path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "expectedDatatype": '"Tgp_ResolutionInner"', "nativeAccepted": False, "propagationQualified": False}
        self.report["namespaceReferenceProbe"] = probe
        self.persist()
        probe["status"] = "native-import-dispatched"
        self.persist()
        try:
            prepared, applied = self.apply("plc_type_import_xml", {"scopePath": scope, "xml": authored, "expectedTypeNames": ["Tgp_ResolutionOuter"]})
        except Exception as error:
            probe["status"] = "native-attempt-failed" if isinstance(error, NativeToolError) else "native-outcome-unverified"
            probe["failure"] = {"type": type(error).__name__, "message": str(error)}
            self.persist()
            raise
        # Keep this exact change recoverable if readback canonicalizes or rejects
        # the requested unqualified spelling. A qualified export proves no such path.
        self.pending_mutation = prepared
        after = snapshot(self.session.call, self.output / "namespace-probe-after")
        assert_snapshot(before, after, except_paths=(outer,))
        readback = artifact_bytes(self.session.call, self.session.call("type_read", {"typePath": outer})["artifact"]).decode("utf-8-sig")
        members = [node for node in safe_xml(readback).iter() if node.tag.rsplit("}", 1)[-1] == "Member" and node.get("Name") == "Child"]
        probe["observedDatatypes"] = [node.get("Datatype") for node in members]
        probe["nativeAccepted"] = applied["compile"]["errorCount"] == 0
        probe["status"] = "native-readback-observed"
        self.persist()
        require(len(members) == 1 and members[0].get("Datatype") == probe["expectedDatatype"],
                "Native UDT readback does not retain the unqualified reference; namespace propagation remains unqualified.")
        require(native_hash(authored) == native_hash(readback), "Native UDT changed outside the exact unqualified declaration spelling.")
        self.report["namespaceFixtureScope"] = "Native unqualified Inner reference resolved from Alternate Outer with a parent-namespace Int decoy; exact dependency paths required."
        probe["status"] = "native-unqualified-fixture-established"
        self.pending_mutation = self.baseline = None
        self.persist()

    def seed_units(self):
        require(self.args.unit_seeder is not None and self.args.unit_seeder.is_file(), "Namespace/DB-ambiguity setup requires the existing guarded UnitSeeder binary.")
        self.session.guard()
        authorization = self.native / "unit-authorization.json"
        authorization.write_text(canonical({"processId": self.session.session["processId"], "projectPath": self.session.current_project,
            "ownsPortalProcess": True, "creationTool": "tia_create_scratch"}), encoding="utf-8")
        executable = self.args.unit_seeder.resolve()
        row = {"path": str(executable), "sha256": hashlib.sha256(executable.read_bytes()).hexdigest(), "mode": "seed", "dispatched": True}
        self.report["unitSeeder"] = row
        self.persist()
        # One bounded invocation only. Never replay a timeout or uncertain result.
        try:
            result = subprocess.run([str(executable), "seed", str(authorization)], cwd=ROOT, capture_output=True,
                                    text=True, encoding="utf-8", timeout=self.args.timeout)
        except subprocess.TimeoutExpired as error:
            decode = lambda value: value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value
            row.update(outcome="unknown_after_timeout", timeoutSeconds=self.args.timeout,
                       stdout=decode(error.stdout), stderr=decode(error.stderr), replayPermitted=False)
            self.persist()
            raise
        row.update(exitCode=result.returncode, stdout=result.stdout, stderr=result.stderr)
        self.persist()
        parsed = json.loads(result.stdout.strip().splitlines()[-1])
        require(result.returncode == 0 and parsed.get("completed") is True and parsed.get("compileErrors") == 0,
                "Guarded unit fixture setup did not complete.")
        require(parsed.get("processId") == self.session.session["processId"] and
                pathlib.Path(parsed["projectPath"]).resolve() == pathlib.Path(self.session.current_project), "Seeder target receipt differs.")
        require(hashlib.sha256(executable.read_bytes()).hexdigest() == row["sha256"], "Seeder binary changed during fixture setup.")
        self.session.guard()

    def observations(self):
        inventory = complete_inventory(self.session.call)
        scope = inventory["plcPath"]
        bindings = {"caller": select(inventory["blocks"], "Tgp_TemplateFC_" + self.args.language, scope)["path"],
            "dbOne": select(inventory["blocks"], "Tgp_ResolutionOne", scope)["path"],
            "dbTwo": select(inventory["blocks"], "Tgp_ResolutionTwo", scope)["path"],
            "tags": select(inventory["tagTables"], "Tgp_ResolutionTags", scope)["path"], "otherTags": self.empty_table["path"]}
        if self.args.phase == "namespace":
            unit = select(inventory["softwareScopes"], "TiaMcp_UnitA")["path"]
            bindings.update(nestedCaller=select(inventory["blocks"], "Tgp_ResolutionNested_" + self.args.language, unit)["path"],
                **namespace_type_bindings(inventory, unit))
        exports = {path: self.session.call("block_read", {"blockPath": path})["xml"] for role, path in bindings.items() if role in {"caller", "nestedCaller"}}
        return {"inventory": inventory, "bindings": bindings, "exports": exports}

    def request(self, identifier):
        source_id = "nested-udt-namespace" if identifier in NAMESPACE else identifier
        case = copy.deepcopy(self.manifest[source_id])
        if identifier in NAMESPACE:
            case["outputComponents"] = ["Active"]
            if getattr(self.args, "namespace_unqualified", False):
                case["prerequisites"] = [self.report["namespaceFixtureScope"]]
        if identifier == "nested-udt-wrong-type":
            case["operand"]["dataType"] = "Int"
            case["expect"] = {"outcome": "rejected", "code": "OperandTypeMismatch"}
        if identifier == "nested-udt-missing-member":
            case["operand"]["components"][-1] = "Absent"
            case["expect"] = {"outcome": "rejected", "code": "OperandNotFound"}
        case["id"] = identifier
        observed = self.observations()
        result = bind_case(case, observed)
        operand = case["operand"]
        components = list(operand["components"])
        if operand["scope"] == "db":
            owner = next(row for row in observed["inventory"]["blocks"] if row["path"] == observed["bindings"][operand["ownerRole"]])
            components.insert(0, owner["name"])
        result["expect"].update(nativeScope="LocalVariable" if operand["scope"] == "local" else "GlobalVariable",
            nativeComponents=components, outputComponents=case.get("outputComponents", ["OutputBool"]))
        return result

    def run_case(self, identifier):
        folder = self.output / identifier
        folder.mkdir()
        case = {"id": identifier, "status": "running", "engineeringVerified": False, "preservationVerified": False,
                "persistenceVerified": False, "recoveryVerified": False, "runtimeVerified": False}
        self.report["cases"].append(case)
        self.persist()
        before = snapshot(self.session.call, folder / "before")
        self.baseline = before
        if identifier in STALE:
            self.stale_case(identifier, folder, case, before)
        else:
            bound = self.request(identifier)
            case["request"] = bound
            if bound["expect"]["outcome"] == "rejected":
                case["diagnostic"] = expect_rejection(self.session.call, bound["tool"], bound["arguments"], {bound["expect"]["code"]})
                assert_snapshot(before, snapshot(self.session.call, folder / "rejected"))
                case.update(status="passed", rejectionVerified=True, preservationVerified=True)
            else:
                prepared = self.session.call(bound["tool"], bound["arguments"])
                case["prepared"] = prepared
                verify_prepared(prepared, bound["expect"])
                assert_snapshot(before, snapshot(self.session.call, folder / "prepared-unchanged"))
                self.qualify_positive(prepared, bound, before, folder, case)
        self.baseline = None
        self.persist()
        print("PASS " + identifier, flush=True)

    def qualify_positive(self, prepared, bound, before, folder, case):
        caller = bound["arguments"]["payload"]["blockPath"]
        original_path = self.session.current_project
        expected_xml = artifact_bytes(self.session.call, prepared["preview"]["typed"]["nativeArtifact"]).decode("utf-8-sig")
        (folder / "prepared.xml").write_text(expected_xml, encoding="utf-8")
        verify_boolean_native(expected_xml, prepared["preview"]["typed"]["networkId"], self.args.language, bound["expect"])
        before_xml = pathlib.Path(before["objects"]["block:" + caller]["file"]).read_bytes().decode("utf-8-sig")
        verify_untouched_caller(before_xml, expected_xml, bound["arguments"]["payload"]["model"]["networkId"])
        case["independentNativeTopologyVerified"] = True
        case["untouchedCallerXmlVerified"] = True
        self.pending_mutation = prepared
        self.persist()
        applied = self.session.call("change_apply", apply_arguments(prepared))
        case["applied"] = applied
        require(applied.get("state") == "applied" and applied.get("compile", {}).get("errorCount") == 0 and
                "changed_project_saved" in applied.get("stages", []), "Typed apply/compile/save did not complete.")
        case["engineeringVerified"] = True
        after = snapshot(self.session.call, folder / "after")
        assert_snapshot(before, after, except_paths={caller})
        require(after["objects"]["block:" + caller]["payloadSha256"] == native_hash(expected_xml), "Complete prepared XML differs from native readback.")
        case["preservationVerified"] = True
        self.recover(prepared, before, folder, case)
        recovered_path = self.session.current_project
        self.session.reopen(original_path)
        assert_snapshot(after, snapshot(self.session.call, folder / "reopened"))
        case["persistenceVerified"] = True
        self.session.reopen(recovered_path)
        assert_snapshot(before, snapshot(self.session.call, folder / "recovered-reopened"))
        case.update(status="passed", recoveryVerified=True)

    def recover(self, prepared, expected, folder, record):
        record["restored"] = self.restore_once(prepared)
        self.pending_mutation = None
        assert_snapshot(expected, snapshot(self.session.call, folder / "restored"))
        self.persist()

    def restore_once(self, prepared):
        require(self.unresolved_stale_apply is None, "An unverified stale apply permits only status/readback and cleanup.")
        identifier = prepared["changeId"]
        require(identifier not in self.restore_attempts, "A checkpoint restore was already dispatched; reconcile without replay.")
        self.restore_attempts.add(identifier)
        self.report.setdefault("restoreAttempts", []).append({"changeId": identifier, "dispatchedAtUtc": utc_now()})
        self.persist()
        return self.session.call("change_restore", {"changeId": identifier, "previewHash": prepared["previewHash"]})

    def stale_case(self, identifier, folder, case, before):
        base = self.request("db-first-owner" if identifier == "stale-db-ambiguity" else "tag-exact-owner")
        prepared = self.session.call(base["tool"], base["arguments"])
        case["originalPrepared"] = prepared
        verify_prepared(prepared, base["expect"])
        if identifier == "tag-duplicate-setup-guard":
            attempted = {"kind": "tag_table_import_xml", "payload": {"xml": tag_fixture(self.empty_xml, "Tgp_ResolutionDuplicate", address="%M100.2"),
                          "expectedTableNames": ["Tgp_ResolutionDuplicate"]}}
            case["setupDiagnostic"] = expect_rejection(self.session.call, "change_prepare", attempted, {"TagScopeMismatch"})
            assert_snapshot(before, snapshot(self.session.call, folder / "setup-rejected"))
            case.update(status="passed", preservationVerified=True, fixtureGuardVerified=True,
                        nativeAmbiguityQualified=False, limitation="Production collision guard prevents duplicate-tag fixture; ambiguity route remains unqualified.")
            return
        if identifier == "stale-tag-address":
            tag_owner = base["arguments"]["payload"]["model"]["operands"][0]["ownerPath"]
            deliberate, _ = self.apply("tag_set", {"tagTablePath": tag_owner, "memberName": "Ready", "attributes": {"LogicalAddress": "%M100.1"}})
        else:
            unit = select(complete_inventory(self.session.call)["softwareScopes"], "TiaMcp_UnitB")
            deliberate, _ = self.apply("source_upsert", {"scopePath": unit["path"], "language": "SCL",
                "source": self.fixture_text(SOURCES / "resolution-competing-db.scl"), "expectedBlockNames": ["Tgp_ResolutionOne"]})
        # Until explicit restore, keep the deliberate change available to failure recovery.
        self.pending_mutation = deliberate
        changed = snapshot(self.session.call, folder / "deliberate-change")
        if identifier == "stale-db-ambiguity":
            same_name = [row for row in changed["inventory"]["blocks"] if row["name"] == "Tgp_ResolutionOne"]
            require(len(same_name) == 2 and len({row["path"] for row in same_name}) == 2, "Competing DB identities were not independently observed.")
            case["competingDbPath"] = verify_competing_db_change(before, changed, unit["path"])
            case["deliberateAdditionOnlyXmlVerified"] = True
            fresh = self.request("db-first-owner")
            case["freshAmbiguityDiagnostic"] = expect_rejection(self.session.call, "change_prepare", fresh["arguments"], {"AmbiguousOperand"})
            assert_snapshot(changed, snapshot(self.session.call, folder / "fresh-ambiguity-rejected"))
        else:
            table = select(changed["inventory"]["tagTables"], "Tgp_ResolutionTags", changed["inventory"]["plcPath"])
            require([tag["logicalAddress"] for tag in table["tags"] if tag["name"] == "Ready"] == ["%M100.1"], "Changed tag address not independently observed.")
            require(table["path"] == tag_owner, "Changed tag table differs from the originally selected exact owner.")
            verify_tag_address_change(before, changed, tag_owner, "Ready", "%M100.0", "%M100.1")
            case["deliberateAddressOnlyXmlVerified"] = True
        attempted = apply_arguments(prepared)
        case["staleApplyAttempt"] = {"changeId": prepared["changeId"], "idempotencyKey": attempted["idempotencyKey"],
            "dispatched": True, "expectedRejectionVerified": False}
        self.pending_mutation = prepared
        self.unresolved_stale_apply = prepared["changeId"]
        self.persist()
        case["staleDiagnostic"] = expect_rejection(self.session.call, "change_apply", attempted, {"StaleRevision", "StaleDeclaration"})
        case["staleApplyAttempt"]["expectedRejectionVerified"] = True
        self.unresolved_stale_apply = None
        self.pending_mutation = deliberate
        self.persist()
        assert_snapshot(changed, snapshot(self.session.call, folder / "stale-rejected"))
        self.recover(deliberate, before, folder, case)
        recovered = self.session.current_project
        self.session.reopen(recovered)
        assert_snapshot(before, snapshot(self.session.call, folder / "recovered-reopened"))
        case.update(status="passed", staleRejectionVerified=True, preservationVerified=True, recoveryVerified=True,
                    nativeAmbiguityQualified=identifier == "stale-db-ambiguity")

    def run(self):
        try:
            self.session = OwnedScratch(self.args.host, self.args.worker, self.native, self.report, self.persist, timeout=self.args.timeout)
            self.setup()
            for identifier in self.args.case:
                self.run_case(identifier)
            if self.report.get("namespaceReferenceProbe") and "nested-udt-namespace" in self.args.case:
                self.report["namespaceReferenceProbe"]["propagationQualified"] = True
            self.report["status"] = "passed"
        except Exception as error:
            self.report.update(status="blocked", failure={"type": type(error).__name__, "message": str(error)})
            if self.report["cases"] and self.report["cases"][-1]["status"] == "running":
                self.report["cases"][-1]["status"] = "blocked"
            if self.pending_mutation is not None and self.session is not None:
                if self.unresolved_stale_apply is not None:
                    self.report["failureRestoreSuppressed"] = {"changeId": self.unresolved_stale_apply,
                        "reason": "Stale apply did not return the exact expected rejection; status/readback and cleanup only."}
                    self.persist()
                try:
                    status = self.session.call("change_status", {"changeId": self.pending_mutation["changeId"]})
                    self.report["failureMutationStatus"] = status
                    if self.unresolved_stale_apply is not None:
                        pass
                    elif self.pending_mutation["changeId"] in self.restore_attempts:
                        self.report["failureRestoreNotReplayed"] = True
                    elif status.get("state") in {"applied", "recovery_required"} and status.get("recoveryArchive"):
                        self.report["failureRestore"] = self.restore_once(self.pending_mutation)
                        if self.baseline is not None:
                            assert_snapshot(self.baseline, snapshot(self.session.call, self.output / "failure-restored"))
                            self.report["failureOriginalXmlVerified"] = True
                except Exception as recovery_error:
                    self.report["failureRecoveryError"] = str(recovery_error)
        finally:
            if self.session is not None:
                try:
                    self.session.close()
                except Exception as error:
                    self.report.update(status="blocked", cleanupFailure=str(error))
            try:
                require(all(hashlib.sha256(pathlib.Path(row["path"]).read_bytes()).hexdigest() == row["sha256"] for row in self.report["harnessFiles"]),
                        "Harness inputs changed during the run.")
            except Exception as error:
                self.report.update(status="blocked", harnessIntegrityFailure=str(error))
            self.report["completedAtUtc"] = utc_now()
            self.persist()
        return 0 if self.report["status"] == "passed" else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--artifacts", type=pathlib.Path, required=True)
    parser.add_argument("--artifact-policy-root", type=pathlib.Path,
                        help="Explicit absolute short artifact root when running from an isolated source/build tree.")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--phase", choices=("standard", "namespace", "stale"), default="standard")
    parser.add_argument("--language", choices=("LAD", "FBD"), default="LAD")
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--unit-seeder", type=pathlib.Path)
    parser.add_argument("--namespace-unqualified", action="store_true",
        help="Diagnostic native UDT reference-spelling probe. Canonicalized/rejected imports never qualify unqualified propagation.")
    parser.add_argument("--timeout", type=int, default=300)
    args = parser.parse_args()
    if args.namespace_unqualified and args.phase != "namespace":
        parser.error("--namespace-unqualified requires --phase namespace.")
    allowed = {"standard": STANDARD, "namespace": NAMESPACE, "stale": STALE}[args.phase]
    args.case = args.case or list(allowed)
    if not args.case or len(args.case) != len(set(args.case)) or set(args.case) - set(allowed):
        parser.error("Select unique cases belonging to the requested phase.")
    if (args.phase == "namespace" or "stale-db-ambiguity" in args.case) and not args.unit_seeder:
        parser.error("Namespace and stale DB ambiguity fixtures require --unit-seeder.")
    if not 5 <= args.timeout <= 930:
        parser.error("--timeout must be within 5..930 seconds.")
    try:
        artifact_policy(args.artifacts, args.artifact_policy_root or (ROOT / "artifacts"))
    except RuntimeError as error:
        parser.error(str(error))
    return ResolutionRun(args).run()


if __name__ == "__main__":
    raise SystemExit(main())
