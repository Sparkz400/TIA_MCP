"""Independent original PLC, state-interval, dependency and complete YAML-tree oracle."""
import copy
import hashlib
import json
import xml.etree.ElementTree as ET

from jsonschema import Draft202012Validator
from hmi_text_list_author_checks import expected_documents, parse_candidate, ROOT
from hmi_text_list_generation_checks import local_reader, expected_intent as bool_intent
from hmi_dependency_impact_checks import verify as verify_dependencies

DOMAINS = {'SInt': (-128, 127), 'USInt': (0, 255), 'Int': (-32768, 32767),
           'UInt': (0, 65535), 'DInt': (-2147483648, 2147483647),
           'UDInt': (0, 4294967295), 'LInt': (-9223372036854775808, 9223372036854775807)}


def validate(value, kind):
    schema = json.loads((ROOT / 'contracts/unified-text-list-author.v3.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)


def retained(read, identity, suffix):
    return json.loads(read(dict(path='hmi-component-reviews/' + identity + suffix, sha256=identity)))


def expected_intent(request, original):
    output = next(o for o in original['outputs'] if o['id'] == request['outputId'])
    t = request['template']
    return dict(schemaVersion='unified-text-list-author-request.v1',
                **{k: copy.deepcopy(t[k]) for k in ('fileBase', 'textLibrary', 'languages', 'defaultLanguage')},
                texts=[dict(id=s['id'], translations=copy.deepcopy(s['translations'])) for s in t['states']],
                lists=[dict(name=output['name'], rangeType='Decimal', entries=[
                    dict(**{k: s[k] for k in ('id', 'type', 'fromValue', 'toValue', 'isDefault')}, textId=s['id'])
                    for s in t['states']])])


def source_intent(read, record):
    request = json.loads(read(record['input']))
    if request['schemaVersion'].endswith('.v1'):
        return request
    component = retained(read, request['componentReviewId'], '.json')
    original = json.loads(read(component['input']))
    return (expected_intent if request['schemaVersion'].endswith('.v3') else bool_intent)(request, original)


def expected_coverage(template):
    low, high = DOMAINS[template['dataType']]
    ranges = sorted((s['fromValue'], s['toValue']) for s in template['states'])
    gaps, cursor = [], low
    for start, end in ranges:
        assert type(start) is int and type(end) is int and low <= start <= end <= high
        assert cursor <= start, 'Overlapping state intervals'
        if cursor < start:
            gaps.append(dict(fromValue=cursor, toValue=start - 1))
        cursor = end + 1  # Python integers independently avoid signed Int64 overflow.
    if cursor <= high:
        gaps.append(dict(fromValue=cursor, toValue=high))
    return dict(dataType=template['dataType'], minimum=low, maximum=high, stateCount=len(ranges),
                unlistedIntervals=gaps, allTypeValuesListed=not gaps,
                requireFullTypeCoverage=template['requireFullTypeCoverage'],
                defaultCoversUnlistedValuesVerified=False, nativeNumericSemanticsVerified=False)


def expected_changes(previous, current):
    def states(intent):
        texts = {t['id']: t['translations'] for t in intent['texts']}
        values = {}
        for lst in intent['lists']:
            for entry in lst['entries']:
                row = values.setdefault(entry['id'], dict(entries=[], translations=[]))
                row['entries'].append(entry)
                row['translations'].append(texts[entry['textId']])
        return values
    before, after = states(previous) if previous else {}, states(current)
    return [dict(id=identity, change='added' if identity not in before else 'removed' if identity not in after
                 else 'unchanged' if before[identity] == after[identity] else 'changed',
                 before=before.get(identity), after=after.get(identity), consumerCompatibilityVerified=False)
            for identity in sorted(before.keys() | after.keys())]


def verify(request, receipt, read):
    validate(request, 'request')
    validate(receipt, 'receipt' if 'recordId' in receipt else 'record')
    component = retained(read, request['componentReviewId'], '.json')
    original = json.loads(read(component['input']))
    report = component['report']
    assert original['schemaVersion'] == 'hmi-component-review-request.v7'
    output = next(o for o in original['outputs'] if o['id'] == request['outputId'])
    binding = next(b for b in original['bindings'] if b['id'] == request['bindingId'])
    assert binding['outputId'] == output['id']
    files = {f['id']: f for f in original['files']}
    originals = {i: read(dict(path='hmi-component-reviews/' + f['sha256'] + '.source', sha256=f['sha256']))
                 for i, f in files.items()}
    reviews = {}
    for scope in ('beforeComponent', 'afterComponent', 'destination'):
        if original[scope] is not None:
            identity = original[scope]['reviewId']
            reviews[identity] = json.loads(read(dict(path='project-reviews/' + identity + '.json', sha256=identity)))
    verify_dependencies(component, original, originals, reviews)
    # Every incoming path used below is checked against the original saved graph.
    for path in report['consumerPaths']:
        selected = original[path['scope']]
        assert selected['reviewId'] == path['reviewId'] and path['rootObjectPath'] in selected['objectPaths']
        edges = {e['edgeId']: e for e in reviews[path['reviewId']]['model']['edges']}
        assert path['objectPaths'][0] == path['consumerObjectPath'] and path['objectPaths'][-1] == path['rootObjectPath']
        assert path['distance'] == len(path['edges']) == len(path['objectPaths']) - 1
        for index, edge in enumerate(path['edges']):
            assert edge == edges[edge['edgeId']]
            assert (edge['source']['objectPath'], edge['target']['objectPath']) == tuple(path['objectPaths'][index:index + 2])
    roots = set()
    for scope, field in (('beforeComponent', 'beforeMember'), ('afterComponent', 'afterMember')):
        if binding[field] is None:
            continue
        selected = binding[field]['objectPath']
        roots.add((scope, selected))
        roots.update((scope, p['rootObjectPath']) for p in report['consumerPaths'] if p['scope'] == scope
                     and p['reviewId'] == original[scope]['reviewId'] and p['consumerObjectPath'] == selected)
    associations = [dict(associationIndex=i, association=row) for i, row in enumerate(report['dependencyAssociations'])
                    if any((row['selection']['scope'], row['selection'][key]) in roots for key in ('objectPath', 'targetObjectPath'))]
    scopes = set(roots)
    for row in associations:
        s = row['association']['selection']
        scopes.update((s['scope'], s[key]) for key in ('objectPath', 'targetObjectPath'))
    coverage = [row for row in report['dependencyCoverage'] if (row['scope'], row['objectPath']) in scopes]
    expected_dependency = dict(selectedScopes=coverage, associations=associations,
        contradictionCount=sum(r['association']['status'] in ('different', 'conflicting') for r in associations),
        allObservedSelectedIndicesMatched=bool(coverage) and len(coverage) == len(scopes) and all(r['allObservedIndicesMatched'] for r in coverage),
        dependencyClosureVerified=False, consumerCoverageComplete=False,
        sourceImpactCoverage=report['dependencyImpactCoverage'], sourceConsumerCoverage=report['consumerCoverage'])
    assert {k: v for k, v in receipt['dependencyCoverage'].items() if k != 'policy'} == expected_dependency
    intent = expected_intent(request, original)
    assert receipt['intent'] == intent
    assert receipt['valueCoverage'] == expected_coverage(request['template'])
    previous = retained(read, request['previousRecordId'], '.author.json') if request['previousRecordId'] else None
    prior_intent = source_intent(read, previous) if previous and output['action'] == 'update' else None
    assert receipt['stateChanges'] == expected_changes(prior_intent, intent)
    context = receipt['componentContext']
    assert context['reviewRecordId'] == request['componentReviewId']
    assert context['outputId'] == output['id'] and context['bindingId'] == binding['id']
    assert context['binding']['selection'] == binding
    assert receipt['previousRecordId'] == request['previousRecordId']
    preserved = []
    for identity in request['retainOutputIds']:
        peer = next(o for o in original['outputs'] if o['id'] == identity)
        assert peer['action'] == 'retain'
        for file in sorted((files[i] for i in peer['current']), key=lambda f: f['fileName']):
            preserved.append(dict(outputId=identity, fileName=file['fileName'], bytes=len(originals[file['id']]),
                                  artifact=dict(path='hmi-component-reviews/' + file['sha256'] + '.source', sha256=file['sha256'])))
    assert receipt['preservedFiles'] == preserved
    if receipt['status'] == 'conflict':
        assert receipt['conflicts'] and not receipt['files']
        return
    assert receipt['status'] == 'candidatesReady' and not receipt['conflicts']
    assert expected_dependency['contradictionCount'] == 0
    assert not request['template']['requireFullTypeCoverage'] or receipt['valueCoverage']['allTypeValuesListed']
    member = binding['afterMember']
    assert member is not None and binding['declaredHmiType'] == request['template']['dataType']
    assert member['objectPath'] in original['afterComponent']['objectPaths']
    saved = reviews[original['afterComponent']['reviewId']]
    document = next(d for d in saved['documents'] if d['objectPath'] == member['objectPath'])
    assert hashlib.sha256(document['xml'].encode()).hexdigest() == document['utf8Sha256']
    tree = ET.fromstring(document['xml'])
    for element in tree.iter():
        element.tag = element.tag.rsplit('}', 1)[-1]
    owners = [e for e in tree if e.tag.startswith(('SW.Blocks.', 'SW.Types.'))]
    assert len(owners) == 1
    interfaces = owners[0].findall('./AttributeList/Interface')
    assert len(interfaces) == 1
    cursor = interfaces[0]
    for name in member['memberPath']:
        matches = [m for s in cursor.findall('./Sections/Section') for m in s.findall('./Member') if m.get('Name') == name]
        assert len(matches) == 1
        cursor = matches[0]
    assert cursor.get('Datatype') == request['template']['dataType']
    assert output['ownership'] == 'generated' and output['currentComplete']
    if output['action'] == 'update':
        assert previous and output['baselineComplete']
        pair = sorted((p['fileName'], p['artifact']['sha256'], p['bytes']) for p in previous['files'])
        for phase in ('baseline', 'current'):
            assert sorted((files[i]['fileName'], files[i]['sha256'], files[i]['bytes']) for i in output[phase]) == pair
        assert len(prior_intent['lists']) == 1 and prior_intent['lists'][0]['rangeType'] == 'Decimal'
        assert prior_intent['lists'][0]['name'] == output['name'] and prior_intent['textLibrary'] == intent['textLibrary']
        if previous['schemaVersion'].endswith(('.v2', '.v3')):
            assert previous['componentContext']['afterComponent']['selection']['reviewId'] == original['beforeComponent']['reviewId']
            assert previous['componentContext']['binding']['selection']['afterMember'] == binding['beforeMember']
        if previous['schemaVersion'].endswith('.v3'):
            assert previous['valueCoverage']['dataType'] == request['template']['dataType']
    else:
        assert output['action'] == 'create' and previous is None and not output['current'] and not output['baseline']
    assert len(receipt['files']) == 2
    for index, (file, wanted) in enumerate(zip(receipt['files'], expected_documents(intent))):
        raw = read(file['artifact'])
        assert len(raw) == file['bytes']
        assert file['fileName'] == intent['fileBase'] + ('.hmi.yml' if index == 0 else '.TextLibrary.hmi.yml')
        assert parse_candidate(raw) == wanted, 'YAML tree must equal independent original state/template expectation'
    names = [f['fileName'].lower() for f in receipt['files'] + receipt['preservedFiles']]
    assert len(names) == len(set(names))
