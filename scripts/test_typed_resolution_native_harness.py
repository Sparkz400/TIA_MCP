"""Mock/offline safety and evidence checks; never starts a native process."""
import copy
import json
import itertools
import pathlib
import tempfile
import types
import unittest
from unittest import mock
from xml.etree import ElementTree as ET

from block_test_runtime import NativeToolError
import qualification_owned_scratch as owned
import qualify_typed_resolution as runner


class FakeWatcher:
    def __init__(self, pid):
        self.evidence = {"processId": pid, "handleCaptured": True}
    def wait_for_exit(self, timeout):
        return {"completed": True, "processExitVerified": True, "handleClosed": True}
    def close(self):
        return True


class FakeClient:
    def __init__(self, root, calls, fault=None):
        self.root, self.calls, self.fault = root, calls, fault
        self.project = None
    def notify(self, *args):
        pass
    def request(self, method, params):
        if method == "initialize":
            return {}
        tool, arguments = params["name"], params["arguments"]
        self.calls.append((tool, arguments))
        if tool == "tia_start":
            if self.fault == "start_timeout":
                raise TimeoutError("Native constructor did not return ownership.")
            value = {"attached": True, "ownsPortalProcess": True, "processId": 71, "projects": []}
        elif tool == "tia_create_scratch":
            self.project = str(self.root / "TiaMcpScratch_fixture" / "TiaMcpScratch_fixture.ap21")
            value = {"completed": True, "compile": {"errorCount": 0}, "project": {"path": self.project}}
        elif tool == "tia_session_status":
            value = {"attached": True, "ownsPortalProcess": True, "processId": 71,
                     "projects": [{"path": str(self.root / "other.ap21") if self.fault == "wrong_project" else self.project}]}
        elif tool == "project_open":
            self.project = arguments["path"]
            value = {"project": {"path": self.project}}
        elif tool == "tia_detach":
            value = {"attached": False, "disposedOwnedPortalConnection": True}
        else:
            value = {"accepted": True}
        return {"structuredContent": value}
    def close(self):
        self.calls.append(("host_close", {}))
        return {"hostExitCode": 0, "forcedHostStop": self.fault == "forced_close",
                "workerExits": [{"processExitVerified": True, "exitCode": 0}]}


class LifetimeTests(unittest.TestCase):
    def session(self, root, calls, fault=None):
        report = {}
        session = owned.OwnedScratch(root / "host.exe", root / "worker.exe", root, report, lambda: None,
            client_factory=lambda *args: FakeClient(root, calls, fault), watcher_factory=FakeWatcher,
            manifest_factory=lambda *args: {"immutable": True})
        return session, report

    def test_reopen_uses_only_owned_paths_and_captures_each_lifetime(self):
        with tempfile.TemporaryDirectory() as directory:
            calls = []
            session, report = self.session(pathlib.Path(directory), calls)
            session.start()
            session.call("tia_create_scratch")
            project = session.current_project
            with self.assertRaises(RuntimeError):
                session.call("project_open", {"path": str(pathlib.Path(directory) / "unrelated.ap21")})
            session.reopen(project)
            session.close()
            self.assertEqual(2, len(report["sessions"]))
            self.assertTrue(all(row["cleanup"]["tiaProcessExit"]["processExitVerified"] for row in report["sessions"]))
            self.assertEqual(1, sum(tool == "project_open" for tool, _ in calls))

    def test_wrong_project_prevents_mutation_dispatch(self):
        with tempfile.TemporaryDirectory() as directory:
            calls = []
            session, _ = self.session(pathlib.Path(directory), calls, "wrong_project")
            session.start()
            session.call("tia_create_scratch")
            with self.assertRaises(RuntimeError):
                session.call("change_apply", {"changeId": "x"})
            self.assertNotIn("change_apply", [tool for tool, _ in calls])
            session.close()

    def test_startup_without_handle_is_recorded_uncertain_and_never_retried(self):
        with tempfile.TemporaryDirectory() as directory:
            calls = []
            session, report = self.session(pathlib.Path(directory), calls, "start_timeout")
            with self.assertRaises(NativeToolError):
                session.start()
            with self.assertRaises(RuntimeError):
                session.close()
            with self.assertRaises(RuntimeError):
                session.start()
            self.assertEqual(1, sum(tool == "tia_start" for tool, _ in calls))
            self.assertFalse(report["sessions"][0]["cleanup"]["tiaProcessExit"]["processExitVerified"])

    def test_forced_host_close_blocks_further_sessions(self):
        with tempfile.TemporaryDirectory() as directory:
            session, report = self.session(pathlib.Path(directory), [], "forced_close")
            session.start()
            with self.assertRaises(RuntimeError):
                session.close()
            with self.assertRaises(RuntimeError):
                session.start()
            self.assertFalse(report["sessions"][0]["cleanup"]["completed"])


class EvidenceTests(unittest.TestCase):
    def test_report_replace_retry_preserves_native_stale_rejection_without_redispatch(self):
        for code in (5, 32, 33):
            with self.subTest(winerror=code), tempfile.TemporaryDirectory() as directory:
                run = runner.ResolutionRun.__new__(runner.ResolutionRun)
                run.output = pathlib.Path(directory)
                run.report = {"calls": []}
                response = {"isError": True, "structuredContent": {"error": {"code": "StaleRevision", "message": "No write applied."}}}
                client = mock.Mock()
                client.request.return_value = response
                audit = owned.QualificationAudit(client, run.report["calls"], run.persist)
                original = pathlib.Path.replace
                attempts, pending_payloads = [], []
                def replace(path, target):
                    self.assertEqual(run.output / "result.json.pending", path)
                    self.assertEqual(run.output / "result.json", target)
                    attempts.append(client.request.call_count)
                    if client.request.call_count:
                        pending_payloads.append(path.read_bytes())
                        if len(pending_payloads) == 1:
                            failure = PermissionError("transient report replacement failure")
                            failure.winerror = code
                            raise failure
                    return original(path, target)
                with mock.patch.object(pathlib.Path, "replace", replace):
                    rejected = runner.expect_rejection(audit, "change_apply", {"changeId": "exact-prepared"}, {"StaleRevision"})
                self.assertEqual("StaleRevision", rejected["error"]["code"])
                self.assertEqual([0, 1, 1], attempts)
                self.assertEqual(1, client.request.call_count)
                self.assertEqual(pending_payloads[0], pending_payloads[1])
                durable = json.loads((run.output / "result.json").read_bytes())
                self.assertEqual("StaleRevision", durable["calls"][0]["result"]["error"]["code"])
                self.assertFalse((run.output / "result.json.pending").exists())

    def test_report_nonretryable_replace_keeps_previous_report_and_pending_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            run = runner.ResolutionRun.__new__(runner.ResolutionRun)
            run.output = pathlib.Path(directory)
            run.report = {"calls": []}
            client = mock.Mock()
            client.request.return_value = {"isError": True, "structuredContent": {"error": {"code": "StaleRevision"}}}
            audit = owned.QualificationAudit(client, run.report["calls"], run.persist)
            original = pathlib.Path.replace
            failures = []
            def replace(path, target):
                if client.request.call_count:
                    failures.append((path, target))
                    failure = OSError("nonretryable report replacement failure")
                    failure.winerror = 87
                    raise failure
                return original(path, target)
            with mock.patch.object(pathlib.Path, "replace", replace), self.assertRaises(OSError):
                audit("change_apply", {"changeId": "exact-prepared"})
            self.assertEqual(1, len(failures))
            self.assertEqual(1, client.request.call_count)
            previous = json.loads((run.output / "result.json").read_bytes())
            pending = json.loads((run.output / "result.json.pending").read_bytes())
            self.assertEqual("started", previous["calls"][0]["status"])
            self.assertEqual("StaleRevision", pending["calls"][0]["result"]["error"]["code"])

    def test_snapshot_keeps_duplicate_names_and_all_object_kinds(self):
        with tempfile.TemporaryDirectory() as directory:
            inventory = {"completeness": {"scopeComplete": True, "truncated": False},
                "blocks": [{"name": "Same", "path": "/root/Same"}, {"name": "Same", "path": "/unit/Same"}],
                "types": [{"name": "Nested", "path": "/types/Nested"}], "tagTables": [{"name": "Tags", "path": "/tags/Tags"}]}
            calls = []
            def call(tool, args):
                calls.append((tool, args))
                if tool == "software_inspect":
                    return inventory
                return {"artifact": {"xml": '<Document><Payload Name="' + next(iter(args.values())) + '" /></Document>'}}
            with mock.patch.object(owned, "artifact_bytes", side_effect=lambda call, artifact: artifact["xml"].encode()):
                first = owned.snapshot(call, pathlib.Path(directory) / "first")
                second = owned.snapshot(call, pathlib.Path(directory) / "second")
            self.assertEqual(4, len(first["objects"]))
            owned.assert_snapshot(first, second)
            for kind in ("block:/unit/Same", "type:/types/Nested", "tagTable:/tags/Tags"):
                corrupt = copy.deepcopy(second)
                corrupt["objects"][kind]["payloadSha256"] = "different"
                with self.assertRaises(RuntimeError):
                    owned.assert_snapshot(first, corrupt)
            self.assertEqual({"block_read", "type_read", "tag_table_read", "software_inspect"}, {tool for tool, _ in calls})

    def test_partial_inventory_is_not_evidence(self):
        for coverage in ({"scopeComplete": False, "truncated": False}, {"scopeComplete": True, "truncated": True}):
            with self.assertRaises(RuntimeError):
                owned.complete_inventory(lambda *args: {"completeness": coverage})

    def test_exact_rejection_does_not_accept_timeout_or_replay(self):
        for code in ("TimeoutOutcomeUnknown", "EngineeringError", "RecoveryRequired"):
            call = mock.Mock(side_effect=NativeToolError("change_apply", {"error": {"code": code}}))
            with self.assertRaises(RuntimeError):
                runner.expect_rejection(call, "change_apply", {}, {"StaleRevision", "StaleDeclaration"})
            self.assertEqual(1, call.call_count)

    def test_namespace_oracle_rejects_decoy_dependencies(self):
        expected = {"operandIdentity": "/caller::5:State5:Child4:Flag", "dataType": "Bool", "typeDependencyPaths": ["/types/B.Outer", "/types/B.Inner"]}
        prepared = {"preview": {"typed": {"resolvedOperands": [{"key": "input", "identity": expected["operandIdentity"], "dataType": "Bool"}]},
                    "declarationDependencies": [{"kind": "type", "path": path} for path in expected["typeDependencyPaths"]]}}
        runner.verify_prepared(prepared, expected)
        prepared["preview"]["declarationDependencies"][-1]["path"] = "/types/A.Inner"
        with self.assertRaises(RuntimeError):
            runner.verify_prepared(prepared, expected)


class FixtureTests(unittest.TestCase):
    def test_namespace_fixture_requires_exact_same_scope_chain_and_parent_namespace_decoy(self):
        scope = "/plc/units/TiaMcp_UnitA"
        rows = [{"name": name, "scopePath": scope, "path": scope + "/types/namespaces/" + namespace + "/" + name}
                for name, namespace in (("Tgp_ResolutionOuter", "TiaMcp_UnitA.Alternate"),
                                        ("Tgp_ResolutionInner", "TiaMcp_UnitA.Alternate"),
                                        ("Tgp_ResolutionInner", "TiaMcp_UnitA"))]
        inventory = {"completeness": {"scopeComplete": True, "truncated": False}, "types": rows}
        result = runner.namespace_type_bindings(inventory, scope)
        self.assertEqual(rows[0]["path"], result["outerType"])
        self.assertEqual(rows[1]["path"], result["innerType"])
        self.assertEqual(rows[2]["path"], result["decoyType"])
        for drift in (lambda value: value["types"].pop(),
                      lambda value: value["types"][1].update(scopePath="/plc/units/TiaMcp_UnitB"),
                      lambda value: value["types"][0].update(path=rows[0]["path"].replace("TiaMcp_UnitA.Alternate", "WrongNamespace")),
                      lambda value: value["types"].append(copy.deepcopy(value["types"][1])),
                      lambda value: value["completeness"].update(truncated=True)):
            corrupt = copy.deepcopy(inventory)
            drift(corrupt)
            with self.assertRaises(RuntimeError):
                runner.namespace_type_bindings(corrupt, scope)

    def test_namespace_sources_and_packet_use_explicit_alternate_dependency_without_overclaim(self):
        nested = (runner.SOURCES / "resolution-nested-types.scl").read_text(encoding="utf-8-sig")
        decoy = (runner.SOURCES / "resolution-decoy-type.scl").read_text(encoding="utf-8-sig")
        self.assertIn('NAMESPACE TiaMcp_UnitA.Alternate\n', nested)
        self.assertIn('Child : "TiaMcp_UnitA.Alternate.Tgp_ResolutionInner";', nested)
        self.assertNotIn('Child : "Tgp_ResolutionInner";', nested)
        self.assertIn('Flag : Bool;', nested)
        self.assertIn('NAMESPACE TiaMcp_UnitA\n', decoy)
        self.assertIn('Flag : Int;', decoy)
        packet = next(case for case in json.loads(runner.MANIFEST.read_bytes())["cases"] if case["id"] == "nested-udt-namespace")
        self.assertIn("fully qualified B.Inner", packet["prerequisites"][0])
        self.assertIn("does not qualify unqualified nested namespace propagation", packet["prerequisites"][0])
        self.assertEqual(["outerType", "innerType"], packet["expect"]["typeDependencyRoles"])

    def test_new_unit_fixtures_omit_only_historical_block_number_and_keep_native_ids(self):
        inventory = {"completeness": {"scopeComplete": True, "truncated": False}, "blocks": [
            {"name": "TiaMcp_IsEnabled", "kind": "FC", "number": 2},
            {"name": "TiaMcp_IsEnabled", "kind": "FC", "number": 3}]}
        for language in ("LAD", "FBD"):
            for reference, name, nested in (("Tgp_TemplateFC_" + language, "Tgp_TemplateFC_" + language, False),
                                            ("Tgp_Unit_" + language, "Tgp_ResolutionNested_" + language, True)):
                xml = (runner.ROOT / "fixtures/typed-graphical/native" / (reference + ".xml")).read_text(encoding="utf-8-sig")
                if nested:
                    xml = runner.nested_caller_fixture(xml, language)
                result = runner.new_autonumber_fixture(xml, name, inventory)
                expected = runner.native_document(xml)
                block = next(node for node in expected if node.tag in {"SW.Blocks.FC", "SW.Blocks.FB"})
                attributes = block.find("AttributeList")
                attributes.remove(attributes.find("Number"))
                self.assertEqual(runner.structural_xml(expected), runner.structural_xml(runner.native_document(result)))
                self.assertEqual("true", next(node for node in ET.fromstring(result) if node.tag.startswith("SW.Blocks.")).findtext("AttributeList/AutoNumber"))
                for invalid_xml, invalid_inventory, expected_name in (
                        (xml.replace("<AutoNumber>true</AutoNumber>", "<AutoNumber>false</AutoNumber>"), inventory, name),
                        (xml, inventory, "WrongName"),
                        (xml, dict(inventory, blocks=inventory["blocks"] + [{"name": name}]), name),
                        (xml, dict(inventory, blocks=inventory["blocks"] + [{"name": name.swapcase()}]), name),
                        (xml, dict(inventory, completeness={"scopeComplete": False, "truncated": False}), name),
                        (xml, dict(inventory, completeness={"scopeComplete": True, "truncated": True}), name)):
                    with self.assertRaises(RuntimeError):
                        runner.new_autonumber_fixture(invalid_xml, expected_name, invalid_inventory)

    def test_boolean_oracle_accepts_retained_native_lad_fbd_and_rejects_wrong_owner(self):
        for language in ("LAD", "FBD"):
            xml = (runner.ROOT / "fixtures/typed-graphical/native" / ("Tgp_TemplateFC_" + language + ".xml")).read_text(encoding="utf-8-sig")
            network = ET.fromstring(xml).find(".//SW.Blocks.CompileUnit").get("ID")
            expected = {"nativeScope": "LocalVariable", "nativeComponents": ["InputBool"], "outputComponents": ["OutputBool"]}
            runner.verify_boolean_native(xml, network, language, expected)
            with self.assertRaises(RuntimeError):
                runner.verify_boolean_native(xml, network, language, dict(expected, nativeScope="GlobalVariable"))
            with self.assertRaises(RuntimeError):
                runner.verify_boolean_native(xml, network, language, dict(expected, nativeComponents=["Tgp_ResolutionOne", "Flag"]))

    def test_short_artifact_policy_is_explicit_bounded_and_rejects_drive_root(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            selected = root / "short"
            policy = runner.artifact_policy(selected, root)
            self.assertEqual(str(root.resolve()), policy["root"])
            self.assertFalse(selected.exists())
            for selected, allowed in ((root.parent / "outside", root), (pathlib.Path("relative"), root), (root, pathlib.Path(root.anchor))):
                with self.assertRaises(RuntimeError):
                    runner.artifact_policy(selected, allowed)

    def test_nested_fixture_uses_retained_native_envelope_both_languages(self):
        for language in ("LAD", "FBD"):
            xml = (runner.ROOT / "fixtures/typed-graphical/native" / ("Tgp_Unit_" + language + ".xml")).read_text(encoding="utf-8-sig")
            changed = runner.nested_caller_fixture(xml, language)
            before, after = ET.fromstring(xml), ET.fromstring(changed)
            self.assertEqual(before.find("SW.Blocks.FB/ObjectList").find("SW.Blocks.CompileUnit").get("ID"), after.find("SW.Blocks.FB/ObjectList").find("SW.Blocks.CompileUnit").get("ID"))
            state = [node for node in after.iter() if node.get("Name") == "State"]
            self.assertEqual(1, len(state))
            self.assertEqual('"TiaMcp_UnitA.Alternate.Tgp_ResolutionOuter"', state[0].get("Datatype"))
            self.assertIn("}", state[0].tag)
            self.assertEqual(ET.tostring(before.find("SW.Blocks.FB/ObjectList")), ET.tostring(after.find("SW.Blocks.FB/ObjectList")))

    def test_tag_fixture_refuses_nonempty_native_template(self):
        empty = '<Document><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Original</Name></AttributeList><ObjectList /></SW.Tags.PlcTagTable></Document>'
        created = runner.tag_fixture(empty)
        tag = ET.fromstring(created).find(".//SW.Tags.PlcTag/AttributeList")
        self.assertEqual("Ready", tag.findtext("Name"))
        self.assertEqual("%M100.0", tag.findtext("LogicalAddress"))
        with self.assertRaises(RuntimeError):
            runner.tag_fixture(created)

    def test_default_table_system_inventory_and_empty_native_export_are_distinct(self):
        # Native attempt 01: correct standard scope, zero tags, 47 system constants.
        table = {"name": "Standard-Variablentabelle", "path": "/plc/tags/Standard-Variablentabelle",
                 "scopePath": "/plc", "tags": [], "constants": [
                     {"name": "Hardware" + str(index), "kind": "system"} for index in range(47)]}
        inventory = {"tagTables": [table]}
        unchanged = copy.deepcopy(inventory)
        self.assertIs(table, runner.empty_tag_template(inventory, "/plc"))
        exported = '<Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Standard-Variablentabelle</Name></AttributeList></SW.Tags.PlcTagTable></Document>'
        authored = ET.fromstring(runner.tag_fixture(exported))
        self.assertEqual("Tgp_ResolutionTags", authored.findtext("SW.Tags.PlcTagTable/AttributeList/Name"))
        self.assertEqual(1, len(list(authored.iter("SW.Tags.PlcTag"))))
        self.assertEqual(unchanged, inventory)
        for field, value in (("tags", [{"name": "UserTag"}]), ("constants", [{"name": "UserConstant", "kind": "user"}]),
                             ("constants", [{"name": "Unknown"}]), ("scopePath", "/other")):
            changed = copy.deepcopy(inventory)
            changed["tagTables"][0][field] = value
            with self.assertRaises(RuntimeError):
                runner.empty_tag_template(changed, "/plc")
        for kind in ("PlcTag", "PlcUserConstant", "PlcSystemConstant"):
            nonempty = exported.replace("</SW.Tags.PlcTagTable>", '<ObjectList><SW.Tags.' + kind + ' ID="1"/></ObjectList></SW.Tags.PlcTagTable>')
            with self.assertRaises(RuntimeError):
                runner.tag_fixture(nonempty)
        with self.assertRaises(RuntimeError):
            runner.empty_tag_template({"tagTables": [table, dict(table, name="Tgp_ResolutionTags", tags=[{"name": "Ready"}])]}, "/plc")

    def test_independent_boolean_oracle_detects_wrong_binding_and_wiring(self):
        expected = {"nativeScope": "GlobalVariable", "nativeComponents": ["Ready"], "outputComponents": ["OutputBool"]}
        xml = '<Document><SW.Blocks.CompileUnit ID="3"><FlgNet><Parts><Access Scope="GlobalVariable" UId="1"><Symbol><Component Name="Ready" /></Symbol></Access><Access Scope="LocalVariable" UId="2"><Symbol><Component Name="OutputBool" /></Symbol></Access><Part Name="Coil" UId="3" /></Parts><Wires><Wire UId="4"><IdentCon UId="1" /><NameCon UId="3" Name="in" /></Wire><Wire UId="5"><IdentCon UId="2" /><NameCon UId="3" Name="operand" /></Wire></Wires></FlgNet></SW.Blocks.CompileUnit></Document>'
        runner.verify_boolean_native(xml, "3", "FBD", expected)
        for corrupt in (xml.replace('Name="Ready"', 'Name="Wrong"'), xml.replace('Name="in"', 'Name="operand"'), xml.replace('<Part Name="Coil" UId="3" />', '<Part Name="Coil" UId="3"><Negated Name="operand" /></Part>')):
            with self.assertRaises(RuntimeError):
                runner.verify_boolean_native(corrupt, "3", "FBD", expected)

    def test_full_caller_outside_selected_network_cannot_drift(self):
        before = '<Document><SW.Blocks.FC ID="0"><AttributeList><Name>KeepMe</Name></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="3"><FlgNet><Parts /></FlgNet><Title>Preserve</Title></SW.Blocks.CompileUnit><SW.Blocks.CompileUnit ID="4"><FlgNet><Parts><Part Name="Opaque" /></Parts></FlgNet></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>'
        changed = before.replace('<Parts />', '<Parts><Part Name="Contact" /></Parts>')
        runner.verify_untouched_caller(before, changed, "3")
        for corrupt in (changed.replace("Preserve", "Lost"), changed.replace("KeepMe", "Renamed"), changed.replace('ID="4"', 'ID="5"'), changed.replace("Opaque", "Dropped")):
            with self.assertRaises(RuntimeError):
                runner.verify_untouched_caller(before, corrupt, "3")


class RestoreNoReplayTests(unittest.TestCase):
    def test_partial_restore_and_lost_response_dispatch_once(self):
        for code in ("RecoveryRequired", "TimeoutOutcomeUnknown"):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                args = types.SimpleNamespace(output=root / "evidence", artifacts=root / "native", phase="standard", language="LAD",
                    host=root / "host.exe", worker=root / "worker.exe", timeout=30, case=["db-first-owner"], artifact_policy_root=root)
                run = runner.ResolutionRun(args)
                calls = []
                class Session:
                    def __init__(self, *args, **kwargs):
                        pass
                    def call(self, tool, arguments):
                        calls.append(tool)
                        if tool == "change_restore":
                            raise NativeToolError(tool, {"error": {"code": code}})
                        if tool == "change_status":
                            return {"state": "recovery_required", "recoveryArchive": {"path": "checkpoint.zap21"}}
                        raise AssertionError(tool)
                    def close(self):
                        calls.append("close")
                def setup():
                    run.pending_mutation = {"changeId": "once", "previewHash": "p"}
                def case(identifier):
                    run.restore_once(run.pending_mutation)
                with mock.patch.object(runner, "OwnedScratch", Session), mock.patch.object(run, "setup", setup), mock.patch.object(run, "run_case", case):
                    self.assertEqual(1, run.run())
                self.assertEqual(["change_restore", "change_status", "close"], calls)
                result = json.loads((args.output / "result.json").read_text())
                self.assertTrue(result["failureRestoreNotReplayed"])
                self.assertEqual(1, len(result["restoreAttempts"]))
                self.assertEqual("blocked", result["status"])


class StaleAddressTests(unittest.TestCase):
    owner = "/plc/tags/Tgp_ResolutionTags"
    xml = '<Document><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Tgp_ResolutionTags</Name></AttributeList><ObjectList><SW.Tags.PlcTag ID="1"><AttributeList><Name>Ready</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%M100.0</LogicalAddress><ExternalWritable>true</ExternalWritable></AttributeList><Comment>Keep me</Comment></SW.Tags.PlcTag><UnknownExtension Mode="keep"/></ObjectList></SW.Tags.PlcTagTable></Document>'

    def state(self, root, name, xml=None):
        folder = root / name
        folder.mkdir()
        table = folder / "table.xml"
        table.write_text(xml or self.xml, encoding="utf-8")
        caller = folder / "caller.xml"
        caller.write_text('<Document><SW.Blocks.FC><AttributeList><Name>Caller</Name></AttributeList></SW.Blocks.FC></Document>')
        return {"inventory": {"plcPath": "/plc", "tagTables": [{"path": self.owner, "scopePath": "/plc", "name": "Tgp_ResolutionTags",
                    "tags": [{"name": "Ready", "logicalAddress": "%M100.1" if "%M100.1" in (xml or self.xml) else "%M100.0"}]}]},
                "objects": {"tagTable:" + self.owner: {"kind": "tagTable", "path": self.owner, "file": str(table), "payloadSha256": owned.native_hash(table.read_text())},
                    "block:/plc/blocks/Caller": {"kind": "block", "path": "/plc/blocks/Caller", "file": str(caller), "payloadSha256": owned.native_hash(caller.read_text())}}}

    def test_deliberate_address_change_compares_complete_target_and_every_peer(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            before = self.state(root, "before")
            changed_xml = self.xml.replace("%M100.0", "%M100.1")
            after = self.state(root, "after", changed_xml)
            runner.verify_tag_address_change(before, after, self.owner, "Ready", "%M100.0", "%M100.1")
            for index, corrupt in enumerate((changed_xml.replace("Keep me", "Lost"), changed_xml.replace('ID="1"', 'ID="2"'),
                    changed_xml.replace("Bool", "Int"), changed_xml.replace('Mode="keep"', 'Mode="lost"'), self.xml)):
                with self.assertRaises(RuntimeError):
                    runner.verify_tag_address_change(before, self.state(root, "corrupt" + str(index), corrupt), self.owner, "Ready", "%M100.0", "%M100.1")
            peer_drift = copy.deepcopy(after)
            peer_drift["objects"]["block:/plc/blocks/Caller"]["payloadSha256"] = "different"
            with self.assertRaises(RuntimeError):
                runner.verify_tag_address_change(before, peer_drift, self.owner, "Ready", "%M100.0", "%M100.1")

    def test_unverified_stale_apply_status_only_and_exact_rejection_restores_once(self):
        for language, code in itertools.product(("LAD", "FBD"), ("TimeoutOutcomeUnknown", "EngineeringError", "RecoveryRequired", "StaleRevision", "StaleDeclaration", "unexpected_success")):
            with self.subTest(language=language, code=code), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                args = types.SimpleNamespace(output=root / "evidence", artifacts=root / "native", phase="stale", language=language,
                    host=root / "host.exe", worker=root / "worker.exe", timeout=30, case=["stale-tag-address"], artifact_policy_root=root)
                run = runner.ResolutionRun(args)
                before = self.state(root, "before")
                changed = self.state(root, "changed", self.xml.replace("%M100.0", "%M100.1"))
                prepared = {"changeId": "stale-original", "previewHash": "p"}
                deliberate = {"changeId": "deliberate-address", "previewHash": "d"}
                calls = []
                class Session:
                    current_project = str(root / "recovered.ap21")
                    def __init__(self, *args, **kwargs):
                        pass
                    def call(self, tool, arguments):
                        calls.append((tool, arguments))
                        if tool == "change_prepare":
                            return prepared
                        if tool == "change_apply":
                            report = json.loads((args.output / "result.json").read_text())
                            attempt = report["cases"][0]["staleApplyAttempt"]
                            self_check = attempt["changeId"] == "stale-original" and attempt["dispatched"] and not attempt["expectedRejectionVerified"]
                            if not self_check:
                                raise AssertionError("Intent was not durable before dispatch")
                            if code == "unexpected_success":
                                return {"state": "applied"}
                            raise NativeToolError(tool, {"error": {"code": code}})
                        if tool == "change_status":
                            return {"state": "applied", "recoveryArchive": {"path": "checkpoint.zap21"}}
                        if tool == "change_restore":
                            return {"state": "restored"}
                        raise AssertionError(tool)
                    def reopen(self, path):
                        calls.append(("reopen", {"path": path}))
                    def close(self):
                        calls.append(("close", {}))
                bound = {"tool": "change_prepare", "arguments": {"payload": {"model": {"operands": [{"ownerPath": self.owner}]}}}, "expect": {}}
                snapshots = [before, changed, changed, before, before]
                with mock.patch.object(runner, "OwnedScratch", Session), mock.patch.object(run, "setup"), \
                        mock.patch.object(run, "request", return_value=bound), mock.patch.object(run, "apply", return_value=(deliberate, {})), \
                        mock.patch.object(runner, "verify_prepared"), mock.patch.object(runner, "snapshot", side_effect=snapshots):
                    outcome = run.run()
                result = json.loads((args.output / "result.json").read_text())
                restores = [arguments for tool, arguments in calls if tool == "change_restore"]
                self.assertEqual(1, sum(tool == "change_apply" for tool, _ in calls))
                if code in {"StaleRevision", "StaleDeclaration"}:
                    self.assertEqual(0, outcome)
                    self.assertEqual([{"changeId": "deliberate-address", "previewHash": "d"}], restores)
                    self.assertTrue(result["cases"][0]["deliberateAddressOnlyXmlVerified"])
                    self.assertTrue(result["cases"][0]["staleApplyAttempt"]["expectedRejectionVerified"])
                else:
                    self.assertEqual(1, outcome)
                    self.assertEqual([], restores)
                    self.assertEqual("stale-original", result["failureRestoreSuppressed"]["changeId"])
                    self.assertEqual([{"changeId": "stale-original"}], [arguments for tool, arguments in calls if tool == "change_status"])
                    self.assertFalse(result["cases"][0]["staleApplyAttempt"]["expectedRejectionVerified"])


class CompetingDbTests(unittest.TestCase):
    def test_competing_db_requires_exact_addition_and_preserves_every_existing_object(self):
        path = "/plc/units/B/blocks/namespaces/TiaMcp_UnitB/Tgp_ResolutionOne"
        xml = '<Document><SW.Blocks.GlobalDB ID="0"><AttributeList><Name>Tgp_ResolutionOne</Name><Namespace>TiaMcp_UnitB</Namespace><Interface><Sections><Section Name="Static"><Member Name="Flag" Datatype="Bool"><StartValue>false</StartValue></Member><Member Name="RetainedMarker" Datatype="Int"><StartValue>91</StartValue></Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>'
        with tempfile.TemporaryDirectory() as directory:
            file = pathlib.Path(directory) / "competing.xml"
            file.write_text(xml, encoding="utf-8")
            before = {"objects": {"block:/original": {"path": "/original", "payloadSha256": "unchanged"},
                "type:/udt": {"path": "/udt", "payloadSha256": "unchanged-type"}}}
            after = {"objects": dict(before["objects"], **{"block:" + path: {"path": path, "kind": "block", "file": str(file)}}),
                "inventory": {"blocks": [{"name": "Tgp_ResolutionOne", "path": path, "scopePath": "/plc/units/B"}]}}
            self.assertEqual(path, runner.verify_competing_db_change(before, after, "/plc/units/B"))
            for content in (xml.replace("91", "0"), xml.replace('Datatype="Bool"', 'Datatype="Int"'),
                            xml.replace("GlobalDB", "InstanceDB"), xml.replace("TiaMcp_UnitB", "TiaMcp_UnitA")):
                file.write_text(content, encoding="utf-8")
                with self.assertRaises(RuntimeError):
                    runner.verify_competing_db_change(before, after, "/plc/units/B")
            file.write_text(xml, encoding="utf-8")
            for drift in (lambda state: state["objects"]["type:/udt"].update(payloadSha256="changed"),
                          lambda state: state["objects"].pop("block:/original"),
                          lambda state: state["objects"].update({"block:/extra": {"path": "/extra"}}),
                          lambda state: state["inventory"]["blocks"][0].update(scopePath="/wrong")):
                corrupt = copy.deepcopy(after)
                drift(corrupt)
                with self.assertRaises(RuntimeError):
                    runner.verify_competing_db_change(before, corrupt, "/plc/units/B")


if __name__ == "__main__":
    unittest.main()
