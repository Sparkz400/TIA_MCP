"""Independent YAML-tree oracle and actual MCP authoring workflow; no Siemens execution."""
import copy
import hashlib
import json
from pathlib import Path

import yaml
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]


def expected_documents(request):
    resources = {}
    for row in request['lists']:
        entries = {}
        for item in row['entries']:
            entry = {'Type': item['type'], 'Value': item['toValue'] if item['fromValue'] is None else item['fromValue'],
                     'IsDefaultEntry': item['isDefault'], 'Text': request['textLibrary'] + '.' + item['textId']}
            if item['fromValue'] is not None: entry['FromValue'] = item['fromValue']
            if item['toValue'] is not None: entry['ToValue'] = item['toValue']
            entries[item['id']] = entry
        resources[row['name']] = {'RangeType': row['rangeType'], 'Entries': entries}
    return [dict(TextListContainers=dict(TextList=dict(ResourceListType='TextList', ResourceLists=resources))),
            dict(TextLibraries={request['textLibrary']: dict(Type='Text', Languages=request['languages'], DefaultLanguage=request['defaultLanguage'],
                Entries={t['id']: dict(Text=[t['translations'][language] for language in request['languages']]) for t in request['texts']})})]


def parse_candidate(raw):
    assert len(raw) <= 8 * 1024 * 1024 and raw.startswith(b'#Version: 2.0\n')
    text = raw.decode('utf-8', errors='strict')
    # Generated candidates never need aliases, tags or duplicate keys. Reject
    # those before SafeLoader so shadowed values cannot disappear in comparison.
    assert not any(isinstance(token, (yaml.tokens.AliasToken, yaml.tokens.AnchorToken, yaml.tokens.TagToken)) for token in yaml.scan(text))
    node = yaml.compose(text, Loader=yaml.SafeLoader)
    def unique(value):
        if isinstance(value, yaml.nodes.MappingNode):
            keys = [key.value for key, _ in value.value]
            assert len(keys) == len(set(keys)), 'Duplicate YAML mapping key'
            for _, child in value.value: unique(child)
        elif isinstance(value, yaml.nodes.SequenceNode):
            for child in value.value: unique(child)
    unique(node)
    return yaml.safe_load(text)


def verify(request, record, candidates):
    schema = json.loads((ROOT / 'contracts/unified-text-list-author.v1.schema.json').read_text(encoding='utf-8'))
    for value, kind in ((request, 'request'), (record, 'receipt' if 'recordId' in record else 'record')):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    expected = expected_documents(request)
    assert len(candidates) == len(record['files']) == 2
    for index, (row, raw, tree) in enumerate(zip(record['files'], candidates, expected)):
        assert row['fileName'] == request['fileBase'] + ('.hmi.yml' if index == 0 else '.TextLibrary.hmi.yml')
        assert row['bytes'] == len(raw) and row['artifact']['sha256'] == hashlib.sha256(raw).hexdigest()
        actual = parse_candidate(raw)
        # JSON comparison also distinguishes YAML bool values from integer 0/1.
        assert json.dumps(actual, ensure_ascii=False, sort_keys=True) == json.dumps(tree, ensure_ascii=False, sort_keys=True), 'Authored YAML differs from original intent'
    assert record['listCount'] == len(request['lists']) and record['textCount'] == len(request['texts']) and record['languageCount'] == len(request['languages'])


def run(client, upload, read, check, refuse, output):
    request = json.loads((ROOT / 'fixtures/hmi/unified-text-list/author-request.json').read_text(encoding='utf-8'))
    variants = [request]
    expanded = copy.deepcopy(request)
    expanded['texts'][0]['translations']['en-US'] = '"\\\r\n\t\0\x85\u2028\u2029Über 日本語 😀\n---\n!tag &anchor'
    expanded['texts'][0]['translations']['de-DE'] = ''
    expanded['languages'].reverse(); expanded['defaultLanguage'] = 'de-DE'
    expanded['lists'][0]['name'] = 'Unicode 日本語: "quoted" # []'
    expanded['lists'][0]['entries'] = [dict(id='Low', type='To', fromValue=None, toValue=-1, isDefault=True, textId='Text_Off'),
        dict(id='Middle', type='Range', fromValue=0, toValue=10, isDefault=False, textId='Text_Ready'),
        dict(id='High', type='From', fromValue=11, toValue=None, isDefault=False, textId='Text_Ready')]
    for kind in ('Bool', 'BitNumber'):
        row = copy.deepcopy(request['lists'][0]); row.update(name=kind, rangeType=kind); expanded['lists'].append(row)
    variants.append(expanded)
    extremes = copy.deepcopy(request)
    extremes['lists'][0]['entries'][0].update(fromValue=-9223372036854775808, toValue=-9223372036854775808)
    extremes['lists'][0]['entries'][1].update(fromValue=9223372036854775807, toValue=9223372036854775807)
    variants.append(extremes)
    records = []
    for index, value in enumerate(variants):
        raw = json.dumps(value, ensure_ascii=False).encode('utf-8')
        reference = upload(client, raw)
        receipt = client.tool('hmi_text_list_author', dict(uploadRef=reference))
        check(receipt == client.tool('hmi_text_list_author', dict(uploadRef=reference)), 'Authoring retry retains exact receipt')
        candidates = [read(client, row['artifact']) for row in receipt['files']]
        verify(value, receipt, candidates)
        check(read(client, receipt['input']) == raw, 'Authoring retains original intent bytes')
        record = json.loads(read(client, receipt['recordArtifact']))
        verify(value, record, candidates)
        check(True, 'Independent YAML parser matches every original list, value, text reference and ordered translation')
        if index == 0:
            for candidate, name in zip(candidates, ('QualificationTextLists.hmi.yml', 'QualificationTextLists.TextLibrary.hmi.yml')):
                original = (ROOT / 'fixtures/hmi/unified-text-list' / name).read_bytes()
                check(yaml.safe_load(original) == parse_candidate(candidate), 'Authored structure matches retained bilingual fixture: ' + name)
        directory = output / ('authored-' + str(index)); directory.mkdir()
        (directory / 'intent.json').write_bytes(raw); (directory / 'receipt.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding='utf-8')
        for row, candidate in zip(receipt['files'], candidates): (directory / row['fileName']).write_bytes(candidate)
        # Mutation controls alter decoded content then regenerate valid YAML and
        # honest hashes: verification must depend on intent, not just integrity.
        for defect in ('translation', 'range', 'languageOrder', 'extraContent'):
            altered = [parse_candidate(c) for c in candidates]
            text_library = altered[1]['TextLibraries'][value['textLibrary']]
            if defect == 'translation': next(iter(text_library['Entries'].values()))['Text'][0] = 'unintended'
            elif defect == 'languageOrder': text_library['Languages'].reverse()
            elif defect == 'range': next(iter(next(iter(altered[0]['TextListContainers']['TextList']['ResourceLists'].values()))['Entries'].values()))['Value'] = 999
            else: altered[0]['Unexpected'] = 'data'
            changed = [b'#Version: 2.0\n' + yaml.safe_dump(tree, allow_unicode=True, sort_keys=False).encode('utf-8') for tree in altered]
            honest = copy.deepcopy(receipt)
            for row, candidate in zip(honest['files'], changed): row['bytes'] = len(candidate); row['artifact']['sha256'] = hashlib.sha256(candidate).hexdigest()
            try: verify(value, honest, changed)
            except AssertionError: check(True, 'Independent oracle rejects ' + defect)
            else: raise AssertionError('Oracle accepted ' + defect)
        records.append((value, receipt, candidates))
    for arguments in ({}, dict(uploadRef='upload:'+'0'*32, recordId='0'*64)):
        refuse(client, 'hmi_text_list_author', arguments, 'InvalidHmiComponentReview')
    for defect in ('missingTranslation', 'overlap', 'extraClaim', 'path', 'unknownReference', 'fraction'):
        invalid = copy.deepcopy(request)
        if defect == 'missingTranslation': del invalid['texts'][0]['translations']['de-DE']
        elif defect == 'overlap': invalid['lists'][0]['entries'][1].update(fromValue=0, toValue=0)
        elif defect == 'extraClaim': invalid['nativeQualified'] = True
        elif defect == 'path': invalid['fileBase'] = '../outside'
        elif defect == 'unknownReference': invalid['lists'][0]['entries'][0]['textId'] = 'missing'
        else: invalid['lists'][0]['entries'][0]['fromValue'] = 0.1
        refuse(client, 'hmi_text_list_author', dict(uploadRef=upload(client, json.dumps(invalid).encode())), 'PackageInvalid' if defect == 'path' else 'InvalidHmiComponentReview')
    return records


def reopen(client, records, read, check, refuse, artifacts):
    for request, receipt, originals in records:
        check(client.tool('hmi_text_list_author', dict(recordId=receipt['recordId'])) == receipt, 'Authoring survives Host restart and upload release')
        candidates = [read(client, row['artifact']) for row in receipt['files']]
        check(candidates == originals, 'Exact candidate bytes survive restart')
        verify(request, receipt, candidates)
        for reference in [receipt['input'], *(row['artifact'] for row in receipt['files'])]:
            path = artifacts / reference['path']; saved = path.read_bytes()
            try:
                path.write_bytes(saved + b'tampered')
                refuse(client, 'hmi_text_list_author', dict(recordId=receipt['recordId']), 'ArtifactChanged')
            finally: path.write_bytes(saved)
        record = json.loads(read(client, receipt['recordArtifact'])); record['nativeQualified'] = True
        raw = json.dumps(record).encode(); identity = hashlib.sha256(raw).hexdigest()
        (artifacts / 'hmi-component-reviews' / (identity + '.author.json')).write_bytes(raw)
        refuse(client, 'hmi_text_list_author', dict(recordId=identity), 'ArtifactChanged')
