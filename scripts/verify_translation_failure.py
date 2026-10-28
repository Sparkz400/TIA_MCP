"""Independent file-only verification of ONE remaining native translation failure case.

Never launches TIA, retries an import, or grants production admission.
"""
import hashlib
import json
from pathlib import Path
import sys

from qualify_translation_category_probe import require, scan_store, WindowsFiles
from qualify_translation_import import FAILURE_CASES
from verify_translation_import import MAIN_COMMENT_VIEWS, workbook, peer


def verify(root):
    root = Path(root).resolve()
    def load(path): return json.loads(path.read_text(encoding='utf-8-sig'))
    result, admission = load(root / 'result.json'), load(root / 'admission.json')
    case = admission.get('failureCase')
    require(case in FAILURE_CASES and admission['kind'] == 'translation-failure-qualification', 'Exact failure packet required')
    require(Path(admission['artifactRoot']).resolve() == root and admission['resumeOperation'] is None, 'Fresh fixture required; historical packet cannot be replayed')
    require(result['completed'] and result['productionEnabled'] is False and result['automaticReplay'] is False and result['preexistingPreserved'], 'Incomplete native failure packet')
    require(len(result['trials']) == 1 and result['trials'][0]['name'] == case, 'Exactly one failure trial required')
    trial = result['trials'][0]
    def artifact(value):
        path = (root / value['relativePath']).resolve()
        require(path.is_relative_to(root), 'Artifact escaped run root')
        data = path.read_bytes()
        require(hashlib.sha256(data).hexdigest() == value['sha256'], 'Retained artifact changed')
        return data
    def parsed(value): return json.loads(artifact(value))
    revisions = [load(path) for path in sorted((root / 'engineering-operations' / trial['id']).glob('*.json'))]
    require(revisions and all(value['id'] == trial['id'] and value['revision'] == i for i, value in enumerate(revisions)), 'Incomplete operation history')
    final, unknown = revisions[-1], revisions[trial['unknownRevision']]
    require(final['state'] == 'reconciled' and unknown['state'] == 'outcomeUnknown' and final['revision'] == trial['finalRevision'], 'Unknown outcome and terminal reconciliation required')
    require(not any(value['state'] == 'succeeded' for value in revisions), 'Failure packet cannot substitute a successful lifecycle')
    binding = parsed(final['plan']['inputs'][0]); baseline = binding['baseline']
    before, candidate = workbook(artifact(baseline['workbook'])), workbook(artifact(binding['candidate']))
    require(before.keys() == candidate.keys(), 'Complete native key set changed')
    changed = [key for key in before if before[key] != candidate[key]]
    oracle = load(root / 'edit-oracle.json')['requested']
    require(changed == [oracle['nativeKey']], 'Exactly one reviewed Main edit required')
    key = changed[0]; old, new = before[key][-1], candidate[key][-1]
    require(before[key][:-1] == candidate[key][:-1] and old and new == oracle['replacement'] and
            before[key][1] == '<BlockCommentCategoryData>' and before[key][2].split('\\')[2:] in MAIN_COMMENT_VIEWS, 'Candidate exceeds exact Main target scope')

    def observation(value, expected, restored=False):
        require(workbook(artifact(value['workbook'])) == expected and value['scope'] == baseline['scope'], 'Complete native texts/cultures differ')
        actual = {p['identity']: p for p in value['peers']}
        require(len(actual) == len(value['peers']) and actual.keys() == {p['identity'] for p in baseline['peers']}, 'Complete peer set differs')
        for expected_peer in baseline['peers']:
            found = actual[expected_peer['identity']]
            require(expected_peer['comparisonProfile'] == found['comparisonProfile'] and
                    peer(artifact(expected_peer['evidence']), expected_peer['comparisonProfile'], old, old if restored else new) ==
                    peer(artifact(found['evidence']), found['comparisonProfile'], old, old if restored else new), 'Native peer preservation failed')
        require(value['target']['sessionEpoch'] != baseline['target']['sessionEpoch'] and value['target']['adapterSha256'] == baseline['target']['adapterSha256'] and
                value['target']['hardwareIdentity'] == baseline['target']['hardwareIdentity'] and value['target']['offline'], 'Fresh pinned native observation required')

    original = parsed(parsed(final['reconciliation'][0]['evidence'])['observation'])
    original_texts = workbook(artifact(original['workbook']))
    require(original_texts in (before, candidate), 'Unknown or partial original outcome')
    observation(original, original_texts)
    require(final['reconciliation'][0]['disposition'] == ('unchanged' if original_texts == before else 'verifiedApplied'), 'Original disposition differs')
    resolution = final['recoveryResolutions']; require(len(resolution) == 1 and resolution[0]['disposition'] == 'verifiedRestored', 'Independent recovery required')
    recovery = parsed(resolution[0]['evidence'])
    require(set(recovery) == {'restored', 'reopened'}, 'Two recovery observations required')
    for value in recovery.values():
        require(Path(value['target']['projectFilePath']).resolve() == Path(trial['recoveryPath']).resolve() != Path(baseline['target']['projectFilePath']).resolve(), 'Distinct reserved recovery destination required')
        observation(value, before, restored=True)
    require(recovery['restored']['target']['sessionEpoch'] != recovery['reopened']['target']['sessionEpoch'] and
            recovery['restored']['target']['reference']['snapshot']['observationId'] != recovery['reopened']['target']['reference']['snapshot']['observationId'], 'Recovered copy lacks fresh reopen')
    injection = load(root / 'translation-qualification' / (trial['id'] + '-failure-injection.json'))
    require(injection['operationId'] == trial['id'] and injection['planSha256'] == final['planSha256'] and injection['revision'] == 0 and
            injection['interruptBeforeDispatch'] == (case == 'before-dispatch') and
            injection['loseReplyAfterStage'] == (None if case == 'before-dispatch' else case.removesuffix('-reply')), 'Exact retained failure intent differs')
    imports = [load(path) for path in root.glob('translation-native/import-*/native-return.json')]
    expected_imports = 0 if case == 'before-dispatch' else 1
    require(len(imports) == trial['expectedImports'] == expected_imports, 'Native import count differs; replay forbidden')
    if imports:
        require(imports[0]['operationId'] == trial['id'] and imports[0]['planSha256'] == final['planSha256'] and imports[0]['candidateSha256'] == binding['candidate']['sha256'] and
                imports[0]['state'] == 'Info' and imports[0]['updateSourceLanguage'] is False, 'Native original return is incomplete or foreign')
    if case == 'before-dispatch': require(not any(value['state'] == 'dispatchIntent' for value in revisions), 'Pre-dispatch interruption imported')
    restores = [load(path) for path in root.glob('translation-native/restore-*/restore-return.json')]
    require(len(restores) == 1 and restores[0]['oneRetrieve'] and restores[0]['projectPath'] == trial['recoveryPath'], 'Exactly one checkpoint restoration required')
    calls = [load(path) for path in sorted(root.glob('*-qualification_translation_apply.json'))]
    require(len(calls) == 2 and all(value['response'].get('isError', False) for value in calls), 'Lost response and replay refusal required')
    require(all(value['request']['operationId'] == trial['id'] and value['request']['planSha256'] == final['planSha256'] for value in calls) and
            calls[0]['request']['revision'] == 0 and calls[1]['request']['revision'] == unknown['revision'], 'Refusal did not bind the attempted operation')
    marker = trial['collisionMarker']; data = Path(marker['path']).read_bytes()
    require(hashlib.sha256(data).hexdigest() == marker['sha256'], 'Unrelated existing recovery destination changed')
    collisions = [load(path) for path in sorted(root.glob('*-qualification_translation_recover.json'))]
    require(len(collisions) == 2 and collisions[0]['response'].get('isError', False) and not collisions[1]['response'].get('isError', False), 'Existing destination refusal and one recovery required')
    close = load(root / 'exact-close.json'); tree = close['tree']
    require(close['hostExitCode'] == 0 and close['closeInterrupted'] is None and tree['completed'] and tree['workerStartupCaptured'] and
            tree['nativeStartupInventoryComplete'] and not tree['startupOutcomeUnknown'] and not tree['forcedTermination'], 'Exact normal cleanup required')
    require(sorted(e['role'] for e in tree['exits']) == ['host', 'native', 'worker'] and all(e['proof']['exitCode'] == 0 and
            not e['proof']['deadlineOrCancellationObserved'] and all(e['proof'][name] for name in ['completed', 'processExitVerified', 'birthAndImageVerified', 'sameOsHandleRetained', 'handleClosed']) for e in tree['exits']), 'Incomplete exact owned exits')
    locks = WindowsFiles(); handle = locks.lock(root / 'engineering-operations' / 'store.lock')
    try: stores = scan_store(root, [])
    finally: require(locks.close(handle), 'Verifier lock did not close')
    require(not any(store['pending'] for store in stores), 'Unresolved native resources remain')
    return {'completed': True, 'nativeExecuted': True, 'productionEnabled': False, 'case': case, 'imports': expected_imports,
            'restorations': 1, 'pendingResources': 0, 'exactOwnedExits': 3, 'rows': len(before), 'changedTargets': 1}


if __name__ == '__main__': print(json.dumps(verify(sys.argv[1]), indent=2))
