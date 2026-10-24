"""Qualify persistent reviews on a fresh owned scratch project and after host restart."""
import argparse
import hashlib
import json
import pathlib
import uuid

from block_test_process import OwnedTiaProcessWatcher
from block_test_runtime import redact, utc_now
from qualify_project_index import initialize, require, validate_owned_status
from run_block_tests import detach_owned, save_json
from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=pathlib.Path, required=True)
    parser.add_argument('--worker', type=pathlib.Path, required=True)
    parser.add_argument('--artifacts', type=pathlib.Path, required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    parser.add_argument('--check-network', action='store_true', help='Also verify configured subnet inspection on the new owned scratch CPU.')
    args = parser.parse_args()
    require(not args.report.exists(), 'Evidence report already exists.')
    directory = args.artifacts.resolve() / ('rv_' + uuid.uuid4().hex[:8])
    report = {'schemaVersion': 1, 'startedAtUtc': utc_now(), 'completed': False, 'steps': [], 'binaries': {}}
    for role, path in (('host', args.host), ('hostAssembly', args.host.with_suffix('.dll')), ('worker', args.worker)):
        report['binaries'][role] = {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
    client = watcher = None
    attached = False

    def persist():
        save_json(args.report, report)

    def connect():
        new_client = Client(args.host.resolve(), 300, ['--worker', str(args.worker.resolve()), '--artifacts', str(directory)])
        initialize(new_client)
        return new_client

    def call(name, arguments=None):
        step = {'tool': name, 'arguments': arguments or {}, 'startedAtUtc': utc_now(), 'status': 'started'}
        report['steps'].append(step)
        persist()
        try:
            value = client.tool(name, arguments or {})
            step.update(status='passed', result=redact(value), completedAtUtc=utc_now())
            print('PASS ' + name, flush=True)
            return value
        except Exception as error:
            step.update(status='failed', failure=str(error))
            raise
        finally:
            persist()

    def close():
        nonlocal client
        client.close()
        report.setdefault('toolCalls', []).extend(client.tool_calls)
        diagnostics = [json.loads(line) for line in client.errors if line.startswith('{"bridgeWorkerExit":')]
        report.setdefault('hostExits', []).append({'pid': client.process.pid, 'exitCode': client.process.returncode,
            'workerExits': diagnostics, 'stderr': redact(client.errors)})
        require(client.process.returncode == 0, 'Host shutdown failed.')
        require(all(item['bridgeWorkerExit']['processExitVerified'] and item['bridgeWorkerExit']['exitCode'] == 0 for item in diagnostics), 'Worker shutdown unverified.')
        client = None
        persist()

    try:
        client = connect()
        started = call('tia_start')
        attached = True
        pid = started['processId']
        validate_owned_status(started, pid, require_owner=True)
        watcher = OwnedTiaProcessWatcher(pid)
        scratch = call('tia_create_scratch')
        require(scratch['completed'] and scratch['compile']['errorCount'] == 0, 'Scratch fixture failed.')
        project_path = pathlib.Path(scratch['project']['path'])
        report['projectPath'] = str(project_path)
        plc_path = call('project_inventory', {'maxItems': 1000})['plcs'][0]['path']
        if args.check_network:
            hardware = call('hardware_inspect', {'maxItems': 2000})
            interfaces = [row for row in hardware['items'] if row.get('network', {}).get('nodes')]
            require(interfaces, 'Fresh fixture has no observed network interface.')
            selected = interfaces[0]
            node = selected['network']['nodes'][0]
            require('subnet' in node and 'ioControllers' in selected['network'] and 'ioConnectors' in selected['network'], 'Extended network contract missing.')
            prepared = call('change_prepare', {'kind': 'network_configure', 'payload': {
                'hardwarePath': selected['path'], 'nodeName': node['name'], 'subnetName': 'ReviewSubnet'}})
            applied = call('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
            require(applied['state'] == 'applied', 'Owned subnet fixture was not applied.')
            observed = call('hardware_inspect', {'hardwarePath': selected['path'], 'maxItems': 2000})['items'][0]['network']
            subnet = next(item for item in observed['nodes'] if item['name'] == node['name'])['subnet']
            require(subnet['resolution'] == 'resolved' and subnet['name'] == 'ReviewSubnet' and subnet['path'].endswith('/subnets/ReviewSubnet'), 'Native subnet association differs from the explicit fixture.')
            require(observed['physicalTopologyVerified'] is False, 'Offline association incorrectly claims physical proof.')
            report['configuredNetworkInspection'] = {'subnetVerified': True, 'observed': observed,
                'distributedIoDeviceFixtureVerified': False}
        block_name = 'ReviewCounter'
        for value in (1, 2):
            validate_owned_status(call('tia_session_status'), pid, project_path, require_owner=True)
            source = f'FUNCTION "{block_name}" : Int\nVERSION : 0.1\nBEGIN\n// ReviewMarker{value}\n#ReviewCounter := {value};\nEND_FUNCTION\n'
            prepared = call('change_prepare', {'kind': 'source_upsert', 'plcPath': plc_path,
                'payload': {'language': 'SCL', 'source': source, 'expectedBlockNames': [block_name]}})
            applied = call('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
            require(applied['state'] == 'applied', 'Fixture source was not applied.')
            review = call('project_snapshot_capture', {'request': {'plcPath': plc_path,
                'maxBlocks': 1, 'blockPaths': [plc_path + '/blocks/' + block_name], 'maxRelationshipRoots': 1}})
            require(review['exportedBlocks'] == 1, 'Exact selected native export missing.')
            report['beforeId' if value == 1 else 'afterId'] = review['reviewId']
            search = call('project_snapshot_search', {'reviewId': review['reviewId'], 'query': f'ReviewMarker{value}'})
            require(search['totalObservedHits'] >= 1, 'Native source comment was not searchable.')
        compared = call('project_snapshot_compare', {'beforeId': report['beforeId'], 'afterId': report['afterId']})
        change = next(row for row in compared['changes'] if row.get('afterObjectPath') == plc_path + '/blocks/' + block_name)
        require('logicChanged' in change['changedFields'] and 'documentChanged' in change['changedFields'], 'Native implementation change was not detected.')
        require(compared['contentUnknownBlocks'] > 0 and compared['wholeProjectEquivalent'] is False, 'Omitted exports lost their unknown state.')
        call('project_health_report', {'reviewId': report['afterId']})
        detach_owned(call)
        attached = False
        close()
        report['ownedProcessExit'] = watcher.wait_for_exit(60000)
        watcher = None
        client = connect()
        require(call('project_snapshot_compare', {'beforeId': report['beforeId'], 'afterId': report['afterId']})['changes'] == compared['changes'], 'Restart changed persisted comparison.')
        require(call('project_snapshot_search', {'reviewId': report['afterId'], 'query': 'ReviewMarker2'})['totalObservedHits'] >= 1, 'Restart lost source search.')
        call('project_health_report', {'reviewId': report['afterId']})
        close()
        report['offlineRestartWithoutWorker'] = not report['hostExits'][-1]['workerExits']
        require(report['offlineRestartWithoutWorker'], 'Offline review unexpectedly launched a native worker.')
        report['completed'] = True
    except Exception as error:
        report['failure'] = {'type': type(error).__name__, 'message': str(error)}
    finally:
        if client:
            if attached:
                try: detach_owned(call)
                except Exception as error: report['detachFailure'] = str(error)
            try: close()
            except Exception as error: report['closeFailure'] = str(error)
        if watcher:
            try: report['ownedProcessExit'] = watcher.wait_for_exit(60000)
            except Exception as error: report['processExitFailure'] = str(error)
        report['completedAtUtc'] = utc_now()
        persist()
    print(json.dumps({'completed': report['completed'], 'report': str(args.report), 'failure': report.get('failure')}, indent=2))
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
