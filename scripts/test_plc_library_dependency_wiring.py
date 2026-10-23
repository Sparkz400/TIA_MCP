"""Source-only capture wiring; this does not compile the Siemens caller."""
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]


class PlcLibraryDependencyWiring(unittest.TestCase):
    def test_block_and_type_share_one_capture_budget(self):
        source=(ROOT/'src/TiaMcp.Openness.V21/OpennessSession.ProjectModel.cs').read_text(encoding='utf-8')
        self.assertEqual(1,source.count('readonly ScanBudget LibraryDependencyBudget = new ScanBudget(20000);'))
        self.assertIn('PlcLibraryInstanceCapture.Read(block, capture.LibraryDependencyBudget.Take)',source)
        self.assertIn('PlcLibraryInstanceCapture.Read(type, capture.LibraryDependencyBudget.Take)',source)
        self.assertEqual(2,source.count('capture.LibraryDependencyBudget.Take'))

    def test_dependency_limits_and_partial_coverage_are_exposed(self):
        source=(ROOT/'src/TiaMcp.Openness.V21/OpennessSession.ProjectModel.cs').read_text(encoding='utf-8')
        self.assertIn('["maxLibraryDependencyReadOperations"] = 20000',source)
        self.assertIn('["libraryDependencyReadOperations"] = capture.LibraryDependencyBudget.Used',source)
        self.assertIn('["libraryDependencyBudgetExhausted"] = capture.LibraryDependencyBudget.Truncated',source)
        self.assertIn('ModelCoverage("plcLibraryDependencies", root, "partial"',source)


if __name__=='__main__':unittest.main()
