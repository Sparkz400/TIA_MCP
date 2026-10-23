"""Run the actual evidence regressions with both Python optimization modes."""
import os
import subprocess
import sys
import unittest
from pathlib import Path


class QualificationOptimizationTests(unittest.TestCase):
    def test_native_evidence_guards_survive_optimization(self):
        for option in ('-O', '-OO'):
            with self.subTest(option=option):
                result = subprocess.run([sys.executable, option, '-m', 'unittest',
                    'test_encoder_hardware_qualification', 'test_engineering_preservation', 'test_native_evidence_guards',
                    'test_encoder_lifecycle', 'test_component_exact_lifetime', 'test_compilation_evidence', 'test_encoder_checkpoint',
                    'test_safety_qualification', 'test_safety_lifecycle', 'test_safety_recovery_evidence', 'test_safety_scratch_admission'],
                    cwd=Path(__file__).resolve().parent, env={**os.environ, 'PYTHONDONTWRITEBYTECODE': '1'},
                    # This bounds all 11 filesystem-heavy suites together. A measured
                    # passing workstation run takes 70 seconds; allow scheduling/I/O
                    # headroom without changing any native-operation deadlines.
                    capture_output=True, text=True, encoding='utf-8', timeout=180)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
