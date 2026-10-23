"""Exact contained HMI configured-fixture reopen/retrieval qualification.

Compares captured inventories/scalars and available exports. Missing native
coverage remains explicit; never enables production mutation or runtime.
"""
import argparse
import copy
import json
import math
from pathlib import Path

import native_dispatch_guard as guard
from native_dispatch_isolation import RebootIsolation, canonical, overlaps
from prepare_hmi_fixture_ui import execute_helper
from qualify_hmi import native_compile_gate, native_payload
from qualify_translation_category_probe import StoreExclusiveWindow


def strict_equal(left, right):
    # Python container equality aliases True/1 and False/0; native captured JSON
    # types are evidence and must remain distinct, including inside unknown data.
    return json.dumps(left, sort_keys=True, separators=(',', ':'), allow_nan=False) == json.dumps(right, sort_keys=True, separators=(',', ':'), allow_nan=False)


def retained(ref):
    guard.require(isinstance(ref, dict) and ref == guard.reference(ref.get('path', '')), 'Retained configured evidence changed')
    canonical(ref['path'])
    return Path(ref['path'])


def clean_compile(owners, rows):
    result = native_compile_gate(dict(nativeBefore=owners, observed=rows), 'observed')
    guard.require(result['status'] == 'passed' and all(type(r.get('warningCount')) is int and r['warningCount'] == 0
                  and r.get('messagesComplete') is True and r.get('state') == 'Success' for r in rows),
                  'Every exact owner requires complete zero-error/zero-warning native compilation')
    return result


def completed_tree(wrapper, result_path):
    guard.require(wrapper.get('completed') is True and type(wrapper.get('helperExitCode')) is int and wrapper['helperExitCode'] == 0
                  and wrapper.get('processProof', {}).get('completed') is True
                  and wrapper['processProof'].get('explicitNativeProcessDisposeInvoked') is True
                  and wrapper['processProof'].get('coordinatorForcedTermination') is False,
                  'Original configured helper and complete retained process cleanup are required')
    proof_ref = wrapper['processProof']['jobProof']
    job = guard.read(retained(proof_ref))
    guard.require(job.get('schemaVersion') == 'owned-job-terminal.v1' and job.get('createdAtomicallyInJob') is True
                  and job.get('helperHandleSignaled') is True and type(job.get('activeProcesses')) is int and job['activeProcesses'] == 0
                  and type(job.get('totalProcesses')) is int and job['totalProcesses'] >= 1
                  and job.get('limitTerminatedProcesses') == 0 and job.get('limitFlags') == 0 and job.get('automaticTermination') is False,
                  'Exact original atomic job must be terminal')
    matches = []
    for path in guard.DEFAULT_ROOT.glob('*/terminal.json'):
        terminal = guard.read(path)
        if terminal.get('jobEvidence') != proof_ref: continue
        intent = guard.read(retained(terminal['intent']))
        guard.require(terminal.get('schemaVersion') == 'native-dispatch-terminal.v1'
                      and terminal.get('originalAttemptPromoted') is False
                      and intent.get('schemaVersion') == 'native-dispatch-intent.v1'
                      and intent.get('containmentRequiredFromCreation') is True
                      and intent.get('jobName') == job.get('jobName')
                      and intent.get('source') == guard.reference(Path(result_path).parent / 'admission.json'),
                      'Terminal job does not bind the exact original dispatch intent')
        matches.append(guard.reference(path))
    guard.require(len(matches) == 1, 'Exactly one original registry terminal must bind this configured job proof')
    return matches[0]


def configured_source(path):
    path = canonical(path)
    wrapper = guard.read(path); completed_tree(wrapper, path)
    native = wrapper['nativeResult']
    guard.require(strict_equal(native, guard.read(path.parent / 'native/result.json')) and native.get('completed') is True
                  and native.get('savedConfiguredArchiveCaptured') is True and native.get('explicitOwnedSdkDispose') is True
                  and native.get('retainedHandleExitVerified') is True and native.get('sourceArchiveUnchanged') is True,
                  'Exact completed raw configured native result required')
    retained(native['archive']); retained(native['sourceReport'])
    project = canonical(native['projectPath'])
    guard.require(project.is_file() and project.is_relative_to(path.parent / 'native')
                  and project.name.startswith('TiaMcpHmi_') and project.suffix == '.ap21', 'Exact owned configured project required')
    clean_compile(native['after'], native['compileAfter'])
    captured_state(native['after'])
    return wrapper


def recovered_source(path, configured_path, native_source):
    path = canonical(path)
    prior = guard.read(path); completed_tree(prior, path)
    previous = prior['nativeResult']
    guard.require(previous.get('phase') == 'reopen-retrieve' and previous.get('completed') is True
                  and previous.get('configuredResult') == guard.reference(configured_path)
                  and strict_equal(previous, guard.read(path.parent / 'native/result.json')), 'Exact completed recovery source required')
    compare_capture(native_source['after'], previous['recovered'])
    clean_compile(native_source['after'], previous['recoveredCompile'])
    project = canonical(previous['recoveredProjectPath'])
    guard.require(project.is_relative_to(path.parent / 'native/recovered') and project.is_file(), 'Exact owned recovered project required')
    return prior, project


def captured_state(owners):
    """Independent expectation: preserve every captured value and available file.

    Top-level owners use exact unique names. Every nested array/order and unknown
    property remains significant. Only export location and known XML wall-clock
    metadata differ; opaque bytes, comments, processing instructions and IDs do not.
    """
    guard.require(isinstance(owners, list) and owners and len(owners) <= 16, 'Bounded nonempty captured HMI owners required')
    names = [row.get('name') for row in owners]
    guard.require(all(isinstance(name, str) and name for name in names) and len(set(names)) == len(names),
                  'Distinct captured HMI owner identities required')
    files = []
    def visit(value, location):
        if isinstance(value, list): return [visit(item, location + '/' + str(i)) for i, item in enumerate(value)]
        if not isinstance(value, dict):
            guard.require(not isinstance(value, float) or math.isfinite(value), 'Nonfinite captured values refuse')
            return value
        guard.require(value.get('truncated') is not True, 'Truncated captured state cannot prove preservation')
        output = {}
        for key, item in value.items():
            if key == 'export':
                path = retained(item)
                guard.require(path.stat().st_size <= 32 * 1024 * 1024, 'Captured export exceeds comparison bound')
                data = path.read_bytes()
                content = native_payload(data) if path.suffix.lower() == '.xml' else data.hex()
                output[key] = dict(content=content)
                files.append(dict(location=location, original=item, bytes=len(data)))
            else:
                output[key] = visit(item, location + '/' + key)
        return output
    values = []
    for owner in sorted(owners, key=lambda row: row['name']):
        guard.require(type(owner.get('remainingBudget')) is int and owner['remainingBudget'] > 0,
                      'Captured inventory budget must remain positive')
        values.append(visit(owner, owner['name']))
    return values, files


def compare_capture(expected, actual):
    before, original_files = captured_state(expected)
    after, observed_files = captured_state(actual)
    guard.require(strict_equal(before, after), 'Captured HMI state or exported content differs from the independent configured baseline')
    return dict(equal=True, ownerCount=len(before), originalFiles=original_files, observedFiles=observed_files,
                wholeHmiPreservationQualified=False, scope='Every captured field, array and available export; unavailable native content remains unqualified')


def run(args):
    worker = getattr(args, 'bridge_worker', None)
    bridge_files = []
    if worker is not None:
        worker = canonical(worker)
        guard.require(args.phase == 'reopen-retrieve', 'Bridge inspection requires a fresh archive copy')
        bridge_files = [guard.reference(p) for p in sorted(worker.parent.iterdir()) if p.is_file()]
        guard.require(guard.reference(worker) in bridge_files, 'Worker missing from exact bridge bundle')
    configured_path = canonical(args.configured_result)
    configured = configured_source(configured_path)
    native_source = configured['nativeResult']
    project = canonical(native_source['projectPath'])
    prior = None
    if args.phase == 'recovered-reopen':
        guard.require(args.prior_result is not None, 'Recovered reopen requires its completed first lifetime')
        prior_path = canonical(args.prior_result)
        prior, project = recovered_source(prior_path, configured_path, native_source)
    else:
        guard.require(args.prior_result is None, 'Unexpected prior result for first lifetime')
    root = canonical(args.output)
    guard.require(not overlaps(root, project.parent) and not root.exists(), 'New disjoint verification root required')
    root.mkdir(parents=True)
    output = root / 'native'
    result = dict(completed=False, automaticReplay=False, nativeQualified=False, runtimeVerified=False)
    resources = [dict(kind='project', path=str(project), effect='Open exact saved owned fixture and compile; no save to this original project'),
                 dict(kind='workspace', path=str(output), effect='Capture state and independently retrieve pinned archive into fresh owned destination')]
    isolation = RebootIsolation(output)
    try:
        with StoreExclusiveWindow([root, *args.known_store], project, root / 'window', isolation=isolation,
                mode='isolatedQualificationMutation', resources=resources,
                effects='Fresh owned HMI reopen/retrieval and compile; no online/runtime operation'):
            # Repeat all immutable source checks under current registry/store locks.
            guard.require(strict_equal(configured_source(configured_path), configured), 'Configured source changed before dispatch')
            if prior is not None:
                checked_prior, checked_project = recovered_source(prior_path, configured_path, native_source)
                guard.require(strict_equal(checked_prior, prior) and checked_project == project,
                              'Recovery source changed before dispatch')
            admission = dict(ownedFixture=True, sequentialNativeAdmission=True, automaticReplay=False, runtimeAllowed=False,
                helperSha256=guard.reference(args.helper)['sha256'], configuredResult=guard.reference(configured_path),
                archive=native_source['archive'], projectPath=str(project), phase=args.phase,
                outputDirectory=str(output), artifactRoot=str(root), storeWindowIntent=guard.reference(root / 'window-intent.json'))
            admission['configuredTerminal'] = completed_tree(configured, configured_path)
            if worker is not None:
                guard.require(all(guard.reference(ref['path']) == ref for ref in bridge_files), 'Bridge bundle changed before dispatch')
                admission.update(bridgeWorker=guard.reference(worker), bridgeFiles=bridge_files)
            if prior is not None:
                admission['priorResult'] = guard.reference(prior_path)
                admission['priorTerminal'] = completed_tree(prior, prior_path)
            request = guard.write(root / 'admission.json', admission)
            execute_helper(args.helper, request, root, output, result, isolation=isolation, command='verify-configured')
            native = result['nativeResult'] = guard.read(output / 'result.json')
            guard.require(result['helperExitCode'] == 0 and result['processProof']['completed']
                          and native.get('completed') is True and native.get('projectPath') == str(project),
                          'Configured native verification or original contained lifetime failed')
            result['preservation'] = {'reopened': compare_capture(native_source['after'], native['reopened'])}
            clean_compile(native_source['after'], native['reopenedCompile'])
            if args.phase == 'reopen-retrieve':
                result['preservation']['recovered'] = compare_capture(native_source['after'], native['recovered'])
                clean_compile(native_source['after'], native['recoveredCompile'])
            if worker is not None:
                bridge = native['bridge']
                guard.require(bridge.get('completed') is True and bridge['worker'] == admission['bridgeWorker'], 'Bridge observation incomplete')
                result['preservation']['bridgeBefore'] = compare_capture(native_source['after'], bridge['before'])
                result['preservation']['bridgeAfter'] = compare_capture(bridge['before'], bridge['after'])
                clean_compile(bridge['after'], bridge['nativeCompile'])
                guard.require(all(guard.reference(ref['path']) == ref for ref in bridge_files), 'Bridge bundle changed during dispatch')
            retained(native_source['archive'])
            guard.require(guard.reference(configured_path) == admission['configuredResult'], 'Configured receipt changed during verification')
            guard.require(strict_equal(configured_source(configured_path), configured),
                          'Configured source changed during verification')
            if prior is not None:
                checked_prior, checked_project = recovered_source(prior_path, configured_path, native_source)
                guard.require(guard.reference(prior_path) == admission['priorResult'] and
                              strict_equal(checked_prior, prior) and checked_project == project,
                              'Recovery source changed during verification')
            result['completed'] = True
    except BaseException as error:
        result['failure'] = dict(type=type(error).__name__, message=str(error))
    finally:
        guard.write(root / 'result.json', result)
    print(json.dumps(dict(completed=result['completed'], report=str(root / 'result.json'))))
    return 0 if result['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('helper', 'configured-result', 'output'): parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--phase', choices=['reopen-retrieve', 'recovered-reopen'], required=True)
    parser.add_argument('--prior-result', type=Path)
    parser.add_argument('--bridge-worker', type=Path, help='Exact current adapter assembly to observe the fresh recovered copy; no production writes')
    parser.add_argument('--known-store', type=Path, action='append', default=[])
    raise SystemExit(run(parser.parse_args()))
