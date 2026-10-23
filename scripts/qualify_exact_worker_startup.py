"""One empty start/status/detach using the original retained process tree.

No legacy monitor, PID reopen, termination or replay. This controller does not
reconcile earlier failed attempts or grant admission to other native work.
"""
import argparse
import copy
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import time

from exact_qualification_client import ExactClient
from retained_native_lifecycle import require

SOURCE_NAMES = ('qualify_exact_worker_startup.py', 'qualify_worker_startup_exact.py',
                'exact_qualification_client.py', 'retained_native_lifecycle.py',
                'smoke_mcp.py', 'block_test_process.py')


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def reference(path):
    return {'path': str(Path(path).resolve()), 'sha256': digest(path)}


def emit(path, packet):
    path = Path(path)
    require(not path.exists(), 'Evidence receipt already exists')
    pending = path.with_name(path.name + '.pending')
    with pending.open('x', encoding='utf-8') as stream:
        json.dump(packet, stream, indent=2)
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    # Windows rename refuses an existing destination. Failed writes remain
    # explicitly pending; final-name readers never consume unflushed success.
    pending.rename(path)


def package(executable):
    paths = sorted(path for path in Path(executable).resolve().parent.rglob('*') if path.is_file())
    require(0 < len(paths) <= 5000, 'Binary package inventory is empty or exceeds bound')
    return [reference(path) for path in paths]


def check_sources(records):
    require(all(digest(row['path']) == row['sha256'] for row in records), 'Admitted source/package bytes changed')


def check_inputs(args, sources, packages):
    check_sources(sources)
    require(package(args.host) == packages['host'] and package(args.worker) == packages['worker'],
            'Binary package bytes or inventory changed')


def validate_close(close, host, native_pid, host_path, worker_path):
    require(close.get('hostExitCode') == 0 and close.get('forcedHostStop') is False and
            close.get('stderrEofObserved') is True and not close.get('closeInterrupted'), 'Normal Host closure/EOF is incomplete')
    tree = close['tree']
    require(tree.get('completed') is True and tree.get('startupOutcomeUnknown') is False and
            tree.get('forcedTermination') is False and tree.get('workerStartupCaptured') is True and
            tree.get('nativeStartupInventoryComplete') is True and tree.get('expectedWorkerCount') == 1 and
            all(not tree.get(key) for key in ('errors', 'unknownReasons', 'finalizationErrors', 'startupInventoryUnknownReasons')),
            'Exact startup inventory or terminal evidence is incomplete')
    rows = tree.get('exits', [])
    require(sorted(row['role'] for row in rows) == ['host', 'native', 'worker'], 'Exactly one Host, worker and native lifetime required')
    by_role = {row['role']: row for row in rows}
    require(by_role['host']['identity'] == host and Path(host['exe']).resolve() == host_path.resolve(), 'Host identity changed')
    worker, native = (by_role[key]['identity'] for key in ('worker', 'native'))
    require(Path(worker['exe']).resolve() == worker_path.resolve() and worker['parentPid'] == host['pid'] and
            host in by_role['worker']['ancestry'], 'Worker ownership changed')
    require(native['pid'] == native_pid and native['name'] == 'Siemens.Automation.Portal.exe' and
            native['parentPid'] == worker['pid'] and worker in by_role['native']['ancestry'], 'Native ownership changed')
    for row in rows:
        proof, identity = row['proof'], row['identity']
        require(all(proof.get(key) is True for key in ('completed', 'handleCaptured', 'processExitVerified',
                'handleClosed', 'birthAndImageVerified', 'sameOsHandleRetained')) and
                type(proof.get('exitCode')) is int and proof['exitCode'] == 0 and proof.get('forcedTermination') is False and
                proof.get('deadlineOrCancellationObserved') is False and not proof.get('unknownReasons') and
                proof.get('processId') == identity['pid'] and type(proof.get('originalFileTimeTicks')) is int and
                abs((proof['originalFileTimeTicks'] - 116444736000000000) / 10000000 - identity['created']) <= .000002 and
                Path(proof['imagePath']).resolve() == Path(identity['exe']).resolve(), 'Missing, changed, late or forced original-handle proof')
    receipts = close.get('workerExits', [])
    require(len(receipts) == 1, 'Exactly one Host worker exit receipt required')
    receipt = receipts[0]
    require(receipt.get('processId') == worker['pid'] and receipt.get('processExitVerified') is True and
            receipt.get('exitCode') == 0 and receipt.get('forcedTermination') is False and
            abs(datetime.fromisoformat(receipt['startedAtUtc'].replace('Z', '+00:00')).timestamp() - worker['created']) <= .01,
            'Host worker receipt differs from retained identity')


def run(args):
    root = args.root.resolve()
    require(all(not root.is_relative_to(path.resolve().parent) for path in (args.host, args.worker)),
            'Evidence root must be outside the frozen binary packages')
    root.mkdir(parents=True, exist_ok=False)
    sources = [reference(Path(__file__).with_name(name)) for name in SOURCE_NAMES]
    binaries = {'host': reference(args.host), 'worker': reference(args.worker)}
    packages = {'host': package(args.host), 'worker': package(args.worker)}
    records = sources + packages['host'] + packages['worker']
    report = {'completed': False, 'scope': 'empty start/status/detach only', 'automaticReplay': False,
              'steps': [], 'sourceFiles': sources, 'binaries': binaries, 'packages': packages}
    emit(root / 'source-admission.json', {'files': records, 'nativeStarted': False, 'automaticReplay': False})
    client, failure = None, None
    try:
        check_inputs(args, sources, packages)
        host_args = ['--worker', str(args.worker.resolve()), '--artifacts', str(root / 'host')]
        if args.api_dir: host_args += ['--api-dir', str(args.api_dir.resolve())]
        client = ExactClient(args.host.resolve(), args.timeout, host_args)
        # Retain the owner before the first log or request can fail.
        report['host'] = copy.deepcopy(client.exact_tree.anchor['identity'])
        emit(root / 'host-start.json', report['host'])
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                       'clientInfo': {'name': 'exact-startup', 'version': '2'}})
        client.notify('notifications/initialized')
        for ordinal, name in enumerate(('tia_discover', 'tia_start', 'tia_session_status', 'tia_detach', 'tia_discover')):
            check_inputs(args, sources, packages)
            step = {'name': name, 'started': time.time()}
            report['steps'].append(step)
            emit(root / f'step-{ordinal:02}-{name}-intent.json', step)
            step['result'] = client.tool(name)
            step['elapsedSeconds'] = time.time() - step['started']
            emit(root / f'step-{ordinal:02}-{name}-result.json', step)
            if name in ('tia_start', 'tia_session_status'):
                result = step['result']
                require(result.get('ownsPortalProcess') is True and result.get('projects') == [], 'Expected an owned empty native instance')
                if name == 'tia_start': report['ownedProcessId'] = result['processId']
                require(result['processId'] == report['ownedProcessId'], 'Native status identity changed')
                client.exact_tree.one('native', result['processId'])
            # ExactClient.detach has already validated the original native
            # handle before returning. Never construct a second PID watcher.
        require(report['steps'][0]['result']['processes'] == report['steps'][-1]['result']['processes'], 'Preexisting native discovery changed')
        report['preexistingPreserved'] = True
    except BaseException as error:
        failure = error
    finally:
        if client is not None:
            try:
                report['close'] = client.close()
                report['hostLogs'] = list(client.errors)
            except BaseException as error:
                report['cleanupFailure'] = {'type': type(error).__name__, 'error': str(error)}
                if failure is None: failure = error
    try:
        if failure is None:
            validate_close(report['close'], report['host'], report['ownedProcessId'], args.host, args.worker)
            check_inputs(args, sources, packages)
            report['completed'] = True
    except BaseException as error:
        failure = error
    if failure is not None: report['failure'] = {'type': type(failure).__name__, 'error': str(failure)}
    try:
        emit(root / 'result.json', report)
        emit(root / 'startup-verification.json', {'completed': report['completed'],
             'exactOriginalStartupTreeVerified': report['completed'], 'sourceFiles': sources,
             'automaticReplay': False, 'resultSha256': digest(root / 'result.json'), 'independentReviewRequired': True})
    except BaseException:
        if failure is None: raise
    if failure is not None: raise failure
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('host', 'worker', 'root'): parser.add_argument('--' + key, type=Path, required=True)
    parser.add_argument('--api-dir', type=Path, help='TIA V21 PublicAPI net48 directory when TIA is not installed in the default location')
    parser.add_argument('--timeout', type=int, default=120)
    args = parser.parse_args()
    require(args.timeout > 0, 'Positive timeout required')
    report = run(args)
    print(json.dumps({'completed': report['completed'], 'root': str(args.root.resolve())}), flush=True)
    return 0


if __name__ == '__main__': raise SystemExit(main())
