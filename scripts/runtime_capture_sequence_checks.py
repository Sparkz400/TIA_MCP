"""Independent original-export and explicit session-boundary oracle."""
import copy
import hashlib
import json
from pathlib import Path


SCENARIOS = ('durationLimit', 'verificationCancellation', 'queuedDeadline', 'explicitStop')


def verify(document, rpc_methods):
    rows = document['scenarios']
    assert [r['scenario'] for r in rows] == list(SCENARIOS)
    assert document['ordinarySessionClosed']['state'] == 'closed'
    ordinary = document['ordinarySessionClosed']
    attempts, epochs, selections = {ordinary['attemptId']}, {ordinary['context']['epoch']}, set()
    previous_end = document['ordinaryCloseRpcEnd']
    for row in rows:
        connected, selected, observed, closed = (row[k] for k in ('connected', 'selection', 'observed', 'closed'))
        attempt, context = connected['attemptId'], connected['context']
        selection = selected['selectionId']
        assert connected['state'] == 'connected' and closed['state'] == 'closed'
        assert observed['attemptId'] == closed['attemptId'] == attempt == context['connectionId']
        assert observed['context'] == closed['context'] == context
        assert selected['context'] == context
        assert attempt not in attempts and context['epoch'] not in epochs and selection not in selections
        attempts.add(attempt); epochs.add(context['epoch']); selections.add(selection)
        start, connected_end, selected_end, captured_end, closed_end = row['rpcBoundaries']
        assert previous_end == start < connected_end <= selected_end <= captured_end < closed_end
        assert rpc_methods[start:connected_end].count('Api.Login') == 1
        assert rpc_methods[connected_end:captured_end].count('Api.Login') == 0
        assert rpc_methods[connected_end:captured_end].count('Api.Logout') == 0
        assert rpc_methods[captured_end:closed_end] == ['Api.Logout']
        previous_end = closed_end
        scenario = row['capture']
        assert scenario['scenario'] == row['scenario']
        status = scenario['status']
        assert status['state'] == 'ended' and status['localFailure'] is None
        request = status['plan']['request']
        assert request['attemptId'] == attempt and request['selectionIds'] == [selection]
        for key, value in scenario['request'].items():
            assert request[key] == value
        signal, = status['plan']['signals']
        assert signal['selectionId'] == selection and signal['selection']['context'] == context
        reference, = scenario['export']['artifacts']
        data = Path(reference['path']).read_bytes()
        assert len(data) == reference['bytes'] and hashlib.sha256(data).hexdigest() == reference['sha256']
        original = json.loads(data)
        assert original['capture'] == status and original['rows'] == scenario['page']['rows']
        for item in original['rows']:
            if item['kind'] == 'read':
                assert item['scheduledMilliseconds'] < request['durationSeconds'] * 1000
        reason = 'stopped' if row['scenario'] == 'explicitStop' else 'durationLimit'
        assert status['recordedProgress']['terminalReason'] == reason
        if row['scenario'] == 'verificationCancellation':
            assert observed == scenario['invalidatedSession']
            assert observed['state'] == 'remoteSessionUnknown' and observed['errorCode'] == 'RuntimeCancelled'
            assert scenario['oldAttemptRefusal']['error']['code'] == 'RuntimeSessionUnavailable'
            assert rpc_methods[selected_end:captured_end] == ['Api.GetSessionInfo']
            reads = [r for r in original['rows'] if r['kind'] == 'read']
            assert len(reads) == 1 and reads[0]['errorCode'] == 'RuntimeCancelled' and reads[0]['readResultJson'] is None
    assert not document['nativeQualified']


def negative_controls(document, rpc_methods):
    failed = []
    for name, mutate in (
        ('reused old attempt', lambda d: d['scenarios'][1]['connected'].update(attemptId=d['scenarios'][0]['connected']['attemptId'])),
        ('reused old selection', lambda d: d['scenarios'][1]['selection'].update(selectionId=d['scenarios'][0]['selection']['selectionId'])),
        ('invented healthy cancelled session', lambda d: d['scenarios'][1]['observed'].update(state='connected')),
        ('omitted explicit close', lambda d: d['scenarios'][0]['closed'].update(state='connected')),
        ('cross-session capture intent', lambda d: d['scenarios'][1]['capture']['request'].update(attemptId=d['scenarios'][0]['connected']['attemptId'])),
        ('rewritten original cancellation', lambda d: d['scenarios'][1]['capture']['status']['recordedProgress'].update(terminalReason='sampleLimit')),
    ):
        changed = copy.deepcopy(document)
        mutate(changed)
        try:
            verify(changed, rpc_methods)
        except AssertionError:
            failed.append(name)
        else:
            raise AssertionError('Accepted sequencing mutation: ' + name)
    changed_calls = list(rpc_methods)
    row = document['scenarios'][1]
    changed_calls[row['rpcBoundaries'][2]] = 'Api.Login'
    try:
        verify(document, changed_calls)
    except AssertionError:
        failed.append('silent login inside cancellation scenario')
    else:
        raise AssertionError('Accepted automatic login')
    return failed


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture', required=True, type=Path)
    args = parser.parse_args()
    document = json.loads((args.fixture/'capture-cancellation-scenarios.json').read_text(encoding="utf-8"))
    verification = json.loads((args.fixture/'verification.json').read_text(encoding="utf-8"))
    assert verification['captureCancellationSequence'] == document
    verify(document, verification['rpcMethods'])
    controls = negative_controls(document, verification['rpcMethods'])
    assert document['negativeControls'] == controls
    print('PASS capture cancellation sequencing: four isolated sessions and seven independent mutation controls')
