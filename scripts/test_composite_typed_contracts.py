"""Typed C02 contracts, prior-producer integrity and independent mutation controls."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import unittest

from test_composite_engineering_contracts import ROOT, VALIDATORS, sample
from composite_typed_oracle import verify_mappings, verify_selection


class TypedContracts(unittest.TestCase):
    def test_exact_selector_fields(self):
        packet = sample(); packet['schemaVersion'] = 'composite-engineering-request.v3'
        for kind in ('driveTelegram', 'driveTelegramAddress', 'technologyEncoder'):
            selector = dict(kind=kind, phase='before', recordRef='a' * 64, id='exact')
            if kind == 'driveTelegramAddress': selector['addressId'] = 'input'
            packet['mappings'] = [dict(id='typed', phase='before', purpose='telegram', reason='Explicit declaration', source=dict(kind='amlObject', phase='before', recordRef=packet['before']['automationMlRef'], path='/owner'), target=selector)]
            VALIDATORS['request-v3'].validate(packet)
            for field in list(selector):
                broken = copy.deepcopy(packet); del broken['mappings'][0]['target'][field]
                self.assertFalse(VALIDATORS['request-v3'].is_valid(broken), field)
            broken = copy.deepcopy(packet); broken['mappings'][0]['target']['extra'] = True
            self.assertFalse(VALIDATORS['request-v3'].is_valid(broken))
            for version, validator in [('v1', 'request'), ('v2', 'request-v2')]:
                broken = copy.deepcopy(packet); broken['schemaVersion'] = 'composite-engineering-request.' + version
                self.assertFalse(VALIDATORS[validator].is_valid(broken))

    def test_actual_prior_producer_files(self):
        root = ROOT / 'fixtures/composite-engineering/retained-v1-v2'
        fixture = json.loads((root / 'fixture.json').read_bytes())
        self.assertEqual({r['schemaVersion'] for r in fixture['records']}, {'composite-engineering-review.v1', 'composite-engineering-review.v2'})
        self.assertEqual({p.relative_to(root / 'store').as_posix() for p in (root / 'store').rglob('*') if p.is_file()}, {r['path'] for r in fixture['files']})
        for row in fixture['files']:
            data = (root / 'store' / row['path']).read_bytes()
            self.assertEqual((len(data), hashlib.sha256(data).hexdigest()), (row['bytes'], row['sha256']))


def verify_cases(path):
    fixture = json.loads((path / 'mcp-fixture.json').read_bytes()); root = Path(fixture['artifactRoot'])
    cases = json.loads((path / 'cases.json').read_bytes())
    for case in cases:
        VALIDATORS['request-v3'].validate(case['request']); VALIDATORS['review-v3'].validate(case['report'])
        for selection in case['report']['selections']:
            if selection['selector']['kind'] in ('driveTelegram', 'driveTelegramAddress', 'technologyEncoder') and selection['evidence'] is not None:
                verify_selection(root, case['request'], selection)
    request = json.loads((path / 'request.json').read_bytes()); report = json.loads((path / 'report.json').read_bytes())
    verify_mappings(request, report)
    negative = 0
    for field in ('recordId', 'phase', 'telegramId'):
        changed = copy.deepcopy(report)
        row = next(s for s in changed['selections'] if s['selector']['kind'] == 'technologyEncoder')
        row['evidence']['encoder']['target'][field] = 'altered'
        try: verify_mappings(request, changed)
        except AssertionError: negative += 1
        else: raise AssertionError('Changed original reference accepted: ' + field)
    for field in ('startBit', 'lengthBits', 'endBitExclusive'):
        changed = copy.deepcopy(next(s for s in report['selections'] if s['selector']['kind'] == 'driveTelegram'))
        changed['evidence']['addresses'][0][field] += 1
        try: verify_selection(root, request, changed)
        except AssertionError: negative += 1
        else: raise AssertionError('Changed normalized unit accepted: ' + field)
    result = dict(completed=True, nativeExecuted=False, cases=len(cases), negativeControls=negative)
    (path / 'independent-verification.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(f'PASS typed composite schemas and original-input oracle: {len(cases)} cases, {negative} negative controls.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('--fixture', type=Path)
    args, rest = parser.parse_known_args()
    if args.fixture: verify_cases(args.fixture)
    else: unittest.main(argv=[__file__, *rest])
