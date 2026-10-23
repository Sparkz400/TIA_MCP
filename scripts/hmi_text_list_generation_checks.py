"""Independent original-request, saved-PLC declaration and full YAML-tree checks."""
import copy
import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path

from jsonschema import Draft202012Validator
from hmi_text_list_author_checks import expected_documents, parse_candidate, ROOT


def validate(value, kind):
    schema = json.loads((ROOT / 'contracts/unified-text-list-author.v2.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)


def expected_intent(request, original_review_request):
    output = next(o for o in original_review_request['outputs'] if o['id'] == request['outputId'])
    template = request['template']
    return dict(schemaVersion='unified-text-list-author-request.v1',
                **{key: copy.deepcopy(template[key]) for key in ('fileBase', 'textLibrary', 'languages', 'defaultLanguage')},
                texts=[dict(id=name + 'Text', translations=copy.deepcopy(template[name.lower() + 'Text'])) for name in ('False', 'True')],
                lists=[dict(name=output['name'], rangeType='Bool', entries=[
                    dict(id=name, type='SingleValue', fromValue=value, toValue=value, isDefault=False, textId=name + 'Text')
                    for value, name in enumerate(('False', 'True'))])])


def verify(request, receipt, read):
    validate(request, 'request')
    validate(receipt, 'receipt' if 'recordId' in receipt else 'record')
    component_record = json.loads(read(dict(path='hmi-component-reviews/' + request['componentReviewId'] + '.json', sha256=request['componentReviewId'])))
    original = json.loads(read(component_record['input']))
    output = next(o for o in original['outputs'] if o['id'] == request['outputId'])
    binding = next(b for b in original['bindings'] if b['id'] == request['bindingId'])
    assert binding['outputId'] == output['id']
    assert original['schemaVersion'] == 'hmi-component-review-request.v7'
    intent = expected_intent(request, original)
    assert receipt['intent'] == intent, 'Derived intent must originate in original template and selected output'
    assert receipt['componentContext']['reviewRecordId'] == request['componentReviewId']
    assert receipt['componentContext']['binding']['selection'] == binding
    assert receipt['previousRecordId'] == request['previousRecordId']
    selected_peers = [next(o for o in original['outputs'] if o['id'] == identity) for identity in request['retainOutputIds']]
    files = {f['id']: f for f in original['files']}
    preserved = []
    for peer in selected_peers:
        assert peer['action'] == 'retain'
        for source in sorted((files[i] for i in peer['current']), key=lambda f: f['fileName']):
            row = dict(outputId=peer['id'], fileName=source['fileName'], bytes=source['bytes'],
                       artifact=dict(path='hmi-component-reviews/' + source['sha256'] + '.source', sha256=source['sha256']))
            raw = read(row['artifact'])
            assert len(raw) == row['bytes']
            preserved.append(row)
    assert receipt['preservedFiles'] == preserved, 'Every selected manual peer retains original bytes and filename'
    if receipt['status'] == 'conflict':
        assert receipt['files'] == [] and receipt['conflicts']
        return
    assert receipt['status'] == 'candidatesReady' and receipt['conflicts'] == []
    assert binding['declaredHmiType'] == 'Bool' and binding['afterMember'] is not None
    source_id = original['afterComponent']['reviewId']
    saved = json.loads(read(dict(path='project-reviews/' + source_id + '.json', sha256=source_id)))
    member = binding['afterMember']
    assert member['objectPath'] in original['afterComponent']['objectPaths']
    document = next(d for d in saved['documents'] if d['objectPath'] == member['objectPath'])
    xml = document['xml']
    assert hashlib.sha256(xml.encode()).hexdigest() == document['utf8Sha256']
    tree = ET.fromstring(xml)
    for element in tree.iter():
        element.tag = element.tag.rsplit('}', 1)[-1]
    owners = [e for e in tree if e.tag.startswith(('SW.Blocks.', 'SW.Types.'))]
    assert len(owners) == 1
    interfaces = owners[0].findall('./AttributeList/Interface')
    assert len(interfaces) == 1
    cursor = interfaces[0]
    for name in member['memberPath']:
        matches = [m for section in cursor.findall('./Sections/Section') for m in section.findall('./Member') if m.get('Name') == name]
        assert len(matches) == 1
        cursor = matches[0]
    assert cursor.get('Datatype') == 'Bool', 'Captured original member must be Bool'
    assert output['ownership'] == 'generated'
    if output['action'] == 'update':
        assert output['currentComplete'] and output['baselineComplete']
        prior_id = request['previousRecordId']
        prior = json.loads(read(dict(path='hmi-component-reviews/' + prior_id + '.author.json', sha256=prior_id)))
        expected_pair = sorted((p['fileName'], p['artifact']['sha256'], p['bytes']) for p in prior['files'])
        for phase in ('baseline', 'current'):
            actual_pair = sorted((files[i]['fileName'], files[i]['sha256'], files[i]['bytes']) for i in output[phase])
            assert actual_pair == expected_pair
        if prior['schemaVersion'].endswith('.v2'):
            assert prior['componentContext']['afterComponent']['selection']['reviewId'] == original['beforeComponent']['reviewId']
            assert prior['componentContext']['binding']['selection']['afterMember'] == binding['beforeMember']
    else:
        assert output['action'] == 'create' and request['previousRecordId'] is None
        assert output['currentComplete'] and not output['current'] and not output['baseline']
    expected = expected_documents(intent)
    assert len(receipt['files']) == 2
    for index, (row, wanted) in enumerate(zip(receipt['files'], expected)):
        raw = read(row['artifact'])
        assert row['fileName'] == intent['fileBase'] + ('.hmi.yml' if index == 0 else '.TextLibrary.hmi.yml')
        assert len(raw) == row['bytes']
        assert json.dumps(parse_candidate(raw), sort_keys=True, ensure_ascii=False) == json.dumps(wanted, sort_keys=True, ensure_ascii=False), 'Candidate differs from independent original-input expectation'
    names = [r['fileName'].lower() for r in receipt['files'] + receipt['preservedFiles']]
    assert len(names) == len(set(names)), 'Preserved peer must not collide with a generated candidate'


def local_reader(root):
    root = Path(root).resolve()
    def read(reference):
        path = (root / reference['path']).resolve()
        path.relative_to(root)
        raw = path.read_bytes()
        assert hashlib.sha256(raw).hexdigest() == reference['sha256']
        return raw
    return read
