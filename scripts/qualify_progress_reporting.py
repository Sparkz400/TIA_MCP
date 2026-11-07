"""Qualify #219: change results say whether the project edit is complete, progress polls of a finished change carry an
unchanged phase fingerprint, a prepared-only change reports nothing changed, and host_build_identity reports a worker binary
replaced after Host start as changedOnDisk. The identity part starts no worker. Owned scratch only."""
import argparse
import json
from pathlib import Path
import shutil
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
    report = {'completed': False, 'steps': [], 'toolCalls': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(name, flush=True)
        return data

    def start(client, name):
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': name, 'version': '1'}})
        client.notify('notifications/initialized')

    # Host A: a native change and its milestones and progress.
    a = Client(args.host.resolve(), 180, ['--worker', str(args.worker.resolve()), '--artifacts', str(root / 'a')])
    try:
        start(a, 'progress-reporting')
        assert a.tool('tia_start')['ownsPortalProcess'] is True
        record('owned scratch', a.tool('tia_create_scratch'))
        plc = a.tool('project_inventory')['plcs'][0]['path']
        prepared = a.tool('change_prepare', {'kind': 'tag_create', 'plcPath': plc, 'payload': {
            'tagTableName': 'Default tag table', 'name': 'Progress', 'dataTypeName': 'Bool', 'logicalAddress': '%M110.0'}})
        before = a.tool('change_status', {'changeId': prepared['changeId']})['progress']
        assert before['projectEditComplete'] is False and before['summary'].startswith('Prepared only'), before
        record('a prepared-only change reports nothing changed', before)
        applied = a.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                                          'checkpointSave': True, 'compile': True, 'save': True})
        progress = applied['progress']
        status = {m['milestone']: m['status'] for m in progress['milestones']}
        assert progress['projectEditComplete'] is True and status['saved'] == 'done' and status['compiled'] == 'done' and status['compilerSucceeded'] == 'yes', progress
        assert 'host_build_identity' in progress['notCovered'], progress['notCovered']
        record('the applied change reports a complete, saved and compiled edit', progress)
        polls = [a.tool('change_status', {'changeId': prepared['changeId'], 'progressOnly': True}) for _ in range(2)]
        assert polls[0]['phaseFingerprint'] == polls[1]['phaseFingerprint'] and polls[0]['state'] == 'applied' and polls[0]['currentPhase'] is None, polls
        record('repeated progress polls of a finished change are unchanged', {'phaseFingerprint': polls[0]['phaseFingerprint'], 'state': polls[0]['state']})
    finally:
        record('owned worker shutdown', a.close())
        report['toolCalls'] += a.tool_calls

    # Host B: a copied worker that is never started; changing the copy after Host start must show.
    copy = root / 'worker-copy' / args.worker.name
    copy.parent.mkdir()
    shutil.copyfile(args.worker, copy)
    b = Client(args.host.resolve(), 60, ['--worker', str(copy), '--artifacts', str(root / 'b')])
    try:
        start(b, 'build-identity')
        first = b.tool('host_build_identity')
        worker = next(f for f in first['files'] if f['role'] == 'engineeringWorker')
        assert first['runningMatchesDisk'] is True and worker['changedOnDisk'] is False and worker['sha256AtHostStart'] == worker['sha256Now'], first
        record('identity matches disk at start', first)
        with open(copy, 'ab') as handle:
            handle.write(b'\0')
        second = b.tool('host_build_identity')
        worker = next(f for f in second['files'] if f['role'] == 'engineeringWorker')
        assert second['runningMatchesDisk'] is False and worker['changedOnDisk'] is True and 'Restart the Host' in second['note'], second
        record('a worker replaced after Host start is reported as changed on disk', second)
        report['completed'] = True
    finally:
        record('identity host shutdown', b.close())
        report['toolCalls'] += b.tool_calls
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
