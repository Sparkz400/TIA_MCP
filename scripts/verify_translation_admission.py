"""File-only exact-instance translation oracle; never dispatches or grants admission.

Equality below compares actual retained workbooks and independently normalized XML,
in addition to equality of Host-issued exact keys. Original paths/opaque keys stay
intact. Operator review of this receipt is separate from registry integrity loading.
"""
import hashlib
import json
from pathlib import Path
import sys

from qualify_translation_category_probe import require, scan_store, WindowsFiles
from qualify_translation_import import FAILURE_CASES, STALE_CODES
from verify_translation_import import MAIN_COMMENT_VIEWS, accepted_return, workbook, peer
from verify_watch_workflow import canonical_sha


def load(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def decoded(call):
    value = call['response']
    require(not value.get('isError', False), 'Expected successful retained response')
    return value.get('structuredContent') or json.loads('\n'.join(c['text'] for c in value['content'] if c['type'] == 'text'))


def text_sha(value):
    return canonical_sha({'state': 'empty' if value == '' else 'text', 'text': value, 'richText': False})


def content_sha(rows):
    return canonical_sha({'targetCulture': 'en-GB', 'rows': {key: {
        'worksheet': 'User Texts' if row[0].endswith('sheet1.xml') else 'System Texts',
        'category': row[1], 'viewPath': row[2], 'substitutions': row[4],
        'sourceCulture': 'de-DE', 'sourceSha256': text_sha(row[5]),
        'targetCulture': 'en-GB', 'targetSha256': text_sha(row[6])} for key, row in rows.items()}})


class Oracle:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.imports = [dict(load(p), log=(p.parent / 'complete-log').read_bytes()) for p in self.root.glob('translation-native/import-*/native-return.json')]
        self.restores = [load(p) for p in self.root.glob('translation-native/restore-*/restore-return.json')]
        self.oracle = load(self.root / 'edit-oracle.json')

    def artifact(self, value):
        path = (self.root / value['relativePath']).resolve()
        require(path.is_relative_to(self.root) and not path.is_symlink(), 'Artifact escaped root or is linked')
        data = path.read_bytes()
        require(hashlib.sha256(data).hexdigest() == value['sha256'], 'Retained artifact changed')
        return data

    def parsed(self, value): return json.loads(self.artifact(value))
    def calls(self, name, operation):
        return [c for p in sorted(self.root.glob('*-' + name + '.json'))
                if (c := load(p))['request'].get('operationId') == operation]

    def history(self, operation):
        history = [load(p) for p in sorted((self.root / 'engineering-operations' / operation).glob('*.json'))]
        require(history and all(v['id'] == operation and v['revision'] == i for i, v in enumerate(history)), 'Incomplete operation history')
        return history

    def binding(self, operation): return self.parsed(self.history(operation)[0]['plan']['inputs'][0])

    def exact_key(self, key, operation):
        op = self.history(operation)[0]; binding = self.binding(operation); baseline = binding['baseline']; target = baseline['target']
        before, after = workbook(self.artifact(baseline['workbook'])), workbook(self.artifact(binding['candidate']))
        rows = [{'nativeKey': k, 'category': v[1], 'viewPath': v[2], 'sourceStateSha256': text_sha(v[-2]),
                 'originalTargetStateSha256': text_sha(v[-1]), 'candidateTargetStateSha256': text_sha(after[k][-1])}
                for k, v in sorted(before.items())]
        require(key['rows'] == rows and key['baselineContentSha256'] == content_sha(before) and
                key['candidateContentSha256'] == content_sha(after), 'Key does not bind actual complete row states/layout')
        require(key['projectResourceKey'] == op['plan']['resources'][0]['resourceKey'] and key['scope'] == baseline['scope'] and
                all(key[k] == target[k] for k in ('hardwareIdentity', 'nativeIdentity', 'profileSha256')) and
                key['tiaVersion'] == target['reference']['snapshot']['tiaVersion'] == 21 and
                key['binaries']['workerSha256'] == target['adapterSha256'], 'Key identity/binary differs from actual retained capture')
        require([(p['identity'], p['comparisonProfile']) for p in key['peers']] ==
                sorted((p['identity'], p['comparisonProfile']) for p in baseline['peers']), 'Key peer identity/profile inventory differs')

    def compare_peers(self, baseline, value, old, new):
        peers = {p['identity']: p for p in value['peers']}
        require(len(peers) == len(value['peers']) and peers.keys() == {p['identity'] for p in baseline['peers']}, 'Peer inventory differs')
        for original in baseline['peers']:
            actual = peers[original['identity']]
            require(original['comparisonProfile'] == actual['comparisonProfile'] and
                    peer(self.artifact(original['evidence']), original['comparisonProfile'], old, new) ==
                    peer(self.artifact(actual['evidence']), actual['comparisonProfile'], old, new), 'Actual normalized peer content differs')

    def operation(self, trial, reverse=False):
        history = self.history(trial['id']); final = history[-1]
        require(final['state'] == 'reconciled' and final['revision'] == trial['finalRevision'], 'Terminal reconciliation missing')
        binding = self.parsed(final['plan']['inputs'][0]); baseline = binding['baseline']
        before, candidate = workbook(self.artifact(baseline['workbook'])), workbook(self.artifact(binding['candidate']))
        key = self.oracle['requested']['nativeKey']; changed = [k for k in before if before[k] != candidate.get(k)]
        require(before.keys() == candidate.keys() and changed == [key], 'Exactly the one reviewed native key must change')
        old, new = before[key][-1], candidate[key][-1]
        expected = (self.oracle['requested']['replacement'], self.oracle['original']['target']['text']) if reverse else (
            self.oracle['original']['target']['text'], self.oracle['requested']['replacement'])
        require((old, new) == expected and before[key][:-1] == candidate[key][:-1] and
                before[key][1] == '<BlockCommentCategoryData>' and before[key][2].split('\\')[2:] in
                MAIN_COMMENT_VIEWS, 'Edit exceeds exact reviewed Main target transition')

        def observation(value, expected, restored=False, fresh=True):
            require(workbook(self.artifact(value['workbook'])) == expected and value['scope'] == baseline['scope'], 'Complete native texts/cultures differ')
            self.compare_peers(baseline, value, old, old if restored else new)
            require((not fresh or value['target']['sessionEpoch'] != baseline['target']['sessionEpoch']) and
                    all(value['target'][k] == baseline['target'][k] for k in ('adapterSha256', 'hardwareIdentity')) and value['target']['offline'], 'Fresh pinned offline observation required')

        original = self.parsed(self.parsed(final['reconciliation'][0]['evidence'])['observation'])
        original_texts = workbook(self.artifact(original['workbook']))
        require(original_texts in (before, candidate), 'Unknown/partial original outcome')
        observation(original, original_texts)
        disposition = 'unchanged' if original_texts == before else 'verifiedApplied'
        require(final['reconciliation'][0]['disposition'] == disposition and
                Path(original['target']['projectFilePath']).resolve() == Path(baseline['target']['projectFilePath']).resolve(), 'Original disposition/location differs')
        resolutions = final['recoveryResolutions']
        require(len(resolutions) == 1 and resolutions[0]['disposition'] == 'verifiedRestored', 'Independent restoration missing')
        recovery = self.parsed(resolutions[0]['evidence']); require(set(recovery) == {'restored', 'reopened'}, 'Two recovery observations required')
        for value in recovery.values():
            require(Path(value['target']['projectFilePath']).resolve() == Path(trial['recoveryPath']).resolve() !=
                    Path(baseline['target']['projectFilePath']).resolve(), 'Distinct reserved recovery location required')
            observation(value, before, restored=True)
        require(recovery['restored']['target']['sessionEpoch'] != recovery['reopened']['target']['sessionEpoch'] and
                recovery['restored']['target']['reference']['snapshot']['observationId'] != recovery['reopened']['target']['reference']['snapshot']['observationId'], 'Recovery was not freshly reopened')
        case = trial.get('name'); successful = reverse or case == 'restore-reply'
        if successful:
            applied = next((v for v in history if v['state'] == 'succeeded'), None)
            require(applied is not None and disposition == 'verifiedApplied', 'Successful full lifecycle required')
            evidence = {e['gate']: e['artifact'] for e in applied['evidence']}
            for gate in ('dispatch.readback', 'dispatch.reopen'):
                observation(self.parsed(evidence[gate]), candidate, fresh=gate == 'dispatch.reopen')
            for gate in ('dispatch.compile', 'dispatch.save', 'dispatch.cleanup'):
                require(self.parsed(evidence[gate])['disposition'] == 'passed', 'Incomplete successful lifecycle: ' + gate)
        else:
            require(not any(v['state'] == 'succeeded' for v in history), 'Failure case substituted successful lifecycle')
        if not reverse:
            unknown = history[trial['unknownRevision']]
            require(unknown['state'] == 'outcomeUnknown', 'Retained unknown outcome missing')
            if case != 'restore-reply':
                injection = load(self.root / 'translation-qualification' / (trial['id'] + '-failure-injection.json'))
                require(injection['operationId'] == trial['id'] and injection['planSha256'] == final['planSha256'] and injection['revision'] == 0 and
                        injection['interruptBeforeDispatch'] == (case == 'before-dispatch') and injection.get('driftSourceAfterCheckpoint', False) == (case == 'source-drift') and
                        injection['loseReplyAfterStage'] == (None if case in ('before-dispatch', 'source-drift') else case.removesuffix('-reply')), 'Exact failure injection differs')
            if case in ('before-dispatch', 'source-drift'):
                require(not any(v['state'] == 'dispatchIntent' for v in history), 'Pre-dispatch refusal reached import intent')
            if case == 'source-drift': self.source_drift(trial, final, before, key)
        imports = [i for i in self.imports if i['operationId'] == trial['id']]
        expected_imports = 0 if case in ('before-dispatch', 'source-drift') else 1
        require(len(imports) == expected_imports, 'Import count differs; replay forbidden')
        for imported in imports:
            require(imported['planSha256'] == final['planSha256'] and imported['candidateSha256'] == binding['candidate']['sha256'] and
                    accepted_return(imported['state'], imported['log'], before, candidate) and imported['updateSourceLanguage'] is False, 'Native return differs')
        restores = [r for r in self.restores if r['projectPath'] == trial['recoveryPath']]
        require(len(restores) == 1 and restores[0]['oneRetrieve'], 'Exactly one distinct checkpoint restoration required')
        apply_calls = self.calls('qualification_translation_apply', trial['id'])
        require(len(apply_calls) == (1 if reverse else 2) and
                [c['response'].get('isError', False) for c in apply_calls] == ([False] if reverse else [not successful, True]), 'Attempt/replay-refusal sequence differs')
        require(all(c['request']['planSha256'] == final['planSha256'] for c in apply_calls) and apply_calls[0]['request']['revision'] == 0, 'Apply plan/revision differs')
        recover_calls = self.calls('qualification_translation_recover', trial['id'])
        require(len(recover_calls) == (1 if reverse else 2) and
                [c['response'].get('isError', False) for c in recover_calls] == ([False] if reverse else [True, case == 'restore-reply']), 'Collision/restore-loss sequence differs')
        if not reverse:
            marker = trial['collisionMarker']; require(hashlib.sha256(Path(marker['path']).read_bytes()).hexdigest() == marker['sha256'], 'Existing recovery destination changed')
            require(recover_calls[1]['request'].get('loseRestoreReply', False) == (case == 'restore-reply'), 'Restore response-loss intent differs')
            require(trial['expectedImports'] == expected_imports and trial.get('originalDisposition', disposition) == disposition, 'Trial summary differs')
        return {'case': case or 'reverse-' + trial['case'], 'operationId': trial['id'], 'imports': expected_imports,
                'restorations': 1, 'rows': len(before), 'originalDisposition': disposition}

    def source_drift(self, trial, final, before, key):
        intents = [load(p) for p in self.root.glob('translation-native/sourceDrift-*/source-drift-intent.json') if load(p)['operationId'] == trial['id']]
        require(len(intents) == 1, 'Exactly one source mutation intent required')
        intent = intents[0]
        require(intent['planSha256'] == final['planSha256'] and intent['before'] == before[key][-2] == 'Prueftext Main {0}' and
                intent['after'] == 'Qualification source drift Main {0}' and intent['automaticReplay'] is False and intent['checkpoint'] == final['checkpoints'][0], 'Bounded source mutation differs')
        # The second live export itself must prove the changed source, not merely
        # a callback flag or a claimed StaleWorkbook error.
        expected = dict(before); expected[key] = (*before[key][:-2], intent['after'], before[key][-1])
        changed = []
        for path in self.root.glob('translation-native/observe-*/all.xlsx'):
            if workbook(path.read_bytes()) == expected: changed.append(path)
        require(len(changed) == 1, 'Actual source-only drift export missing or repeated')
        failures = [self.parsed(e['artifact']) for e in final['evidence'] if e['gate'] == 'failure' or e['gate'].startswith('interruption.')]
        require(any(f.get('code') in STALE_CODES for f in failures), 'Source drift did not retain exact stale refusal')

    def cleanup(self):
        close = load(self.root / 'exact-close.json'); tree = close['tree']
        require(close['hostExitCode'] == 0 and close['closeInterrupted'] is None and tree['completed'] and tree['workerStartupCaptured'] and
                tree['nativeStartupInventoryComplete'] and not tree['startupOutcomeUnknown'] and not tree['forcedTermination'], 'Exact normal cleanup required')
        require(sorted(e['role'] for e in tree['exits']) == ['host', 'native', 'worker'] and all(e['proof']['exitCode'] == 0 and
                not e['proof']['deadlineOrCancellationObserved'] and all(e['proof'][k] for k in
                ('completed', 'processExitVerified', 'birthAndImageVerified', 'sameOsHandleRetained', 'handleClosed')) for e in tree['exits']), 'Incomplete exact owned exits')
        locks = WindowsFiles(); handle = locks.lock(self.root / 'engineering-operations' / 'store.lock')
        try: stores = scan_store(self.root, [])
        finally: require(locks.close(handle), 'Verifier lock did not close')
        require(not any(s['pending'] for s in stores), 'Unresolved resource remains')


def verify(root):
    root = Path(root).resolve(); result, admission = load(root / 'result.json'), load(root / 'admission.json')
    same = admission.get('sameInstance', False)
    require(admission['kind'] == ('translation-same-instance-qualification' if same else 'translation-failure-qualification') and
            Path(admission['artifactRoot']).resolve() == root and admission['resumeOperation'] is None, 'Fresh exact packet required')
    require(result['completed'] and result['productionEnabled'] is False and result['automaticReplay'] is False and result['preexistingPreserved'], 'Incomplete native packet')
    cases = list(FAILURE_CASES) if same else [admission.get('failureCase')]
    require(all(c in FAILURE_CASES for c in cases) and [t['name'] for t in result['trials']] == cases, 'Exact bounded ordered case list required')
    oracle = Oracle(root); checks = [oracle.operation(t) for t in result['trials']]
    if same:
        key = load(root / 'forward-instance-key.json'); require(key['sha256'] == canonical_sha(key['key']) == result['forwardKeySha256'], 'Exact key digest differs')
        preparations = [decoded(load(p)) for p in sorted(root.glob('*-qualification_translation_prepare.json'))]
        by_id = {p['operation']['id']: p for p in preparations}; require(len(by_id) == len(preparations), 'Duplicate prepared operation')
        forward_ids = [t['id'] for t in result['trials']] + [result['finalPreparedOperation']]
        initial = oracle.binding(forward_ids[0]); initial_texts = workbook(oracle.artifact(initial['baseline']['workbook']))
        for operation in forward_ids:
            prepared = by_id[operation]
            require(prepared['admissionKey'] == key['key'] and prepared['admissionKeySha256'] == key['sha256'], 'Different exact key cannot share evidence')
            oracle.exact_key(key['key'], operation)
            binding = oracle.binding(operation)
            require(workbook(oracle.artifact(binding['baseline']['workbook'])) == initial_texts and
                    workbook(oracle.artifact(binding['candidate'])) == workbook(oracle.artifact(initial['candidate'])) and
                    binding['baseline']['scope'] == initial['baseline']['scope'] and
                    binding['baseline']['target']['projectFilePath'] == initial['baseline']['target']['projectFilePath'], 'Actual same-fixture baseline/candidate/location differs')
            oracle.compare_peers(initial['baseline'], binding['baseline'], oracle.oracle['original']['target']['text'], oracle.oracle['requested']['replacement'])
        require(len(oracle.history(result['finalPreparedOperation'])) == 1, 'Final retained B-to-F plan was unexpectedly attempted')
        reverses = result['reverseTrials']; require([r['case'] for r in reverses] == [t['name'] for t in result['trials'] if t['originalDisposition'] == 'verifiedApplied'] and len(reverses) <= 5, 'Reverse transitions differ from known completed edits')
        require(set(by_id) == set(forward_ids + [r['id'] for r in reverses]), 'Unexpected prepared operation')
        checks += [oracle.operation(t, reverse=True) for t in reverses]
    require(sum(c['imports'] for c in checks) == len(oracle.imports) <= (10 if same else 1) and
            len(checks) == len(oracle.restores) <= (12 if same else 1), 'Native mutation budget differs')
    oracle.cleanup()
    return {'completed': True, 'nativeExecuted': True, 'productionEnabled': False, 'sameInstance': same,
            'forwardKeySha256': result.get('forwardKeySha256'), 'checks': checks, 'imports': len(oracle.imports),
            'restorations': len(oracle.restores), 'pendingResources': 0, 'exactOwnedExits': 3}


if __name__ == '__main__': print(json.dumps(verify(sys.argv[1]), indent=2))
