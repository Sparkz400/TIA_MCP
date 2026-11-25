"""contracts/hmi-inspection.v1.schema.json: the per-section request rules agree with the worker.

Each case is (request, worker answers with rows). The expectations follow OpennessSession.Hmi.cs (HmiInspect,
HmiResolveNativeTarget, the items branch of HmiCaptureClassic/Unified) and HmiInspectionRules.RequireTargetKind; they are
not native evidence. Synthetic names only."""
import json
import pathlib
import unittest

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = pathlib.Path(__file__).resolve().parents[2]
DOCUMENTS = [json.loads((ROOT / "contracts" / name).read_text(encoding="utf-8"))
             for name in ("hmi-inspection.v1.schema.json", "hmi-identity.v2.schema.json")]
REGISTRY = Registry().with_resources((d["$id"], Resource.from_contents(d)) for d in DOCUMENTS)
REQUEST = Draft202012Validator(DOCUMENTS[0], registry=REGISTRY)

HMI = "/devices/Panel_1/items/HMI_RT_1/hmi/HMI_RT_1"
SCREEN = HMI + "/screens/Screen_1"


def identity(kind, path, **extra):
    """A full v2 identity as hmi_inspect returns it (HmiIdentity)."""
    return {"schemaVersion": 2, "projectPath": "D:/fixture/Fixture.ap21", "projectCopyId": "copy", "sessionGeneration": "session",
            "family": "unified", "devicePath": "/devices/Panel_1/items/HMI_RT_1", "hmiPath": HMI, "scopePath": HMI,
            "objectPath": path, "kind": kind, "name": path.rsplit("/", 1)[-1], **extra}


DEVICE = identity("hmiDevice", HMI)
# One object kind that belongs to each section, at a path below the device (RequireTargetKind / Section).
OWN = {
    "connections": ("hmiConnection", HMI + "/connections/Connection_1"),
    "tags": ("hmiTagTable", HMI + "/tagTables/Table_1"),
    "screens": ("hmiScreen", SCREEN),
    "items": ("hmiScreenItem", SCREEN + "/items/Button_1"),
    "alarms": ("hmiAlarm", HMI + "/discreteAlarms/Alarm_1"),
    "scripts": ("hmiScript", HMI + "/scripts/Module_1"),
    "resources": ("hmiResource", HMI + "/textLists/List_1"),
    "model": ("hmiModelNode", HMI + "/model/Screens/Screen_1"),
}
# A kind from another section at the same kind of path: refused before any capture.
FOREIGN = {"connections": "hmiTag", "tags": "hmiModelNode", "screens": "hmiTag", "items": "hmiTag", "alarms": "hmiBogus",
           "scripts": "hmiScreen", "resources": "hmiAlarm", "model": "hmiScreen"}


def cases():
    # devices has no section-specific refusal in the worker; its refused case is the shared limit bound (HmiBound).
    yield {"section": "devices"}, True
    yield {}, True  # section defaults to devices
    yield {"schemaVersion": 1, "section": "devices", "limit": 37, "offset": 0}, True
    yield {"section": "devices", "limit": 0}, False
    for section, (kind, path) in OWN.items():
        extra = {"screenPath": SCREEN} if section == "items" else {}
        # The shapes the harnesses send: the device identity as target, optionally with paging (qualify_hmi.inspect_all).
        yield {"section": section, "target": DEVICE, **extra}, True
        yield {"schemaVersion": 1, "limit": 37, "offset": 0, "section": section, "target": {"hmiPath": HMI}, **extra}, True
        yield {"section": section, "identity": DEVICE, **extra}, True
        yield {"section": section, "hmiPath": HMI, "family": "unified", **extra}, True
        # An object identity of the section's own kind.
        yield {"section": section, "target": identity(kind, path), **extra}, True
        # No HMI selected: HmiResolveNativeTarget refuses with InvalidRequest.
        yield {"section": section, **extra}, False
        yield {"section": section, "hmiPath": "", **extra}, False
        # A kind of another section below the device: RequireTargetKind refuses, in target and in identity.
        yield {"section": section, "target": identity(FOREIGN[section], path), **extra}, False
        yield {"section": section, "identity": identity(FOREIGN[section], path), **extra}, False
        # The same kind at the device path selects the HMI (isDevicePath) and stays accepted.
        yield {"section": section, "target": identity(FOREIGN[section], HMI), **extra}, True
        # Fields another section reads are ignored by the worker, never refused.
        yield {"section": section, "target": DEVICE, "modelRoots": ["Screens"], "excludeSystem": True, "includeScreenOwner": True,
               "screenPath": SCREEN}, True
    # items: a screen may select its own items, and screenPath may come from the selector.
    yield {"section": "items", "target": identity("hmiScreen", SCREEN), "screenPath": SCREEN}, True
    yield {"section": "items", "target": identity("hmiScreen", SCREEN, screenPath=SCREEN)}, True
    yield {"section": "items", "identity": identity("hmiScreen", SCREEN, screenPath=SCREEN)}, True
    # items without screenPath: both families answer a failed capture with an InvalidRequest diagnostic and no rows.
    yield {"section": "items", "target": DEVICE}, False
    yield {"section": "items", "target": DEVICE, "screenPath": ""}, False
    # The worker reads the selector's screenPath from target before identity.
    yield {"section": "items", "target": {"hmiPath": HMI}, "identity": identity("hmiScreen", SCREEN, screenPath=SCREEN)}, False
    # Requests the harnesses send (qualify_hmi_gate_route.py, qualify_hmi_text_list_route.py).
    yield {"schemaVersion": 1, "target": DEVICE, "section": "tags", "objectPath": HMI + "/tagTables/Table_1", "includeSource": True}, True
    yield {"section": "resources", "target": DEVICE, "limit": 200}, True
    yield {"section": "model", "target": DEVICE, "limit": 200, "offset": 200, "rowMode": "compact", "modelRoots": ["Screens", "TagTables"]}, True


class HmiInspectionSectionTests(unittest.TestCase):
    def test_schema_is_valid(self):
        Draft202012Validator.check_schema(DOCUMENTS[0])

    def test_every_section_has_an_accepted_and_a_refused_request(self):
        seen = {}
        for request, accepted in cases():
            seen.setdefault(request.get("section", "devices"), set()).add(accepted)
        self.assertEqual({section: {True, False} for section in DOCUMENTS[0]["properties"]["section"]["enum"]}, seen)

    def test_requests_agree_with_the_worker_rules(self):
        for request, accepted in cases():
            with self.subTest(request=request):
                self.assertEqual(accepted, REQUEST.is_valid(request), [error.message for error in REQUEST.iter_errors(request)][:3])

    def test_every_section_states_the_fields_it_reads(self):
        described = set()
        for rule in DOCUMENTS[0]["allOf"]:
            section = rule["if"]["properties"]["section"].get("const")
            if section:
                self.assertTrue(rule["then"]["description"].startswith("section " + section), section)
                described.add(section)
        self.assertEqual(set(DOCUMENTS[0]["properties"]["section"]["enum"]), described)

    def test_response_definitions_are_untouched_by_the_request_rules(self):
        row = {"identity": DEVICE, "kind": "hmiDevice", "name": "HMI_RT_1", "parentPath": "/devices/Panel_1/items/HMI_RT_1", "source": {"kind": "native"}}
        Draft202012Validator({"$ref": DOCUMENTS[0]["$id"] + "#/$defs/row"}, registry=REGISTRY).validate(row)


if __name__ == "__main__":
    unittest.main()
