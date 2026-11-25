"""Keep the GitHub workflows pinned and the release gate in step with the offline workflow it reads."""
import pathlib
import re
import unittest

import yaml


ROOT = pathlib.Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github/workflows"
PINNED = re.compile(r"^[\w.-]+/[\w./-]+@[0-9a-f]{40} # v\d+\.\d+\.\d+$")


def workflow(name):
    return yaml.safe_load((WORKFLOWS / name).read_text(encoding="utf-8"))


@unittest.skipUnless(WORKFLOWS.is_dir(), "Needs the repository's .github/workflows folder.")
class WorkflowTests(unittest.TestCase):
    def test_every_action_is_pinned_to_a_commit(self):
        # A tag can be moved to other code; a commit SHA cannot. The trailing comment names the release for Dependabot.
        loose = []
        for path in sorted(WORKFLOWS.glob("*.yml")):
            for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
                used = re.match(r"^\s*(?:- )?uses:\s*(.*)$", line)
                if used and not used.group(1).startswith("./") and not PINNED.match(used.group(1)):
                    loose.append("%s:%d: %s" % (path.name, number, used.group(1)))
        self.assertFalse(loose, "Actions not pinned as owner/name@<40-hex commit> # vX.Y.Z:\n" + "\n".join(loose))

    def test_release_gate_names_offline_jobs_that_only_run_for_code(self):
        # release.yml accepts an offline run only if these jobs passed. That proves the run tested code only while each
        # of them is skipped as a whole job for a docs-only change; a job that merely skips its steps still succeeds.
        gate = workflow("release.yml")["jobs"]["checks"]["steps"][0]["run"]
        required = re.search(r"ran_code='(\[.*?\]) as \$required", gate, re.S)
        self.assertIsNotNone(required, "release.yml no longer lists the offline jobs it requires.")
        names = set(yaml.safe_load(required.group(1)))
        offline = workflow("offline.yml")["jobs"]
        found = {}
        for job in offline.values():
            shards = job.get("strategy", {}).get("matrix", {}).get("shard", [None])
            for shard in shards:
                found[job["name"].replace("${{ matrix.shard }}", str(shard))] = job
        self.assertFalse(names - found.keys(), "release.yml requires offline jobs that offline.yml does not have.")
        for name in sorted(names):
            self.assertEqual("needs.changes.outputs.code == 'true'", found[name].get("if"), name)
        # Every job that is skipped as a whole for a docs-only change must be required, or the gate has a hole.
        scoped = {name for name, job in found.items() if job.get("if") == "needs.changes.outputs.code == 'true'"}
        self.assertEqual(scoped, names)

    def test_release_build_has_no_write_token(self):
        jobs = workflow("release.yml")["jobs"]
        self.assertEqual({"contents": "read"}, jobs["build"]["permissions"])
        self.assertEqual({"id-token": "write", "attestations": "write"}, jobs["attest"]["permissions"])
        self.assertIn("attest", jobs["draft"]["needs"])


if __name__ == "__main__":
    unittest.main()
