"""Qualify #217 natively: a change_apply cancelled by the MCP client while TIA works finishes once, is never replayed,
and its real outcome becomes visible without restarting the Host. Owned scratch only; never a user project."""
import argparse
import json
from pathlib import Path
import time
import uuid

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    client = Client(args.host.resolve(), 180, ['--worker', str(args.worker.resolve()), '--artifacts', str(root)])
    report = {'completed': False, 'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(name, flush=True)
        return data

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'cancellation', 'version': '1'}})
        client.notify('notifications/initialized')
        assert record('owned start', client.tool('tia_start'))['ownsPortalProcess'] is True
        record('owned scratch', client.tool('tia_create_scratch'))
        plc = client.tool('project_inventory')['plcs'][0]['path']
        prepared = record('prepare tag_create', client.tool('change_prepare', {'kind': 'tag_create', 'plcPath': plc, 'payload': {
            'tagTableName': 'Default tag table', 'name': 'CancelledOnce', 'dataTypeName': 'Bool', 'logicalAddress': '%M80.0'}}))
        apply = {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                 'checkpointSave': True, 'compile': True, 'save': True}
        client.sequence += 1
        request_id = client.sequence
        client.process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': request_id, 'method': 'tools/call',
                                               'params': {'name': 'change_apply', 'arguments': apply}}) + '\n')
        client.process.stdin.flush()
        # Cancel as soon as the worker's journal shows native work, so the cancellation lands mid-apply.
        journal, seen, deadline = root / 'changes' / ('change-' + prepared['changeId']) / 'journal.json', None, time.monotonic() + 120
        while seen not in ('checkpointing', 'applying'):
            assert time.monotonic() < deadline, 'The apply never started'
            try:
                seen = json.loads(journal.read_text(encoding='utf-8')).get('state')
            except (OSError, ValueError):
                pass
            time.sleep(0.01)
        client.notify('notifications/cancelled', {'requestId': request_id, 'reason': 'qualification: user stop'})
        record('cancel change_apply while native work runs', {'requestId': request_id, 'journalState': seen})

        refusals, started = 0, time.monotonic()
        while True:
            result = client.request('tools/call', {'name': 'change_status', 'arguments': {'changeId': prepared['changeId']}})
            value = result.get('structuredContent') or json.loads(result['content'][0]['text'])
            if not result.get('isError'):
                break
            assert value['error']['code'] == 'PreviousOperationRunning', value
            refusals += 1
            assert time.monotonic() - started < 600, 'The cancelled apply did not finish within 10 minutes'
            time.sleep(5)
        record('status refused while the apply ran', {'refusals': refusals})
        assert refusals > 0, 'The cancellation arrived after the apply finished; nothing was in flight'
        stages = value['stages']
        record('outcome after the late completion', {'state': value['state'], 'stages': stages})
        assert value['state'] == 'applied' and stages.count('mutation_applied') == 1 and 'changed_project_saved' in stages, value['state']
        late = [line for line in client.errors if 'bridgeLateResponse' in line and 'change_apply' in line]
        assert len(late) == 1, late
        record('Host recorded the late response', json.loads(late[0]))
        replay = record('same key returns the recorded result', client.tool('change_apply', apply))
        assert replay['state'] == 'applied' and replay['stages'] == stages, replay['stages']
        table = client.tool('tag_table_read', {'plcPath': plc, 'tagTableName': 'Default tag table'})['xml']
        record('tag created exactly once', table.count('<Name>CancelledOnce</Name>'))
        assert table.count('<Name>CancelledOnce</Name>') == 1
        # The cancelled dispatch left its admission outcomeUnknown; the existing fresh-instance route verifies and releases it.
        unknown = []
        for folder in sorted((root / 'engineering-admissions').glob('[0-9a-f]*')):
            latest = json.loads(sorted(folder.glob('*.json'))[-1].read_text(encoding='utf-8'))
            if latest.get('state') in ('dispatchIntent', 'outcomeUnknown'):
                unknown.append(latest['id'])
        record('cancelled dispatch admission held', unknown)
        assert len(unknown) == 1, unknown
        project = client.tool('tia_session_status')['projects'][0]['path']
        record('close the original TIA', client.tool('tia_detach'))
        assert client.tool('tia_start')['ownsPortalProcess'] is True
        released = record('reconcile the admission', client.tool('engineering_admission_reconcile', {'admissionId': unknown[0]}))
        assert released.get('released') is True and released.get('automaticReplay') is False, released
        for attempt in range(13):
            opened = client.request('tools/call', {'name': 'project_open', 'arguments': {'path': project}})
            if not opened.get('isError'):
                break
            assert 'already been opened' in json.dumps(opened), opened
            time.sleep(15)
        else:
            raise AssertionError('The project lock was not released within three minutes')
        nxt = client.tool('change_prepare', {'kind': 'tag_create', 'plcPath': plc, 'payload': {
            'tagTableName': 'Default tag table', 'name': 'AfterCancel', 'dataTypeName': 'Bool', 'logicalAddress': '%M80.1'}})
        done = record('next change applies after reconciliation', client.tool('change_apply', {
            'changeId': nxt['changeId'], 'previewHash': nxt['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
            'checkpointSave': True, 'compile': True, 'save': True}))
        assert done['state'] == 'applied', done['state']
        report['completed'] = True
    finally:
        record('owned worker shutdown', client.close())
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
