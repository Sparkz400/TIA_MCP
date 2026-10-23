import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from runtime_capture_checks import validator, last_read

ROOT=Path(__file__).resolve().parents[1]


class RuntimeCaptureReadSelection(unittest.TestCase):
    def test_trailing_gaps_do_not_replace_the_final_read_error(self):
        for error in ['CredentialRetired', 'RuntimeCaptureHandoffUnavailable']:
            row={'kind':'read','errorCode':error}
            self.assertIs(last_read([row, {'kind':'gap','errorCode':None}, {'kind':'gap','errorCode':None}]), row)

    def test_later_success_cannot_be_hidden_by_an_earlier_error(self):
        rows=[{'kind':'read','errorCode':'CredentialRetired'}, {'kind':'read','errorCode':None}, {'kind':'gap','errorCode':None}]
        self.assertIsNone(last_read(rows)['errorCode'])

    def test_last_error_is_not_replaced_by_an_earlier_expected_error(self):
        rows=[{'kind':'read','errorCode':'CredentialRetired'}, {'kind':'read','errorCode':'RuntimeTimeout'}, {'kind':'gap','errorCode':None}]
        self.assertEqual('RuntimeTimeout', last_read(rows)['errorCode'])

    def test_no_read_is_not_success(self):
        for rows in [[], [{'kind':'gap','errorCode':None}]]:
            with self.assertRaisesRegex(AssertionError, 'no read observation'):
                last_read(rows)


class RuntimeCaptureContracts(unittest.TestCase):
    def setUp(self):
        registry=Registry()
        for name in ['physical-runtime','diagnostic-case','diagnostic-signal-manifest','diagnostic-alarm-bindings','runtime-capture']:
            schema=json.loads((ROOT/'contracts'/f'{name}.v1.schema.json').read_text())
            Draft202012Validator.check_schema(schema)
            registry=registry.with_resource(schema['$id'],Resource.from_contents(schema))
        self.validator=Draft202012Validator(schema,registry=registry)
        self.document=json.loads((ROOT/'fixtures/physical-runtime/capture-portable.json').read_text())

    def test_loopback_export_validates(self):
        self.validator.validate(self.document)
        self.assertEqual(json.loads(self.document['rows'][0]['readResultJson'])['value'],2**64-1)

    def test_explicit_association_never_promotes_evidence(self):
        associated=json.loads((ROOT/'fixtures/physical-runtime/capture-associated.json').read_text())
        self.validator.validate(associated)
        for field in ['addressMatchVerified','programMatchVerified','sourceAuthenticityVerified','nativeQualified']:
            changed=copy.deepcopy(associated);changed['capture']['plan']['association'][field]=True
            self.assertFalse(self.validator.is_valid(changed),field)
        changed=copy.deepcopy(associated);changed['capture']['plan']['association']['signals'][0]['declaration']['resolution']='unavailable'
        self.assertFalse(self.validator.is_valid(changed))

    def test_claims_and_limits_are_not_promoted(self):
        for field in ['nativeQualified','programMatchVerified','atomicAcrossSignals','scanSynchronized']:
            changed=copy.deepcopy(self.document);changed['capture'][field]=True
            self.assertFalse(self.validator.is_valid(changed),field)
        for field,value in [('intervalMilliseconds',99),('maximumSamples',100001),('maximumBytes',67108865)]:
            changed=copy.deepcopy(self.document);changed['capture']['plan']['request'][field]=value
            self.assertFalse(self.validator.is_valid(changed),field)

    def test_alarm_capture_retains_unselected_bindings_without_runtime_authority(self):
        document=json.loads((ROOT/'fixtures/physical-runtime/capture-alarm.json').read_text(encoding='utf-8'))
        self.validator.validate(document)
        association=document['capture']['plan']['association']
        self.assertEqual(1,len(association['signals']))
        self.assertEqual(['enable','enableSource','final'],[b['signalId'] for b in association['alarmBindings']['bindings']])
        for field in ['nativeQualified','sourceAuthenticityVerified','runtimeAlarmAssociationVerified','coverageComplete']:
            for value in [True,None,'false']:
                changed=copy.deepcopy(document);changed['capture']['plan']['association']['alarmBindings'][field]=value
                self.assertFalse(self.validator.is_valid(changed),field)
        changed=copy.deepcopy(document);changed['capture']['plan']['association']['alarmBindings']=None
        self.assertFalse(self.validator.is_valid(changed))
        for key,value in [('allSelectedMatched',False),('bindings',[])]:
            changed=copy.deepcopy(document);changed['capture']['plan']['association']['alarmBindings'][key]=value
            self.assertFalse(self.validator.is_valid(changed),key)
        changed=copy.deepcopy(document);changed['capture']['plan']['association']['alarmBindings']['bindings'][0]['status']='storageMismatch'
        self.assertFalse(self.validator.is_valid(changed))

    def test_handoff_shape_and_refusal_records(self):
        bound=json.loads((ROOT/'fixtures/physical-runtime/capture-handoff.json').read_text())
        self.validator.validate(bound)
        for field in ['deviceIdentityVerified','programMatchVerified','effectiveRightsVerified','sourceAuthenticityVerified','operationAdmitted','assertionsAdmitted']:
            for value in [True,None,'false']:
                changed=copy.deepcopy(bound);changed['capture']['plan']['handoff'][field]=value
                self.assertFalse(self.validator.is_valid(changed),field)
        for parent,key in [('plan','handoff'),('request','handoffRef'),('request','reviewId')]:
            changed=copy.deepcopy(bound);target=changed['capture']['plan']
            if parent=='request':target=target['request']
            del target[key];self.assertFalse(self.validator.is_valid(changed),key)
        for code in ['RuntimeCaptureHandoffMismatch','RuntimeCaptureHandoffUnavailable']:
            changed=copy.deepcopy(bound);changed['rows'][0].update(readResultJson=None,errorCode=code)
            self.validator.validate(changed)
        report=json.loads((ROOT/'fixtures/physical-runtime/capture-analysis.json').read_text())
        report['handoff']=copy.deepcopy(bound['capture']['plan']['handoff'])
        report['capture']['request']['handoffRef']=report['handoff']['recordRef']
        analysis=validator('runtime-capture-analysis');analysis.validate(report)
        missing=copy.deepcopy(report);del missing['handoff'];self.assertFalse(analysis.is_valid(missing))
        missing=copy.deepcopy(report);del missing['capture']['request']['handoffRef'];self.assertFalse(analysis.is_valid(missing))

    def test_errors_and_gaps_remain_distinct(self):
        changed=copy.deepcopy(self.document);row=changed['rows'][0]
        row.update(readResultJson=None,errorCode='RuntimeTimeout');self.validator.validate(changed)
        row['readResultJson']='{}';self.assertFalse(self.validator.is_valid(changed))
        row.update(kind='gap',selectionId=None,readResultJson=None,errorCode=None,skippedPolls=3);self.validator.validate(changed)
        row['skippedPolls']=0;self.assertFalse(self.validator.is_valid(changed))

    def test_capture_analysis_cannot_promote_behavior_or_hide_timing(self):
        report=json.loads((ROOT/'fixtures/physical-runtime/capture-analysis.json').read_text())
        validate=validator('runtime-capture-analysis');validate.validate(report)
        for field in ['nativeQualification','programMatchVerified','addressMatchVerified','atomicAcrossSignals','scanSynchronized','sourceAuthenticityVerified','clockSynchronizationVerified','causationProven']:
            changed=copy.deepcopy(report);changed[field]=True
            self.assertFalse(validate.is_valid(changed),field)
        changed=copy.deepcopy(report);changed['hypotheses'][1]['verdict']='supportedPrediction'
        self.assertFalse(validate.is_valid(changed))
        changed=copy.deepcopy(report);del changed['hypotheses'][1]['examples'][0]['reads']['enable']['elapsedMilliseconds']
        self.assertFalse(validate.is_valid(changed))
        changed=copy.deepcopy(report);changed['hypotheses'][1]['prediction']=None
        validate.validate(changed)


if __name__=='__main__':unittest.main()
