"""TS-Q03 exploratory system-test runner; no system result profile is qualified yet.

Creates its own scratch in a qualification-only TIA UI, pauses for the operator to configure
anonymous OPC UA access, and uses only its ticket-owned TCP/IP PLCSIM instance. It never adopts
an existing project, changes machine networking or automates certificate/security UI decisions.
Native results and preservation/recovery are retained; even a native Success cannot qualify 1.0.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import uuid

from block_test_runtime import save_generated_scratch_project, verify_credential_provisioning, verify_download, verify_owned_instance
from library_native_harness import Harness, require

PROFILE = 'v21.owned-plcsim.system.qualification-v1'
LIMITATIONS = [
    'Exploratory TS-Q03 only: candidate .tst syntax, native scope and result tree need independent qualification.',
    'Operator configures anonymous OPCUAServerAccess and certificate trust on this owned scratch; no existing project is adopted.',
    'TCP/IP single-adapter mode must already be configured; the runner never changes machine networking.',
    'One exact case per Run; production admission remains closed. No claimed qualified pass or release acceptance.',
    'The simulator ticket expires after 15 minutes; an expired ticket refuses rather than being renewed or replayed.',
]


def operator_ready(h, phase, instruction):
    # These are operator acknowledgements, not machine proof of settings or authorization for another target.
    print(instruction, flush=True)
    require(input('Type READY after completing this step, or anything else to stop: ').strip() == 'READY', 'Operator setup not completed')
    h.report.setdefault('operatorSetup', []).append({'phase': phase, 'acknowledged': True, 'independentProof': False})
    h.persist()


def exploratory_result(results):
    require(results.get('raw') is not None, 'The exact native result must be retained')
    classification = results.get('result', {}).get('classification')
    require(classification in ('executionFailure', 'incompleteEvidence'),
            'An unqualified system result cannot pass or establish an assertion profile: ' + str(classification))
    return classification


def exercise(h, prepared, release, case):
    status = prepared['status']
    job, review, operation = status['job']['id'], status['job']['reviewSha256'], status['operations'][0]['id']

    def action(current):
        row = next(o for o in current['operations'] if o['id'] == operation)
        return dict(jobId=job, reviewedSha256=review, operationId=operation, expectedRevision=row['revision'])

    def current():
        return h.tool('testsuite_style_job_status', {'jobId': job})

    error = h.call_error('testsuite_style_job_execute_next', action(status))
    require(error.get('code') == 'TestSuiteProfileUnqualified', 'Production must refuse TS-Q03')
    require(current()['operations'][0]['revision'] == 0, 'Production refusal changed the operation')
    if case == 'released':
        release()
        error = h.call_error('qualification_testsuite_execute', action(status))
        require(error.get('code') == 'SimulationTargetMismatch', 'A released target must refuse')
        final = current()['operations'][0]
        require(final['state'] == 'outcomeUnknown' and not final['checkpoints'], 'Released refusal must precede checkpoint/Run')
        h.report['finalOperation'] = final
        return
    if case == 'afterRun':
        error = h.call_error('qualification_testsuite_execute', {**action(status), 'fault': 'afterRun'})
        require(error.get('code') == 'QualificationResponseLost', 'Expected exact response-loss boundary')
    else:
        h.stage('exploratory system dispatch', lambda: h.tool('qualification_testsuite_execute', action(status)))
    status = current()
    require(status['operations'][0]['state'] == ('outcomeUnknown' if case == 'afterRun' else 'recoveryRequired'),
            'An exploratory run must not finish as succeeded')
    results = h.tool('testsuite_style_job_results', {'jobId': job, 'operationId': operation})
    if case != 'afterRun':
        h.report['classification'] = exploratory_result(results)
        raw = h.directory / results['raw']['relativePath']
        h.report['rawResults'] = json.loads(raw.read_text(encoding='utf-8'))
    recovered = h.stage('restore checkpoint', lambda: h.tool('qualification_testsuite_recover',
        {**action(status), 'recoveryLocation': str(h.directory / 'rc')}))
    reconciled = h.stage('reconcile original and recovered fixture', lambda: h.tool('qualification_testsuite_reconcile', action(recovered)))
    final = reconciled['operations'][0]
    require(final['state'] == 'reconciled' and final['recoveryResolutions'][0]['disposition'] == 'verifiedRestored', 'Recovery not proven')
    h.report['finalOperation'] = final


def body(h, args):
    h.plc = h.owned_scratch('TS-Q03 system ' + args.case)
    h.watch_owned_tia()
    project = h.scratch['project']['path']
    h.apply('project_simulation_enable', {'enabled': True})
    h.apply('opcua_server_settings_set', {'serverEnabled': {'expected': False, 'value': True},
        'runtimeLicense': {'expected': 'NoLicense', 'value': 'SimaticOpcUaS71500Small'}})
    operator_ready(h, 'scratch-access', 'In ONLY the new qualification project ' + project +
        ': grant the anonymous user OPCUAServerAccess, verify the CPU address is ' + args.cpu_ip +
        ', keep the four generated blocks and empty Test Suite groups, then save. Configure PLCSIM TCP/IP mode before proceeding.')
    # The worker will still verify actual scope, ownership and route. Acknowledgement does not replace those checks.
    created = h.stage('create owned TCPIP instance', lambda: h.tool('sim_create',
        {'cpuType': 'CPU1511', 'creationId': uuid.uuid4().hex, 'communicationInterface': 'TCPIP'}))
    instance = verify_owned_instance(created['instance'])
    released = False

    def release():
        nonlocal released
        if released:
            return
        for action in ('stop', 'powerOff'):
            try:
                h.stage('owned simulator ' + action, lambda: h.tool('sim_state', {'instanceId': instance['id'], 'action': action, 'timeoutMs': 60000}))
            except Exception:
                pass  # The owned release below must still succeed; its refusal fails the run.
        result = h.stage('release owned instance', lambda: h.tool('sim_release', {'instanceId': instance['id'], 'deleteOwned': True}))
        require(result.get('unregistered') is True, 'Owned simulator release failed')
        released = True

    try:
        require(instance.get('operatingState') == 'Off', 'Fresh OFF instance required')
        cpus = h.scratch['scratchSecurity']['cpus']
        require(len(cpus) == 1, 'One scratch credential required')
        ticket = {'downloadTicket': created['downloadTicket'], 'credentialMetadataPath': cpus[0]['credentialMetadataPath']}
        download = h.stage('download own scratch', lambda: h.tool('simulation_download', ticket))
        verify_download(download, instance)
        h.report['generatedSave'] = save_generated_scratch_project(h.tool, download)
        provisioned = h.stage('provision own empty CPU', lambda: h.tool('simulation_provision_scratch_credential',
            {**ticket, 'trustOwnedEmptyCpuCertificate': args.trust_owned_empty_cpu_certificate}))
        h.report['provisioning'] = verify_credential_provisioning(provisioned, download,
            trust_owned_empty_cpu_certificate=args.trust_owned_empty_cpu_certificate)
        h.tool('sim_state', {'instanceId': instance['id'], 'action': 'powerOn', 'timeoutMs': 60000})
        running = h.tool('sim_state', {'instanceId': instance['id'], 'action': 'run', 'timeoutMs': 30000})
        require(running['instance']['operatingState'] == 'Run', 'Owned instance must be in RUN')
        h.report['gateReadback'] = h.tool('sim_read', {'instanceId': instance['id'], 'signals': [{'symbol': '"TiaMcp_EnableGate_DB".Active', 'type': 'bool'}]})
        values = h.report['gateReadback'].get('values', [])
        require(len(values) == 1 and values[0].get('value') is True, 'The independent simulator read must observe Active = TRUE')
        address = 'opc.tcp://' + args.cpu_ip + ':4840/'
        operator_ready(h, 'certificate-trust', 'Establish Test Suite trust for the server certificate of ONLY this owned simulator at ' + address +
            '. Keep the operator available for native certificate prompts. Do not execute or add test cases manually.')
        fixture = 'systemFail' if args.case == 'explore-fail' else 'systemPass'
        prepared = h.stage('prepare exact system fixture', lambda: h.tool('qualification_testsuite_prepare',
            {'projectPath': project, 'fixture': fixture, 'idempotencyKey': h.directory.name, 'simulatorTicket': created['downloadTicket'], 'serverAddress': address}))
        require(prepared['observation']['executionProfile'] == PROFILE and prepared['observation']['nativeExecuted'] is False, 'Prepare may not Run')
        h.report['prepare'] = prepared
        # Exercise the public system-plan tool too; the generated review must be identical to qualification preparation.
        review = json.loads((h.directory / prepared['review']['relativePath']).read_text(encoding='utf-8'))
        run = review['runs'][0]
        public = h.tool('testsuite_system_plan_prepare', {'request': {'target': run['operation']['target'], 'executionProfile': PROFILE,
            'simulatorTicket': created['downloadTicket'], 'serverAddress': address, 'wholeGroup': False,
            'definitions': [{'manifest': run['run']['definitions'][0], 'originalArtifact': prepared['observation']['definition']['originalArtifact']}],
            'runs': [{'selectedDefinitionIds': [run['run']['definitions'][0]['id']], 'excludedDefinitionIds': []}]}})
        require(public['review']['sha256'] == prepared['review']['sha256'], 'Public system plan differs from exact qualification review')
        exercise(h, prepared, release, args.case)
        h.report.update(nativeQualified=False, resultProfileQualified=False, releaseAccepted=False)
    finally:
        release()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('host', 'worker', 'sim-worker', 'artifacts'):
        parser.add_argument('--' + option, type=Path, required=True)
    parser.add_argument('--case', choices=['explore-pass', 'explore-fail', 'afterRun', 'released'], required=True)
    parser.add_argument('--cpu-ip', default='192.168.0.1')
    parser.add_argument('--timeout', type=int, default=900)
    parser.add_argument('--run-native', action='store_true')
    parser.add_argument('--trust-owned-empty-cpu-certificate', action='store_true')
    args = parser.parse_args()
    import ipaddress
    require(str(ipaddress.IPv4Address(args.cpu_ip)) == args.cpu_ip, 'Canonical IPv4 address required')
    require(args.trust_owned_empty_cpu_certificate, 'Explicit first-use certificate trust of this run-owned empty simulator is required')
    args.runner = __file__
    args.host_args = ['--testsuite-qualification', '--timeout-seconds', str(args.timeout)]
    Harness(args, 'tq3_', 'TS-Q03 exploratory system test', LIMITATIONS, []).run(lambda h: body(h, args))


if __name__ == '__main__':
    main()
