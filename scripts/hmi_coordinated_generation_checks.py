"""Independent original-selection, dependency and complete YAML-output oracle."""
import copy
import json
import xml.etree.ElementTree as ET

from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError
from hmi_text_list_author_checks import ROOT, expected_documents, parse_candidate
from hmi_text_list_generation_checks import local_reader, expected_intent as bool_intent, verify as verify_bool
from hmi_numeric_generation_checks import expected_intent as numeric_intent, expected_coverage, verify as verify_numeric, retained
from hmi_dependency_impact_checks import verify as verify_dependencies


def verify(request, receipt, read):
    schema = json.loads((ROOT / 'contracts/unified-text-list-set.v1.schema.json').read_text(encoding='utf-8'))
    for value, kind in ((request, 'request'), (receipt, 'receipt' if 'recordId' in receipt else 'record')):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    assert json.loads(read(receipt['input'])) == request
    component = retained(read, request['componentReviewId'], '.json')
    original = json.loads(read(component['input']))
    report = component['report']
    assert original['schemaVersion'] == 'hmi-component-review-request.v7'
    assert receipt['componentReviewId'] == request['componentReviewId']
    assert receipt['previousRecordId'] == request['previousRecordId']
    source_files = {f['id']: f for f in original['files']}
    originals = {i: read(dict(path='hmi-component-reviews/' + f['sha256'] + '.source', sha256=f['sha256']))
                 for i, f in source_files.items()}
    reviews = {}
    for scope in ('beforeComponent', 'afterComponent', 'destination'):
        if original[scope]:
            identity = original[scope]['reviewId']
            reviews[identity] = json.loads(read(dict(path='project-reviews/' + identity + '.json', sha256=identity)))
    verify_dependencies(component, original, originals, reviews)
    selected = {o['outputId'] for o in request['outputs']}
    selected_bindings = {o['bindingId'] for o in request['outputs']}
    coverage = receipt['coverage']
    assert coverage['declaredOutputIds'] == [o['id'] for o in original['outputs']]
    assert coverage['declaredBindingIds'] == [b['id'] for b in original['bindings']]
    assert coverage['uncoveredOutputIds'] == [o['id'] for o in original['outputs']
                                              if o['id'] not in selected | set(request['retainOutputIds'])]
    assert coverage['uncoveredBindingIds'] == [b['id'] for b in original['bindings'] if b['id'] not in selected_bindings]
    assert coverage['allDeclaredOutputsCovered'] == (not coverage['uncoveredOutputIds'])
    assert coverage['allDeclaredBindingsCovered'] == (not coverage['uncoveredBindingIds'])
    assert coverage['sourceConsumerCoverage'] == report['consumerCoverage']
    assert coverage['sourceDependencyImpactCoverage'] == report['dependencyImpactCoverage']
    for field in ('consumerCoverageComplete', 'dependencyClosureVerified', 'componentMemberCoverageComplete'):
        assert coverage[field] is False
    # Reconstruct captured member enumeration from original XML, independently
    # retaining unselected declarations, the depth bound and unavailable objects.
    members, unavailable = [], []
    truncated = False
    for scope, field in (('beforeComponent', 'beforeMember'), ('afterComponent', 'afterMember')):
        if not original[scope]:
            continue
        identity = original[scope]['reviewId']
        saved = reviews[identity]
        for path in original[scope]['objectPaths']:
            docs = [d for d in saved['documents'] if d['objectPath'] == path and d['status'] == 'exported']
            owners = []
            if len(docs) == 1:
                tree = ET.fromstring(docs[0]['xml'])
                for element in tree.iter():
                    element.tag = element.tag.rsplit('}', 1)[-1]
                owners = [e for e in tree if e.tag.startswith('SW.Blocks.') or e.tag == 'SW.Types.PlcStruct']
            interfaces = owners[0].findall('./AttributeList/Interface') if len(owners) == 1 else []
            if len(interfaces) != 1:
                unavailable.append(dict(scope=scope, objectPath=path))
                continue
            def walk(parent, prefix):
                nonlocal truncated
                for member in parent.findall('./Sections/Section/Member'):
                    if len(members) >= 4096 or len(prefix) >= 16:
                        truncated = True
                        return
                    parts = prefix + [member.get('Name', '')]
                    ids = [b['id'] for b in original['bindings'] if b['id'] in selected_bindings and b[field] and
                           b[field]['objectPath'] == path and b[field]['memberPath'] == parts]
                    members.append(dict(scope=scope, reviewId=identity, objectPath=path, memberPath=parts,
                                        dataType=member.get('Datatype'), bindingIds=ids))
                    walk(member, parts)
            walk(interfaces[0], [])
    observed = coverage['members']
    assert [{k: v for k, v in m.items() if k != 'evidence'} for m in observed['observations']] == members
    for member in observed['observations']:
        doc = next(d for d in reviews[member['reviewId']]['documents'] if d['objectPath'] == member['objectPath'])
        assert member['evidence']['utf8Sha256'] == doc['utf8Sha256']
        assert member['evidence']['liveVerified'] is False
    assert observed['unavailableObjects'] == unavailable and observed['truncated'] == truncated
    assert observed['componentMemberCoverageComplete'] is False
    previous = retained(read, request['previousRecordId'], '.text-set.json') if request['previousRecordId'] else None
    expected_files = []
    ready = receipt['status'] == 'candidatesReady'
    assert len(receipt['outputs']) == len(request['outputs'])
    for selection, output in zip(request['outputs'], receipt['outputs']):
        numeric = selection['profile'] == 'unified-integer-state-text-list.v1'
        prior = next((p['authorRecordId'] for p in previous['outputs'] if p['outputId'] == selection['outputId']), None) if previous else selection['previousAuthorRecordId']
        child_request = dict(schemaVersion='unified-text-list-author-request.v3' if numeric else 'unified-text-list-author-request.v2',
            componentReviewId=request['componentReviewId'], outputId=selection['outputId'], bindingId=selection['bindingId'],
            profile=selection['profile'], previousRecordId=prior, template=selection['template'], retainOutputIds=[])
        intent = (numeric_intent if numeric else bool_intent)(child_request, original)
        assert output['intent'] == intent
        assert all(output[k] == selection[k] for k in ('outputId', 'bindingId', 'profile'))
        binding = next(b for b in original['bindings'] if b['id'] == selection['bindingId'])
        assert output['componentContext']['binding']['selection'] == binding
        roots = set()
        for scope, field in (('beforeComponent', 'beforeMember'), ('afterComponent', 'afterMember')):
            if not binding[field]:
                continue
            path = binding[field]['objectPath']
            roots.add((scope, path))
            roots.update((scope, node) for p in report['consumerPaths'] if p['scope'] == scope
                         and p['reviewId'] == original[scope]['reviewId'] and path in (p['rootObjectPath'], p['consumerObjectPath'])
                         for node in p['objectPaths'])
        associations = [dict(associationIndex=i, association=row) for i, row in enumerate(report['dependencyAssociations'])
                        if any((row['selection']['scope'], row['selection'][k]) in roots for k in ('objectPath', 'targetObjectPath'))]
        scopes = set(roots)
        for row in associations:
            s = row['association']['selection']
            scopes.update((s['scope'], s[k]) for k in ('objectPath', 'targetObjectPath'))
        covered = [r for r in report['dependencyCoverage'] if (r['scope'], r['objectPath']) in scopes]
        expected_dependency = dict(selectedScopes=covered, associations=associations,
            unobservedScopes=[dict(scope=s, objectPath=p) for s, p in sorted(scopes - {(r['scope'], r['objectPath']) for r in covered})],
            contradictionCount=sum(r['association']['status'] in ('different', 'conflicting') for r in associations),
            allObservedSelectedIndicesMatched=bool(covered) and len(covered) == len(scopes) and all(r['allObservedIndicesMatched'] for r in covered),
            dependencyClosureVerified=False, consumerCoverageComplete=False,
            sourceImpactCoverage=report['dependencyImpactCoverage'], sourceConsumerCoverage=report['consumerCoverage'])
        assert {k: v for k, v in output['dependencyCoverage'].items() if k != 'policy'} == expected_dependency
        paths = [p for p in report['consumerPaths'] if any(binding[field] and p['scope'] == scope
                 and p['reviewId'] == original[scope]['reviewId'] and
                 binding[field]['objectPath'] in (p['rootObjectPath'], p['consumerObjectPath'])
                 for scope, field in (('beforeComponent', 'beforeMember'), ('afterComponent', 'afterMember')))]
        assert output['consumerPaths'] == paths
        for p in paths:
            edges = {e['edgeId']: e for e in reviews[p['reviewId']]['model']['edges']}
            assert p['distance'] == len(p['edges']) == len(p['objectPaths']) - 1
            for i, edge in enumerate(p['edges']):
                assert edge == edges[edge['edgeId']]
                assert (edge['source']['objectPath'], edge['target']['objectPath']) == tuple(p['objectPaths'][i:i+2])
        if numeric:
            assert output['valueCoverage'] == expected_coverage(selection['template'])
        else:
            assert output['valueCoverage'] is None and output['stateChanges'] is None
        if ready:
            assert expected_dependency['contradictionCount'] == 0
            assert not request['requireObservedDependencies'] or expected_dependency['allObservedSelectedIndicesMatched']
            child = retained(read, output['authorRecordId'], '.author.json')
            assert json.loads(read(child['input'])) == child_request
            (verify_numeric if numeric else verify_bool)(child_request, child, read)
            assert child['componentContext'] == output['componentContext']
            for actual, expected in zip(child['files'], expected_documents(intent)):
                assert parse_candidate(read(actual['artifact'])) == expected
                expected_files.append(dict(**actual, outputId=selection['outputId']))
        else:
            assert output['authorRecordId'] is None
    preserved = []
    for identity in request['retainOutputIds']:
        peer = next(o for o in original['outputs'] if o['id'] == identity)
        assert peer['action'] == 'retain'
        for file in sorted((source_files[i] for i in peer['current']), key=lambda f: f['fileName']):
            preserved.append(dict(outputId=identity, fileName=file['fileName'], bytes=len(originals[file['id']]),
                                  artifact=dict(path='hmi-component-reviews/' + file['sha256'] + '.source', sha256=file['sha256'])))
    assert receipt['preservedFiles'] == preserved
    assert receipt['files'] == expected_files
    if ready:
        assert not receipt['conflicts'] and not coverage['uncoveredOutputIds'] and not coverage['uncoveredBindingIds']
        names = [f['fileName'].casefold() for f in expected_files + preserved]
        libraries = [o['intent']['textLibrary'].casefold() for o in receipt['outputs']]
        lists = [o['intent']['lists'][0]['name'].casefold() for o in receipt['outputs']]
        assert len(names) == len(set(names)) and len(libraries) == len(set(libraries)) and len(lists) == len(set(lists))
        assert all(o['intent']['languages'] == receipt['outputs'][0]['intent']['languages'] and
                   o['intent']['defaultLanguage'] == receipt['outputs'][0]['intent']['defaultLanguage'] for o in receipt['outputs'])
        if previous:
            assert previous['status'] == 'candidatesReady'
            assert selected == {o['outputId'] for o in previous['outputs']}
            assert previous['preservedFiles'] == preserved
    else:
        assert receipt['conflicts']


def negative_controls(request, receipt, read):
    checks = []
    for label, mutate in [
        ('coverage promotion', lambda r: r['coverage'].update(consumerCoverageComplete=True)),
        ('missing output', lambda r: r['outputs'].pop()),
        ('omitted unselected declaration', lambda r: r['coverage']['members']['observations'].pop()),
        ('dependency invention', lambda r: r['outputs'][0]['dependencyCoverage'].update(allObservedSelectedIndicesMatched=True)),
        ('language ordering', lambda r: r['outputs'][0]['intent']['languages'].reverse()),
        ('wrong translation', lambda r: r['outputs'][1]['intent']['texts'][0]['translations'].update({'en-US': 'invented'})),
        ('candidate ownership swap', lambda r: r['files'][0].update(outputId='state')),
    ]:
        changed = copy.deepcopy(receipt)
        mutate(changed)
        try:
            verify(request, changed, read)
        except (AssertionError, ValidationError):
            checks.append(label)
        else:
            raise AssertionError('Independent control accepted: ' + label)
    return checks
