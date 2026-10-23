"""Portable adversarial checks for the existing native TO fixture oracle."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from qualify_technology_families import load_profiles, qualify_technology_families, requested_value, complete_connection_observations


class TechnologyFamilyEvidenceTests(unittest.TestCase):
    def test_invalid_existing_name_cannot_fall_back_to_generated_fixture_name(self):
        for name in ("", " ", None, False, 0, "bad\nname", "bad\uD800name", "x" * 129):
            profile = {"family": "axis", "cpuTypeIdentifier": "cpu", "typeIdentifier": "TO_PositioningAxis",
                       "version": "10.0", "technologyName": name}
            with self.subTest(name=name), tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / "profile.json"
                path.write_text(json.dumps(profile), encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_profiles([path])
                def no_calls(*args):
                    raise RuntimeError("Invalid explicit name reached a native callback")
                with self.assertRaises(ValueError):
                    qualify_technology_families(no_calls, no_calls, [profile], "cpu")

    def run_fixture(self, damage=None, stage="after", created=False):
        profile = {"family": "axis", "cpuTypeIdentifier": "cpu", "typeIdentifier": "TO_PositioningAxis",
                   "version": "10.0", "parameters": {"Scale": 2.0}, "dependencies": {"Mode": 1}}
        if not created:
            profile["technologyName"] = "Axis"
        name = profile.get("technologyName", "TiaMcp_axis_Fixture0")
        project = {"name": "P", "path": "P.ap21"}
        rows = [{"name": n, "value": v, "type": t, "valueAccess": "Read, Write", "writeAccessKnown": True, "writable": True, "readAccessKnown": True, "readable": True, "valueCaptured": True, "valueComplete": True, "valueStatus": "captured"}
                for n, v, t in [("Scale", 1.0, "System.Double"), ("Mode", 1, "System.Int32"), ("Peer", False, "System.Boolean")]]
        state = {"parameters": rows, "exists": not created}
        operations = []
        evidence = []

        def apply(kind, payload, label):
            if kind != "to_create":
                self.assertEqual("P/PLC/technology/" + name, payload["technologyPath"])
            operations.append(kind)
            if kind == "to_delete":
                state["exists"] = False
            else:
                state["exists"] = True
                self.assertEqual(profile["parameters"], payload["parameters"])
                state["parameters"][0]["value"] = 2.0

        reads = {"engineering_read": 0, "technology_read": 0}
        def call(tool, arguments):
            if tool == "technology_read":
                self.assertEqual("P/PLC/technology/" + name, arguments["technologyPath"])
            reads[tool] += 1
            obj = {"name": name, "path": "P/PLC/technology/" + name, "ownerPath": "P/PLC/technology", "composition": "TechnologicalObjects",
                   "number": 42, "typeIdentifier": profile["typeIdentifier"], "version": "10.0",
                   "programmingLanguage": "DB", "parameters": copy.deepcopy(state["parameters"]), "parametersTruncated": False, "parametersComplete": True}
            engineering = {"project": project, "processId": 123, "technologyObjects": [obj] if state["exists"] else [], "truncated": False, "scopeComplete": True}
            native = {"project": project, "processId": 123, "plcPath": "P/PLC", "technologyObject": {"path": "P/PLC/technology/" + name,
                      "ownerPath": "P/PLC/technology", "composition": "TechnologicalObjects",
                      "name": name, "systemLibraryType": profile["typeIdentifier"], "systemLibraryVersion": "10.0"},
                      "parameters": copy.deepcopy(obj["parameters"]), "parameterPrefix": None, "parametersComplete": True,
                      "hardwareConnections": [{"role": "actor", "isConnected": False, "attributes": []}], "warnings": [],
                      "completeness": {"scopeComplete": True, "truncated": False}, "artifact": {"path": "synthetic.xml"}}
            path = obj["path"]
            native["hardwareConnections"] = [
                {"role": "actor", "service": "axis", "observationPath": path + "/hardwareConnections/axis/actor",
                 "status": "observed", "scopeComplete": True, "fieldStatus": {"isConnected": "captured", "connectOption": "captured", "attributes": "captured"},
                 "isConnected": False, "connectOption": "None", "attributes": []},
                {"role": "torque", "service": "axis", "observationPath": path + "/hardwareConnections/axis/torque",
                 "status": "notExposed", "scopeComplete": True, "fieldStatus": {}}]
            native["hardwareConnectionCoverage"] = {"schemaVersion": 1, "technologyPath": path, "complete": False,
                "scopeComplete": True, "truncated": False, "services": [
                    {"service": "axis", "status": "observed", "scopeComplete": True, "rowsCaptured": 2, "sensorCollectionStatus": "observed"},
                    *[{"service": service, "status": "notExposed", "scopeComplete": True, "rowsCaptured": 0}
                      for service in ("encoder", "measuringInput", "outputCam")]]}
            native["hardwareConnectionCoverage"]["referenceCoverage"] = {"providerStatus": "notRequested", "projectPath": None,
                "selectionPlcPath": "P/PLC", "visitedReads": 0, "maximumReads": 50000, "truncated": False,
                "identifierCollisionDetected": False, "persistenceVerified": False, "atomicSnapshot": False}
            phase = "delete" if not state["exists"] else "before" if reads[tool] == 1 else "after"
            if damage and phase == stage:
                damage(engineering, native)
            return copy.deepcopy(engineering if tool == "engineering_read" else native)

        try:
            result = qualify_technology_families(call, apply, [profile], "cpu", evidence)
            return result, operations
        except AssertionError:
            self.assertEqual("failed", evidence[0]["status"])
            if stage != "delete":
                self.assertNotIn("to_delete", operations, "A failed observation must not trigger another mutation")
            if stage == "before" and not created:
                self.assertEqual([], operations)
            raise

    def test_complete_scalar_edit_and_created_fixture_pass(self):
        for created in (False, True):
            rows, operations = self.run_fixture(created=created)
            self.assertEqual("passed", rows[0]["status"])
            self.assertEqual(["to_create", "to_delete"] if created else ["to_parameters"], operations)

    def test_explicit_incomplete_capture_cannot_pass_even_with_matching_values(self):
        for stage in ("before", "after"):
            for view in ("engineering", "native"):
                for field, value in (("valueComplete", False), ("valueCaptured", False), ("readAccessKnown", False),
                                     ("readable", False), ("excluded", True), ("valueStatus", "displayOnly")):
                    def damage(e, n):
                        rows = e["technologyObjects"][0]["parameters"] if view == "engineering" else n["parameters"]
                        rows[0][field] = value
                    with self.subTest(stage=stage, view=view, field=field), self.assertRaises(AssertionError):
                        self.run_fixture(damage, stage=stage)

    def test_complete_capture_flags_cannot_be_missing_or_false(self):
        for owner, field in (("engineering", "scopeComplete"), ("object", "parametersComplete"), ("native", "parametersComplete")):
            for missing in (False, True):
                def damage(e, n):
                    target = e if owner == "engineering" else n if owner == "native" else e["technologyObjects"][0]
                    if missing:
                        target.pop(field)
                    else:
                        target[field] = False
                with self.subTest(owner=owner, missing=missing), self.assertRaises(AssertionError):
                    self.run_fixture(damage, stage="before")

    def test_baseline_paths_must_agree_before_mutation(self):
        damages = [lambda e, n: n["technologyObject"].__setitem__("path", "P/PLC/technology/Peer"),
                   lambda e, n: n.__setitem__("plcPath", "P/PLC2"),
                   lambda e, n: e["technologyObjects"][0].pop("path"),
                   lambda e, n: e["technologyObjects"][0].__setitem__("path", 1)]
        for damage in damages:
            with self.subTest(damage=damage), self.assertRaises(AssertionError):
                self.run_fixture(damage, stage="before")

    def test_baseline_owner_and_composition_must_agree_before_mutation(self):
        for field, value in (("ownerPath", "P/PLC/technology/Other"), ("composition", "MeasuringInputs"),
                             ("ownerPath", None), ("composition", "Unknown")):
            for view in ("engineering", "native"):
                def damage(e, n):
                    row = e["technologyObjects"][0] if view == "engineering" else n["technologyObject"]
                    row[field] = value
                with self.subTest(field=field, value=value, view=view), self.assertRaises(AssertionError):
                    self.run_fixture(damage, stage="before")

    def test_peer_parameter_damage_never_passes(self):
        def mutations(e, n):
            e["technologyObjects"][0]["parameters"][2]["value"] = True
            n["parameters"][2]["value"] = True
        with self.assertRaises(AssertionError):
            self.run_fixture(mutations)

    def test_missing_added_and_duplicate_parameters_never_pass(self):
        for change in (lambda rows: rows.pop(), lambda rows: rows.append({"name": "Injected", "value": 1}),
                       lambda rows: rows.append(copy.deepcopy(rows[0]))):
            with self.subTest(change=change), self.assertRaises(AssertionError):
                self.run_fixture(lambda e, n: (change(e["technologyObjects"][0]["parameters"]), change(n["parameters"])))

    def test_access_and_native_type_drift_never_pass(self):
        for field, value in (("type", "System.Single"), ("writable", False), ("writeAccessKnown", False), ("valueAccess", "Read")):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                self.run_fixture(lambda e, n: e["technologyObjects"][0]["parameters"][0].__setitem__(field, value))

    def test_truncated_or_incomplete_observation_never_passes(self):
        damages = [lambda e, n: e.__setitem__("truncated", True),
                   lambda e, n: e["technologyObjects"][0].__setitem__("parametersTruncated", True),
                   lambda e, n: n["completeness"].__setitem__("scopeComplete", False),
                   lambda e, n: n["completeness"].__setitem__("truncated", True),
                   lambda e, n: n["warnings"].append({"message": "read failed"}),
                   lambda e, n: n["parameters"][0].__setitem__("error", "read failed")]
        for damage in damages:
            with self.subTest(damage=damage), self.assertRaises(AssertionError):
                self.run_fixture(damage)

    def test_native_and_engineering_values_must_agree(self):
        for value in (1.0, True, "2", None):
            with self.subTest(value=value), self.assertRaises(AssertionError):
                self.run_fixture(lambda e, n: n["parameters"][0].__setitem__("value", value))

    def test_identity_and_session_drift_never_pass(self):
        damages = [lambda e, n: e["technologyObjects"][0].__setitem__("version", "9.0"),
                   lambda e, n: e["technologyObjects"][0].__setitem__("name", "Peer"),
                   lambda e, n: n["technologyObject"].__setitem__("path", "P/PLC/technology/Peer"),
                   lambda e, n: n.__setitem__("plcPath", "P/OtherPLC"),
                   lambda e, n: n.__setitem__("processId", 456)]
        for damage in damages:
            with self.subTest(damage=damage), self.assertRaises(AssertionError):
                self.run_fixture(damage)

    def test_connection_drift_requires_explicit_contract(self):
        with self.assertRaises(AssertionError):
            self.run_fixture(lambda e, n: n["hardwareConnections"][0].__setitem__("isConnected", True))

    def test_service_coverage_and_slot_defects_precede_mutation(self):
        defects = [
            lambda n: n.pop("hardwareConnectionCoverage"),
            lambda n: n["hardwareConnectionCoverage"].__setitem__("technologyPath", "foreign"),
            lambda n: n["hardwareConnectionCoverage"].__setitem__("scopeComplete", False),
            lambda n: n["hardwareConnectionCoverage"]["services"].pop(),
            lambda n: n["hardwareConnectionCoverage"]["services"].__setitem__(1, copy.deepcopy(n["hardwareConnectionCoverage"]["services"][0])),
            lambda n: n["hardwareConnectionCoverage"]["services"][0].__setitem__("status", "notVisited"),
            lambda n: n["hardwareConnectionCoverage"]["services"][0].__setitem__("status", "notExposed"),
            lambda n: n["hardwareConnectionCoverage"]["services"][0].__setitem__("rowsCaptured", 0),
            lambda n: n["hardwareConnectionCoverage"]["services"][0].__setitem__("sensorCollectionStatus", "readError"),
            lambda n: n["hardwareConnections"].pop(),
            lambda n: n["hardwareConnections"][0].__setitem__("scopeComplete", False),
            lambda n: n["hardwareConnections"][0].__setitem__("status", "notExposed"),
            lambda n: n["hardwareConnections"][0].__setitem__("observationPath", "foreign"),
            lambda n: n["hardwareConnections"][0].__setitem__("role", "sensor"),
            lambda n: n["hardwareConnections"][0]["fieldStatus"].__setitem__("isConnected", "readError"),
            lambda n: n["hardwareConnections"][0]["fieldStatus"].pop("isConnected"),
            lambda n: n["hardwareConnections"][0].__setitem__("isConnected", 0),
        ]
        for defect in defects:
            with self.subTest(defect=defect), self.assertRaises(AssertionError):
                self.run_fixture(lambda e, n: defect(n), stage="before")

    def test_sensor_positions_and_empty_absence_are_explicit(self):
        path = "P/PLC/technology/Axis"
        coverage = {"schemaVersion": 1, "technologyPath": path, "complete": False, "scopeComplete": True, "truncated": False,
                    "services": [{"service": name, "status": "notExposed", "scopeComplete": True, "rowsCaptured": 0}
                                 for name in ("axis", "encoder", "measuringInput", "outputCam")]}
        coverage["referenceCoverage"] = {"providerStatus": "notRequested", "projectPath": None,
            "selectionPlcPath": "P/PLC", "visitedReads": 0, "maximumReads": 50000, "truncated": False,
            "identifierCollisionDetected": False, "persistenceVerified": False, "atomicSnapshot": False}
        complete_connection_observations([], coverage, path, "P.ap21", "P/PLC")
        coverage["services"][0].update(status="observed", rowsCaptured=4, sensorCollectionStatus="observed")
        rows = [{"role": role, "service": "axis", "observationPath": path + "/hardwareConnections/axis/" + role,
                 "status": "notExposed", "scopeComplete": True, "fieldStatus": {}} for role in ("actor", "torque")]
        rows += [{"role": "sensor", "service": "axis", "observationPath": path + "/hardwareConnections/axis/sensors/" + str(i),
                  "status": "observed", "scopeComplete": True, "fieldStatus": {"isConnected": "captured", "connectOption": "captured", "attributes": "captured"},
                  "isConnected": False, "connectOption": "None", "attributes": []} for i in range(2)]
        complete_connection_observations(rows, coverage, path, "P.ap21", "P/PLC")
        rows[3]["observationPath"] = rows[2]["observationPath"]
        with self.assertRaises(AssertionError): complete_connection_observations(rows, coverage, path, "P.ap21", "P/PLC")
        rows[3]["observationPath"] = path + "/hardwareConnections/axis/sensors/2"
        with self.assertRaises(AssertionError): complete_connection_observations(rows, coverage, path, "P.ap21", "P/PLC")
        rows[3]["observationPath"] = path + "/hardwareConnections/axis/sensors/1"
        cycle = []; cycle.append(cycle); rows[2]["extra"] = cycle
        with self.assertRaisesRegex(AssertionError, "review bound"):
            complete_connection_observations(rows, coverage, path, "P.ap21", "P/PLC")

    def test_incomplete_connection_observations_precede_write(self):
        for marker in ({"valueComplete": False}, {"identityComplete": False}, {"expanded": False}, {"truncated": True}, {"error": "readError"}):
            def damage(e, n): n["hardwareConnections"][0]["channel"] = {"parent": marker}
            with self.subTest(marker=marker), self.assertRaises(AssertionError):
                self.run_fixture(damage, stage="before")
        with self.assertRaises(AssertionError):
            complete_connection_observations([None] * 50001, {}, "owner", "P.ap21", "P/PLC")
        cycle = []; cycle.append(cycle)
        with self.assertRaises(AssertionError):
            complete_connection_observations(cycle, {}, "owner", "P.ap21", "P/PLC")

    def test_failed_before_observation_precedes_parameter_write(self):
        with self.assertRaises(AssertionError):
            self.run_fixture(lambda e, n: n["completeness"].__setitem__("scopeComplete", False), stage="before")

    def test_dependency_boolean_is_not_integer(self):
        def damage(e, n):
            e["technologyObjects"][0]["parameters"][1]["value"] = True
            n["parameters"][1]["value"] = True
        with self.assertRaises(AssertionError):
            self.run_fixture(damage, stage="before")

    def test_unknown_write_access_or_type_precedes_parameter_write(self):
        for field, value in (("writable", None), ("writeAccessKnown", False), ("type", None), ("type", "")):
            with self.subTest(field=field, value=value), self.assertRaises(AssertionError):
                self.run_fixture(lambda e, n: e["technologyObjects"][0]["parameters"][0].__setitem__(field, value), stage="before")

    def test_double_drift_within_old_tolerance_is_refused(self):
        def damage(e, n):
            e["technologyObjects"][0]["parameters"][0]["value"] = 2.0000001
            n["parameters"][0]["value"] = 2.0000001
        with self.assertRaises(AssertionError):
            self.run_fixture(damage)

    def test_numeric_native_rounding_is_explicit_and_not_boolean(self):
        self.assertFalse(requested_value(1.0, True, "System.Double"))
        self.assertFalse(requested_value(0.1, 0.100000000001, "System.Double"))
        self.assertTrue(requested_value(0.1, 0.10000000149011612, "System.Single"))
        self.assertTrue(requested_value(0.1, 0.1, "System.Single"))
        self.assertFalse(requested_value(0.1, 0.1001, "System.Single"))
        with self.assertRaises(AssertionError):
            requested_value(1e100, 1, "System.Single")
        with self.assertRaises(AssertionError):
            requested_value(1.0, float("nan"), "System.Double")

    def test_deletion_absence_requires_complete_unchanged_owner(self):
        for damage in (lambda e, n: e.__setitem__("truncated", True), lambda e, n: e.__setitem__("processId", 456)):
            with self.subTest(damage=damage), self.assertRaises(AssertionError):
                self.run_fixture(damage, stage="delete", created=True)


if __name__ == "__main__":
    unittest.main()
