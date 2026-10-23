"""Owner-context schema checks; OS observations never qualify a native Add-In."""
import argparse
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator, FormatChecker, ValidationError

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT/'contracts/addin-owner-context.v1.schema.json').read_text(encoding='utf-8'))
VALIDATOR = Draft202012Validator(SCHEMA, format_checker=FormatChecker())


def sample():
    return dict(schemaVersion='addin-owner-context.v1', workerInstanceId='a'*32, workerProcessId=12,
        sessionGeneration='b'*32, ownsPortalProcess=False, state='available', reason=None,
        process=dict(processId=16, startedAtUtc='2026-10-18T01:00:00.0000000Z', executablePath=r'C:\Fixture\process.exe', windowsSessionId=1),
        osProcessIdentityVerified=True, nativeProcessIdMatched=True, producerAuthenticated=False, liveSelectionVerified=False, nativeQualified=False)


class AddInOwnerContracts(unittest.TestCase):
    def test_available_context_is_separate_from_native_authority(self):
        Draft202012Validator.check_schema(SCHEMA)
        VALIDATOR.validate(sample())

    def test_unavailable_retains_historical_identity_without_authority(self):
        for reason in ['ProcessChanged', 'ProcessExited', 'Disposed', 'CaptureUnavailable', 'IdentityQueryUnavailable', 'NotAttached']:
            row=sample(); row.update(state='unavailable', reason=reason, osProcessIdentityVerified=False, nativeProcessIdMatched=False)
            VALIDATOR.validate(row); row['process']=None; VALIDATOR.validate(row)

    def test_contradictory_available_and_unavailable_states_refuse(self):
        for patch in [dict(reason='ProcessExited'), dict(process=None), dict(osProcessIdentityVerified=False),
                      dict(nativeProcessIdMatched=False), dict(state='unavailable')]:
            row=sample(); row.update(patch)
            with self.subTest(patch=patch), self.assertRaises(ValidationError): VALIDATOR.validate(row)

    def test_native_authority_promotion_refuses(self):
        for field in ['producerAuthenticated', 'liveSelectionVerified', 'nativeQualified']:
            row=sample(); row[field]=True
            with self.subTest(field=field), self.assertRaises(ValidationError): VALIDATOR.validate(row)

    def test_missing_and_unknown_fields_refuse(self):
        for field in sample():
            row=sample(); del row[field]
            with self.subTest(field=field), self.assertRaises(ValidationError): VALIDATOR.validate(row)
        for path in [[], ['process']]:
            row=sample(); target=row if not path else row['process']; target['extra']='untrusted'
            with self.assertRaises(ValidationError): VALIDATOR.validate(row)

    def test_exact_process_generation_and_time_shapes(self):
        for field,value in [('workerProcessId',0),('workerProcessId',True),('workerInstanceId','A'*32),('sessionGeneration','a'*31)]:
            row=sample(); row[field]=value
            with self.subTest(field=field,value=value), self.assertRaises(ValidationError): VALIDATOR.validate(row)
        for field,value in [('processId',-1),('processId',True),('startedAtUtc','2026-10-18T02:00:00+01:00'),('executablePath',''),('windowsSessionId',-1)]:
            row=sample(); row['process'][field]=value
            with self.subTest(field=field,value=value), self.assertRaises(ValidationError): VALIDATOR.validate(row)


def inspect_fixture(path):
    report=json.loads(path.read_text(encoding='utf-8-sig'))
    assert report['completed'] and report['nativeExecuted'] is False and report['nativeIdRoute']=='synthetic'
    assert report['clrVersion'].startswith('4.') and report['mscorlibVersion'].startswith('4.8.')
    for label in ['available','disposed','captureUnavailable','detached']: VALIDATOR.validate(report[label])
    before,after=report['available'],report['disposed']
    assert before['process']==after['process'] and before['workerInstanceId']==after['workerInstanceId']
    assert before['state']=='available' and after['state']=='unavailable' and after['reason']=='Disposed'
    assert report['captureUnavailable']['process'] is None and report['detached']['process'] is None
    print('PASS independent schema/identity checks over four actual .NET Framework owner contexts; native ID route synthetic.')


if __name__=='__main__':
    parser=argparse.ArgumentParser(); parser.add_argument('--fixture', type=Path); args=parser.parse_args()
    if args.fixture: inspect_fixture(args.fixture)
    else: unittest.main(argv=['test_addin_owner_context'])
