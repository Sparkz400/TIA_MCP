"""The CI package-stages matrix must run every packaged workflow stage of Test-OfflinePackage.ps1 exactly once."""
import os
import pathlib
import shutil
import subprocess
import unittest

import yaml


ROOT = pathlib.Path(__file__).resolve().parents[2]
RUNNER = ROOT / "scripts/Test-OfflinePackage.ps1"
WORKFLOW = ROOT / ".github/workflows/offline.yml"
PWSH = shutil.which("pwsh")


def list_stages(*arguments):
    result = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-File", str(RUNNER), *arguments],
                            capture_output=True, text=True, timeout=60, env=dict(os.environ))
    return result


@unittest.skipUnless(PWSH and WORKFLOW.is_file(), "Needs PowerShell 7 and .github/workflows/offline.yml.")
class PackageStageSplitTests(unittest.TestCase):
    def test_every_stage_runs_in_exactly_one_ci_leg(self):
        listed = list_stages("-ListStages")
        self.assertEqual(0, listed.returncode, listed.stderr)
        stages = listed.stdout.split()
        self.assertTrue(stages, "Test-OfflinePackage.ps1 -ListStages printed no stages.")
        self.assertEqual(len(stages), len(set(stages)), "Stage names must be unique.")
        jobs = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))["jobs"]
        legs = jobs["package-stages"]["strategy"]["matrix"]["include"]
        assigned = [name for leg in legs for name in leg["stages"].split()]
        duplicates = sorted({name for name in assigned if assigned.count(name) > 1})
        self.assertEqual([], duplicates, "Stages assigned to more than one CI leg.")
        self.assertEqual([], sorted(set(stages) - set(assigned)), "Stages no CI leg runs.")
        self.assertEqual([], sorted(set(assigned) - set(stages)), "CI legs name stages the runner does not have.")
        self.assertEqual(list(range(len(legs))), [leg["shard"] for leg in legs])
        self.assertIn("/%d)" % len(legs), jobs["package-stages"]["name"], "The leg name states the leg count.")

    def test_aggregate_check_requires_build_and_every_leg(self):
        jobs = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))["jobs"]
        aggregate = jobs["package"]
        self.assertEqual("Portable package and relocation", aggregate["name"])
        self.assertEqual({"changes", "package-build", "package-stages"}, set(aggregate["needs"]))
        # Without always() a failed leg would skip the required check, and GitHub counts a skipped check as passed.
        self.assertIn("always()", aggregate["if"])
        self.assertEqual("needs.package-build.outputs.mode == 'full'", jobs["package-stages"]["if"])

    def test_unknown_or_quick_stage_selection_is_refused_before_any_work(self):
        for arguments in (["-Stage", "no-such-stage"], ["-Quick", "-Stage", "graph-mcp"]):
            with self.subTest(arguments=arguments):
                result = list_stages(*arguments, "-OutputDirectory", str(ROOT / "artifacts/offline-package/never-created"))
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((ROOT / "artifacts/offline-package/never-created").exists())


if __name__ == "__main__":
    unittest.main()
