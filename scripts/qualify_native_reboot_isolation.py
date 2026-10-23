"""Read-only qualification of actual local boot/registry/store ownership.

Does not start TIA, complete a ticket, delete a quarantine or grant reusable
admission. The native coordinator must perform all checks again under its locks.
"""
import argparse
from pathlib import Path

import native_dispatch_guard as guard
from native_dispatch_isolation import RebootIsolation, observe_boot
from qualify_translation_category_probe import StoreExclusiveWindow


def run(output, stores):
    output = Path(output).resolve(); output.mkdir(parents=True, exist_ok=False)
    workspace = output / 'never-created-native'
    before = [guard.reference(p) for p in sorted(guard.DEFAULT_ROOT.glob('*/**/*.json'))]
    boot = observe_boot()
    guard.write(output / 'current-boot.json', boot)
    project = output / 'provenance-only.ap21'
    resources = [dict(kind='project', path=str(project), effect='No project access'),
                 dict(kind='workspace', path=str(workspace), effect='Admission check only; never created')]
    options = dict(mode='isolatedQualificationMutation', resources=resources, effects='File-only admission qualification')
    default_refused = False
    try:
        with StoreExclusiveWindow([output, *stores], project, output / 'default', **options):
            raise AssertionError('Default quarantine unexpectedly allowed entry')
    except ValueError as error:
        guard.require('Unresolved native dispatch quarantine' in str(error), 'Unexpected default refusal')
        default_refused = True
    with StoreExclusiveWindow([output, *stores], project, output / 'isolated',
            isolation=RebootIsolation(workspace), **options) as window:
        competing_refused = False
        try:
            with StoreExclusiveWindow([output], project, output / 'competing',
                    isolation=RebootIsolation(workspace), **options):
                raise AssertionError('A second owner entered the held guard')
        except OSError:
            competing_refused = True
        guard.require(competing_refused, 'Competing native registry owner did not refuse')
        observations = window.evidence['nativeDispatchGuard']['observations']
        separated = [row for row in observations if 'terminal' not in row]
        from native_dispatch_isolation import LEGACY_INTENT_SHA256
        from native_startup_isolation import STARTUP_INTENT_SHA256
        guard.require({row['intent']['sha256'] for row in separated} ==
                      {LEGACY_INTENT_SHA256, STARTUP_INTENT_SHA256}
                      and len(separated) == 2
                      and all(row['historicalCleanupVerified'] is False for row in separated),
                      'Expected exactly both unchanged historical unknown dispositions')
    after = [guard.reference(p) for p in sorted(guard.DEFAULT_ROOT.glob('*/**/*.json'))]
    guard.require(before == after and not workspace.exists(), 'Registry or disposable workspace changed')
    receipt = dict(completed=True, nativeDispatched=False, defaultQuarantineRefused=default_refused,
        competingOwnerRefused=competing_refused, registryUnchanged=True, historicalCleanupVerified=False,
        originalAttemptPromoted=False, originalRegistry=before, observations=observations,
        currentBoot=guard.reference(output / 'current-boot.json'), automaticReplay=False)
    guard.write(output / 'result.json', receipt)
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--known-store', type=Path, action='append', default=[])
    args = parser.parse_args()
    import json
    print(json.dumps(run(args.output, args.known_store), indent=2))
