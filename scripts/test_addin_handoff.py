"""Check internal lifecycle contracts and independently inspect retained managed fixtures; no native acceptance."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator, FormatChecker, ValidationError
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT / 'contracts/addin-handoff-state.v1.schema.json').read_text(encoding='utf-8'))
SELECTION = json.loads((ROOT / 'contracts/addin-selection.v1.schema.json').read_text(encoding='utf-8'))
REGISTRY = Registry().with_resources((s['$id'], Resource.from_contents(s)) for s in [SCHEMA, SELECTION])


def validator(kind='status'):
    return Draft202012Validator({**SCHEMA, '$ref': '#/$defs/' + kind}, registry=REGISTRY, format_checker=FormatChecker())


def status():
    return dict(schemaVersion='addin-handoff-status.v1', requestId='a' * 32, state='interrupted', receiverInstanceId='b' * 32,
                hostProcessId=123, reviewId='c' * 64, selectionSha256='d' * 64,
                binding=dict(workerProcessId=124, workerInstanceId='e' * 32, sessionGeneration='f' * 32,
                             tiaProcess=dict(processId=125, startedAtUtc='2026-10-18T00:00:00.0000000Z', executablePath='C:\\TIA\\tia.exe', windowsSessionId=1),
                             projectHost='test-host', projectPath='D:\\project.ap21', snapshotId='snapshot', assemblySha256='a' * 64,
                             configurationSha256='b' * 64, authorityEvidenceSha256='c' * 64, syntheticAuthority=True),
                selection=None, automaticReplay=False, producerAuthenticated=False, liveSelectionVerified=False, nativeQualified=False)


class HandoffContractTests(unittest.TestCase):
    def test_schema_and_incomplete_states(self):
        Draft202012Validator.check_schema(SCHEMA)
        for state in ['awaitingResponse', 'expiredOrUnavailable', 'interrupted', 'cancelled', 'consumedNoReceipt', 'receiptUncommitted']:
            value = status(); value['state'] = state; validator().validate(value)

    def test_no_manufactured_native_authority_or_replay(self):
        for key in ['automaticReplay', 'producerAuthenticated', 'liveSelectionVerified', 'nativeQualified']:
            value = status(); value[key] = True
            with self.assertRaises(ValidationError): validator().validate(value)

    def test_exact_identity_and_fields(self):
        for key in status():
            value = status(); del value[key]
            with self.assertRaises(ValidationError): validator().validate(value)
        for key, bad in [('requestId', '../escape'), ('receiverInstanceId', 'A' * 32), ('reviewId', 'x' * 64), ('hostProcessId', 0)]:
            value = status(); value[key] = bad
            with self.assertRaises(ValidationError): validator().validate(value)
        value = status(); value['binding']['token'] = 'untrusted'
        with self.assertRaises(ValidationError): validator().validate(value)
        value = status(); value['binding']['tiaProcess']['startedAtUtc'] = '2026-10-18T00:00:00+00:00'
        with self.assertRaises(ValidationError): validator().validate(value)

    def test_completion_requires_a_retained_selection(self):
        value = status(); value['state'] = 'completed'
        with self.assertRaises(ValidationError): validator().validate(value)
        value['selection'] = {}
        with self.assertRaises(ValidationError): validator().validate(value)

    def test_challenge_is_bounded_and_unqualified(self):
        value = dict(schemaVersion='addin-handoff-challenge.v1', requestId='a' * 32, receiverInstanceId='b' * 32,
                     challenge='c' * 64, lifetimeMilliseconds=30000, selectionSha256='d' * 64, syntheticAuthority=True,
                     producerAuthenticated=False, liveSelectionVerified=False, nativeQualified=False)
        validator('challenge').validate(value)
        for key, bad in [('challenge', 'short'), ('lifetimeMilliseconds', 30001), ('producerAuthenticated', True)]:
            changed = copy.deepcopy(value); changed[key] = bad
            with self.assertRaises(ValidationError): validator('challenge').validate(changed)


def inspect(log, output):
    def digest(data): return hashlib.sha256(data).hexdigest()
    def read(path):
        raw = path.read_bytes()
        assert len(raw) <= 1024 * 1024, 'Fixture file exceeds the handoff input bound'
        return raw, json.loads(raw)
    roots = [Path(json.loads(line.removeprefix('HANDOFF_FIXTURE '))['root']) for line in log.read_text(encoding='utf-8-sig').splitlines() if line.startswith('HANDOFF_FIXTURE ')]
    assert len(roots) >= 6, 'All managed lifecycle groups must execute before independent inspection'
    proofs = []
    for root in roots:
        for path in sorted(root.glob('*.status.json')):
            raw, document = read(path); validator().validate(document)
            assert document['binding']['syntheticAuthority'] is True
            directory = root / 'addin-handoffs' / document['requestId']
            plan_raw, plan = read(directory / 'plan.json'); selection_raw, selected = read(directory / 'selection.input')
            review_raw, review = read(root / 'project-reviews' / (document['reviewId'] + '.json'))
            assert plan['requestId'] == document['requestId'] and plan['receiverInstanceId'] == document['receiverInstanceId']
            assert plan['binding'] == document['binding'] and plan['hostProcessId'] == document['hostProcessId']
            assert digest(selection_raw) == plan['selectionSha256'] == document['selectionSha256']
            assert digest(review_raw) == selected['reviewId'] == plan['reviewId'] == document['reviewId']
            model = review['model']
            for reference in selected['references']:
                assert reference['snapshotId'] == model['snapshotId'] and reference['sessionGeneration'] == model['sessionGeneration']
                assert reference['projectPath'] == model['project']['projectPath']
                matches = [n for n in model['nodes'] if n['nodeId'] == reference['nodeId']]
                assert len(matches) == 1 and matches[0]['origin'] == 'native' and matches[0]['objectPath'] == reference['objectPath']
            if document['state'] in ('completed', 'receiptUncommitted', 'cancelled', 'consumedNoReceipt'):
                consumed_raw, consumed = read(directory / 'consumed.json')
                assert consumed['planSha256'] == digest(plan_raw)
                assert consumed['action'] == ('cancel' if document['state'] == 'cancelled' else 'complete')
            if document['state'] in ('completed', 'receiptUncommitted'):
                receipt_raw, receipt = read(directory / 'receipt.json')
                assert receipt['version'] == 1 and receipt['planSha256'] == digest(plan_raw) and receipt['consumptionSha256'] == digest(consumed_raw)
            if document['state'] == 'completed':
                _, completed = read(directory / 'completed.json'); assert completed['receiptSha256'] == digest(receipt_raw)
                record = document['selection']; assert record['recordId'] == receipt['selectionRecordId']
                report_raw, report = read(root / record['reportArtifact']['path'])
                assert digest(report_raw) == record['recordId'] == record['reportArtifact']['sha256']
                assert (root / record['input']['path']).read_bytes() == selection_raw
                assert report['input'] == record['input'] and report['reviewId'] == document['reviewId']
                assert report['report']['producerAuthenticated'] is False and report['report']['liveSelectionVerified'] is False
                assert [row['reference'] for row in report['report']['selections']] == selected['references']
            else:
                assert not (directory / 'completed.json').is_file()
            proofs.append(dict(path=str(path), sha256=digest(raw), state=document['state'], planSha256=digest(plan_raw), selectionSha256=digest(selection_raw)))
    states = {p['state'] for p in proofs}
    assert states == {'completed', 'consumedNoReceipt', 'cancelled', 'expiredOrUnavailable', 'interrupted', 'receiptUncommitted'}, states
    result = dict(completed=True, nativeExecuted=False, transportExecuted=False, productionEnrollmentAvailable=False,
                  fixtureRoots=[str(p) for p in roots], statuses=proofs)
    with output.open('x', encoding='utf-8') as stream: json.dump(result, stream, indent=2); stream.write('\n')
    print(f'PASS independent handoff schema, original bytes, saved references and receipt-chain checks: {len(proofs)} statuses; synthetic authority only.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--log', type=Path); parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.log:
        assert args.output is not None; inspect(args.log, args.output)
    else: unittest.main(argv=['test_addin_handoff'])
