"""Offline checks of the VCI mapping runner's catalog binding and its other-engineer Git steps; no TIA Portal."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import types
import unittest

import qualify_vci_mappings as runner

ROOT = Path(__file__).resolve().parents[1]


class VciMappingRunner(unittest.TestCase):
    def test_every_qualified_kind_is_an_owned_catalog_change_kind(self):
        catalog = json.loads((ROOT / "contracts/feature-catalog.v1.json").read_text(encoding="utf-8"))
        kinds = {row["changeKind"] for row in catalog["features"] if row["changeKind"]}
        self.assertTrue(set(runner.KINDS) <= kinds)
        self.assertIn("vci_conflict_resolve", runner.KINDS)

    @unittest.skipUnless(shutil.which("git"), "Git is not installed")
    def test_other_engineer_changes_only_the_value_and_pushes_to_the_bare_remote(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            run = types.SimpleNamespace(git_exe=shutil.which("git"), git={"remote": str(work / "remote.git"), "clone": str(work / "clone"),
                                                                           "document": "VciGitFc.scl", "value": 1})
            runner.plain_git(run, work, "init", "--bare", "--initial-branch=main", run.git["remote"])
            seed = work / "seed"
            runner.plain_git(run, work, "init", "--initial-branch=main", str(seed))
            document = b"\xef\xbb\xbfFUNCTION \"VciGitFc\" : Void\r\nBEGIN\r\n\t#Value := 1;\r\nEND_FUNCTION\r\n"
            (seed / "VciGitFc.scl").write_bytes(document)
            runner.plain_git(run, seed, "add", "VciGitFc.scl")
            runner.plain_git(run, seed, "commit", "-m", "baseline")
            runner.plain_git(run, seed, "push", run.git["remote"], "main")
            runner.plain_git(run, work, "clone", "--branch", "main", run.git["remote"], run.git["clone"])
            head = runner.remote_commit(run, 5)
            self.assertEqual(head, runner.remote_git(run, "rev-parse", "refs/heads/main"))
            pushed = subprocess.run([run.git_exe, "--git-dir=" + run.git["remote"], "cat-file", "blob", "main:VciGitFc.scl"], capture_output=True, check=True).stdout
            self.assertEqual(document.replace(b":= 1;", b":= 5;"), pushed)
            with self.assertRaises(AssertionError):
                runner.remote_commit(run, 7)  # The document holds 5 now, not the remembered value 1.


if __name__ == "__main__":
    unittest.main()
