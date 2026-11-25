import json
import tempfile
import unittest
from pathlib import Path

from hmi_evidence import derive


class HmiEvidenceTests(unittest.TestCase):
    def test_derives_gates_tools_and_requires_verified_cleanup(self):
        report = {"completed": True, "cleanupVerified": True,
                  "profiles": [{"profile": "unified-comfort", "gates": {"archiveRecovery": {"status": "passed"}, "compile": {"status": "failed"}}}],
                  "launcherShutdown": {"toolCalls": [{"name": "project_retrieve", "isError": False}]}}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bridge.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            derived = derive([path])
            self.assertTrue(derived["completed"])
            self.assertEqual([s["name"] for s in derived["steps"]], ["unified-comfort: archiveRecovery=passed", "unified-comfort: compile=failed"])
            self.assertEqual(derived["toolCalls"], [{"name": "project_retrieve", "isError": False}])
            report["cleanupVerified"] = False
            path.write_text(json.dumps(report), encoding="utf-8")
            self.assertFalse(derive([path])["completed"])


if __name__ == "__main__":
    unittest.main()
