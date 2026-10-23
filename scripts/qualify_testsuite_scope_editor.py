"""One owned GUI scratch for independent scope readback; never invokes Test Suite execution."""
import argparse
import copy
from pathlib import Path
import time

from exact_qualification_client import ExactClient
from qualify_exact_worker_startup import reference, package, check_sources, emit, validate_close
from retained_native_lifecycle import require


def run(args):
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=False)
    sources = [reference(Path(__file__).with_name(n)) for n in (
        'qualify_testsuite_scope_editor.py', 'qualify_exact_worker_startup.py', 'exact_qualification_client.py',
        'retained_native_lifecycle.py', 'qualify_worker_startup_exact.py', 'smoke_mcp.py', 'block_test_process.py')]
    packages = {'host': package(args.host), 'worker': package(args.worker)}
    records = sources + packages['host'] + packages['worker']
    report = {'completed': False, 'nativeExecutionInvoked': False, 'scopeQualified': False,
              'automaticReplay': False, 'steps': [], 'sources': sources, 'packages': packages}
    emit(root / 'admission.json', report)
    client, failure = None, None
    try:
        check_sources(records)
        client = ExactClient(args.host.resolve(), 600, ['--worker', str(args.worker.resolve()),
            '--artifacts', str(root / 'h'), '--timeout-seconds', '600', '--testsuite-scope-probe'])
        report['host'] = copy.deepcopy(client.exact_tree.anchor['identity'])
        emit(root / 'host.json', report['host'])
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
            'clientInfo': {'name': 'testsuite-scope-editor', 'version': '1'}})
        client.notify('notifications/initialized')
        def call(name, arguments=None):
            check_sources(records)
            number = len(report['steps'])
            emit(root / f'{number:02}-{name}-intent.json', {'name': name, 'arguments': arguments})
            result = client.tool(name, arguments or {})
            report['steps'].append({'name': name, 'result': result})
            emit(root / f'{number:02}-{name}-result.json', result)
            print(name + ' returned', flush=True)
            return result
        before = call('tia_discover')
        started = call('tia_start')
        require(started['ownsPortalProcess'] is True and started['projects'] == [], 'Owned empty portal required')
        report['nativePid'] = started['processId']
        scratch = call('tia_create_scratch')
        require(scratch['completed'] is True, 'Scratch creation incomplete')
        target = scratch['project']['path']
        prepared = call('qualification_testsuite_scope_probe', {'action': 'prepare', 'projectPath': target})
        require(prepared['processId'] == report['nativePid'] and prepared['styleExecutionInvoked'] is False,
                'Probe ownership or nonexecution contract changed')
        emit(root / 'awaiting-scope-observation.json', {'projectPath': target, 'processId': report['nativePid'],
            'releaseFile': str(root / 'finish-observation'), 'completeEffectiveScope': False})
        print('SCOPE_EDITOR_READY ' + str(root), flush=True)
        # A receipt-file signal ends observation only. It cannot admit execution or qualification.
        # Retain all original process handles while the operator reads the supported scope editor.
        while not (root / 'finish-observation').is_file():
            time.sleep(.25)
        call('qualification_testsuite_scope_probe', {'action': 'read', 'projectPath': target})
        call('tia_detach')
        after = call('tia_discover')
        require(before['processes'] == after['processes'], 'Preexisting native inventory changed')
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
                if failure is None: failure = error
    if failure is None:
        try:
            validate_close(report['close'], report['host'], report['nativePid'], args.host, args.worker)
            check_sources(records)
            report['completed'] = True
        except BaseException as error: failure = error
    if failure is not None: report['failure'] = {'type': type(failure).__name__, 'message': str(failure)}
    emit(root / 'report.json', report)
    print('SCOPE_PROBE_COMPLETED ' + str(report['completed']), flush=True)
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--worker', type=Path, required=True)
    parser.add_argument('--root', type=Path, required=True)
    raise SystemExit(run(parser.parse_args()))
