"""Offline contracts for native interface inspection and generated call wiring."""
import copy
import hashlib
import pathlib
import sys
import unittest

from block_test_program import InterfaceError, generate_program, inspect_interface


def member(name, data_type, children=None):
    return {"name": name, "dataType": data_type, "members": children or []}


def native(kind="FC", inputs=None, outputs=None, inouts=None, state=None, returns="Void", name="UnitUnderTest"):
    sections = [{"name": "Input", "members": inputs or []},
                {"name": "Output", "members": outputs or []},
                {"name": "InOut", "members": inouts or []}]
    sections.append({"name": "Static", "members": state or []} if kind == "FB" else
                    {"name": "Return", "members": [member("Ret_Val", returns)]})
    return {"block": {"name": name, "kind": kind}, "objectKinds": ["SW.Blocks." + kind],
            "sections": sections, "completeness": {"complete": False, "scopeComplete": True, "truncated": False},
            "warnings": [], "artifact": {"sha256": "c" * 64}}


class InterfaceTests(unittest.TestCase):
    def test_native_fc_and_all_named_parameter_directions(self):
        declaration = native(inputs=[member("Enable", "Bool")], outputs=[member("Result", "DInt")],
                             inouts=[member("Value", "Int")], returns="Real")
        interface = inspect_interface(declaration)
        self.assertEqual([(p["name"], p["direction"]) for p in interface["parameters"]],
                         [("Enable", "Input"), ("Result", "Output"), ("Value", "InOut"), ("Ret_Val", "Return")])
        self.assertEqual(interface["nativeSha256"], "c" * 64)
        program = generate_program(interface)
        self.assertIn('"BlockTest_Data".dut.Ret_Val := "UnitUnderTest"(Enable := "BlockTest_Data".dut.Enable, '
                      'Result => "BlockTest_Data".dut.Result, Value := "BlockTest_Data".dut.Value);', program["source"])
        self.assertTrue(program["bindings"]["dut"]["Value"]["writable"])
        self.assertFalse(program["bindings"]["dut"]["Result"]["writable"])
        self.assertFalse(program["bindings"]["dut"]["Ret_Val"]["writable"])

    def test_nested_udt_signed_and_multidimensional_array_leaves(self):
        rows = [member("Samples", "Array[-2..2] of Int"),
                member("Meta", "Struct", [member("Count", "DInt"), member("Label", "String[12]")]),
                member("Grid", "Array[1..2, -1..0] of Real")]
        definition = {"objectKinds": ["SW.Types.PlcStruct"], "sections": [{"name": "None", "members": rows}],
                      "artifact": {"sha256": "a" * 64}}
        program = generate_program(inspect_interface(native(inouts=[member("Item", '"Payload"')]), {"Payload": definition}))
        bindings = program["bindings"]["dut"]
        self.assertEqual(len(bindings), 11)
        self.assertEqual(bindings["Item.Samples[-2]"]["type"], "int16")
        self.assertEqual(bindings["Item.Meta.Label"]["capacity"], 12)
        self.assertEqual(bindings["Item.Grid[2,-1]"]["symbol"], "BlockTest_Data.dut.Item.Grid[2,-1]")
        self.assertEqual(program["typeRevisions"], {"Payload": "a" * 64})
        self.assertIn('Item : "Payload";', program["source"])
        self.assertEqual(program["requiredTypes"], ["Payload"])

    def test_supported_scalar_signal_types_and_time_units(self):
        expected = {"Bool": "bool", "SInt": "int8", "USInt": "uint8", "Int": "int16", "UInt": "uint16",
                    "DInt": "int32", "UDInt": "uint32", "LInt": "int64", "ULInt": "uint64", "Byte": "uint8",
                    "Word": "uint16", "DWord": "uint32", "LWord": "uint64", "Real": "float", "LReal": "double",
                    "Time": "int32", "LTime": "int64", "String[8]": "string", "WString[32]": "wstring"}
        rows = [member("P" + str(i), t) for i, t in enumerate(expected)]
        bindings = generate_program(inspect_interface(native(inputs=rows)))["bindings"]["dut"]
        self.assertEqual([b["type"] for b in bindings.values()], list(expected.values()))
        self.assertEqual(bindings["P15"]["unit"], "ms")
        self.assertEqual(bindings["P16"]["unit"], "ns")

    def test_partial_warning_and_ambiguous_interfaces_fail_closed(self):
        mutations = [lambda n: n["completeness"].update(truncated=True),
                     lambda n: n["completeness"].update(scopeComplete=False),
                     lambda n: n["warnings"].append({"message": "export failed"}),
                     lambda n: n["sections"].append(copy.deepcopy(n["sections"][0])),
                     lambda n: n["sections"].pop(1),
                     lambda n: n.update(objectKinds=["SW.Blocks.FC", "SW.Blocks.FB"]),
                     lambda n: n["block"].update(kind="FB"),
                     lambda n: n["block"].update(isKnowHowProtected=True),
                     lambda n: n["artifact"].update(sha256="not-a-hash")]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                declaration = native()
                mutation(declaration)
                with self.assertRaises(InterfaceError):
                    inspect_interface(declaration)

    def test_unknown_types_bounds_and_member_ambiguity_rejected(self):
        invalid = [member("A", "ANY"), member("A", "Pointer"), member("A", "Array[*] of Int"),
                   member("A", "Array[2..1] of Int"), member("A", "Array[0..4096] of Int"),
                   member("A", "Array[-2147483649..-2147483648] of Int"), member("A", "String[255]"),
                   member("A", "Struct"), member("A", "Int; END_VAR; BEGIN"), member("A\nInjected", "Bool")]
        for row in invalid:
            with self.subTest(row=row), self.assertRaises(InterfaceError):
                inspect_interface(native(inputs=[row]))
        with self.assertRaises(InterfaceError):
            inspect_interface(native(inputs=[member("A", "Bool"), member("a", "Bool")]))
        with self.assertRaises(InterfaceError):
            inspect_interface(native(inputs=[member("State", "Bool")]))
        # Reject compounded expansions without materializing millions of leaves.
        with self.assertRaises(InterfaceError):
            inspect_interface(native(inputs=[member("A", "Array[0..4095] of Array[0..4095] of Int")]))

    def test_missing_conflicting_and_recursive_dependency_definitions(self):
        declaration = native(inputs=[member("Item", '"Payload"')])
        with self.assertRaisesRegex(InterfaceError, "missing native dependency"):
            inspect_interface(declaration)
        with self.assertRaisesRegex(InterfaceError, "Recursive"):
            inspect_interface(declaration, {"Payload": [member("Child", '"Payload"')]})
        declaration["sections"][0]["members"][0]["members"] = [member("B", "Int")]
        with self.assertRaisesRegex(InterfaceError, "disagree"):
            inspect_interface(declaration, {"Payload": [member("A", "Int")]})

    def test_udt_inline_shape_allows_instance_specific_start_metadata(self):
        default_leaf = member("Value", "Int")
        default_leaf["startValueXml"] = "<StartValue>3</StartValue>"
        actual_leaf = member("Value", "Int")
        actual_leaf["startValueXml"] = "<StartValue>9</StartValue>"
        definitions = {"Payload": [member("Nested", "Struct", [default_leaf])]}
        declaration = native(inputs=[member("Item", '"Payload"', [member("Nested", "Struct", [actual_leaf])])])
        program = generate_program(inspect_interface(declaration, definitions))
        self.assertIn("Item.Nested.Value", program["bindings"]["dut"])

    def test_dry_declarations_do_not_claim_native_revision(self):
        declaration = native()
        declaration.pop("artifact")
        program = generate_program(inspect_interface(declaration))
        self.assertIsNone(program["nativeSha256"])
        self.assertEqual(len(program["interfaceRevision"]), 64)
        self.assertEqual(program["qualification"], "generated_not_compiled_or_runtime_qualified")

    def test_native_dependency_fb_capture_keeps_elementary_inout_and_excludes_references_and_temp(self):
        # Optimized FBs store elementary IN_OUT by value in the instance; structured IN_OUT is a reference.
        child = native("FB", name="Child", inputs=[member("Enable", "Bool")], outputs=[member("Count", "Int")],
                       inouts=[member("Accumulator", "Int"), member("Reference", '"Pair"')], state=[member("Memory", "Bool")])
        child["sections"].append({"name": "Temp", "members": [member("Local", "Int")]})
        parent = native("FB", state=[member("First", '"Child"')])
        interface = inspect_interface(parent, {"Child": child})
        self.assertEqual(interface["state"][0]["type"]["kind"], "instance")
        self.assertEqual([m["name"] for m in interface["state"][0]["type"]["members"]], ["Enable", "Count", "Accumulator", "Memory"])
        program = generate_program(interface)
        self.assertEqual(set(program["bindings"]["dut"]),
                         {"state.First.Enable", "state.First.Count", "state.First.Accumulator", "state.First.Memory"})
        self.assertIn("structured (by-reference) IN_OUT", program["stateCaptureLimitations"][0])

    def test_private_members_reserved_tokens_and_void_children_rejected(self):
        private = member("Memory", "Bool")
        private["xmlAttributes"] = {"Accessibility": "Private"}
        with self.assertRaisesRegex(InterfaceError, "not publicly accessible"):
            inspect_interface(native("FB", state=[private]))
        with self.assertRaisesRegex(InterfaceError, "Reserved SCL token"):
            inspect_interface(native(inputs=[member("IF", "Bool")]))
        declaration = native()
        declaration["sections"][-1]["members"][0]["members"] = [member("Unexpected", "Bool")]
        with self.assertRaisesRegex(InterfaceError, "Void return"):
            inspect_interface(declaration)


class ProgramTests(unittest.TestCase):
    def test_independent_native_fb_instances_and_state_snapshots(self):
        interface = inspect_interface(native("FB", inputs=[member("Enable", "Bool")],
                                             outputs=[member("Count", "Int")], state=[member("Previous", "Bool")]))
        program = generate_program(interface, ["left", "right"])
        source = program["source"]
        self.assertEqual(program["instanceIds"], {"left": 0, "right": 1})
        self.assertEqual(program["expectedBlockNames"], ["BlockTest_Data", "BlockTest_left_Instance", "BlockTest_right_Instance", "Main"])
        self.assertIn('"BlockTest_left_Instance"(Enable := "BlockTest_Data".left.Enable, Count => "BlockTest_Data".left.Count);', source)
        self.assertIn('"BlockTest_right_Instance"(Enable := "BlockTest_Data".right.Enable, Count => "BlockTest_Data".right.Count);', source)
        self.assertIn('"BlockTest_Data".right.State.Previous := "BlockTest_right_Instance".Previous;', source)
        self.assertFalse(program["bindings"]["left"]["state.Previous"]["writable"])
        self.assertEqual(program["resetStrategy"], "fresh_simulator_deployment_per_case")

    def test_handshake_ack_follows_call_counter_and_capture(self):
        program = generate_program(inspect_interface(native("FB", state=[member("Memory", "Int")])))
        source = program["source"]
        gate = source.index('IF "BlockTest_Data".RequestId <> "BlockTest_Data".AckId THEN')
        call = source.index('"BlockTest_dut_Instance"();')
        capture = source.index('State.Memory := "BlockTest_dut_Instance".Memory;')
        counter = source.index('CallCount := "BlockTest_Data".CallCount + UDINT#1;')
        ack = source.index('AckId := "BlockTest_Data".RequestId;')
        self.assertTrue(gate < call < capture < counter < ack)
        self.assertEqual(source.count('"BlockTest_dut_Instance"();'), 1)
        self.assertIn('CASE "BlockTest_Data".InstanceIndex OF', source)
        self.assertIn('CallCount = UDINT#4294967295', source)
        self.assertEqual(program["signals"]["ackId"], {"symbol": "BlockTest_Data.AckId", "type": "uint32"})
        self.assertFalse(program["timing"]["scanSynchronized"])
        self.assertEqual(program["timing"]["mode"], "application_call_controlled")

    def test_iec_instances_capture_exported_members_without_copying_native_state(self):
        interface = inspect_interface(native("FB", outputs=[member("Done", "Bool")], state=[
            member("Timer", "TON_TIME", [member("IN", "Bool"), member("Q", "Bool"), member("ET", "Time")]),
            member("Counter", "CTU_SINT")]))
        program = generate_program(interface)
        self.assertIn("Timer : Struct", program["source"])
        self.assertNotIn("Counter : CTU_SINT", program["source"])
        self.assertIn("state.Timer.ET", program["bindings"]["dut"])
        self.assertNotIn("state.Counter", program["bindings"]["dut"])
        self.assertEqual(len(program["stateCaptureLimitations"]), 2)

    def test_array_of_struct_declaration(self):
        interface = inspect_interface(native(inputs=[member("Values", "Array[-1..0] of Struct", [member("A", "Int")])]))
        program = generate_program(interface)
        self.assertIn("Values : Array[-1..0] of Struct\n      A : Int;\n    END_STRUCT;", program["source"])
        self.assertEqual(set(program["bindings"]["dut"]), {"Values[-1].A", "Values[0].A"})

    def test_determinism_source_hash_and_interface_tampering(self):
        interface = inspect_interface(native(inputs=[member("A", "Bool")]))
        first = generate_program(interface)
        self.assertEqual(first, generate_program(interface))
        self.assertEqual(first["revision"], hashlib.sha256(first["source"].encode()).hexdigest())
        self.assertNotEqual(first["revision"], generate_program(interface, prefix="Different")["revision"])
        interface["parameters"][0]["type"]["dataType"] = "Bool; injected"
        with self.assertRaisesRegex(InterfaceError, "changed after native"):
            generate_program(interface)

    def test_invalid_program_names_collisions_and_case_ambiguity(self):
        interface = inspect_interface(native())
        for instances in ([], ["same", "SAME"], ["RequestId"], ['bad"'], "dut"):
            with self.subTest(instances=instances), self.assertRaises(InterfaceError):
                generate_program(interface, instances)
        for prefix in ("", "has space", "x" * 33, "a;bad"):
            with self.subTest(prefix=prefix), self.assertRaises(InterfaceError):
                generate_program(interface, prefix=prefix)
        with self.assertRaises(InterfaceError):
            generate_program(inspect_interface(native(name="Main")))

    def test_catalog_interfaces_and_every_authored_signal_resolve(self):
        sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
        from runtime_catalog import specifications
        for suite in specifications():
            with self.subTest(fixture=suite["fixtureId"]):
                program = generate_program(inspect_interface(suite["interface"], suite.get("typeDefinitions")), suite["instances"])
                for case in suite["cases"]:
                    references = list(case.get("initial", {}))
                    for step in case["steps"]:
                        references.extend(step.get("set", {}))
                        references.extend(assertion["path"] for assertion in step.get("assertions", []))
                    for path in references:
                        instance, leaf = path.split(".", 1)
                        self.assertIn(leaf, program["bindings"][instance], path)


if __name__ == "__main__":
    unittest.main()
