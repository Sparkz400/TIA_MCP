"""Offline checks of the pure helpers in qualify_plc_object_mutations.py; nothing here starts TIA."""
import codecs
import json
import pathlib
import sys
import tempfile
import unittest

from preview_payload import preview_payload
import qualify_plc_object_mutations as q
import release_matrix

ROOT = pathlib.Path(__file__).resolve().parents[1]
P = "/projects/S/devices/St/items/PLC/plc/PLC_1"


def inventory(project="D:/a/S.ap21", address="%M0.0", groups=(P + "/blocks",)):
    return {"blocks": [{"path": P + "/blocks/Main", "name": "Main", "number": 1, "identity": {"projectPath": project}}],
            "types": [{"path": P + "/types/U", "name": "U"}],
            "tagTables": [{"path": P + "/tags/T", "name": "T", "identity": {"projectPath": project},
                           "tags": [{"path": P + "/tags/T/tags/A", "name": "A", "logicalAddress": address}],
                           "constants": [{"path": P + "/tags/T/systemConstants/Local~Device", "name": "Local~Device", "kind": "system"}]}],
            "softwareScopes": [{"path": P, "groups": list(groups), "identity": {"projectPath": project}}]}


class FakeHarness:
    """Answers as the worker does around a checkpoint that failed before any mutation."""
    apply_args = staticmethod(q.Details.apply_args)

    def __init__(self):
        self.report, self.failed = {"mutations": []}, {}

    def persist(self):
        pass

    def stage(self, name, operation):
        return operation()

    def call_error(self, name, arguments):
        self.failed[arguments["changeId"]] = arguments["idempotencyKey"]
        return {"code": "CheckpointFailed", "message": "no archive"}

    def tool(self, name, arguments):
        failed = arguments["changeId"] in self.failed
        if name == "change_apply":
            assert self.failed[arguments["changeId"]] == arguments["idempotencyKey"], "The replay must reuse its idempotency key"
        return {"state": "not_applied" if failed else "applied", "failure": "no archive" if failed else None, "recoveryArchiveSha256": None,
                "stages": ["prechange_project_saved", "change_finished_with_failure"]}


class FakeRun(q.Run):
    """Prepares into a folder and appends applied decisions to the decision log as the worker does."""

    def __init__(self, folder):
        super().__init__(FakeHarness())
        self.plc, self.project, self.folder, self.refusals, self.intents = P, "D:/a/S.ap21", pathlib.Path(folder), [], {}

    def state(self):
        return {}

    def prepare(self, label, kind, payload, preview=None, intent=None):
        directory = self.folder / "changes" / ("change-%d" % len(list(self.folder.glob("changes/change-*"))))
        directory.mkdir(parents=True)
        shown = {"payload": preview_payload(payload), "warnings": ["Confirmed intent conflicts with a decision of an earlier applied change"]
                 if intent and "hold blocks" in intent["decisions"][0]["statement"] else []}
        (directory / "journal.json").write_text(json.dumps({"preview": shown}), encoding="utf-8")
        if "source" in payload:
            (directory / "requested.scl").write_bytes(codecs.BOM_UTF8 + payload["source"].encode("utf-8"))
        self.intents[directory.name] = intent  # The log is written from the test's own data, not from a tool result.
        return {"changeId": directory.name, "previewHash": "h", "journalPath": str(directory / "journal.json"), "preview": shown, "intent": intent}

    def finish(self, label, kind, prepared, **expected):
        readbacks = []
        try:
            with (self.folder / "changes" / "intent-decisions.jsonl").open("a", encoding="utf-8") as log:
                change = next(name for name in self.intents if name == prepared["changeId"])
                for decision in self.intents[change]["decisions"]:
                    log.write(json.dumps({"projectPath": self.project, "changeId": change, **{k: decision[k] for k in ("id", "statement", "target")}}) + "\n")
        except OSError as error:
            readbacks.append({"role": "intent-log-write-failure", "message": str(error)})
        return prepared, {"readbacks": readbacks}

    def refuse_apply(self, kind, label, prepared, code, **overrides):
        source = pathlib.Path(prepared["journalPath"]).parent / "requested.scl"
        self.refusals.append((code, q.stored_input(source) == prepared["preview"]["payload"]["source"]))


class PlcObjectMutationTests(unittest.TestCase):
    def test_change_tools_are_asked_for_the_full_view(self):
        calls = []
        h = object.__new__(q.Details)
        h.client = type("Client", (), {"tool": lambda self, name, arguments=None: calls.append((name, arguments))})()
        h.tool("change_status", {"changeId": "c"})
        h.tool("tia_detach")
        self.assertEqual([("change_status", {"changeId": "c", "includeDetails": True}), ("tia_detach", None)], calls)
        self.assertTrue(q.Details.apply_args({"changeId": "c", "previewHash": "p"})["includeDetails"])

    def test_persisted_source_is_compared_by_the_previews_hash_and_restored(self):
        with tempfile.TemporaryDirectory() as folder:
            run = FakeRun(folder)
            prepared = run.prepare("rewrite", "source_upsert", {"language": "SCL", "source": q.drop(2) + "\u00e4"})
            q.hashed_input(run, prepared)
            source = pathlib.Path(prepared["journalPath"]).parent / "requested.scl"
            self.assertEqual([("PreviewMismatch", True), ("ArtifactChanged", False)], run.refusals)
            self.assertEqual(prepared["preview"]["payload"]["source"], q.stored_input(source))

    @unittest.skipUnless(sys.platform == "win32", "the decision log is locked through msvcrt")
    def test_audit_steps_block_the_archive_and_lock_the_decision_log(self):
        with tempfile.TemporaryDirectory() as folder:
            run = FakeRun(folder)
            q.audit_fixes(run)
            report = run.h.report
            self.assertTrue((pathlib.Path(folder) / "changes" / "change-0" / "before.zap21" / "occupied").is_dir())
            self.assertEqual(["not_applied"], [row["state"] for row in report["mutations"]])
            self.assertEqual("intent-log-write-failure", report["auditFixes"]["decisionLogLocked"]["role"])
            lines = (pathlib.Path(folder) / "changes" / "intent-decisions.jsonl").read_text(encoding="utf-8").splitlines()
            self.assertEqual(2, len(lines))
            self.assertEqual("change-1", json.loads(lines[0])["changeId"])
            with self.assertRaises(ValueError):
                json.loads(lines[1])

    def test_view_ignores_project_paths_and_diff_names_exact_keys(self):
        self.assertEqual(q.view(inventory()), q.view(inventory(project="D:/recovery/S.ap21")))
        before = q.view(inventory(), {"block:" + P + "/blocks/Main": {"payloadSha256": "a"}})
        after = q.view(inventory(address="%M1.0", groups=(P + "/blocks", P + "/blocks/groups/G")), {"block:" + P + "/blocks/Main": {"payloadSha256": "b"}})
        self.assertEqual({"added": ["group:" + P + "/blocks/groups/G"], "removed": [],
                          "changed": ["block:" + P + "/blocks/Main", "member:" + P + "/tags/T/tags/A"]}, q.diff(before, after))
        self.assertEqual({"added": [], "removed": [], "changed": []}, q.diff(before, before))

    def test_matches_is_a_recursive_subset_with_exact_lists(self):
        self.assertTrue(q.matches({"effects": [{"name": "A"}]}, {"effects": [{"name": "A", "type": "System.String"}], "x": 1}))
        self.assertFalse(q.matches({"effects": []}, {"effects": [{"name": "A"}]}))
        self.assertFalse(q.matches({"name": None}, {}))
        self.assertTrue(q.matches({"name": None}, {"name": None}))
        self.assertFalse(q.matches({"after": False}, {"after": 0}))
        self.assertFalse(q.matches({"number": 1}, {"number": True}))

    def test_stage_order_text_edits_and_target_groups(self):
        self.assertTrue(q.in_order(["a", "x", "b"], ("a", "b")))
        self.assertFalse(q.in_order(["b", "a"], ("a", "b")))
        self.assertEqual("<N>2</N>", q.replace_once("<N>1</N>", "1", "2"))
        for text in ("<N>2</N>", "<N>1</N><N>1</N>"):
            with self.assertRaises(AssertionError):
                q.replace_once(text, "<N>1</N>", "x")
        expected = {"plc_compile": None, "group_rename": P + "/tags/groups/G", "tag_table_import_xml": P + "/tags", "constant_delete": P + "/tags",
                    "plc_type_import_xml": P + "/types", "type_source_upsert": P + "/types", "block_attributes_set": P + "/blocks", "source_upsert": P + "/blocks"}
        self.assertEqual(expected, {kind: q.target_group(kind, P, {"groupPath": P + "/tags/groups/G"}) for kind in expected})

    def test_coverage_gaps_name_each_missing_criterion(self):
        complete = {need: ["x"] for need in q.NEEDS}
        self.assertEqual({}, q.coverage_gaps({"tag_set": complete}, ["tag_set"]))
        self.assertEqual({"tag_set": ["restored"], "tag_create": list(q.NEEDS)},
                         q.coverage_gaps({"tag_set": {**complete, "restored": []}}, ["tag_set", "tag_create"]))

    def test_covered_and_excluded_kinds_are_the_x11_change_kinds(self):
        catalog = json.loads((ROOT / release_matrix.CATALOG).read_text(encoding="utf-8"))
        ledger = json.loads((ROOT / release_matrix.LEDGER).read_text(encoding="utf-8"))
        owners = {row["featureId"]: row["obligations"] for row in ledger["catalogFeatures"]}
        x11 = {f["changeKind"] for f in catalog["features"] if f["changeKind"] and "X11" in release_matrix.owners(f["featureId"], owners)}
        self.assertEqual(len(q.KINDS), len(set(q.KINDS)))
        self.assertFalse(set(q.KINDS) & set(q.EXCLUDED))
        self.assertEqual(x11, set(q.KINDS) | set(q.EXCLUDED))


if __name__ == "__main__":
    unittest.main()
