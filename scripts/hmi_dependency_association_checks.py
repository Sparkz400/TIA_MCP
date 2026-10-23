"""Check C01 v6 against original saved objects and independently checked exports."""
import copy
import re
import uuid
from collections import defaultdict
from hmi_library_association_checks import verify as verify_libraries


def guid(value):
    if not isinstance(value, str) or not re.fullmatch(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', value):
        return None
    parsed = uuid.UUID(value)
    return str(parsed) if parsed.int else None


def verify(record, request, originals, reviews):
    assert request['schemaVersion'] in ('hmi-component-review-request.v6','hmi-component-review-request.v7')
    report = record['report']
    verify_libraries(record, request, originals, reviews)
    objects = {}
    identities = defaultdict(list)
    for scope in ['beforeComponent', 'afterComponent']:
        component = request[scope]
        if component is None:
            continue
        nodes = {n['objectPath']: n for n in reviews[component['reviewId']]['model']['nodes'] if 'objectPath' in n}
        captured = {n['capturedObject']['objectPath']: n['capturedObject'] for n in report[scope]['objects']}
        assert set(captured) == set(component['objectPaths'])
        for path in component['objectPaths']:
            node = nodes[path]
            assert captured[path] == node
            metadata = node.get('metadata')
            observed = metadata.get('libraryInstance') if isinstance(metadata, dict) else None
            type_id = version = None
            if isinstance(observed, dict) and observed.get('schemaVersion') == 'plc-library-instance-observation.v1' and observed.get('source') == 'LibraryTypeInstanceInfo.LibraryTypeVersion' and observed.get('status') == 'linked':
                type_id, version = guid(observed.get('typeGuid')), guid(observed.get('versionGuid'))
                if type_id is None or version is None:
                    type_id = version = None
            dependencies, reason = None, 'ownerIdentityUnavailable'
            if version is not None:
                identities[scope, version].append(dict(objectPath=path, source='savedObject', typeGuid=type_id))
                reason = 'dependencyObservationUnavailable'
                value = observed.get('dependencies')
                if isinstance(value, dict) and value.get('schemaVersion') == 'plc-library-dependencies.v1' and value.get('source') == 'LibraryTypeVersion.Dependencies' and value.get('status') == 'observed':
                    reason = 'invalidDependencyObservation'
                    supplied = value.get('requiredVersionGuids')
                    if isinstance(supplied, list) and len(supplied) <= 1000:
                        parsed = [guid(v) for v in supplied]
                        if all(v is not None for v in parsed):
                            dependencies, reason = parsed, None
            objects[scope, path] = dict(review=component['reviewId'], type=type_id, version=version, dependencies=dependencies, reason=reason)
    assert sum(len(o['dependencies'] or []) for o in objects.values()) <= 20000
    exports = {}
    for row in report['libraryAssociations']:
        selection, identity = row['selection'], row['exportIdentity']
        key = selection['scope'], selection['objectPath']
        exports[key] = row
        identities[key[0], identity['versionGuid']].append(dict(objectPath=key[1], source='selectedExport', typeGuid=identity['typeGuid']))
    expected_identities = []
    conflicts = set()
    for scope in ['beforeComponent', 'afterComponent']:
        for key in sorted(k for k in identities if k[0] == scope):
            values = identities[key]; types = sorted({v['typeGuid'] for v in values})
            if len(types) > 1:
                conflicts.add(key)
            expected_identities.append(dict(scope=scope, versionGuid=key[1], typeGuids=types, conflictingTypeObservations=len(types) > 1, observations=values))
    assert report['dependencyIdentities'] == expected_identities
    compare = lambda expected, actual: 'unavailable' if actual is None else 'match' if actual == expected else 'different'
    mappings = {}
    expected_rows = []
    assert len(request['dependencyAssociations']) <= 4096
    for selection in request['dependencyAssociations']:
        scope, path, index, target_path = (selection[k] for k in ['scope', 'objectPath', 'dependencyIndex', 'targetObjectPath'])
        owner, target = objects[scope, path], objects[scope, target_path]
        assert isinstance(index, int) and not isinstance(index, bool) and owner['dependencies'] is not None and 0 <= index < len(owner['dependencies'])
        required = owner['dependencies'][index]
        exported = exports.get((scope, target_path))
        file = exported['file'] if exported else None
        identity = exported['exportIdentity'] if exported else None
        target_agreement = compare(required, target['version'])
        export_agreement = compare(required, identity['versionGuid'] if identity else None)
        pair_agreement = exported['identifierAgreement'] if exported else 'unavailable'
        conflict = (scope, required) in conflicts
        agreements = [target_agreement, export_agreement, pair_agreement]
        status = 'different' if 'different' in agreements else 'conflicting' if conflict else 'match' if all(a == 'match' for a in agreements) else 'unavailable'
        row = dict(selection=selection, reviewId=owner['review'], requiredVersionGuid=required, targetTypeGuid=target['type'], targetVersionGuid=target['version'],
                   exportFile=file, exportIdentity=identity, targetVersionAgreement=target_agreement, exportVersionAgreement=export_agreement,
                   targetExportAgreement=pair_agreement, identityConflict=conflict, status=status, declaredCycle=False, nativeIdentityVerified=False, dependencyClosureVerified=False)
        key = scope, path, index
        assert key not in mappings
        mappings[key] = row; expected_rows.append(row)
    # Transitive closure of explicit matching edges, independently from the C# traversal.
    for scope in ['beforeComponent', 'afterComponent']:
        vertices = [path for s, path in objects if s == scope]
        reachable = {path: set() for path in vertices}
        matched = [r for r in expected_rows if r['selection']['scope'] == scope and r['status'] == 'match']
        for row in matched:
            reachable[row['selection']['objectPath']].add(row['selection']['targetObjectPath'])
        for middle in vertices:
            for start in vertices:
                if middle in reachable[start]:
                    reachable[start].update(reachable[middle])
        for row in matched:
            row['declaredCycle'] = row['selection']['objectPath'] in reachable[row['selection']['targetObjectPath']]
    assert report['dependencyAssociations'] == expected_rows
    coverage = []
    for (scope, path), observed in objects.items():
        deps = observed['dependencies']; indices = list(range(len(deps or [])))
        selected = {i: mappings[scope, path, i] for i in indices if (scope, path, i) in mappings}
        repeated = defaultdict(list)
        for index, value in enumerate(deps or []):
            repeated[value].append(index)
        coverage.append(dict(scope=scope, reviewId=observed['review'], objectPath=path,
            dependencyStatus='unavailable' if deps is None else 'observed', reason=observed['reason'], requiredCount=None if deps is None else len(deps),
            selectedIndices=[i for i in indices if i in selected], unassociatedIndices=[i for i in indices if i not in selected],
            repeatedVersionGuids=[dict(versionGuid=v, indices=positions) for v, positions in repeated.items() if len(positions) > 1],
            selfReferenceIndices=[i for i in indices if deps[i] == observed['version']],
            allObservedIndicesMatched=deps is not None and all(i in selected and selected[i]['status'] == 'match' for i in indices), dependencyClosureVerified=False))
    assert report['dependencyCoverage'] == coverage
    summary = dict(dependencyAssociationCount=len(expected_rows), dependencyMismatchCount=sum(r['status'] == 'different' for r in expected_rows),
        dependencyUnavailableCount=sum(r['status'] == 'unavailable' for r in expected_rows), dependencyConflictingCount=sum(r['status'] == 'conflicting' for r in expected_rows),
        dependencyUnobservedObjectCount=sum(r['dependencyStatus'] == 'unavailable' for r in coverage),
        dependencyUnassociatedIndexCount=sum(len(r['unassociatedIndices']) for r in coverage), dependencyCyclicMappingCount=sum(r['declaredCycle'] for r in expected_rows))
    assert all(report['summary'][key] == value for key, value in summary.items())


def negative_controls(record, request, originals, reviews):
    controls = {
        'invented agreement': lambda r: r['report']['dependencyAssociations'][0].update(status='different'),
        'invented dependency': lambda r: r['report']['dependencyAssociations'][0].update(requiredVersionGuid='10000000-0000-0000-0000-000000000099'),
        'invented cycle': lambda r: r['report']['dependencyAssociations'][0].update(declaredCycle=True),
        'invented mapping coverage': lambda r: r['report']['dependencyCoverage'][0]['unassociatedIndices'].append(999),
        'promoted native identity': lambda r: r['report']['dependencyAssociations'][0].update(nativeIdentityVerified=True),
        'invented summary': lambda r: r['report']['summary'].update(dependencyAssociationCount=999),
    }
    failed = []
    for name, mutate in controls.items():
        changed = copy.deepcopy(record); mutate(changed)
        try:
            verify(changed, request, originals, reviews)
        except AssertionError:
            failed.append(name)
        else:
            raise AssertionError('Accepted negative control: ' + name)
    return failed
