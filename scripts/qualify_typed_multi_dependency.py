"""Qualify createMulti with an existing direct InstanceDB in an owned scratch.

Engineering only. The caller default (17) and existing DB start value (42) are
independent fixture expectations. No simulator, OB insertion or mutation replay.
"""
import argparse
import copy
import hashlib
import json
import pathlib
from xml.dom import minidom

from qualification_owned_scratch import assert_snapshot, complete_inventory, native_hash, require, snapshot
from qualify_typed_resolution import ResolutionRun, apply_arguments, artifact_policy, select, verify_untouched_caller
from run_conformance import native_document, structural_xml
from smoke_mcp import ROOT
from typed_graphical_native_expectations import adapt_reference_bindings, assert_independent_topology
from typed_graphical_transport import artifact_bytes
from engineering_preservation import native_payload


def local(node):
    return node.tag.rsplit("}", 1)[-1]


def static_section(document):
    blocks = [node for node in document if node.tag.startswith("SW.Blocks.")]
    require(len(blocks) == 1, "Exactly one native block is required.")
    sections = list(blocks[0].find("AttributeList/Interface"))
    require(len(sections) == 1, "Exactly one top-level interface is required.")
    matches = [node for node in sections[0] if local(node) == "Section" and node.get("Name") == "Static"]
    require(len(matches) == 1, "Exactly one top-level Static section is required.")
    return matches[0]


def member(document, name):
    matches = [node for node in static_section(document) if local(node) == "Member" and node.get("Name") == name]
    require(len(matches) == 1, "Exact top-level static member is missing/ambiguous: " + name)
    return matches[0]


def marker_value(xml, expected):
    marker = member(native_document(xml), "RetainedMarker")
    values = [node.text for node in marker if local(node) == "StartValue"]
    require(marker.get("Datatype") == "Int" and values == [str(expected)], "RetainedMarker differs from its independent nondefault-value oracle.")


def caller_fixture(reference_xml, marker_reference_xml):
    """Copy only the captured marker declaration into the retained native FB."""
    native_document(reference_xml)
    marker = member(native_document(marker_reference_xml), "RetainedMarker")
    require(marker.get("Datatype") == "Int", "Captured marker must be Int.")
    marker_value(marker_reference_xml, 17)
    # Preserve the native default namespaces in opaque FlgNet/interface payloads.
    document = minidom.parseString(reference_xml)
    block = document.getElementsByTagName("SW.Blocks.FB")
    require(len(block) == 1, "The caller reference must be an FB.")
    interface = block[0].getElementsByTagName("Interface")
    require(len(interface) == 1, "Expected one caller interface.")
    sections = [node for node in interface[0].getElementsByTagName("Sections") if node.parentNode is interface[0]]
    require(len(sections) == 1, "Expected one caller root Sections.")
    static = [node for node in sections[0].childNodes if node.nodeType == node.ELEMENT_NODE and node.getAttribute("Name") == "Static"]
    require(len(static) == 1 and not any(node.nodeType == node.ELEMENT_NODE and node.getAttribute("Name") == "RetainedMarker" for node in static[0].childNodes), "Marker fixture would overwrite a declaration.")
    source = minidom.parseString(marker_reference_xml)
    selected = [node for node in source.getElementsByTagName("Member") if node.getAttribute("Name") == "RetainedMarker"]
    require(len(selected) == 1, "Captured marker is ambiguous.")
    static[0].appendChild(document.importNode(selected[0], True))
    return document.toxml()


def verify_append(before_xml, after_xml, new_name, expected_value, network_id=None):
    """The added member must equal the existing same-type Child, name excepted.

    Removing that single append must reproduce all previous DB XML, or all
    caller XML outside the separately checked network, including IDs/comments.
    """
    native_payload(before_xml)
    native_payload(after_xml)
    before, after = minidom.parseString(before_xml), minidom.parseString(after_xml)
    def elements(node):
        return [child for child in node.childNodes if child.nodeType == child.ELEMENT_NODE]
    def section(document):
        interfaces = document.getElementsByTagName("Interface")
        require(len(interfaces) == 1, "Exactly one native interface is required.")
        roots = [node for node in elements(interfaces[0]) if node.localName == "Sections"]
        require(len(roots) == 1, "Exactly one native root Sections is required.")
        found = [node for node in elements(roots[0]) if node.localName == "Section" and node.getAttribute("Name") == "Static"]
        require(len(found) == 1, "Exactly one native Static section is required.")
        return found[0]
    def selected(document, name):
        found = [node for node in elements(section(document)) if node.localName == "Member" and node.getAttribute("Name") == name]
        require(len(found) == 1, "Exact static member is missing/ambiguous: " + name)
        return found[0]
    def subtree(node):
        clone, namespaces, cursor = node.cloneNode(True), {}, node
        while cursor is not None:
            if cursor.nodeType == cursor.ELEMENT_NODE:
                for attr in cursor.attributes.values():
                    if attr.name == "xmlns" or attr.prefix == "xmlns": namespaces.setdefault(attr.name, attr.value)
            cursor = cursor.parentNode
        for name, value in namespaces.items(): clone.setAttribute(name, value)
        return native_payload(clone.toxml())
    marker_value(before_xml, expected_value)
    marker_value(after_xml, expected_value)
    require(not any(node.getAttribute("Name") == new_name for node in elements(section(before))), "New member already exists in baseline.")
    added = selected(after, new_name)
    expected = selected(before, "Child")
    expected.setAttribute("Name", new_name)
    try:
        require(subtree(added) == subtree(expected), "Appended storage differs from the independently observed same-type Child declaration.")
    finally:
        expected.setAttribute("Name", "Child")
    require(elements(section(after))[-1] is added, "New storage must append after all existing declarations.")
    section(after).removeChild(added)
    reduced = after.toxml()
    if network_id is None:
        require(native_payload(before_xml) == native_payload(reduced), "Dependent DB changed beyond the exact appended member.")
    else:
        verify_untouched_caller(before_xml, reduced, network_id)


class MultiDependencyRun(ResolutionRun):
    def __init__(self, args):
        super().__init__(args)
        self.report["qualificationScope"] = "Owned standard-route createMulti engineering with direct existing InstanceDB; runtime not run."
        for name in ("qualify_typed_multi_dependency.py", "typed_graphical_native_expectations.py"):
            path = pathlib.Path(__file__).with_name(name)
            self.report["harnessFiles"].append({"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        cases = json.loads(self.fixture_text(ROOT / "fixtures/typed-graphical/extended-cases.v1.json"))["cases"]
        self.specification = next(row for row in cases if row["fixtureId"] == "typed-createmulti-fb-multi-" + args.language.lower())
        self.persist()

    def setup(self):
        self.session.start()
        self.report["scratch"] = self.session.call("tia_create_scratch", {"cpuTypeIdentifier": self.specification["cpuTypeIdentifier"]})
        self.reference = self.fixture_text(ROOT / self.specification["referenceXml"])
        captured = self.fixture_text(ROOT / "fixtures/typed-graphical/native/instance-effects/before-caller.xml")
        authored = caller_fixture(self.reference, captured)
        (self.output / "authored-caller.xml").write_text(authored, encoding="utf-8")
        self.caller_name = self.specification["blockName"]
        self.db_name = self.caller_name + "_RetainedDB"
        self.apply("block_import_xml", {"xml": authored, "expectedBlockNames": [self.caller_name]})
        source = ('DATA_BLOCK "' + self.db_name + '"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"'
                  + self.caller_name + '"\nBEGIN\n RetainedMarker := 42;\nEND_DATA_BLOCK\n')
        (self.output / "authored-instance.scl").write_text(source, encoding="utf-8")
        self.apply("source_upsert", {"language": "SCL", "source": source, "expectedBlockNames": [self.db_name]})

    def run_case(self, identifier):
        folder = self.output / identifier
        folder.mkdir()
        case = {"id": identifier, "status": "running", "engineeringVerified": False, "preservationVerified": False,
                "persistenceVerified": False, "recoveryVerified": False, "runtimeVerified": False}
        self.report["cases"].append(case)
        self.persist()
        before = snapshot(self.session.call, folder / "before")
        self.baseline = before
        inventory = before["inventory"]
        caller = select(inventory["blocks"], self.caller_name, inventory["plcPath"])["path"]
        instance = select(inventory["blocks"], self.db_name, inventory["plcPath"])["path"]
        callee = select(inventory["blocks"], "TiaMcp_EnableGate", inventory["plcPath"])["path"]
        def xml(state, path):
            return pathlib.Path(state["objects"]["block:" + path]["file"]).read_bytes().decode("utf-8-sig")
        before_caller, before_db = xml(before, caller), xml(before, instance)
        marker_value(before_caller, 17)
        marker_value(before_db, 42)
        db = native_document(before_db).find("SW.Blocks.InstanceDB")
        require(db is not None and db.findtext("AttributeList/InstanceOfName") == self.caller_name,
                "Observed DB is not the exact caller's direct instance.")
        case["baselineNondefaultValueVerified"] = True
        payload = copy.deepcopy(self.specification["payload"])
        networks = list(native_document(before_caller).iter("SW.Blocks.CompileUnit"))
        require(len(networks) == 1 and networks[0].get("ID"), "Require one exact observed caller network.")
        payload["blockPath"] = caller
        payload["model"]["networkId"] = networks[0].get("ID")
        for node in payload["model"]["nodes"]:
            if node.get("calleePath") == "$block:TiaMcp_EnableGate":
                node["calleePath"] = callee
        case["request"] = {"kind": "graphical_typed_edit", "payload": payload}
        prepared = self.session.call("change_prepare", case["request"])
        case["prepared"] = prepared
        assert_snapshot(before, snapshot(self.session.call, folder / "prepared-unchanged"))
        expected_caller = artifact_bytes(self.session.call, prepared["preview"]["typed"]["nativeArtifact"]).decode("utf-8-sig")
        effects = [effect for effect in prepared["preview"]["effects"] if effect.get("action") == "nativeDependentRecompile"]
        require(len(effects) == 1 and effects[0].get("path") == instance and effects[0].get("expectedArtifact"),
                "Prepared dependent effects must identify exactly the existing direct instance DB.")
        expected_db = artifact_bytes(self.session.call, effects[0]["expectedArtifact"]).decode("utf-8-sig")
        (folder / "prepared-caller.xml").write_text(expected_caller, encoding="utf-8")
        (folder / "prepared-instance.xml").write_text(expected_db, encoding="utf-8")
        new_name = payload["instances"][0]["name"]
        verify_append(before_caller, expected_caller, new_name, 17, payload["model"]["networkId"])
        verify_append(before_db, expected_db, new_name, 42)
        oracle = adapt_reference_bindings(self.reference, self.specification["referenceBindings"])
        assert_independent_topology(oracle, expected_caller, payload["model"]["networkId"])
        case.update(independentAppendXmlVerified=True, independentNativeTopologyVerified=True)
        original_path = self.session.current_project
        self.pending_mutation = prepared
        attempted = apply_arguments(prepared)
        case["applyAttempt"] = dict(attempted, dispatched=True)
        self.persist()
        applied = self.session.call("change_apply", attempted)
        case["applied"] = applied
        require(applied.get("state") == "applied" and applied.get("compile", {}).get("errorCount") == 0
                and "changed_project_saved" in applied.get("stages", []), "Typed multi-instance apply/compile/save did not complete.")
        case["engineeringVerified"] = True
        after = snapshot(self.session.call, folder / "after")
        assert_snapshot(before, after, except_paths={caller, instance})
        for path, expected in ((caller, expected_caller), (instance, expected_db)):
            require(after["objects"]["block:" + path]["payloadSha256"] == native_hash(expected), "Complete prepared/native dependent content differs.")
        verify_append(before_caller, xml(after, caller), new_name, 17, payload["model"]["networkId"])
        verify_append(before_db, xml(after, instance), new_name, 42)
        case["preservationVerified"] = True
        self.recover(prepared, before, folder, case)
        recovered = self.session.current_project
        self.session.reopen(original_path)
        assert_snapshot(after, snapshot(self.session.call, folder / "reopened"))
        case["persistenceVerified"] = True
        self.session.reopen(recovered)
        assert_snapshot(before, snapshot(self.session.call, folder / "recovered-reopened"))
        case.update(status="passed", recoveryVerified=True)
        self.baseline = None
        self.persist()
        print("PASS " + identifier, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("host", "worker", "artifacts", "output"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    parser.add_argument("--artifact-policy-root", type=pathlib.Path)
    parser.add_argument("--language", choices=("LAD", "FBD"), default="LAD")
    parser.add_argument("--timeout", type=int, default=300)
    args = parser.parse_args()
    args.phase = "multi-dependency"
    args.case = ["create-multi-existing-db-" + args.language.lower()]
    if not 5 <= args.timeout <= 930:
        parser.error("--timeout must be within 5..930 seconds.")
    try:
        artifact_policy(args.artifacts, args.artifact_policy_root or (ROOT / "artifacts"))
    except RuntimeError as error:
        parser.error(str(error))
    return MultiDependencyRun(args).run()


if __name__ == "__main__":
    raise SystemExit(main())
