"""Strict handoff wire contracts and retained real-process fixtures; no native enrollment proof."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator, FormatChecker, ValidationError
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]
SCHEMAS = [json.loads((ROOT / ('contracts/' + name)).read_text(encoding='utf-8')) for name in
           ['addin-handoff-wire.v1.schema.json', 'addin-handoff-state.v1.schema.json', 'addin-selection.v1.schema.json']]
REGISTRY = Registry().with_resources((s['$id'], Resource.from_contents(s)) for s in SCHEMAS)


def validate(value, kind):
    Draft202012Validator({**SCHEMAS[0], '$ref': '#/$defs/' + kind}, registry=REGISTRY, format_checker=FormatChecker()).validate(value)


class ReceiverContracts(unittest.TestCase):
    def test_all_finite_actions(self):
        Draft202012Validator.check_schema(SCHEMAS[0])
        for action in ['status', 'read', 'begin', 'complete', 'cancel']:
            request = dict(protocolVersion=1, action=action)
            if action != 'status': request['requestId'] = 'a' * 32
            if action == 'begin': request.update(reviewId='b' * 64, selectionBase64='e30=')
            if action in ('complete', 'cancel'): request['challenge'] = 'c' * 64
            validate(request, 'request')
            for field in request:
                changed = copy.deepcopy(request); del changed[field]
                with self.assertRaises(ValidationError): validate(changed, 'request')

    def test_no_caller_authority_or_native_forwarding(self):
        for action in ['attach', 'execute', 'native_dispatch', 'run', 'open']:
            with self.assertRaises(ValidationError): validate(dict(protocolVersion=1, action=action), 'request')
        for key in ['peer', 'workerProcessId', 'assemblyPath', 'authority', 'nativeQualified']:
            with self.assertRaises(ValidationError): validate(dict(protocolVersion=1, action='status', **{key: 'untrusted'}), 'request')

    def test_payloads_and_versions_are_bounded(self):
        request = dict(protocolVersion=1, action='begin', requestId='a' * 32, reviewId='b' * 64, selectionBase64='e30=')
        for key, value in [('protocolVersion', 2), ('requestId', 'A' * 32), ('reviewId', '../escape'), ('selectionBase64', '%'), ('selectionBase64', 'e30=\n'), ('selectionBase64', 'a' * 1398108)]:
            changed = {**request, key: value}
            with self.assertRaises(ValidationError): validate(changed, 'request')

    def test_failure_cannot_carry_success_or_unbounded_exception(self):
        value = dict(protocolVersion=1, success=False, error='AddInEnrollmentUnavailable')
        validate(value, 'response')
        for key, bad in [('result', {}), ('error', 'x' * 81), ('success', True)]:
            with self.assertRaises(ValidationError): validate({**value, key: bad}, 'response')


def inspect(log, output):
    rows = [json.loads(line.removeprefix('RECEIVER_FIXTURE ')) for line in log.read_text(encoding='utf-8-sig').splitlines() if line.startswith('RECEIVER_FIXTURE ')]
    assert {r['name'] for r in rows} == {'crash-recovery', 'impostor-malformed', 'bounded-disconnect', 'peer-exit'} and len(rows) == 4
    proofs = []
    for row in rows:
        path = Path(row['path']); raw = path.read_bytes(); record = json.loads(raw)
        assert record['nativeExecuted'] is False
        root = path.parent; discovery = json.loads((root / 'addin-handoffs/receiver.json').read_bytes())
        validate(discovery, 'discovery'); assert discovery['state'] == 'stopped' and discovery['authorityConfigured'] is True
        assert discovery['pipeName'] == 'TiaMcp-AddIn-' + discovery['receiverInstanceId']
        statuses = [record[k] for k in ['completed', 'consumed', 'status'] if k in record]
        for status in statuses:
            validate(dict(protocolVersion=1, success=True, result=status), 'response')
            assert status['binding']['syntheticAuthority'] is True
            directory = root / 'addin-handoffs' / status['requestId']
            plan_raw = (directory / 'plan.json').read_bytes(); plan = json.loads(plan_raw)
            assert plan['selectionSha256'] == status['selectionSha256'] == hashlib.sha256((directory / 'selection.input').read_bytes()).hexdigest()
            assert plan['binding'] == status['binding']
            consumption = json.loads((directory / 'consumed.json').read_bytes())
            assert consumption['planSha256'] == hashlib.sha256(plan_raw).hexdigest()
            if status['state'] == 'completed':
                receipt_raw = (directory / 'receipt.json').read_bytes(); receipt = json.loads(receipt_raw)
                assert json.loads((directory / 'completed.json').read_bytes())['receiptSha256'] == hashlib.sha256(receipt_raw).hexdigest()
                assert receipt['planSha256'] == hashlib.sha256(plan_raw).hexdigest()
                assert receipt['consumptionSha256'] == hashlib.sha256((directory / 'consumed.json').read_bytes()).hexdigest()
                assert receipt['selectionRecordId'] == status['selection']['recordId']
            else:
                assert status['state'] == 'consumedNoReceipt' and not (directory / 'receipt.json').exists()
        proofs.append(dict(name=row['name'], path=str(path), sha256=hashlib.sha256(raw).hexdigest()))
    with output.open('x', encoding='utf-8') as stream:
        json.dump(dict(completed=True, nativeExecuted=False, authority='synthetic test executable only', fixtures=proofs), stream, indent=2)
        stream.write('\n')
    print('PASS independent receiver contracts and retained crash/disconnect/peer-exit evidence: 4 fixtures; no native enrollment.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--log', type=Path); parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.log:
        assert args.output is not None; inspect(args.log, args.output)
    else:
        unittest.main(argv=['test_addin_receiver'])
