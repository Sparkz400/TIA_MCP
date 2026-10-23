"""Qualify reviewed remote-I/O changes in a fresh owned offline project only.

Catalog identifiers and channel values come from the installed native metadata.
Every engineering change uses the public prepared/checkpoint/compile workflow.
"""
import argparse
import copy
import hashlib
import json
import pathlib
import uuid

from block_test_process import OwnedTiaProcessWatcher
from block_test_runtime import redact, utc_now
from qualify_project_index import initialize, require, validate_owned_status
from run_block_tests import detach_owned, save_json
from smoke_mcp import Client


CHANNEL_ATTRIBUTES = {'ChannelActivation', 'MeasuringType', 'MeasuringRange', 'InputDelay', 'SubstituteValue', 'DigitalInputDelay'}
HARDWARE_ATTRIBUTES = CHANNEL_ATTRIBUTES | {'Name', 'Author', 'Comment', 'InformationText', 'PlantDesignation', 'LocationIdentifier', 'AdditionalInformation'}
NETWORK_ATTRIBUTES = {'Address', 'SubnetMask', 'RouterAddress', 'UseRouter', 'UseIpProtocol', 'IpProtocolSelection', 'PnDeviceName', 'PnDeviceNameAutoGeneration', 'PnDeviceNameSetDirectly', 'DhcpClientId'}
QUALIFICATION_SCOPES = ('full', 'configured-module-lifecycle')


def channel_gate(subtree, scope):
    require(scope in QUALIFICATION_SCOPES, 'Unknown remote-I/O qualification scope.')
    observations = [{'hardwarePath': row['path'], 'channels': copy.deepcopy(row.get('channels', []))}
                    for row in subtree if row.get('channels')]
    choice = None
    for row in subtree:
        for channel in row.get('channels', []):
            for attribute in channel['attributes']:
                if (attribute['name'] not in CHANNEL_ATTRIBUTES or 'Write' not in attribute.get('accessMode', '') or 'error' in attribute
                        or attribute.get('valueComplete') is not True or attribute.get('valueCaptured') is not True
                        or attribute.get('readAccessKnown') is not True or attribute.get('readable') is not True
                        or attribute.get('valueStatus') != 'captured' or attribute.get('metadataUnavailable')):
                    continue
                before = attribute.get('value')
                alternatives = [not before] if type(before) is bool else [entry['value'] for entry in attribute.get('nativeEnumValues', []) if entry['value'] != before]
                if alternatives:
                    choice = (row, channel, attribute, alternatives[0])
                    break
            if choice: break
        if choice: break
    evidence = {'status': 'eligible_unqualified' if choice else 'unsupported_for_reviewed_channel_route',
                'qualified': False, 'nativeObservations': observations,
                'reason': 'Requires a reviewed attribute with native write access and an explicitly observed distinct typed value.'}
    if scope == 'configured-module-lifecycle':
        require(observations, 'Limited lifecycle scope requires observed native channels; absent evidence is not unsupported capability proof.')
        for row in subtree:
            require(not row.get('channelsError') and not row.get('metadataUnavailable'), 'Channel metadata capture is incomplete.')
            for channel in row.get('channels', []):
                attributes = channel.get('attributes', [])
                require(not channel.get('error') and not channel.get('metadataUnavailable') and attributes
                        and any(attribute.get('name') in CHANNEL_ATTRIBUTES for attribute in attributes)
                        and all('error' not in attribute and not attribute.get('metadataUnavailable')
                                and attribute.get('accessMode') in ('Read', 'Write', 'ReadWrite')
                                and 'value' in attribute and attribute.get('valueComplete') is True
                                and attribute.get('valueCaptured') is True and attribute.get('readAccessKnown') is True
                                and attribute.get('readable') is True and attribute.get('valueStatus') == 'captured' for attribute in attributes),
                        'Channel metadata capture is incomplete; read errors cannot prove an unsupported route.')
        require(choice is None, 'Limited lifecycle scope expects the observed unsupported channel route; use full scope for eligible channels.')
    return choice, evidence


def qualification_complete(scope, gates):
    require(scope in QUALIFICATION_SCOPES, 'Unknown remote-I/O qualification scope.')
    required = {'controllerAssignment', 'fixtureServerRelocation', 'moduleCreation', 'slotMove', 'addressChange',
                'freshProcessPersistence', 'archiveRecovery', 'disconnectOutcomeAndRecovery', 'recoveredFreshProcessPersistence'}
    if scope == 'full': required.add('channelChange')
    return set(gates) == required and all(value is True for value in gates.values())


def verify_engineering_compile(compilation, expect_failure=False):
    """Review complete native diagnostics independently of the apply state."""
    targets = compilation.get('targets', [])
    require(len(targets) == 2 and {row.get('target') for row in targets} == {'ScratchStation', 'TiaMcpRemoteIo'},
            'Remote-I/O compile must cover both exact device targets.')
    states = {'Success', 'Information', 'Warning', 'Error'}
    for target in targets:
        require(type(target.get('errorCount')) is int and target['errorCount'] >= 0
                and target.get('messagesTruncated') is False and target.get('state') in states
                and isinstance(target.get('messages'), list) and target['messages']
                and all(message.get('state') in states for message in target['messages']),
                'Native compile diagnostics are missing, incomplete or unknown.')
    require(type(compilation.get('errorCount')) is int
            and compilation['errorCount'] == sum(target['errorCount'] for target in targets), 'Native compile error totals differ.')
    errors = any(target['state'] == 'Error' or any(message['state'] == 'Error' for message in target['messages']) for target in targets)
    if expect_failure:
        require(compilation['errorCount'] > 0 and errors, 'Expected native CompileFailed lacks actual complete error diagnostics.')
    else:
        require(compilation['errorCount'] == 0 and not errors, 'Native compile has errors despite a successful operation state.')


def server_relocation(manifest, expected_companion):
    """Explicit fixture change: free DI slots without exempting native slot shifts."""
    require(len(expected_companion) == 2, 'Expected exact server and diagnostic-child contract.')
    server = expected_companion[0]
    require(server['typeIdentifier'] == 'OrderNumber:6ES7 193-6PA00-0AA0/V1.2'
            and server['name'] == 'Servermodul_1' and server['positionNumber'] == 1,
            'Server relocation requires the captured initial slot-1 companion.')
    rows = {row['path']: row for row in manifest}
    require(len(rows) == len(manifest), 'Configured manifest contains duplicate hardware paths.')
    require(all(rows.get(row['path']) == row for row in expected_companion),
            'Created server differs from its independent pre-assignment contract.')
    rack = server['containerPath']
    require(rack in rows, 'Server rack is absent from the complete configured manifest.')
    require(not any(row['containerPath'] == rack and row['positionNumber'] in (3, 4) for row in manifest),
            'Reviewed DI destination 3 and server destination 4 must be vacant.')
    require([row['path'] for row in manifest if row['containerPath'] == rack and row['positionNumber'] == 1] == [server['path']],
            'Initial module slot must contain only the exact generated server.')
    return {'hardwarePath': server['path'], 'positionNumber': 4}


def verified_manifest(result, role, before=False):
    records = result['preview']['backups'] if before else result['readbacks']
    matches = [entry['artifact'] for entry in records if entry['role'] == role]
    require(len(matches) == 1, 'Expected exactly one configured manifest artifact for ' + role)
    artifact = matches[0]
    content = pathlib.Path(artifact['path']).read_bytes()
    require(len(content) == artifact['bytes'] and hashlib.sha256(content).hexdigest() == artifact['sha256'], 'Configured manifest artifact digest differs.')
    return json.loads(content.decode('utf-8-sig'))


def configuration(rows):
    """Independent public-read projection for fresh-worker persistence checks."""
    def attributes(items, names):
        return {row['name']: row.get('value') for row in items if row['name'] in names and 'error' not in row}
    result = []
    for item in rows:
        row = {key: item.get(key) for key in ('path', 'name', 'positionNumber', 'classification', 'isBuiltIn', 'isPlugged')}
        row['attributes'] = attributes(item.get('attributes', []), HARDWARE_ATTRIBUTES)
        row['addresses'] = [{key: address.get(key) for key in ('ioType', 'startAddress', 'length')} for address in item.get('addresses', [])]
        row['channels'] = [{**{key: channel.get(key) for key in ('number', 'type', 'ioType')}, 'attributes': attributes(channel.get('attributes', []), CHANNEL_ATTRIBUTES)} for channel in item.get('channels', [])]
        network = item.get('network')
        if network:
            row['network'] = {
                'nodes': [{**{key: node.get(key) for key in ('name', 'type', 'subnet')}, 'attributes': attributes(node.get('attributes', []), NETWORK_ATTRIBUTES)} for node in network['nodes']],
                'controllers': [{key: controller.get(key) for key in ('controllerIndex', 'ownerHardwarePath', 'ioSystem', 'assignedConnectors')} for controller in network['ioControllers']],
                'connectors': [{key: connector.get(key) for key in ('connectorIndex', 'ownerHardwarePath', 'connectedToIoSystem')} for connector in network['ioConnectors']],
            }
        result.append(row)
    return sorted(result, key=lambda row: row['path'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=pathlib.Path, required=True)
    parser.add_argument('--worker', type=pathlib.Path, required=True)
    parser.add_argument('--artifacts', type=pathlib.Path, required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    parser.add_argument('--scope', choices=QUALIFICATION_SCOPES, default='full',
                        help='Explicit limited lifecycle qualification retains unsupported channel evidence and cannot qualify the full route.')
    args = parser.parse_args()
    require(not args.report.exists(), 'Existing evidence must not be overwritten.')
    directory = args.artifacts.resolve() / ('n_' + uuid.uuid4().hex[:6])
    report = {'schemaVersion': 1, 'startedAtUtc': utc_now(), 'completed': False, 'steps': [], 'gates': {}, 'binaries': {},
              'qualificationScope': args.scope, 'fullRemoteIoRouteQualified': False, 'channelWriteQualified': False,
              'harnessSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              'scope': 'Fresh offline CPU 1511-1 PN and catalog-selected ET 200SP IM/DI; no runtime, shared-device or physical topology qualification.'}
    for role, path in [('host', args.host.with_suffix('.dll')), ('worker', args.worker)]:
        report['binaries'][role] = {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
    client = watcher = None
    attached = False
    plc_path = None
    project_path = None

    def persist():
        save_json(args.report, report)

    def call(name, arguments=None, allow_error=False):
        step = {'tool': name, 'arguments': arguments or {}, 'startedAtUtc': utc_now()}
        report['steps'].append(step)
        persist()
        raw = client.request('tools/call', {'name': name, 'arguments': arguments or {}})
        value = raw.get('structuredContent') or json.loads(raw['content'][0]['text'])
        step.update(result=redact(value), isError=bool(raw.get('isError')), completedAtUtc=utc_now())
        persist()
        print(('ERROR ' if raw.get('isError') else 'PASS ') + name, flush=True)
        require(allow_error or not raw.get('isError'), name + ': ' + json.dumps(value))
        return value

    def connect():
        nonlocal client, watcher, attached
        client = Client(args.host.resolve(), 300, ['--worker', str(args.worker.resolve()), '--artifacts', str(directory)])
        initialize(client)
        started = call('tia_start')
        attached = True
        validate_owned_status(started, started['processId'], require_owner=True)
        watcher = OwnedTiaProcessWatcher(started['processId'])

    def close():
        nonlocal client, watcher, attached
        failures = []
        if attached:
            try:
                detach_owned(call)
            except Exception as error:
                failures.append('Detach: ' + str(error))
            attached = False
        if client:
            owned_client = client
            client = None
            try:
                receipt = owned_client.close()
                report.setdefault('hostExits', []).append(receipt)
                require(receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'], 'Host exit failed.')
                require(receipt['workerExits'] and all(row['processExitVerified'] and row['exitCode'] == 0 for row in receipt['workerExits']), 'Worker exit was not verified.')
            except Exception as error:
                failures.append('Host/worker close: ' + str(error))
        if watcher:
            owned_watcher = watcher
            watcher = None
            try:
                report.setdefault('ownedTiaExits', []).append(owned_watcher.wait_for_exit(60000))
            except Exception as error:
                report.setdefault('ownedTiaExits', []).append(getattr(error, 'evidence', {'failure': str(error)}))
                failures.append('TIA exit: ' + str(error))
        if failures:
            report.setdefault('cleanupFailures', []).extend(failures)
        persist()
        require(not failures, ' | '.join(failures))

    def inventory():
        return call('project_inventory', {'maxItems': 2000})

    def inspect():
        rows = []
        for item in inventory()['hardware']:
            result = call('hardware_inspect', {'hardwarePath': item['path'], 'maxItems': 2000})
            require(not result.get('completeness', {}).get('truncated'), 'Selected hardware read was truncated.')
            rows.extend(result['items'])
        return rows

    def prepare(kind, payload, allow_error=False):
        return call('change_prepare', {'kind': kind, 'plcPath': plc_path, 'payload': payload}, allow_error)

    def apply(kind, payload, prepared=None, prerequisite=False):
        nonlocal project_path
        require(not prerequisite or kind in ('hardware_add', 'network_configure'), 'Only preexisting prerequisite operations may defer compile/save.')
        prepared = prepared or prepare(kind, payload)
        try:
            result = call('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                          'checkpointSave': True, 'compile': not prerequisite, 'save': not prerequisite})
            require(result['state'] == 'applied' and (prerequisite or result['compile']['errorCount'] == 0), 'Prepared mutation did not compile.')
            if not prerequisite:
                verify_engineering_compile(result['compile'])
            if kind in ('hardware_plug', 'hardware_move', 'hardware_address_set', 'hardware_channel_attributes', 'network_io_assign'):
                require('configured_hardware_preservation_verified' in result['stages'], 'Native configured preservation was not verified.')
            return prepared, result
        except Exception as primary_error:
            report['primaryFailure'] = str(primary_error)
            try:
                status = call('change_status', {'changeId': prepared['changeId']})
                if status.get('state') in ('applied', 'recovery_required') and status.get('afterFingerprint') and status.get('recoveryArchive'):
                    # Restore rechecks the actual fingerprint under exclusive
                    # access; an uncertain mutation is never replayed.
                    recovered = call('change_restore', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash']}, allow_error=True)
                    report['failureRecovery'] = recovered
                    if recovered.get('recoveredProject'):
                        project_path = pathlib.Path(recovered['recoveredProject']['path'])
                else:
                    report['failureRecovery'] = {'status': 'blocked', 'reason': 'A completed mutation state, after fingerprint and recovery archive were not established.', 'changeStatus': status}
            except Exception as recovery_error:
                report['failureRecovery'] = {'status': 'failed', 'failure': str(recovery_error)}
            raise

    try:
        connect()
        scratch = call('tia_create_scratch', {'cpuTypeIdentifier': 'OrderNumber:6ES7 511-1AL03-0AB0/V4.1'})
        require(scratch['completed'] and scratch['compile']['errorCount'] == 0, 'Fresh scratch fixture must compile cleanly.')
        project_path = pathlib.Path(scratch['project']['path'])
        report['originalProjectPath'] = str(project_path)
        plc_path = inventory()['plcs'][0]['path']
        catalog = call('hardware_catalog_search', {'filter': 'IM 155-6 PN ST', 'maxItems': 500})
        heads = sorted([row for row in catalog['entries'] if '6ES7 155-6' in (row.get('articleNumber') or '') and 'PN ST' in (row.get('typeName') or '')], key=lambda row: row['typeIdentifier'], reverse=True)
        require(heads, 'No installed ET 200SP IM 155-6 PN ST catalog entry.')
        report['interfaceModule'] = heads[0]
        apply('hardware_add', {'typeIdentifier': heads[0]['typeIdentifier'], 'deviceName': 'TiaMcpRemoteIo', 'itemName': 'TiaMcpRemoteHead'}, prerequisite=True)
        rows = inspect()
        cpu = next(row for row in rows if row.get('network', {}).get('ioControllers'))
        remote = next(row for row in rows if 'TiaMcpRemoteIo' in row['path'] and row.get('network', {}).get('ioConnectors'))
        rails = [row for row in inventory()['hardware'] if 'TiaMcpRemoteIo' in row['path'] and row['classification'] == 'None' and row['positionNumber'] == 0 and row['path'].count('/items/') == 1]
        require(len(rails) == 1, 'The captured remote-I/O profile must expose exactly one root rack at position 0.')
        rail = rails[0]
        require(heads[0]['typeIdentifier'] == 'OrderNumber:6ES7 155-6AU02-0BN0/V6.4', 'The explicit generated-server contract requires the captured IM 155-6AU02 V6.4 profile.')
        head_rows = [row for row in rows if row['name'] == 'TiaMcpRemoteHead' and row['path'].count('/items/') == 1]
        require(len(head_rows) == 1, 'The remote head identity is ambiguous.')
        head = head_rows[0]
        author = next(value['value'] for value in head['attributes'] if value['name'] == 'Author' and 'error' not in value)
        for index, target in enumerate((cpu, remote), start=10):
            apply('network_configure', {'hardwarePath': target['path'], 'nodeName': target['network']['nodes'][0]['name'], 'subnetName': 'RemoteIoProofSubnet',
                                       'attributes': {'Address': '192.0.2.' + str(index), 'SubnetMask': '255.255.255.0'}}, prerequisite=True)
        assignment = {'hardwarePath': remote['path'], 'connectorIndex': 0, 'controllerHardwarePath': cpu['path'], 'controllerIndex': 0, 'createIoSystemName': 'RemoteIoProofSystem',
                      'expectedServerModule': {'name': 'Servermodul_1', 'typeIdentifier': 'OrderNumber:6ES7 193-6PA00-0AA0/V1.2', 'containerHardwarePath': rail['path'], 'positionNumber': 1,
                                               'attributes': {'AdditionalInformation': '', 'Author': author, 'Comment': '', 'LocationIdentifier': '', 'Name': 'Servermodul_1', 'PlantDesignation': ''}}}
        assignment_prepared, assignment_result = apply('network_io_assign', assignment)
        report['gates']['controllerAssignment'] = True
        companion = next(effect['expectedNativeCompanion'] for effect in assignment_prepared['preview']['effects']
                         if 'expectedNativeCompanion' in effect)
        server_move = server_relocation(verified_manifest(assignment_result, 'configured-hardware-preservation'), companion)
        report['fixtureServerRelocation'] = server_move
        # Native05 proved the generated server occupies slot 1 and CanPlugNew
        # rejects all DI candidates there. Move that exact server explicitly,
        # with its own checkpoint, full compile and unchanged-peer manifest.
        # No automatic displacement or generic preservation exception is allowed.
        apply('hardware_move', server_move)
        report['gates']['fixtureServerRelocation'] = True
        modules = call('hardware_catalog_search', {'filter': 'DI 8x24VDC ST', 'maxItems': 500})
        candidates = sorted([row for row in modules['entries'] if '6ES7 131-6' in (row.get('articleNumber') or '')], key=lambda row: row['typeIdentifier'], reverse=True)
        selected = None
        for candidate in candidates:
            payload = {'hardwarePath': rail['path'], 'typeIdentifier': candidate['typeIdentifier'], 'name': 'RemoteDI', 'positionNumber': 1}
            proposed = prepare('hardware_plug', payload, allow_error=True)
            if proposed.get('changeId'):
                apply('hardware_plug', payload, proposed)
                selected = candidate
                break
            require(proposed.get('error', {}).get('code') == 'IncompatibleHardware', 'Unexpected module preparation failure: ' + str(proposed))
        require(selected, 'No catalog-selected DI module can plug into the reviewed ET 200SP slot.')
        report['digitalInputModule'] = selected
        report['gates']['moduleCreation'] = True
        rows = inspect()
        module = next(row for row in rows if row['name'] == 'RemoteDI')
        apply('hardware_move', {'hardwarePath': module['path'], 'positionNumber': 3})
        report['gates']['slotMove'] = True
        rows = inspect()
        subtree = [row for row in rows if row['path'] == module['path'] or row['path'].startswith(module['path'] + '/')]
        address_item, address = next((row, value) for row in subtree for value in row['addresses'] if value['ioType'] == 'Input' and value['length'] > 0)
        end = max(value['startAddress'] + (value['length'] + 7) // 8 for row in rows for value in row['addresses'] if value['ioType'] == 'Input')
        address_payload = {'hardwarePath': address_item['path'], 'ioType': 'Input', 'oldStartAddress': address['startAddress'], 'startAddress': ((end + 15) // 16) * 16 + 16}
        _, address_result = apply('hardware_address_set', address_payload)
        report['gates']['addressChange'] = True
        channel_choice, report['channelEvidence'] = channel_gate(subtree, args.scope)
        persist()
        if args.scope == 'full':
            require(channel_choice, 'No reviewed native channel attribute exposes a distinct typed value; channel change remains unqualified.')
            row, channel, attribute, requested = channel_choice
            channel_payload = {'hardwarePath': row['path'], 'channelType': channel['type'], 'channelIoType': channel['ioType'], 'channelNumber': channel['number'], 'attributes': {attribute['name']: requested}}
            _, channel_result = apply('hardware_channel_attributes', channel_payload)
            compiled_manifest = verified_manifest(channel_result, 'configured-hardware-preservation')
            report['channelChange'] = {'selector': channel_payload, 'before': attribute['value'], 'after': requested}
            report['gates']['channelChange'] = True
            report['channelEvidence'].update(status='qualified', qualified=True)
            report['channelWriteQualified'] = True
        else:
            compiled_manifest = verified_manifest(address_result, 'configured-hardware-preservation')
        before_reopen = configuration(inspect())
        close()
        connect()
        call('project_open', {'path': str(project_path)})
        require(configuration(inspect()) == before_reopen, 'Fresh TIA/worker reopen changed configured hardware/network fields.')
        next_address = {**address_payload, 'oldStartAddress': address_payload['startAddress'], 'startAddress': address_payload['startAddress'] + 32}
        proposed = prepare('hardware_address_set', next_address)
        require(verified_manifest(proposed, 'configured-hardware-preservation', before=True) == compiled_manifest,
                'Fresh TIA/worker configured manifest differs from the exact post-compile manifest.')
        report['gates']['freshProcessPersistence'] = True
        prepared, _ = apply('hardware_address_set', next_address, proposed)
        recovered = call('change_restore', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash']})
        require(recovered['state'] == 'restored' and 'configured_hardware_recovery_verified' in recovered['stages'], 'Archive recovery manifest was not verified.')
        require(verified_manifest(recovered, 'configured-hardware-recovery') == compiled_manifest, 'Restored native manifest differs from the exact post-compile manifest.')
        project_path = pathlib.Path(recovered['recoveredProject']['path'])
        require(configuration(inspect()) == before_reopen, 'Archive recovery did not restore the exact reviewed configured state.')
        report['gates']['archiveRecovery'] = True
        # An unassigned remote station can be a native compile error. Exercise
        # that negative outcome explicitly; do not weaken the compile policy.
        prepared = prepare('network_io_assign', {'hardwarePath': remote['path'], 'connectorIndex': 0, 'disconnect': True})
        disconnected = call('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                            'checkpointSave': True, 'compile': True, 'save': True}, allow_error=True)
        if disconnected.get('error'):
            require(disconnected['error']['code'] == 'CompileFailed', 'Disconnect failed for an unexpected reason.')
            status = call('change_status', {'changeId': prepared['changeId']})
            require(status['state'] == 'recovery_required' and status.get('afterFingerprint'), 'Disconnect outcome is not reconciled for recovery.')
            verify_engineering_compile(status['compile'], expect_failure=True)
            report['controllerDisconnect'] = {'status': 'rejectedByNativeCompile', 'compile': status['compile']}
        else:
            require(disconnected['state'] == 'applied', 'Disconnect did not complete.')
            verify_engineering_compile(disconnected['compile'])
            report['controllerDisconnect'] = {'status': 'compiled', 'compile': disconnected['compile']}
        disconnected_network = call('hardware_inspect', {'hardwarePath': remote['path'], 'maxItems': 2000})['items'][0]['network']
        require(disconnected_network['ioConnectors'][0]['connectedToIoSystem']['state'] == 'notAssigned', 'Disconnect native readback differs.')
        recovered = call('change_restore', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash']})
        require(recovered['state'] == 'restored' and 'configured_hardware_recovery_verified' in recovered['stages'], 'Disconnect recovery manifest differs.')
        project_path = pathlib.Path(recovered['recoveredProject']['path'])
        require(configuration(inspect()) == before_reopen, 'Disconnect recovery lost configured fields.')
        report['gates']['disconnectOutcomeAndRecovery'] = True
        close()
        connect()
        call('project_open', {'path': str(project_path)})
        require(configuration(inspect()) == before_reopen, 'Recovered fresh TIA/worker reopen changed configured fields.')
        recovered_preview = prepare('hardware_address_set', next_address)
        require(verified_manifest(recovered_preview, 'configured-hardware-preservation', before=True) == compiled_manifest,
                'Recovered fresh TIA/worker manifest differs from the exact compiled state.')
        report['gates']['recoveredFreshProcessPersistence'] = True
        close()
        report['completed'] = qualification_complete(args.scope, report['gates'])
    except Exception as error:
        report['failure'] = str(error)
        print('FAILED ' + str(error)[:1500], flush=True)
    finally:
        try:
            if client or watcher: close()
        except Exception as error:
            report['cleanupFailure'] = str(error)
            report['completed'] = False
        report['completedAtUtc'] = utc_now()
        report['fullRemoteIoRouteQualified'] = args.scope == 'full' and report['completed'] and report['channelWriteQualified']
        persist()
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
