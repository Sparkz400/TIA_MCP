"""Fake-bridge checks of the combined recipe probe's own logic; not native evidence."""
import unittest
from plc_hmi_recipe_probe import probe_plc_hmi_recipe

H, P = "/d/HMI_{0}/hmi/HMI_RT_{0}", "/d/PLC_{0}/plc/PLC_{0}"
GUID = lambda c: (c * 8) + "-1111-1111-1111-111111111111"


def fixture():
    profile = {"profile": "unified-comfort", "plcHmiFixture": {"status": "created", "hmiA": "HMI_RT_1",
        "motorStatus": {"typeGuid": GUID("a"), "v1": {"versionGuid": GUID("b")}, "v2": {"versionGuid": GUID("c")}},
        "motorDrive": {"typeGuid": GUID("d"), "v1": {"versionGuid": GUID("e")}}}}
    inspections, proofs = [], []
    for n in (1, 2):
        owner = {"name": "HMI_RT_%d" % n, "objectPath": H.format(n), "hmiPath": H.format(n), "family": "unified"}
        connection = {"objectPath": H.format(n) + "/connections/HMI_Connection_%d" % n}
        inspections += [{"target": owner, "section": "connections", "result": {"items": [{"identity": connection, "name": "HMI_Connection_%d" % n}]}},
                        {"target": owner, "section": "tags", "result": {"items": []}},
                        {"target": owner, "section": "screens", "result": {"items": [{"identity": {"objectPath": H.format(n) + "/screens/Screen_1"}}]}}]
        proofs.append({"hmiSoftware": owner["name"], "plcOwner": {"objectPath": P.format(n)}, "connection": {"sourcePath": connection["objectPath"]},
                       "declaration": {"sourcePath": H.format(n) + "/tagTables/Default%20tag%20table/tags/LinkedSignal"}})
    return profile, inspections, proofs


class Fake:
    def __init__(self, update_refused=True, apply_refused=True, mutate=False):
        self.update_refused, self.apply_refused, self.mutate, self.calls, self.queries = update_refused, apply_refused, mutate, [], 0

    def tool(self, name, arguments=None):
        if name == "tia_session_status": return {"projects": [{"name": "Fixture", "path": "C:/x/Fixture.ap21"}]}
        if name == "library_open": return {"libraryId": "project-lib"}
        if name == "library_list": return {"libraries": [{"libraryId": "project-lib", "revision": "r1"}]}
        if name == "library_query":
            self.queries += 1
            return {"items": [{"typeName": "MotorStatus"}] + ([{"typeName": "Extra"}] if self.mutate and self.queries > 1 else [])}
        if name == "hmi_inspect": return {"items": [{"identity": {"objectPath": arguments["request"]["target"]["objectPath"] + "/tagTables/T/tags/LinkedSignal"}}]}
        raise AssertionError(name)

    def request(self, method, params):
        arguments = params["arguments"]; self.calls.append(arguments)
        if params["name"] == "change_apply":
            return {"structuredContent": {"state": "applied"}} if not self.apply_refused else {"isError": True, "structuredContent": {"error": {"code": "PlcIntegrationNotQualified"}}}
        payload = arguments["payload"]
        if payload["hmi"]["family"] == "classic": return {"isError": True, "structuredContent": {"error": {"code": "HmiFamilyNotQualified"}}}
        if payload["action"] == "plc_hmi_component_plan":
            tags = payload["hmi"]["tags"]
            return {"structuredContent": {"changeId": "lib-1", "previewHash": "0" * 64, "preview": {"integrationPlan": {
                "status": "planned_only", "applySupported": False,
                "blockers": [{"code": c} for c in ("HmiMemberMappingUnqualified", "HmiScreenItemCreationUnavailable", "HmiPreservationGateClosed")],
                "steps": [{"kind": k} for k in ("library_instantiate", "graphical_typed_edit", "hmi_engineering")],
                "hmi": {"target": {"connection": {"plcPath": payload["target"]["plcPath"]}}, "tags": [{"plcSymbol": payload["integration"]["instanceDb"]["name"] + "." + ".".join(tags[0]["source"]["members"])}]}}}}}
        if not self.update_refused: return {"structuredContent": {"changeId": "lib-2"}}
        consumer = {"itemPath": H.format(1) + "/screens/Screen_1/items/MotorSpeedConsumer", "property": "Left"}
        return {"isError": True, "structuredContent": {"error": {"code": "HmiBindingWouldBreak", "details": {"breakingCount": 1, "unresolved": [], "bindings": [
            {"tagName": "MotorSpeed", "status": "typeChanged", "beforeDataType": "Real", "afterDataType": "LReal", "consumers": [consumer]},
            {"tagName": "MotorRunning", "status": "unchanged", "consumers": []}]}}}}


class PlcHmiRecipeProbe(unittest.TestCase):
    def test_plans_and_refusals_pass(self):
        client = Fake(); result = probe_plc_hmi_recipe(client, *fixture())
        self.assertEqual("passed", result["status"]); self.assertFalse(result["applyQualified"])
        install = client.calls[0]["payload"]
        self.assertEqual(P.format(2), install["target"]["plcPath"]); self.assertEqual("Default tag table", install["hmi"]["tagTable"])
        self.assertEqual(GUID("e"), install["selection"][0]["versionGuid"]); self.assertEqual(GUID("b"), install["integration"]["dependencyPins"][0]["versionGuid"])
        update = client.calls[2]["payload"]
        self.assertEqual(P.format(1), update["target"]["plcPath"]); self.assertEqual(GUID("c"), update["selection"][0]["versionGuid"])

    def test_accepted_update_apply_or_changed_inventory_fail(self):
        for client, message in ((Fake(update_refused=False), "exact impact report"), (Fake(apply_refused=False), "stay refused"), (Fake(mutate=True), "changed the project library")):
            with self.subTest(message=message), self.assertRaisesRegex(AssertionError, message):
                probe_plc_hmi_recipe(client, *fixture())

    def test_missing_fixture_refuses(self):
        profile, inspections, proofs = fixture(); profile["plcHmiFixture"]["status"] = "failed"
        with self.assertRaisesRegex(AssertionError, "plchmi fixture"):
            probe_plc_hmi_recipe(Fake(), profile, inspections, proofs)


if __name__ == "__main__":
    unittest.main()
