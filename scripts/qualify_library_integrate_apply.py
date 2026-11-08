"""Native qualification of the production library_integrate combined apply, on by default since 29 October 2026.

Starts the frozen Host in owned TIA sessions and owned scratch projects only: with the former
opt-in flag --enable-library-integrate-apply (still accepted, now a no-op) for the installs and
refusals, and without it to prove the default. Reuses the component qualification fixture (FB->UDT global
library, peer and existing FBD FC callers, mapped tags) and its independent oracles.
Each eligible plan is applied once; a failed or uncertain apply is never replayed or
restored automatically. A failed step is retained as failed, never converted to success.

--candidates runs the candidate cases (LAD and FB callers, an earlier placement, reuse of the installed FB and a
multi-instance with it; each a verified shape since 1 November) on a --component-qualification Host, which also admits
shapes without native evidence, in its own owned scratch; each case is independent and stops only itself.
"""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import traceback
import uuid

from block_test_runtime import NativeToolError, utc_now
from compilation_evidence import require_compile_only
from qualification_owned_scratch import OwnedScratch, assert_snapshot, require, snapshot
from qualify_plc_component import (FIXTURES, ComponentRun, first_available_db_number, installed_objects, step_request,
                                   terminal_network, verify_component_call, verify_preserved_installation, verify_standalone_caller)
from run_conformance import safe_xml
from smoke_mcp import Client, ROOT
from typed_graphical_transport import artifact_bytes

FLAG = "--enable-library-integrate-apply"
CANDIDATE_FLAG = "--component-qualification"
COMBINED_PATH_OVERHEAD = len("/changes/c/01234567/change-lib-" + "0" * 32 + "/before")
REFUSED = "PlcIntegrationNotQualified"
# FB callers are verified since 31 October and a multi-instance of the reused FB since 1 November; a multi-instance after a fresh
# import is not, so the refusal cases prepare an FB caller with a multi-instance of the FB they import.
FB_REFUSAL = "A multi-instance is verified only for the reused installed FB in an existing LAD or FBD FB caller; other multi-instances stay plan-only."
HOST_FB = "TiaMcpComponentHost"
# The Host FB moves off FB2, the number the component FB is imported with: the captured instance profile is bound to it
# (31 October), and prepare now refuses an occupied number (ComponentBlockNumberOccupied).
HOST_FB_NUMBER = 30
MODES = ("createStandaloneFc", "existing")
EXTRA_TOOLS = ("library_open", "library_close", "library_list", "library_query", "library_capabilities")
OB_NAME, OB_NUMBER = "TiaMcpComponentOb", 123
# OB event classes beside program cycle: the number and the informative start information V21 gives each class on S7-1500.
EVENT_CLASSES = {"ProgramCycle": (OB_NUMBER, None),
                 "Startup": (100, (("LostRetentive", "Bool"), ("LostRTC", "Bool"))),
                 "CyclicInterrupt": (30, (("Initial_Call", "Bool"), ("Event_Count", "Int"))),
                 "TimeOfDay": (10, (("CaughtUp", "Bool"), ("SecondTimeInterval", "Bool"))),
                 "TimeDelayInterrupt": (20, (("Sign", "Word"),)),
                 "HardwareInterrupt": (40, (("LADDR", "HW_IO"), ("USI", "Word"), ("IChannel", "USInt"), ("EventType", "Byte"))),
                 "DiagnosticErrorInterrupt": (82, (("IO_State", "Word"), ("LADDR", "HW_ANY"), ("Channel", "UInt"), ("MultiError", "Bool")))}
# A LAD OB takes its coil network from the LAD FC caller fixture, imported under this name before the baseline.
LAD_PEER = "TiaMcpLadPeer"
HARNESS = ("qualify_plc_component.py", "qualification_owned_scratch.py", "compilation_evidence.py", "smoke_mcp.py",
           "block_test_runtime.py", "run_block_tests.py", "typed_graphical_transport.py")


def host_client(enabled):
    """Client factory for the frozen Host, with or without the former opt-in flag."""
    return lambda host, timeout, arguments: Client(host, timeout, [*arguments, *([FLAG] if enabled else [])])


def candidate_client(host, timeout, arguments):
    """The qualification Host: it admits candidate shapes in its own owned scratch and makes each new scratch de-DE only."""
    return Client(host, timeout, [*arguments, CANDIDATE_FLAG])


# Candidate install cases: manifest overrides and the exact unverified features the preview must name. ladCaller, fbCaller,
# earlierPlacement and reusedComponent passed natively on 31 October and are verified shapes, so their previews name none.
CANDIDATES = {
    "obCaller": ({"callerMode": "existing", "caller": OB_NAME, "callerSource": None, "obCaller": True}, []),
    "ladStandalone": ({"callerMode": "createStandaloneFc", "language": "LAD"}, []),
    "ladExisting": ({"callerMode": "existing", "language": "LAD", "callerSource": "callers/TiaMcpComponentCaller.lad.s7dcl"}, []),
    "fbCaller": ({"callerMode": "existing", "caller": "TiaMcpComponentHost", "callerSource": "callers/TiaMcpComponentHost.s7dcl"}, []),
    "earlierPlacement": ({"callerMode": "existing", "callerSource": None, "twoNetworks": True, "insertNetworkIndex": 0}, []),
}
REUSE_CALLER, REUSE_INSTANCE, MULTI_NAME = "TiaMcpComponentCaller2", "TiaMcpComponentGate_2", "Gate"
# Combinations of the verified features, each in its own owned scratch: whether the plan reuses the FB that a first verified
# standalone FBD FC install put into the project, the caller under test, and the features its preview must name (none since the
# runs of 2 November verified each combination). An existing caller comes from its fixture source; "networks": 2 repeats its one
# network through its own export, so the call goes after the first. reuseExisting is the reusedComponent key with an existing
# FBD FC, a variant its first evidence did not exercise.
COMBINATIONS = {
    "ladEarlier": (False, {"kind": "FC", "language": "LAD", "networks": 2}, []),
    "fbEarlier": (False, {"kind": "FB", "language": "FBD", "networks": 2}, []),
    "ladFb": (False, {"kind": "FB", "language": "LAD"}, []),
    "ladFbEarlier": (False, {"kind": "FB", "language": "LAD", "networks": 2}, []),
    "reuseExisting": (True, {"kind": "FC", "language": "FBD"}, []),
    "reuseLad": (True, {"kind": "FC", "language": "LAD", "new": True}, []),
    "reuseLadExisting": (True, {"kind": "FC", "language": "LAD"}, []),
    "reuseEarlier": (True, {"kind": "FC", "language": "FBD", "networks": 2}, []),
    "reuseLadEarlier": (True, {"kind": "FC", "language": "LAD", "networks": 2}, []),
    "reuseFb": (True, {"kind": "FB", "language": "FBD"}, []),
    "reuseFbEarlier": (True, {"kind": "FB", "language": "FBD", "networks": 2}, []),
    "reuseLadFb": (True, {"kind": "FB", "language": "LAD"}, []),
    "reuseLadFbEarlier": (True, {"kind": "FB", "language": "LAD", "networks": 2}, []),
    "multiEarlier": (True, {"kind": "FB", "language": "FBD", "networks": 2, "multi": True}, []),
    "multiLad": (True, {"kind": "FB", "language": "LAD", "multi": True}, []),
    "multiLadEarlier": (True, {"kind": "FB", "language": "LAD", "networks": 2, "multi": True}, []),
    # The Host FB with its own instance DB, which gains the member through the captured component effect.
    "multiDependentsEarlier": (True, {"kind": "FB", "language": "FBD", "networks": 2, "multi": True, "dependents": True}, []),
    "multiDependentsLad": (True, {"kind": "FB", "language": "LAD", "multi": True, "dependents": True}, []),
    "multiDependentsLadEarlier": (True, {"kind": "FB", "language": "LAD", "networks": 2, "multi": True, "dependents": True}, []),
    # OB callers beside the verified obCaller shape (a fresh component after the last network of an FBD OB): a LAD OB, an earlier
    # placement and reuse of the installed FB, in every combination; verified on 7 November (local.library_integrate.candidate_shapes.ob_combinations.20261107).
    "obLad": (False, {"kind": "OB", "language": "LAD"}, []),
    "obEarlier": (False, {"kind": "OB", "language": "FBD", "networks": 2}, []),
    "obLadEarlier": (False, {"kind": "OB", "language": "LAD", "networks": 2}, []),
    "obReuse": (True, {"kind": "OB", "language": "FBD"}, []),
    "obReuseLad": (True, {"kind": "OB", "language": "LAD"}, []),
    "obReuseEarlier": (True, {"kind": "OB", "language": "FBD", "networks": 2}, []),
    "obReuseLadEarlier": (True, {"kind": "OB", "language": "LAD", "networks": 2}, []),
    # A startup and a cyclic-interrupt OB caller beside the program-cycle ones; verified on 8 November
    # (local.library_integrate.candidate_shapes.ob_event_classes.20261108).
    "obStartup": (False, {"kind": "OB", "language": "FBD", "eventClass": "Startup"}, []),
    "obCyclic": (False, {"kind": "OB", "language": "FBD", "eventClass": "CyclicInterrupt"}, []),
    # Four further event classes, and the cyclic-interrupt OB with LAD, an earlier placement and reuse; verified on 8 November
    # (local.library_integrate.candidate_shapes.ob_event_classes_2.20261108).
    "obTimeOfDay": (False, {"kind": "OB", "language": "FBD", "eventClass": "TimeOfDay"}, []),
    "obTimeDelay": (False, {"kind": "OB", "language": "FBD", "eventClass": "TimeDelayInterrupt"}, []),
    "obHardware": (False, {"kind": "OB", "language": "FBD", "eventClass": "HardwareInterrupt"}, []),
    "obDiagnostic": (False, {"kind": "OB", "language": "FBD", "eventClass": "DiagnosticErrorInterrupt"}, []),
    "obCyclicLad": (False, {"kind": "OB", "language": "LAD", "eventClass": "CyclicInterrupt"}, []),
    "obCyclicEarlier": (False, {"kind": "OB", "language": "FBD", "networks": 2, "eventClass": "CyclicInterrupt"}, []),
    "obCyclicReuse": (True, {"kind": "OB", "language": "FBD", "eventClass": "CyclicInterrupt"}, []),
    # Every other verified event class with LAD, an earlier placement and reuse; verified on 8 November (local.library_integrate.candidate_shapes.ob_combinations_2.20261108).
    "obStartupLad": (False, {"kind": "OB", "language": "LAD", "eventClass": "Startup"}, []),
    "obStartupEarlier": (False, {"kind": "OB", "language": "FBD", "networks": 2, "eventClass": "Startup"}, []),
    "obStartupReuse": (True, {"kind": "OB", "language": "FBD", "eventClass": "Startup"}, []),
    "obTimeOfDayLad": (False, {"kind": "OB", "language": "LAD", "eventClass": "TimeOfDay"}, []),
    "obTimeOfDayEarlier": (False, {"kind": "OB", "language": "FBD", "networks": 2, "eventClass": "TimeOfDay"}, []),
    "obTimeOfDayReuse": (True, {"kind": "OB", "language": "FBD", "eventClass": "TimeOfDay"}, []),
    "obTimeDelayLad": (False, {"kind": "OB", "language": "LAD", "eventClass": "TimeDelayInterrupt"}, []),
    "obTimeDelayEarlier": (False, {"kind": "OB", "language": "FBD", "networks": 2, "eventClass": "TimeDelayInterrupt"}, []),
    "obTimeDelayReuse": (True, {"kind": "OB", "language": "FBD", "eventClass": "TimeDelayInterrupt"}, []),
    "obHardwareLad": (False, {"kind": "OB", "language": "LAD", "eventClass": "HardwareInterrupt"}, []),
    "obHardwareEarlier": (False, {"kind": "OB", "language": "FBD", "networks": 2, "eventClass": "HardwareInterrupt"}, []),
    "obHardwareReuse": (True, {"kind": "OB", "language": "FBD", "eventClass": "HardwareInterrupt"}, []),
    "obDiagnosticLad": (False, {"kind": "OB", "language": "LAD", "eventClass": "DiagnosticErrorInterrupt"}, []),
    "obDiagnosticEarlier": (False, {"kind": "OB", "language": "FBD", "networks": 2, "eventClass": "DiagnosticErrorInterrupt"}, []),
    "obDiagnosticReuse": (True, {"kind": "OB", "language": "FBD", "eventClass": "DiagnosticErrorInterrupt"}, []),
    # The reused-FB multi-instance in a Host FB that a parent FB embeds, beside the verified FBD one (multiInstanceParent): LAD, an
    # earlier placement and the Host's own DB in every combination, and the verified shape two levels deep with the parent's and the
    # grandparent's DBs. All verified on 7 November.
    "parentLad": (True, {"kind": "FB", "language": "LAD", "multi": True, "parent": True},
                  []),
    "parentEarlier": (True, {"kind": "FB", "language": "FBD", "networks": 2, "multi": True, "parent": True},
                      []),
    "parentLadEarlier": (True, {"kind": "FB", "language": "LAD", "networks": 2, "multi": True, "parent": True},
                         []),
    "parentOwnDb": (True, {"kind": "FB", "language": "FBD", "multi": True, "dependents": True, "parent": True},
                    []),
    "parentOwnDbLad": (True, {"kind": "FB", "language": "LAD", "multi": True, "dependents": True, "parent": True},
                       []),
    "parentOwnDbEarlier": (True, {"kind": "FB", "language": "FBD", "networks": 2, "multi": True, "dependents": True, "parent": True},
                           []),
    "parentOwnDbLadEarlier": (True, {"kind": "FB", "language": "LAD", "networks": 2, "multi": True, "dependents": True, "parent": True},
                              []),
    "parentDeep": (True, {"kind": "FB", "language": "FBD", "multi": True, "parent": True, "deep": True}, []),
}
CALLER_SOURCES = {("FB", "FBD"): "callers/TiaMcpComponentHost.s7dcl", ("FB", "LAD"): "callers/TiaMcpComponentHost.lad.s7dcl",
                  ("FC", "FBD"): "TiaMcpComponentCaller.s7dcl", ("FC", "LAD"): "callers/TiaMcpComponentCaller.lad.s7dcl"}
# multiInstance installs through the captured component multi-instance template v21.fw41.component-gate.multi-instance.v1
# (route-capture-component-storage-packet.v1.json); it passed natively on 1 November, so its preview names no candidate feature.
# multiInstanceDependents gives the Host FB its own instance DB first: the DB gains the new member through the captured component
# effect (instance-effects-component.v1.json), which the runner checks against the captured DB member. multiInstanceParent embeds
# the Host FB in a parent FB instead: the parent gains the member nested in its Host member through the captured component
# transitive effect (instance-effects-transitive-component.v1.json), checked against the captured nested member.
# A Host without that manifest still refuses the plan before admission (DependencyProfileUnavailable).
HOST_DB = HOST_FB + "_DB"
HOST_DB_SOURCE = 'DATA_BLOCK "%s"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"%s"\nBEGIN\nEND_DATA_BLOCK\n' % (HOST_DB, HOST_FB)
PARENT_FB, PARENT_FB_NUMBER = "TiaMcpComponentParent", 31
PARENT_SOURCE = ('FUNCTION_BLOCK "%s"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nVAR\n Host : "%s";\nEND_VAR\nBEGIN\n #Host();\nEND_FUNCTION_BLOCK\n'
                 % (PARENT_FB, HOST_FB))
DEPENDENT_DB_FEATURES = []  # dependentInstanceDb+fbCaller+multiInstance+reusedComponent, verified on 2 November
PARENT_FEATURES = []  # embeddingParent+fbCaller+multiInstance+reusedComponent, verified on 7 November
COMPONENT_EFFECT = ROOT / "fixtures/typed-graphical/native/instance-effects-component/after-instance.xml"
COMPONENT_TRANSITIVE_PARENT = ROOT / "fixtures/typed-graphical/native/instance-effects-transitive-component/after-Tgp_CompNestParent.xml"
# The captured nested member as an instance DB holds it (the parent's DB in the same capture).
COMPONENT_TRANSITIVE_PARENT_DB = ROOT / "fixtures/typed-graphical/native/instance-effects-transitive-component/after-Tgp_CompNestParent_DB.xml"
# deep: the parent gets its own instance DB and a grandparent FB with its DB embeds it, so the member nests two levels deep.
PARENT_DB, GRAND_FB, GRAND_FB_NUMBER, GRAND_DB = PARENT_FB + "_DB", "TiaMcpComponentGrand", 32, "TiaMcpComponentGrand_DB"
PARENT_DB_SOURCE = 'DATA_BLOCK "%s"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"%s"\nBEGIN\nEND_DATA_BLOCK\n' % (PARENT_DB, PARENT_FB)
GRAND_SOURCE = ('FUNCTION_BLOCK "%s"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nVAR\n Middle : "%s";\nEND_VAR\nBEGIN\n #Middle();\nEND_FUNCTION_BLOCK\n'
                % (GRAND_FB, PARENT_FB))
GRAND_DB_SOURCE = 'DATA_BLOCK "%s"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\n"%s"\nBEGIN\nEND_DATA_BLOCK\n' % (GRAND_DB, GRAND_FB)
DEPENDENTS_REFUSED = "DependencyProfileUnavailable"
# The worker's message for a combined apply refused while it resolves the shared-mutation scope (OpennessSession.SharedMutationAdmission).
BEFORE_ADMISSION = "Combined apply refused before admission: "
REFUSED_CASES = set()


def failed_candidates(outcomes):
    """Every candidate case that did not reach its outcome: installed, or refused for the refusal cases."""
    return {name: outcome for name, outcome in outcomes.items() if outcome != ("refused" if name in REFUSED_CASES else "installed")}


def structural(node):
    """Tag, attributes, trimmed text and children of an XML element, DocumentInfo aside (extract_typed_instance_effects.py)."""
    return (node.tag, sorted(node.attrib.items()), (node.text or "").strip(), [structural(e) for e in node if e.tag != "DocumentInfo"])


def top_static(document):
    """The Static section directly under a block's Interface."""
    statics = [s for interface in document.iter("Interface") for sections in interface for s in sections if s.get("Name") == "Static"]
    require(len(statics) == 1, "The export lacks exactly one top-level Static section.")
    return statics[0]


def verify_dependent_db(before_xml, after_xml, captured_xml, member):
    """The caller's own instance DB gains exactly the new multi-instance member, equal to the captured component effect's DB member;
    with that member removed, the DB equals its baseline."""
    after = safe_xml(after_xml)
    section = top_static(after)
    added = [m for m in section if m.get("Name") == member]
    expected = [m for m in top_static(safe_xml(captured_xml)) if m.get("Name") == member]
    require(len(added) == 1 and len(expected) == 1, "The instance DB or the captured effect lacks exactly one Static member " + member + ".")
    require(structural(added[0]) == structural(expected[0]), "The new instance DB member differs from the captured component effect.")
    section.remove(added[0])
    require(structural(after) == structural(safe_xml(before_xml)), "The instance DB changed beyond the new member.")


def verify_parent_fb(before_xml, after_xml, captured_xml, host_type, member):
    """Also an embedding instance DB or a grandparent (with captured_xml of the same block kind): the one member typed host_type at
    any depth appends the new member."""
    """A parent FB embedding the multi-instance caller gains exactly the new member at the end of that embedded member's nested
    Static section, equal to the captured transitive effect's nested member; with it removed, the parent equals its baseline."""
    def nested(document, datatype):
        hosts = [m for m in document.iter() if m.tag.rsplit("}", 1)[-1] == "Member" and m.get("Datatype") == datatype]
        require(len(hosts) == 1, "Expected one member typed " + datatype + ".")
        statics = [s for sections in hosts[0] if sections.tag.rsplit("}", 1)[-1] == "Sections" for s in sections if s.get("Name") == "Static"]
        require(len(statics) == 1, "The embedded member lacks exactly one nested Static section.")
        return statics[0]
    after = safe_xml(after_xml)
    section = nested(after, '"' + host_type + '"')
    tail = [m for m in section if m.tag.rsplit("}", 1)[-1] == "Member"][-1:]
    expected = [m for m in nested(safe_xml(captured_xml), '"Tgp_CompNestEffect"') if m.get("Name") == member]
    require(len(tail) == 1 and tail[0].get("Name") == member and len(expected) == 1, "The parent does not append " + member + " to its embedded member.")
    require(structural(tail[0]) == structural(expected[0]), "The parent's nested member differs from the captured transitive effect.")
    section.remove(tail[0])
    require(structural(after) == structural(safe_xml(before_xml)), "The parent FB changed beyond the nested member.")


def admission_records(root):
    """Every shared-mutation admission record the Host wrote under its artifact root, archived ones included."""
    base = Path(root) / "engineering-admissions"
    return sorted(p.name for folder in (base, base / ".archive") if folder.is_dir() for p in folder.iterdir() if p.is_dir() and p.name != ".archive")


def two_network_fc(peer_xml, name, number):
    """An FBD FC with two networks from the peer FC's native export: its one network twice, every object ID renumbered
    consecutively in document order (V21's own numbering), renamed and renumbered."""
    networks = re.findall(r"<SW\.Blocks\.CompileUnit .*?</SW\.Blocks\.CompileUnit>", peer_xml, re.S)
    require(len(networks) == 1, "Peer caller must export exactly one network.")
    xml = peer_xml.replace(networks[0], networks[0] + networks[0])
    ids = iter(range(int(re.search(r'(?<![A-Za-z])ID="([0-9A-F]+)"', xml).group(1), 16), 1 << 24))
    xml = re.sub(r'(?<![A-Za-z])ID="[0-9A-F]+"', lambda _: 'ID="' + format(next(ids), "X") + '"', xml)
    for pattern, value in ((r"<Name>[^<]*</Name>", "<Name>" + name + "</Name>"), (r"<Number>\d+</Number>", "<Number>" + str(number) + "</Number>")):
        xml, count = re.subn(pattern, value, xml, count=1)
        require(count == 1, "Peer export lacks one block " + pattern)
    return xml


def german_only_scratch(apply):
    """Make a new scratch exactly de-DE, the qualification Host's state: every other active language adds an empty
    MultilingualTextItem (and shifts IDs) in the FB/UDT exports bound to the captured storage profile by exact content.
    The current reference language cannot be removed in the same change, so the others go in a second one."""
    def languages(result): return result["preview"]["effects"][0]["languageSettings"]["after"]
    state = languages(apply("project_languages_set", {"addActive": ["de-DE"], "referenceLanguage": "de-DE", "editingLanguage": "de-DE"}))
    others = [culture for culture in state["active"] if culture != "de-DE"]
    if others: state = languages(apply("project_languages_set", {"removeActive": others}))
    require(state == {"active": ["de-DE"], "reference": "de-DE", "editing": "de-DE"}, "Scratch languages are not exactly de-DE: " + str(state))


def require_capabilities(capabilities):
    """Every Host, with or without the former flag, reports combined apply."""
    plc = capabilities.get("plcIntegration") or {}
    require(plc.get("applySupported") is True and "combinedApply" in plc, "library_capabilities does not report the default combined apply.")
    return plc


def require_combined_preview(preview, eligible=False, refusal=None):
    """Every PLC recipe preview carries combinedApply, so the reviewed previewHash states whether change_apply installs."""
    block = preview.get("combinedApply")
    require(isinstance(block, dict) and block.get("enabled") is True and block.get("eligible") is eligible
            and preview.get("applySupported") is eligible, "combinedApply eligibility differs: " + str(block))
    if eligible:
        steps = [e for e in preview.get("effects", []) if e.get("action") == "applyPlcComponentSteps"]
        count = len(preview["integrationPlan"]["steps"])  # a reused FB has no import step
        require(block.get("refusal") is None and steps == [{"action": "applyPlcComponentSteps", "nativeMutation": True, "steps": count}],
                "Eligible preview lacks its exact combined effect.")
    else:
        require(isinstance(block.get("refusal"), str) and block["refusal"].startswith(refusal),
                "Unexpected combined-apply refusal: " + str(block.get("refusal")))
    return block


def refusal(error, code=REFUSED):
    detail = error.result.get("error") if isinstance(error, NativeToolError) and isinstance(error.result, dict) else None
    require(isinstance(detail, dict) and detail.get("code") == code, "Expected " + code + ", observed: " + str(error))
    return detail


def verify_combined_receipt(applied, prepared, project):
    """One verified installation whose single recovery checkpoint is the import step's complete-project archive."""
    journal = applied.get("journal") or {}
    summary = journal.get("combinedApply") or {}
    require(applied.get("state") == "applied" and applied.get("changeId") == prepared["changeId"]
            and applied.get("previewHash") == prepared["previewHash"] and summary.get("state") == "installation_verified"
            and summary.get("failedStep") is None and journal.get("engineeringVerified") is True and "failure" not in journal,
            "Combined apply did not report a verified installation.")
    checkpoints = journal.get("checkpoints")
    require(isinstance(checkpoints, list) and len(checkpoints) == 1 and checkpoints[0].get("kind") == "project"
            and checkpoints[0].get("originalPath") == project, "Combined apply must record exactly one initial project checkpoint.")
    checkpoint = checkpoints[0]
    require(hashlib.sha256(Path(checkpoint["archivePath"]).read_bytes()).hexdigest() == checkpoint["sha256"],
            "Initial checkpoint archive bytes differ from the journal.")
    return checkpoint


def version_key(name, version):
    return name, str(uuid.UUID(version["typeGuid"])), str(uuid.UUID(version["versionGuid"]))


def planned_objects(plan, existing):
    """Block keys the reviewed plan creates; the UDT is pinned by version, not path. A reused FB and a multi-instance add neither."""
    return (set() if plan["fb"]["reused"] else {"block:" + plan["fb"]["path"]}) | ({"block:" + plan["instanceDb"]["path"]} if "instanceDb" in plan else set()) \
        | (set() if existing else {"block:" + plan["caller"]["blockPath"]})


def verify_instance_db(xml, manifest, number):
    blocks = [node for node in safe_xml(xml) if node.tag.startswith("SW.Blocks.")]
    require(len(blocks) == 1 and blocks[0].tag == "SW.Blocks.InstanceDB", "Installed instance DB export must contain one instance DB.")
    attributes = blocks[0].find("AttributeList")
    require(attributes is not None and all(attributes.findtext(key) == value for key, value in {
        "Name": manifest["instance"], "InstanceOfName": manifest["root"], "InstanceOfType": "FB", "Number": str(number)}.items()),
        "Installed instance DB identity or first available number differs.")


def fbd_ob(main_xml, peer_xml, source_tag, target_tag, language="FBD", event_class="ProgramCycle"):
    """FBD (or LAD) OB fixture from two native exports: Main's OB envelope with the peer FC's coil network in that language,
    rebound from the peer's locals to two global tags. Another event class takes its number and informative inputs."""
    networks = re.findall(r"<NetworkSource>.*?</NetworkSource>", peer_xml, re.S)
    require(len(networks) == 1, "Peer caller must export exactly one network.")
    body = networks[0]
    for local, tag in (("PreserveInput", source_tag), ("PreserveOutput", target_tag)):
        body, count = re.subn(r'Scope="LocalVariable"([^>]*>\s*<Symbol>\s*<Component Name=")' + local + '"',
                              lambda match: 'Scope="GlobalVariable"' + match.group(1) + tag + '"', body)
        require(count == 1, "Peer network must access " + local + " exactly once.")
    xml, count = re.subn(r"<NetworkSource>.*?</NetworkSource>", lambda _: body, main_xml, flags=re.S)
    require(count == 1, "Main must export exactly one network.")
    number, inputs = EVENT_CLASSES[event_class]
    if inputs:
        section = '<Section Name="Input">' + "".join('<Member Name="%s" Datatype="%s" Accessibility="Public" Informative="true" />' % member
                                                    for member in inputs) + "</Section>"
        xml, count = re.subn(r'<Section Name="Input">.*?</Section>', lambda _: section, xml, flags=re.S)
        require(count == 1, "Main must export one Input section.")
        require(xml.count("<SecondaryType>ProgramCycle</SecondaryType>") == 1, "Main must be a program-cycle OB.")
        xml = xml.replace("<SecondaryType>ProgramCycle</SecondaryType>", "<SecondaryType>" + event_class + "</SecondaryType>")
    for old, new in (("<Name>Main</Name>", "<Name>" + OB_NAME + "</Name>"), ("<Number>1</Number>", "<Number>" + str(number) + "</Number>")):
        require(xml.count(old) == 1, "Main export lacks exactly one " + old)
        xml = xml.replace(old, new)
    require(xml.count("<ProgrammingLanguage>SCL</ProgrammingLanguage>") == 2, "Main must be one SCL block with one SCL network.")
    return xml.replace("<ProgrammingLanguage>SCL</ProgrammingLanguage>", "<ProgrammingLanguage>" + language + "</ProgrammingLanguage>")


class IntegrateApplyRun(ComponentRun):
    def __init__(self, args):
        require(args.run_native, "Explicit --run-native is required.")
        self.args, self.manifest = args, json.loads((FIXTURES / "manifest.json").read_text(encoding="utf-8"))
        self.base = copy.deepcopy(self.manifest)
        # Report, fixture documents and the Host's own artifacts share one fresh short run directory.
        self.output = self.native = Path(os.path.abspath(args.artifacts)) / ("i" + uuid.uuid4().hex[:6])
        # The worker refuses at prepare when a step archive would exceed TIA's 143 characters (ComponentPathTooLong);
        # check the same length here, before any authoring: root + changes/c/<8 hex>/change-lib-<32 hex>/before.
        require(len(str(self.native)) + COMBINED_PATH_OVERHEAD <= 143, "Choose a shorter --artifacts path for the combined apply's step archives.")
        require(len(str(self.native / "scratch" / ("TiaMcpScratch_" + "0" * 24) / ("TiaMcpScratch_" + "0" * 24))) <= 143,
                "Choose a short native --artifacts path.")
        self.output.mkdir(parents=True, exist_ok=False)
        self.session = None
        self.report = {"schemaVersion": 1, "qualification": "library_integrate_combined_apply", "status": "running",
            "startedAtUtc": utc_now(), "hostFlag": FLAG, "fixture": copy.deepcopy(self.manifest), "workspace": str(self.output),
            "steps": [], "cases": {}, "events": [], "fixtureChanges": [],
            "combinedApplyVerified": False, "refusalsVerified": False, "defaultHostVerified": False,
            "candidates": bool(getattr(args, "candidates", False)), "candidateCases": {},
            "runtimeVerified": False, "automaticReplayAllowed": False,
            "harnessFiles": [{"path": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                             for path in (Path(__file__), *(Path(__file__).with_name(name) for name in HARNESS))]}
        self.persist()

    def call(self, tool, arguments=None):
        result = super().call(tool, arguments)
        # The fixtures and captured storage references are de-DE only; only the qualification Host forces that itself.
        if tool == "tia_create_scratch":
            if result.get("componentQualificationLanguages") is None: german_only_scratch(self.apply)
            else: require(result["componentQualificationLanguages"] == {"active": ["de-DE"], "reference": "de-DE", "editing": "de-DE"},
                          "Qualification Host scratch is not exactly de-DE.")
        return result

    def use(self, overrides):
        """This case's fixture manifest: the base manifest with the case's overrides."""
        self.manifest = {**copy.deepcopy(self.base), **overrides}

    def recipe(self, scratch, library, released, plc, baseline, caller_recipe):
        payload = super().recipe(scratch, library, released, plc, baseline, caller_recipe)
        if self.manifest.get("multiInstance"):
            integration = payload["integration"]; del integration["instanceDb"]
            integration["multiInstance"] = {"name": self.manifest["multiInstance"]}
        return payload

    def step(self, name, operation):
        row = {"name": name, "status": "running", "startedAtUtc": utc_now()}
        self.report["steps"].append(row); self.persist()
        print("START " + name, flush=True)
        try:
            result = operation()
            row["status"] = "passed"
            return result
        except Exception as error:
            row.update(status="failed", failure=str(error))
            raise
        finally:
            row["completedAtUtc"] = utc_now(); self.persist()

    def case(self, name, enabled):
        case = self.report["cases"][name] = {"libraryIntegrateApplyFlag": enabled}
        self.persist()
        return case

    def project_versions(self):
        library = self.call("library_open", {"request": {"kind": "project"}})["libraryId"]
        names = {self.manifest["root"], self.manifest["dependency"]}
        return sorted(version_key(v["typeName"], v) for v in self.query(library) if v.get("kind") == "version" and v["typeName"] in names)

    def prepare(self, scratch, library, released, plc, baseline, caller, caller_recipe):
        payload = self.recipe(scratch, library, released, plc, baseline, caller_recipe)
        prepared = self.call("change_prepare", {"kind": "library_integrate", "payload": payload})
        return prepared, self.review_plan(prepared, payload, caller, caller_recipe["mode"] == "existing")

    def refused_apply(self, prepared, code=REFUSED):
        """One change_apply that must be refused before admission (since dc805b70 the worker refuses while resolving the
        shared-mutation scope, so the Host writes no admission record) and before any claim; the plan stays prepared."""
        admissions = admission_records(self.output)
        try:
            result = self.call("change_apply", step_request(prepared))
        except NativeToolError as error:
            detail = refusal(error, code)
        else:
            raise RuntimeError("change_apply installed a plan that must be refused: " + str(result.get("state")))
        require((detail.get("message") or "").startswith(BEFORE_ADMISSION), "The apply was not refused before admission: " + str(detail.get("message")))
        require(admission_records(self.output) == admissions, "The refused apply wrote an admission record.")
        status = self.call("change_status", {"changeId": prepared["changeId"]})
        require(status.get("state") == "prepared" and not (status.get("journal") or {}).get("idempotencyKeyHash"),
                "A refused apply changed the plan's durable state.")
        return detail

    def install(self, name, archive, released, overrides=None, features=()):
        self.use(overrides if overrides is not None else {"callerMode": name})
        out, case = self.output / name, self.case(name, True)
        scratch, opened, plc = self.stage_consumer(archive, released)
        if self.manifest.get("obCaller"):
            exports = [artifact_bytes(self.call, self.call("block_read", {"blockPath": plc + "/blocks/" + name})["artifact"]).decode("utf-8-sig")
                       for name in ("Main", self.manifest["unrelatedCaller"])]
            first, second = self.manifest["signals"]
            self.apply("block_import_xml", {"xml": fbd_ob(*exports, first["name"], second["name"]), "expectedBlockNames": [OB_NAME]})
        if self.manifest["caller"] == HOST_FB: self.apply("block_number_set", {"blockName": HOST_FB, "number": HOST_FB_NUMBER})
        if self.manifest.get("twoNetworks"):
            peer = artifact_bytes(self.call, self.call("block_read", {"blockPath": plc + "/blocks/" + self.manifest["unrelatedCaller"]})["artifact"]).decode("utf-8-sig")
            self.apply("block_import_xml", {"xml": two_network_fc(peer, self.manifest["caller"], 77), "expectedBlockNames": [self.manifest["caller"]]})
        case["capabilities"] = require_capabilities(self.call("library_capabilities"))
        baseline = snapshot(self.call, out / "before")
        caller, caller_recipe, network = self.select_caller(baseline, plc)
        prepared, plan = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
        after = self.installed(case, out, prepared, plan, baseline, plc, caller, network, released, features)
        self.call("library_close", {"libraryId": opened["libraryId"]})
        self.finish(case, out, prepared, plc, case["initialCheckpoint"], baseline, after, [])

    def installed(self, case, out, prepared, plan, baseline, plc, caller, network, released, features=()):
        """Apply one eligible plan once and verify the installation against its baseline; returns the after snapshot."""
        applied, record = self.apply_combined(case, prepared, baseline, features)
        return self.verify_installed(case, out, plan, baseline, plc, caller, network, released, record)

    def apply_combined(self, case, prepared, baseline, features):
        """The one change_apply of an eligible plan, whose preview names exactly these candidate features."""
        case["plan"] = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}
        case["combinedApply"] = require_combined_preview(prepared["preview"], eligible=True)
        require(sorted(case["combinedApply"].get("candidateFeatures") or {}) == sorted(features),
                "Preview names other candidate features: " + str(case["combinedApply"].get("candidateFeatures")))
        request = step_request(prepared)
        self.retain("combined_apply_dispatching", request)
        try:
            applied = self.call("change_apply", request)
        except Exception:
            # Inspection only: a failed or uncertain apply is never replayed or restored automatically.
            try: case["failureStatus"] = self.call("change_status", {"changeId": prepared["changeId"]})
            except Exception as inspection: case["failureStatusError"] = str(inspection)
            self.persist()
            raise
        self.retain("combined_apply_result", applied)
        checkpoint = case["initialCheckpoint"] = verify_combined_receipt(applied, prepared, baseline["inventory"]["project"]["path"])
        record = json.loads(Path(applied["journal"]["combinedApply"]["coordinatorJournal"]).read_text(encoding="utf-8-sig"))["record"]
        require(record.get("state") == "installation_verified", "Coordinator journal is not at verified installation.")
        return applied, record

    def verify_installed(self, case, out, plan, baseline, plc, caller, network, released, record):
        existing = self.manifest["callerMode"] == "existing"
        after = snapshot(self.call, out / "after")
        reused, multi = plan["fb"]["reused"], self.manifest.get("multiInstance")
        # A reused FB adds only its new storage and a new caller; a multi-instance adds no block at all.
        added = installed_objects(after, self.manifest, plc, existing) if not reused else \
            (["block:" + plan["instanceDb"]["path"]] if "instanceDb" in plan else []) + ([] if existing else ["block:" + plan["caller"]["blockPath"]])
        require(planned_objects(plan, existing) == {key for key in added if key.startswith("block:")}, "Installed blocks differ from the reviewed plan.")
        # The new network directly follows the selected one (the last by default); the caller's own instance DB gains the member.
        dependent, parent = self.manifest.get("dependentDb"), self.manifest.get("parentFb")
        nested = ([parent] if parent else []) + list(self.manifest.get("parentDependents") or [])
        verify_preserved_installation(baseline, after, caller, added, multi, self.manifest.get("insertNetworkIndex", -1) if existing else None,
                                      [p for p in (dependent,) if p] + nested)
        for path in nested:
            before_xml, after_xml = (Path(s["objects"]["block:" + path]["file"]).read_text(encoding="utf-8-sig") for s in (baseline, after))
            captured = COMPONENT_TRANSITIVE_PARENT_DB if path.endswith("_DB") else COMPONENT_TRANSITIVE_PARENT
            verify_parent_fb(before_xml, after_xml, captured.read_text(encoding="utf-8-sig"), HOST_FB, multi)
        if parent:
            case["embeddingParentVerified"] = nested
        if dependent:
            before_xml, after_xml = (Path(s["objects"]["block:" + dependent]["file"]).read_text(encoding="utf-8-sig") for s in (baseline, after))
            verify_dependent_db(before_xml, after_xml, COMPONENT_EFFECT.read_text(encoding="utf-8-sig"), multi)
            case["dependentInstanceDbVerified"] = dependent
        caller_xml = Path(after["objects"]["block:" + caller]["file"]).read_text(encoding="utf-8-sig")
        verify_component_call(caller_xml, self.manifest)
        if existing:
            proof = case["callerInsertion"] = record["installationEvidence"]["callerInsertion"]
            require(proof["verified"] is True and proof["insertAfterNetworkId"] == network, "Native caller insertion proof differs.")
            require(not multi or proof.get("addedStaticMember", {}).get("name") == multi, "Native multi-instance member proof differs.")
        else:
            verify_standalone_caller(caller_xml, self.manifest)
        if not multi:
            verify_instance_db(Path(after["objects"]["block:" + plan["instanceDb"]["path"]]["file"]).read_text(encoding="utf-8-sig"),
                               self.manifest, first_available_db_number(baseline["inventory"]["blocks"]))
        # Reuse installs nothing twice: the project library keeps exactly the released versions.
        require(self.project_versions() == sorted(version_key(name, pin) for name, pin in released.items()),
                "Project library does not hold exactly the released FB/UDT versions.")
        require(not reused or (len(plan["steps"]) == 1 and record["library"].get("dispatch") is None and plan["reuse"]["duplicatesCreated"] is False
                               and sorted(v["versionGuid"] for v in plan["reuse"]["versions"]) == sorted(v["versionGuid"] for v in released.values())),
                "Reuse plan or journal differs: it must reuse every released version in one typed step.")
        case["installationVerified"] = True; self.persist()
        return after

    def finish(self, case, out, prepared, plc, checkpoint, baseline, after, restored_versions):
        """Fresh reopen, a clean compile, then the single initial checkpoint into a new location in a fresh owned Portal."""
        self.session.reopen(self.session.current_project)
        assert_snapshot(after, snapshot(self.call, out / "reopened"))
        case["freshReopenVerified"] = True; self.persist()
        compile_prepared = self.call("change_prepare", {"kind": "plc_compile", "plcPath": plc, "payload": {}})
        require_compile_only(compile_prepared, self.call("change_apply", step_request(compile_prepared)), plc)
        case["compileVerified"] = True; self.persist()
        self.restore(case, prepared, checkpoint, baseline, out, restored_versions)

    def reuse(self, name, archive, released, multi=False, dependents=False, parent=False):
        """Candidate: a first verified install, then a plan that reuses the installed FB (exact proofs, no import step), either
        in a second new FBD FC with a new global DB or as a multi-instance in an existing FB caller. With dependents the FB
        caller has its own instance DB, which gains the member too; with parent a parent FB embeds the caller and gains the
        member nested in its embedded caller through the captured component transitive effect."""
        self.use({"callerMode": "createStandaloneFc"})
        case, out = self.case(name, True), self.output / name
        scratch, opened, plc = self.stage_consumer(archive, released)
        if multi:
            self.host_fb(plc)
        if dependents:
            self.apply("source_upsert", {"language": "SCL", "source": HOST_DB_SOURCE, "expectedBlockNames": [HOST_DB]})
        baseline = snapshot(self.call, out / "before-first")
        caller, caller_recipe, network = self.select_caller(baseline, plc)
        first, plan = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
        case["first"] = {}
        self.installed(case["first"], out / "first", first, plan, baseline, plc, caller, network, released)
        if parent:
            # After the first install, which imports the component FB with its own number, so the parent cannot take it.
            self.apply("source_upsert", {"language": "SCL", "source": PARENT_SOURCE, "expectedBlockNames": [PARENT_FB]})
            self.apply("block_number_set", {"blockName": PARENT_FB, "number": PARENT_FB_NUMBER})
        self.use({"callerMode": "existing", "caller": "TiaMcpComponentHost", "multiInstance": MULTI_NAME,
                  **({"dependentDb": plc + "/blocks/" + HOST_DB} if dependents else {}), **({"parentFb": plc + "/blocks/" + PARENT_FB} if parent else {})} if multi else
                 {"callerMode": "createStandaloneFc", "caller": REUSE_CALLER, "instance": REUSE_INSTANCE})
        baseline = snapshot(self.call, out / "before")
        caller, caller_recipe, network = self.select_caller(baseline, plc)
        prepared, plan = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
        applied, record = self.apply_combined(case, prepared, baseline, PARENT_FEATURES if parent else DEPENDENT_DB_FEATURES if dependents else [])
        after = self.verify_installed(case, out, plan, baseline, plc, caller, network, released, record)
        self.call("library_close", {"libraryId": opened["libraryId"]})
        # The typed step's own pre-mutation archive is the recovery anchor: it holds the first installation.
        require(case["initialCheckpoint"]["archivePath"] != case["first"]["initialCheckpoint"]["archivePath"]
                and case["initialCheckpoint"].get("changeId") == applied["journal"]["combinedApply"]["typedChangeId"],
                "The reuse checkpoint is not the typed step's own archive.")
        self.finish(case, out, prepared, plc, case["initialCheckpoint"], baseline, after, sorted(version_key(n, pin) for n, pin in released.items()))

    def caller_fixture(self, plc, name, kind, language, networks=1):
        """An existing caller before the baseline: its fixture source under this name, an FB moved to HOST_FB_NUMBER; with two
        networks its one network twice, re-imported over itself from its own native export."""
        source = (self.native / "documents" / CALLER_SOURCES[kind, language]).read_text(encoding="utf-8-sig")
        source = source.replace('"' + (HOST_FB if kind == "FB" else self.base["caller"]) + '"', '"' + name + '"')
        self.apply("source_upsert", {"language": "SIMATIC_SD", "source": source, "expectedBlockNames": [name]})
        if kind == "FB": self.apply("block_number_set", {"blockName": name, "number": HOST_FB_NUMBER})
        if networks == 2:
            xml = artifact_bytes(self.call, self.call("block_read", {"blockPath": plc + "/blocks/" + name})["artifact"]).decode("utf-8-sig")
            number = int(re.search(r"<Number>(\d+)</Number>", xml).group(1))
            self.apply("block_import_xml", {"xml": two_network_fc(xml, name, number), "expectedBlockNames": [name]})

    def ob_fixture(self, plc, language, networks=1, event_class="ProgramCycle"):
        """The OB caller before the baseline: Main's envelope with the peer FBD FC's network, or for LAD the LAD FC fixture's network
        (imported as LAD_PEER); with two networks its one network twice, re-imported over itself from its own native export."""
        peer = self.manifest["unrelatedCaller"]
        if language == "LAD":
            source = (self.native / "documents" / CALLER_SOURCES["FC", "LAD"]).read_text(encoding="utf-8-sig")
            self.apply("source_upsert", {"language": "SIMATIC_SD", "source": source.replace('"' + self.base["caller"] + '"', '"' + LAD_PEER + '"'),
                                         "expectedBlockNames": [LAD_PEER]})
            peer = LAD_PEER
        export = lambda name: artifact_bytes(self.call, self.call("block_read", {"blockPath": plc + "/blocks/" + name})["artifact"]).decode("utf-8-sig")
        first, second = self.manifest["signals"]
        self.apply("block_import_xml", {"xml": fbd_ob(export("Main"), export(peer), first["name"], second["name"], language, event_class),
                                        "expectedBlockNames": [OB_NAME]})
        if networks == 2:
            self.apply("block_import_xml", {"xml": two_network_fc(export(OB_NAME), OB_NAME, EVENT_CLASSES[event_class][0]), "expectedBlockNames": [OB_NAME]})

    def combination(self, name, archive, released, reuse, caller, features):
        """One combination of verified features. A reuse plan first installs the verified standalone FBD FC, so the plan under test
        reuses the installed FB with a new global DB or, with "multi", a multi-instance; otherwise the plan imports it fresh."""
        existing = not caller.get("new")
        target = HOST_FB if caller["kind"] == "FB" else OB_NAME if caller["kind"] == "OB" else REUSE_CALLER if reuse else self.base["caller"]
        overrides = {"callerMode": "existing" if existing else "createStandaloneFc", "caller": target, "language": caller["language"],
                     "callerSource": None, "insertNetworkIndex": 0 if caller.get("networks") == 2 else -1,
                     "multiInstance": MULTI_NAME if caller.get("multi") else None, **({"instance": REUSE_INSTANCE} if reuse else {})}
        case, out = self.case(name, True), self.output / name
        self.use({"callerMode": "createStandaloneFc"} if reuse else overrides)
        scratch, opened, plc = self.stage_consumer(archive, released)
        if caller["kind"] == "OB": self.ob_fixture(plc, caller["language"], caller.get("networks", 1), caller.get("eventClass", "ProgramCycle"))
        elif existing: self.caller_fixture(plc, target, caller["kind"], caller["language"], caller.get("networks", 1))
        if caller.get("dependents"):
            self.apply("source_upsert", {"language": "SCL", "source": HOST_DB_SOURCE, "expectedBlockNames": [HOST_DB]})
            overrides["dependentDb"] = plc + "/blocks/" + HOST_DB
        if reuse:
            baseline = snapshot(self.call, out / "before-first")
            first_caller, first_recipe, first_network = self.select_caller(baseline, plc)
            first, plan = self.prepare(scratch, opened["libraryId"], released, plc, baseline, first_caller, first_recipe)
            case["first"] = {}
            self.installed(case["first"], out / "first", first, plan, baseline, plc, first_caller, first_network, released)
            if caller.get("parent"):
                # After the first install, which imports the component FB with its own number (as in reuse(parent=True)).
                overrides["parentFb"], overrides["parentDependents"] = plc + "/blocks/" + PARENT_FB, []
                self.apply("source_upsert", {"language": "SCL", "source": PARENT_SOURCE, "expectedBlockNames": [PARENT_FB]})
                self.apply("block_number_set", {"blockName": PARENT_FB, "number": PARENT_FB_NUMBER})
                if caller.get("deep"):
                    self.apply("source_upsert", {"language": "SCL", "source": PARENT_DB_SOURCE, "expectedBlockNames": [PARENT_DB]})
                    self.apply("source_upsert", {"language": "SCL", "source": GRAND_SOURCE, "expectedBlockNames": [GRAND_FB]})
                    self.apply("block_number_set", {"blockName": GRAND_FB, "number": GRAND_FB_NUMBER})
                    self.apply("source_upsert", {"language": "SCL", "source": GRAND_DB_SOURCE, "expectedBlockNames": [GRAND_DB]})
                    overrides["parentDependents"] = [plc + "/blocks/" + name for name in (PARENT_DB, GRAND_FB, GRAND_DB)]
            self.use(overrides)
        baseline = snapshot(self.call, out / "before")
        path, recipe, network = self.select_caller(baseline, plc)
        prepared, plan = self.prepare(scratch, opened["libraryId"], released, plc, baseline, path, recipe)
        applied, record = self.apply_combined(case, prepared, baseline, features)
        after = self.verify_installed(case, out, plan, baseline, plc, path, network, released, record)
        self.call("library_close", {"libraryId": opened["libraryId"]})
        # A reused FB's recovery anchor is the typed step's own pre-mutation archive, which holds the first installation.
        require(not reuse or case["initialCheckpoint"]["archivePath"] != case["first"]["initialCheckpoint"]["archivePath"]
                and case["initialCheckpoint"].get("changeId") == applied["journal"]["combinedApply"]["typedChangeId"],
                "The reuse checkpoint is not the typed step's own archive.")
        self.finish(case, out, prepared, plc, case["initialCheckpoint"], baseline, after,
                    sorted(version_key(n, pin) for n, pin in released.items()) if reuse else [])

    def dependents_refused(self, case, out, prepared, baseline, dependent, opened):
        """The preview already refuses with DependencyProfileUnavailable, naming the uncovered dependent; change_apply is
        refused before admission (no admission record), the plan stays prepared and the scratch is unchanged."""
        block = case["combinedApply"] = require_combined_preview(prepared["preview"], refusal="The multi-instance caller ")
        require(block.get("refusalCode") == DEPENDENTS_REFUSED and dependent in (block.get("refusalDetails") or {}).get("dependents", []),
                "The preview does not refuse the dependent " + dependent + ": " + str(block.get("refusalDetails")))
        case["refusal"] = self.refused_apply(prepared, DEPENDENTS_REFUSED)
        assert_snapshot(baseline, snapshot(self.call, out / "after-refusal"))
        case.update(outcome="refused", admissionWritten=False, projectUnchanged=True); self.persist()
        self.call("library_close", {"libraryId": opened["libraryId"]})
        self.session.close()

    def restore(self, case, prepared, checkpoint, baseline, out, restored_versions=()):
        self.session.close()
        self.session.start()
        status = self.session.audit("tia_session_status")
        require(status.get("ownsPortalProcess") is True and status.get("processId") == self.session.session["processId"]
                and status.get("projects") == [], "Restore requires one fresh empty owned Portal.")
        request = {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}
        self.retain("change_restore_dispatching", request)
        # Library restore needs an empty Portal, so it bypasses the selected-project guard; dispatched once.
        restored = self.session.audit("change_restore", request)
        self.retain("change_restore_result", restored)
        resources = restored.get("resources", [])
        require(restored.get("state") == "restored_to_new_locations" and len(resources) == 1 and resources[0].get("kind") == "project"
                and resources[0].get("retrieved") is True and resources[0].get("archiveSha256") == checkpoint["sha256"],
                "Initial checkpoint restore receipt differs.")
        recovered = resources[0]["project"]["path"]
        require(Path(recovered).resolve() != Path(checkpoint["originalPath"]).resolve(), "Restore overwrote the original project.")
        self.session.remember(recovered)
        self.session.guard()
        assert_snapshot(baseline, snapshot(self.call, out / "restored"))
        require(self.project_versions() == list(restored_versions), "Restored project library holds other component versions.")
        self.session.reopen(recovered)
        assert_snapshot(baseline, snapshot(self.call, out / "restored-reopened"))
        case["restoreVerified"] = True; self.persist()
        self.session.close()

    def host_fb(self, plc):
        """The FBD FB caller fixture, imported before the baseline; returns its path and an insertion recipe after its network."""
        source = (self.native / "documents" / "callers" / (HOST_FB + ".s7dcl")).read_text(encoding="utf-8-sig")
        self.apply("source_upsert", {"language": "SIMATIC_SD", "source": source, "expectedBlockNames": [HOST_FB]})
        self.apply("block_number_set", {"blockName": HOST_FB, "number": HOST_FB_NUMBER})
        return plc + "/blocks/" + HOST_FB

    def host_fb_recipe(self, baseline, path):
        network, _ = terminal_network(Path(baseline["objects"]["block:" + path]["file"]).read_text(encoding="utf-8-sig"), "FBD")
        return {"mode": "existing", "blockPath": path, "insertAfterNetworkId": network}

    def refusals(self, archive, released):
        """Flag on: an existing FBD FB caller with an unqualified multi-instance is refused without a project change."""
        self.use({"callerMode": "createStandaloneFc"})  # each case starts from the base manifest, no multiInstance left over
        case, out = self.case("refusals", True), self.output / "refusals"
        scratch, opened, plc = self.stage_consumer(archive, released)
        host = self.host_fb(plc)
        baseline = snapshot(self.call, out / "before")
        for name, caller, caller_recipe, reason, multi in (
                ("fbMultiInstance", host, self.host_fb_recipe(baseline, host), FB_REFUSAL, MULTI_NAME),):
            self.manifest["multiInstance"] = multi
            prepared, _ = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
            case[name] = {"changeId": prepared["changeId"], "combinedApply": require_combined_preview(prepared["preview"], refusal=reason)}
            case[name]["refusal"] = self.refused_apply(prepared); self.persist()
        assert_snapshot(baseline, snapshot(self.call, out / "after"))
        case["projectUnchanged"] = True; self.persist()
        self.session.close()

    def default_host(self, archive, released):
        """Host without the former flag: combined apply is the default, so the standalone FBD plan previews eligible (the
        install steps already applied it; it is not applied again) and an FB caller plan with an unverified multi-instance is
        refused with no change."""
        self.use({"callerMode": "createStandaloneFc"})  # each case starts from the base manifest, no multiInstance left over
        self.session.client_factory = host_client(False)
        case, out = self.case("defaultHost", False), self.output / "defaultHost"
        scratch, opened, plc = self.stage_consumer(archive, released)
        case["capabilities"] = require_capabilities(self.call("library_capabilities"))
        host = self.host_fb(plc)
        baseline = snapshot(self.call, out / "before")
        caller, caller_recipe, _ = self.select_caller(baseline, plc)
        prepared, _ = self.prepare(scratch, opened["libraryId"], released, plc, baseline, caller, caller_recipe)
        case["combinedApply"] = require_combined_preview(prepared["preview"], eligible=True)
        self.manifest["multiInstance"] = MULTI_NAME
        prepared, _ = self.prepare(scratch, opened["libraryId"], released, plc, baseline, host, self.host_fb_recipe(baseline, host))
        self.manifest["multiInstance"] = None
        case["fbMultiInstance"] = {"changeId": prepared["changeId"], "combinedApply": require_combined_preview(prepared["preview"], refusal=FB_REFUSAL),
                            "refusal": self.refused_apply(prepared)}
        assert_snapshot(baseline, snapshot(self.call, out / "after"))
        case["projectUnchanged"] = True; self.persist()
        self.session.close()

    def candidates(self, archive, released):
        """Every candidate case on the qualification Host, each in its own owned scratch; one failure stops only its case."""
        self.session.client_factory = candidate_client
        cases = [(name, lambda n=name: self.install(n, archive, released, *CANDIDATES[n])) for name in CANDIDATES]
        cases += [("reuse", lambda: self.reuse("reuse", archive, released)), ("multiInstance", lambda: self.reuse("multiInstance", archive, released, True)),
                  ("multiInstanceDependents", lambda: self.reuse("multiInstanceDependents", archive, released, True, True)),
                  ("multiInstanceParent", lambda: self.reuse("multiInstanceParent", archive, released, True, parent=True))]
        cases += [(name, lambda n=name: self.combination(n, archive, released, *COMBINATIONS[n])) for name in COMBINATIONS]
        selected = getattr(self.args, "case", None)
        require(not selected or set(selected) <= {name for name, _ in cases}, "Unknown --case: " + str(selected))
        for name, operation in [(n, o) for n, o in cases if not selected or n in selected]:
            try:
                self.step("candidate:" + name, operation)
                self.report["candidateCases"][name] = self.report["cases"][name].get("outcome", "installed")
            except Exception as error:
                self.report["candidateCases"][name] = "failed: " + str(error)
                try: self.session.close()
                except Exception as cleanup: self.report["candidateCases"][name] += "; cleanup: " + str(cleanup)
            self.persist()
        failed = failed_candidates(self.report["candidateCases"])
        if failed: raise RuntimeError("Candidate cases failed: " + json.dumps(failed))
        self.report["status"] = "passed"

    def run(self):
        primary = None
        try:
            self.session = OwnedScratch(self.args.host, self.args.worker, self.output, self.report, self.persist,
                                        timeout=self.args.timeout, client_factory=host_client(True), extra_tools=EXTRA_TOOLS)
            archive, released = self.step("author", self.author)
            if self.args.production_case:
                self.session.client_factory = host_client(False)
                name = self.args.production_case
                self.report["productionCase"] = name
                self.step("production:" + name, lambda: self.install(name, archive, released, *CANDIDATES[name]))
                self.report["status"] = "passed"
                return
            if self.args.candidates:
                self.candidates(archive, released)
                return
            for mode in MODES:
                self.step("install:" + mode, lambda: self.install(mode, archive, released))
            self.report["combinedApplyVerified"] = True
            self.step("refusals", lambda: self.refusals(archive, released))
            self.report["refusalsVerified"] = True
            self.step("defaultHost", lambda: self.default_host(archive, released))
            self.report["defaultHostVerified"] = True
            self.report["status"] = "passed"
        except Exception as error:
            primary = error
            self.report.update(status="failed", failure=str(error), traceback=traceback.format_exc())
            raise
        finally:
            cleanup_error = None
            try:
                if self.session is not None: self.session.close()
            except Exception as error:
                cleanup_error = error
                self.report.update(status="failed", cleanupFailure=str(error))
            self.report["completedAtUtc"] = utc_now(); self.persist()
            print("Report: " + str(self.output / "result.json"), flush=True)
            if primary is None and cleanup_error is not None: raise cleanup_error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    parser.add_argument("--candidates", action="store_true", help="Qualify the candidate shapes on a --component-qualification Host instead of the verified ones.")
    parser.add_argument("--case", action="append", help="With --candidates, run only this candidate case (repeatable).")
    parser.add_argument("--production-case", choices=sorted(CANDIDATES), help="Run one admitted case on the normal Host without qualification flags.")
    args = parser.parse_args()
    require(5 <= args.timeout <= 900, "Timeout must be 5..900 seconds.")
    IntegrateApplyRun(args).run()


if __name__ == "__main__": main()
