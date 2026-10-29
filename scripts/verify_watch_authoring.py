"""Independent file-only WT-05 oracle. Does not import production authoring code.

Only create-bool is admitted. Native IDs, all leaf text, unknown payload and
peer bytes remain significant; only export time/container indentation differ.
"""
import argparse
import copy
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET

import verify_watch_workflow as base
import verify_watch_expansion as expansion
from qualify_exact_worker_startup import emit

need, load, artifact = base.need, base.load, base.artifact
TABLE = 'WT05_BoolCreation'
PROFILES = ['watch.add-instance-bool.v1', 'watch.create-instance.v1', 'watch.reference-free-authoring-ids.v1']
TEMPLATE_SHA = 'd710c6b85dd5c94cc27d7eb7edc24f5ac188ec5bdbaccdae5998acf5b697c09d'
REASON = 'Captured Output state of the selected FB instance: Active'


def absence(root, snapshot):
    target = snapshot['target']
    need(target['kind'] == 'watch' and target['exists'] is False and target['group'] == 'root'
         and target['name'] == TABLE and target['rows'] == [] and target['isConsistent'] is True,
         'Exact absent destination required')
    data = artifact(root, target['artifact']).read_bytes()
    need(data == ('watch-table-absent.v1\nroot\n' + TABLE).encode('utf-8')
         and base.sha(data) == target['semanticSha256'], 'Absence receipt is not bound to destination')
    need(not any(t['name'].casefold() == TABLE.casefold() for t in snapshot['tables']),
         'Destination collides with native watch/force inventory')


def snapshot(root, snap, before=None, candidate=None, project=None, fresh=False):
    target, observation = snap['target'], snap['observation']
    if candidate is None:
        absence(root, snap)
    else:
        need(target['exists'] is True and target['name'] == TABLE and target['group'] == 'root',
             'Exact created table identity required')
        data = artifact(root, target['artifact']).read_bytes()
        expansion.typed_rows(snap, expansion.document(data))
        need(expansion.native_key(data) == expansion.native_key(candidate), 'Complete created native XML differs from review')
        need([t for t in snap['tables'] if t['kind'] == 'watch' and t['group'] == 'root' and t['name'] == TABLE] == [target],
             'Created target inventory differs')
    for table in snap['tables']:
        artifact(root, table['artifact'])
    need(load(artifact(root, snap['receipt'])) == {k: v for k, v in snap.items() if k not in ('receipt', 'saved', 'automaticReplay')},
         'Native snapshot receipt differs')
    need(observation['offline'] is True and snap['projectModified'] is False
         and observation['projectFilePath'] == snap['project']['path']
         and observation['reference']['contentSha256'] == target['semanticSha256'], 'Saved offline observation differs')
    if project is not None:
        need(snap['project']['path'] == project, 'Wrong physical project')
    if before is not None:
        for key in ('adapterSha256', 'profileSha256', 'hardwareIdentity', 'nativeIdentity'):
            need(observation[key] == before['observation'][key], 'Bound target profile changed: ' + key)
        need(snap['peers'] == before['peers'] and snap['peerSha256'] == before['peerSha256'], 'Complete peer manifest changed')
        need(expansion.peer_bytes(root, snap) == expansion.peer_bytes(root, before), 'Native peer bytes changed beyond export timestamp')
    if fresh:
        need(before is not None and snap['processId'] != before['processId']
             and observation['sessionEpoch'] != before['observation']['sessionEpoch'], 'Fresh native process/session required')


def exact_creation(template, candidate, active, reason, export):
    need(base.sha(template) == TEMPLATE_SHA, 'Genuine packaged template hash differs')
    expected = copy.deepcopy(expansion.document(template))
    # The template names the installation that exported it; readback names this session's.
    infos, current = expected.findall('DocumentInfo'), base.xml(export).findall('DocumentInfo')
    need(len(infos) == len(current) == 1, 'One DocumentInfo per template and export required')
    index = list(expected).index(infos[0])
    expected[index] = copy.deepcopy(current[0])
    expected[index].tail = infos[0].tail
    watch = base.table(expected)
    watch.find('AttributeList/Name').text = TABLE
    rows = expansion.row_elements(expected)
    need(len(rows) == 1, 'One native template row required')
    row = rows[0]
    row.find('AttributeList/Name').text = base.SYMBOL
    row.find('AttributeList/DisplayFormat').text = 'Bool'
    row.find('AttributeList/ModifyValue').text = ''
    items = row.find("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList")
    primary = 'en-GB' if 'en-GB' in active else active[0]
    need(primary in [i.findtext('AttributeList/Culture') for i in items], 'Unqualified default culture grammar')
    for item in list(items):
        culture = item.findtext('AttributeList/Culture')
        if culture not in active:
            items.remove(item)
        else:
            item.find('AttributeList/Text').text = reason if culture == primary else ''
    # Explicit reviewed structural transformation, never readback normalization.
    mapping = {}
    for index, node in enumerate(n for n in expected.iter() if 'ID' in n.attrib):
        mapping[node.get('ID')] = format(index, 'X')
        node.set('ID', format(index, 'X'))
    actual = expansion.document(candidate)
    need(base.xml_key(actual, False) == base.xml_key(expected, False)
         and expansion.external_xml_nodes(template) == expansion.external_xml_nodes(candidate),
         'Candidate changes payload beyond one exact Bool creation/default reason/explicit IDs')
    return {'rows': 1, 'symbol': base.SYMBOL, 'datatype': 'Bool', 'modifyValue': '',
            'defaultCommentCulture': primary, 'reason': reason, 'requiredProfiles': PROFILES,
            'documentIdMapping': mapping}


def reviewed_candidate(root, check_sources=True):
    root = Path(root).resolve()
    outer = load(root / 'review.json')
    record, review = outer['operation'], outer['review']
    request, generated = load(root / 'authoring-request.json'), load(root / 'generated.json')
    before = review['before']
    need(record['state'] == 'prepared' and record['revision'] == 0 and record['previousSha256'] is None
         and record['plan']['operation'] == 'watchAuthoring', 'Initial authoring operation required')
    need(base.canonical_sha(record['plan']) == record['planSha256'], 'Plan digest mismatch')
    need(record['plan']['target'] == before['observation'], 'Plan target mismatch')
    need(load(artifact(root, outer['reviewArtifact'])) == review, 'Retained review mismatch')
    for entry in record['plan']['inputs']:
        artifact(root, entry)
    need(record['plan']['inputs'][:3] == [outer['reviewArtifact'], review['input'], review['generationArtifact']], 'Plan input aliases differ')
    generation = load(artifact(root, review['generationArtifact']))
    need(generation == review['generation'] and generated['plan'] == generation['plan']
         and generated['generationSha256'] == request['generationSha256'] == review['generationArtifact']['sha256']
         and generated['generationId'] == request['generationId']
         and load(artifact(root, generated['artifact'])) == generation, 'Generated intent identity changed')
    binding, capture = generation['capture']['binding'], generation['capture']
    need(binding['profile'] == 'v21-direct-instance-declarations-1', 'Wrong native source capture profile')
    need(request['mode'] == review['mode'] == 'create' and 'template' not in request
         and request['destination'] == {k: review['selector'][k] for k in ('projectPath', 'plcPath', 'group', 'tableName')}, 'Wrong creation request')
    need(review['selector']['sourceBinding'] == binding and review['selector']['allowAbsent'] is True
         and binding['projectPath'] == before['project']['path'], 'Source/destination binding differs')
    for path_key, hash_key in (('blockPath', 'blockSha256'), ('instanceDbPath', 'instanceSha256')):
        need(before['peers']['block:' + binding[path_key]] == binding[hash_key], 'Source differs from native peer snapshot')
    for entry in capture['artifacts']:
        source_path = artifact(root, entry).resolve()
        need(any(artifact(root, item).resolve() == source_path and item['sha256'] == entry['sha256']
                 for item in record['plan']['inputs']), 'Source capture omitted from immutable inputs')
    captured_blocks = {}
    peers = expansion.peer_bytes(root, before)
    for entry in capture['artifacts']:
        data = artifact(root, entry).read_bytes()
        objects = [n for n in base.xml(data) if isinstance(n.tag, str) and n.tag.startswith('SW.Blocks.')]
        need(len(objects) == 1, 'One exact captured block per artifact required')
        block = objects[0]
        name = block.findtext('AttributeList/Name')
        need(name not in captured_blocks, 'Duplicate source capture')
        captured_blocks[name] = block
        need(peers.get(('block', block.tag, name)) == base.without_created_bytes(data), 'Source native bytes differ from preservation snapshot')
    need(set(captured_blocks) == {'TiaMcp_EnableGate', 'TiaMcp_EnableGate_DB'}, 'Wrong captured source blocks')
    db, fb = (captured_blocks[k] for k in ('TiaMcp_EnableGate_DB', 'TiaMcp_EnableGate'))
    need(db.tag == 'SW.Blocks.InstanceDB' and fb.tag == 'SW.Blocks.FB'
         and db.findtext('AttributeList/InstanceOfName') == 'TiaMcp_EnableGate'
         and db.findtext('AttributeList/InstanceOfType') == 'FB', 'Native DB does not declare the selected FB')
    for block in (fb, db):
        members = block.findall("AttributeList/Interface/{*}Sections/{*}Section[@Name='Output']/{*}Member[@Name='Active']")
        need(len(members) == 1 and members[0].get('Datatype') == 'Bool', 'Native declaration is not the exact Active Bool output')
    rows = [r for r in generated['plan']['rows'] if r['candidate']['memberPath'] == ['Active'] and r['candidate']['datatype'] == 'Bool']
    need(len(rows) == 1 and capture['selectors'][rows[0]['candidateId']] == base.SYMBOL, 'Exact captured Bool candidate required')
    row = rows[0]
    need(row['candidate']['reason'] == REASON, 'Default diagnostic reason differs from native declaration')
    need(request['steps'] == [{'kind': 'add', 'candidateId': row['candidateId'], 'templateOrdinal': 0}], 'Only one selected Bool addition admitted')
    need([s['step'] for s in review['edit']['steps']] == request['steps']
         and review['edit']['requiredProfiles'] == PROFILES, 'Steps or required native gates differ')
    need(review['template']['origin'] == 'packagedGenuineNativeCapture' and review['template']['sourceSha256'] == TEMPLATE_SHA,
         'Packaged template provenance required')
    snapshot(root, before)
    exports = [t['artifact'] for t in before['tables'] if t['artifact'] == review['template'].get('documentInfoSource')]
    need(len(exports) == 1, 'Export metadata must come from one same-session baseline export')
    candidate = artifact(root, review['input'])
    delta = exact_creation(artifact(root, review['template']['artifact']).read_bytes(), candidate.read_bytes(),
                           before['peers']['languages']['active'], row['candidate']['reason'],
                           artifact(root, exports[0]).read_bytes())
    need(review['edit']['documentIdMapping'] == delta['documentIdMapping'], 'Reviewed ID remapping differs from explicit transformation')
    need(review['edit']['before'] == {'name': TABLE, 'rows': []}, 'Creation has fabricated before rows')
    after = review['edit']['after']
    native_row = expansion.row_elements(expansion.document(candidate.read_bytes()))[0]
    comments = {i.findtext('Culture'): i.findtext('Text', '') for i in native_row.findall('.//MultilingualTextItem/AttributeList')}
    need(after == {'name': TABLE, 'rows': [{'ordinal': 0, 'kind': 'PlcWatchTableEntry', 'name': base.SYMBOL,
        'address': native_row.findtext('AttributeList/Address'), 'displayFormat': 'Bool', 'modifyValue': '', 'comments': comments}]}, 'Reviewed row summary differs from XML')
    need(review['edit']['steps'][0]['before'] == review['edit']['before']
         and review['edit']['steps'][0]['after'] == after, 'Step row summaries differ')
    sources = expansion.frozen_sources(root, load(root / 'admission.json'), before) if check_sources else None
    return {'operationId': record['id'], 'reviewedPlanSha256': record['planSha256'],
            'reviewedInputSha256': base.sha(candidate.read_bytes()), 'delta': delta,
            'frozenSources': sources, 'nativeInvoked': False, 'noOnlineAction': True}


def lifecycle(root, admission, snapshots):
    admission = dict(admission, hostProcessId=load(root / 'authoring-host.json')['hostProcessId'])
    result = expansion.actual_host_lifecycle(root, admission, False)
    close = load(root / 'host-01-close.json')['close']
    tree = close['tree']
    need(tree['completed'] and not tree['startupOutcomeUnknown'] and not close['closeInterrupted'], 'Incomplete exact process tree')
    natives = set()
    for row in tree['exits']:
        proof, identity = row['proof'], row['identity']
        need(all(proof.get(k) is True for k in ('completed', 'processExitVerified', 'handleClosed', 'handleCaptured',
                 'sameOsHandleRetained', 'birthAndImageVerified')) and proof['exitCode'] == 0
             and not proof['deadlineOrCancellationObserved'] and not proof['unknownReasons']
             and proof['processId'] == identity['pid']
             and abs((proof['originalFileTimeTicks'] - 116444736000000000) / 10000000 - identity['created']) <= .000002
             and Path(proof['imagePath']).resolve() == Path(identity['exe']).resolve(), 'Exact original handle proof differs')
        if row['role'] == 'native':
            natives.add(identity['pid'])
    need(all(s['processId'] in natives for s in snapshots), 'A native snapshot has no exact terminal proof')
    return result


def recovery_evidence(root):
    """A failed apply can have successful recovery without qualifying creation."""
    root = Path(root).resolve()
    review = load(root/'review.json')['review']
    before, operation = review['before'], load(root/'operation.json')
    records = expansion.journal(root, operation['id'])
    final = records[-1]
    need(final['state'] == 'reconciled' and len(final['checkpoints']) == len(final['recoveryDestinations'])
         == len(final['recoveryResolutions']) == len(final['reconciliation']) == 1, 'Recovery ownership not fully reconciled')
    gates = {e['gate']: load(artifact(root, e['artifact'])) for e in final['evidence']}
    retrieve, restored, original = (gates[k] for k in ('recovery.retrieve', 'recovery.restored', 'recovery.original'))
    recovered = retrieve['recovered']['project']['path']
    destination = final['recoveryDestinations'][0]['recoveryLocation']
    need(retrieve['checkpoint'] == final['checkpoints'][0] and recovered != operation['projectPath']
         and Path(recovered).resolve().is_relative_to(Path(destination).resolve())
         and Path(destination).resolve().is_relative_to(root), 'Checkpoint/destination binding differs')
    for snap in (retrieve['restored'], restored):
        snapshot(root, snap, before, project=recovered, fresh=True)
    need(retrieve['restored']['processId'] == retrieve['recovered']['processId']
         and retrieve['restored']['observation']['sessionEpoch'] == retrieve['recovered']['sessionEpoch'], 'Retrieval process/session binding differs')
    candidate = artifact(root, review['input']).read_bytes() if original['target']['exists'] else None
    snapshot(root, original, before, candidate, operation['projectPath'], True)
    disposition = 'verifiedApplied' if candidate is not None else 'unchanged'
    resolution, recovery_resolution = final['reconciliation'][0], final['recoveryResolutions'][0]
    need(resolution['disposition'] == disposition and load(artifact(root, resolution['evidence'])) == original
         and recovery_resolution['disposition'] == 'verifiedRestored'
         and recovery_resolution['resourceKey'] == resolution['resourceKey']
         and recovery_resolution['recoveryLocation'] == destination
         and load(artifact(root, recovery_resolution['evidence'])) == restored, 'Recovery disposition differs')
    fresh = [before, retrieve['restored'], restored, original]
    if (root/'verified.json').exists(): fresh.append(load(root/'verified.json')['reopened'])
    need(len({s['processId'] for s in fresh}) == len(fresh) and len({s['observation']['sessionEpoch'] for s in fresh}) == len(fresh), 'Recovery/reopen process/session reused')
    refusal = load(root/'replay-refusal-before-original-readback.json')
    held = load(root/'owner-held-before-original-readback.json')
    need(refusal['operationId'] == held['operationId'] == operation['id'] and refusal['code'] == 'RecoveryRequired'
         and refusal['nativeDispatchInvoked'] is False and held['code'] == 'ResourceBusy' and held['nativeDispatchInvoked'] is False,
         'Ownership/replay guard missing before original reconciliation')
    competitor = expansion.journal(root, held['competitorId'])
    need(all(r['state'] == 'prepared' for r in competitor), 'Competing owner acquired reserved resources')
    return {'completed': True, 'originalDisposition': disposition, 'restoredAbsence': True,
            'freshRecoveredReopen': True, 'recoveryPath': recovered, 'finalState': final['state'],
            'peerExportsVerified': len(expansion.peer_bytes(root, before)),
            'exactLifecycle': lifecycle(root, load(root/'admission.json'), fresh)}


def verify(root):
    root = Path(root).resolve()
    stages, issues = [], []
    def check(name, fn):
        try:
            value = fn()
            stages.append({'name': name, 'passed': True, 'detail': value})
            return value
        except (ValueError, OSError, KeyError, IndexError, TypeError, ET.ParseError) as error:
            stages.append({'name': name, 'passed': False})
            issues.append({'stage': name, 'error': str(error)})
    reviewed = check('exactGeneratedCreationReview', lambda: reviewed_candidate(root))
    if reviewed is not None:
        def workflow():
            outer, operation = load(root/'review.json'), load(root/'operation.json')
            before, review = outer['review']['before'], outer['review']
            records = expansion.journal(root, operation['id'])
            need(records[0] == outer['operation'] and operation['id'] == reviewed['operationId'], 'Journal/operation mismatch')
            approved = load(root/'independent-review.json')
            need(approved == dict(reviewed, approved=True) and operation['digest'] == reviewed['reviewedPlanSha256']
                 == (root/'approved-digest').read_text(encoding="utf-8").strip(), 'Independent approval differs')
            need(sum(r['state'] == 'dispatchIntent' and (i == 0 or records[i-1]['state'] != 'dispatchIntent') for i, r in enumerate(records)) == 1, 'Exactly one dispatch required')
            final = records[-1]
            need(final['state'] == 'reconciled' and len(final['checkpoints']) == len(final['recoveryDestinations'])
                 == len(final['recoveryResolutions']) == len(final['reconciliation']) == 1, 'Recovery ownership not fully reconciled')
            gates = {e['gate']: load(artifact(root, e['artifact'])) for e in final['evidence']}
            candidate = artifact(root, review['input']).read_bytes()
            applied, reopened = load(root/'applied.json')['applied'], load(root/'verified.json')['reopened']
            need(applied == gates['dispatch.readback'] and applied['saved'] is True and applied['automaticReplay'] is False, 'Saved readback not retained')
            for gate in ('readback', 'preservation', 'saved', 'freshReopen'):
                need(gates[gate] == reopened, 'Fresh verification gate differs')
            original = operation['projectPath']
            snapshot(root, applied, before, candidate, original)
            snapshot(root, reopened, before, candidate, original, True)
            retrieve = gates['recovery.retrieve']
            need(retrieve['checkpoint'] == final['checkpoints'][0], 'Wrong restored checkpoint')
            recovered = retrieve['recovered']['project']['path']
            destination = final['recoveryDestinations'][0]['recoveryLocation']
            need(recovered != original and Path(recovered).resolve().is_relative_to(Path(destination).resolve())
                 and Path(destination).resolve().is_relative_to(root), 'Recovery escaped its distinct reservation')
            restored, reconciled = gates['recovery.restored'], gates['recovery.original']
            for snap in (retrieve['restored'], restored):
                snapshot(root, snap, before, project=recovered, fresh=True)
            snapshot(root, reconciled, before, candidate, original, True)
            resolution, recovery_resolution = final['reconciliation'][0], final['recoveryResolutions'][0]
            need(resolution['disposition'] == 'verifiedApplied' and load(artifact(root, resolution['evidence'])) == reconciled
                 and recovery_resolution['disposition'] == 'verifiedRestored'
                 and recovery_resolution['resourceKey'] == resolution['resourceKey']
                 and recovery_resolution['recoveryLocation'] == destination
                 and load(artifact(root, recovery_resolution['evidence'])) == restored, 'Recovery disposition differs')
            fresh = [before, reopened, retrieve['restored'], restored, reconciled]
            need(len({s['processId'] for s in fresh}) == len(fresh) and len({s['observation']['sessionEpoch'] for s in fresh}) == len(fresh), 'Native reopen process/session reused')
            ending = load(root/'result.json')
            need(ending == {'completed': True, 'projectPath': original, 'recoveryPath': recovered, 'id': operation['id'], 'nativeOnline': False}
                 and not (root/'failure.json').exists() and not (root/'runner-failure.json').exists(), 'Failed trial cannot qualify')
            return {'journalRevisions': len(records), 'singleDispatch': True, 'restoredAbsence': True,
                    'recoveredReopen': True, 'unchangedPeers': len(expansion.peer_bytes(root, before)),
                    'lifecycle': lifecycle(root, load(root/'admission.json'), fresh + [applied]),
                    'profileBinding': {k: before['observation'][k] for k in ('adapterSha256', 'profileSha256', 'hardwareIdentity', 'nativeIdentity')}}
        check('nativeCreationPersistenceRecoveryAndCleanup', workflow)
        check('checkpointRecoveryAndCleanup', lambda: recovery_evidence(root))
        def frozen():
            admission = load(root/'admission.json')
            inputs = load(root/'frozen-inputs.json')
            for kind, rows in inputs['packages'].items():
                directory = Path(admission[kind]).parent
                need({str(p.resolve()) for p in directory.rglob('*') if p.is_file()} == {r['path'] for r in rows}, 'Frozen package inventory changed')
                for row in rows:
                    artifact(root, row)
            for row in inputs['sources'] + load(root/'authoring-inputs.json')['sources']:
                need(base.sha((root/'controller-source'/Path(row['path']).name).read_bytes()) == row['sha256'], 'Retained controller source changed')
            return {'packages': inputs['packages'], 'sourcesRetained': True}
        check('frozenPackagesAndController', frozen)
    return {'kind': 'independent-watch-authoring-verification', 'completed': not issues,
            'qualificationPassed': not issues, 'nativeInvoked': False, 'root': str(root),
            'verifiedAtUtc': datetime.now(timezone.utc).isoformat(), 'stages': stages, 'issues': issues,
            'requiredProfiles': PROFILES, 'productionActivation': False,
            'limitations': ['Only the recorded one-row create/default-comment/native target profile.',
                            'Maintenance, explicit multilingual comments, other scalar types, collision and stale-source native refusal remain separate qualifications.']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.root)
    emit(args.output, result)
    print('PASS' if result['completed'] else result['issues'])
    return 0 if result['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
