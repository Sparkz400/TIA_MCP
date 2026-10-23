"""Validate portable Web protocol results; no network, Siemens or live identity claim."""
import argparse
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator, FormatChecker

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT / 'contracts/physical-runtime.v1.schema.json').read_text(encoding='utf-8'))
VALIDATOR = Draft202012Validator(SCHEMA, format_checker=FormatChecker())


def sample():
    return dict(schemaVersion='physical-runtime-web-read.v1', selection=dict(
        context=dict(endpoint='https://plc.example', connectionId='1'*32, epoch='2'*32, peerCertificateSha256='3'*64),
        symbol='"MyDB".Counter', representation='simple', metadataSha256='5'*64),
        requestId='4'*32, status='read', value=18446744073709551615, providerErrorCode=None,
        requestStartedUtc='2026-10-17T10:00:00Z', responseReceivedUtc='2026-10-17T10:00:00.020Z',
        elapsedMilliseconds=20, valueEncoding='siemens-web-api-json', clock='hostUtcAndMonotonicDuration',
        hostClockWentBackwards=False, sourceTimestamp=None, serverTimestamp=None,
        atomicAcrossSignals=False, scanSynchronized=False, deviceIdentityVerified=False,
        programMatchVerified=False, nativeQualified=False)


class PhysicalRuntimeContracts(unittest.TestCase):
    def test_schema_and_exact_values(self):
        Draft202012Validator.check_schema(SCHEMA)
        VALIDATOR.validate(sample())
        for value in [-(2**63), 2**64-1, None, {'futureType': [True, 'x', 0]}]:
            row = sample(); row['value'] = value
            VALIDATOR.validate(row)
            self.assertEqual(json.loads(json.dumps(row))['value'], value)

    def test_refuses_manufactured_native_evidence(self):
        for field in ['atomicAcrossSignals','scanSynchronized','deviceIdentityVerified','programMatchVerified','nativeQualified']:
            row=sample(); row[field]=True
            self.assertFalse(VALIDATOR.is_valid(row), field)
        row=sample(); row['sourceTimestamp']='2026-10-17T10:00:00Z'
        self.assertFalse(VALIDATOR.is_valid(row))

    def test_error_cannot_retain_a_good_value(self):
        row=sample(); row.update(status='permissionDenied', providerErrorCode=2, value=None)
        VALIDATOR.validate(row)
        row['value']=42
        self.assertFalse(VALIDATOR.is_valid(row))
        row=sample(); row['providerErrorCode']=2
        self.assertFalse(VALIDATOR.is_valid(row))

    def test_raw_bytes_and_scope(self):
        row=sample(); row['selection']['representation']='raw'; row['value']=[0,255]
        VALIDATOR.validate(row)
        for value in [[-1],[256],[1.5],[True],None,'abc']:
            row['value']=value
            self.assertFalse(VALIDATOR.is_valid(row))
        for endpoint in ['http://plc.example','https://user:pass@plc.example','https://plc.example/path','https://plc.example?secret=x','https://plc.example\\x','https://plc.example\n']:
            row=sample();row['selection']['context']['endpoint']=endpoint
            self.assertFalse(VALIDATOR.is_valid(row),endpoint)
        row=sample();row['selection']['context']['epoch']='short'
        self.assertFalse(VALIDATOR.is_valid(row))


def verify_managed(path):
    data=json.loads(Path(path).read_text(encoding='utf-8'))
    assert data['schemaVersion']=='runtime-contract-test-output.v1' and data['nativeExecuted'] is False
    rows=data['reads'];assert len(rows)==4
    for row in rows: VALIDATOR.validate(row)
    assert rows[0]['value']==18446744073709551615 and rows[1]['value']==-9223372036854775808
    assert rows[0]['selection']['symbol']=='"MyDB".Counter'
    assert rows[0]['requestStartedUtc'].startswith('2026-10-17T10:00:00')
    assert rows[2]['hostClockWentBackwards'] is True and rows[2]['elapsedMilliseconds']==20
    assert rows[3]['value']==[0,255,17] and rows[3]['selection']['representation']=='raw'
    print('PASS independent Python schema/value/time checks over four actual managed protocol results; no native acceptance.')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--fixture')
    args=parser.parse_args()
    if args.fixture: verify_managed(args.fixture)
    else: unittest.main(argv=['test_physical_runtime_contracts'])
