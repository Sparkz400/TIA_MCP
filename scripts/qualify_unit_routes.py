"""Additional software-unit routes for the owned-scratch object-model harness.

All fixture XML originates from native exports or fixture_sources. No live
qualification is claimed until every assertion and native compile succeeds.
"""
from xml.dom import minidom

from run_conformance import access_location, assert_expectation, native_document, safe_xml, structural_xml


def fixture_namespace(xml, namespace):
    # Unit-native FC exports omit this optional field. Carrying the root fixture's
    # writable MemoryLayout into a unit makes TIA demand missing ReadOnly metadata.
    return fixture_field(xml, "SW.Blocks.FC", "Namespace", namespace,
                         omit_optional_fields=("MemoryLayout",) if namespace else ())


def fixture_table_name(xml, name):
    return fixture_field(xml, "SW.Tags.PlcTagTable", "Name", name)


def fixture_field(xml, object_kind, field_name, value, omit_optional_fields=()):
    # Validate first, then retain native namespace declarations/prefixes. TIA's
    # importer rejects the ns0:Member form emitted by ElementTree reserialization.
    safe_xml(xml)
    document = minidom.parseString(xml)
    blocks = document.getElementsByTagName(object_kind)
    if len(blocks) != 1:
        raise ValueError("Expected exactly one " + object_kind + " fixture.")
    attributes = next(node for node in blocks[0].childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName == "AttributeList")
    for child in list(attributes.childNodes):
        if child.nodeType == child.ELEMENT_NODE and child.tagName in omit_optional_fields:
            attributes.removeChild(child)
    fields = [node for node in attributes.childNodes if node.nodeType == node.ELEMENT_NODE and node.tagName == field_name]
    if len(fields) > 1:
        raise ValueError("The fixture contains duplicate " + field_name + " fields.")
    field = fields[0] if fields else document.createElement(field_name)
    if not fields:
        attributes.appendChild(field)
    for child in list(field.childNodes):
        field.removeChild(child)
    field.appendChild(document.createTextNode(value))
    return document.toxml()


def native_equal(before, after):
    return structural_xml(native_document(before)) == structural_xml(native_document(after))


def qualify_unit_routes(call, apply, rejected, export_xml, scopes, evidence=None):
    """Scopes maps root/A/B to exact scope paths and existing namespace presets."""
    evidence = evidence if evidence is not None else {}
    evidence.update({"graphical": [], "tagTables": [], "qualification": "unverified",
                     "limitations": ["Empty native tag-table round trips do not qualify tag/constant value edits.",
                                     "Native table names are PLC-wide in this fixture; same-name tables across units were rejected, so routing uses distinct names.",
                                     "No cross-unit dependency creation or unit movement is qualified."]})
    for language in ("LAD", "FBD"):
        name = "TiaMcp_Unit" + language + "Fixture"
        fixture = call("fixture_sources", {"language": language, "blockName": name})
        original = fixture["payload"]["xml"]
        baselines = {}
        for label, scope in scopes.items():
            payload = {"xml": fixture_namespace(original, scope["namespace"]),
                       "scopePath": scope["path"], "expectedBlockNames": [name]}
            apply("block_import_xml", payload)
            baselines[label] = call("block_read", {"blockName": name, "scopePath": scope["path"]})
        rejected("block_read", {"blockName": name}, "AmbiguousTarget")
        selected = baselines["A"]
        rejected("change_prepare", {"kind": "block_import_xml", "payload": {
            "blockPath": selected["block"]["path"], "xml": fixture_namespace(original, scopes["B"]["namespace"]),
            "expectedBlockNames": [name]}}, "NamespaceMismatch")
        location = access_location(selected["xml"], "Enable")
        apply("graphical_operand_set", {"blockPath": selected["block"]["path"], **location,
                                       "components": ["EnableAlternate"]})
        edited = call("block_read", {"blockPath": selected["block"]["path"]})
        assert_expectation(edited, {"path": "/xml", "op": "operandPreserved", "value": {
            "baselineStep": "before", "from": "Enable", "to": "EnableAlternate"}}, {"before": selected})
        for label in ("root", "B"):
            after = call("block_read", {"blockPath": baselines[label]["block"]["path"]})
            assert native_equal(baselines[label]["xml"], after["xml"]), "Unselected scope changed: " + label
        evidence["graphical"].append({"language": language, "selectedPath": selected["block"]["path"],
                                      "duplicateNameRejected": True, "wrongNamespaceRejected": True,
                                      "fullNativePayloadPreserved": True, "unselectedScopesPreserved": True})

    inventory = call("software_inspect", {"maxItems": 2000})
    candidates = [table for table in inventory["tagTables"] if table.get("scopePath") == scopes["root"]["path"]
                  or table["path"].startswith(scopes["root"]["path"] + "/tags/")]
    # Native default tables from the scratch have no symbols. Do not clone
    # hardware/system constants into a different scope or drop any source data.
    template = None
    for candidate in candidates:
        exported = call("tag_table_read", {"tagTablePath": candidate["path"]})
        original = export_xml(exported)
        document = safe_xml(original)
        table = document.find("SW.Tags.PlcTagTable")
        if table is not None and not list(table.iter("SW.Tags.PlcTag")) and not list(table.iter("SW.Tags.PlcUserConstant")) and not list(table.iter("SW.Tags.PlcSystemConstant")):
            template = original
            break
    if template is None:
        raise RuntimeError("Owned scratch exposes no empty native tag-table template; unit tag routes remain unqualified.")
    baselines = {}
    for label, scope in scopes.items():
        name = "TiaMcp_UnitTagsFixture_" + label
        xml = fixture_table_name(template, name)
        apply("tag_table_import_xml", {"scopePath": scope["path"], "xml": xml, "expectedTableNames": [name]})
        baselines[label] = call("tag_table_read", {"tagTablePath": scope["path"] + "/tags/" + name})
    name = "TiaMcp_UnitTagsFixture_A"
    selected_path = scopes["A"]["path"] + "/tags/" + name
    before_xml = {label: export_xml(value) for label, value in baselines.items()}
    rejected("change_prepare", {"kind": "tag_table_import_xml", "payload": {
        "tagTablePath": selected_path, "scopePath": scopes["B"]["path"], "xml": before_xml["A"],
        "expectedTableNames": [name]}}, "ObjectNotFound")
    apply("tag_table_import_xml", {"tagTablePath": selected_path, "xml": before_xml["A"], "expectedTableNames": [name]})
    for label, scope in scopes.items():
        after = call("tag_table_read", {"tagTablePath": scope["path"] + "/tags/TiaMcp_UnitTagsFixture_" + label})
        assert native_equal(before_xml[label], export_xml(after)), "Tag-table payload changed: " + label
    evidence["tagTables"].append({"selectedPath": selected_path, "tableNamesUniqueAcrossPlc": True,
                                  "crossScopeRejected": True, "fullNativePayloadPreserved": True})
    evidence["qualification"] = "passed"
    return evidence
