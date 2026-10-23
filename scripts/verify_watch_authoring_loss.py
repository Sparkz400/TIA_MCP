"""Independent absent-to-created lost-Host-reply oracle; no native execution."""
import argparse
from pathlib import Path

import verify_watch_authoring as authoring
import verify_watch_expansion as expansion
import verify_watch_workflow as base
from qualify_exact_worker_startup import emit

POINT = 'host.watch_apply_native'
need, load, artifact = base.need, base.load, base.artifact


def loss_boundary(records, marker, project):
    need(marker['point'] == POINT and marker['automaticReplay'] is False, 'Wrong creation interruption boundary')
    response = marker['response']
    need(response['project']['path'] == project and response['saved'] is True and response['target']['exists'] is True,
         'Lost reply is not a saved creation of the original target')
    dispatches = [r for i, r in enumerate(records) if r['state'] == 'dispatchIntent' and (i == 0 or records[i-1]['state'] != 'dispatchIntent')]
    need(len(dispatches) == 1 and len(dispatches[0]['checkpoints']) == 1, 'Creation must dispatch once after one checkpoint')
    need(not any(e['gate'] == 'dispatch.readback' for r in records for e in r['evidence']), 'Lost Host reply was silently adopted as coordinator readback')
    need(any(r['state'] == 'recoveryIntent' for r in records), 'Unknown created outcome was not explicitly recovered')
    return response


def verify(root):
    root = Path(root).resolve()
    try:
        outer, operation, admission = load(root/'review.json'), load(root/'operation.json'), load(root/'admission.json')
        review, before = outer['review'], outer['review']['before']
        reviewed = authoring.reviewed_candidate(root)
        need(load(root/'independent-review.json') == dict(reviewed, approved=True)
             and operation['digest'] == reviewed['reviewedPlanSha256'] == (root/'approved-digest').read_text().strip(), 'Exact independent approval differs')
        need(review['exactProfileCandidate']['scopeMatched'] is True and admission['mode'] == POINT, 'Wrong creation scope or loss case')
        records = expansion.journal(root, operation['id'])
        need(records[0] == outer['operation'], 'Original prepared journal differs')
        marker = load(root/'interruption-marker.json')
        response = loss_boundary(records, marker, operation['projectPath'])
        candidate = artifact(root, review['input']).read_bytes()
        authoring.snapshot(root, before)
        authoring.snapshot(root, response, before, candidate, operation['projectPath'])
        final = records[-1]
        need(final['state'] == 'reconciled' and len(final['checkpoints']) == len(final['recoveryDestinations'])
             == len(final['recoveryResolutions']) == len(final['reconciliation']) == 1, 'Every creation/recovery resource must reconcile')
        gates = {e['gate']: load(artifact(root,e['artifact'])) for e in final['evidence']}
        retrieve, restored, original = (gates[k] for k in ('recovery.retrieve','recovery.restored','recovery.original'))
        recovered, destination = retrieve['recovered']['project']['path'], final['recoveryDestinations'][0]['recoveryLocation']
        need(retrieve['checkpoint'] == final['checkpoints'][0] and recovered != operation['projectPath']
             and Path(recovered).resolve().is_relative_to(Path(destination).resolve()) and Path(destination).resolve().is_relative_to(root), 'Distinct original checkpoint recovery required')
        for snap in (retrieve['restored'],restored): authoring.snapshot(root,snap,before,project=recovered,fresh=True)
        authoring.snapshot(root,original,before,candidate,operation['projectPath'],True)
        need(original['target']['exists'] is True and final['reconciliation'][0]['disposition'] == 'verifiedApplied'
             and final['recoveryResolutions'][0]['disposition'] == 'verifiedRestored', 'Absent/created outcome classification differs')
        need(load(artifact(root,final['reconciliation'][0]['evidence'])) == original
             and load(artifact(root,final['recoveryResolutions'][0]['evidence'])) == restored, 'Resource evidence differs')
        fresh = [before,retrieve['restored'],restored,original]
        need(len({s['processId'] for s in fresh}) == len(fresh) and len({s['observation']['sessionEpoch'] for s in fresh}) == len(fresh), 'Fresh recovery/original lifetimes required')
        refusal, held = load(root/'replay-refusal-before-original-readback.json'), load(root/'owner-held-before-original-readback.json')
        need(refusal['code'] == 'RecoveryRequired' and held['code'] == 'ResourceBusy'
             and refusal['operationId'] == held['operationId'] == operation['id'] and not refusal['nativeDispatchInvoked'] and not held['nativeDispatchInvoked'], 'Original owner released before final native proof')
        need(all(r['state']=='prepared' for r in expansion.journal(root,held['competitorId'])), 'Competing owner acquired')
        interruption = expansion.interruption_check(root,admission,records,operation)
        for index in (1,2):
            close=load(root/f'host-{index:02}-close.json')['close']
            need(close['tree']['completed'] and not close['tree']['startupOutcomeUnknown'] and not close['closeInterrupted'], 'Exact terminal tree incomplete')
            for row in close['tree']['exits']:
                proof=row['proof']; expected=74 if index==1 and row['role']=='host' else 0
                need(proof['completed'] and proof['sameOsHandleRetained'] and proof['birthAndImageVerified'] and
                     not proof['deadlineOrCancellationObserved'] and not proof['forcedTermination'] and proof['exitCode']==expected, 'Unexpected exact process outcome')
        for row in load(root/'creation-loss-inputs.json')['sources']:
            need(base.sha((root/'controller-source'/Path(row['path']).name).read_bytes())==row['sha256'], 'Loss controller/verifier changed')
        for folder in (root/'engineering-admissions').iterdir():
            if folder.is_dir() and not folder.name.startswith('.'):
                need(load(sorted(folder.glob('*.json'))[-1])['state'] in ('returned','reconciled'), 'Shared mutation remains pending')
        need(not (root/'runner-failure.json').exists() and (root/'expected-response-loss.json').exists(), 'Unexpected controller failure')
        return {'completed':True,'kind':'independent-watch-creation-lost-reply','nativeInvoked':False,'productionActivation':False,
            'absentToCreatedUnknownOutcome':True,'originalDisposition':'verifiedApplied','restoredAbsence':True,'freshRecoveredReopen':True,
            'singleDispatch':True,'readbackNotAdopted':True,'peerExports':len(expansion.peer_bytes(root,before)),'interruption':interruption,'issues':[]}
    except Exception as error:
        return {'completed':False,'kind':'independent-watch-creation-lost-reply','nativeInvoked':False,'productionActivation':False,'issues':[str(error)]}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root',type=Path); parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args(); result=verify(args.root); emit(args.output,result)
    return 0 if result['completed'] else 1


if __name__ == '__main__': raise SystemExit(main())
