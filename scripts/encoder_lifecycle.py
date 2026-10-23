"""Encoder harness integration with existing exact process lifetime controllers."""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import uuid
from types import SimpleNamespace

from component_exact_lifetime import ComponentOwnedScratch, hold_unknown_startup, worker_receipt_bound
from created_process_lifetime import CreatedProcess, await_created_terminal
from engineering_preservation import require
from smoke_mcp import Client, ROOT
from encoder_checkpoint import verify_seeder_checkpoint


POLICY = 'encoder-exact-retained-terminal-v1'
TOOLS = {'technology_read', 'engineering_read', 'project_inventory', 'hardware_inspect',
         'hardware_catalog_search', 'project_retrieve', 'device_inventory'}


def encoder_client(host, timeout, arguments, allocated_client=None):
    client = allocated_client if allocated_client is not None else Client.__new__(Client)
    Client.__init__(client, host, timeout, arguments)
    return client


encoder_client.retained_type = Client


def new_session(host, worker, artifacts, report, persist, directory):
    report['lifetimePolicy'] = POLICY
    return ComponentOwnedScratch(host, worker, artifacts, report, persist,
        lifecycle_directory=directory, client_factory=encoder_client, extra_tools=TOOLS)


def sync_steps(report):
    # Preserve the old report's readback navigation while retaining full call intents.
    report['steps'] = [{'name': row['tool'], 'arguments': row['arguments'],
        'isError': row.get('status') != 'passed', 'result': row.get('result'), 'status': row.get('status')}
        for session in report.get('sessions', []) for row in session['calls']]


def verify_lifecycle(report, *, policy=POLICY):
    require(report.get('lifetimePolicy') == policy and report.get('cleanupVerified') is True,
            'Exact cleanup evidence is required; legacy PID observations are insufficient')
    sessions = report.get('sessions')
    require(isinstance(sessions, list) and sessions, 'Owned session evidence is missing')
    for session in sessions:
        cleanup = session.get('cleanup', {})
        shutdown = cleanup.get('launcherShutdown', {})
        tree = shutdown.get('tree', {})
        require(cleanup.get('completed') is True and cleanup.get('errors') == [] and
                cleanup.get('automaticTermination') is False and cleanup.get('workerShutdownReceiptBoundToBirth') is True and
                type(shutdown.get('hostExitCode')) is int and shutdown['hostExitCode'] == 0 and
                shutdown.get('forcedHostStop') is False and shutdown.get('stderrEofObserved') is True and
                shutdown.get('closeInterrupted') is None and tree.get('completed') is True and
                tree.get('forcedTermination') is False and tree.get('startupOutcomeUnknown') is False,
                'Owned Host/worker/native tree cleanup is incomplete or unclean')
        exits = tree.get('exits')
        require(isinstance(exits, list) and {row.get('role') for row in exits} == {'host', 'worker', 'native'} and
                len(exits) == 3, 'One exact Host, worker and native terminal receipt is required')
        require(worker_receipt_bound(shutdown, SimpleNamespace(snapshot=lambda: exits)),
                'Worker shutdown receipt does not match the retained original birth')
        expected_pids = {'host': session.get('hostProcessId'), 'worker': session.get('workerHandle', {}).get('processId'),
                         'native': session.get('processId')}
        require(all(type(pid) is int and pid > 0 for pid in expected_pids.values()) and len(set(expected_pids.values())) == 3,
                'Distinct exact startup process identities are required')
        for row in exits:
            proof = row.get('proof', {})
            require(row.get('identity', {}).get('pid') == expected_pids[row['role']] and
                    proof.get('processId') == expected_pids[row['role']], 'Terminal process identity differs from startup')
            require(proof.get('completed') is True and proof.get('processExitVerified') is True and
                    proof.get('handleClosed') is True and proof.get('sameOsHandleRetained') is True and
                    proof.get('forcedTermination') is False and type(proof.get('exitCode')) is int and proof['exitCode'] == 0 and
                    proof.get('deadlineOrCancellationObserved') is False,
                    'Original process handle did not prove clean terminal completion')
        native = next(row for row in exits if row['role'] == 'native')
        require(native.get('identity', {}).get('pid') == session.get('processId') and
                session.get('handleCapture', {}).get('birthAndImageVerified') is True,
                'Native terminal identity differs from its captured startup')
    for helper in report.get('seederLifetimes', []):
        proof = helper.get('terminal', {})
        require(proof.get('completed') is True and proof.get('processExitVerified') is True and
                proof.get('handleClosed') is True and proof.get('sameCreateProcessHandle') is True and
                proof.get('birthAndImageVerified') is True and proof.get('forcedTermination') is False and
                type(proof.get('exitCode')) is int and proof['exitCode'] == 0 and
                proof.get('deadlineOrCancellationObserved') is False and helper.get('unknown') == [] and
                'captureFailure' not in helper and 'launchFailure' not in helper and
                proof.get('identity') == helper.get('start', {}).get('identity') and bool(proof.get('identity')),
                'Seeder original process identity or terminal evidence is incomplete')


def finish_session(session, report, persist, *, policy=POLICY, after_cleanup=None):
    report['completed'] = False
    report['cleanupVerified'] = False
    try:
        session.close()
        report['cleanupVerified'] = True
        verify_lifecycle(report, policy=policy)
    except BaseException as error:
        report['cleanupVerified'] = False
        report['cleanupFailure'] = type(error).__name__ + ': ' + str(error)
        raise
    finally:
        persist()
    try:
        if after_cleanup is not None:
            after_cleanup()
        report['completed'] = report.get('qualificationChecksPassed') is True and 'failure' not in report
    except BaseException as error:
        report['postCleanupFailure'] = type(error).__name__ + ': ' + str(error)
        raise
    finally:
        persist()


def launch_helper(command, **kwargs):
    process = subprocess.Popen.__new__(subprocess.Popen)
    try:
        subprocess.Popen.__init__(process, command, **kwargs)
    except BaseException as error:
        if getattr(process, '_handle', None) is None:
            if isinstance(error, OSError) and not getattr(process, '_child_created', False):
                raise
            hold_unknown_startup('Encoder helper constructor retained no exact process handle')
        return process, error
    return process, None


def run_seeder(session, executable, expected_hash, mode, authorization, directory, report, persist):
    require(mode in ('inspect', 'connect'), 'Unknown encoder seeder mode')
    session.guard()
    session.tree.await_capture_complete()
    require(session.watcher.api.wait(session.watcher._handle, 0) == 258 and
            authorization.get('processId') == session.session['processId'] and
            str(Path(authorization.get('projectPath', '')).resolve()) == session.current_project,
            'Seeder authorization differs from the live exact owned session')
    require(hashlib.sha256(executable.read_bytes()).hexdigest() == expected_hash, 'Seeder binary changed before dispatch')
    prefix = Path(directory) / (mode + '-' + uuid.uuid4().hex[:8])
    auth_path = prefix.with_suffix('.authorization.json')
    auth_path.write_text(json.dumps(authorization, indent=2), encoding='utf-8')
    packet = {'mode': mode, 'authorization': str(auth_path), 'seederSha256': expected_hash,
              'authorizationSha256': hashlib.sha256(auth_path.read_bytes()).hexdigest(),
              'authorizationProcessId': authorization['processId'], 'authorizationProjectPath': authorization['projectPath'],
              'dispatchAttempted': True, 'automaticReplay': False, 'unknown': [], 'startCallCount': len(session.session['calls'])}
    report.setdefault('seederLifetimes', []).append(packet); persist()
    def uncertain(value):
        packet['unknown'].append(value); persist()
    stdout_path, stderr_path = prefix.with_suffix('.stdout.raw'), prefix.with_suffix('.stderr.raw')
    packet.update(stdout=str(stdout_path), stderr=str(stderr_path))
    with stdout_path.open('xb') as stdout, stderr_path.open('xb') as stderr:
        process, launch_error = launch_helper([str(executable.resolve()), mode, str(auth_path)], cwd=ROOT,
            stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        if launch_error is not None:
            packet['launchFailure'] = type(launch_error).__name__ + ': ' + str(launch_error)
        lifetime = None
        try:
            lifetime = CreatedProcess(process, executable)
            packet['start'] = copy.deepcopy(lifetime.proof)
            persist()
        except BaseException as error:
            packet['captureFailure'] = type(error).__name__ + ': ' + str(error)
        finally:
            packet['terminal'] = await_created_terminal(process, lifetime, 180, uncertain)
            persist()
        require(packet['terminal'].get('completed') is True and packet['terminal'].get('exitCode') == 0 and
                'captureFailure' not in packet and launch_error is None, 'Seeder outcome or original process lifetime is unqualified')
    require(hashlib.sha256(executable.read_bytes()).hexdigest() == expected_hash, 'Seeder binary changed during execution')
    require(stdout_path.stat().st_size <= 4 * 1024 * 1024, 'Seeder result exceeds the inspection bound; raw bytes retained')
    output = stdout_path.read_bytes()
    require(len(output) <= 4 * 1024 * 1024, 'Seeder output grew beyond its bound')
    packet['outcomeSha256'] = hashlib.sha256(output).hexdigest()
    packet['outcomeBytes'] = len(output)
    value = json.loads(output.decode('utf-8').strip().splitlines()[-1])
    require(isinstance(value, dict), 'Seeder result must be an object')
    value['seederSha256'] = expected_hash
    packet['outcomeCaptured'] = True
    report['seeder' + mode.title()] = value; persist()
    if mode == 'connect':
        packet['checkpointVerified'] = False
        verify_seeder_checkpoint(report)
        packet['checkpointVerified'] = True; persist()
    session.guard()
    packet['endCallCount'] = len(session.session['calls']); persist()
    require(value.get('completed') is True, value)
    return value
