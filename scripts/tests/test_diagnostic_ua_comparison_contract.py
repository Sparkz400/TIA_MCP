"""Comparison boundaries; produced reports are independently checked through the actual Host."""
import copy
import unittest
from qualify_diagnostic_ua_observation import validator


class UaComparisonContractTests(unittest.TestCase):
    def test_explicit_bounded_mapping_and_windows(self):
        request=dict(reason='Compare explicit windows',beforeAnchorSeconds=0,afterAnchorSeconds=0.001,windowStartSeconds=-0.01,windowEndSeconds=1,
                     signalMap=[dict(before='enable',after='enable')])
        value=dict(beforeSnapshotRef='capture:'+('a'*32)+':2:'+('b'*64),afterSnapshotRef='ua-subscription:'+('c'*32)+':3:'+('d'*64),request=request)
        check=validator('diagnostic-ua-comparison','#/$defs/input');check.validate(value)
        for key in request:
            changed=copy.deepcopy(value);del changed['request'][key];self.assertFalse(check.is_valid(changed))
        for change in [lambda d:d['request'].update(signalMap=[]),lambda d:d['request'].update(beforeAnchorSeconds=0.0001),
                       lambda d:d['request'].update(afterAnchorSeconds=1000001),lambda d:d['request'].update(extra=True),
                       lambda d:d['request']['signalMap'][0].update(unknown=True),lambda d:d.update(beforeSnapshotRef='current'),
                       lambda d:d['request'].update(allowChangedSource='true')]:
            changed=copy.deepcopy(value);change(changed);self.assertFalse(check.is_valid(changed))

    def test_export_does_not_promote_authority(self):
        check=validator('diagnostic-ua-comparison-export')
        value=dict(schemaVersion='diagnostic-ua-comparison-export.v1',id='a'*64,artifact=dict(path='capture.json',sha256='a'*64,bytes=1),
                   reportId='b'*64,format='json',mediaType='application/json',sourceEvidenceRevalidated=True,limits='Descriptive only')
        flags=['atomicAcrossSignals','scanSynchronized','programMatchVerified','addressMatchVerified','datatypeMatchVerified','sourceAuthenticityVerified',
               'physicalQualified','clockSynchronizationVerified','assertionsAdmitted','causationProven','bugFixProven','nativeQualification','nativeAccessAttempted','sourceCompletenessVerified']
        value.update({k:False for k in flags});check.validate(value)
        for flag in flags:
            self.assertFalse(check.is_valid({**value,flag:True}))


if __name__=='__main__':unittest.main()
