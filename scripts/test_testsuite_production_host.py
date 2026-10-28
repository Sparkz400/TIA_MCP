"""Exercise TS-03 through actual MCP Hosts with both worker paths deliberately absent."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    if args.report.exists():
        parser.error('Use a fresh report path.')
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    root = Path(fixture['artifactRoot'])
    missing = root / 'intentionally-absent-native-worker.exe'
    assert not missing.exists()
    names = {'testsuite_style_' + name for name in ('plan_prepare', 'plan_review', 'job_prepare', 'job_status',
        'job_stop_request', 'job_execute_next', 'job_results', 'job_mark_interrupted', 'job_recover', 'job_reconcile')}
    checks, shutdowns = [], []

    def check(condition, label):
        assert condition, label
        checks.append(label)

    def connect(scope_probe=False):
        client = Client(args.host.resolve(), 30, ['--worker', str(missing), '--sim-worker', str(missing), '--artifacts', str(root),
                                                *(['--testsuite-scope-probe'] if scope_probe else [])])
        try:
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'ts03-offline', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client

    def close(client):
        receipt = client.close()
        check(receipt['hostExitCode'] == 0 and not receipt['workerExits'] and not receipt['forcedHostStop'], 'Host exits without native workers')
        shutdowns.append(receipt)

    def reject(client, tool, arguments, code):
        response = client.request('tools/call', {'name': tool, 'arguments': arguments})
        check(response.get('isError') and response['structuredContent']['error']['code'] == code, code)

    def reject_malformed_json(client, request):
        # The installed MCP SDK rejects malformed UTF-16 before invoking the tool.
        try:
            client.request('tools/call', {'name': 'testsuite_style_plan_prepare', 'arguments': {'request': request}})
        except RuntimeError as error:
            check(isinstance(error.args[0], dict) and error.args[0].get('code') == -32603, 'Malformed UTF-16 refused at MCP transport boundary')
            return
        raise AssertionError('Malformed UTF-16 reached the tool')

    client = connect()
    try:
        tools = client.request('tools/list')['tools']
        check(names <= {t['name'] for t in tools}, 'Ten Test Suite MCP tools registered')
        check('qualification_testsuite_scope_probe' not in {t['name'] for t in tools}, 'Scope-probe tool is absent from normal Hosts')
        check(not any(t['name'].startswith('qualification_testsuite_') for t in tools), 'Assisted qualification tools are absent without the dedicated session flag')
        review = client.tool('testsuite_style_plan_prepare', {'request': fixture['request']})
        check(review['qualifiedProfiles'] == [] and review['nativeExecutionAuthorized'] is False, 'No qualified production profiles')
        # Exact metadata must refuse before any new retained review/operation, not after JSON replacement.
        def files():
            return {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in root.rglob('*') if p.is_file()}
        before_invalid = files()
        for field in ('dependencies', 'unresolvedSyntax'):
            for invalid in ('bad\u0000', 'bad\n', 'bad\ufffe', 'bad\uffff'):
                request = copy.deepcopy(fixture['request'])
                request['definitions'][0]['manifest'][field] = [invalid]
                reject(client, 'testsuite_style_plan_prepare', {'request': request}, 'InvalidDefinition')
            for invalid in ('bad\ud800', 'bad\udc00'):
                request = copy.deepcopy(fixture['request'])
                request['definitions'][0]['manifest'][field] = [invalid]
                reject_malformed_json(client, request)
        request = copy.deepcopy(fixture['request']); request['bad\ud800'] = 'invalid field name'
        reject_malformed_json(client, request)
        for invalid in ('bad\u0000', 'bad\n', 'bad\ufffe', 'bad\uffff'):
            request = copy.deepcopy(fixture['request']); request['executionProfile'] = invalid
            reject(client, 'testsuite_style_plan_prepare', {'request': request}, 'TestSuiteJobLimit')
        check(files() == before_invalid, 'Invalid metadata leaves all retained files unchanged')
        unicode_request = copy.deepcopy(fixture['request'])
        unicode_request['executionProfile'] = 'profile-😀-e\u0301-\ufffd'
        unicode_review = client.tool('testsuite_style_plan_prepare', {'request': unicode_request})
        check(unicode_review['completePlan']['executionProfile'] == unicode_request['executionProfile'], 'Valid Unicode profile retained exactly')
        artifact = review['review']
        full = client.tool('testsuite_style_plan_review', artifact)
        check(full == review['completePlan'] and len(full['runs']) == 1, 'Complete review reopens exactly')
        prepare = {**artifact, 'reviewedSha256': artifact['sha256'], 'idempotencyKey': 'host-restart-job'}
        before_invalid = files()
        for invalid in ('bad\u0000', 'bad\n', 'bad\ufffe', 'bad\uffff'):
            reject(client, 'testsuite_style_job_prepare', {**prepare, 'idempotencyKey': invalid}, 'InvalidRequest')
        check(files() == before_invalid, 'Invalid job keys create no retained operations')
        job = client.tool('testsuite_style_job_prepare', prepare)
        check(client.tool('testsuite_style_job_prepare', prepare)['job']['id'] == job['job']['id'], 'MCP preparation idempotent')
        action = {'jobId': job['job']['id'], 'reviewedSha256': artifact['sha256'], 'operationId': job['operations'][0]['id'], 'expectedRevision': 0}
        reject(client, 'testsuite_style_job_execute_next', action, 'TestSuiteProfileUnqualified')
        reject(client, 'testsuite_style_job_recover', {**action, 'destinations': []}, 'TestSuiteProfileUnqualified')
        reject(client, 'testsuite_style_job_reconcile', action, 'RecoveryRequired')
        check(client.tool('testsuite_style_job_status', {'jobId': action['jobId']})['operations'][0]['revision'] == 0, 'Native refusals do not acquire or change operation')
        result = client.tool('testsuite_style_job_results', {'jobId': action['jobId'], 'operationId': action['operationId']})
        check(result['result'] is None and result['raw'] is None, 'No passing results invented')
    finally:
        close(client)
    client = connect(scope_probe=True)
    try:
        tools = client.request('tools/list')['tools']
        check('qualification_testsuite_scope_probe' in {t['name'] for t in tools}, 'Isolated Host registers only the additional scope probe')
        # Typed before worker launch; close() then asserts that no worker ran.
        reject(client, 'qualification_testsuite_scope_probe', {'action': 'run', 'projectPath': 'D:\\not-a-native-project.ap21'}, 'InvalidRequest')
        other = client.tool('testsuite_style_job_prepare', {**prepare, 'idempotencyKey': 'scope-probe-gate-job'})
        reject(client, 'testsuite_style_job_execute_next', {**action, 'jobId': other['job']['id'],
            'operationId': other['operations'][0]['id'], 'expectedRevision': 0}, 'TestSuiteProfileUnqualified')
    finally:
        close(client)
    client = connect()
    try:
        status = client.tool('testsuite_style_job_status', {'jobId': action['jobId']})
        check(status['nextOperationId'] == action['operationId'], 'Prepared job survives actual Host restart')
        stop = client.tool('testsuite_style_job_stop_request', {'jobId': action['jobId'], 'reviewedSha256': action['reviewedSha256']})
        check(stop['stopRequested'] and stop['operations'][0]['state'] == 'stopped' and stop['nativeCancellationSupported'] is False, 'Persistent stop without cancellation claim')
    finally:
        close(client)
    client = connect()
    try:
        status = client.tool('testsuite_style_job_status', {'jobId': action['jobId']})
        check(status['stopRequested'] and status['nextOperationId'] is None, 'Stop survives actual Host restart')
        reject(client, 'testsuite_style_job_execute_next', {**action, 'expectedRevision': status['operations'][0]['revision']}, 'RecoveryRequired')
    finally:
        close(client)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({'completed': True, 'checkCount': len(checks), 'checks': checks, 'shutdowns': shutdowns,
        'nativeExecuted': False, 'nativeQualified': False}, indent=2) + '\n', encoding='utf-8')
    print(f'PASS TS-03 actual Host: {len(checks)} checks, {len(shutdowns)} Host lifetimes, no native worker launched')


if __name__ == '__main__':
    main()
