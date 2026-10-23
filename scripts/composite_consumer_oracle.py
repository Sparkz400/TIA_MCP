"""Independent original-input provenance and unbounded small-fixture context closure."""
import hashlib
import json
from collections import Counter, deque


def verify_consumers(root, request, report):
    digest = lambda data: hashlib.sha256(data).hexdigest()
    checked = []
    supported = {'calls', 'dependsOn', 'hmiBinding', 'instanceOf', 'libraryVersion', 'reads', 'usesType', 'writes'}
    for context in report['consumerContexts']:
        phase = context['phase']; review_id = request[phase]['reviewId']
        raw = (root / 'project-reviews' / (review_id + '.json')).read_bytes()
        assert digest(raw) == review_id == context['reviewId']
        model = json.loads(raw)['model']; nodes = model['nodes']
        path_counts = Counter(n.get('objectPath') for n in nodes)
        id_counts = Counter(n.get('nodeId') for n in nodes)
        by_path = {n['objectPath']: n for n in nodes if path_counts[n.get('objectPath')] == id_counts[n.get('nodeId')] == 1 and n.get('origin') != 'virtual'}
        by_id = {n['nodeId']: n for n in by_path.values()}
        records = {}
        for field, folder in [('connections', 'connection-reviews'), ('drives', 'drive-profile-reviews'), ('technology', 'technology-profile-reviews')]:
            for reference in request[phase][field]:
                raw = (root / folder / (reference + '.json')).read_bytes(); assert digest(raw) == reference
                envelope = json.loads(raw); original = (root / envelope['input']['path']).read_bytes()
                assert digest(original) == envelope['input']['sha256'] and envelope['reviewId'] == review_id
                records[reference] = (envelope['report'], json.loads(original))

        def resolution(path, plc=None):
            if path is None: return 'notDeclared'
            rows = [n for n in nodes if n.get('objectPath') == path]
            if not rows: return 'missing'
            if len(rows) != 1 or id_counts[rows[0].get('nodeId')] != 1: return 'ambiguousIdentity'
            if rows[0].get('origin') == 'virtual': return 'virtualObject'
            if plc is not None and rows[0].get('plcPath') != plc: return 'scopeUnknown' if rows[0].get('plcPath') is None else 'outOfScope'
            return 'capturedObject'

        # All roots must trace to exact impacted selections and independently retained inputs.
        selections = {s['key']: s for s in report['selections']}
        expected_roots = []
        for impact in report['impacts']:
            if impact['phase'] != phase: continue
            for key in impact['destinationKeys']:
                selection = selections[key]; selector = selection['selector']; kind = selector['kind']
                if kind == 'model': paths = [('mappedObject', selector['objectPath'])]
                elif kind in ('driveTelegram', 'driveTelegramAddress'):
                    packet = records[selector['recordRef']][1]; paths = [('driveSelectionContext', packet['hardwarePath'])]
                    if kind == 'driveTelegramAddress':
                        telegram = next(t for t in packet[phase]['telegrams'] if t['id'] == selector['id'])
                        address = next(a for a in telegram['addresses'] if a['id'] == selector['addressId'])
                        paths.append(('telegramAddressPlcContext', address['plcPath']))
                else:
                    assert kind == 'technologyEncoder'
                    packet = records[selector['recordRef']][1]
                    encoder = next(e for e in packet['encoders'][phase]['observations'] if e['id'] == selector['id'])
                    assert encoder['route'] == 'startdriveTelegram'
                    drive = records[encoder['target']['recordId']][1]
                    paths = [('encoderOwnerContext', packet['technologyPath']), ('encoderTelegramHardwareContext', drive['hardwarePath'])]
                expected_roots.extend((key, impact['amlImpact']['path'], path, role, impact['destinationDepths'][key]) for role, path in paths)
        assert [(r['selectorKey'], r['amlImpactPath'], r['objectPath'], r['role'], r['declaredDepth']) for r in context['rootBindings']] == expected_roots
        initial = {}
        for r in context['rootBindings']:
            assert r['resolution'] == resolution(r['objectPath'])
            if resolution(r['objectPath']) == 'capturedObject': initial[r['objectPath']] = min(initial.get(r['objectPath'], 129), r['declaredDepth'])
        graph = {}
        for association in context['associations']:
            kind = association['kind']; index = association['sourceIndex']; plc = None
            if kind == 'capturedHardwareChild':
                child = nodes[index]; parent = by_id[child['parentId']]
                assert association['recordRef'] == review_id and child['kind'] == parent['kind'] == 'hardware'
                assert association['evidence'] == {key: child[key] for key in ('nodeId', 'objectPath', 'parentId')}
                source, target, state = parent['objectPath'], child['objectPath'], 'declared'
            else:
                component, original = records[association['recordRef']]
                if kind == 'connectionPeerContext':
                    row = component['observations'][index]; assert row['phase'] == phase
                    supplied = next(c for c in original[phase]['connections'] if c['id'] == row['connection']['id'])
                    assert supplied == row['connection']
                    sides, field = association['role'].split('.'); left, right = sides.split('To')
                    source, target = supplied[left][field], supplied[right][field]
                    state = 'declared' if row['local']['status'] == row['partner']['status'] == 'capturedHardwareAncestry' else 'unresolved'
                elif kind == 'connectionConsumer':
                    row = component['consumers'][index]; assert row['phase'] == phase and row['evidence'] in original[phase]['consumers']
                    side, field = association['role'].split('.')
                    supplied = next((c for c in original[phase]['connections'] if c['id'] == row['evidence']['connectionId']), None)
                    source, target = supplied[side][field] if supplied else None, row['evidence']['objectPath']
                    observed = next((o for o in component['observations'] if o['phase'] == phase and supplied and o['connection']['id'] == supplied['id']), None)
                    state = 'declared' if supplied and resolution(target) == 'capturedObject' and observed[side]['status'] == 'capturedHardwareAncestry' else 'unresolved'
                elif kind == 'technologyRelationship':
                    row = component['relationships'][index]; assert row['phase'] == phase and row['relationship'] in original[phase]['relationships']
                    source, target, state = row['relationship']['targetPath'], row['relationship']['sourcePath'], row['relationship']['state']
                    plc = original[phase]['identity']['plcPath']
                elif kind == 'technologyEncoder':
                    row = component['encoderConnections'][index]; assert row['phase'] == phase
                    encoder = original['encoders'][phase]['observations'][row['sourceIndex']]
                    assert row['encoder'] == encoder and encoder['route'] == 'startdriveTelegram'
                    drive = records[encoder['target']['recordId']][1]
                    assert any(t['id'] == encoder['target']['telegramId'] for t in drive[phase]['telegrams'])
                    source, target, state = drive['hardwarePath'], original['technologyPath'], 'declared'
                    plc = original[phase]['identity']['plcPath']
                    assert encoder['state'] == 'connected' and encoder['sensorIndex'] != 0
                    assert drive[phase]['valueDomain'] == original[phase]['valueDomain']
                else:
                    assert kind == 'technologyConsumer'
                    row = component['consumers'][index]; consumer = row['consumer']
                    assert row['phase'] == phase and consumer in original['consumers'][phase]
                    plc = original[phase]['identity']['plcPath']; target = consumer['objectPath']
                    subject_kind = consumer['subjectKind']
                    collection, key = ('parameters', 'name') if subject_kind == 'parameter' else ('relationships', 'id')
                    subject = next((s for s in original[phase][collection] if s[key] == consumer['subjectKey']), None)
                    source = original['technologyPath'] if subject_kind == 'parameter' else subject['sourcePath'] if subject else None
                    exact = resolution(target, plc) == 'capturedObject' and by_path[target]['nodeId'] == consumer['nodeId']
                    state = 'declared' if subject and consumer['state'] == 'declared' and exact and (subject_kind == 'parameter' or subject['state'] == 'declared') else 'unresolved'
                assert association['evidence'] == row
            assert (association['fromObjectPath'], association['toObjectPath'], association['sourceState']) == (source, target, state)
            assert association['fromResolution'] == resolution(source, plc) and association['toResolution'] == resolution(target, plc)
            eligible = state == 'declared' and resolution(source, plc) == resolution(target, plc) == 'capturedObject'
            assert association['eligible'] == eligible and association['nativeLinkVerified'] is False
            if eligible: graph.setdefault(source, set()).add(target)
        expected_saved = []
        for edge in model['edges']:
            if edge['kind'] not in supported or edge.get('unresolved') is not None or edge['evidence'].get('resolution') != 'resolved' or edge['evidence'].get('origin') not in ('parsedSupported', 'nativeConfirmed'): continue
            if any(e['objectPath'] not in by_path or by_path[e['objectPath']]['nodeId'] != e['nodeId'] or e.get('resolution', '') not in ('', 'resolved') for e in (edge['source'], edge['target'])): continue
            source, target = edge['target']['objectPath'], edge['source']['objectPath']
            graph.setdefault(source, set()).add(target)
            if source != target: expected_saved.append((source, target, edge))
        distance = dict(initial); pending = deque(sorted(initial, key=lambda p: (distance[p], p)))
        while pending:
            source = pending.popleft()
            for target in sorted(graph.get(source, ())):
                if target not in distance or distance[target] > distance[source] + 1: distance[target] = distance[source] + 1; pending.append(target)
        assert context['coverage']['truncated'] is False and context['coverage']['complete'] is False
        assert {c['objectPath']: c['discoveryDepth'] for c in context['candidates']} == distance
        for row in context['candidates']: assert row['nodeId'] == by_path[row['objectPath']]['nodeId'] and row['nativeImpactVerified'] is False
        for row in context['associations']: assert row['traversed'] == (row['eligible'] and row['fromObjectPath'] in distance)
        actual_saved = [(s['fromObjectPath'], s['toObjectPath'], s['edges'][0]) for s in context['savedLinks']]
        assert sorted(actual_saved, key=lambda s: s[2]['edgeId']) == sorted((s for s in expected_saved if s[0] in distance), key=lambda s: s[2]['edgeId'])
        checked.append(dict(phase=phase, candidates=len(distance), suppliedAndCapturedLinks=len(context['associations']), savedLinks=len(actual_saved)))
    return checked
