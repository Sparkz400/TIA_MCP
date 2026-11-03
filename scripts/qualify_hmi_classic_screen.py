"""Native Classic RT Advanced capture/compose/batch/rollback checks on an owned configured fixture.

Requires the immutable archive of prepare_hmi_fixture_ui.py. Creates a fresh worker-owned
copy, changes two regular screens, exercises a later native import refusal, and reopens
the saved result in a new Portal. Never runs HMI runtime, scripts or online operations.
"""
import argparse
import json
from pathlib import Path
import time
import xml.etree.ElementTree as ET

from block_test_process import OwnedTiaProcessWatcher
from qualify_engineering_gaps import prepared_archive, retrieve_fits
from qualify_hmi_gate_route import apply_arguments, devices, digest, ended, hmi_target, normalized, root_object, set_root_attribute
from qualify_reliability import ReliabilityClient, decoded, require, wait_for


def read_artifact(reference):
    raw = Path(reference['path']).read_bytes()
    require(digest(raw) == reference['sha256'], 'Retained native artifact changed')
    if 'bytes' in reference:
        require(len(raw) == reference['bytes'], 'Retained native artifact byte count changed')
    return raw.decode('utf-8-sig')


def documents(capture):
    return {path: read_artifact(reference) for path, reference in capture['screens'].items()}


def identity(capture, path, xml):
    return {**capture['identity'], 'kind': 'hmiScreen', 'objectPath': path,
            'name': root_object(xml).findtext('AttributeList/Name')}


def payload(capture, candidates):
    original = documents(capture)
    members = [dict(target=identity(capture, path, original[path]), **candidate) for path, candidate in candidates.items()]
    require(1 <= len(members) <= 2, 'This fixture run requires one or two regular screens')
    result = dict(schemaVersion=1, operation='classic_xml_import', expectedContentHash=capture['contentHash'], **members[0])
    if len(members) > 1:
        result['additionalScreens'] = members[1:]
    return result


def same_documents(expected, actual):
    require(set(expected) == set(actual), 'Regular-screen inventory changed')
    for path in expected:
        require(normalized(expected[path]) == normalized(actual[path]), 'Native screen differs: ' + path)


def run(args):
    from qualify_interruption import admissions, children, read_json
    fixture = prepared_archive(args.fixture)
    require(fixture['profile'] == 'classic-rt-advanced', 'Only the configured RT Advanced fixture is admitted')
    args.base.mkdir(parents=True, exist_ok=False)
    store = args.base / 'h'
    require(retrieve_fits(store), 'Choose a shorter artifact root')
    report = dict(completed=False, nativeExecuted=True, runtimeVerified=False, visualVerified=False,
                  fixture=fixture, hostSha256=digest(args.host.read_bytes()), workerSha256=digest(args.worker.read_bytes()),
                  runnerSha256=digest(Path(__file__).read_bytes()),
                  steps=[], checks=[], lifetimes=[])

    def persist():
        temporary = args.base / 'report.tmp'
        temporary.write_text(json.dumps(report, indent=2), encoding='utf-8')
        temporary.replace(args.base / 'report.json')

    def call(client, label, name, arguments=None, allow_error=False):
        print('START ' + label, flush=True)
        start = time.monotonic()
        raw = client.raw_tool(name, arguments)
        value = decoded(raw)
        reference = args.base / ('%02d-%s.json' % (len(report['steps']), label))
        reference.write_text(json.dumps(raw, indent=2), encoding='utf-8')
        report['steps'].append(dict(label=label, tool=name, seconds=round(time.monotonic() - start, 3),
                                    isError=bool(raw.get('isError')), path=str(reference), sha256=digest(reference.read_bytes())))
        persist()
        require(allow_error or not raw.get('isError'), label + ': ' + json.dumps(value)[:1800])
        print('DONE %s %.3fs' % (label, report['steps'][-1]['seconds']), flush=True)
        return value

    def check(label, action):
        action()
        report['checks'].append(label)
        persist()

    def connect():
        client = ReliabilityClient(args.host, args.worker, store, timeout=900)
        client.initialize()
        return client

    client, watcher, project, expected = None, None, None, None
    try:
        client = connect()
        started = call(client, 'start', 'tia_start')
        watcher = OwnedTiaProcessWatcher(started['processId'])
        project = call(client, 'retrieve', 'project_retrieve', dict(archivePath=fixture['path'], expectedSha256=fixture['sha256']))['project']['path']
        target = devices(client, 'classic')['HMI_RT_1']
        capture = call(client, 'capture-before', 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target))))
        before = documents(capture)
        require(len(before) == 2, 'Expected exactly the two independently seeded regular screens')
        first, second = sorted(before)
        source_items = [item for layer in ET.fromstring(before[first]).iter('Hmi.Screen.ScreenLayer') for item in layer.findall('ObjectList/*')]
        require(source_items and source_items[0].tag == 'Hmi.Screen.Rectangle', 'The fixture needs its seeded rectangle prototype')
        source_name = source_items[0].findtext('AttributeList/ObjectName')
        composed = call(client, 'compose', 'hmi_classic_screen_compose', dict(request=dict(capture=capture['capture'], screenPath=first,
            components=[dict(sourceScreenPath=first, sourceItem=source_name, name='BatchAdded', attributes=dict(Left=160, Top=100))])))
        candidates = {first: dict(xmlArtifact=composed['xmlArtifact']), second: dict(xml=set_root_attribute(before[second], 'BackColor', '200, 210, 220'))}
        expected = {**before, first: read_artifact(composed['xmlArtifact']), second: candidates[second]['xml']}
        prepared = call(client, 'prepare-batch', 'change_prepare', dict(kind='hmi_classic_screen_edit', payload=payload(capture, candidates)))
        apply = apply_arguments(prepared)
        applied = call(client, 'apply-batch', 'change_apply', apply)
        check('two-screen batch applied', lambda: require(applied.get('state') == 'applied', json.dumps(applied)[:1800]))
        journal = json.loads(Path(applied['journalPath']).read_text(encoding='utf-8-sig'))
        check('every member has dispatch and readback evidence', lambda: require(
            all('hmi_screen_%d_%s' % (i, stage) in journal['stages'] for i in (0, 1) for stage in ('import_started', 'readback_verified')),
            'Incomplete member stages'))
        replay = call(client, 'same-key-replay', 'change_apply', apply)
        check('same-key reply preserves the completed journal', lambda: require(replay.get('state') == 'applied' and
            journal == json.loads(Path(applied['journalPath']).read_text(encoding='utf-8-sig')), 'Replay changed native outcome or journal'))
        after = call(client, 'capture-applied', 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target))))
        check('independent exports equal both candidates', lambda: same_documents(expected, documents(after)))

        # The second native import receives an invalid color after the first valid import.
        # No crash injection or production bypass: ordinary Siemens validation must reject it.
        failing = {first: dict(xml=set_root_attribute(expected[first], 'BackColor', '210, 220, 230')),
                   second: dict(xml=set_root_attribute(expected[second], 'BackColor', 'invalid-color'))}
        prepared = call(client, 'prepare-later-failure', 'change_prepare', dict(kind='hmi_classic_screen_edit', payload=payload(after, failing)))
        call(client, 'apply-later-failure', 'change_apply', apply_arguments(prepared), allow_error=True)
        rolled = call(client, 'rollback-status', 'change_status', dict(changeId=prepared['changeId'], includeDetails=True))
        check('later native import failure rolls back', lambda: require(rolled.get('state') == 'rolled_back', json.dumps(rolled)[:1800]))
        stages = rolled['stages']
        check('both attempted screens restore in reverse order', lambda: require(
            stages.index('hmi_screen_1_rollback_readback_verified') < stages.index('hmi_screen_0_rollback_readback_verified') and
            all('hmi_screen_%d_import_started' % i in stages for i in (0, 1)), 'Incorrect rollback order or missing dispatch'))
        restored = call(client, 'capture-rolled-back', 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target))))
        check('rollback restores every regular screen', lambda: same_documents(expected, documents(restored)))
        # A new prepare proves the verified rollback releases the in-process change guard.
        call(client, 'prepare-after-rollback', 'change_prepare', dict(kind='hmi_classic_screen_edit', payload=payload(restored,
            {first: dict(xml=expected[first]), second: dict(xml=expected[second])})))

        # Warm color-only edits avoid comparing a cold component insertion with warm scalar imports.
        for index, path in enumerate((first, second)):
            current = call(client, 'capture-single-%d' % index, 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target))))
            candidate = set_root_attribute(expected[path], 'BackColor', '220, 230, 240')
            single = call(client, 'prepare-single-%d' % index, 'change_prepare', dict(kind='hmi_classic_screen_edit', payload=payload(current, {path: dict(xml=candidate)})))
            result = call(client, 'apply-single-%d' % index, 'change_apply', apply_arguments(single))
            require(result.get('state') == 'applied', 'Separate screen apply did not complete')
            expected[path] = candidate
        current = call(client, 'capture-warm-batch', 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target))))
        same_documents(expected, documents(current))
        warm_expected = {path: set_root_attribute(xml, 'BackColor', '225, 235, 245') for path, xml in expected.items()}
        warm = call(client, 'prepare-warm-batch', 'change_prepare', dict(kind='hmi_classic_screen_edit', payload=payload(current,
            {path: dict(xml=xml) for path, xml in warm_expected.items()})))
        result = call(client, 'apply-warm-batch', 'change_apply', apply_arguments(warm))
        require(result.get('state') == 'applied', 'Warm color-only batch did not complete')
        expected = warm_expected
        measured = {step['label']: step['seconds'] for step in report['steps']}
        report['timing'] = dict(coldComposeBatchPrepareApplySeconds=sum(measured[key] for key in ('prepare-batch', 'apply-batch')),
            warmColorBatchPrepareApplySeconds=sum(measured[key] for key in ('prepare-warm-batch', 'apply-warm-batch')),
            separatePrepareApplySeconds=sum(measured['%s-single-%d' % (phase, i)] for i in (0, 1) for phase in ('prepare', 'apply')),
            samplesPerMode=1, scope='Warm BackColor changes on two screens, singles then batch; excludes capture; not a general performance benchmark')
        persist()

        # Kill only an attached observer Host. Its worker may finish; the owner keeps the same TIA open.
        observer, worker_watch = None, None
        try:
            observer = connect()
            call(observer, 'observer-attach', 'tia_attach', dict(processId=started['processId']))
            workers = [row for row in children(observer.process.pid) if Path(row.get('ExecutablePath') or '').resolve() == args.worker]
            require(len(workers) == 1, 'Cannot identify the exact observer worker')
            worker_watch = OwnedTiaProcessWatcher(int(workers[0]['ProcessId']), query_exit_code=True)
            worker_watch.evidence['basis'] = 'Retained handle captured from the exact attached Host child and verified worker executable before interruption.'
            target2 = devices(observer, 'classic')['HMI_RT_1']
            current = call(observer, 'capture-before-loss', 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target2))))
            same_documents(expected, documents(current))
            lost_expected = {path: set_root_attribute(xml, 'BackColor', '230, 240, 250') for path, xml in expected.items()}
            pending = call(observer, 'prepare-response-loss', 'change_prepare', dict(kind='hmi_classic_screen_edit', payload=payload(current,
                {path: dict(xml=xml) for path, xml in lost_expected.items()})))
            journal_path = Path(pending['journalPath'])
            earlier = {record['id'] for record in admissions(store, 'change_apply')}
            request_id = observer.begin('tools/call', dict(name='change_apply', arguments=apply_arguments(pending)))
            def dispatched():
                journal = read_json(journal_path)
                return journal if journal and 'hmi_screen_0_import_started' in journal.get('stages', []) else None
            interrupted = wait_for(dispatched, 'No native screen dispatch observed', timeout=900)
            require(interrupted.get('state') not in ('applied', 'rolled_back'), 'Missed the in-flight response-loss boundary')
            observer.kill_owned_host()
            report['responseLoss'] = dict(requestId=request_id, hostProcessId=observer.process.pid,
                hostExitCode=observer.process.returncode, observedState=interrupted['state'], observedStages=interrupted['stages'],
                workerExit=worker_watch.wait_for_exit(180000))
            observer.close()
            observer = None
            terminal = read_json(journal_path)
            require(terminal and terminal['state'] == 'applied', 'Interrupted observer worker did not finish the batch')
            expected = lost_expected
            unresolved = [record for record in admissions(store, 'change_apply') if record['id'] not in earlier]
            require(len(unresolved) == 1 and unresolved[0]['state'] in ('dispatchIntent', 'outcomeUnknown'), 'No unique unresolved admission after Host loss')
            report['responseLoss']['admissionBefore'] = unresolved[0]
            persist()
            observer = connect()
            call(observer, 'reconcile-attach', 'tia_attach', dict(processId=started['processId']))
            reconciled = call(observer, 'reconcile-open-batch', 'engineering_admission_reconcile', dict(admissionId=unresolved[0]['id']))
            check('lost-response admission releases without replay', lambda: require(reconciled.get('released') is True and
                reconciled.get('held') is False and reconciled.get('automaticReplay') is False and read_json(journal_path) == terminal and
                len(admissions(store, 'change_apply')) == len(earlier) + 1, 'Reconciliation did not release without replay'))
            target2 = devices(observer, 'classic')['HMI_RT_1']
            final = call(observer, 'capture-after-reconcile', 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target2))))
            check('same open TIA retains the interrupted batch', lambda: same_documents(expected, documents(final)))
            call(observer, 'reconcile-detach', 'tia_detach')
            receipt = observer.receipt_close()
            report['responseLoss']['reconcilerClose'] = receipt
            observer = None
            require(receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'] and receipt['stderrEofObserved'] and receipt['workerExits'] and
                all(row['processExitVerified'] and row['exitCode'] == 0 and not row['forcedTermination'] for row in receipt['workerExits']),
                'Fresh attached reconciler did not close cleanly')
            owner_status = call(client, 'owner-after-reconcile', 'tia_session_status')
            require(owner_status['processId'] == started['processId'], 'Original owned TIA was replaced')
            persist()
        finally:
            if observer:
                report['observerClose'] = observer.receipt_close()
            if worker_watch:
                worker_watch.close()
        call(client, 'detach', 'tia_detach')
    except Exception as error:
        report['failure'] = repr(error)
    finally:
        if client:
            report['lifetimes'].append(ended(client, watcher))
        persist()

    if 'failure' not in report and project and expected:
        client, watcher = None, None
        try:
            require(report['lifetimes'][0]['normalLifecycle'], 'Initial owned lifecycle was not clean')
            client = connect()
            started = call(client, 'fresh-start', 'tia_start')
            watcher = OwnedTiaProcessWatcher(started['processId'])
            opened = call(client, 'fresh-open', 'project_open', dict(path=project))
            require(opened.get('opened') is True and opened.get('readOnly') is False, 'Fresh project must open read-write')
            target = devices(client, 'classic')['HMI_RT_1']
            final = call(client, 'fresh-capture', 'hmi_classic_screen_capture', dict(request=dict(projectPath=project, target=hmi_target(target))))
            check('fresh independent reopen retains the saved batch', lambda: same_documents(expected, documents(final)))
            call(client, 'fresh-detach', 'tia_detach')
        except Exception as error:
            report['failure'] = repr(error)
        finally:
            if client:
                report['lifetimes'].append(ended(client, watcher))
            persist()
    require(digest(Path(fixture['path']).read_bytes()) == fixture['sha256'], 'Original configured archive changed')
    report['completed'] = 'failure' not in report and len(report['lifetimes']) == 2 and all(r['normalLifecycle'] for r in report['lifetimes'])
    persist()
    print(json.dumps(dict(completed=report['completed'], checks=len(report['checks']), failure=report.get('failure'))), flush=True)
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('host', 'worker', 'fixture', 'base'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--run-native', action='store_true', required=True)
    options = parser.parse_args()
    options.host = options.host.resolve(strict=True)
    options.worker = options.worker.resolve(strict=True)
    options.base = options.base.resolve()
    raise SystemExit(run(options))
