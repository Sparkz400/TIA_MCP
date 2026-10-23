"""Pure conformance harness tests. No Siemens APIs, network or MCP host required."""
import copy
import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

from generate_conformance import ROOT, build_manifest, canonical, generate, load_specifications, validate_manifest
from run_conformance import (NativeFailure, access_location, archive_manifests, assert_expectation, initial_result,
                             load_manifests, pointer, resolve, run_fixture)
from qualify_hardware_families import FAMILIES, candidates, qualify_address_change, qualify_hardware_families
from qualify_unit_routes import fixture_namespace, fixture_table_name, native_equal
from qualify_technology_families import load_profiles, qualify_technology_families
from smoke_safety_direct import edit_user_comment, require_signature_evidence


XML = '''<Document><SW.Blocks.FC><AttributeList><Interface><Member Name="Enable"/></Interface></AttributeList>
<ObjectList><SW.Blocks.CompileUnit ID="7"><AttributeList><NetworkSource><FlgNet xmlns="urn:test">
<Parts><Access UId="12"><Symbol><Component Name="Enable"/></Symbol></Access></Parts>
<Wires><Wire UId="15"><IdentCon UId="12"/><NameCon UId="13" Name="in"/></Wire></Wires>
</FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>'''


def temporary_directory():
    workspace = ROOT.resolve()
    test_root = (workspace / "artifacts/conformance/test-tmp").resolve()
    if not test_root.is_relative_to(workspace):
        raise ValueError("Temporary test directory escaped workspace.")
    test_root.mkdir(parents=True, exist_ok=True)
    return tempfile.TemporaryDirectory(dir=test_root, prefix="fixture_")


class ConformanceTests(unittest.TestCase):
    def test_corpus_count_languages_and_behavior(self):
        specifications = load_specifications("Tcf")
        self.assertEqual(14, len(specifications))
        self.assertEqual({"SCL", "STL", "LAD", "FBD"}, {item["language"] for item in specifications})
        for item in specifications:
            manifest = validate_manifest(build_manifest(item))
            self.assertTrue(manifest["expectedBehavior"])
            self.assertEqual("specified_not_executed", manifest["coverage"]["runtime"])

    def test_generation_is_byte_deterministic_and_variants_are_distinct(self):
        with temporary_directory() as temp:
            first, second, variant = [pathlib.Path(temp) / part for part in ("a", "b", "c")]
            self.assertEqual(generate(first), generate(second))
            for path in first.iterdir():
                self.assertEqual(path.read_bytes(), (second / path.name).read_bytes())
            generate(variant, prefix="Variant2")
            self.assertNotEqual((first / "scl-boolean.scl").read_bytes(), (variant / "scl-boolean.scl").read_bytes())
            self.assertEqual(14, len(load_manifests(first)))

    def test_unicode_bytes_preserved(self):
        with temporary_directory() as temp:
            generate(temp)
            text = (pathlib.Path(temp) / "scl-unicode-data.scl").read_text(encoding="utf-8")
            self.assertIn("Prüfung – 温度 – Δοκιμή", text)
            self.assertIn("WString[32]", text)

    def test_boundary_and_state_vectors_are_explicit(self):
        specs = {item["fixtureId"]: item for item in load_specifications("Tcf")}
        self.assertEqual(8, len(specs["scl-boolean"]["expectedBehavior"]["vectors"]))
        self.assertEqual(-65535, specs["scl-arithmetic"]["expectedBehavior"]["vectors"][1]["outputs"]["Difference"])
        sequence = specs["scl-iec-ctu"]["expectedBehavior"]["vectors"]
        self.assertEqual(sequence[0]["outputs"], sequence[1]["outputs"], "Held high must not count twice")
        self.assertEqual(0, sequence[-1]["outputs"]["CountValue"])

    def test_tampered_manifest_is_rejected(self):
        with temporary_directory() as temp:
            generate(temp)
            path = pathlib.Path(temp) / "scl-boolean.json"
            path.write_text(path.read_text(encoding="utf-8") + " ", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                load_manifests(temp)

    def test_tampered_source_is_rejected(self):
        with temporary_directory() as temp:
            generate(temp)
            (pathlib.Path(temp) / "scl-boolean.scl").write_text("changed")
            with self.assertRaisesRegex(ValueError, "Source integrity"):
                load_manifests(temp)

    def test_archive_retains_exact_specification_and_rejects_replacement(self):
        with temporary_directory() as temp:
            generated = pathlib.Path(temp) / "generated"
            output = pathlib.Path(temp) / "run"
            output.mkdir()
            generate(generated, selected=["scl-boolean"])
            manifests = load_manifests(generated)
            archive_manifests(manifests, output)
            self.assertEqual((generated / "scl-boolean.json").read_bytes(), (output / "scl-boolean.manifest.json").read_bytes())
            archive_manifests(manifests, output)
            (output / "scl-boolean.manifest.json").write_text("altered")
            with self.assertRaisesRegex(ValueError, "Refusing to overwrite"):
                archive_manifests(manifests, output)

    def test_manifest_path_escape_is_rejected(self):
        with temporary_directory() as temp:
            directory = pathlib.Path(temp) / "generated"
            index = generate(directory)
            index["fixtures"][0]["path"] = "../escape.json"
            (directory / "index.json").write_text(canonical(index), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "immediate file"):
                load_manifests(directory)

    def test_tool_allowlist_rejects_physical_or_existing_project_access(self):
        baseline = build_manifest(load_specifications("Tcf")[0])
        for tool in ("tia_attach", "project_open", "simulation_download", "sim_write", "download"):
            modified = copy.deepcopy(baseline)
            modified["steps"][0]["tool"] = tool
            with self.assertRaisesRegex(ValueError, "allowlist"):
                validate_manifest(modified)

    def test_unknown_or_forward_reference_is_rejected(self):
        manifest = build_manifest(load_specifications("Tcf")[0])
        manifest["steps"][0]["arguments"] = {"payload": {"$ref": "program_apply/result"}}
        with self.assertRaisesRegex(ValueError, "earlier step"):
            validate_manifest(manifest)

    def test_unknown_fixture_and_invalid_prefix_are_rejected(self):
        for prefix in ("bad name", "../bad", 'a"b', "x" * 21):
            with self.assertRaises(ValueError):
                load_specifications(prefix)
        with temporary_directory() as temp:
            with self.assertRaisesRegex(ValueError, "Unknown fixtures"):
                generate(temp, selected=["does-not-exist"])

    def test_references_json_pointer_and_idempotency_keys(self):
        self.assertEqual(3, pointer({"a/b": {"~": [3]}}, "/a~1b/~0/0"))
        values = {"p": {"changeId": "known", "payload": {"x": [1]}}}
        result = resolve({"id": {"$ref": "p/changeId"}, "p": {"$ref": "p/payload"}}, values, "run")
        result["p"]["x"].append(2)
        self.assertEqual([1], values["p"]["payload"]["x"])
        self.assertEqual("known", result["id"])
        self.assertEqual(resolve({"$runKey": "s"}, {}, "r"), resolve({"$runKey": "s"}, {}, "r"))
        self.assertNotEqual(resolve({"$runKey": "s"}, {}, "r"), resolve({"$runKey": "s"}, {}, "other"))

    def test_xml_access_discovery_is_namespace_aware_and_unambiguous(self):
        self.assertEqual({"networkId": "7", "accessUid": "12"}, access_location(XML, "Enable"))
        with self.assertRaisesRegex(ValueError, "exactly one"):
            access_location(XML, "Missing")
        duplicated = XML.replace('</Parts>', '<Access UId="99"><Symbol><Component Name="Enable"/></Symbol></Access></Parts>')
        with self.assertRaisesRegex(ValueError, "found 2"):
            access_location(duplicated, "Enable")
        with self.assertRaisesRegex(ValueError, "DTD"):
            access_location('<!DOCTYPE foo [<!ENTITY x "bad">]>' + XML, "Enable")

    def test_graphical_preservation_detects_unrelated_wire_or_interface_changes(self):
        edited = XML.replace('<Component Name="Enable"/>', '<Component Name="EnableAlternate"/>')
        assertion = {"path": "/xml", "op": "operandPreserved", "value": {"baselineStep": "read", "from": "Enable", "to": "EnableAlternate"}}
        results = {"read": {"xml": XML}}
        assert_expectation({"xml": edited}, assertion, results)
        for altered in (edited.replace('Name="in"', 'Name="other"'), edited.replace('Member Name="Enable"', 'Member Name="Changed"')):
            with self.assertRaises(AssertionError):
                assert_expectation({"xml": altered}, assertion, results)

    def test_graphical_preservation_covers_comments_titles_and_unknown_native_elements(self):
        original = XML.replace("<Document>", "<Document><DocumentInfo><Created>old</Created></DocumentInfo>").replace(
            "</SW.Blocks.FC>", '<MultilingualText CompositionName="Comment"><Text>  Prüfung – 温度  </Text></MultilingualText>'
            '<VendorExtension Version="7"><Payload>opaque</Payload></VendorExtension></SW.Blocks.FC>')
        edited = original.replace('<Component Name="Enable"/>', '<Component Name="EnableAlternate"/>').replace("<Created>old</Created>", "<Created>new</Created>")
        assertion = {"path": "/xml", "op": "operandPreserved", "value": {"baselineStep": "read", "from": "Enable", "to": "EnableAlternate"}}
        assert_expectation({"xml": edited}, assertion, {"read": {"xml": original}})
        for altered in (edited.replace("  Prüfung – 温度  ", "Prüfung – 温度"), edited.replace("opaque", "lost"),
                        edited.replace('Version="7"', 'Version="8"'), edited.replace('CompositionName="Comment"', 'CompositionName="Title"')):
            with self.assertRaises(AssertionError):
                assert_expectation({"xml": altered}, assertion, {"read": {"xml": original}})

    def test_unit_fixture_namespace_changes_only_namespace(self):
        original = XML.replace("<Interface>", "<Namespace>Old</Namespace><Interface>")
        changed = fixture_namespace(original, "UnitA.Alternate")
        self.assertTrue(native_equal(original.replace("<Namespace>Old</Namespace>", "<Namespace>UnitA.Alternate</Namespace>"), changed))
        with self.assertRaisesRegex(ValueError, "exactly one"):
            fixture_namespace("<Document/>", "UnitA")

    def test_unit_fixture_serialization_retains_native_namespace_placement(self):
        original = XML.replace('<Interface><Member Name="Enable"/></Interface>',
            '<Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Input">'
            '<Member Name="Enable" Datatype="Bool"/></Section></Sections></Interface>')
        changed = fixture_namespace(original, "UnitA")
        self.assertIn('<Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">', changed)
        self.assertIn('<FlgNet xmlns="urn:test">', changed)
        self.assertNotIn("ns0:", changed)
        self.assertNotIn("ns1:", changed)
        table = '<Document><SW.Tags.PlcTagTable><AttributeList><Name>Old</Name></AttributeList><ObjectList>' \
            '<Vendor xmlns="urn:retained"><Payload>  opaque  </Payload></Vendor></ObjectList></SW.Tags.PlcTagTable></Document>'
        edited = fixture_table_name(table, "TiaMcp_UnitTagsFixture")
        self.assertIn('<Vendor xmlns="urn:retained">', edited)
        self.assertNotIn("ns0:", edited)
        self.assertTrue(native_equal(table.replace("<Name>Old</Name>", "<Name>TiaMcp_UnitTagsFixture</Name>"), edited))

    def test_unit_fixture_omits_root_only_optional_memory_layout(self):
        original = XML.replace("<Interface>", "<MemoryLayout>Optimized</MemoryLayout><Interface>")
        root = fixture_namespace(original, "")
        self.assertIn("<MemoryLayout>Optimized</MemoryLayout>", root)
        unit = fixture_namespace(original, "UnitA")
        self.assertNotIn("<MemoryLayout>", unit)
        expected = original.replace("<MemoryLayout>Optimized</MemoryLayout>", "").replace(
            "</AttributeList>", "<Namespace>UnitA</Namespace></AttributeList>", 1)
        # AttributeList element order follows the source, with the new namespace
        # added at its end. Apart from this explicit unit adaptation the native
        # interface and networks must retain their content.
        self.assertIn('<Member Name="Enable"/>', unit)
        self.assertIn('<FlgNet xmlns="urn:test">', unit)
        self.assertNotIn("ns0:", unit)
        self.assertTrue(native_equal(expected, unit))

    def test_missing_hardware_families_are_blocked_without_mutation(self):
        calls = []
        def call(tool, arguments):
            calls.append((tool, arguments))
            if tool == "project_inventory":
                return {"hardware": [{"positionNumber": 0, "path": "/devices/CPU/items/Rail"}]}
            return {"entries": []}
        rows = qualify_hardware_families(call, lambda *args: self.fail("No missing module may be applied"), list(FAMILIES))
        self.assertEqual(["DI", "DO", "AI", "AO"], [row["family"] for row in rows])
        self.assertTrue(all(row["status"] == "blocked" and row["catalogCandidates"] == [] for row in rows))
        self.assertEqual(4, sum(tool == "hardware_catalog_search" for tool, _ in calls))

    def test_hardware_selection_keeps_native_identifier_and_excludes_other_families(self):
        entries = [{"articleNumber": "6ES7 521-EXAMPLE", "typeIdentifier": "native:DI"},
                   {"articleNumber": "6ES7 522-EXAMPLE", "typeIdentifier": "native:DO"},
                   {"articleNumber": "6ES7 521-without-identifier"}]
        self.assertEqual([entries[0]], candidates({"entries": entries}, "DI"))
        self.assertEqual([entries[1]], candidates({"entries": entries}, "DO"))

    def test_unexpected_hardware_prepare_error_is_not_treated_as_unavailable(self):
        def call(tool, arguments, *args, **kwargs):
            if tool == "project_inventory":
                return {"hardware": [{"positionNumber": 0, "path": "/devices/CPU/items/Rail"}]}
            if tool == "hardware_catalog_search":
                return {"entries": [{"articleNumber": "6ES7 521-example", "typeIdentifier": "native:DI"}]}
            return {"error": {"code": "ProjectMismatch"}}
        with self.assertRaisesRegex(RuntimeError, "ProjectMismatch"):
            qualify_hardware_families(call, lambda *args: self.fail("No failed prepare may be applied"), ["DI"])

    def test_hardware_address_fixture_rejects_overlap_and_restores_original_range(self):
        rail = "/devices/PLC/items/Rail"
        module = {"name": "Fixture", "path": rail + "/items/Fixture"}
        address = {"ioType": "Input", "startAddress": 0, "length": 16}
        state = {module["path"]: {**module, "addresses": [copy.deepcopy(address)]}}
        operations = []
        def call(tool, arguments, *args, **kwargs):
            if tool == "hardware_inspect":
                self.assertLessEqual(arguments["maxItems"], 2000)
                items = [copy.deepcopy(row) for path, row in state.items() if not arguments.get("hardwarePath") or path == arguments["hardwarePath"]]
                return {"items": items, "completeness": {"truncated": False}}
            self.assertEqual("change_prepare", tool)
            self.assertEqual(2, arguments["payload"]["startAddress"], "Negative fixture must target the peer's actual address")
            return {"error": {"code": "AddressConflict"}}
        def apply(kind, payload, label):
            operations.append((kind, copy.deepcopy(payload)))
            if kind == "hardware_plug":
                path = rail + "/items/" + payload["name"]
                state[path] = {"path": path, "name": payload["name"], "addresses": [{**address, "startAddress": 2}]}
            elif kind == "hardware_address_set":
                actual = state[payload["hardwarePath"]]["addresses"][0]
                self.assertEqual(actual["startAddress"], payload["oldStartAddress"])
                self.assertNotEqual(2, payload["startAddress"], "Rejected overlap must never be applied")
                actual["startAddress"] = payload["startAddress"]
            elif kind == "hardware_delete":
                del state[payload["hardwarePath"]]
            else:
                self.fail(kind)
        result = qualify_address_change(call, apply, module, [{"items": list(copy.deepcopy(state).values())}], {"typeIdentifier": "exact-native-id"}, rail, "DI")
        self.assertEqual("passed", result["status"])
        self.assertEqual([address], state[module["path"]]["addresses"])
        self.assertEqual(1, len(state), "Peer module must be removed")
        self.assertEqual(["hardware_plug", "hardware_address_set", "hardware_address_set", "hardware_delete"], [kind for kind, _ in operations])

    def test_safety_comment_fixture_preserves_native_payload_and_requires_real_signatures(self):
        original = ('<Document><SW.Blocks.FB><AttributeList><ProgrammingLanguage>F_LAD</ProgrammingLanguage></AttributeList>'
                    '<ObjectList><MultilingualText CompositionName="Comment"><ObjectList><MultilingualTextItem><AttributeList>'
                    '<Culture>de-DE</Culture><Text>existing</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>'
                    '<UnknownNativeAttribute Value="untouched"/></ObjectList></SW.Blocks.FB></Document>')
        changed = edit_user_comment(original)
        self.assertTrue(native_equal(original.replace("<Text>existing</Text>", "<Text>existing [TIA MCP offline preservation fixture]</Text>"), changed))
        with self.assertRaisesRegex(ValueError, "native user"):
            edit_user_comment(original.replace("F_LAD", "LAD"))
        for snapshot in ({}, {"programSignatures": [{}], "blockSignatures": []}):
            with self.assertRaisesRegex(AssertionError, "signatures"):
                require_signature_evidence(snapshot)
        require_signature_evidence({"programSignatures": [{"type": name, "value": 1} for name in
            ("CollectiveOfflineSignature", "SoftwareOfflineSignature", "HardwareOfflineSignature", "CommunicationAddressOfflineSignature")],
            "blockSignatures": [{"type": "BlockOfflineSignature", "value": 2}],
            "completeness": {"scopeComplete": True, "truncated": False}, "warnings": [], "blockSignatureServiceAvailable": True})

    def test_technology_profiles_require_explicit_native_identity_and_matching_cpu(self):
        profile = {"family": "axis", "cpuTypeIdentifier": "exact-native-cpu", "typeIdentifier": "TO_PositioningAxis", "version": "9.0",
                   "parameters": {"Config.Example": 1.0}, "dependencies": {"Config.SelectedMode": 0}}
        with temporary_directory() as temp:
            path = pathlib.Path(temp) / "profile.json"
            path.write_text(json.dumps(profile), encoding="utf-8")
            self.assertEqual([profile], load_profiles([path]))
            for change in ({"version": "latest"}, {"typeIdentifier": "PID_Compact"}, {"family": "unsupported"}, {"parameters": {"x": {"nested": 1}}}):
                path.write_text(json.dumps({**profile, **change}), encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_profiles([path])
        def no_calls(*args, **kwargs):
            self.fail("Missing or mismatched profiles must not access or mutate native objects")
        missing = qualify_technology_families(no_calls, no_calls, [], "exact-native-cpu")
        self.assertEqual(["axis", "encoder"], [row["family"] for row in missing])
        self.assertTrue(all(row["status"] == "blocked" for row in missing))
        mismatch = qualify_technology_families(no_calls, no_calls, [profile], "different-native-cpu")
        self.assertTrue(all(row["status"] == "blocked" for row in mismatch))

    @staticmethod
    def technology_observations(profile, name, parameters, exists=True):
        project = {"name": "P", "path": "P.ap21"}
        parameters = [{**p, "readAccessKnown": True, "readable": True, "valueCaptured": True,
                       "valueComplete": True, "valueStatus": "captured"} for p in parameters]
        values = copy.deepcopy(parameters)
        engineering = {"project": project, "processId": 1, "truncated": False, "scopeComplete": True, "technologyObjects": [
            {"name": name, "path": "P/PLC/technology/" + name, "typeIdentifier": profile["typeIdentifier"], "version": profile["version"],
             "ownerPath": "P/PLC/technology", "composition": "TechnologicalObjects",
             "parametersTruncated": False, "parametersComplete": True, "parameters": parameters}] if exists else []}
        native = {"project": project, "processId": 1, "plcPath": "P/PLC", "technologyObject": {"name": name,
                  "ownerPath": "P/PLC/technology", "composition": "TechnologicalObjects",
                  "path": "P/PLC/technology/" + name, "systemLibraryType": profile["typeIdentifier"], "systemLibraryVersion": profile["version"]},
                  "parameters": values, "parametersComplete": True, "hardwareConnections": [], "warnings": [], "parameterPrefix": None,
                  "completeness": {"scopeComplete": True, "truncated": False}}
        native["hardwareConnectionCoverage"] = {"schemaVersion": 1, "technologyPath": "P/PLC/technology/" + name,
            "complete": False, "scopeComplete": True, "truncated": False, "services": [
                {"service": service, "status": "notExposed", "scopeComplete": True, "rowsCaptured": 0}
                for service in ("axis", "encoder", "measuringInput", "outputCam")]}
        native["hardwareConnectionCoverage"]["referenceCoverage"] = {"providerStatus": "notRequested", "projectPath": None,
            "selectionPlcPath": "P/PLC", "visitedReads": 0, "maximumReads": 50000, "truncated": False,
            "identifierCollisionDetected": False, "persistenceVerified": False, "atomicSnapshot": False}
        return engineering, native

    def test_technology_existing_profile_checks_dependencies_before_mutation(self):
        profile = {"family": "encoder", "cpuTypeIdentifier": "cpu", "typeIdentifier": "TO_ExternalEncoder", "version": "9.0",
                   "technologyName": "Existing", "dependencies": {"Mode": 1}, "parameters": {"Scale": 2.0}}
        def call(tool, arguments):
            observations = self.technology_observations(profile, "Existing", [
                {"name": "Mode", "value": 0, "writable": False}, {"name": "Scale", "value": 1.0, "writable": True}])
            return observations[0 if tool == "engineering_read" else 1]
        with self.assertRaisesRegex(AssertionError, "dependency"):
            qualify_technology_families(call, lambda *args: self.fail("Dependency mismatch must block writes"), [profile], "cpu")

    def test_technology_creation_initializes_parameters_before_first_compile(self):
        profile = {"family": "axis", "cpuTypeIdentifier": "cpu", "typeIdentifier": "TO_PositioningAxis", "version": "10.0",
                   "dependencies": {"Mode": 1}, "parameters": {"Mode": 1, "Scale": 2.0}}
        state = {"exists": False, "parameters": {}}
        operations = []
        def apply(kind, payload, label):
            operations.append(kind)
            if kind == "to_create":
                self.assertEqual(profile["parameters"], payload["parameters"])
                state["parameters"] = dict(payload["parameters"])
                state["exists"] = True
            elif kind == "to_delete":
                state["exists"] = False
            else:
                self.fail("New TO parameters must be applied in the creation checkpoint before first compile")
        def call(tool, arguments):
            observations = self.technology_observations(profile, "TiaMcp_axis_Fixture0", [
                {"name": name, "value": value, "writable": True, "writeAccessKnown": True, "type": "System.Double" if isinstance(value, float) else "System.Int32"}
                for name, value in state["parameters"].items()], state["exists"])
            return observations[0 if tool == "engineering_read" else 1]
        rows = qualify_technology_families(call, apply, [profile], "cpu")
        self.assertEqual(["to_create", "to_delete"], operations)
        self.assertEqual("passed", rows[0]["status"])
        self.assertEqual("after-initialized-creation-and-compile", rows[0]["nativeBaselineStage"])
        self.assertEqual(profile["parameters"], rows[0]["initialParameters"])

    def test_call_edge_requires_correct_pair_and_exact_terminal_name(self):
        assertion = {"path": "/edges", "op": "callEdge", "value": {"caller": "Parent", "callee": "Child"}}
        assert_expectation({"edges": [{"callerPath": "/blocks/Parent", "calleePath": "/blocks/Child"}]}, assertion, {})
        with self.assertRaises(AssertionError):
            assert_expectation({"edges": [{"callerPath": "/blocks/Parent", "calleePath": "/blocks/ChildExtra"}]}, assertion, {})

    def test_boolean_does_not_satisfy_compile_error_count(self):
        with self.assertRaises(AssertionError):
            assert_expectation({"errorCount": False}, {"path": "/errorCount", "op": "equals", "value": 0}, {})

    def test_member_assertion_respects_hierarchy_and_type(self):
        data = {"sections": [{"members": [{"name": "Meta", "dataType": "Struct", "members": [
            {"name": "CountValue", "dataType": "DInt", "members": []}]}]}]}
        assertion = {"path": "/sections", "op": "member", "value": {"path": ["Meta", "CountValue"], "dataType": "DInt"}}
        assert_expectation(data, assertion, {})
        for path, data_type in ((["CountValue"], "DInt"), (["Meta", "CountValue"], "Int")):
            with self.assertRaises(AssertionError):
                assert_expectation(data, {"path": "/sections", "op": "member", "value": {"path": path, "dataType": data_type}}, {})

    def test_native_failure_is_preserved_and_recovery_is_not_a_retry(self):
        manifest = build_manifest(load_specifications("Tcf")[0])
        report = initial_result(manifest, "a" * 64, "run1", "engineering", True)
        calls = []
        native_error = {"error": {"code": "CompileFailed", "message": "Concrete native compile diagnostic"}}
        def call(client, tool, arguments):
            calls.append(tool)
            if tool == "change_prepare":
                return {"changeId": "change1", "previewHash": "hash1"}
            if tool == "change_apply":
                raise NativeFailure(tool, native_error)
            if tool == "change_status":
                return {"recoveryArchive": {"path": "checkpoint.zap21"}}
            if tool == "change_restore":
                return {"recoveredProject": {"path": "owned-copy.ap21"}}
            raise AssertionError(tool)
        with patch("run_conformance.native_call", side_effect=call):
            run_fixture(object(), manifest, report, lambda: None)
        self.assertEqual("failed", report["status"])
        self.assertEqual(["change_prepare", "change_apply", "change_status", "change_restore"], calls)
        self.assertEqual(native_error, report["steps"][1]["nativeFailure"])
        self.assertEqual("passed", report["steps"][-1]["status"])

    def test_recovery_failure_does_not_replace_primary_failure(self):
        manifest = build_manifest(load_specifications("Tcf")[0])
        report = initial_result(manifest, "a" * 64, "run2", "engineering", True)
        def call(client, tool, arguments):
            if tool == "change_prepare":
                return {"changeId": "change1", "previewHash": "hash1"}
            raise RuntimeError("primary" if tool == "change_apply" else "recovery")
        with patch("run_conformance.native_call", side_effect=call):
            run_fixture(object(), manifest, report, lambda: None)
        self.assertEqual("primary", report["steps"][1]["error"])
        self.assertEqual("recovery", report["steps"][-1]["error"])
        self.assertEqual("failed", report["status"])

    def test_dry_run_never_starts_host_or_claims_qualification(self):
        import run_conformance
        with temporary_directory() as temp:
            directory = pathlib.Path(temp)
            generated, reports = directory / "generated", directory / "reports"
            generate(generated)
            with patch("sys.argv", ["run_conformance.py", "--generated", str(generated), "--report-directory", str(reports)]), \
                 patch("run_conformance.Client", side_effect=AssertionError("Host must not start")):
                self.assertEqual(0, run_conformance.main())
            result = json.loads((reports / "scl-boolean.result.json").read_text(encoding="utf-8"))
            self.assertEqual("skipped", result["status"])
            self.assertTrue(any("not been executed" in item for item in result["limitations"]))

    def test_runtime_request_is_blocked_not_passed_or_skipped(self):
        import run_conformance
        with temporary_directory() as temp:
            directory = pathlib.Path(temp)
            generate(directory / "generated", selected=["scl-boolean"])
            with patch("sys.argv", ["run_conformance.py", "--live", "--phase", "runtime", "--generated", str(directory / "generated"), "--report-directory", str(directory / "reports")]), \
                 patch("run_conformance.Client", side_effect=AssertionError("Unimplemented runtime must not start a host")):
                self.assertEqual(2, run_conformance.main())
            result = json.loads((directory / "reports/scl-boolean.result.json").read_text(encoding="utf-8"))
            self.assertEqual("blocked", result["status"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
