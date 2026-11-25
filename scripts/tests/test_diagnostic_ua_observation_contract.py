"""Explicit offline UA report contract boundaries; full produced documents checked by loopback qualification."""
import copy
import unittest
from qualify_diagnostic_ua_observation import validator


class UaObservationContractTests(unittest.TestCase):
    def test_exact_snapshot_required(self):
        check = validator(fragment="#/$defs/input")
        for prefix in ("capture", "ua-subscription"):
            value = dict(snapshotRef=prefix + ":" + "a" * 32 + ":2:" + "b" * 64)
            check.validate(value)
            for bad in ("current", "head", value["snapshotRef"].replace(":2:", ":02:"), value["snapshotRef"].upper(), value["snapshotRef"]+"\n"):
                self.assertFalse(check.is_valid(dict(snapshotRef=bad)))
            self.assertFalse(check.is_valid({**value, "bindingId": "a" * 64}))

    def test_export_scope_and_original_identity(self):
        value = dict(schemaVersion="diagnostic-ua-observation-export.v1",id="a"*64,
                     artifact=dict(path="retained/report.json",sha256="a"*64,bytes=1),reportId="b"*64,
                     format="json",mediaType="application/json",sourceEvidenceRevalidated=True,limits="Retained observations only")
        flags = ["atomicAcrossSignals","scanSynchronized","programMatchVerified","addressMatchVerified","datatypeMatchVerified",
                 "sourceAuthenticityVerified","physicalQualified","clockSynchronizationVerified","assertionsAdmitted",
                 "causationProven","bugFixProven","nativeQualification","nativeAccessAttempted","sourceCompletenessVerified"]
        value.update({f:False for f in flags})
        check = validator("diagnostic-ua-observation-export");check.validate(value)
        for f in flags:
            self.assertFalse(check.is_valid({**value,f:True}),f)
            missing=copy.deepcopy(value);del missing[f];self.assertFalse(check.is_valid(missing),f)
        for field in ("reportId","artifact","sourceEvidenceRevalidated"):
            missing=copy.deepcopy(value);del missing[field];self.assertFalse(check.is_valid(missing))

    def test_only_supported_export_formats(self):
        check = validator("diagnostic-ua-observation-export", "#/$defs/input")
        for fmt in ("json","markdown","html"):
            check.validate(dict(reportId="a"*64,format=fmt))
            self.assertFalse(check.is_valid(dict(reportId="a"*64+"\n",format=fmt)))
        for fmt in ("csv","HTML","pdf",None):
            self.assertFalse(check.is_valid(dict(reportId="a"*64,format=fmt)))
        self.assertFalse(check.is_valid(dict(reportId="a"*64,format="json",allowMissingSources=True)))


if __name__ == "__main__":
    unittest.main()
