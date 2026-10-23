"""Independently prove the selected encoder Boolean edit from retained native XML."""
import argparse
import hashlib
import json
import pathlib

import engineering_preservation
from engineering_preservation import require, native_payload_hash
from audit_engineering_preservation import verified_artifact
from encoder_lifecycle import verify_lifecycle, sync_steps
import encoder_copy_admission as copy_admission
import encoder_software_preservation as software_peers
import encoder_hardware_preservation as hardware_peers
from block_test_runtime import NativeToolError
from run_block_tests import same_user_project_lock
from compilation_evidence import require_clean_compile, fixture_plc_selection
from qualify_encoder_hardware import require_source_compiles
from encoder_endpoints import POLICY as ENDPOINT_POLICY, endpoint, require_endpoint_calls, same


from encoder_software_preservation import INTERFACE, LOCATOR, expected_encoder_edit


def native_export(value):
    data = verified_artifact(value['artifact'])
    xml = data.decode('utf-8-sig')
    require(xml == value['xml'], 'Report XML differs from byte-verified native export')
    return xml


def audit(source, result):
    result['configuredHardwarePeersVerified'] = False
    result['softwarePeersVerified'] = False
    result['fullProjectPreservationVerified'] = False
    result['compilationEvidenceVerified'] = False
    result['compilerTargetCoverageVerified'] = False
    result['freshBaselineCompilationVerified'] = False
    result['exactLifecycleVerified'] = False
    result['endpointCorrespondenceVerified'] = False
    result['closedCopyAdmissionVerified'] = False
    result['closedSourceSnapshotsVerified'] = False
    if 'lifetimePolicy' in source:
        verify_lifecycle(source)
        result['exactLifecycleVerified'] = True
    before, after = source['nativeBefore'], source['edit']['nativeAfter']
    baseline_xml, changed_xml = native_export(before), native_export(after)
    differences = source['parameterDifferences']
    require(set(differences) == {'Sensor.InverseDirection'}, 'Unexpected parameter metadata changes')
    delta = differences['Sensor.InverseDirection']
    require(delta['before']['value'] is False and delta['after']['value'] is True, "Native evidence requirement failed: delta['before']['value'] is False and delta['after']['value'] is True")
    require(delta['before']['type'] == delta['after']['type'] == 'System.Boolean', "Native evidence requirement failed: delta['before']['type'] == delta['after']['type'] == 'System.Boolean'")
    require({**delta['before'], 'value': True} == delta['after'], "Native evidence requirement failed: {**delta['before'], 'value': True} == delta['after']")
    require(same(source['profile']['parameterEdit'], {'Sensor.InverseDirection': True}), "Native evidence requirement failed: source['profile']['parameterEdit'] == {'Sensor.InverseDirection': True}")
    expected = expected_encoder_edit(baseline_xml)
    result['exactEdit'] = {'locator': LOCATOR, 'namespace': INTERFACE,
                           'nativeBefore': before['artifact'], 'nativeAfter': after['artifact'],
                           'baselinePayloadSha256': native_payload_hash(baseline_xml),
                           'expectedPayloadSha256': native_payload_hash(expected),
                           'actualPayloadSha256': native_payload_hash(changed_xml),
                           'transformation': 'Add one StartValue element containing true beneath the selected previously empty Bool member',
                           'falseBaselineEvidence': delta['before'], 'trueReadbackEvidence': delta['after']}
    result['exactEdit']['passed'] = result['exactEdit']['expectedPayloadSha256'] == result['exactEdit']['actualPayloadSha256']
    require(result['exactEdit']['passed'], 'Native encoder XML changed beyond the selected Boolean start value')
    require(native_payload_hash(baseline_xml) != native_payload_hash(changed_xml), 'Qualification requires a real native edit')
    def same_connections(first, second):
        if source.get('endpointPolicy') == ENDPOINT_POLICY:
            return same(endpoint(first, source['profile']), endpoint(second, source['profile']))
        return same(first['hardwareConnections'], second['hardwareConnections'])
    require(same_connections(before, after), 'Hardware connections changed during the edit')
    applied = source['edit']['applied']
    require(applied['state'] == 'applied', 'Native encoder change was not applied')
    require_clean_compile(applied.get('compile'))
    require('changed_project_saved' in applied['stages'], "Native evidence requirement failed: 'changed_project_saved' in applied['stages']")
    result['editCompilation'] = applied['compile']
    recovery = source['recovery']
    require(recovery['restored']['state'] == 'restored', "Native evidence requirement failed: recovery['restored']['state'] == 'restored'")
    checkpoint = {'path': recovery['restored']['recoveryArchive'], 'sha256': recovery['restored']['recoveryArchiveSha256']}
    verified_artifact(checkpoint)
    recovered = recovery['nativeRecovered']
    recovered_xml = native_export(recovered)
    result['recovery'] = {'archive': checkpoint, 'nativeRecovered': recovered['artifact'],
                          'expectedPayloadSha256': native_payload_hash(baseline_xml),
                          'actualPayloadSha256': native_payload_hash(recovered_xml),
                          'compile': recovery['compile']}
    result['recovery']['passed'] = result['recovery']['expectedPayloadSha256'] == result['recovery']['actualPayloadSha256']
    require(result['recovery']['passed'], 'Recovered native payload differs')
    require_clean_compile(recovery.get('compile'))
    if 'lifetimePolicy' in source:
        require_source_compiles(source)
        result['compilerTargetCoverageVerified'] = True
        result['freshBaselineCompilationVerified'] = True
        result['endpointCorrespondenceVerified'] = True
        result['configuredHardwarePeersVerified'] = source.get('hardwarePeerPolicy') == hardware_peers.POLICY
        if source.get('softwarePeerPolicy') == software_peers.POLICY:
            for value in (before, after, recovered, recovery['reopen']['nativeReopened'], source['saveReopen']['nativeReopened']):
                software_peers.verify_exports(value)
            result['softwarePeersVerified'] = True
        if 'closedCopies' in source:
            require(source.get('completed') is True and source.get('qualificationChecksPassed') is True and
                    same(source['closedCopies'], copy_admission.capture_closed(source, pathlib.Path(source['runDirectory'])/'audit-source-location.json')),
                    'Original closed encoder snapshots differ')
            result['closedSourceSnapshotsVerified'] = True
    result['compilationEvidenceVerified'] = True
    require(same_connections(before, recovered), "Native evidence requirement failed: before['hardwareConnections'] == recovered['hardwareConnections']")
    result['savedReopens'] = []
    for label, record, expected_xml in [
        ('recovered', recovery.get('reopen'), baseline_xml),
        ('changed', source.get('saveReopen'), changed_xml),
    ]:
        if record is None:
            continue
        value = record['nativeReopened']
        actual_xml = native_export(value)
        require(same_connections(before, value), 'Saved encoder endpoint changed on reopen')
        row = {'copy': label, 'nativeExport': value['artifact'], 'expectedPayloadSha256': native_payload_hash(expected_xml),
               'actualPayloadSha256': native_payload_hash(actual_xml)}
        row['passed'] = row['expectedPayloadSha256'] == row['actualPayloadSha256']
        result['savedReopens'].append(row)
        require(row['passed'], 'Saved encoder copy changed on reopen: ' + label)
    result['saveReopenCoverage'] = 'both copies verified' if len(result['savedReopens']) == 2 else 'not available in this source report'


def audit_companion(source, companion, result):
    verify_lifecycle(source)
    require_source_compiles(source)
    verify_lifecycle(companion)
    copy_admission.audit_copies(source, companion, result['sourceReportSha256'])
    result['closedCopyAdmissionVerified'] = True
    require(companion['completed'] is True and companion['readOnly'] is True, "Native evidence requirement failed: companion['completed'] is True and companion['readOnly'] is True")
    require(companion['sourceReport']['sha256'] == result['sourceReportSha256'], 'Companion refers to a different native run')
    require(companion['priorOwnedProcess']['exited'] is True, "Native evidence requirement failed: companion['priorOwnedProcess']['exited'] is True")
    require(companion['priorOwnedProcess']['processId'] == source['nativeBefore']['processId'], "Native evidence requirement failed: companion['priorOwnedProcess']['processId'] == source['nativeBefore']['processId']")
    require(set(companion['copies']) == {'recovered', 'changed'}, "Native evidence requirement failed: set(companion['copies']) == {'recovered', 'changed'}")
    read_tools = {'tia_discover', 'tia_start', 'tia_session_status', 'project_open', 'technology_read', 'artifact_read', 'engineering_read',
                  'project_inventory', 'hardware_inspect', 'tia_detach', 'software_inspect', 'block_read', 'type_read', 'tag_table_read'}
    flattened = {'sessions': companion['sessions']}
    sync_steps(flattened)
    require(companion['steps'] == flattened['steps'], 'Companion steps differ from retained session calls')
    require(all(step['name'] in read_tools for step in companion['steps']), 'Companion dispatched a non-read-only tool')
    require(len(companion['sessions']) == 2, 'Companion requires exactly two fresh owned sessions')
    process_ids = [source['nativeBefore']['processId']]
    for label, expected in [('recovered', source['nativeBefore']), ('changed', source['edit']['nativeAfter'])]:
        copy = companion['copies'][label]
        value = copy['nativeReadback']
        require(copy['passed'] is True, 'Companion copy checks failed')
        sessions = [session for session in companion['sessions'] if session['processId'] == copy['processId']]
        require(len(sessions) == 1, 'Copy does not identify one retained owned session')
        calls = sessions[0]['calls']
        inventories = [(i, row['result']) for i, row in enumerate(calls) if row['tool'] == 'project_inventory' and row.get('status') == 'passed']
        require(len(inventories) == 3 and inventories[0][1] == copy.get('nativeInventory'), 'Companion inventory differs from original read')
        inventory_index, inventory = inventories[0]
        selector = fixture_plc_selection(inventory, copy['projectPath'], copy['processId'])
        require(selector == copy.get('selector'), 'Companion selection differs from inventory')
        reads = [(i, row) for i, row in enumerate(calls) if row['tool'] == 'engineering_read' or row['tool'] == 'technology_read' and row['arguments'].get('technologyName') == 'TiaMcp_EncoderConnected_Fixture']
        require(len(reads) == 2 and all(i > inventory_index for i, _ in reads), 'Companion reads lack prior owner inventory')
        for _, read in reads:
            arguments = read['arguments'].get('selectors', {}) if read['tool'] == 'engineering_read' else read['arguments']
            require(all(arguments.get(key) == val for key, val in selector.items()) and
                    read['result'].get('project', {}).get('path') == copy['projectPath'] and read['result'].get('processId') == copy['processId'],
                    'Companion original read changed its selected project/PLC')
            if read['tool'] == 'engineering_read':
                require(all(isinstance(row.get('path'), str) and row['path'].startswith(selector['plcPath'] + '/technology/')
                            for row in read['result'].get('technologyObjects', [])), 'Companion parameter object belongs to another PLC')
        require(value.get('plcPath') == selector['plcPath'], 'Companion native read identifies another PLC')
        attempts = companion['openEvidence'][label]['projectOpenRetry']['attempts']
        open_calls = [call for call in calls if call['tool'] == 'project_open']
        require(1 <= len(open_calls) == len(attempts) == copy['openAttempts'] <= 31, 'Project-open attempt evidence differs')
        require([row['status'] for row in attempts] == ['rejected'] * (len(attempts) - 1) + ['passed'],
                'Only known rejections followed by one successful open are admissible')
        for index, call in enumerate(calls):
            if call['tool'] == 'project_open':
                require(call['arguments'] == {'path': copy['projectPath']}, 'Open attempt targeted another project')
            if call['status'] == 'passed':
                continue
            owner = companion['lockOwner']
            require(call['status'] == 'failed' and call['tool'] == 'project_open' and
                    isinstance(owner.get('user'), str) and bool(owner['user']) and
                    isinstance(owner.get('computer'), str) and bool(owner['computer']) and
                    same_user_project_lock(NativeToolError(call['tool'], call.get('result', {})), copy['projectPath'], **owner),
                    'Companion contains an unknown failed call')
            following = calls[index + 1] if index + 1 < len(calls) else {}
            status = following.get('result', {})
            require(following.get('tool') == 'tia_session_status' and following.get('status') == 'passed' and
                    status.get('attached') is True and status.get('ownsPortalProcess') is True and
                    status.get('processId') == copy['processId'] and status.get('projects') == [],
                    'Project-lock rejection lacks an immediate exact owned empty-session check')
        require(value['processId'] == copy['processId'] and value['project']['path'] == copy['projectPath'], "Native evidence requirement failed: value['processId'] == copy['processId'] and value['project']['path'] == copy['projectPath']")
        expected_path = source['recoveredProjectPath'] if label == 'recovered' else source['projectPath']
        require(pathlib.Path(copy['sourceProjectPath']).resolve() == pathlib.Path(expected_path).resolve(), "Native evidence requirement failed: pathlib.Path(copy['projectPath']).resolve() == pathlib.Path(expected_path).resolve()")
        starts = [step['result'] for step in companion['steps'] if step['name'] == 'tia_start'
                  and step['result']['processId'] == copy['processId']]
        opens = [step['result'] for step in companion['steps'] if step['name'] == 'project_open' and not step['isError']
                 and step['result']['processId'] == copy['processId']]
        require(len(starts) == len(opens) == 1, 'Native evidence requirement failed: len(starts) == len(opens) == 1')
        require(starts[0]['ownsPortalProcess'] is True and starts[0]['projects'] == [], "Native evidence requirement failed: starts[0]['ownsPortalProcess'] is True and starts[0]['projects'] == []")
        require(opens[0]['opened'] is True and opens[0]['project']['path'] == copy['projectPath'], "Native evidence requirement failed: opens[0]['opened'] is True and opens[0]['project']['path'] == copy['projectPath']")
        process_ids.append(copy['processId'])
        hardware_start = software_peers.require_calls(value, calls)
        hardware_peers.require_calls(value, calls, hardware_start)
        hardware_peers.compare(expected, value)
        software_peers.compare(expected, value)
        software_peers.verify_exports(value)
        actual_xml, expected_xml = native_export(value), native_export(expected)
        row = {'copy': label, 'processId': copy['processId'], 'nativeExport': value['artifact'],
               'expectedPayloadSha256': native_payload_hash(expected_xml), 'actualPayloadSha256': native_payload_hash(actual_xml)}
        row['passed'] = row['expectedPayloadSha256'] == row['actualPayloadSha256']
        result['savedReopens'].append(row)
        require(row['passed'], 'Companion saved-copy native XML differs: ' + label)
        require(value['parameters'] == expected['parameters'], 'Native parameter list changed on reopen')
        require(same(require_endpoint_calls(value, source['profile'], calls), endpoint(expected, source['profile'])),
                'Native hardware endpoint changed on reopen')
        require(same(copy.get('hardwareInspection'), value['endpointEvidence']['hardware']) and
                copy.get('hardwarePath') == value['endpointEvidence']['hardware'][0]['items'][0]['path'],
                'Companion hardware summary differs from original endpoint evidence')
        parameter_reads = [step['result'] for step in companion['steps'] if step['name'] == 'engineering_read'
                           and step['result']['processId'] == copy['processId']]
        require(len(parameter_reads) == 1 and not parameter_reads[0]['truncated'], "Native evidence requirement failed: len(parameter_reads) == 1 and not parameter_reads[0]['truncated']")
        objects = parameter_reads[0]['technologyObjects']
        require(len(objects) == 1 and not objects[0]['parametersTruncated'], "Native evidence requirement failed: len(objects) == 1 and not objects[0]['parametersTruncated']")
        actual_parameters = {item['name']: item for item in objects[0]['parameters']}
        expected_parameters = dict(source['edit']['readback'])
        if label == 'recovered':
            expected_parameters['Sensor.InverseDirection'] = source['parameterDifferences']['Sensor.InverseDirection']['before']
        require(actual_parameters == expected_parameters, 'Native typed parameter metadata differs on reopen')
        row['exactParameterMetadataCount'] = len(actual_parameters)
        row['exactHardwareConnections'] = True
    require(len(set(process_ids)) == 3, 'Companion did not use separate fresh owned TIA processes')
    result['savedCopyProcessIds'] = dict(zip(('apply', 'recoveredReopen', 'changedReopen'), process_ids))
    result['saveReopenCoverage'] = 'Both copies independently verified from the hash-bound read-only companion; original failed report retained'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=pathlib.Path, required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    parser.add_argument('--reopen-report', type=pathlib.Path, help='Optional later read-only companion bound to the source report SHA-256.')
    args = parser.parse_args()
    data = args.input.read_bytes()
    source = json.loads(data.decode('utf-8-sig'))
    result = {'completed': False, 'scope': 'Independent offline proof of the exact selected encoder native edit and available recovery evidence',
              'sourceReport': str(args.input.resolve()), 'sourceReportSha256': hashlib.sha256(data).hexdigest(),
              'sourceReportCompleted': source['completed'], 'sourceFailure': source.get('failure'),
              'auditSourceSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              'comparatorSha256': hashlib.sha256(pathlib.Path(engineering_preservation.__file__).read_bytes()).hexdigest()}
    try:
        audit(source, result)
        if args.reopen_report:
            companion_bytes = args.reopen_report.read_bytes()
            result['reopenCompanion'] = {'path': str(args.reopen_report.resolve()),
                                         'sha256': hashlib.sha256(companion_bytes).hexdigest()}
            audit_companion(source, json.loads(companion_bytes.decode('utf-8-sig')), result)
        result['completed'] = True
    except Exception as error:
        result['failure'] = type(error).__name__ + ': ' + str(error)
        raise
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print('PASS exact native encoder edit audit', flush=True)


if __name__ == '__main__':
    main()
