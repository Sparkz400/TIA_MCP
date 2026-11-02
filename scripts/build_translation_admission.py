"""Build and install the exact translation admission registry from a verified same-instance packet.

The registry is trusted deployment configuration (docs/reference/translation-production-admission.md).
This script only packages reviewed evidence: the key is the Host-issued forward key of the packet,
every native case cites the independent verify_translation_admission.py receipt of that packet, and
every portable case cites the TiaMcp.TranslationImport.Tests receipt built from the same frozen source.
It installs the same registry next to the Host and the worker and never touches any other deployment.

A restricted-contract entry built from that packet alone is provisional: it names the qualification deployment it
is installed into, and every other Host or worker refuses it. `--finalize` adds the default-mode packet that
qualify_translation_native.py ran on exactly that provisional registry (clean lifecycle plus the worker and Host
loss interruptions), drops the deployment binding and installs the final entry into another deployment.
"""
import argparse
import hashlib
import json
from pathlib import Path

from qualify_translation_category_probe import ref, require
from verify_watch_workflow import canonical_matches

CONTRACT = 'translation.whole-project-de-DE-en-GB-main-ob1.exact.v1'
RESTRICTED_CONTRACT = 'translation.plc-block-comments-de-DE-en-GB-main-ob1.exact.v1'
# Same order and names as TranslationProductionAdmission.NativeCases / PortableCases.
NATIVE_CASES = ['complete-inventory', 'native-import-readback', 'preservation', 'compile-save-fresh-reopen',
                'distinct-recovery-fresh-reopen', 'lost-restore-response-reconciliation', 'lost-import-response-reconciliation',
                'lost-compile-response-reconciliation', 'lost-save-response-reconciliation', 'lost-reopen-response-reconciliation',
                'interruption-before-dispatch', 'stale-source-refusal', 'existing-recovery-destination-refusal', 'exact-process-cleanup']
CASES = {CONTRACT: NATIVE_CASES, RESTRICTED_CONTRACT: NATIVE_CASES + ['restricted-category-filter']}
# Same as TranslationProductionAdmission.DefaultModeCases; --interrupt cases of qualify_translation_native.py.
DEFAULT_MODE_CASES = ['default-mode-lifecycle', 'worker-loss-at-checkpoint-intent', 'worker-loss-during-import', 'host-loss-during-import']
INTERRUPTIONS = {'worker:checkpointIntent': 'worker-loss-at-checkpoint-intent', 'worker:dispatchIntent': 'worker-loss-during-import',
                 'host:dispatchIntent': 'host-loss-during-import'}
LIFECYCLE_REFUSALS = {'forgedPlan', 'staleRevision', 'replay', 'existingDestination', 'changedBaseline'}
PORTABLE_CASES = ['exact-key-and-binary-refusal', 'stale-plan-revision-replay-refusal', 'source-clear-empty-richtext-refusal',
                  'key-source-target-culture-peer-drift-refusal', 'artifact-tampering-refusal', 'lost-cleanup-response-reservation']


def load(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def canonical(value): return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def build(packet, verification, portable, manifest, profile_id, deployment=None):
    packet = Path(packet).resolve()
    # The proofs carry the Host's own key hash: RFC 8785 since 1 November 2026, the earlier form for older packets.
    forward = load(packet / 'forward-instance-key.json'); key = forward['key']; key_sha = forward['sha256']
    require(canonical_matches(key_sha, key) and key['contract'] in CASES, 'Forward key digest or contract differs')
    native_cases = CASES[key['contract']]; restricted = key['contract'] == RESTRICTED_CONTRACT
    categories = load(packet / 'category-verification.json') if restricted else None
    require(not restricted or categories['populatedMainControl'] is True and key['scope']['categoryIds'] == ['BlockCommentCategoryData'] and
            [(c['category'], c['keptRows'], c['expectedRows']) for c in categories['restricted']['captures']] ==
            [('BlockCommentCategoryData', 5, 5), ('HwcnCommentTextCategoryData', 1, 1), ('AlarmClassDisplayNameCategory', 0, 0)],
            'A restricted key needs its packet\'s conclusive Main control and exact Host-filtered captures')
    checked, tests = load(verification), load(portable)
    require(checked['completed'] and checked['nativeExecuted'] and checked['sameInstance'] and checked['forwardKeySha256'] == key_sha and
            checked['pendingResources'] == 0, 'Independent same-instance verification incomplete or for another key')
    require([c['case'] for c in checked['checks'][:7]] == ['before-dispatch', 'source-drift', 'import-reply', 'compile-reply', 'save-reply',
                                                            'reopen-reply', 'restore-reply'], 'Verification lacks the ordered native case list')
    require(tests['failed'] == 0 and tests['passed'] > 0, 'Portable suite did not pass')
    source = hashlib.sha256(Path(manifest).read_bytes()).hexdigest()
    fixture = load(packet / 'fixture-identity.json')
    native = canonical({'kind': 'translation-admission-native-evidence', 'contract': key['contract'], 'keySha256': key_sha, 'sourcePackageSha256': source,
                        'packet': str(packet), 'result': ref(packet / 'result.json'), 'admission': ref(packet / 'admission.json'),
                        'exactClose': ref(packet / 'exact-close.json'), 'forwardKey': ref(packet / 'forward-instance-key.json'),
                        'independentVerification': checked, 'cases': native_cases,
                        **({'categoryVerification': ref(packet / 'category-verification.json'), 'categoryControls': categories} if restricted else {})})
    port = canonical({'kind': 'translation-admission-portable-evidence', 'contract': key['contract'], 'keySha256': key_sha, 'sourcePackageSha256': source,
                      'suite': 'tests/TiaMcp.TranslationImport.Tests', 'receipt': tests, 'cases': PORTABLE_CASES})
    files = {hashlib.sha256(data).hexdigest(): data for data in (native, port)}
    native_sha, port_sha = (hashlib.sha256(data).hexdigest() for data in (native, port))
    proofs = [{'case': c, 'keySha256': key_sha, 'sourcePackageSha256': source, 'evidenceSha256': native_sha, 'mode': 'native'} for c in native_cases] + \
             [{'case': c, 'keySha256': key_sha, 'sourcePackageSha256': source, 'evidenceSha256': port_sha, 'mode': 'portable'} for c in PORTABLE_CASES]
    entry = {'id': profile_id, 'projectPath': fixture['projectPath'], 'plcPath': fixture['plcPath'],
             'profile': {'id': profile_id, 'key': key, 'sourcePackageSha256': source, 'proofs': proofs},
             'evidence': [{'path': 'evidence/' + digest + '.json', 'sha256': digest} for digest in sorted(files)]}
    if restricted:
        require(deployment is not None, 'A provisional restricted entry needs its qualification deployment')
        entry['qualification'] = {'hostDirectory': str(Path(deployment[0]).resolve()), 'workerDirectory': str(Path(deployment[1]).resolve())}
    return {'version': 1, 'entries': [entry]}, files


def last_state(packet, operation):
    revisions = sorted((packet / 'engineering-operations' / operation).glob('*.json'))
    require(bool(revisions), 'Default-mode operation journal missing: ' + operation)
    return load(revisions[-1])['state']


def verify_default_mode(packet, registry, entry):
    """File-only check of a qualify_translation_native.py packet against the provisional entry it must have run on."""
    result, admission, close = (load(packet / name) for name in ('result.json', 'admission.json', 'exact-close.json'))
    deployment = entry['qualification']
    require(result['completed'] is True and result['qualificationMode'] is False and result['automaticReplay'] is False and
            admission['kind'] == 'translation-native-default-mode' and admission['qualificationMode'] is False and
            admission['profileId'] == entry['id'] and admission['registry']['sha256'] == ref(registry)['sha256'] and
            admission['contract'] == RESTRICTED_CONTRACT and admission['scope'] == entry['profile']['key']['scope'],
            'Default-mode packet is incomplete or ran on another registry or scope')
    # Both frozen packages must be recorded, every file inside its bound directory; an empty inventory proves nothing.
    require(all(admission['packages'][key] and all(Path(f['path']).resolve().is_relative_to(Path(deployment[part])) for f in admission['packages'][key])
                for part, key in (('hostDirectory', 'host'), ('workerDirectory', 'worker'))), 'Default-mode packet ran outside the bound qualification deployment')
    cases = {i['case']: i for i in result['interruptions']}
    require(len(cases) == len(result['interruptions']) and set(cases) == set(INTERRUPTIONS) and
            all(i['originalDisposition'] == 'unchanged' and i['replayRefusal'] == 'RecoveryRequired' for i in cases.values()),
            'Interruption cases are incomplete, replayed or changed the project')
    operation = result['operation']
    require(set(result['refusals']) == LIFECYCLE_REFUSALS and all(result['refusals'].values()) and operation['finalRevision'] > operation['appliedRevision'] and
            all(last_state(packet, op) == 'reconciled' for op in [operation['id']] + [i['operationId'] for i in cases.values()]),
            'Clean default-mode lifecycle or reconciliation incomplete')
    require(close['hostExitCode'] == 0 and close['closeInterrupted'] is None and close['tree']['completed'], 'Exact Host cleanup incomplete')
    return {'packet': str(packet), 'result': ref(packet / 'result.json'), 'admission': ref(packet / 'admission.json'),
            'exactClose': ref(packet / 'exact-close.json'), 'interruptions': [cases[c] for c in INTERRUPTIONS], 'operation': operation}


def finalize(registry, packet):
    """Add the verified default-mode packet to a provisional restricted entry and drop its deployment binding."""
    registry, packet = Path(registry).resolve(), Path(packet).resolve()
    document = load(registry)
    require(len(document['entries']) == 1, 'Exactly one provisional entry required')
    entry = document['entries'][0]
    require(entry['profile']['key']['contract'] == RESTRICTED_CONTRACT and 'qualification' in entry, 'Only a provisional restricted entry can be finalized')
    files = {}
    for item in entry['evidence']:
        data = (registry.parent / item['path']).read_bytes()
        require(hashlib.sha256(data).hexdigest() == item['sha256'], 'Provisional evidence changed')
        files[item['sha256']] = data
    proof = entry['profile']['proofs'][0]
    data = canonical({'kind': 'translation-admission-default-mode-evidence', 'contract': RESTRICTED_CONTRACT, 'keySha256': proof['keySha256'],
                      'sourcePackageSha256': proof['sourcePackageSha256'], 'provisionalRegistry': ref(registry),
                      **verify_default_mode(packet, registry, entry), 'cases': DEFAULT_MODE_CASES})
    digest = hashlib.sha256(data).hexdigest(); files[digest] = data
    final = {k: v for k, v in entry.items() if k != 'qualification'}
    final['profile'] = dict(entry['profile'], proofs=entry['profile']['proofs'] + [
        {'case': c, 'keySha256': proof['keySha256'], 'sourcePackageSha256': proof['sourcePackageSha256'], 'evidenceSha256': digest, 'mode': 'native'}
        for c in DEFAULT_MODE_CASES])
    final['evidence'] = [{'path': 'evidence/' + d + '.json', 'sha256': d} for d in sorted(files)]
    return {'version': 1, 'entries': [final]}, files


def install(directory, registry, files):
    target = Path(directory).resolve() / 'translation-admission'
    require(not target.exists(), 'An admission is already installed: ' + str(target))
    (target / 'evidence').mkdir(parents=True)
    for digest, data in files.items(): (target / 'evidence' / (digest + '.json')).write_bytes(data)
    (target / 'registry.json').write_bytes(canonical(registry))
    return ref(target / 'registry.json')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('host', 'worker'): parser.add_argument('--' + name, type=Path, required=True)
    for name in ('packet', 'verification', 'portable', 'source_manifest'): parser.add_argument('--' + name.replace('_', '-'), type=Path)
    parser.add_argument('--profile-id')
    parser.add_argument('--finalize', type=Path, help='Provisional restricted registry.json to finalize; install the result into --host/--worker')
    parser.add_argument('--default-packet', type=Path, help='qualify_translation_native.py root that ran on the provisional registry')
    args = parser.parse_args()
    if args.finalize:
        require(args.default_packet is not None, '--finalize needs --default-packet')
        registry, files = finalize(args.finalize, args.default_packet)
    else:
        require(all(getattr(args, n) for n in ('packet', 'verification', 'portable', 'source_manifest', 'profile_id')), 'Packet inputs and --profile-id required')
        registry, files = build(args.packet, args.verification, args.portable, args.source_manifest, args.profile_id,
                                (args.host.resolve().parent, args.worker.resolve().parent))
    installed = [install(path.resolve().parent, registry, files) for path in (args.host, args.worker)]
    require(installed[0]['sha256'] == installed[1]['sha256'], 'Host and worker registries differ')
    print(json.dumps({'installed': installed, 'keySha256': registry['entries'][0]['profile']['proofs'][0]['keySha256'],
                      'evidence': sorted(files)}, indent=2))


if __name__ == '__main__': main()
