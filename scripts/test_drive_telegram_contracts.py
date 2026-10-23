"""Independent original-row, unit-arithmetic and schema checks for supplied telegrams."""
import argparse
import json
from pathlib import Path
import unittest
import unicodedata
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT / 'contracts/drive-profile-review.v2.schema.json').read_text(encoding='utf-8'))


def validate(value, kind):
    Draft202012Validator({**SCHEMA, '$ref': '#/$defs/' + kind}).validate(value)


def address():
    return dict(id='input', plcPath='/p/plc', direction='Input', context='declared', startAddress=256,
                addressUnit='byte', length=64, lengthUnit='bit', unknownReason=None)


def telegram():
    return dict(id='t', type='MainTelegram', number=81, inputBytes=8, outputBytes=4,
                addressesComplete=True, addresses=[address()], unknownReason=None)


class TelegramContracts(unittest.TestCase):
    def test_schema_and_typed_rows(self):
        Draft202012Validator.check_schema(SCHEMA)
        validate(telegram(), 'telegram')

    def test_unknown_requires_reason(self):
        for field in ['type', 'number', 'inputBytes', 'outputBytes']:
            value = telegram(); value[field] = None
            self.assertFalse(Draft202012Validator({**SCHEMA, '$ref': '#/$defs/telegram'}).is_valid(value))
            value['unknownReason'] = 'Not observed'; validate(value, 'telegram')
        value = address(); value['addressUnit'] = 'unknown'
        self.assertFalse(Draft202012Validator({**SCHEMA, '$ref': '#/$defs/telegramAddress'}).is_valid(value))
        value['unknownReason'] = 'Unit not documented'; validate(value, 'telegramAddress')

    def test_explicit_units_and_integer_bounds(self):
        for field, bad in [('startAddress', -1), ('length', 2147483648), ('startAddress', True),
                           ('length', 1.5), ('addressUnit', 'word'), ('lengthUnit', 'Byte'), ('direction', 'input')]:
            value = address(); value[field] = bad
            self.assertFalse(Draft202012Validator({**SCHEMA, '$ref': '#/$defs/telegramAddress'}).is_valid(value))

    def test_exact_fields_and_bounds(self):
        value = telegram(); value['nativeQualified'] = True
        self.assertFalse(Draft202012Validator({**SCHEMA, '$ref': '#/$defs/telegram'}).is_valid(value))
        value = telegram(); value['addresses'] *= 17
        self.assertFalse(Draft202012Validator({**SCHEMA, '$ref': '#/$defs/telegram'}).is_valid(value))

    def test_nested_output_cannot_promote_authority(self):
        for kind, flag in [('telegramAddressObservation', 'nativeAddressVerified'),
                           ('telegramObservation', 'nativeCompatibilityVerified'),
                           ('telegramChange', 'identityEqualityVerified')]:
            self.assertEqual({'const': False}, SCHEMA['$defs'][kind]['properties'][flag])

    def test_version_is_explicit(self):
        self.assertEqual({'const': 'drive-profile-review-request.v2'}, SCHEMA['$defs']['request']['properties']['schemaVersion'])
        self.assertEqual({'const': 'drive-profile-review-record.v2'}, SCHEMA['$defs']['record']['properties']['schemaVersion'])


def verify(packet, report, review):
    validate(packet, 'request'); validate(report, 'report')
    assert report['reviewId'] == packet['reviewId'] and report['hardwarePath'] == packet['hardwarePath']
    observed = report['telegrams']; expected_count = sum(len(packet[p]['telegrams']) for p in ['before', 'after'])
    assert len(observed) == expected_count
    def bits(value, unit): return None if value is None or unit == 'unknown' else value * (8 if unit == 'byte' else 1)
    for phase in ['before', 'after']:
        assert report['coverage'][phase + 'TelegramsComplete'] == packet[phase]['telegramsComplete']
        rows = [r for r in observed if r['phase'] == phase]
        assert len(rows) == len(packet[phase]['telegrams'])
        for index, (row, original) in enumerate(zip(rows, packet[phase]['telegrams'], strict=True)):
            assert row['sourceIndex'] == index and row['telegram'] == original
            assert row['nativeCompatibilityVerified'] is False
            expected = 'unknown' if original['number'] is None else 'freelyConfigurable' if original['number'] == 999 and original['type'] == 'MainTelegram' else 'declaredNumber'
            assert row['numberSemantics'] == expected
            assert len(row['addresses']) == len(original['addresses'])
            for address_index, (item, raw) in enumerate(zip(row['addresses'], original['addresses'], strict=True)):
                assert item['sourceIndex'] == address_index and item['address'] == raw and item['nativeAddressVerified'] is False
                start, length = bits(raw['startAddress'], raw['addressUnit']), bits(raw['length'], raw['lengthUnit'])
                assert (item['startBit'], item['lengthBits'], item['endBitExclusive']) == (start, length, None if start is None or length is None else start + length)
                size = original['inputBytes'] if raw['direction'] == 'Input' else original['outputBytes'] if raw['direction'] == 'Output' else None
                assert item['directionalSizeComparison'] == ('unknown' if size is None or length is None else 'equalDeclaredSize' if length == size * 8 else 'differentDeclaredSize')
                matches = [n for n in review['model']['nodes'] if n.get('objectPath') == raw['plcPath']]
                key = matches[0].get('nodeId') if len(matches) == 1 else None
                valid_id = isinstance(key,str) and 0 < len(key) <= 8192 and key.strip() and not any(unicodedata.category(c) == 'Cc' or 0xd800 <= ord(c) <= 0xdfff for c in key)
                state = ('notDeclared' if raw['plcPath'] is None else 'missing' if not matches else 'ambiguousIdentity' if len(matches) != 1 else
                         'invalidIdentity' if not valid_id else 'ambiguousIdentity' if sum(n.get('nodeId') == key for n in review['model']['nodes']) != 1 else
                         'virtualObject' if matches[0].get('origin') == 'virtual' else 'capturedPlc' if matches[0]['kind'] == 'plc' else 'wrongKind')
                assert item['plcResolution'] == state
    left, right = ({r['id']: r for r in packet[p]['telegrams']} for p in ['before', 'after'])
    expected = []
    for key in sorted(left.keys() | right.keys(), key=lambda value: value.encode('utf-16-be')):
        before, after = left.get(key), right.get(key); fields = []
        if before and after:
            for field in ['type', 'number', 'inputBytes', 'outputBytes', 'addressesComplete', 'addresses', 'unknownReason']:
                a, b = before[field], after[field]
                if field == 'addresses': a, b = ({r['id']: r for r in value} for value in [a, b])
                if a != b: fields.append(field)
        expected.append(dict(id=key,status='onlyAfter' if before is None else 'onlyBefore' if after is None else 'changed' if fields else 'unchanged',changedFields=fields,identityEqualityVerified=False,nativeCompatibilityVerified=False))
    assert report['telegramChanges'] == expected
    assert report['summary']['changedTelegramCount'] == sum(row['status'] != 'unchanged' for row in expected)
    return dict(completed=True,nativeExecuted=False,originalRowsChecked=True,independentUnitArithmetic=True,telegrams=len(observed))


if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--fixture',type=Path);args=parser.parse_args()
    if args.fixture:
        root=args.fixture; packet=json.loads((root/'request.json').read_text(encoding='utf-8'))
        report=json.loads((root/'report.json').read_text(encoding='utf-8'))
        review=json.loads((root/'project-reviews'/(packet['reviewId']+'.json')).read_text(encoding='utf-8'))
        result=verify(packet,report,review)
        (root/'independent-verification.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
        print('PASS independently checked typed telegram original rows, units, changes and schemas.')
    else: unittest.main(argv=[__file__])
