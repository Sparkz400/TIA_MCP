"""Independent file-only acceptance of one frozen production Host qualification.

No native calls, production editor, process control or mutable journal APIs.
"""
import argparse
from datetime import datetime, timezone
from pathlib import Path

import verify_watch_expansion as expansion
import verify_watch_workflow as base
from qualify_exact_worker_startup import emit


def verify_run(root, mode):
    root = Path(root).resolve()
    result = expansion.verify(root)
    base.need(result['completed'], str(result['issues']))
    admission = base.load(root / 'admission.json')
    base.need(admission['mode'] == mode, 'Wrong admitted case')
    inputs = base.load(root / 'frozen-inputs.json')
    for kind, rows in inputs['packages'].items():
        directory = Path(admission[kind]).parent
        base.need({str(p.resolve()) for p in directory.rglob('*') if p.is_file()} ==
                  {r['path'] for r in rows}, 'Frozen package inventory changed')
        for row in rows:
            base.artifact(root, row)
    for row in inputs['sources']:
        copied = root / 'controller-source' / Path(row['path']).name
        base.need(base.sha(copied.read_bytes()) == row['sha256'], 'Controller source copy changed')
    operation = base.load(root / 'operation.json')
    records = base.journal_check(root, operation['id'])
    baseline = base.load(root / 'review.json')['review']['before']
    final = records[-1]
    base.need(final['state'] == 'reconciled', 'Ownership has not been reconciled')
    gates = {r['gate']: base.load(base.artifact(root, r['artifact'])) for r in final['evidence']}
    snapshots = [('restored', gates['recovery.restored'], 'FALSE'),
                 ('original', gates['recovery.original'], 'TRUE')]
    if mode == 'bool':
        snapshots += [('applied', base.load(root/'applied.json')['applied'], 'TRUE'),
                      ('reopened', base.load(root/'verified.json')['reopened'], 'TRUE')]
        base.need('dispatch.readback' in gates, 'Returned native result not retained in the journal')
    else:
        refusal = base.load(root/'replay-refusal-before-original-readback.json')
        held = base.load(root/'owner-held-before-original-readback.json')
        base.need(refusal['operationId'] == held['operationId'] == operation['id'] and
                  refusal['code'] == 'RecoveryRequired' and refusal['nativeDispatchInvoked'] is False and
                  held['code'] == 'ResourceBusy' and held['nativeDispatchInvoked'] is False,
                  'Mutation was not refused while original readback was still outstanding')
        competitors = expansion.journal(root, held['competitorId'])
        base.need(all(r['state'] == 'prepared' for r in competitors), 'Competitor acquired ownership')
        base.need(any(r['state'] == 'recoveryIntent' and any(e['gate'] == 'recovery.restored' for e in r['evidence'])
                      and not r['reconciliation'] for r in records), 'Restored-only evidence prematurely released ownership')
        if mode == 'save.after':
            base.need(any(r['state'] == 'outcomeUnknown' for r in records), 'Worker loss did not persist unknown outcome')
        base.need(result['hostRestartVerified'], 'Fresh Host restart was not proved')
    strict = {name: base.strict_native_exports(root, baseline, snapshot, literal)
              for name, snapshot, literal in snapshots}
    # Validate every original process object, including the Host itself.
    terminal = []
    for close_file in sorted(root.glob('host-*-close.json')):
        packet = base.load(close_file)
        for row in packet['close']['tree']['exits']:
            proof, identity = row['proof'], row['identity']
            base.need(all(proof.get(k) is True for k in ('completed', 'processExitVerified',
                      'handleClosed', 'handleCaptured', 'sameOsHandleRetained', 'birthAndImageVerified'))
                      and not proof['deadlineOrCancellationObserved'] and not proof['unknownReasons']
                      and proof['processId'] == identity['pid']
                      and abs((proof['originalFileTimeTicks'] - 116444736000000000)/10000000 - identity['created']) <= .000002
                      and Path(proof['imagePath']).resolve() == Path(identity['exe']).resolve(),
                      'Original process handle proof is incomplete or mismatched')
            terminal.append({'role': row['role'], 'pid': identity['pid'], 'exitCode': proof['exitCode']})
    return {'completed': True, 'mode': mode, 'root': str(root), 'operationId': operation['id'],
            'expansionOracle': result, 'strictNativeBytes': strict, 'journalRevisions': len(records),
            'singleDispatch': True, 'finalState': final['state'], 'exactProcessExits': terminal,
            'hostPackage': inputs['packages']['host'], 'workerPackage': inputs['packages']['worker']}


def verify(normal, worker_loss, host_loss):
    cases = [verify_run(normal, 'bool'), verify_run(worker_loss, 'save.after'),
             verify_run(host_loss, 'host.watch_apply_native')]
    for case in cases[1:]:
        for key in ('hostPackage', 'workerPackage'):
            base.need(case[key] == cases[0][key], 'Different binary package across qualification cases')
    return {'completed': True, 'nativeInvokedByVerifier': False, 'sameFrozenBuild': True,
            'verifiedAtUtc': datetime.now(timezone.utc).isoformat(), 'cases': cases,
            'limitations': ['One offline Bool ModifyValue profile on the recorded owned V21 scratch.',
                            'Worker loss and Host loss are injected after save, before response publication.',
                            'No physical PLC, online watch/force, runtime or broader mutation qualification.']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('normal', 'worker-loss', 'host-loss', 'output'):
        parser.add_argument('--' + key, type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.normal, args.worker_loss, args.host_loss)
    emit(args.output, result)
    print('PASS one frozen build: normal Bool workflow, actual worker loss and actual Host restart')


if __name__ == '__main__':
    main()
