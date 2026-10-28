"""Default-mode lifecycle of the six translation_native_* tools on an installed exact admission.

Runs the unchanged frozen binaries without --translation-qualification. The fixed registry next to
the Host and worker admits one project instance and one Main OB1 comment edit. The run refuses an
absent and a malformed profile before native dispatch, captures, reviews, approves and imports that
edit once, refuses a forged plan, a stale revision, a replay and an existing recovery destination,
recovers the checkpoint to a new directory, reconciles both copies read-only, and finally shows that
the changed project no longer matches the admission.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path

from exact_qualification_client import ExactClient
from qualify_exact_worker_startup import emit, package
from qualify_translation_category_probe import ref, require

PRODUCTION = {'translation_native_capture', 'translation_native_prepare', 'translation_native_apply',
              'translation_native_status', 'translation_native_recover', 'translation_native_reconcile'}
SOURCE, ORIGINAL, REPLACEMENT = 'Prueftext Main {0}', 'Verification text Main {0}', 'Verified translation Main {0} — Ω'


class Run:
    def __init__(self, args):
        self.args, self.root = args, args.root.resolve()
        require(not self.root.exists(), 'Fresh run root required')
        self.root.mkdir(parents=True)
        self.client, self.counter = None, 0
        self.packages = {key: package(getattr(args, key)) for key in ('host', 'worker')}
        registry = args.host.resolve().parent / 'translation-admission' / 'registry.json'
        entries = [e for e in json.loads(registry.read_text(encoding='utf-8'))['entries'] if e['id'] == args.profile_id]
        require(len(entries) == 1, 'Exactly one installed entry for the profile required')
        self.entry = entries[0]
        self.save('admission', {'kind': 'translation-native-default-mode', 'profileId': args.profile_id, 'registry': ref(registry),
                                'projectPath': self.entry['projectPath'], 'plcPath': self.entry['plcPath'], 'packages': self.packages,
                                'runner': ref(Path(__file__)), 'qualificationMode': False, 'automaticReplay': False})

    def save(self, name, value):
        emit(self.root / (name + '.json'), value)
        return value

    def call(self, name, args=None, expect=None):
        """expect=None: must succeed; expect='' any refusal; otherwise the exact refusal code."""
        require(all(package(getattr(self.args, key)) == value for key, value in self.packages.items()), 'Frozen package changed')
        self.counter += 1
        response = self.client.request('tools/call', {'name': name, 'arguments': args or {}})
        self.save(f'{self.counter:03}-{name}', {'request': args or {}, 'response': response})
        if expect is not None:
            require(response.get('isError', False), 'Expected refusal did not occur: ' + name)
            code = ((response.get('structuredContent') or {}).get('error') or {}).get('code')
            require(expect == '' or code == expect, f'{name}: expected {expect}, observed {code}')
            return code
        require(not response.get('isError', False), 'Native tool failed: ' + json.dumps(response))
        return response.get('structuredContent') or json.loads('\n'.join(c['text'] for c in response['content'] if c['type'] == 'text'))

    def upload(self, name, data):
        begun = self.call('artifact_upload_begin', {'fileName': name, 'expectedBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        self.call('artifact_upload_chunk', {'uploadId': begun['uploadId'], 'offset': 0, 'data': base64.b64encode(data).decode()})
        return self.call('artifact_upload_commit', {'uploadId': begun['uploadId']})['artifactRef']

    def main_row(self, capture_ref):
        rows, cursor = [], None
        while True:
            page = self.call('translation_inspect', {'captureRef': capture_ref, 'pageSize': 1000, **({'cursor': cursor} if cursor else {})})
            rows += page['rows']; cursor = page['nextCursor']
            if cursor is None: break
        main = [r for r in rows if r['source'].get('text') == SOURCE and r['target'].get('text') == ORIGINAL]
        require(len(main) == 1, 'Exactly one captured Main OB1 comment required')
        return main[0], len(rows)

    def lifecycle(self, report):
        profile = {'profileId': self.args.profile_id}
        self.call('translation_native_capture', {'profileId': 'not-installed-profile'}, 'TranslationImportProfileUnqualified')
        self.call('translation_native_capture', {'profileId': 'Not An Id'}, 'TranslationAdmissionInvalid')
        self.call('translation_native_status', {'operationId': '0' * 32}, 'OperationNotFound')
        self.call('project_open', {'path': self.entry['projectPath']})
        capture = self.call('translation_native_capture', profile)
        require(capture['nativeImportExecuted'] is False, 'Capture must not import')
        capture_ref = capture['capture']['recordRef']
        main, total = self.main_row(capture_ref)
        changed = [r for r in self.entry['profile']['key']['rows'] if r['originalTargetStateSha256'] != r['candidateTargetStateSha256']]
        require([r['nativeKey'] for r in changed] == [main['nativeKey']] and len(self.entry['profile']['key']['rows']) == total,
                'Captured rows differ from the admitted instance')
        proposals = json.dumps({'edits': [{'nativeKey': main['nativeKey'], 'culture': main['culture'], 'kind': 'set', 'value': REPLACEMENT,
                                           'expectedSourceSha256': main['sourceSha256'], 'expectedTargetSha256': main['targetSha256']}]}).encode()
        review = self.call('translation_prepare', {'captureRef': capture_ref, 'proposalsArtifactRef': self.upload('proposals.json', proposals)})
        review_ref, review_sha = review['recordRef'], review['metadata']['reviewSha256']
        approval = self.call('translation_approve', {'reviewRef': review_ref, 'exactReviewSha256': review_sha,
                                                     'reviewer': 'User-authorized disposable-fixture coordinator', 'approveEveryChange': True})
        candidate = self.call('translation_candidate', {'reviewRef': review_ref, 'exactReviewSha256': review_sha, 'approvalSha256': approval['recordSha256']})
        op = self.call('translation_native_prepare', {**profile, 'candidateRef': candidate['recordRef'], 'candidateReceiptSha256': candidate['recordSha256'],
                                                      'idempotencyKey': 'native-' + self.root.name})
        request = {**profile, 'operationId': op['id'], 'planSha256': op['planSha256'], 'revision': op['revision']}
        report['refusals'] = {
            'forgedPlan': self.call('translation_native_apply', {**request, 'planSha256': '0' * 64}, ''),
            'staleRevision': self.call('translation_native_apply', {**request, 'revision': op['revision'] + 1}, '')}
        require(self.call('translation_native_status', {'operationId': op['id']}) == op, 'Refused apply changed the operation')
        applied = self.call('translation_native_apply', request)
        require(applied['state'] == 'succeeded', 'Full native lifecycle failed')
        report['refusals']['replay'] = self.call('translation_native_apply', {**request, 'revision': applied['revision']}, '')
        status = self.call('translation_native_status', {'operationId': op['id']})
        require(status == applied, 'Status differs from the applied operation')
        collision = self.root / 'collision'; collision.mkdir()
        marker = collision / 'untouched.txt'; marker.write_text('Unrelated existing destination; never overwrite', encoding='utf-8')
        before = ref(marker)
        project = Path(self.entry['projectPath'])
        report['refusals']['existingDestination'] = self.call('translation_native_recover', {**request, 'revision': status['revision'],
            'newProjectPath': str(collision / project.name)}, 'TranslationRecoveryDestinationExists')
        require(self.call('translation_native_status', {'operationId': op['id']}) == status and ref(marker) == before,
                'Collision refusal changed the operation or the existing destination')
        destination = self.root / 'recovery' / project.parent.name / project.name
        recovered = self.call('translation_native_recover', {**request, 'revision': status['revision'], 'newProjectPath': str(destination)})
        require(recovered['state'] == 'recoveryIntent', 'A restore return alone must not reconcile')
        done = self.call('translation_native_reconcile', {**request, 'revision': recovered['revision']})
        require(done['state'] == 'reconciled' and done['reconciliation'][0]['disposition'] == 'verifiedApplied' and
                done['recoveryResolutions'][0]['disposition'] == 'verifiedRestored', 'Original and recovered copies not independently verified')
        require(self.call('translation_native_status', {'operationId': op['id']}) == done, 'Terminal status differs')
        # The admission binds the baseline: the changed project can no longer be prepared from the old candidate.
        self.call('project_open', {'path': self.entry['projectPath']})
        report['refusals']['changedBaseline'] = self.call('translation_native_prepare', {**profile, 'candidateRef': candidate['recordRef'],
            'candidateReceiptSha256': candidate['recordSha256'], 'idempotencyKey': 'native-after-' + self.root.name}, '')
        report['operation'] = {'id': op['id'], 'appliedRevision': applied['revision'], 'finalRevision': done['revision'],
                               'recoveryPath': str(destination), 'nativeKey': main['nativeKey'], 'rows': total, 'collisionMarker': before}

    def execute(self):
        report = {'completed': False, 'qualificationMode': False, 'automaticReplay': False}
        try:
            self.client = ExactClient(self.args.host.resolve(), 300, ['--worker', str(self.args.worker.resolve()), '--artifacts', str(self.root)])
            self.client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'translation-native-default', 'version': '1'}})
            self.client.notify('notifications/initialized')
            names = {t['name'] for t in self.client.request('tools/list', {})['tools']}
            require(PRODUCTION <= names and not any(n.startswith('qualification_translation_') for n in names), 'Default mode must expose only the production tools')
            before = self.call('tia_discover')
            started = self.call('tia_start')
            require(started['ownsPortalProcess'] and not started['projects'], 'Fresh empty owned Portal required')
            self.lifecycle(report)
            self.call('tia_detach')
            after = self.call('tia_discover')
            require(sorted(before['processes'], key=lambda x: x['processId']) == sorted(after['processes'], key=lambda x: x['processId']), 'Preexisting Portal set changed')
            report['preexistingPreserved'] = report['completed'] = True
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
    parser.add_argument('--profile-id', required=True)
    parser.add_argument('--execute', action='store_true')
    args = parser.parse_args()
    require(args.execute, 'Explicit coordinator execution required')
    result = Run(args).execute()
    print(json.dumps({'completed': result['completed'], 'root': str(args.root), 'failure': result.get('failure')}), flush=True)
    return 0 if result['completed'] else 1


if __name__ == '__main__': raise SystemExit(main())
