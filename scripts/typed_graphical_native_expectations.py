"""Compare independently authored native topology without depending on the emitter.

Native UIds are graph identities, not behavior. Alpha-renaming/reordering is
allowed only for this *additional semantic* check. The qualifier separately
compares the entire prepared/native XML, including IDs and all other attributes.
"""
import copy
import hashlib
import json
import re
from xml.dom import minidom, Node

from run_conformance import safe_xml, structural_xml, native_document
from engineering_preservation import native_payload


def local(element):
    return element.tag.rsplit("}", 1)[-1]


def signature(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


EMPTY_TEXT_CULTURE = re.compile(r"(<MultilingualTextItem\s[^>]*>\s*<AttributeList>\s*<Culture>)([^<]+)(</Culture>\s*<Text\s*/>)")
TEXT_CULTURE = re.compile(r"<Culture>([^<]+)</Culture>")


def project_culture(xml):
    """The single culture of a block exported from the target project."""
    cultures = set(TEXT_CULTURE.findall(xml))
    if len(cultures) != 1:
        raise ValueError("Exactly one observed project culture is required: " + ", ".join(sorted(cultures)))
    return cultures.pop()


def adapt_reference_culture(xml, culture):
    """Move a fixture's empty-text items to the target project's culture; any text stays exact.

    Returns the adapted XML and the number of retargeted items.
    """
    adapted, count = EMPTY_TEXT_CULTURE.subn(lambda m: m.group(1) + culture + m.group(3), xml)
    if set(TEXT_CULTURE.findall(adapted)) - {culture}:
        raise ValueError("Reference text in another culture cannot be adapted without changing its content.")
    return adapted, count


def adapt_reference_bindings(xml, bindings):
    """Apply explicit fixture author substitutions, independent of emitter output.

    Only exact symbolic Access/Instance references may change. Matching counts
    are assertions so a changed fixture cannot silently weaken its oracle.
    """
    from xml.etree import ElementTree as ET
    document = safe_xml(xml)
    for binding in bindings:
        if binding["element"] not in ("Access", "Instance"):
            raise ValueError("Only symbolic native references may be adapted.")
        matches = []
        for element in document.iter():
            if local(element) != binding["element"] or element.get("Scope") != binding["fromScope"]:
                continue
            components = [node for node in element.iter() if local(node) == "Component"]
            if [node.get("Name") for node in components] == binding["fromComponents"]:
                matches.append((element, components))
        if len(matches) != binding.get("expectedCount", 1):
            raise ValueError("Independent reference binding count differs from its explicit expectation.")
        if len(binding["fromComponents"]) != len(binding["toComponents"]):
            raise ValueError("Reference component topology changes need another native fixture.")
        for element, components in matches:
            element.set("Scope", binding["toScope"])
            for node, name in zip(components, binding["toComponents"]):
                node.set("Name", name)
    return ET.tostring(document, encoding="unicode")


def label(element):
    element = copy.deepcopy(element)
    for node in element.iter():
        node.attrib.pop("UId", None)
        if node.text is not None and not node.text.strip():
            node.text = None
        if node.tail is not None and not node.tail.strip():
            node.tail = None
    return structural_xml(element)


def topology(xml, network_id=None):
    document = safe_xml(xml)
    units = [node for node in document.iter() if node.tag == "SW.Blocks.CompileUnit" and
             (network_id is None or node.get("ID") == network_id)]
    if len(units) != 1:
        raise ValueError("Exactly one selected network is required for independent topology comparison.")
    networks = [node for node in units[0].iter() if local(node) == "FlgNet"]
    if len(networks) != 1:
        raise ValueError("Expected one native FlgNet network.")
    net = copy.deepcopy(networks[0])
    parts = next(child for child in net if local(child) == "Parts")
    wire_list = next(child for child in net if local(child) == "Wires")
    nodes, edges, transparent = {}, [], []
    negations = {}
    for part in parts:
        uid = part.get("UId")
        if not uid or uid in nodes:
            raise ValueError("Missing or duplicate native definition.")
        # A native one-input AND is the identity function. Lift inversions into
        # explicit logical NOT nodes so native pin inversion and typed NOT can
        # be compared without pretending their XML is identical.
        if local(part) == "Part" and part.get("Name") == "A" and any(
                local(child) == "TemplateValue" and child.get("Name") == "Card" and child.text == "1" for child in part):
            transparent.append(uid)
        for child in list(part):
            if local(child) == "Negated" and child.get("Name") != "operand":
                negations[(uid, child.get("Name"))] = "not:" + uid + ":" + child.get("Name")
                part.remove(child)
        nodes[uid] = label(part)
    for index, wire in enumerate(wire_list):
        wire_key = "wire:" + str(index)
        nodes[wire_key] = ("wire",)
        for endpoint in wire:
            tag = local(endpoint)
            if tag == "Powerrail":
                nodes["powerrail"] = ("powerrail",)
                edges.append((wire_key, "powerrail", ""))
                continue
            if tag not in ("NameCon", "IdentCon") or endpoint.get("UId") not in nodes:
                raise ValueError("Independent topology comparison requires fully resolved native wires.")
            uid, port = endpoint.get("UId"), endpoint.get("Name", "value")
            negated = negations.get((uid, port))
            if negated:
                nodes[negated] = ("logical-not",)
                edges.append((wire_key, negated, "in"))
                bridge = "inversion-wire:" + negated
                nodes[bridge] = ("wire",)
                edges.extend([(bridge, negated, "out"), (bridge, uid, port)])
            else:
                edges.append((wire_key, uid, port))
    for uid in transparent:
        attached = [(wire, part, port) for wire, part, port in edges if part == uid]
        if sorted(port for _, _, port in attached) != ["in1", "out"]:
            raise ValueError("One-input native AND does not have the expected identity topology.")
        first, second = attached[0][0], attached[1][0]
        edges = [(first if wire == second else wire, part, port) for wire, part, port in edges if part != uid]
        nodes.pop(uid)
        nodes.pop(second)
    # Canonical color refinement retains pin names, direction encoded in pin
    # identity, complete part/access payloads, connection multiplicity and cycle
    # structure. Unsupported/opaque connectors are rejected above.
    colors = {key: signature(value) for key, value in nodes.items()}
    initial = colors.copy()
    for _ in range(len(nodes)):
        neighbors = {key: [] for key in nodes}
        for wire, part, port in edges:
            neighbors[wire].append(("endpoint", port, colors[part]))
            neighbors[part].append(("wire", port, colors[wire]))
        colors = {key: signature((initial[key], sorted(neighbors[key]))) for key in nodes}
    return sorted(colors.values())


def assert_independent_topology(reference_xml, prepared_xml, network_id):
    if topology(reference_xml) != topology(prepared_xml, network_id):
        raise AssertionError("Prepared native instruction/operand/wire topology differs from the independently authored native reference.")


def assert_existing_caller_insertion(before_xml, after_xml, inserted_id, after_id):
    """Strict additive-network contract; never normalize pre-existing IDs.

    Storage/declaration mutation needs its own independently captured effect
    contract. This checks network-only insertion into an existing caller.
    """
    # Parse for selection without discarding comment/PI nodes or namespace
    # declarations that may bind QName values inside opaque native payloads.
    native_payload(before_xml)
    native_payload(after_xml)
    before, after = minidom.parseString(before_xml), minidom.parseString(after_xml)
    def children(node):
        return [child for child in node.childNodes if child.nodeType == Node.ELEMENT_NODE]
    before_blocks = [node for node in children(before.documentElement) if node.tagName in ("SW.Blocks.FC", "SW.Blocks.FB")]
    after_blocks = [node for node in children(after.documentElement) if node.tagName in ("SW.Blocks.FC", "SW.Blocks.FB")]
    if len(before_blocks) != 1 or len(after_blocks) != 1:
        raise AssertionError("Insertion requires exactly one existing FC/FB envelope.")
    lists = [node for node in children(after_blocks[0]) if node.tagName == "ObjectList"]
    original_lists = [node for node in children(before_blocks[0]) if node.tagName == "ObjectList"]
    if len(lists) != 1 or len(original_lists) != 1:
        raise AssertionError("Insertion requires an existing native ObjectList.")
    objects, original_objects = lists[0], original_lists[0]
    inserted = [node for node in children(objects) if node.tagName == "SW.Blocks.CompileUnit" and node.getAttribute("ID") == inserted_id]
    if len(inserted) != 1:
        raise AssertionError("Insertion must add exactly one selected direct compile unit.")
    inserted = inserted[0]
    try:
        before_ids = [int(node.getAttribute("ID"), 16) for node in before.getElementsByTagName("*") if node.hasAttribute("ID")]
        after_ids = [int(node.getAttribute("ID"), 16) for node in after.getElementsByTagName("*") if node.hasAttribute("ID")]
        added_ids = [int(node.getAttribute("ID"), 16) for node in [inserted, *inserted.getElementsByTagName("*")] if node.hasAttribute("ID")]
    except ValueError as error:
        raise AssertionError("Native object IDs must be hexadecimal identities.") from error
    if len(before_ids) != len(set(before_ids)) or len(after_ids) != len(set(after_ids)) or set(before_ids) & set(added_ids):
        raise AssertionError("Insertion reuses or aliases an existing native object ID.")
    elements = children(objects)
    if after_id is None:
        if any(node.tagName == "SW.Blocks.CompileUnit" for node in children(original_objects)) or elements[-1] is not inserted:
            raise AssertionError("First-network insertion must append to a network-free caller.")
    else:
        anchors = [node for node in children(objects) if node.tagName == "SW.Blocks.CompileUnit" and node.getAttribute("ID") == after_id]
        if len(anchors) != 1 or elements.index(inserted) != elements.index(anchors[0]) + 1:
            raise AssertionError("Inserted compile unit is not at its exact observed placement.")
        previous = inserted.previousSibling
        while previous is not None and previous.nodeType == Node.TEXT_NODE and not previous.data.strip():
            previous = previous.previousSibling
        if previous is not anchors[0]:
            raise AssertionError("Insertion crossed a retained opaque comment or processing instruction at the selected boundary.")
    objects.removeChild(inserted)
    if native_payload(before.toxml()) != native_payload(after.toxml()):
        raise AssertionError("Existing caller XML changed outside the one inserted network, including native IDs, comments, declarations or opaque content.")
