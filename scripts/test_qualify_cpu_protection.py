"""Offline checks of the pure helpers in qualify_cpu_protection.py; nothing here starts TIA."""
import pathlib
import unittest

import qualify_cpu_protection as q

ROOT = pathlib.Path(__file__).resolve().parents[1]


class CpuProtectionRunnerTests(unittest.TestCase):
    def test_binding_matches_the_worker_derivation(self):
        worker = (ROOT / "src/TiaMcp.Openness.V21/OpennessSession.CpuParameters.cs").read_text(encoding="utf-8")
        shared = (ROOT / "src/TiaMcp.Openness.V21/RoutedCredentials.cs").read_text(encoding="utf-8")
        binding = q.binding("/p/d/items/CPU/plc/PLC", "/p/d/items/CPU")
        self.assertEqual({"provider", "authority", "ownerId", "resourceKind", "resourceId"}, set(binding["resource"]))
        self.assertIn('Binding(item.Path + "/plc/" + Segment(plc.Name), item.Path, "cpuProtection", "fullAccess", "fullAccessPassword")', worker)
        self.assertIn('["provider"] = "' + binding["resource"]["provider"] + '"', shared)
        self.assertIn('["resourceKind"] = purpose', shared)
        self.assertEqual(binding["purpose"], binding["resource"]["resourceKind"])

    def test_host_routes_the_purpose(self):
        host = (ROOT / "src/TiaMcp.Host/Security/CredentialRoute.cs").read_text(encoding="utf-8")
        self.assertIn('"cpuProtection"', host)


if __name__ == "__main__":
    unittest.main()
