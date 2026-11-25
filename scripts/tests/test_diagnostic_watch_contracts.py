"""Offline watch contracts refuse implicit identity and native qualification claims."""
import copy
import unittest
from jsonschema import ValidationError
from qualify_trace_diagnostics_offline import watch_contracts


class DiagnosticWatchContracts(unittest.TestCase):
    def test_request_requires_explicit_version_and_bounded_intent(self):
        validator = watch_contracts()['input']
        valid = dict(planId='a'*64, request=dict(tableName='Conveyor diagnosis', tiaVersion=21))
        validator.validate(valid)
        for field, value in [('tiaVersion',None), ('tiaVersion',0), ('tiaVersion',101), ('maxRows',0), ('maxRows',65),
                             ('externalStorage',[dict(objectPath='/external')]), ('nativeExecutable',True)]:
            changed=copy.deepcopy(valid); changed['request'][field]=value
            with self.assertRaises(ValidationError): validator.validate(changed)
        changed=copy.deepcopy(valid); del changed['request']['tiaVersion']
        with self.assertRaises(ValidationError): validator.validate(changed)

    def test_summary_cannot_upgrade_evidence_or_native_eligibility(self):
        receipt = dict(id='a'*64, schemaVersion='diagnostic-watch-plan.v1', caseId='b'*64, reviewId='c'*64,
                       artifact=dict(path='retained/plan.json',sha256='a'*64,bytes=100), planSha256='d'*64,
                       rowCount=11,candidateCount=11,unresolvedCount=0,truncated=False,
                       tiaVersionOrigin='callerSuppliedUnverified',sourceAuthenticityVerified=False,
                       sourceEvidencePolicy='Supplied observations only.',nativeAccessAttempted=False,
                       nativeExecutable=False,nativeDocumentGenerated=False,qualification='offlinePlanningOnly')
        validator=watch_contracts()['receipt']; validator.validate(receipt)
        for field in ['sourceAuthenticityVerified','nativeAccessAttempted','nativeExecutable','nativeDocumentGenerated']:
            changed=dict(receipt); changed[field]=True
            with self.assertRaises(ValidationError): validator.validate(changed)


if __name__ == '__main__': unittest.main()
