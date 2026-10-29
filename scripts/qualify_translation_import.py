"""Sequential, explicit-runtime translation qualification on one newly owned fixture.

Never adopts wt02, changes production gates, retries imports, or uses old admission.
Original process handles and foreign-store locks remain held through terminal proof.
"""
import argparse
from contextlib import ExitStack
import json
import os
from pathlib import Path
import shutil
import sys

from exact_qualification_client import ExactClient
from qualify_exact_worker_startup import emit, package, check_sources
from qualify_translation_category_probe import StoreExclusiveWindow, ref, require
from component_window_lifetime import scan_component_reservations
from verify_translation_populated_category_probe import decode, compare, populated_control
from verify_watch_workflow import canonical_sha

REPO = Path(__file__).resolve().parents[1]
# The export sorts rows by source text, so a changed source can also move its row: the profile check then refuses first.
STALE_CODES = ('StaleWorkbook', 'TranslationScopeMismatch')
FAILURE_CASES = ('before-dispatch', 'source-drift', 'import-reply', 'compile-reply', 'save-reply', 'reopen-reply', 'restore-reply')


def portal_check(before, after, owned):
    """The owned Portal must have exited. Other lanes start and end their own Portals on this machine; these runners
    never terminate a process, so a foreign Portal that ended meanwhile is recorded, not treated as a failure."""
    remaining = {p['processId'] for p in after['processes']}
    require(owned not in remaining, 'The owned Portal remains after detach')
    ended = sorted({p['processId'] for p in before['processes']} - remaining)
    return {'ownedPortalExited': True, 'foreignPortalsEnded': ended, 'preexistingPreserved': not ended}


def category_verdict(result):
    require(result['projectModified'] is False, 'Category probe modified the fixture')
    populated, matrix = result['populated'], result['matrix']
    require(populated['completed'] and matrix['completed'], 'Incomplete category probes')
    decoded = {}
    for group, body in [('main', populated), ('matrix', matrix)]:
        for request in body['exports']:
            require(request['state'] in {'returned', 'failed'}, 'Unresolved native category export')
            require(request['afterModified'] is False, 'Category request changed modified state')
            if request['state'] == 'returned':
                original = request['artifact']; path = Path(original['path'])
                require(ref(path) == original, 'Category export bytes changed')
                decoded[group + ':' + request['mode']] = decode(path)[0]
    baseline = decoded['matrix:plc-all']
    require(bool(baseline), 'Populated PLC category control required')
    controls = []
    for suffix, category in [('comments', 'BlockCommentCategoryData'), ('hardware', 'HwcnCommentTextCategoryData'), ('alarms', 'AlarmClassDisplayNameCategory')]:
        controls.append({'selection': 'plc', 'category': category,
                         **compare(baseline, decoded['matrix:plc-' + suffix], category)})
    main_control = populated_control(decoded['main:plc-all-before'], decoded['main:main-all-before'], populated['projectName'], populated['plcName'])
    require(main_control == populated['populatedMainControlPassed'], 'Independent Main control differs')
    if main_control:
        require(decoded['main:main-all-before'] == decoded['main:main-all-after'] and
                decoded['main:plc-all-before'] == decoded['main:plc-all-after'], 'Category baselines changed')
        for suffix, category in [('comments', 'BlockCommentCategoryData'), ('hardware', 'HwcnCommentTextCategoryData'), ('alarms', 'AlarmClassDisplayNameCategory')]:
            controls.append({'selection': 'Main', 'category': category,
                             **compare(decoded['main:main-all-before'], decoded['main:main-' + suffix], category)})
    # A populated filtering failure is a resolved restriction, never a passing
    # restricted profile. This packet uses the independently captured full-project
    # legacy overload; neither category nor selected-object filtering is relied on.
    return {'completed': True, 'populatedPlcRows': len(baseline), 'populatedMainControl': main_control,
            'controls': controls, 'restrictedProfilesEnabled': False,
            'admittedScope': 'whole-project:legacy-bilingual', 'categoryFilteringReliedOn': False}


class Run:
    def __init__(self, args):
        self.args, self.root = args, args.root.resolve()
        self.failure_case = getattr(args, 'failure_case', None)
        self.same_instance = getattr(args, 'same_instance', False)
        self.before_checkpoint = getattr(args, 'interrupt_before_checkpoint', False)
        self.stop_after_noop = getattr(args, 'stop_after_noop', False)
        self.forward_key = None
        require(not self.before_checkpoint or not (self.same_instance or self.failure_case or args.resume_root or self.stop_after_noop),
                'The pre-checkpoint interruption needs its own fresh fixture; its resume run reconciles it')
        require(not self.same_instance or not self.failure_case and not args.resume_root, 'Same-instance qualification requires its own new fixture and bounded case list')
        require(self.failure_case is None or self.failure_case in FAILURE_CASES, 'Unsupported bounded failure case')
        require(not self.failure_case or not args.resume_root, 'Failure packet requires a fresh isolated fixture; accepted trials are never resumed or replayed')
        self.artifacts = args.resume_root.resolve() if args.resume_root else self.root
        require(not self.root.exists(), 'Fresh short run root required')
        self.root.mkdir(parents=True)
        self.client = None
        self.counter = 0
        self.packages = {key: package(getattr(args, key)) for key in ('host', 'worker')}
        folder = self.root / 'controller-source'; folder.mkdir()
        files = sorted({Path(m.__file__).resolve() for m in sys.modules.values() if getattr(m, '__file__', None)
                        and Path(m.__file__).resolve().parent == Path(__file__).resolve().parent and Path(m.__file__).suffix == '.py'})
        self.sources = [ref(path) for path in files]
        for path in files: shutil.copyfile(path, folder / path.name)
        self.save('admission', {'kind': 'translation-same-instance-qualification' if self.same_instance else 'translation-failure-qualification' if self.failure_case else 'translation-fresh-fixture-qualification', 'packages': self.packages,
            'sources': self.sources, 'root': str(self.root), 'artifactRoot': str(self.artifacts), 'productionEnabled': False, 'automaticReplay': False,
            'resumeOperation': args.resume_operation,
            'failureCase': self.failure_case,
            'sameInstance': self.same_instance,
            'interruptBeforeCheckpoint': self.before_checkpoint,
            'stopAfterNoop': self.stop_after_noop,
            'effects': ('One newly owned offline bilingual scratch; seven exact forward cases on the same full instance key, at most five explicitly reviewed reverse Main target operations, at most ten imports and twelve distinct checkpoint restorations, read-only reconciliation and exact cleanup.') if self.same_instance else
                ('One newly owned offline bilingual scratch; one reviewed Main comment operation interrupted at ' + self.failure_case +
                '; one checkpoint restoration to a distinct absent directory, read-only reconciliation and exact cleanup. Accepted no-op/edit trials are not repeated.')
                if self.failure_case else ('One newly owned offline bilingual scratch; category probes and one no-op operation interrupted after acquisition, '
                'before checkpoint and before any import; a later run reconciles it read-only.') if self.before_checkpoint else
                'One newly owned offline bilingual scratch; category probes, two reviewed imports, separate checkpoint restorations and read-only reconciliation.'})

    def failure_trial(self, selector, edit, project, report):
        """Exactly one unattempted operation; a failed/unknown response never triggers replay."""
        prepared = self.call('qualification_translation_prepare', {**selector, 'idempotencyKey': 'failure-' + self.root.name + '-' + self.failure_case, **edit})
        if getattr(self, 'same_instance', False):
            require(prepared['admissionKeySha256'] == canonical_sha(prepared['admissionKey']), 'Admission key digest differs')
            if self.forward_key is None:
                self.forward_key = prepared['admissionKey']; self.save('forward-instance-key', {'key': self.forward_key, 'sha256': prepared['admissionKeySha256']})
            require(prepared['admissionKey'] == self.forward_key, 'Exact forward project/key/source/target/peer/binary instance drift; no identity normalization permitted')
        op = prepared['operation']
        request = {'operationId': op['id'], 'planSha256': op['planSha256'], 'revision': op['revision']}
        options = {'interruptBeforeDispatch': True} if self.failure_case == 'before-dispatch' else {'driftSourceAfterCheckpoint': True} if self.failure_case == 'source-drift' else {} if self.failure_case == 'restore-reply' else {'loseReplyAfterStage': self.failure_case.removesuffix('-reply')}
        applied = self.call('qualification_translation_apply', {**request, **options}, expect_error=self.failure_case != 'restore-reply')
        if self.failure_case == 'source-drift': require(any(code in json.dumps(applied) for code in STALE_CODES), 'The actual changed source was not refused as stale before dispatch')
        status = self.call('qualification_translation_status', {'operationId': op['id']})
        require(status['state'] == ('succeeded' if self.failure_case == 'restore-reply' else 'outcomeUnknown') and len(status['checkpoints']) == 1, 'Interrupted native operation must retain unknown outcome and original checkpoint')
        self.call('qualification_translation_apply', {**request, 'revision': status['revision']}, expect_error=True)
        # A real existing destination negative must preserve both the revision and
        # the unrelated marker; it runs before the single restoration attempt.
        collision = self.root / ('collision-' + self.failure_case); collision.mkdir()
        marker = collision / 'untouched.txt'; marker.write_text('Unrelated existing destination; never overwrite', encoding='utf-8')
        marker_before = ref(marker)
        self.call('qualification_translation_recover', {**request, 'revision': status['revision'], 'newProjectPath': str(collision / Path(project).name)}, expect_error=True)
        unchanged = self.call('qualification_translation_status', {'operationId': op['id']})
        require(unchanged == status and ref(marker) == marker_before, 'Collision refusal changed operation or existing destination')
        destination = self.artifacts / ('failure-recovery-' + self.root.name + '-' + self.failure_case) / Path(project).parent.name / Path(project).name
        recovered = self.call('qualification_translation_recover', {**request, 'revision': status['revision'], 'newProjectPath': str(destination), 'loseRestoreReply': self.failure_case == 'restore-reply'}, expect_error=self.failure_case == 'restore-reply')
        if self.failure_case == 'restore-reply':
            recovered = self.call('qualification_translation_status', {'operationId': op['id']}); status = recovered
        require(recovered['state'] == ('outcomeUnknown' if self.failure_case == 'restore-reply' else 'recoveryIntent'), 'A restore return alone cannot reconcile ownership')
        done = self.call('qualification_translation_reconcile', {**request, 'revision': recovered['revision']})
        require(done['state'] == 'reconciled' and done['recoveryResolutions'][0]['disposition'] == 'verifiedRestored', 'Complete recovered fresh reopen required')
        report['trials'].append({'name': self.failure_case, 'id': op['id'], 'unknownRevision': status['revision'], 'finalRevision': done['revision'],
            'recoveryPath': str(destination), 'expectedImports': 0 if self.failure_case in ('before-dispatch', 'source-drift') else 1,
            'originalDisposition': done['reconciliation'][0]['disposition'], 'collisionMarker': marker_before})

    def before_checkpoint_trial(self, selector, report):
        """One unattempted no-op interrupted before its checkpoint; only reconcile_before_checkpoint in a later run may close it."""
        prepared = self.call('qualification_translation_prepare', {**selector, 'idempotencyKey': 'before-checkpoint-' + self.root.name})
        op = prepared['operation']
        self.call('qualification_translation_apply', {'operationId': op['id'], 'planSha256': op['planSha256'], 'revision': op['revision'],
                                                      'interruptBeforeCheckpoint': True}, expect_error=True)
        status = self.call('qualification_translation_status', {'operationId': op['id']})
        require(status['state'] == 'outcomeUnknown' and status['revision'] == 2 and not status['checkpoints'] and
                [e['gate'] for e in status['evidence']] == ['interruption.1'], 'Pre-checkpoint interruption must retain only acquired/unknown history')
        report['trials'].append({'name': 'before-checkpoint', 'id': op['id'], 'unknownRevision': status['revision'], 'imports': 0})

    def same_instance_trials(self, selector, edit, project, original_target, report):
        report['reverseTrials'] = []
        for case in FAILURE_CASES:
            self.failure_case = case
            self.failure_trial(selector, edit, project, report)
            self.call('project_open', {'path': project})
            if report['trials'][-1]['originalDisposition'] == 'verifiedApplied':
                # A separately reviewed known reverse transition, never a replay
                # of the original unknown import and never restore-to-original.
                prepared = self.call('qualification_translation_prepare', {**selector, 'idempotencyKey': 'reverse-' + self.root.name + '-' + case,
                    'nativeKey': edit['nativeKey'], 'replacement': original_target})
                op = prepared['operation']; request = {'operationId': op['id'], 'planSha256': op['planSha256'], 'revision': op['revision']}
                applied = self.call('qualification_translation_apply', request)
                require(applied['state'] == 'succeeded', 'Exact reviewed reverse lifecycle failed; stop without retry')
                destination = self.artifacts / ('reverse-recovery-' + case) / Path(project).parent.name / Path(project).name
                recovered = self.call('qualification_translation_recover', {**request, 'revision': applied['revision'], 'newProjectPath': str(destination)})
                done = self.call('qualification_translation_reconcile', {**request, 'revision': recovered['revision']})
                require(done['state'] == 'reconciled' and done['reconciliation'][0]['disposition'] == 'verifiedApplied' and
                    done['recoveryResolutions'][0]['disposition'] == 'verifiedRestored', 'Reverse original/recovery proof incomplete')
                report['reverseTrials'].append({'case': case, 'id': op['id'], 'appliedRevision': applied['revision'], 'finalRevision': done['revision'], 'recoveryPath': str(destination)})
                self.call('project_open', {'path': project})
        final = self.call('qualification_translation_prepare', {**selector, 'idempotencyKey': 'final-forward-key-' + self.root.name, **edit})
        require(final['admissionKey'] == self.forward_key, 'Final forward instance is not exactly restored')
        report['finalPreparedOperation'] = final['operation']['id']; report['forwardKeySha256'] = final['admissionKeySha256']
        require(len(report['reverseTrials']) <= 5, 'Reverse mutation budget exceeded')

    def save(self, name, value):
        emit(self.root / (name + '.json'), value)
        return value

    def call(self, name, args=None, expect_error=False):
        check_sources(self.sources)
        require(all(package(getattr(self.args, key)) == value for key, value in self.packages.items()), 'Frozen package changed')
        self.counter += 1
        response = self.client.request('tools/call', {'name': name, 'arguments': args or {}})
        self.save(f'{self.counter:03}-{name}', {'request': args or {}, 'response': response})
        error = response.get('isError', False)
        if error: self.save(f'{self.counter:03}-host-diagnostics', self.client.errors)
        if expect_error:
            require(error, 'Expected refusal did not occur: ' + name)
            return response
        require(not error, 'Native tool failed: ' + json.dumps(response))
        return response.get('structuredContent') or json.loads('\n'.join(c['text'] for c in response['content'] if c['type'] == 'text'))

    def execute(self):
        report = {'completed': False, 'productionEnabled': False, 'trials': [], 'automaticReplay': False}
        # Reserve the new run workspace against every declared foreign store. Its
        # own Host uses the same shared durable store for fixture creation/imports.
        resources = [{'kind': 'workspace', 'path': str(self.artifacts), 'effect': 'owned isolated fixture and recovery outputs'},
                     {'kind': 'project', 'path': str(self.artifacts / 'uncreated-project-scope'), 'effect': 'bounded project beneath reserved workspace'}]
        roots = [Path(p).resolve() for p in self.args.store_root]
        require(0 < len(roots) <= 128 and len(set(roots)) == len(roots), 'Bounded unique foreign stores required')
        roots.sort(key=lambda p: str(p).upper())
        with ExitStack() as held:
            guard_owner = None
            for offset in range(0, len(roots), 16):
                batch = roots[offset:offset + 16]
                require(self.artifacts not in batch, 'Host artifact store is owned by its durable dispatcher, not a foreign window')
                window = held.enter_context(StoreExclusiveWindow(batch, self.artifacts / 'uncreated-project-scope', self.root / f'foreign-store-window-{offset // 16}',
                    mode='isolatedQualificationMutation', resources=resources, effects='Fresh isolated translation fixture only; wt02 is retained untouched',
                    _guard_owner=guard_owner))
                if guard_owner is None: guard_owner = window
                for root in batch: scan_component_reservations(root, window.keys)
            try:
                self.client = ExactClient(self.args.host.resolve(), 300, ['--worker', str(self.args.worker.resolve()), '--artifacts', str(self.artifacts), '--translation-qualification'])
                self.client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'translation-native-qualification', 'version': '1'}})
                self.client.notify('notifications/initialized')
                before = self.call('tia_discover')
                started = self.call('tia_start')
                require(started['ownsPortalProcess'] and not started['projects'], 'Fresh empty owned Portal required')
                if self.args.resume_root:
                    require(self.args.resume_operation, 'Explicit original operation required for continuation')
                    previous = json.loads((self.artifacts / 'fixture-identity.json').read_text(encoding="utf-8"))
                    selector = {key: previous[key] for key in ('projectPath', 'plcPath')}
                    project, plc = selector['projectPath'], selector['plcPath']
                    old = self.call('qualification_translation_status', {'operationId': self.args.resume_operation})
                    if old['state'] == 'outcomeUnknown':
                        reconciled = self.call('qualification_translation_reconcile_before_checkpoint', {
                            'operationId': old['id'], 'revision': old['revision'], 'originalCloseSha256': ref(self.artifacts / 'exact-close.json')['sha256']})
                        require(reconciled['state'] == 'reconciled', 'Prior pre-checkpoint reservation remains held')
                        report['priorOperationReconciled'] = reconciled['id']
                    else:
                        require(old['state'] == 'reconciled' and self.args.prior_receipts, 'Completed operation needs its exact prior receipts')
                        prior = self.args.prior_receipts.resolve()
                        require(prior.is_relative_to(self.artifacts), 'Prior receipts escaped owned artifact root')
                        previous_result = json.loads((prior / 'result.json').read_text(encoding="utf-8"))
                        require(len(previous_result['trials']) == 1 and previous_result['trials'][0]['name'] == 'noop' and
                                previous_result['trials'][0]['id'] == old['id'], 'Only the completed no-op may be retained for edit continuation')
                        closed = ref(prior / 'exact-close.json')
                        resumed = self.call('qualification_translation_resume_closed_fixture', {
                            'operationId': old['id'], 'revision': old['revision'], 'closeRelativePath': str((prior/'exact-close.json').relative_to(self.artifacts)),
                            'originalCloseSha256': closed['sha256']})
                        report['priorPackets'] = [ref(prior / 'result.json'), closed]
                        report['resumeObservation'] = resumed
                        report['trials'] = previous_result['trials']
                else:
                    scratch = self.call('tia_create_scratch')
                    require(scratch['completed'], 'Incomplete scratch')
                    project = scratch['project']['path']
                    plc = scratch['exports'][0]['block']['plcPath']
                    selector = {'projectPath': project, 'plcPath': plc}
                    self.call('qualification_translation_fixture', selector)
                require(Path(project).is_relative_to(self.artifacts), 'Scratch escaped admitted workspace')
                self.save('fixture-identity', {**selector, 'processId': started['processId']})
                if not self.failure_case and not self.same_instance:
                    categories = self.call('qualification_translation_categories', selector)
                    self.save('category-verification', category_verdict(categories))
                capture = self.call('qualification_translation_capture', selector)
                rows = [r for r in capture['rows'] if r['source']['text'] == 'Prueftext Main {0}' and r['target']['state'] == 'text' and r['target']['text'] == 'Verification text Main {0}']
                require(len(rows) == 1, 'Exactly one actual populated seeded Main comment required')
                edit = {'nativeKey': rows[0]['nativeKey'], 'replacement': 'Verified translation Main {0} — Ω'}
                self.save('edit-oracle', {'original': rows[0], 'requested': edit, 'scope': 'one existing nonempty en-GB target'})
                if self.same_instance: self.same_instance_trials(selector, edit, project, rows[0]['target']['text'], report)
                elif self.failure_case: self.failure_trial(selector, edit, project, report)
                elif self.before_checkpoint: self.before_checkpoint_trial(selector, report)
                for label, change in ([] if self.failure_case or self.same_instance or self.before_checkpoint else [('noop', {}), ('edit', edit)]):
                    if any(t['name'] == label for t in report['trials']): continue
                    if label == 'edit' and self.stop_after_noop: break
                    prepared = self.call('qualification_translation_prepare', {**selector, 'idempotencyKey': 'translation-' + self.root.name + '-' + label, **change})
                    op = prepared['operation']
                    request = {'operationId': op['id'], 'planSha256': op['planSha256'], 'revision': op['revision']}
                    self.call('qualification_translation_apply', {**request, 'planSha256': '0' * 64}, expect_error=True)
                    self.call('qualification_translation_apply', {**request, 'revision': op['revision'] + 1}, expect_error=True)
                    applied = self.call('qualification_translation_apply', request)
                    require(applied['state'] == 'succeeded', 'Full native lifecycle failed')
                    self.call('qualification_translation_apply', {**request, 'revision': applied['revision']}, expect_error=True)
                    destination = self.artifacts / ('recovery-' + self.root.name + '-' + label) / Path(project).parent.name / Path(project).name
                    recovery_request = {**request, 'revision': applied['revision'], 'newProjectPath': str(destination), 'loseRestoreReply': label == 'edit'}
                    recovered = self.call('qualification_translation_recover', recovery_request, expect_error=label == 'edit')
                    status = self.call('qualification_translation_status', {'operationId': op['id']})
                    require(status['state'] == ('outcomeUnknown' if label == 'edit' else 'recoveryIntent'), 'Unexpected restoration disposition')
                    done = self.call('qualification_translation_reconcile', {**request, 'revision': status['revision']})
                    require(done['state'] == 'reconciled' and done['recoveryResolutions'][0]['disposition'] == 'verifiedRestored', 'Recovery not independently verified')
                    report['trials'].append({'name': label, 'id': op['id'], 'appliedRevision': applied['revision'], 'finalRevision': done['revision'],
                        'restorationReplyLost': label == 'edit', 'recoveryPath': str(destination)})
                    # Reconciliation closes both read locations. Reopen the original
                    # through the owned worker; a new capture is required for next trial.
                    if label == 'noop': self.call('project_open', {'path': project})
                self.call('tia_detach')
                after = self.call('tia_discover')
                report.update(portal_check(before, after, started['processId']))
                report['completed'] = True
            except BaseException as error:
                report['failure'] = {'type': type(error).__name__, 'message': str(error)}
            finally:
                if self.client is not None:
                    close = self.client.close()
                    self.save('exact-close', close)
                    self.save('host-diagnostics', self.client.errors)
                    report['cleanup'] = close
                    if close['hostExitCode'] != 0 or close['closeInterrupted'] is not None or not close['tree']['completed']:
                        report['completed'] = False
                self.save('result', report)
        return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('host', 'worker', 'root'): parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--store-root', action='append', required=True)
    parser.add_argument('--resume-root', type=Path)
    parser.add_argument('--resume-operation')
    parser.add_argument('--prior-receipts', type=Path)
    parser.add_argument('--failure-case', choices=FAILURE_CASES,
                        help='Run only one remaining interruption/lost-response case on a fresh fixture; do not repeat accepted trials')
    parser.add_argument('--same-instance', action='store_true', help='One new fixture, seven exact forward cases, bounded reviewed reverse transitions, full unchanged-instance checks')
    parser.add_argument('--interrupt-before-checkpoint', action='store_true', help='Fresh fixture: interrupt one no-op after acquisition, before checkpoint; resume reconciles it')
    parser.add_argument('--stop-after-noop', action='store_true', help='Run only the no-op trial, so a later run can resume the closed fixture for the edit')
    parser.add_argument('--execute', action='store_true')
    args = parser.parse_args()
    require(args.execute, 'Explicit coordinator execution required')
    result = Run(args).execute()
    print(json.dumps({'completed': result['completed'], 'root': str(args.root), 'failure': result.get('failure')}), flush=True)
    return 0 if result['completed'] else 1


if __name__ == '__main__': raise SystemExit(main())
