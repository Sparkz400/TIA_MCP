"""File-only admission, preservation and no-dispatch tests."""
import copy
import io
import json
from pathlib import Path
import tempfile
import unittest
from contextlib import ExitStack
from types import SimpleNamespace
from unittest.mock import Mock, patch

import qualify_vci_preservation_probe as probe
import retained_native_lifecycle as life
from test_retained_native_lifecycle import RetainedTests, Watcher


class ProbeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()

    def tearDown(self): self.temp.cleanup()

    def artifact(self, name, data):
        path = self.root / name; path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return probe.ref(path)

    def fixture(self):
        value = {"plcName": "ScratchPLC", "languages": {"active": ["de-DE"], "reference": "de-DE", "editing": "de-DE"},
                 "tags": [], "types": [], "watchForceTables": [], "watchGroups": ["root"], "watchForceAccess": [], "blocks": []}
        for block in probe.INPUT["blocks"]:
            value["blocks"].append({"kind": "block", "group": "root", "name": block["name"], "number": 1, "comments": [{"culture": "de-DE", "text": "Original"}],
                "nativeXml": ("SW.Blocks." + block["kind"], (("ID", "0"),), None, (
                    (("AttributeList", (), None, ((("Code", (), "UNCHANGED", ()), None),)), None),
                    (("ObjectList", (), None, ((("MultilingualText", (("CompositionName", "Comment"),), "Original", ()), None),)), None)))})
        return value

    def seeded(self):
        before = self.fixture(); after = copy.deepcopy(before)
        after["languages"]["active"].append("en-GB")
        for block, expected in zip(after["blocks"], probe.INPUT["blocks"]): block["comments"] = expected["comments"]
        return before, after

    def test_default_plan_cannot_spawn(self):
        args = ["probe", "--root", str(self.root / "run"), "--collector", "missing.exe", "--collector-manifest", "missing.json",
                "--fixture-helper", "missing.exe", "--fixture-manifest", "missing.json", "--recovery-gate", "not-completed.json"]
        with patch.object(probe.sys, "argv", args), patch.object(probe, "Client", side_effect=AssertionError("Native dispatch")), patch.object(probe.subprocess, "Popen", side_effect=AssertionError("Native dispatch")), patch("sys.stdout", new_callable=io.StringIO) as out:
            self.assertEqual(probe.main(), 0)
            self.assertFalse(json.loads(out.getvalue())["nativeInvoked"])
        self.assertFalse((self.root / "run").exists())

    def test_exact_filetime_roundtrip(self):
        for ticks in (0, 134049455820000000, 134049455821234567, 134049455821234560):
            self.assertEqual(probe.utc_ticks(probe.filetime_iso(ticks)), ticks)
        self.assertEqual(probe.utc_ticks("2026-10-15T10:00:00.1234560Z"), probe.utc_ticks("2026-10-15T10:00:00.123456Z"))
        with self.assertRaises(ValueError): probe.utc_ticks("2026-10-15T10:00:00+02:00")

    def test_seed_exact_only(self):
        self.assertTrue(probe.verify_seed(*self.seeded()))

    def test_seed_rejects_code_edit(self):
        before, after = self.seeded(); row = after["blocks"][0]
        row["nativeXml"] = ("SW.Blocks.FC", (), "changed code", ())
        with self.assertRaisesRegex(ValueError, "beyond"): probe.verify_seed(before, after)

    def test_seed_rejects_extra_culture(self):
        before, after = self.seeded(); after["languages"]["active"].append("nl-NL")
        with self.assertRaisesRegex(ValueError, "Only English"): probe.verify_seed(before, after)

    def test_seed_rejects_invented_readback(self):
        before, after = self.seeded(); after["blocks"][0]["comments"] = [{"culture": "en-GB", "text": "translated"}]
        with self.assertRaisesRegex(ValueError, "readback"): probe.verify_seed(before, after)

    def test_seed_rejects_changed_force_peer(self):
        before, after = self.seeded(); after["watchForceTables"].append({"name": "force"})
        with self.assertRaisesRegex(ValueError, "peer"): probe.verify_seed(before, after)

    def test_inspection_relocation_preserves_other_data(self):
        packet = {"project": {"path": "C:/original.ap21", "name": "Scratch"}, "observedAtUtc": "now", "processId": 1, "origin": "native",
                  "warnings": [], "completeness": {"truncated": False, "scopeComplete": True}, "blocks": [{"identity": {"projectPath": "C:/original.ap21"}, "address": 17}]}
        other = copy.deepcopy(packet); other["project"]["path"] = "C:/restored.ap21"; other["blocks"][0]["identity"]["projectPath"] = "C:/restored.ap21"
        self.assertEqual(probe.inspection_payload(packet), probe.inspection_payload(other))
        other["blocks"][0]["address"] = 18
        self.assertNotEqual(probe.inspection_payload(packet), probe.inspection_payload(other))
        other["blocks"][0]["identity"]["projectPath"] = "C:/foreign.ap21"
        with self.assertRaisesRegex(ValueError, "Foreign"): probe.inspection_payload(other)

    def test_inspection_rejects_incomplete(self):
        for coverage in ({"truncated": False}, {"truncated": True, "scopeComplete": True}, {"truncated": False, "scopeComplete": False}):
            with self.assertRaisesRegex(ValueError, "incomplete"): probe.inspection_payload({"completeness": coverage})

    def test_xml_retains_engineering_and_rejects_tamper(self):
        row = self.artifact("block.xml", b'<Document><Engineering version="21"/><DocumentInfo><Created>now</Created></DocumentInfo><SW.Blocks.FC ID="0"><AttributeList><Name>X</Name></AttributeList></SW.Blocks.FC></Document>')
        self.assertEqual(probe.native_xml(row).tag, "SW.Blocks.FC")
        Path(row["path"]).write_text("changed")
        with self.assertRaisesRegex(ValueError, "differs"): probe.native_xml(row)

    def test_xml_rejects_entities(self):
        row = self.artifact("danger.xml", b'<!DOCTYPE Document [<!ENTITY x "x">]><Document><SW.Blocks.FC/></Document>')
        with self.assertRaisesRegex(ValueError, "entities"): probe.native_xml(row)

    def overlay(self, completed=False):
        project = self.root / "scratch.ap21"
        folder = self.root / "isolated-vci-reservations"; folder.mkdir()
        path = folder / "seed-intent.json"
        resources = [{"kind": "project", "path": str(project), "effect": "seed"}]
        keys = [probe.resource_key("project", project)]
        probe.save(path, {"kind": "isolated-vci-resource-reservation", "productionMutationAdmission": False, "automaticReplay": False, "resources": resources, "resourceKeys": keys})
        if completed: probe.save(folder / "seed-outcome.json", {"intentSha256": probe.sha(path), "state": "returned", "nativeHelperCompleted": True})
        return path, keys

    def test_overlay_rejects_unresolved_exact_and_parent(self):
        path, keys = self.overlay()
        for scope in (keys, [probe.resource_key("workspace", self.root)]):
            with self.assertRaisesRegex(ValueError, "Unresolved"): probe.scan_overlays([self.root], scope)
        self.assertEqual(len(probe.scan_overlays([self.root], [probe.resource_key("project", self.root / "other.ap21")])), 1)

    def test_overlay_accepts_only_known_success(self):
        path, keys = self.overlay(True)
        with self.assertRaisesRegex(ValueError, "Unresolved"): probe.scan_overlays([self.root], keys)
        (path.parent / "seed-outcome.json").write_text(json.dumps({"intentSha256": probe.sha(path), "state": "outcomeUnknown", "nativeHelperCompleted": True}))
        with self.assertRaisesRegex(ValueError, "Unresolved"): probe.scan_overlays([self.root], keys)

    def test_overlay_rejects_tampered_intent(self):
        path, keys = self.overlay(True)
        path.write_text(path.read_text() + " ")
        with self.assertRaisesRegex(ValueError, "binding"): probe.scan_overlays([self.root], keys)

    def test_manifest_rejects_unbound_dependency(self):
        exe = self.artifact("bin/helper.exe", b"reviewed executable")
        sources = [self.artifact("src/Program.cs", b"source"), self.artifact("src/Helper.csproj", b"project")]
        manifest = self.root / "manifest.json"; probe.save(manifest, {"files": [exe, *sources]})
        probe.freeze_binding(exe["path"], manifest, ["Program.cs", "Helper.csproj"])
        self.artifact("bin/unreviewed.dll", b"not admitted")
        with self.assertRaisesRegex(ValueError, "not frozen"): probe.freeze_binding(exe["path"], manifest, ["Program.cs", "Helper.csproj"])

    def test_inventory_detects_unexported_system_block(self):
        native = self.fixture(); host = {"software": {"blocks": [{"name": b["name"]} for b in native["blocks"]], "types": [], "tagTables": []}}
        self.assertTrue(probe.verify_inventory_coverage(native, host))
        host["software"]["blocks"].append({"name": "HiddenSystemBlock"})
        with self.assertRaisesRegex(ValueError, "inventory differ"): probe.verify_inventory_coverage(native, host)

    def test_seed_projection_allows_only_empty_english_and_document_ids(self):
        def item(culture, text, number):
            return ('MultilingualTextItem', (('CompositionName','Items'),('ID',str(number))), None,
                    ((('AttributeList', (), None, ((('Culture', (), culture, ()), None), (('Text', (), text, ()), None))), None),))
        def doc(english=None, uid='19', composition='Title'):
            children = [(item('de-DE','Keep',2),None)]
            if english is not False: children.append((item('en-GB',english,3),None))
            mult = ('MultilingualText',(('CompositionName',composition),('ID','1')),None,((('ObjectList',(),None,tuple(children)),None),))
            return ('SW.Blocks.FC',(('ID','0'),),None,((('ObjectList',(),None,((mult,None),(('SW.Blocks.CompileUnit',(('ID','3' if english is False else '4'),('UId',uid)),None,()),None))),None),))
        old = doc(False); new = doc()
        self.assertEqual(probe.seed_projection(old, english_activation=True)[0], probe.seed_projection(new, english_activation=True)[0])
        self.assertNotEqual(probe.seed_projection(old)[0], probe.seed_projection(new)[0])
        wrong_text = doc('Unreviewed')
        self.assertNotEqual(probe.seed_projection(old, english_activation=True)[0], probe.seed_projection(wrong_text, english_activation=True)[0])
        changed_uid = doc(uid='20')
        self.assertNotEqual(probe.seed_projection(old, english_activation=True)[0], probe.seed_projection(changed_uid, english_activation=True)[0])
        self.assertNotEqual(probe.seed_projection(doc(False,composition='Unknown'),english_activation=True)[0],probe.seed_projection(doc(composition='Unknown'),english_activation=True)[0])

    def test_seed_projection_preserves_document_reference_relationship(self):
        def sample(identity, reference):
            return ('SW.Blocks.FC', (('ID',identity),), None, ((('Ref',(('RefID',reference),),None,()),None),))
        self.assertEqual(probe.seed_projection(sample('0','0'),english_activation=True)[0], probe.seed_projection(sample('7','7'),english_activation=True)[0])
        with self.assertRaisesRegex(ValueError, 'Unresolved'):
            probe.seed_projection(sample('0','7'),english_activation=True)

    def test_seed_projection_rejects_id_aliases_and_changed_relations(self):
        def sample(first,second,target):
            return ('SW.Blocks.FC',(('ID',first),),None,((('Child',(('ID',second),),None,()),None),(('Ref',(('RefID',target),),None,()),None)))
        with self.assertRaisesRegex(ValueError,'ambiguous'): probe.seed_projection(sample('A','0a','A'),english_activation=True)
        self.assertNotEqual(probe.seed_projection(sample('0','1','0'),english_activation=True)[0],probe.seed_projection(sample('A','B','B'),english_activation=True)[0])

    def test_target_comment_structure_is_not_dropped(self):
        def sample(extra):
            return ('SW.Blocks.FC',(),None,((('ObjectList',(),None,((('MultilingualText',(('CompositionName','Comment'),('Format',extra)),None,()),None),)),None),))
        self.assertNotEqual(probe.seed_projection(sample('plain'),selected_comment=True)[0],probe.seed_projection(sample('changed'),selected_comment=True)[0])

    def test_seed_projection_preserves_real_insertion_indentation(self):
        import xml.etree.ElementTree as ET
        before = '<SW.Blocks.FC ID="0"><ObjectList><MultilingualText ID="1" CompositionName="Title"><ObjectList>\n  <MultilingualTextItem ID="2" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text /></AttributeList></MultilingualTextItem>\n</ObjectList></MultilingualText></ObjectList></SW.Blocks.FC>'
        after = before.replace('</MultilingualTextItem>\n</ObjectList>', '</MultilingualTextItem>\n  <MultilingualTextItem ID="3" CompositionName="Items"><AttributeList><Culture>en-GB</Culture><Text /></AttributeList></MultilingualTextItem>\n</ObjectList>')
        self.assertEqual(probe.seed_projection(probe.tree(ET.fromstring(before)),english_activation=True)[0], probe.seed_projection(probe.tree(ET.fromstring(after)),english_activation=True)[0])
        self.assertNotEqual(probe.tree(ET.fromstring(before)), probe.tree(ET.fromstring(after)))

    def test_seed_projection_does_not_hide_nonwhitespace_tail(self):
        import xml.etree.ElementTree as ET
        raw = '<SW.Blocks.FC ID="0"><ObjectList><MultilingualText ID="1" CompositionName="Title"><ObjectList><MultilingualTextItem ID="2" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text /></AttributeList></MultilingualTextItem>HIDDEN<MultilingualTextItem ID="3" CompositionName="Items"><AttributeList><Culture>en-GB</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.FC>'
        with self.assertRaisesRegex(ValueError,'whitespace'): probe.seed_projection(probe.tree(ET.fromstring(raw)),english_activation=True)

    def test_vci_inventory_requires_complete_settings_and_unique_native_mappings(self):
        path = self.root / "before.json"
        path.write_text(json.dumps({"completeWithinBounds": True}))
        with self.assertRaisesRegex(ValueError, "missing"): probe.fixture_vci(self.root)
        state = {"projectLanguageImportOptions": "Keep", "workspaces": []}
        path.write_text(json.dumps({"completeWithinBounds": True, "vci": state}))
        self.assertEqual(probe.fixture_vci(self.root), state)
        mapping = {"identity": [{"type": "FC", "Name": "F"}], "format": "SimaticML", "directory": "D:/isolated", "fileName": "fixture", "compareState": "Identical"}
        state["workspaces"] = [{"identity": [{"type": "Workspace", "Name": "W"}], "name": "W", "root": "D:/isolated", "culture": "de-DE", "mappings": [mapping, mapping]}]
        path.write_text(json.dumps({"completeWithinBounds": True, "vci": state}))
        with self.assertRaisesRegex(ValueError, "Duplicate native mapped-object"): probe.fixture_vci(self.root)


class ControllerTransitions(unittest.TestCase):
    """Run the real controller orchestration with native boundaries replaced."""
    setUp = ProbeTests.setUp
    tearDown = ProbeTests.tearDown
    fixture = ProbeTests.fixture
    seeded = ProbeTests.seeded
    def controller(self, failure=None):
        run = probe.Coordinator.__new__(probe.Coordinator)
        run.root = self.root; run.stores = [self.root]; run.parent_reservation = None; run.overlay_owner = None
        run.coordinator_identity = {"pid": 123, "created": 1.25}; run.run_nonce = "test-unique-run"
        run.fixture = {}; run.collector = {}; run.native = ({"pid": 7}, None); run.helper_alive = False
        run.pending_helper = None; run.discovery = {"processes": []}
        probe.save(self.root / "reviewed-input.json", probe.INPUT)
        trace = []; before, seeded = self.seeded()
        baseline_vci = {"projectLanguageImportOptions": "Keep", "workspaces": []}
        exported_vci = {"projectLanguageImportOptions": "Keep", "workspaces": [{"name": "twelve-exact-workspaces"}]}
        original = self.root / "TiaMcpScratch_test.ap21"
        host = {"software": {"blocks": [{"name": b["name"]} for b in before["blocks"]], "types": [], "tagTables": []}, "hardware": {"unchanged": True}}
        retrieves = []
        def call(name, arguments=None):
            trace.append(name)
            if name == "tia_create_scratch": return {"completed": True, "processId": 7, "compile": {"errorCount": 0}, "project": {"path": str(original)}}
            if name == "project_open": return {"opened": True, "project": {"path": arguments["path"]}}
            if name == "project_retrieve":
                target = self.root / ("restored-" + str(len(retrieves))) / "TiaMcpScratch_test.ap21"; retrieves.append(target)
                return {"retrieved": True, "archive": {"sha256": arguments["expectedSha256"]}, "project": {"path": str(target)}}
            if name == "tia_discover": return {"processes": []}
            raise AssertionError(name)
        def helper(binding, mode, label, project, extra, held):
            trace.append("helper:" + label)
            self.assertTrue(held.active)
            self.assertIsNotNone(run.parent_reservation)
            self.assertFalse(run.parent_reservation.with_name("run-outcome.json").exists())
            directory = self.root / label; directory.mkdir()
            if failure == "seed-helper" and label == "seed": raise RuntimeError("partial native seed")
            if mode == "seed":
                checkpoints = {}
                for role in ("preseed", "postseed"):
                    file = directory / (role + ".zap21"); file.write_text(role)
                    checkpoints[role + "Checkpoint"] = {**probe.ref(file), "role": role}
                return directory, checkpoints
            return directory, {"completed": True}
        def inventory(label, project):
            trace.append("inventory:" + label)
            if failure == "postexport-collection" and label == "postexport-inventory": raise RuntimeError("collection failed after successful helper")
            value = {"peers": before if label == "preseed-inventory" else seeded,
                     "vci": baseline_vci if label in {"postseed-inventory", "preseed-inventory"} else exported_vci}
            if failure == "preseed-vci" and label == "preseed-inventory": value = {**value, "vci": exported_vci}
            if failure == "postseed-vci" and label == "postseed-inventory": value = {**value, "vci": exported_vci}
            return value
        def snapshot(label, project, checkpoint):
            trace.append("snapshot:" + label)
            folder = self.root / label; folder.mkdir(); probe.save(folder / "before.json", {"testRetained": True})
            return folder
        def review(*args):
            trace.append("review:" + str(len(args)))
            return {"boundedPeersUnchanged": True, "allExportsReturned": failure != "export-return-false", "nativeImportEnabled": False,
                    "reopenedSnapshotMatches": len(args) == 3, "restoredSnapshotMatches": len(args) == 3 and failure != "restore-review-false"}
        def close():
            trace.append("close")
            self.assertFalse(run.helper_alive)
            if run.parent_reservation: self.assertFalse(run.parent_reservation.with_name("run-outcome.json").exists())
            return run.write("lifecycle-close", {"completed": failure != "close-false"})
        class Window:
            def __init__(self, roots, project, prefix, **kwargs):
                self.prefix = prefix; self.resources = kwargs.get("resources", [{"kind": "project", "path": str(project), "effect": "read"}])
                self.keys = [probe.resource_key(r["kind"], r["path"]) for r in self.resources]
                self.evidence = {"resources": self.resources, "effects": kwargs.get("effects", "read")}; self.active = False
            def __enter__(self): self.active = True; trace.append("window-enter:" + self.prefix.name); return self
            def __exit__(self, *args):
                self.active = False; trace.append("window-exit:" + self.prefix.name)
                probe.save(str(self.prefix) + "-intent.json", self.evidence)
                probe.save(str(self.prefix) + "-close.json", {"allClosed": failure != "store-release-false"})
                if failure == "store-release-false" and self.prefix.name == "final-reservation-locks": raise RuntimeError("Final store release failed")
        run.start = lambda: trace.append("start")
        run.start_native = lambda: trace.append("start-native")
        run.close_native = lambda: trace.append("close-native")
        run.call = call; run.helper = helper; run.inventory = inventory; run.snapshot = snapshot
        run.host_peers = lambda label: host
        run.close = close
        run.load_oracle = lambda: SimpleNamespace(review=review, snapshot=lambda *_: ({"workspaces": exported_vci["workspaces"]}, {"strictRawPeers": True}))
        stack = ExitStack()
        stack.enter_context(patch.object(probe, "StoreExclusiveWindow", Window))
        stack.enter_context(patch.object(probe, "StoreReadWindow", Window))
        def captured(folder, label="before"):
            if failure == "seed-collection": raise RuntimeError("seed raw collection failed")
            return before if label == "before" else seeded
        stack.enter_context(patch.object(probe, "fixture_snapshot", side_effect=captured))
        stack.enter_context(patch.object(probe, "fixture_vci", return_value=baseline_vci))
        stack.enter_context(patch.object(probe, "collector_vci", side_effect=lambda folder,label: baseline_vci if label == "before" else exported_vci))
        return run, trace, stack

    def assert_pending(self, run):
        self.assertTrue(run.parent_reservation.exists())
        self.assertFalse(run.parent_reservation.with_name("run-outcome.json").exists())
        with self.assertRaisesRegex(ValueError, "Unresolved"):
            probe.scan_overlays([self.root], [probe.resource_key("project", run.original)])

    def test_controller_success_resolves_only_after_both_recoveries_and_cleanup(self):
        run, trace, stack = self.controller()
        with stack: result = run.execute()
        self.assertTrue(result["completed"], result)
        self.assertEqual(trace.count("project_retrieve"), 2)
        self.assertLess(trace.index("inventory:preseed-inventory"), trace.index("snapshot:original-final"))
        self.assertLess(trace.index("close"), trace.index("window-enter:final-reservation-locks"))
        self.assertTrue(probe.scan_overlays([self.root], [probe.resource_key("project", run.original)])[0]["completed"])

    def test_controller_partial_seed_keeps_parent_and_stops_export(self):
        run, trace, stack = self.controller("seed-helper")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertNotIn("helper:export", trace); self.assert_pending(run)

    def test_controller_seed_collection_failure_keeps_parent(self):
        run, trace, stack = self.controller("seed-collection")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertNotIn("helper:export", trace); self.assert_pending(run)

    def test_controller_false_export_return_blocks_reopen_and_recovery(self):
        run, trace, stack = self.controller("export-return-false")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertNotIn("project_retrieve", trace); self.assertNotIn("snapshot:reopened", trace); self.assert_pending(run)

    def test_controller_postexport_collection_failure_keeps_parent(self):
        run, trace, stack = self.controller("postexport-collection")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertNotIn("project_retrieve", trace); self.assert_pending(run)

    def test_controller_postseed_vci_mismatch_blocks_preseed_retrieval(self):
        run, trace, stack = self.controller("postseed-vci")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertEqual(trace.count("project_retrieve"), 1); self.assert_pending(run)

    def test_controller_false_restoration_review_blocks_preseed_retrieval(self):
        run, trace, stack = self.controller("restore-review-false")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertEqual(trace.count("project_retrieve"), 1); self.assert_pending(run)

    def test_controller_preseed_vci_mismatch_blocks_final_original(self):
        run, trace, stack = self.controller("preseed-vci")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertNotIn("snapshot:original-final", trace); self.assert_pending(run)

    def test_controller_false_cleanup_keeps_parent_after_checks(self):
        run, trace, stack = self.controller("close-false")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assertIn("snapshot:original-final", trace); self.assert_pending(run)

    def test_final_store_release_failure_never_publishes_terminal_parent(self):
        run, trace, stack = self.controller("store-release-false")
        with stack: result = run.execute()
        self.assertFalse(result["completed"]); self.assert_pending(run)
        self.assertFalse(json.loads((self.root / "result.json").read_text())["completed"])

    def test_actual_run_detach_deadline_and_cancellation_blocks_next_start_until_terminal(self):
        run, trace, stack = self.controller()
        watcher = Watcher(7); watcher.evidence["originalFileTimeTicks"] = 7
        info = {"pid": 7, "created": 2.25}; row = {"identity": info, "watcher": watcher}
        run.native = (info, watcher); run.native_row = row; run.identities = {(7, 2.25)}; run.lifetimes = []
        original_call = run.call
        def call(name, arguments=None):
            if name == "tia_detach": trace.append("detach-request"); return {"disposedOwnedPortalConnection": True, "attached": False}
            return original_call(name, arguments)
        waits = 0
        def wait(*_):
            nonlocal waits
            waits += 1; self.assertIsNotNone(run.native); self.assertEqual(watcher.closes, 0)
            if waits == 1: return 258
            if waits == 2: raise KeyboardInterrupt()
            if waits == 3: raise SystemExit()
            watcher.live = False; trace.append("native-terminal"); return 0
        watcher.api.wait = wait
        def close_row(actual):
            self.assertIs(actual, row)
            return life.wait_retained_terminal(watcher, timeout_seconds=0, clock=lambda: 1)
        def close_all():
            self.assertFalse(watcher.live); self.assertEqual(watcher.closes, 1)
            trace.append("tree-close"); return {"completed": False, "exits": []}
        run.tree = SimpleNamespace(await_capture_complete=lambda: None, close_record=close_row, close_all=close_all)
        process = SimpleNamespace(stdin=Mock(), stdout=Mock(), stderr=Mock(), returncode=0, poll=Mock(return_value=0), kill=Mock(side_effect=AssertionError("kill")))
        process.stdin.close.side_effect = lambda: trace.append("host-eof")
        run.client = SimpleNamespace(process=process, output_reader=Mock(), error_reader=Mock(), errors=[], close=Mock(side_effect=AssertionError("Client.close")))
        run.client.output_reader.is_alive.return_value = False; run.client.error_reader.is_alive.return_value = False
        run.call = call
        run.close_native = probe.Coordinator.close_native.__get__(run)
        run.close = probe.Coordinator.close.__get__(run)
        with stack, patch("sys.stdout", new_callable=io.StringIO): result = run.execute()
        self.assertFalse(result["completed"]); self.assert_pending(run)
        self.assertLess(trace.index("native-terminal"), trace.index("host-eof"))
        self.assertNotIn("start-native", trace); self.assertNotIn("project_retrieve", trace)
        self.assertTrue(run.lifetimes[0]["proof"]["deadlineOrCancellationObserved"])
        run.client.close.assert_not_called(); process.kill.assert_not_called()

    def test_actual_run_start_timeout_retains_pre_reply_tree_and_never_autokills_host(self):
        processes, watchers, make, host, worker, hp, wp, factory, binder = RetainedTests().fixture(self.root)
        run = probe.Coordinator.__new__(probe.Coordinator)
        run.root = self.root; run.host = hp; run.worker = wp; run.host_files = []; run.source_rows = []
        run.client = run.native = run.native_row = run.tree = None; run.helper_alive = False
        run.host_watch = run.worker_watch = None; run.identities = set(); run.lifetimes = []
        run.calls = 0; run.parent_reservation = None
        run.load_oracle = lambda: object(); trace = []
        class Tree(life.RetainedNativeTree):
            def __init__(self, *args):
                super().__init__(*args, watcher_factory=factory, binder=binder, start_thread=False)
                trace.append("host-monitor-before-initialize")
        class Client:
            def __init__(self, *_):
                self.errors = []; self.output_reader = Mock(); self.error_reader = Mock()
                self.output_reader.is_alive.return_value = False; self.error_reader.is_alive.return_value = False
                self.process = SimpleNamespace(pid=1, stdin=Mock(), stdout=Mock(), stderr=Mock(), returncode=0, poll=Mock(return_value=0), kill=Mock(side_effect=AssertionError("kill")))
                def eof():
                    selftest.assertIn("native-captured-before-timeout", trace)
                    trace.append("host-eof")
                    for item in watchers.values(): item.live = False
                self.process.stdin.close.side_effect = eof
            def request(self, *_): trace.append("initialize"); return {}
            def notify(self, *_): pass
            def tool(self, name, arguments=None):
                if name == "tia_discover": return {"processes": []}
                if name == "tia_start":
                    selftest.assertIsNotNone(run.tree.pending_start)
                    native = make(3, 2, "Siemens.Automation.Portal.exe"); native.ancestors = [worker, host]
                    host.descendants.append(native); worker.descendants = [native]
                    run.tree.collect(); selftest.assertIn(3, watchers)
                    trace.append("native-captured-before-timeout"); raise TimeoutError("synthetic missing native reply")
                raise AssertionError(name)
            def close(self): raise AssertionError("automatic Client.close forbidden")
        selftest = self
        with patch.object(probe, "Client", Client), patch.object(probe, "RetainedNativeTree", Tree), \
                patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), \
                patch.object(life, "identity", side_effect=lambda p: dict(p.info)), patch("sys.stdout", new_callable=io.StringIO):
            result = run.execute()
        self.assertFalse(result["completed"])
        self.assertEqual(trace, ["host-monitor-before-initialize", "initialize", "native-captured-before-timeout", "host-eof"])
        self.assertEqual(len(watchers), 3); self.assertTrue(all(w.closes == 1 for w in watchers.values()))
        self.assertTrue(result["lifecycle"]["close"]["tree"]["startupOutcomeUnknown"])
        run.client.process.kill.assert_not_called()

    def test_actual_run_missing_worker_startup_holds_before_eof_despite_terminal_host(self):
        processes, watchers, make, host, worker, hp, wp, factory, binder = RetainedTests().fixture(self.root)
        host.descendants = []
        run = probe.Coordinator.__new__(probe.Coordinator)
        run.root = self.root; run.host = hp; run.worker = wp; run.host_files = []; run.source_rows = []
        run.client = run.native = run.native_row = run.tree = None; run.helper_alive = False
        run.host_watch = run.worker_watch = None; run.identities = set(); run.lifetimes = []
        run.calls = 0; run.parent_reservation = None; run.load_oracle = lambda: object(); trace = []; test = self
        class Tree(life.RetainedNativeTree):
            def __init__(self, *args):
                super().__init__(*args, watcher_factory=factory, binder=binder, start_thread=False)
        class Client:
            def __init__(self, *_):
                self.errors = []; self.output_reader = Mock(); self.error_reader = Mock()
                self.output_reader.is_alive.return_value = False; self.error_reader.is_alive.return_value = False
                self.process = SimpleNamespace(pid=1, stdin=Mock(), stdout=Mock(), stderr=Mock(), returncode=0, poll=Mock(return_value=0), kill=Mock(side_effect=AssertionError("kill")))
                self.process.stdin.close.side_effect = lambda: trace.append("host-eof")
            def request(self, *_):
                trace.append("initialize-timeout"); watchers[1].live = False
                raise TimeoutError("ordinary Host exited before worker capture")
            def notify(self, *_): raise AssertionError("later dispatch")
            def tool(self, *_): raise AssertionError("later dispatch")
            def close(self): raise AssertionError("Client.close")
        calls = 0
        def advance(_):
            nonlocal calls
            calls += 1; test.assertNotIn("host-eof", trace); test.assertTrue(all(w.closes == 0 for w in watchers.values()))
            test.assertFalse(run.tree.worker_captured())
            if calls == 1: raise KeyboardInterrupt()
            if calls == 2: raise SystemExit()
            run.tree.retain(worker, "worker", [dict(host.info)]); watchers[2].live = False
            trace.append("late-original-worker-capture")
        with patch.object(probe, "Client", Client), patch.object(probe, "RetainedNativeTree", Tree), \
                patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), \
                patch.object(life, "identity", side_effect=lambda p: dict(p.info)), \
                patch.object(life.time, "sleep", side_effect=advance), patch("sys.stdout", new_callable=io.StringIO):
            result = run.execute()
        self.assertFalse(result["completed"]); self.assertEqual(calls, 3)
        self.assertEqual(trace, ["initialize-timeout", "late-original-worker-capture", "host-eof"])
        self.assertTrue(result["lifecycle"]["close"]["tree"]["startupInventoryUnknownReasons"])
        self.assertFalse((self.root / "call-001-tia_discover-intent.json").exists())
        self.assertTrue(all(w.closes == 1 for w in watchers.values()))

    def test_actual_run_partial_client_constructor_retains_owned_host_for_cleanup(self):
        processes, watchers, make, host, worker, hp, wp, factory, binder = RetainedTests().fixture(self.root)
        run = probe.Coordinator.__new__(probe.Coordinator)
        run.root = self.root; run.host = hp; run.worker = wp; run.host_files = []; run.source_rows = []
        run.client = run.native = run.native_row = run.tree = None; run.helper_alive = False
        run.host_watch = run.worker_watch = None; run.identities = set(); run.lifetimes = []
        run.calls = 0; run.parent_reservation = None; run.load_oracle = lambda: object(); trace = []
        class Tree(life.RetainedNativeTree):
            def __init__(self, *args):
                super().__init__(*args, watcher_factory=factory, binder=binder, start_thread=False)
                trace.append("host-retained")
        class Client:
            def __init__(self, *_):
                self.errors = []; self.output_reader = Mock(); self.error_reader = Mock()
                self.output_reader.is_alive.return_value = False; self.error_reader.is_alive.return_value = False
                self.process = SimpleNamespace(pid=1, stdin=Mock(), stdout=Mock(), stderr=Mock(), returncode=0, poll=Mock(return_value=0), kill=Mock(side_effect=AssertionError("kill")))
                def eof():
                    trace.append("eof")
                    for watcher in watchers.values(): watcher.live = False
                self.process.stdin.close.side_effect = eof
                trace.append("constructor-interrupted-after-popen"); raise KeyboardInterrupt()
            def request(self, *_): raise AssertionError("initialize after failed constructor")
            def close(self): raise AssertionError("Client.close")
        with patch.object(probe, "Client", Client), patch.object(probe, "RetainedNativeTree", Tree), \
                patch.object(life.psutil, "Process", side_effect=lambda pid: processes[pid]), \
                patch.object(life, "identity", side_effect=lambda p: dict(p.info)), patch("sys.stdout", new_callable=io.StringIO):
            result = run.execute()
        self.assertFalse(result["completed"])
        self.assertEqual(trace, ["constructor-interrupted-after-popen", "host-retained", "eof"])
        self.assertEqual(len(watchers), 2); self.assertTrue(all(w.closes == 1 for w in watchers.values()))
        self.assertEqual(result["failure"], "KeyboardInterrupt: ")

    def test_terminal_overlay_cannot_hide_failed_or_missing_stage(self):
        run, trace, stack = self.controller()
        with stack: result = run.execute()
        self.assertTrue(result["completed"])
        outcome_path = run.parent_reservation.with_name("run-outcome.json")
        outcome = json.loads(outcome_path.read_text())
        (self.root / "original-verified.json").write_text(json.dumps({"completed": False}))
        outcome["evidence"] = [probe.ref(a["path"]) for a in outcome["evidence"]]
        outcome_path.write_text(json.dumps(outcome))
        with self.assertRaisesRegex(ValueError, "did not complete"):
            probe.scan_overlays([self.root], [probe.resource_key("project", run.original)])
        outcome["evidence"] = []
        outcome_path.write_text(json.dumps(outcome))
        with self.assertRaisesRegex(ValueError, "evidence is incomplete"):
            probe.scan_overlays([self.root], [probe.resource_key("project", run.original)])

    def test_terminal_overlay_rejects_failed_store_release_even_with_rebound_hash(self):
        run, trace, stack = self.controller()
        with stack: result = run.execute()
        self.assertTrue(result["completed"])
        outcome_path = run.parent_reservation.with_name("run-outcome.json")
        outcome = json.loads(outcome_path.read_text())
        close_path = self.root / "final-reservation-locks-close.json"
        close_path.write_text(json.dumps({"allClosed": False}))
        outcome["finalStoreClose"] = probe.ref(close_path); outcome_path.write_text(json.dumps(outcome))
        with self.assertRaisesRegex(ValueError, "store release did not complete"):
            probe.scan_overlays([self.root], [probe.resource_key("project", run.original)])

    def test_actual_helper_timeout_retains_store_and_original_handle_until_exit(self):
        run = probe.Coordinator.__new__(probe.Coordinator)
        run.root = self.root; run.stores = [self.root]; run.parent_reservation = None; run.overlay_owner = None
        run.coordinator_identity = {"pid": 123, "created": 1.25}; run.run_nonce = "timeout-run"
        run.original = self.root / "TiaMcpScratch_test.ap21"; run.native_iso = "2026-10-15T00:00:00.1234567Z"
        run.helper_alive = False; run.pending_helper = None
        for name in ("Host.exe", "Worker.exe", "Fixture.exe", "Program.cs", "Fixture.csproj"):
            (self.root / name).write_text(name)
        run.host = self.root / "Host.exe"; run.worker = self.root / "Worker.exe"
        probe.save(self.root / "recovery-gate-verified.json", {"completed": True})
        binding = {"binary": probe.ref(self.root / "Fixture.exe"), "sources": [probe.ref(self.root / p) for p in ("Program.cs", "Fixture.csproj")]}
        binding["files"] = [binding["binary"], *binding["sources"]]
        native = {"pid": 7, "created": 2.25}
        run.native = native, SimpleNamespace(api=SimpleNamespace(wait=lambda *_: 258), _handle=object())
        trace = []; test = self
        class Window:
            def __init__(self):
                self.prefix = test.root / "seed-locks"; self.active = False
                self.keys = [probe.resource_key("project", run.original)]
                self.evidence = {"resources": [{"kind": "project", "path": str(run.original), "effect": "partial mutation"}], "effects": "seed"}
            def __enter__(self):
                self.active = True; probe.save(str(self.prefix) + "-intent.json", self.evidence); trace.append("locks-held"); return self
            def __exit__(self, *args):
                test.assertFalse(run.helper_alive); trace.append("locks-released"); self.active = False
        window = Window()
        class Process:
            def __init__(self, *args, **kwargs): self.pid = 9; self.returncode = None; self.waits = 0
            def wait(self, timeout):
                test.assertTrue(window.active); test.assertTrue(run.helper_alive); self.waits += 1
                test.assertFalse(run.parent_reservation.with_name("run-outcome.json").exists())
                if self.waits == 1: raise probe.subprocess.TimeoutExpired("fixture", timeout)
                if self.waits == 2:
                    packet = json.loads((test.root / "seed-outcome-unknown.json").read_text())
                    test.assertTrue(packet["storeLocksRetained"] and packet["hostCloseProhibited"] and packet["originalCreateProcessHandleRetained"])
                    trace.append("unknown-held"); raise KeyboardInterrupt()
                self.returncode = 0; trace.append("helper-terminal"); return 0
        class Lifetime:
            def __init__(self, process, executable):
                self.process = process; self.identity = {"pid": process.pid, "created": 3.25, "exe": str(executable)}
                self.proof = {"identity": self.identity, "sameCreateProcessHandle": True}
            def finish(self):
                test.assertTrue(window.active); test.assertEqual(self.process.returncode, 0)
                trace.append("original-handle-closed")
                return {"completed": True, "handleClosed": True, "exitCode": 0, "sameCreateProcessHandle": True}
        def body():
            with window as held:
                run.begin_reservation(held)
                run.helper(binding, "seed", "seed", run.original, {}, held)
            self.fail("Unknown helper outcome must not advance even after eventual exit zero")
        def close():
            self.assertFalse(window.active); self.assertFalse(run.helper_alive); self.assertIsNone(run.pending_helper)
            trace.append("host-close"); return run.write("lifecycle-close", {"completed": True})
        run.run = body; run.close = close
        with patch.object(probe.subprocess, "Popen", Process), patch.object(probe, "RetainedHelper", Lifetime), \
                patch.object(probe, "identity", return_value=native), patch.object(probe.psutil, "Process", return_value=object()), \
                patch("sys.stdout", new_callable=io.StringIO) as output:
            result = run.execute()
        self.assertIn("BLOCKED_HELPER_CLEANUP", output.getvalue())
        self.assertEqual(trace, ["locks-held", "unknown-held", "helper-terminal", "original-handle-closed", "locks-released", "host-close"])
        self.assertFalse(result["completed"]); self.assert_pending(run)
        terminal = json.loads((self.root / "seed-terminal-after-unknown.json").read_text())
        self.assertTrue(terminal["outcomeRemainsUnknown"] and terminal["sameStoreLocksHeldUntilTerminal"])


if __name__ == "__main__": unittest.main()
