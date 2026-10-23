"""Guest bootstrap guard tests; local synthetic ZIPs only, no TIA/guest execution."""
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/Invoke-GuestQualification.ps1"


class GuestBootstrapTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="guest-bootstrap-tests-", dir=ROOT / "artifacts")
        self.addCleanup(self.scratch.cleanup)
        self.directory = Path(self.scratch.name)

    def run_archive(self, entries, expected_hash=None, prerequisite_manifest=None):
        archive = self.directory / "input.zip"
        with zipfile.ZipFile(archive, "w") as zipped:
            for name, data in entries:
                zipped.writestr(name, data)
        digest = expected_hash or hashlib.sha256(archive.read_bytes()).hexdigest()
        command = ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive",
            "-ExecutionPolicy", "RemoteSigned", "-File", str(SCRIPT), "-BundleZip", str(archive),
            "-ExpectedSha256", digest, "-EvidenceDirectory", str(self.directory / "evidence")]
        if prerequisite_manifest:
            command += ["-PrerequisitesManifest", str(prerequisite_manifest)]
        process = subprocess.run(command,
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        reports = list((self.directory / "evidence").glob("run_*/guest-summary.json"))
        self.assertEqual(len(reports), 1, process.stdout + process.stderr)
        report = json.loads(reports[0].read_text(encoding="utf-8"))
        self.assertEqual(process.returncode, 1, report)
        self.assertFalse(report["completed"])
        self.assertEqual(report["status"], "failed")
        self.assertFalse(report["settingsModified"])
        return report, reports[0].parent

    def verified_placeholder_bundle(self):
        files = {name: b"placeholder; never executed" for name in [
            "host/TiaMcp.Host.exe", "host/TiaMcp.Host.dll", "host/TiaMcp.Host.deps.json", "host/TiaMcp.Host.runtimeconfig.json",
            "workers/openness/TiaMcp.Openness.V21.exe", "workers/simulator/TiaMcp.Simulator.V8.exe",
            "ui/project-explorer/index.html", "ui/project-explorer/index.template.html",
            "ui/project-explorer/style.css", "ui/project-explorer/model.js",
            "ui/project-explorer/client.js", "ui/project-explorer/explorer.js",
            "contracts/project-model.v1.schema.json", "contracts/project-model-provider.v1.schema.json",
            "contracts/typed-graphical-model.schema.json", "contracts/typed-graphical-change.schema.json",
            "contracts/typed-graphical-read.schema.json", "contracts/typed-graphical-profiles.schema.json",
            "fixtures/typed-graphical/profiles.v1.json", "fixtures/typed-graphical/instance-effects.v1.json"]}
        for name in ("Start-TiaMcp.ps1", "Verify-TiaMcpBundle.ps1"):
            files["scripts/" + name] = (ROOT / "scripts" / name).read_bytes()
        rows = [{"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()} for name, data in files.items()]
        return list(files.items()) + [("manifest.json", json.dumps({"files": rows}).encode())]

    def prerequisites(self, content=None, bad_hash=False, bad_file=False):
        packages = []
        content = content or [[("dotnet.exe", b"never execute")], [("aspnet.txt", b"fixture")], [("python.exe", b"never execute")]]
        for index, (package_id, entries) in enumerate(zip(("dotnet-runtime", "aspnet-runtime", "python"), content)):
            archive = self.directory / (package_id + ".zip")
            with zipfile.ZipFile(archive, "w") as zipped:
                for name, data in entries:
                    zipped.writestr(name, data)
            packages.append({"id": package_id, "file": "../escape.zip" if bad_file and index == 0 else archive.name,
                "sha256": "0" * 64 if bad_hash and index == 0 else hashlib.sha256(archive.read_bytes()).hexdigest(),
                "architecture": "x64", "version": "test"})
        manifest = self.directory / "prerequisites.json"
        manifest.write_text(json.dumps({"schemaVersion": 1, "packages": packages}), encoding="utf-8")
        return manifest

    def test_hash_mismatch_stops_before_extraction(self):
        report, run = self.run_archive([("ordinary.txt", b"fixture")], "0" * 64)
        self.assertIn("SHA-256", report["stages"][0]["details"])
        self.assertFalse((run / "bundle").exists())

    def test_zip_traversal_stops_before_extraction(self):
        report, run = self.run_archive([("../escape.txt", b"fixture")])
        self.assertIn("unsafe", report["stages"][0]["details"])
        self.assertFalse((run / "bundle").exists())
        self.assertFalse((run / "escape.txt").exists())

    def test_case_duplicate_stops_before_extraction(self):
        report, run = self.run_archive([("A.txt", b"a"), ("a.txt", b"b")])
        self.assertIn("duplicate", report["stages"][0]["details"])
        self.assertFalse((run / "bundle").exists())

    def test_symlink_entry_stops_before_extraction(self):
        link = zipfile.ZipInfo("link")
        link.create_system = 3
        link.external_attr = 0o120777 << 16
        report, run = self.run_archive([(link, b"outside")])
        self.assertIn("link/reparse", report["stages"][0]["details"])
        self.assertFalse((run / "bundle").exists())

    def test_valid_zip_without_verifier_is_retained_and_fails_integrity_stage(self):
        report, run = self.run_archive([("ordinary.txt", b"fixture")])
        self.assertEqual([row["status"] for row in report["stages"]], ["passed", "passed", "failed"])
        self.assertEqual(report["stages"][-1]["name"], "bundle_integrity")
        self.assertEqual((run / "bundle/ordinary.txt").read_bytes(), b"fixture")
        self.assertFalse((run / "mcp.local.json").exists())

    def test_portable_hash_mismatch_stops_before_tools_execution(self):
        report, run = self.run_archive(self.verified_placeholder_bundle(), prerequisite_manifest=self.prerequisites(bad_hash=True))
        self.assertEqual(report["stages"][-1]["name"], "portable_prerequisites")
        self.assertIn("SHA-256 mismatch", report["stages"][-1]["details"])
        self.assertFalse((run / "tools").exists())

    def test_portable_manifest_traversal_is_rejected(self):
        report, run = self.run_archive(self.verified_placeholder_bundle(), prerequisite_manifest=self.prerequisites(bad_file=True))
        self.assertIn("metadata", report["stages"][-1]["details"])
        self.assertFalse((run / "tools").exists())

    def test_portable_zip_traversal_is_rejected(self):
        manifest = self.prerequisites(content=[[("../escape.exe", b"never execute")], [("x", b"x")], [("python.exe", b"x")]])
        report, run = self.run_archive(self.verified_placeholder_bundle(), prerequisite_manifest=manifest)
        self.assertIn("unsafe", report["stages"][-1]["details"])
        self.assertFalse((run / "tools").exists())

    def test_portable_merge_refuses_conflicting_file_without_overwriting(self):
        manifest = self.prerequisites(content=[[("dotnet.exe", b"first version")], [("dotnet.exe", b"other version")], [("python.exe", b"never execute")]])
        report, run = self.run_archive(self.verified_placeholder_bundle(), prerequisite_manifest=manifest)
        self.assertIn("conflicting", report["stages"][-1]["details"])
        self.assertEqual((run / "tools/dotnet/dotnet.exe").read_bytes(), b"first version")
        self.assertFalse((run / "environment.json").exists())


if __name__ == "__main__":
    unittest.main()
