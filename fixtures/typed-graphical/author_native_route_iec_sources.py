"""Independently compose native IEC route probes; never invokes typed builders.

This is explicit template derivation, not a TIA-authored source fixture. Native
acceptance must be captured separately before any resulting profile is enabled.
Only observed block routing attributes, selected name/number and the global
instance component are adapted. Interface, instruction and wiring are retained
from the independently authored and natively compiled standard reference.
"""
import hashlib
import json
from pathlib import Path
from xml.dom import minidom

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent


def child(node, name):
    return next((n for n in node.childNodes if n.nodeType == n.ELEMENT_NODE and n.tagName == name), None)


def text(node, value):
    for item in list(node.childNodes):
        node.removeChild(item)
    node.appendChild(node.ownerDocument.createTextNode(str(value)))


def main():
    for route, namespace in (("Unit", "TiaMcp_UnitA"), ("Namespace", "TiaMcp_UnitA.Alternate")):
        number = 600 if route == "Unit" else 700
        for language in ("LAD", "FBD"):
            route_path = ROOT / "native" / ("Tgp" + route + "_TemplateFB_" + language + ".xml")
            route_doc = minidom.parseString(route_path.read_bytes())
            route_attributes = child(route_doc.getElementsByTagName("SW.Blocks.FB")[0], "AttributeList")
            for instruction in ("TON", "TOF", "TP", "CTU", "CTD", "CTUD"):
                for global_instance in (False, True):
                    suffix = instruction + ("_Global" if global_instance else "") + "_" + language
                    reference_path = ROOT / "native" / ("Tgp_" + suffix + ".xml")
                    document = minidom.parseString(reference_path.read_bytes())
                    attributes = child(document.getElementsByTagName("SW.Blocks.FB")[0], "AttributeList")
                    # This preserves the native unit's absence of MemoryLayout.
                    for name in ("Access", "AutoNumber", "MemoryLayout", "Namespace"):
                        old = child(attributes, name)
                        observed = child(route_attributes, name)
                        if old is not None:
                            attributes.removeChild(old)
                        if observed is not None:
                            cloned = document.importNode(observed, True)
                            # Native attributes use lexical element ordering.
                            following = next((n for n in attributes.childNodes if n.nodeType == n.ELEMENT_NODE and n.tagName > name), None)
                            attributes.insertBefore(cloned, following)
                    text(child(attributes, "Name"), "Tgp" + route + "_" + suffix)
                    text(child(attributes, "Number"), number)
                    number += 1
                    if global_instance:
                        original = "Tgp_GlobalTimer" if instruction in ("TON", "TOF", "TP") else "Tgp_GlobalCounter"
                        matches = [n for n in document.getElementsByTagName("Component") if n.getAttribute("Name") == original]
                        assert len(matches) == 1
                        matches[0].setAttribute("Name", namespace + ".Tgp" + route + "_" + original.removeprefix("Tgp_"))
                    target = ROOT / "sources" / ("route-" + route + "-native-" + suffix.lower().replace("_", "-") + ".xml")
                    target.write_bytes(document.toxml(encoding="utf-8"))
                    evidence = {"schemaVersion": 1, "builderUsed": False, "authoredFormat": "native_xml_template_derivation",
                        "requiresNativeAcceptance": True, "namespace": namespace,
                        "references": [{"path": p.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                                       for p in (reference_path, route_path)],
                        "adaptedFields": ["Access", "AutoNumber", "MemoryLayout presence", "Namespace", "Name", "Number"] +
                                         (["GlobalVariable Component Name"] if global_instance else []),
                        "sourceSha256": hashlib.sha256(target.read_bytes()).hexdigest()}
                    target.with_suffix(".provenance.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
