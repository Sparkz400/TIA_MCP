"""Independently inspect C03 retained bytes, receipt manifests and actual interrupted publications."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator, ValidationError
from test_runtime_handoff_contracts import SCHEMA, original, validate, verify


def record(root, reference):
    prefix, identity, digest = reference.split(':')
    assert prefix == 'runtime-handoff' and len(identity) == 32
    directory = root / prefix / 'records' / identity
    assert {p.name for p in directory.iterdir()} == {'intent.json', 'request.json', 'report.json', 'receipt.json'}
    receipt_bytes = (directory / 'receipt.json').read_bytes()
    assert hashlib.sha256(receipt_bytes).hexdigest() == digest
    assert (directory / 'intent.json').read_bytes() == receipt_bytes
    receipt = json.loads(receipt_bytes)
    validate(receipt, 'recordManifest')
    assert receipt['id'] == identity
    request_bytes = (directory / 'request.json').read_bytes()
    request, report = json.loads(request_bytes), json.loads((directory / 'report.json').read_bytes())
    verify(root, request, report)
    raw_artifacts = {'request.json': request_bytes, 'report.json': (directory / 'report.json').read_bytes()}
    for phase in ('baseline', 'current'):
        for role in ('target', 'program', 'session'):
            ref = request[phase][role]
            assert ref['path'] == 'runtime-handoff/observations/' + ref['sha256'] + '.json'
            original(root, ref)
            raw_artifacts[phase + '.' + role + '.json'] = (root / ref['path']).read_bytes()
    assert len(raw_artifacts) == len(receipt['artifacts']) == 8
    for item in receipt['artifacts']:
        raw = raw_artifacts[item['name']]
        assert len(raw) == item['bytes'] and hashlib.sha256(raw).hexdigest() == item['sha256']
    access_path = root / 'access-reviews' / (request['accessRecordId'] + '.json')
    access = json.loads(access_path.read_bytes())
    charged = len(request_bytes) + len(access_path.read_bytes()) + len((root / access['input']['path']).read_bytes())
    charged += len((root / 'project-reviews' / (request['reviewId'] + '.json')).read_bytes())
    charged += sum(len(raw_artifacts[p + '.' + r + '.json']) for p in ('baseline', 'current') for r in ('target', 'program', 'session'))
    assert charged == report['summary']['referencedBytes'], 'Exact original input byte accounting'
    return receipt


class HandoffWorkflowTests(unittest.TestCase):
    def test_tools_and_non_authority(self):
        schema = json.loads(SCHEMA.read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema)
        self.assertEqual(set(schema['x-tool-contracts']), {'runtime_handoff_' + name for name in
                         ('observation', 'review', 'reopen', 'page', 'artifact_read', 'list')})
        for name in ('observationReceipt', 'handoffReceipt', 'handoffPage', 'handoffChunk', 'handoffList'):
            for flag in ('nativeQualified', 'operationAdmitted', 'assertionsAdmitted'):
                self.assertEqual(schema['$defs'][name]['properties'][flag], {'const': False})
        validate({}, 'runtime_handoff_listInput')
        with self.assertRaises(ValidationError):
            validate({'count': 21}, 'runtime_handoff_listInput')
        with self.assertRaises(ValidationError):
            validate({'role': 'native', 'uploadRef': 'upload:' + 'a' * 32}, 'runtime_handoff_observationInput')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture', required=True, type=Path)
    args = parser.parse_args()
    fixture = args.fixture.resolve()
    value = json.loads((fixture / 'verification.json').read_text(encoding='utf-8'))
    root = fixture.parent
    assert value['completed'] and not value['nativeExecuted']
    record(root, value['recordRef'])
    record(root, value['wideRecordRef'])
    phases = {'observationWritten', 'observationPublished', 'intentWritten', 'requestWritten', 'reportWritten', 'receiptWritten'}
    assert {p['phase'] for p in value['processes']} == phases and len(value['processes']) == 6
    committed = 0
    for process in value['processes']:
        phase = process['phase']
        path = Path(process['path'])
        assert path.resolve().is_relative_to(root) and process['childId'] > 0 and process['childExitCode'] != 0 and process['nativeExecuted'] is False
        assert process['published'] == (phase in ('observationPublished', 'receiptWritten'))
        if phase.startswith('observation'):
            raw = path.read_bytes()
            validate(json.loads(raw), 'target')
            assert path.suffix == ('.tmp' if phase == 'observationWritten' else '.json')
            published = path.parent / (hashlib.sha256(raw).hexdigest() + '.json')
            assert published.read_bytes() == raw, 'Explicit exact-byte retry preserved original source'
        else:
            names = {'intent.json'}
            if phase != 'intentWritten': names.add('request.json')
            if phase in ('reportWritten', 'receiptWritten'): names.add('report.json')
            if phase == 'receiptWritten': names.add('receipt.json')
            assert {p.name for p in path.iterdir()} == names, 'Partial publication was not adopted'
            manifest = json.loads((path / 'intent.json').read_bytes())
            validate(manifest, 'recordManifest')
            for item in manifest['artifacts']:
                if item['name'] in names:
                    raw = (path / item['name']).read_bytes()
                    assert len(raw) == item['bytes'] and hashlib.sha256(raw).hexdigest() == item['sha256']
            if phase == 'receiptWritten':
                record(root, 'runtime-handoff:' + path.name + ':' + hashlib.sha256((path / 'receipt.json').read_bytes()).hexdigest())
                committed += 1
    # Alter manifests only in memory: the schema must reject false authority and wrong artifact order.
    manifest = record(root, value['recordRef'])
    for mutation in ('extra-authority', 'wrong-order'):
        bad = copy.deepcopy(manifest)
        if mutation == 'extra-authority': bad['nativeQualified'] = True
        else: bad['artifacts'][0], bad['artifacts'][1] = bad['artifacts'][1], bad['artifacts'][0]
        try:
            validate(bad, 'recordManifest')
        except ValidationError:
            pass
        else:
            raise AssertionError('Manifest schema accepted ' + mutation)
    report = dict(completed=True, nativeExecuted=False, retainedRecordsChecked=2 + committed,
                  interruptedPublicationsChecked=6, originalBytesChecked=True, exactByteAccountingChecked=True,
                  incompletePublicationsNotAdopted=True, mutationControls=['extra-authority', 'wrong-order'])
    (fixture / 'independent-verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('PASS handoff original artifacts, manifests and six actual interruption records')


if __name__ == '__main__':
    main()
