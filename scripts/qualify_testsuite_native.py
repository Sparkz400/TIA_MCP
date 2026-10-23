"""Run one explicitly selected UI-assisted TS-Q01 case in a newly owned scratch.

The worker writes fresh observation challenges. A separate operator must inspect
TIA and ALM and retain the original screenshots before supplying observations.
This controller never fabricates scope or license evidence and never retries a
native effect. Original process handles are retained through normal shutdown.
"""
import argparse
import copy
from pathlib import Path

from exact_qualification_client import ExactClient
from qualify_exact_worker_startup import reference, package, check_sources, emit, validate_close
from retained_native_lifecycle import require


def run(args):
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=False)
    sources = [reference(Path(__file__).with_name(n)) for n in (
        'qualify_testsuite_native.py', 'qualify_exact_worker_startup.py', 'exact_qualification_client.py',
        'retained_native_lifecycle.py', 'qualify_worker_startup_exact.py', 'smoke_mcp.py', 'block_test_process.py')]
    packages = {'host': package(args.host), 'worker': package(args.worker)}
    records = sources + packages['host'] + packages['worker']
    report = {'completed': False, 'case': args.case, 'productionEnabled': False, 'automaticReplay': False,
              'interruptionMethod': 'explicit worker response-loss injection; no process-crash claim',
              'steps': [], 'sources': sources, 'packages': packages,
              'admission': 'User confirmed the next native test window is available; preexisting instances remain untouched.'}
    emit(root / 'admission.json', report)
    client = None
    failure = None
    try:
        check_sources(records)
        client = ExactClient(args.host.resolve(), 900, ['--worker', str(args.worker.resolve()),
            '--artifacts', str(root / 'h'), '--timeout-seconds', '900', '--testsuite-qualification'])
        report['host'] = copy.deepcopy(client.exact_tree.anchor['identity'])
        emit(root / 'host.json', report['host'])
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
            'clientInfo': {'name': 'testsuite-native-qualification', 'version': '1'}})
        client.notify('notifications/initialized')

        def call(name, arguments=None, expect_loss=False):
            check_sources(records)
            number = len(report['steps'])
            emit(root / f'{number:02}-{name}-intent.json', {'name': name, 'arguments': arguments, 'expectInjectedLoss': expect_loss})
            result = client.request('tools/call', {'name': name, 'arguments': arguments or {}})
            emit(root / f'{number:02}-{name}-wire.json', result)
            report['steps'].append({'name': name, 'wire': result})
            print(name + ' returned' + (' ERROR' if result.get('isError') else ''), flush=True)
            if result.get('isError'):
                require(expect_loss and 'QualificationResponseLost' in str(result), 'Unexpected native/Host error: ' + str(result))
                return None
            require(not expect_loss, 'Expected injected reply loss did not occur')
            import json
            packet = result.get('structuredContent') or json.loads('\n'.join(c['text'] for c in result['content'] if c['type'] == 'text'))
            emit(root / f'{number:02}-{name}-result.json', packet)
            return packet

        before = call('tia_discover')
        started = call('tia_start')
        require(started['ownsPortalProcess'] is True and started['projects'] == [], 'Owned empty portal required')
        report['nativePid'] = started['processId']
        scratch = call('tia_create_scratch')
        require(scratch['completed'] is True, 'Scratch creation incomplete')
        fixture = 'violating' if args.case in ('violating', 'afterRestore') else 'clean'
        prepared = call('qualification_testsuite_prepare', {'projectPath': scratch['project']['path'], 'fixture': fixture,
            'idempotencyKey': root.name + '-' + args.case, 'runs': 2 if args.case == 'stop' else 1})
        status = prepared['status']
        job_id = status['job']['id']
        review = status['job']['reviewSha256']
        op_id = status['operations'][0]['id']

        def action(status):
            op = next(o for o in status['operations'] if o['id'] == op_id)
            return {'jobId': job_id, 'reviewedSha256': review, 'operationId': op_id, 'expectedRevision': op['revision']}

        execute = action(status)
        if args.case in ('beforeRun', 'afterRun'):
            execute['fault'] = args.case
        executed = call('qualification_testsuite_execute', execute, expect_loss=args.case in ('beforeRun', 'afterRun'))
        status = executed or call('testsuite_style_job_status', {'jobId': job_id})
        state = status['operations'][0]['state']
        if args.case in ('clean', 'stop'):
            require(state == 'succeeded', 'Clean fixture did not meet every gate: ' + state)
        elif args.case in ('beforeRun', 'afterRun'):
            require(state == 'outcomeUnknown', 'Lost native reply must remain outcomeUnknown')
        else:
            require(state == 'recoveryRequired', 'Violating fixture must not become passed')
        results = call('testsuite_style_job_results', {'jobId': job_id, 'operationId': op_id})
        if args.case in ('clean', 'stop'):
            require(results['result']['classification'] == 'passed', 'Clean classification mismatch')
        elif args.case in ('violating', 'afterRestore'):
            require(results['result']['classification'] == 'assertionFailure', 'Violation was not independently classified as an assertion failure')
        if args.case == 'stop':
            status = call('testsuite_style_job_stop_request', {'jobId': job_id, 'reviewedSha256': review})
            require([o['state'] for o in status['operations']] == ['succeeded', 'stopped'], 'Stop must preserve first result and prevent second dispatch')
        if args.case not in ('clean', 'stop'):
            recovered = call('qualification_testsuite_recover', {**action(status), 'recoveryLocation': str(root / 'h' / 'recovered'),
                'loseRestoreReply': args.case == 'afterRestore'}, expect_loss=args.case == 'afterRestore')
            status = recovered or call('testsuite_style_job_status', {'jobId': job_id})
            status = call('qualification_testsuite_reconcile', action(status))
            require(status['operations'][0]['state'] == 'reconciled', 'Independent original/destination reconciliation incomplete')
            require(status['operations'][0]['recoveryResolutions'][0]['disposition'] == 'verifiedRestored', 'Recovered copy not verified')
        report['finalStatus'] = status
        call('tia_detach')
        after = call('tia_discover')
        require(before['processes'] == after['processes'], 'Preexisting native inventory changed; retain this concurrent-arrival failure')
        report['preexistingPreserved'] = True
    except BaseException as error:
        failure = error
    finally:
        if client is not None:
            try:
                report['close'] = client.close()
                report['hostLogs'] = list(client.errors)
            except BaseException as error:
                report['cleanupFailure'] = str(error)
                if failure is None:
                    failure = error
    if failure is None:
        try:
            validate_close(report['close'], report['host'], report['nativePid'], args.host, args.worker)
            check_sources(records)
            report['completed'] = True
        except BaseException as error:
            failure = error
    if failure is not None:
        report['failure'] = {'type': type(failure).__name__, 'message': str(failure)}
    emit(root / 'report.json', report)
    print('TS_Q01_COMPLETED ' + str(report['completed']), flush=True)
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--worker', type=Path, required=True)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--case', choices=['clean', 'violating', 'stop', 'beforeRun', 'afterRun', 'afterRestore'], required=True)
    raise SystemExit(run(parser.parse_args()))
