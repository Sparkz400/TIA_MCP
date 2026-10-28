"""Qualify the X11/L01 graphical change kinds natively in one owned scratch CPU 1511-1 PN.

The fixtures are the genuine V21 exports Tgp_FC_LAD and Tgp_FC_FBD (an FB calling the scratch's own FC
TiaMcp_IsEnabled), with the project culture and one extra Bool input EnableAlternate. Per language:
call_parameter_set moves the call's Enable argument to EnableAlternate, graphical_network_patch replaces one
Access in place and graphical_network_insert adds a copy of the network with fresh object IDs. Each change must
pass checkpoint, compile, native preservation and save, and a complete snapshot must differ only in the edited
block. A fresh Portal reopen must show the identical state; from there one change per kind is applied again and
its checkpoint restored into a new directory equal to the reopened state. Typed refusals must change nothing,
including call_connection_set, which TIA V21 cannot import (every Access must be wired exactly once).
"""
from __future__ import annotations

import argparse
import re
from pathlib import Path

from library_native_harness import Harness, require
from qualify_plc_object_mutations import Run, coverage_gaps, reopen

KINDS = ("call_parameter_set", "graphical_network_patch", "graphical_network_insert")
NATIVE = Path(__file__).resolve().parents[1] / "fixtures/typed-graphical/native"
NS = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"
# Native UIds of the fixture networks: the call, its Enable argument Access and the Access to replace in place.
UIDS = {"LAD": {"call": "23", "argument": "21", "replaced": "21"}, "FBD": {"call": "24", "argument": "22", "replaced": "21"}}
CRITERIA = {
    "AC-identity": "refused: unknown network, call, parameter and UId references, an SCL block, a language-mismatched or ID-colliding network and call_connection_set, each leaving the PLC software unchanged.",
    "AC-preview": "Every preview is asserted against the request (payload, edit, effects naming the existing block) before apply.",
    "AC-preserve": "applied: a complete snapshot differs only in the edited block; the native readback equals the prepared document (native_preservation_verified).",
    "AC-native": "applied + persisted: checkpoint, compile, preservation and save stages and a fresh owned Portal reopen with identical state.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the reopened state, per kind.",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json and limitations.",
}
LIMITATIONS = [
    "Two genuine V21 FlgNet/v5 fixtures (one LAD and one FBD network, each an FB calling an FC) in an owned scratch CPU 1511-1 PN V4.1.",
    "graphical_network_patch is qualified for in-place replacement of an existing Access only: TIA renumbers UIds and orders Access parts first "
    "on every import, so a patch that adds or removes fragments fails closed with NativePreservationMismatch and must be restored.",
    "call_connection_set is refused before any change: TIA V21 import rejects an Access without a wire and an Access on several wires.",
    "Engineering only; nothing is downloaded or simulated.",
]


def fixture(language, culture):
    """The native export with the scratch's culture and an extra Bool input EnableAlternate."""
    xml = (NATIVE / ("Tgp_FC_%s.xml" % language)).read_text(encoding="utf-8-sig").replace("<Culture>de-DE</Culture>", "<Culture>%s</Culture>" % culture)
    output = '</Section>\n          <Section Name="Output">'
    require(xml.count(output) == 1, "Unexpected fixture interface")
    return xml.replace(output, '<Member Name="EnableAlternate" Datatype="Bool" Remanence="NonRetain" Accessibility="Public" />\n          ' + output)


def network(xml):
    units = re.findall(r'<SW\.Blocks\.CompileUnit ID="([0-9A-F]+)"', xml)
    require(len(units) == 1, "Expected one network, found %s" % units)
    return units[0]


def copy_unit(xml, unit):
    """The compile unit with every object ID moved above the document's highest, so none collides."""
    body = re.search(r'<SW\.Blocks\.CompileUnit ID="%s"[\s\S]*?</SW\.Blocks\.CompileUnit>' % unit, xml).group(0)
    top = max(int(i, 16) for i in re.findall(r'\sID="([0-9A-F]+)"', xml)) + 1
    return re.sub(r'\sID="([0-9A-F]+)"', lambda m: ' ID="%X"' % (int(m.group(1), 16) + top), body)


def access(uid, inner, scope="LocalVariable"):
    return '<Access xmlns="%s" Scope="%s" UId="%s">%s</Access>' % (NS, scope, uid, inner)


def symbol(name):
    return "<Symbol><Component Name=\"%s\" /></Symbol>" % name


TRUE = "<Constant><ConstantType>Bool</ConstantType><ConstantValue>TRUE</ConstantValue></Constant>"


def edits(run, language, block, restore=False):
    """call_parameter_set, an in-place patch and a network insert on one fixture block."""
    key, uid = run.blk(block), UIDS[language]
    unit = network(run.xml(key))
    existing = lambda kind: {"effects": [{"action": kind, "existingBlock": run.info(block)}]}
    def edit(label, kind, payload, texts):
        _, applied = run.mutate(label, kind, payload, {**existing(kind), "edit": payload}, changed=[key], xml={key: texts}, restore=restore)
        require("native_preservation_verified" in applied["stages"] and any(r["role"] == "structured-edit-readback" for r in applied["readbacks"]),
                "%s lacks its native preservation readback" % label)
    edit("%s: move the Enable argument to EnableAlternate" % language, "call_parameter_set",
         {"blockName": block, "networkId": unit, "callUid": uid["call"], "parameterName": "Enable", "components": ["EnableAlternate"]}, '<Component Name="EnableAlternate" />')
    replacement = access(uid["replaced"], TRUE, "LiteralConstant") if language == "LAD" else access(uid["replaced"], symbol("Enable"))
    edit("%s: replace Access %s in place" % (language, uid["replaced"]), "graphical_network_patch",
         {"blockName": block, "networkId": unit, "upsertXml": [replacement], "removeUids": []}, "<ConstantValue>TRUE</ConstantValue>" if language == "LAD" else '<Component Name="Enable" />')
    copied = copy_unit(run.xml(key), unit)
    edit("%s: insert a copy of the network" % language, "graphical_network_insert", {"blockName": block, "afterNetworkId": unit, "compileUnitXml": copied}, "<SW.Blocks.CompileUnit")
    if not restore:
        run.h.stage("%s has two networks" % block, lambda: require(len(re.findall(r"<SW\.Blocks\.CompileUnit ", run.xml(key))) == 2, "Inserted network missing") or 2)


def refusals(run):
    lad, unit = run.blk("Tgp_FC_LAD"), None
    unit = network(run.xml(lad))
    call = {"blockName": "Tgp_FC_LAD", "networkId": unit, "callUid": "23", "parameterName": "Enable", "components": ["EnableAlternate"]}
    run.refuse("call_parameter_set", "an unknown network", {**call, "networkId": "7F"}, "NetworkNotFound")
    run.refuse("call_parameter_set", "an unknown call", {**call, "callUid": "99"}, "CallNotFound")
    run.refuse("call_parameter_set", "an unknown parameter", {**call, "parameterName": "Missing"}, "ParameterNotFound")
    patch = {"blockName": "Tgp_FC_LAD", "networkId": unit, "removeUids": []}
    dangling = '<Wire xmlns="%s" UId="24"><Powerrail /><NameCon UId="77" Name="en" /></Wire>' % NS
    run.refuse("graphical_network_patch", "a wire to a missing part", {**patch, "upsertXml": [dangling]}, "UnresolvedNetworkReference")
    run.refuse("graphical_network_patch", "no fragments", {**patch, "upsertXml": []}, "InvalidNetworkPatch")
    run.refuse("graphical_network_patch", "the SCL block TiaMcp_EnableGate", {**patch, "blockName": "TiaMcp_EnableGate", "networkId": "3", "upsertXml": [access("21", symbol("Enable"))]}, "UnsupportedLanguage")
    lad_xml, fbd_xml = run.xml(lad), run.xml(run.blk("Tgp_FC_FBD"))
    run.refuse("graphical_network_insert", "a LAD network in the FBD block",
               {"blockName": "Tgp_FC_FBD", "afterNetworkId": network(fbd_xml), "compileUnitXml": copy_unit(fbd_xml, network(fbd_xml)).replace("<ProgrammingLanguage>FBD</ProgrammingLanguage>", "<ProgrammingLanguage>LAD</ProgrammingLanguage>")},
               "LanguageConversionNotSupported")
    original = re.search(r'<SW\.Blocks\.CompileUnit ID="%s"[\s\S]*?</SW\.Blocks\.CompileUnit>' % unit, lad_xml).group(0)
    run.refuse("graphical_network_insert", "a network reusing existing object IDs", {"blockName": "Tgp_FC_LAD", "afterNetworkId": unit, "compileUnitXml": original}, "InvalidSimaticML")
    run.refuse("call_connection_set", "moving the Enable wire to the output's Access", {"blockName": "Tgp_FC_LAD", "networkId": unit, "callUid": "23", "parameterName": "Enable", "targetAccessUid": "22"},
               "AccessWiringUnsupported")


def body(h):
    run = Run(h)
    h.report["criteria"] = CRITERIA
    run.plc = h.owned_scratch("graphical changes")
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.current = run.snap("baseline")
    gate = run.blk("TiaMcp_EnableGate")
    culture = re.search(r"<Culture>([^<]+)</Culture>", run.xml(gate)).group(1)
    for language in ("LAD", "FBD"):
        block = "Tgp_FC_" + language
        run.mutate("import " + block, "block_import_xml", {"xml": fixture(language, culture), "expectedBlockNames": [block]},
                   {"effects": [{"action": "create", "blockName": block}]}, added=[run.blk(block)], rows={run.blk(block): {"programmingLanguage": language}})
    refusals(run)
    for language in ("LAD", "FBD"):
        edits(run, language, "Tgp_FC_" + language)
    reopen(run)
    for language in ("LAD", "FBD"):
        edits(run, language, "Tgp_FC_" + language, restore=True)
    gaps = coverage_gaps(run.coverage, KINDS)
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "gr_", "X11/L01 graphical change kinds in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
