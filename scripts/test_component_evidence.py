import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from component_evidence import derive


class ComponentEvidenceTests(unittest.TestCase):
    def test_binds_reports_and_lists_tools_flags_and_states(self):
        with tempfile.TemporaryDirectory() as folder:
            install = Path(folder, "install.json"); runtime = Path(folder, "runtime.json")
            install.write_text(json.dumps({"status": "passed", "sourceMode": "author_new_source", "preservationVerified": True,
                "events": [{"event": "response", "value": {"state": "library_applied"}}],
                "sessions": [{"calls": [{"tool": "tia_start", "status": "passed"}]}]}))
            runtime.write_text(json.dumps({"status": "passed", "kind": "plc_component_runtime", "runtimeVerified": True,
                "calls": [{"tool": "sim_test", "status": "passed"}, {"tool": "sim_release", "status": "failed"}]}))
            derived = derive([install, runtime])
            self.assertTrue(derived["completed"])
            self.assertEqual(derived["sourceReports"][0]["sha256"], hashlib.sha256(install.read_bytes()).hexdigest())
            names = [s["name"] for s in derived["steps"]]
            self.assertIn("author_new_source: preservationVerified=True", names)
            self.assertIn("author_new_source: response=library_applied", names)
            self.assertIn("plc_component_runtime: runtimeVerified=True", names)
            self.assertIn({"name": "sim_release", "isError": True}, derived["toolCalls"])
            runtime.write_text(json.dumps({"status": "failed"}))
            self.assertFalse(derive([install, runtime])["completed"])


if __name__ == "__main__":
    unittest.main()
