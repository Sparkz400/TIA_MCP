"""Independent C01 impact references and raw supplied-file effect oracle."""
import copy
from hmi_dependency_association_checks import verify as verify_dependencies


def effect_status(output, request, originals):
    files = {f['id']: f for f in request['files']}
    side = 'current' if output['action'] == 'retain' else 'proposed'
    expected = [] if output['action'] == 'remove' else output[side]
    complete = output['action'] == 'remove' or (output[side + 'Complete'] and bool(expected))
    observation = next((r for r in request['observations'] if r['outputId'] == output['id']), None)
    observed = observation['files'] if observation else []
    observed_complete = bool(observation and observation['complete'])
    left = {files[i]['fileName']: originals[i] for i in expected}
    right = {files[i]['fileName']: originals[i] for i in observed}
    mismatch = any(left[n] != right[n] for n in left.keys() & right.keys())
    mismatch |= complete and bool(right.keys() - left.keys())
    mismatch |= observed_complete and bool(left.keys() - right.keys())
    return 'mismatch' if mismatch else 'match' if complete and observed_complete else 'incomplete'


def verify(record, request, originals, reviews):
    assert request['schemaVersion'] == 'hmi-component-review-request.v7'
    verify_dependencies(record, request, originals, reviews)
    report = record['report']
    rows = report['dependencyImpacts']
    for index, row in enumerate(rows):
        association = report['dependencyAssociations'][index]
        selection = association['selection']; scope = selection['scope']
        roots = {selection['objectPath'], selection['targetObjectPath']}
        paths = [i for i, p in enumerate(report['consumerPaths']) if p['scope'] == scope
                 and p['reviewId'] == association['reviewId'] and p['rootObjectPath'] in roots]
        model = reviews[association['reviewId']]['model']
        edges = {e['edgeId']: e for e in model['edges']}
        nodes = {n['objectPath']: n['nodeId'] for n in model['nodes'] if 'objectPath' in n}
        for i in paths:
            path = report['consumerPaths'][i]
            assert path['objectPaths'][0] == path['consumerObjectPath']
            assert path['objectPaths'][-1] == path['rootObjectPath']
            assert path['distance'] == len(path['edges']) == len(path['objectPaths']) - 1
            for step, edge in enumerate(path['edges']):
                assert edge == edges[edge['edgeId']]
                assert edge['source']['objectPath'] == path['objectPaths'][step]
                assert edge['target']['objectPath'] == path['objectPaths'][step + 1]
                assert all(endpoint['nodeId'] == nodes[endpoint['objectPath']] for endpoint in (edge['source'], edge['target']))
        affected = roots | {report['consumerPaths'][i]['consumerObjectPath'] for i in paths}
        field = 'beforeMember' if scope == 'beforeComponent' else 'afterMember'
        bindings = [i for i, b in enumerate(request['bindings']) if b[field] and b[field]['objectPath'] in affected]
        mapped = {request['bindings'][i][field]['objectPath'] for i in bindings}
        ids = {request['bindings'][i]['outputId'] for i in bindings}
        outputs = []
        for j, output in enumerate(request['outputs']):
            if output['id'] not in ids:
                continue
            effect = next(i for i, e in enumerate(report['effectChecks']) if e['outputId'] == output['id'])
            coverage = next(i for i, e in enumerate(report['expectationCoverage']) if e['outputId'] == output['id'])
            status = effect_status(output, request, originals)
            assert report['effectChecks'][effect]['status'] == status
            outputs.append(dict(outputId=output['id'], outputIndex=j, action=output['action'], effectCheckIndex=effect,
                effectStatus=status, expectationCoverageIndex=coverage,
                derivedEffectIndices=[i for i, e in enumerate(report['derivedEffects']) if e['expectation']['outputId'] == output['id']]))
        assert row == dict(associationIndex=index, scope=scope, reviewId=association['reviewId'],
            componentObjectPath=selection['objectPath'], dependencyObjectPath=selection['targetObjectPath'],
            associationStatus=association['status'], rootObjectPaths=sorted(roots), consumerPathIndices=paths,
            bindingIndices=bindings, outputs=outputs, unmappedObjectPaths=sorted(affected - mapped),
            nativeImpactVerified=False, consumerCoverageComplete=False, outputMappingComplete=False)
    coverage, = report['dependencyImpactCoverage']
    count = len(request['dependencyAssociations'])
    assert coverage == dict(associationCount=count, includedAssociationCount=len(rows),
        omittedAssociationIndices=list(range(len(rows), count)), returnedBytes=coverage['returnedBytes'],
        maxBytes=1048576, truncated=len(rows) != count, consumerCoverageComplete=False,
        outputMappingComplete=False, nativeImpactVerified=False)
    assert 2 <= coverage['returnedBytes'] <= 1048576
    for key, value in dict(dependencyImpactCount=len(rows), dependencyImpactOutputCount=sum(len(r['outputs']) for r in rows),
                           dependencyImpactOmittedCount=count-len(rows)).items():
        assert report['summary'][key] == value


def negative_controls(record, request, originals, reviews):
    controls = {
        'wrong output effect': lambda r: r['dependencyImpacts'][0]['outputs'][0].update(effectStatus='mismatch'),
        'wrong output index': lambda r: r['dependencyImpacts'][0]['outputs'][0].update(outputIndex=255),
        'cross-scope consumer': lambda r: r['dependencyImpacts'][0].update(scope='beforeComponent'),
        'invented native effect': lambda r: r['dependencyImpacts'][0].update(nativeImpactVerified=True),
        'lost omissions': lambda r: r['dependencyImpactCoverage'][0].update(omittedAssociationIndices=[4095]),
    }
    for label, mutate in controls.items():
        changed = copy.deepcopy(record); mutate(changed['report'])
        try: verify(changed, request, originals, reviews)
        except AssertionError: pass
        else: raise AssertionError('Accepted negative control: ' + label)
    return list(controls)
