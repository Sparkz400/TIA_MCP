"""Independent WT-06 candidate/negative oracle; never starts native processes."""
import argparse
import copy
from pathlib import Path

import verify_watch_authoring as authoring
import verify_watch_expansion as expansion
import verify_watch_workflow as base
from qualify_exact_worker_startup import emit

PROFILE = 'watch.create-owned-v21-1511-v41-de-active-bool.v1'
need, load, artifact = base.need, base.load, base.artifact


def source_negative(root, result):
    root = Path(root)
    need(load(artifact(root, result['receipt'])) == {k: v for k, v in result.items() if k != 'receipt'}, 'Native negative receipt differs')
    need(result['automaticReplay'] is False and result['nativeWatchImportInvoked'] is False and result['restorationPassed'] is True
         and result['nativeSourceWriteAttempted'] is True
         and 'failure' not in result and 'restorationFailure' not in result, 'Failed negative cannot qualify')
    artifact(root, result['checkpoint'])
    before, changed, restored = (result[k] for k in ('before', 'changed', 'restored'))
    authoring.snapshot(root, before)
    authoring.snapshot(root, changed)
    authoring.snapshot(root, restored, before)
    need(len(expansion.peer_bytes(root, before)) == 6, 'Exact six baseline peer exports required')
    binding = result['sourceBinding']
    key = 'block:' + binding['blockPath']
    peers = copy.deepcopy(before['peers'])
    need(changed['peers'][key] != peers[key], 'Source did not actually change')
    peers[key] = changed['peers'][key]
    need(changed['peers'] == peers, 'Temporary source drift changed an unselected peer')
    original_bytes, changed_bytes = expansion.peer_bytes(root, before), expansion.peer_bytes(root, changed)
    source = ('block', 'SW.Blocks.FB', 'TiaMcp_EnableGate')
    need(set(original_bytes) == set(changed_bytes) and all(original_bytes[k] == changed_bytes[k] for k in original_bytes if k != source),
         'Unselected native peer bytes changed')
    original_xml, changed_xml = base.xml(original_bytes[source]), base.xml(changed_bytes[source])
    author = original_xml.find('SW.Blocks.FB/AttributeList/HeaderAuthor')
    need(author is not None and (author.text or '') == result['originalAuthor'] and result['temporaryAuthor'] == 'WT05_STALE_SOURCE', 'HeaderAuthor baseline differs')
    author.text = result['temporaryAuthor']
    need(base.xml_key(original_xml, False) == base.xml_key(changed_xml, False)
         and base.external_xml_nodes(original_bytes[source]) == base.external_xml_nodes(changed_bytes[source]), 'Source delta exceeds exact HeaderAuthor metadata')
    changed_binding = copy.deepcopy(binding)
    changed_binding['blockSha256'] = changed['peers'][key]
    need(result['changedCapture']['binding'] == changed_binding and result['restoredCapture']['binding'] == binding,
         'Source recapture or restoration differs')
    for name, snapshot in (('changedCapture', changed), ('restoredCapture', restored)):
        captured = {}
        for item in result[name]['artifacts']:
            data = artifact(root, item).read_bytes()
            objects = [e for e in base.xml(data) if isinstance(e.tag, str) and e.tag.startswith('SW.Blocks.')]
            need(len(objects) == 1, 'One captured source block required')
            identity = ('block', objects[0].tag, objects[0].findtext('AttributeList/Name'))
            need(identity not in captured, 'Duplicate captured source block')
            captured[identity] = base.without_created_bytes(data)
        need(set(captured) == {source, ('block', 'SW.Blocks.InstanceDB', 'TiaMcp_EnableGate_DB')}
             and all(expansion.peer_bytes(root, snapshot)[k] == v for k, v in captured.items()), 'Captured source bytes differ from native peers')
    need(result['refusal']['code'] == 'StaleSource' and result['refusal']['guard'] == 'VerifyWatchSource'
         and result['refusal']['checkpointOrImportAfterChange'] is False, 'Native stale guard refusal missing')
    for snapshot in (changed, restored):
        for field in ('adapterSha256', 'profileSha256', 'hardwareIdentity', 'nativeIdentity', 'sessionEpoch'):
            need(snapshot['observation'][field] == before['observation'][field], 'Negative native identity changed')
        need(snapshot['processId'] == before['processId'], 'Negative native process changed')
    return {'realSavedSourceDrift': True, 'refusedByNativePreEffectGuard': True, 'restoredCompletePeers': True,
            'hostPrepareAgainstChangedSource': False, 'watchImportInvoked': False}


def verify(root):
    root = Path(root).resolve()
    prior = authoring.verify(root)
    issues = list(prior['issues'])
    stages = {'creationLifecycle': prior}
    def check(name, action):
        try: stages[name] = {'passed': True, 'evidence': action()}
        except Exception as error:
            issues.append(name + ': ' + str(error))
            stages[name] = {'passed': False, 'error': str(error)}
    check('savedNativeSourceDriftAndRestoration', lambda: source_negative(root, load(root/'source-negative.json')))
    def negatives():
        review = load(root/'review.json')['review']
        status = review['exactProfileCandidate']
        need(status['profile'] == PROFILE and status['scopeMatched'] is True and status['mismatches'] == []
             and status['productionApplyEnabled'] is False and status['nativeVerified'] is False, 'Exact still-disabled profile receipt missing')
        negative = load(root/'source-negative.json')
        authoring.snapshot(root, review['before'], negative['before'], fresh=True)
        fresh = load(root/'source-restored-fresh-generation.json')
        fresh_capture = load(artifact(root, fresh['artifact']))['capture']
        need(fresh_capture['binding'] == negative['sourceBinding'], 'Fresh source restoration differs')
        for label, name in (('exact-name', authoring.TABLE), ('case-folded-name', authoring.TABLE.lower()), ('force-name', 'Force table')):
            refusal = load(root/('collision-' + label + '.json'))
            need(refusal['code'] == 'WatchTableCollision' and refusal['arguments']['request']['destination']['tableName'] == name
                 and refusal['automaticReplay'] is False, 'Collision refusal missing: ' + label)
        candidate = artifact(root, review['input']).read_bytes()
        authoring.snapshot(root, load(root/'after-collision-snapshot.json'), review['before'], candidate)
        late = load(root/'late-collision-review.json')['operation']
        ending = load(root/'late-collision-status.json')
        refusal = load(root/'late-collision-refusal.json')
        records = expansion.journal(root, late['id'])
        need(records[-1] == ending and ending['state'] == 'stopped' and ending['checkpoints'] == []
             and not any(r['state'] in ('checkpointIntent', 'dispatchIntent') for r in records), 'Stale pending operation dispatched')
        need(refusal['code'] == 'StaleNativeTarget' and refusal['arguments']['operationId'] == late['id'], 'Stale epoch refusal missing')
        admissions = []
        for folder in (root/'engineering-admissions').iterdir():
            if folder.is_dir() and not folder.name.startswith('.'):
                records = [load(p) for p in sorted(folder.glob('*.json'))]
                need(records and records[-1]['state'] in ('returned', 'reconciled'), 'Owned shared mutation remains unresolved')
                admissions.append(records[-1])
        source_admission = [r for r in admissions if r['scope']['operation'] == 'watch_authoring_source_negative']
        need(len(source_admission) == 1 and source_admission[0]['state'] == 'returned', 'One terminal source-negative admission required')
        authoring.lifecycle(root, load(root/'admission.json'), [negative['before'], review['before'], load(root/'after-collision-snapshot.json')])
        for row in load(root/'exact-profile-inputs.json')['sources']:
            need(base.sha((root/'controller-source'/Path(row['path']).name).read_bytes()) == row['sha256'], 'Retained exact controller source changed')
        return {'collisionCases': 3, 'lateStaleEpochRefusedBeforeCheckpoint': True, 'sharedAdmissionsTerminal': len(admissions)}
    check('exactCandidateFreshRestorationCollisionsAndOwnership', negatives)
    return {'kind': 'independent-watch-exact-profile-verification', 'completed': not issues, 'qualificationPassed': not issues,
            'profile': PROFILE, 'productionActivation': False, 'nativeInvoked': False, 'issues': issues, 'stages': stages,
            'limitations': ['Actual Host prepare with a concurrently changed FB remains separate from the native pre-effect stale guard trial.',
                            'Authoring-specific process-loss/native failure and broader table/comment/scalar profiles are not admitted by this packet.']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.root)
    emit(args.output, result)
    return 0 if result['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
