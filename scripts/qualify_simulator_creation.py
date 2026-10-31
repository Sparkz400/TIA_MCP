"""Qualify durable creation/adoption on fresh empty simulator instances; no project download."""
import argparse
import hashlib
import json
import pathlib
import uuid

from block_test_runtime import redact, utc_now
from run_block_tests import save_json
from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=pathlib.Path, required=True)
    parser.add_argument('--sim-worker', type=pathlib.Path, required=True)
    parser.add_argument('--artifacts', type=pathlib.Path, required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    parser.add_argument('--power-on-empty', action='store_true', help='Power on the owned empty CPU before disconnect; never download a program.')
    args = parser.parse_args()
    creation_id = uuid.uuid4().hex
    report = {'schemaVersion': 1, 'startedAtUtc': utc_now(), 'creationId': creation_id,
              'completed': False, 'poweredOn': False, 'steps': [], 'binaries': {}}
    for role, path in (('host', args.host), ('simWorker', args.sim_worker)):
        report['binaries'][role] = {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
    clients, owned = [], None

    def persist():
        save_json(args.report, report)

    def connect():
        # The Host refuses a powerOn whose 60 s timeout and failure powerOff cannot end inside its bridge timeout.
        client = Client(args.host.resolve(), 300, ['--sim-worker', str(args.sim_worker.resolve()),
                        '--artifacts', str(args.artifacts.resolve())])
        clients.append(client)
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                     'clientInfo': {'name': 'creation-qualification', 'version': '1'}})
        client.notify('notifications/initialized')
        return client

    def call(client, name, arguments=None, expected_error=None):
        step = {'tool': name, 'arguments': arguments or {}, 'status': 'started', 'startedAtUtc': utc_now()}
        report['steps'].append(step)
        persist()
        response = client.request('tools/call', {'name': name, 'arguments': arguments or {}})
        result = response.get('structuredContent')
        if result is None:
            result = json.loads('\n'.join(item['text'] for item in response['content'] if item['type'] == 'text'))
        step.update(result=redact(result), completedAtUtc=utc_now())
        if expected_error:
            assert response.get('isError') and result.get('error', {}).get('code') == expected_error, redact(result)
        else:
            assert not response.get('isError'), redact(result)
        step['status'] = 'passed'
        persist()
        return result

    def close(client):
        client.close()
        report.setdefault('toolCalls', []).extend(client.tool_calls)
        report.setdefault('hostExits', []).append({'pid': client.process.pid, 'exitCode': client.process.returncode,
            'workerExitDiagnostics': [json.loads(line) for line in client.errors if line.startswith('{"bridgeWorkerExit":')],
            'stderr': redact(client.errors)})
        clients.remove(client)
        persist()
        assert client.process.returncode == 0

    try:
        first = connect()
        assert call(first, 'sim_status')['instances'] == [], 'An exclusive empty simulator registry is required.'
        created = call(first, 'sim_create', {'cpuType': 'CPU1511', 'creationId': creation_id})
        owned = created['instance']
        assert owned['owned'] is True and owned['operatingState'] == 'Off'
        if args.power_on_empty:
            powered = call(first, 'sim_state', {'instanceId': owned['id'], 'action': 'powerOn', 'timeoutMs': 60000})
            assert powered['instance']['operatingState'] == 'Stop'
            report['poweredOn'] = True
        status = call(first, 'sim_creation_status', {'creationId': creation_id})
        assert status['receipt']['stage'] == 'ready' and status['workerExitVerified'] is False
        assert status['exactRegisteredIdentityObserved'] is True
        second = connect()
        select = {'instanceId': owned['id'], 'expectedName': owned['name'], 'creationId': creation_id}
        call(second, 'sim_select', select, 'SimulationOwnershipUnverified')
        observed = call(second, 'sim_select', {'instanceId': owned['id'], 'expectedName': owned['name']})
        assert observed['instance']['owned'] is False
        call(second, 'sim_select', select, 'SimulationOwnershipUnverified')
        assert any(row['id'] == owned['id'] and row['name'] == owned['name'] for row in call(first, 'sim_status')['instances'])
        close(first)
        status = call(second, 'sim_creation_status', {'creationId': creation_id})
        assert status['workerExitVerified'] is True and status['receipt']['stage'] == 'ready'
        adopted = call(second, 'sim_select', select)
        assert adopted['instance']['owned'] is True
        assert adopted['instance']['operatingState'] == ('Stop' if args.power_on_empty else 'Off')
        released = call(second, 'sim_release', {'instanceId': owned['id'], 'deleteOwned': True})
        assert released['unregistered'] is True and released['instanceName'] == owned['name']
        owned = None
        status = call(second, 'sim_creation_status', {'creationId': creation_id})
        assert status['receipt']['stage'] == 'unregistered' and not status['exactRegisteredIdentityObserved']
        assert call(second, 'sim_status')['instances'] == []
        close(second)
        report['completed'] = True
    except Exception as error:
        report['failure'] = {'type': type(error).__name__, 'message': str(error)}
        # Never guess which worker owns an interrupted native operation or replay adoption.
        report['unresolvedOwnedInstance'] = redact(owned)
    finally:
        for client in clients[:]:
            try:
                close(client)
            except Exception as error:
                report.setdefault('cleanupFailures', []).append({'type': type(error).__name__, 'message': str(error)})
                report['completed'] = False
        report['completedAtUtc'] = utc_now()
        persist()
    print(json.dumps({'completed': report['completed'], 'steps': len(report['steps']), 'report': str(args.report),
                      'failure': report.get('failure')}, indent=2))
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
