"""Independent input boundaries for repeated observations and combined exports."""
import copy
import unittest

from jsonschema import ValidationError
from qualify_repeated_diagnostic_experiments_offline import validators


class RepeatedDiagnosticExperimentContracts(unittest.TestCase):
    def test_repeats_require_explicit_pairs_and_windows(self):
        pair = dict(id="one", beforeExperimentId="a" * 64, afterExperimentId="b" * 64,
                    beforeMeasurementId="c" * 64, afterMeasurementId="d" * 64,
                    comparison=dict(reason="Explicit relative window.", beforeAnchorSeconds=0,
                                    afterAnchorSeconds=0, windowStartSeconds=-1, windowEndSeconds=1))
        valid = dict(request=dict(reason="Two descriptive pairs.", pairs=[pair, dict(pair, id="two")]))
        schema = validators()["input"]
        schema.validate(valid)
        for count in (0, 1, 9):
            changed = copy.deepcopy(valid)
            changed["request"]["pairs"] = [copy.deepcopy(pair) for _ in range(count)]
            with self.assertRaises(ValidationError):
                schema.validate(changed)
        for missing in pair:
            changed = copy.deepcopy(valid)
            del changed["request"]["pairs"][0][missing]
            with self.assertRaises(ValidationError):
                schema.validate(changed)
        for field, value in (("pValue", .01), ("causationProven", True), ("independentRunsVerified", True)):
            changed = copy.deepcopy(valid)
            changed["request"][field] = value
            with self.assertRaises(ValidationError):
                schema.validate(changed)

    def test_export_has_no_paths_or_executable_format(self):
        schema = validators()["exportInput"]
        for fmt in ("json", "markdown", "html"):
            schema.validate(dict(reportId="e" * 64, format=fmt))
        for invalid in (dict(reportId="e" * 64, format="javascript"),
                        dict(reportId="../input", format="html"),
                        dict(reportId="e" * 64, format="html", outputPath="elsewhere")):
            with self.assertRaises(ValidationError):
                schema.validate(invalid)


if __name__ == "__main__":
    unittest.main()
