"""Build and install the exact translation admission registry from a verified same-instance packet.

The registry is trusted deployment configuration (docs/reference/translation-production-admission.md).
This script only packages reviewed evidence: the key is the Host-issued forward key of the packet,
every native case cites the independent verify_translation_admission.py receipt of that packet, and
every portable case cites the TiaMcp.TranslationImport.Tests receipt built from the same frozen source.
It installs the same registry next to the Host and the worker and never touches any other deployment.
"""
import argparse
import hashlib
import json
from pathlib import Path

from qualify_translation_category_probe import ref, require
from verify_watch_workflow import canonical_sha

CONTRACT = 'translation.whole-project-de-DE-en-GB-main-ob1.exact.v1'
# Same order and names as TranslationProductionAdmission.NativeCases / PortableCases.
NATIVE_CASES = ['complete-inventory', 'native-import-readback', 'preservation', 'compile-save-fresh-reopen',
                'distinct-recovery-fresh-reopen', 'lost-restore-response-reconciliation', 'lost-import-response-reconciliation',
                'lost-compile-response-reconciliation', 'lost-save-response-reconciliation', 'lost-reopen-response-reconciliation',
                'interruption-before-dispatch', 'stale-source-refusal', 'existing-recovery-destination-refusal', 'exact-process-cleanup']
PORTABLE_CASES = ['exact-key-and-binary-refusal', 'stale-plan-revision-replay-refusal', 'source-clear-empty-richtext-refusal',
                  'key-source-target-culture-peer-drift-refusal', 'artifact-tampering-refusal', 'lost-cleanup-response-reservation']


def load(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def canonical(value): return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def build(packet, verification, portable, manifest, profile_id):
    packet = Path(packet).resolve()
    forward = load(packet / 'forward-instance-key.json'); key = forward['key']; key_sha = canonical_sha(key)
    require(forward['sha256'] == key_sha and key['contract'] == CONTRACT, 'Forward key digest or contract differs')
    checked, tests = load(verification), load(portable)
    require(checked['completed'] and checked['nativeExecuted'] and checked['sameInstance'] and checked['forwardKeySha256'] == key_sha and
            checked['pendingResources'] == 0, 'Independent same-instance verification incomplete or for another key')
    require([c['case'] for c in checked['checks'][:7]] == ['before-dispatch', 'source-drift', 'import-reply', 'compile-reply', 'save-reply',
                                                            'reopen-reply', 'restore-reply'], 'Verification lacks the ordered native case list')
    require(tests['failed'] == 0 and tests['passed'] > 0, 'Portable suite did not pass')
    source = hashlib.sha256(Path(manifest).read_bytes()).hexdigest()
    fixture = load(packet / 'fixture-identity.json')
    native = canonical({'kind': 'translation-admission-native-evidence', 'contract': CONTRACT, 'keySha256': key_sha, 'sourcePackageSha256': source,
                        'packet': str(packet), 'result': ref(packet / 'result.json'), 'admission': ref(packet / 'admission.json'),
                        'exactClose': ref(packet / 'exact-close.json'), 'forwardKey': ref(packet / 'forward-instance-key.json'),
                        'independentVerification': checked, 'cases': NATIVE_CASES})
    port = canonical({'kind': 'translation-admission-portable-evidence', 'contract': CONTRACT, 'keySha256': key_sha, 'sourcePackageSha256': source,
                      'suite': 'tests/TiaMcp.TranslationImport.Tests', 'receipt': tests, 'cases': PORTABLE_CASES})
    files = {hashlib.sha256(data).hexdigest(): data for data in (native, port)}
    native_sha, port_sha = (hashlib.sha256(data).hexdigest() for data in (native, port))
    proofs = [{'case': c, 'keySha256': key_sha, 'sourcePackageSha256': source, 'evidenceSha256': native_sha, 'mode': 'native'} for c in NATIVE_CASES] + \
             [{'case': c, 'keySha256': key_sha, 'sourcePackageSha256': source, 'evidenceSha256': port_sha, 'mode': 'portable'} for c in PORTABLE_CASES]
    entry = {'id': profile_id, 'projectPath': fixture['projectPath'], 'plcPath': fixture['plcPath'],
             'profile': {'id': profile_id, 'key': key, 'sourcePackageSha256': source, 'proofs': proofs},
             'evidence': [{'path': 'evidence/' + digest + '.json', 'sha256': digest} for digest in sorted(files)]}
    return {'version': 1, 'entries': [entry]}, files


def install(directory, registry, files):
    target = Path(directory).resolve() / 'translation-admission'
    require(not target.exists(), 'An admission is already installed: ' + str(target))
    (target / 'evidence').mkdir(parents=True)
    for digest, data in files.items(): (target / 'evidence' / (digest + '.json')).write_bytes(data)
    (target / 'registry.json').write_bytes(canonical(registry))
    return ref(target / 'registry.json')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('packet', 'verification', 'portable', 'source_manifest', 'host', 'worker'):
        parser.add_argument('--' + name.replace('_', '-'), type=Path, required=True)
    parser.add_argument('--profile-id', required=True)
    args = parser.parse_args()
    registry, files = build(args.packet, args.verification, args.portable, args.source_manifest, args.profile_id)
    installed = [install(path.resolve().parent, registry, files) for path in (args.host, args.worker)]
    require(installed[0]['sha256'] == installed[1]['sha256'], 'Host and worker registries differ')
    print(json.dumps({'installed': installed, 'keySha256': registry['entries'][0]['profile']['proofs'][0]['keySha256'],
                      'evidence': sorted(files)}, indent=2))


if __name__ == '__main__': main()
