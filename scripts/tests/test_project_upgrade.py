"""Offline checks for the project_upgrade qualifier's pure logic."""
import argparse
import json
import os
from pathlib import Path
import tempfile
import traceback
import unittest
from unittest import mock

from library_native_harness import Harness
from qualify_project_upgrade import (EXAMPLES, REFUSED, body, fingerprint, inventory_view, leaks, paths_fit, pinned, public, refuse,
                                     refusal_view, released, remove_copies, stage_input, upgrade_view)

try:
    import _winapi
except ImportError:  # Not Windows.
    _winapi = None

SPEC = {"version": "V16", "format": "project", "project": "Demo_V21"}
REFUSAL = EXAMPLES["DigitalTwinAtEducation_TIAProject_Basic_V16.ap16"]


def write(path, data=b"x"):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def admission(run, identifier, state, operation="project_upgrade"):
    write(run / "engineering-admissions" / identifier / "00000000.json", json.dumps(
        {"id": identifier, "state": "dispatchIntent", "scope": {"operation": operation, "resources": []}}).encode())
    write(run / "engineering-admissions" / identifier / "00000001.json", json.dumps(
        {"id": identifier, "state": state, "scope": {"operation": operation, "resources": [{"effect": "readUpgradeSource"}]}}).encode())


class Folder(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)


class FingerprintTests(Folder):
    def test_project_covers_every_file_name_and_byte_of_its_folder(self):
        marker = write(self.root / "p" / "Demo.ap16")
        write(self.root / "p" / "System" / "a.xml", b"1")
        first = fingerprint(marker)
        self.assertEqual((first["files"], first["bytes"]), (2, 2))
        write(self.root / "p" / "System" / "a.xml", b"2")
        self.assertNotEqual(first, fingerprint(marker))
        (self.root / "p" / "System" / "a.xml").rename(self.root / "p" / "System" / "b.xml")
        self.assertNotEqual(first["sha256"], fingerprint(marker)["sha256"])

    def test_archive_covers_only_its_bytes(self):
        archive = write(self.root / "Demo.zap14", b"abc")
        first = fingerprint(archive)
        write(self.root / "other.txt")
        self.assertEqual(first, fingerprint(archive))
        self.assertEqual((first["files"], first["bytes"]), (1, 3))


class PathLimitTests(unittest.TestCase):
    def test_longest_upgrade_directory_fits_tia_limit(self):
        run = Path("D:/") / ("r" * 70)
        self.assertEqual(paths_fit(run, EXAMPLES), len(str(run)) + len("/upgrades/u_12345678/DigitalTwinAtEducation_TIAProject_Basic_V16_V21"))
        with self.assertRaises(AssertionError):
            paths_fit(Path("D:/") / ("r" * 76), EXAMPLES)

    def test_pinned_projects_follow_the_observed_names(self):
        # V21 names a folder's upgrade <folder>_V21 (the runner copies the folder under its file's stem) and an archive's
        # after the archived project; refused examples pin their native reason instead.
        for name, spec in EXAMPLES.items():
            if spec["format"] == "project" and "project" in spec:
                self.assertEqual(spec["project"], Path(name).stem + "_V21")
            self.assertEqual("refusal" in spec, "project" not in spec and "inventory" not in spec)


class InputTests(Folder):
    def test_only_the_public_examples_once_each(self):
        paths = [write(self.root / name) for name in EXAMPLES]
        self.assertEqual(sorted(public(paths, EXAMPLES)), sorted(EXAMPLES))
        for wrong in (paths[1:], paths + paths[:1], paths[1:] + [write(self.root / "Customer.ap16")]):
            with self.assertRaises(AssertionError):
                public(wrong, EXAMPLES)
        with self.assertRaises(AssertionError):
            public([self.root / "TinyMySQLClient.zap13"], {"TinyMySQLClient.zap13": "V13"})

    def test_archive_copied_alone_and_project_with_its_folder(self):
        archive = write(self.root / "src" / "Demo.zap14", b"a")
        write(self.root / "src" / "unrelated.pdf")
        project = write(self.root / "proj" / "Demo.ap16")
        write(self.root / "proj" / "Logs" / "ConversionLog.xml")
        run = self.root / "run"
        copied = stage_input(run, archive)
        self.assertEqual([p.name for p in copied.parent.iterdir()], ["Demo.zap14"])
        copied = stage_input(run / "two", project)
        self.assertEqual(fingerprint(copied), fingerprint(project))
        self.assertTrue((copied.parent / "Logs" / "ConversionLog.xml").is_file())


def result(run, **changes):
    value = {"upgraded": True, "compiled": False, "sourceUnchanged": True,
             "source": {"version": "V16", "format": "project", "sha256": "a" * 64, "sourceSha256": "b" * 64, "path": str(run / "inputs" / "x.ap16")},
             "project": {"name": "Demo_V21", "path": str(run / "upgrades" / "u_1" / "Demo_V21" / "Demo_V21.ap21"), "isModified": False},
             "upgradeLogs": [{"path": str(run / "upgrades" / "u_1" / "Logs" / "ConversionLog_16.0.0.0_to_21.0.0.0.xml"), "bytes": 10, "sha256": "c" * 64,
                              "succeeded": True, "errors": 0, "warnings": 2}]}
    for key, item in changes.items():
        if isinstance(item, dict) and isinstance(value.get(key), dict):
            value[key] = {**value[key], **item}
        else:
            value[key] = item
    return value


class UpgradeViewTests(Folder):
    def test_keeps_names_hashes_and_run_relative_paths(self):
        view = upgrade_view(result(self.root), self.root, SPEC)
        self.assertEqual(view["project"]["path"], "upgrades/u_1/Demo_V21/Demo_V21.ap21")
        self.assertEqual(view["logs"], [{"name": "ConversionLog_16.0.0.0_to_21.0.0.0.xml", "bytes": 10, "sha256": "c" * 64,
                                         "succeeded": True, "errors": 0, "warnings": 2}])
        self.assertNotIn(str(self.root), json.dumps(view))

    def test_refuses_every_unproven_outcome(self):
        outside = Path(tempfile.gettempdir()) / "elsewhere" / "Demo_V21.ap21"
        for changes in ({"upgraded": False}, {"compiled": True}, {"sourceUnchanged": False}, {"source": {"version": "V15"}},
                        {"source": {"format": "archive"}}, {"project": {"path": str(outside)}},
                        {"project": {"path": str(self.root / "upgrades" / "Demo.ap16")}}, {"upgradeLogs": []}, {"project": {"name": "p_V21"}},
                        {"project": {"path": str(self.root / "upgrades" / "u_1" / "p_V21" / "p_V21.ap21")}},
                        {"upgradeLogs": [{"path": "l", "bytes": 1, "sha256": "c", "succeeded": False, "errors": 1, "warnings": 0}]}):
            with self.subTest(changes=changes), self.assertRaises(AssertionError):
                upgrade_view(result(self.root, **changes), self.root, SPEC)


def refused(run, **changes):
    details = {"upgraded": False, "nativeType": REFUSAL["refusal"]["nativeType"], "sourceUnchanged": True, "upgradedProjects": [],
               "destination": str(run / "upgrades" / "u_2"), "source": {"sha256": "a" * 64, "sourceSha256": "b" * 64}}
    error = {"code": "ProjectUpgradeFailed", "message": "Required products are missing. " + REFUSAL["refusal"]["text"], "details": details}
    for key, item in changes.items():
        (details if key in details else error)[key] = item
    return error


class RefusalViewTests(Folder):
    def test_pinned_native_refusal_with_retained_copy(self):
        (self.root / "upgrades" / "u_2").mkdir(parents=True)
        view = refusal_view(refused(self.root), self.root, REFUSAL)
        self.assertEqual((view["nativeType"], view["ownedCopy"]), ("Siemens.Engineering.MissingProductsException", "upgrades/u_2"))
        self.assertNotIn(str(self.root), json.dumps(view))

    def test_any_other_failure_fails(self):
        (self.root / "upgrades" / "u_2").mkdir(parents=True)
        for changes in ({"code": "UpgradeVerificationFailed"}, {"nativeType": "System.IO.IOException"}, {"message": "another reason"},
                        {"sourceUnchanged": False}, {"upgraded": True}, {"upgradedProjects": ["x.ap21"]},
                        {"destination": str(self.root / "upgrades" / "missing")}, {"destination": tempfile.gettempdir()}):
            with self.subTest(changes=changes), self.assertRaises(AssertionError):
                refusal_view(refused(self.root, **changes), self.root, REFUSAL)


class InventoryTests(unittest.TestCase):
    INVENTORY = {"completeness": {"truncated": False}, "warnings": [],
                 "hardware": [{"path": "d/1", "typeIdentifier": "OrderNumber:6ES7"}, {"path": "d/0", "typeIdentifier": None}],
                 "plcs": [{"path": "/projects/Demo_V21/devices/d/items/1/plc/PLC_1"}],
                 "blocks": [{"path": "b/Main", "isConsistent": True}, {"path": "b/FB1", "isConsistent": False}]}

    def test_view_is_order_independent_and_counts_the_compile_state(self):
        shuffled = dict(self.INVENTORY, hardware=self.INVENTORY["hardware"][::-1], blocks=self.INVENTORY["blocks"][::-1])
        self.assertEqual(inventory_view(self.INVENTORY), inventory_view(shuffled))
        self.assertEqual(pinned(inventory_view(self.INVENTORY)),
                         {"hardwareItems": 2, "plcs": ["devices/d/items/1/plc/PLC_1"], "blocks": 2, "inconsistentBlocks": 1})

    def test_truncated_inventory_is_refused(self):
        with self.assertRaises(AssertionError):
            inventory_view(dict(self.INVENTORY, completeness={"truncated": True}))


class AdmissionTests(Folder):
    def test_exactly_one_returned_upgrade_admission(self):
        admission(self.root, "0" * 32, "returned", "change_apply")
        admission(self.root, "1" * 32, "returned")
        self.assertEqual(released(self.root, {"0" * 32})["admissionId"], "1" * 32)
        with self.assertRaises(AssertionError):
            released(self.root, set())  # Two new admissions.
        with self.assertRaises(AssertionError):
            released(self.root, {"0" * 32, "1" * 32})  # None.

    def test_unknown_or_foreign_outcomes_fail(self):
        admission(self.root, "2" * 32, "outcomeUnknown")
        with self.assertRaises(AssertionError):
            released(self.root, set())
        admission(self.root, "3" * 32, "returned", "project_retrieve")
        with self.assertRaises(AssertionError):
            released(self.root, {"2" * 32})


class FakeHarness:
    def __init__(self, directory, error, side_effect=None):
        self.directory, self.error, self.side_effect, self.calls = directory, error, side_effect, []

    def stage(self, name, operation):
        return operation()

    def owned_portal(self, label):
        self.calls.append("tia_start")

    def call_error(self, name, arguments):
        self.calls.append((name, Path(arguments["path"]).name))
        if self.side_effect:
            self.side_effect()
        return self.error if Path(arguments["path"]).exists() else {"code": "ProjectNotFound"}


class RefusalTests(Folder):
    def test_refusal_leaves_no_admission_upgrade_directory_or_change(self):
        original = write(self.root / "src" / "TinyMySQLClient.zap13", b"v13")
        h = FakeHarness(self.root / "run", {"code": "ProjectVersionUnsupported", "message": "m"})
        outcome = refuse(h, original)
        self.assertEqual(outcome["error"]["code"], "ProjectVersionUnsupported")
        self.assertEqual(h.calls, ["tia_start", ("project_upgrade", "TinyMySQLClient.zap13"), ("project_upgrade", "Missing.zap14"),
                                   ("project_upgrade", "TinyMySQLClient.zap14")])
        self.assertEqual(outcome["renamed"]["code"], "ProjectVersionUnsupported")

    def test_refusal_that_wrote_fails(self):
        original = write(self.root / "src" / "TinyMySQLClient.zap13", b"v13")
        for index, (error, effect) in enumerate((("ProjectUpgradeFailed", None), ("ProjectVersionUnsupported", "upgrades"),
                                                 ("ProjectVersionUnsupported", "admission"))):
            run = self.root / ("run%d" % index)
            act = {None: None, "upgrades": lambda: (run / "upgrades").mkdir(parents=True, exist_ok=True),
                   "admission": lambda: admission(run, "4" * 32, "returned")}[effect]
            with self.subTest(error=error, effect=effect), self.assertRaises(AssertionError):
                refuse(FakeHarness(run, {"code": error}, act), original)



class LinkTests(Folder):
    @unittest.skipUnless(_winapi and hasattr(_winapi, "CreateJunction"), "Windows junctions only")
    def test_a_junction_in_a_project_folder_is_refused_before_copying(self):
        marker = write(self.root / "proj" / "Demo.ap16")
        write(self.root / "elsewhere" / "secret.txt")
        _winapi.CreateJunction(str(self.root / "elsewhere"), str(self.root / "proj" / "Link"))
        with self.assertRaises(AssertionError):
            stage_input(self.root / "run", marker)
        self.assertFalse((self.root / "run" / "inputs").exists())


class PrivacyTests(Folder):
    @unittest.skipUnless(_winapi, "Windows file sharing only")
    def test_an_unreadable_original_names_only_the_example_in_the_report(self):
        # Originals below a private parent folder; the first one is locked against reading by another handle.
        parent = self.root / "Secret Parent"
        paths = []
        for name in EXAMPLES:
            paths.append(write(parent / name) if name.endswith(".zap14") else write(parent / Path(name).stem / name))
        refused = write(parent / "TinyMySQLClient.zap13")
        locked = next(p for p in paths if p.suffix == ".zap14")
        handle = _winapi.CreateFile(str(locked), 0x80000000, 0, 0, 3, 0x80, 0)
        run = self.root / "run"
        run.mkdir()
        h = Harness.__new__(Harness)
        h.directory, h.client, h.report = run, None, {"steps": []}
        h.args = argparse.Namespace(source=[str(p) for p in paths], refused=str(refused))
        h.tool = lambda name, arguments=None: {}
        try:
            # The run directory of a test lives in a long temporary path; the path check is not under test here.
            with mock.patch("qualify_project_upgrade.paths_fit", lambda run, names: 0), self.assertRaises(RuntimeError) as caught:
                body(h)
            h.report["failure"], h.report["traceback"] = str(caught.exception), "".join(traceback.format_exception(caught.exception))
        finally:
            _winapi.CloseHandle(handle)
        h.persist()
        text = (run / "qualification.json").read_text(encoding="utf-8")
        self.assertIn("TinyMySQLClient_V14_1.zap14: PermissionError", text)
        self.assertNotIn("Secret Parent", text)
        self.assertNotIn(str(self.root), text.replace("\\\\", "\\"))


class CleanupTests(Folder):
    def test_copies_are_removed_and_leaks_counted_by_folder_segment(self):
        run = self.root / "run"
        write(run / "inputs" / "Demo" / "Logs" / "ConversionLog.xml", b"D:\\Secret Parent\\Demo")
        write(run / "upgrades" / "u_1" / "Demo_V21" / "Demo_V21.ap21")
        write(run / "qualification.json", b'{"hashScope": "Archive bytes", "name": "TinyMySQLClient"}')
        self.assertEqual(remove_copies(run), ["qualification.json"])
        originals = [self.root / "Archiv" / "Ex" / "Ex.ap16", self.root / "Secret Parent" / "A.zap14"]
        self.assertEqual(leaks(run, originals), 0)
        write(run / "admission.json", b'{"path": "C:\\\\x\\\\Secret Parent\\\\A.zap14"}')
        write(run / "host.log", b"read D:/y/Archiv/Ex/Ex.ap16")
        write(run / "example.json", b'{"folder": "Ex"}')  # A project's own folder is the public example name.
        self.assertEqual(leaks(run, originals), 2)


if __name__ == "__main__":
    unittest.main()
