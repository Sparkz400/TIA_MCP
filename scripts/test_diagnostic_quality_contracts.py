"""Independent v2 contract boundaries and intentionally incomplete observations."""
import copy
import unittest

from jsonschema import ValidationError
from qualify_diagnostic_quality_offline import validators


class DiagnosticQualityContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schemas = validators()

    def fixture(self):
        return dict(schemaVersion="trace-measurement.v2", tracePlanId="a"*64, reviewId="b"*64,
                    mode="synthetic", programIdentity="unverified supplied source",
                    clocks=[dict(id="source", label="supplied device clock", kind="source", timeUnit="ns")],
                    quality=dict(atomicAcrossSignals=False, scanSynchronized=False, programMatchVerified=False),
                    capture=dict(state="truncated", reason="Acquisition stopped", reportedDroppedSamples=None),
                    expectedIntervals=[], units=dict(signal="1"), samples=[dict(sequence=0, readings=dict(signal=dict(
                        value=None, status="missing", statusCode="raw status", error="transport lost",
                        timestamps=[dict(clockId="source", epoch="boot-1", value=None)])))])

    def test_incomplete_is_representable(self):
        for alter in (lambda c: None, lambda c: c.update(samples=[]),
                      lambda c: c["samples"][0].update(readings={}),
                      lambda c: c["samples"][0]["readings"]["signal"].update(timestamps=[]),
                      lambda c: c["samples"][0]["readings"]["signal"].update(status="bad", value="undecodable raw"),
                      lambda c: c["samples"][0]["readings"]["signal"]["timestamps"][0].update(value=-17)):
            value = self.fixture()
            alter(value)
            self.schemas["trace-measurement.v2"].validate(value)

    def test_explicit_fields_and_limits(self):
        schema = self.schemas["trace-measurement.v2"]
        original = self.fixture()
        for field in original:
            value = copy.deepcopy(original)
            del value[field]
            with self.subTest(field=field), self.assertRaises(ValidationError):
                schema.validate(value)
        for alter in (lambda c: c.update(ignored=True), lambda c: c.update(schemaVersion="trace-measurement.v1"),
                      lambda c: c["samples"][0]["readings"]["signal"].update(status="invented"),
                      lambda c: c["samples"][0]["readings"]["signal"].update(value={}),
                      lambda c: c["samples"][0]["readings"]["signal"].update(status="good", value="42"),
                      lambda c: c["capture"].update(reportedDroppedSamples=-1),
                      lambda c: c.update(clocks=[]), lambda c: c.update(samples=c["samples"]*10001),
                      lambda c: c["clocks"][0].update(timeUnit="ticks"),
                      lambda c: c["samples"][0]["readings"]["signal"]["timestamps"][0].update(value=10**18+1)):
            value = copy.deepcopy(original)
            alter(value)
            with self.assertRaises(ValidationError):
                schema.validate(value)

    def test_polling_cannot_attest_scan_atomicity(self):
        value = self.fixture()
        value["mode"] = "polling"
        self.schemas["trace-measurement.v2"].validate(value)
        for field in ("atomicAcrossSignals", "scanSynchronized"):
            invalid = copy.deepcopy(value)
            invalid["quality"][field] = True
            with self.assertRaises(ValidationError):
                self.schemas["trace-measurement.v2"].validate(invalid)

    def test_v2_is_registered_as_strict_versioned_contracts(self):
        for name in ("trace-import.v2", "diagnostic-report.v2", "trace-comparison.v2",
                     "diagnostic-experiment-review.v2", "diagnostic-experiment-repeat.v2",
                     "diagnostic-quality-report.v2", "diagnostic-quality-report-export.v2"):
            schema = self.schemas[name].schema
            self.assertIs(schema["additionalProperties"], False)
            self.assertEqual(schema["properties"]["schemaVersion"]["const"], name)
            if "behavioralAuthority" in schema["properties"]:
                self.assertIs(schema["properties"]["behavioralAuthority"]["const"], False)


if __name__ == "__main__":
    unittest.main()
