"""Scoped operand and dependent-DB reference inputs; native encodings remain uncaptured.

Writes independently authored software-unit / alternate-namespace sources for
global DB member and unit tag operands in LAD and FBD, and the capture packet
that orders them after the existing instance-effect pairs. Rerun after editing.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent
SOURCES = ROOT / "sources"
UNIT = "TiaMcp_UnitA"
ROUTES = (("Unit", UNIT), ("Namespace", UNIT + ".Alternate"))
ARCHIVE = "artifacts/changes/change-12d1d3b9e3a74cd3be6f53b9f2f35cf4/before.zap21"
ARCHIVE_SHA256 = "39cea56a73b8e58263e256c2f27b2402e1d29989fb12cb471f66970e1cce74d6"
TAGS = (("TgpUnit_TagFlag", "%M200.0"), ("TgpUnit_TagTarget", "%M200.1"))
# Element layout observed in a native V21 export of Tgp_ResolutionTags (lane A resolution run; not retained here).
OBSERVED_TAG_EXPORT = "artifacts/r1/n-lad/r9c971a/exports/tag-table-66a3669092ba47318511facdae2e8184.xml"
OBSERVED_TAG_EXPORT_SHA256 = "f96ad5ae11664fa3a9164627ab4a9447c4635309e17ac68f1677f7d14b85b0a1"


def tag_table():
    tags = []
    for index, (name, address) in enumerate(TAGS):
        base = 1 + 3 * index
        tags.append(f"""      <SW.Tags.PlcTag ID="{base:X}" CompositionName="Tags">
        <AttributeList>
          <DataTypeName>Bool</DataTypeName>
          <ExternalAccessible>true</ExternalAccessible>
          <ExternalVisible>true</ExternalVisible>
          <ExternalWritable>true</ExternalWritable>
          <LogicalAddress>{address}</LogicalAddress>
          <Name>{name}</Name>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID="{base + 1:X}" CompositionName="Comment">
            <ObjectList>
              <MultilingualTextItem ID="{base + 2:X}" CompositionName="Items">
                <AttributeList>
                  <Culture>en-US</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Tags.PlcTag>
""")
    return ('<?xml version="1.0" encoding="utf-8"?>\n<Document>\n  <Engineering version="V21" />\n'
            '  <SW.Tags.PlcTagTable ID="0">\n    <AttributeList>\n      <Name>TgpUnit_OperandTags</Name>\n'
            '    </AttributeList>\n    <ObjectList>\n' + "".join(tags) + '    </ObjectList>\n  </SW.Tags.PlcTagTable>\n</Document>\n')


def operand_db(prefix, namespace):
    return (f'NAMESPACE {namespace}\nDATA_BLOCK "{prefix}OperandDB"\n{{ S7_Optimized_Access := \'TRUE\' }}\n'
            'VERSION : 0.1\nNON_RETAIN\nVAR\n Flag : Bool;\n Target : Bool;\nEND_VAR\nBEGIN\nEND_DATA_BLOCK\n\nEND_NAMESPACE\n')


def caller(name, namespace, language, source, target):
    rung = (f"RUNG wire#powerrail\n  Contact( {source} )\n  Coil( {target} )\nEND_RUNG" if language == "LAD"
            else f"RUNG\n  {source}\n  Coil( {target} )\nEND_RUNG")
    return (f'NAMESPACE {namespace}\n{{ S7_Optimized := "TRUE"; S7_PreferredLanguage := "{language}"; S7_Version := "0.1" }}\n'
            f'FUNCTION_BLOCK "{name}"\nVAR_INPUT\n Enable : Bool;\nEND_VAR\n{{ S7_Language := "{language}" }}\n'
            f'NETWORK\n{rung}\nEND_NETWORK\nEND_FUNCTION_BLOCK\n\nEND_NAMESPACE\n')


def write(name, text):
    path = SOURCES / name
    path.write_bytes(text.encode("utf-8"))
    return path


def entry(path, route, form, names, namespace, **extra):
    return {"path": path.relative_to(REPO).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "route": route, "authoredFormat": form, "names": names, "namespace": namespace, **extra}


def main():
    sources = []
    # Dependent-DB effect pairs (existing fixtures): callee templates, before, then the redeclaring after phase.
    for label, namespace in ROUTES:
        prefix = "Tgp" + label + "_"
        route = "softwareUnit" if namespace == UNIT else "namespace"
        template = REPO / ("fixtures/typed-graphical/native/" + prefix + "TemplateFB_LAD.xml")
        sources.append(entry(template, route, "native_xml_template_derivation", [prefix + "TemplateFB_LAD"], namespace))
    for phase in ("before", "after"):
        for label, namespace in ROUTES:
            prefix = "Tgp" + label + "_"
            route = "softwareUnit" if namespace == UNIT else "namespace"
            path = SOURCES / ("route-" + label + "-instance-effect-" + phase + ".scl")
            names = [prefix + "InstanceEffect"] + ([prefix + "InstanceEffect_DB"] if phase == "before" else [])
            extra = {"modifies": [prefix + "InstanceEffect"], "observes": [prefix + "InstanceEffect_DB"]} if phase == "after" else {}
            sources.append(entry(path, route, "SCL", names, namespace, **extra))
    # Scoped operand storage, then one LAD and one FBD caller per route and operand kind.
    tags = write("route-Unit-operand-tags.xml", tag_table())
    write("route-Unit-operand-tags.provenance.json", json.dumps({
        "schemaVersion": 1, "builderUsed": False, "authoredFormat": "native_tag_table_xml", "requiresNativeAcceptance": True,
        "observedLayout": {"export": OBSERVED_TAG_EXPORT, "table": "Tgp_ResolutionTags", "sha256": OBSERVED_TAG_EXPORT_SHA256, "retained": False},
        "adaptedFields": ["DocumentInfo omitted", "Name", "Tags", "LogicalAddress"],
        "sourceSha256": hashlib.sha256(tags.read_bytes()).hexdigest()}, indent=2) + "\n")
    sources.append(entry(tags, "softwareUnit", "native_tag_table_xml", ["TgpUnit_OperandTags"], UNIT))
    for label, namespace in ROUTES:
        prefix = "Tgp" + label + "_"
        route = "softwareUnit" if namespace == UNIT else "namespace"
        path = write("route-" + label + "-operand-db.scl", operand_db(prefix, namespace))
        sources.append(entry(path, route, "SCL", [prefix + "OperandDB"], namespace))
    for label, namespace in ROUTES:
        prefix = "Tgp" + label + "_"
        route = "softwareUnit" if namespace == UNIT else "namespace"
        db = f'"{namespace}.{prefix}OperandDB"'
        for operand, source, target in (("Db", db + ".Flag", db + ".Target"),
                                        ("Tag", '"TgpUnit_TagFlag"', '"TgpUnit_TagTarget"')):
            for language in ("LAD", "FBD"):
                name = prefix + "Operand" + operand + "_" + language
                path = write("route-" + label + "-operand-" + operand.lower() + "-" + language.lower() + ".s7dcl",
                             caller(name, namespace, language, source, target))
                sources.append(entry(path, route, "SIMATIC_SD", [name], namespace))
    command = ["python", "-B", "scripts/capture_typed_routes_owned.py", "@artifacts/cap-scoped.args",
               "--host", "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe",
               "--worker", "src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe",
               "--output", "artifacts/cap-scoped", "--archive", ARCHIVE, "--sha256", ARCHIVE_SHA256]
    for row in sources:
        file = Path(row["path"]).name
        command += [arg for name in row.get("modifies", []) for arg in ("--modify", file + ":" + name)]
        command += [arg for name in row.get("observes", []) for arg in ("--observe", file + ":" + name)]
    packet = {"schemaVersion": 1, "status": "authored-not-executed",
              "purpose": "Native references for scoped dependent-DB effects (software-unit and namespace instance-effect pairs) and scoped "
                         "global operand encodings: namespace-qualified global DB members and unit PLC tags read and written from LAD and FBD "
                         "in both routes. Encodings and effects are unobserved until captured; nothing here enables a profile.",
              "unit": UNIT, "archive": {"path": ARCHIVE, "sha256": ARCHIVE_SHA256}, "sources": sources,
              "argsFile": [row["path"] for row in sources], "command": command,
              "afterCapture": ["scripts/extract_typed_instance_effects.py <capture> <capture> --route softwareUnit --prefix TgpUnit_ --namespace TiaMcp_UnitA",
                               "scripts/extract_typed_instance_effects.py <capture> <capture> --route namespace --prefix TgpNamespace_ --namespace TiaMcp_UnitA.Alternate",
                               "Worker: select instance-effects[-<route>].v1.json by the change's route before route createMulti can pass.",
                               "Review the captured operand Access/Component encodings before any builder operand profile is enabled."]}
    (ROOT / "route-capture-scoped-packet.v1.json").write_bytes((json.dumps(packet, indent=2) + "\n").encode("utf-8"))
    print(str(len(sources)) + " scoped capture inputs authored; packet status authored-not-executed.")


if __name__ == "__main__":
    main()
