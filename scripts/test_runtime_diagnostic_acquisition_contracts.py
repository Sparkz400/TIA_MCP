"""Acquisition requests cannot attest away runtime uncertainty or change signal identity."""
import copy
import unittest
from jsonschema import ValidationError
from qualify_runtime_diagnostic_acquisition_offline import validators


class RuntimeDiagnosticAcquisitionContracts(unittest.TestCase):
    def test_exact_request_and_false_claim_boundaries(self):
        valid = dict(experimentId="a" * 64, snapshotRef="capture:" + "b" * 32 + ":1:" + "c" * 64,
                     request=dict(reason="Retained observations", units=dict(enable="1", step="count")))
        schema = validators()["input"]
        schema.validate(valid)
        for key in ("programMatchVerified", "atomicAcrossSignals", "scanSynchronized", "quality", "signalMap"):
            changed = copy.deepcopy(valid)
            changed["request"][key] = True
            with self.assertRaises(ValidationError):
                schema.validate(changed)
        for value in ({}, dict(enable=1), dict(enable=""), dict(enable="x" * 65)):
            changed = copy.deepcopy(valid)
            changed["request"]["units"] = value
            with self.assertRaises(ValidationError):
                schema.validate(changed)

    def test_export_only_exact_id_and_inert_formats(self):
        schema = validators()["exportInput"]
        for fmt in ("json", "markdown", "html"):
            schema.validate(dict(acquisitionId="a" * 64, format=fmt))
        for invalid in (dict(acquisitionId="../input", format="json"),
                        dict(acquisitionId="a" * 64, format="script"),
                        dict(acquisitionId="a" * 64, format="html", path="elsewhere")):
            with self.assertRaises(ValidationError):
                schema.validate(invalid)


if __name__ == "__main__":
    unittest.main()
