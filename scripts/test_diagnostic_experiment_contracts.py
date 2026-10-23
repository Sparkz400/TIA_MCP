"""Contracts keep diagnostic experiment intent and offline evidence limits explicit."""
import copy
import unittest

from jsonschema import ValidationError
from qualify_diagnostic_experiments_offline import validators


class DiagnosticExperimentContracts(unittest.TestCase):
    def test_plan_requires_both_justified_handoffs(self):
        valid = dict(planId="a" * 64, request=dict(reason="Observe command loss.",
                     watch=dict(tableName="Conveyor", tiaVersion=21),
                     trace=dict(backend="manual", family="ordinaryPlc", reviewId="b" * 64,
                                settings=dict(task="Main", rationale="Capture signal edge.", sampleEvery=1,
                                              preSamples=10, postSamples=10,
                                              trigger=dict(signalId="run", kind="fallingEdge")))))
        validator = validators()["planInput"]
        validator.validate(valid)
        for missing in ("reason", "watch", "trace"):
            changed = copy.deepcopy(valid)
            del changed["request"][missing]
            with self.assertRaises(ValidationError):
                validator.validate(changed)
        changed = copy.deepcopy(valid)
        changed["request"]["nativeExecutable"] = True
        with self.assertRaises(ValidationError):
            validator.validate(changed)

    def test_review_requires_explicit_comparison_and_exact_ids(self):
        valid = dict(experimentId="a" * 64, request=dict(afterExperimentId="b" * 64,
                     beforeMeasurementId="c" * 64, afterMeasurementId="d" * 64,
                     comparison=dict(reason="Selected event window.", beforeAnchorSeconds=0,
                                     afterAnchorSeconds=0, windowStartSeconds=-1, windowEndSeconds=1)))
        validator = validators()["reviewInput"]
        validator.validate(valid)
        for missing in valid["request"]:
            changed = copy.deepcopy(valid)
            del changed["request"][missing]
            with self.assertRaises(ValidationError):
                validator.validate(changed)
        for field, value in (("signalMaps", []), ("allowChangedSource", "true")):
            changed = copy.deepcopy(valid)
            changed["request"]["comparison"][field] = value
            with self.assertRaises(ValidationError):
                validator.validate(changed)


if __name__ == "__main__":
    unittest.main()
