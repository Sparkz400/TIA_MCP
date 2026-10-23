"""Independent schema and retained-source checks for the offline C02 evaluator."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]
SCHEMAS = {name: json.loads((ROOT / f'contracts/composite-engineering-{name}.v1.schema.json').read_text(encoding='utf-8'))
           for name in ('request', 'review', 'workflow')}
SCHEMAS.update({name + '-v2': json.loads((ROOT / f'contracts/composite-engineering-{name}.v2.schema.json').read_text(encoding='utf-8')) for name in ('request', 'review')})
SCHEMAS.update({name + '-v3': json.loads((ROOT / f'contracts/composite-engineering-{name}.v3.schema.json').read_text(encoding='utf-8')) for name in ('request', 'review')})
VALIDATORS = {name: Draft202012Validator(schema) for name, schema in SCHEMAS.items()}


def sample():
    context = dict(reviewId='a' * 64, automationMlRef='automationml:' + 'b' * 32 + ':' + 'c' * 64,
                   connections=[], drives=[], technology=[])
    return dict(schemaVersion='composite-engineering-request.v1', before=context, after=copy.deepcopy(context),
                mappings=[], correspondences=[], allowDifferentProjectCopy=False)


class CompositeContracts(unittest.TestCase):
    def test_schemas_and_minimal_request(self):
        for schema in SCHEMAS.values():
            Draft202012Validator.check_schema(schema)
        VALIDATORS['request'].validate(sample())

    def test_exact_selector_shapes(self):
        r = sample()
        selector = dict(kind='driveParameter', phase='before', recordRef='d' * 64, name='p1', number=1, arrayIndex=-1)
        r['mappings'] = [dict(id='m', phase='before', purpose='parameter', reason='Declared',
                              source=dict(kind='amlAttribute', phase='before', recordRef=r['before']['automationMlRef'], path='/A'), target=selector)]
        VALIDATORS['request'].validate(r)
        for key, value in [('number', '1'), ('arrayIndex', -2), ('name', ' '), ('name', '\u0085'), ('extra', True)]:
            changed = copy.deepcopy(r); changed['mappings'][0]['target'][key] = value
            self.assertFalse(VALIDATORS['request'].is_valid(changed), (key, value))

    def test_collection_and_unknown_field_bounds(self):
        r = sample(); r['before']['connections'] = ['d' * 64] * 65
        self.assertFalse(VALIDATORS['request'].is_valid(r))
        r = sample(); r['nativeQualified'] = True
        self.assertFalse(VALIDATORS['request'].is_valid(r))

    def test_report_never_admits_native_claims(self):
        for flag in ('nativeExecutable', 'nativeQualified', 'sourceAuthenticityVerified', 'liveFreshnessVerified', 'wholeImpactAnalysisComplete'):
            self.assertEqual({'const': False}, SCHEMAS['review']['properties'][flag])

    def test_workflow_inputs_are_bounded_and_exact(self):
        schema = SCHEMAS['workflow']
        reference = 'composite-engineering:' + 'a' * 32 + ':' + 'b' * 64
        for tool, arguments, bad in [
            ('page', dict(recordRef=reference, count=200), {'count': 201}),
            ('artifact_read', dict(recordRef=reference, name='report.json', count=65536), {'name': '../report.json'}),
            ('list', dict(count=20), {'count': 21}),
            ('reopen', dict(recordRef=reference), {'recordRef': reference.upper()}),
            ('review', dict(uploadRef='upload:x'), {'nativeQualified': True}),
        ]:
            validator = Draft202012Validator({**schema, 'oneOf': [schema['x-tool-contracts']['composite_engineering_' + tool]['input']]})
            validator.validate(arguments)
            self.assertFalse(validator.is_valid({**arguments, **bad}), tool)

    def test_workflow_cannot_promote_native_or_complete_impact(self):
        definitions = SCHEMAS['workflow']['$defs']
        for kind in ('summary', 'page', 'chunk', 'list'):
            self.assertEqual({'const': False}, definitions[kind]['properties']['nativeQualified'])
        for kind in ('summary', 'page'):
            self.assertEqual({'const': False}, definitions[kind]['properties']['wholeImpactAnalysisComplete'])

    def test_request_versions_remain_explicit(self):
        value = sample(); VALIDATORS['request'].validate(value)
        self.assertFalse(VALIDATORS['request-v2'].is_valid(value))
        value['schemaVersion'] = 'composite-engineering-request.v2'
        VALIDATORS['request-v2'].validate(value)
        self.assertFalse(VALIDATORS['request'].is_valid(value))

    def test_consumer_schema_never_promotes_context(self):
        context = SCHEMAS['review-v2']['properties']['consumerContexts']['prefixItems'][0]
        self.assertEqual({'const': 'before'}, context['properties']['phase'])
        for name, flag in [('candidates', 'nativeImpactVerified'), ('savedLinks', 'nativeImpactVerified'), ('associations', 'nativeLinkVerified')]:
            self.assertEqual({'const': False}, context['properties'][name]['items']['properties'][flag])
        self.assertEqual({'const': False}, context['properties']['coverage']['properties']['complete'])

    def test_actual_prior_producer_fixture_remains_exact(self):
        root = ROOT / 'fixtures/composite-engineering/retained-v1'
        fixture = json.loads((root / 'fixture.json').read_text(encoding='utf-8'))
        self.assertEqual('actual-prior-producer-synthetic-fixture', fixture['kind'])
        self.assertFalse(fixture['nativeExecuted'])
        for row in fixture['files']:
            raw = (root / 'store' / row['path']).read_bytes()
            self.assertEqual((row['bytes'], row['sha256']), (len(raw), hashlib.sha256(raw).hexdigest()))
        folder = root / 'store/composite-engineering-reviews' / fixture['recordRef'].split(':')[1]
        VALIDATORS['request'].validate(json.loads((folder / 'request.json').read_bytes()))
        VALIDATORS['review'].validate(json.loads((folder / 'report.json').read_bytes()))
        VALIDATORS['workflow'].validate(fixture['summary'])


def verify_fixture(directory):
    root = Path(directory)
    read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
    digest = lambda data: hashlib.sha256(data).hexdigest()
    request = read(root / 'request.json'); report = read(root / 'report.json')
    version2 = request['schemaVersion'] != 'composite-engineering-request.v1'
    typed = request['schemaVersion'] == 'composite-engineering-request.v3'
    suffix = '-v3' if typed else '-v2' if version2 else ''
    VALIDATORS['request' + suffix].validate(request); VALIDATORS['review' + suffix].validate(report)
    aml = {}
    for phase in ('before', 'after'):
        reference = request[phase]['automationMlRef']; _, record, sha = reference.split(':')
        folder = root / 'automationml-workflow' / record
        receipt_bytes = (folder / 'receipt.json').read_bytes()
        assert digest(receipt_bytes) == sha and receipt_bytes == (folder / 'intent.json').read_bytes()
        receipt = json.loads(receipt_bytes)
        for artifact in receipt['artifacts']:
            data = (folder / artifact['name']).read_bytes()
            assert len(data) == artifact['bytes'] and digest(data) == artifact['sha256']
        original = (folder / 'original.aml').read_bytes(); paths = {}

        def walk(element, path):
            paths[path] = element; counts = {}
            for child in element:
                counts[child.tag] = counts.get(child.tag, 0) + 1
                walk(child, f'{path}/{child.tag.split("}")[-1]}[{counts[child.tag]}]')

        walk(ET.fromstring(original), '/CAEXFile[1]'); aml[phase] = paths
        assert digest(original) == report['contexts'][phase]['automationMlSha256'] == report['amlComparison'][phase + 'Sha256']
    selections = {row['key']: row for row in report['selections']}
    for phase in ('before', 'after'):
        for component in report['contexts'][phase]['components']:
            domain = {'connection': 'connection-reviews', 'drive': 'drive-profile-reviews', 'technology': 'technology-profile-reviews'}[component['kind']]
            component_bytes = (root / domain / (component['recordRef'] + '.json')).read_bytes()
            assert digest(component_bytes) == component['recordRef']
            envelope = json.loads(component_bytes)
            for field in ('schemaVersion', 'afterKind', 'summary', 'coverage', 'limits'):
                assert component[field] == envelope['report'][field]
    for key, row in selections.items():
        s = row['selector']; evidence = row['evidence']; phase = s['phase']
        assert json.loads(key) == s and row['state'] == 'resolvedDeclaration'
        if s['kind'].startswith('aml'):
            original = aml[phase][s['path']]
            assert evidence['path'] == s['path']
            if 'id' in evidence:
                assert evidence['id'] == original.get('ID')
            if s['kind'] == 'amlAttribute':
                assert evidence['values'] == [c.text for c in original if c.tag == 'Value']
            continue
        review_bytes = (root / 'project-reviews' / (request[phase]['reviewId'] + '.json')).read_bytes()
        assert digest(review_bytes) == request[phase]['reviewId']; review = json.loads(review_bytes)
        if s['kind'] == 'model':
            expected = [n for n in review['model']['nodes'] if n['nodeId'] == s['nodeId'] and n['objectPath'] == s['objectPath']]
            assert expected == [evidence]
            continue
        if s['kind'] in ('driveTelegram', 'driveTelegramAddress', 'technologyEncoder'):
            from composite_typed_oracle import verify_selection
            verify_selection(root, request, row)
            continue
        domain, collection, field = {'connectionEndpoint': ('connection-reviews', 'connections', 'connection'),
                                     'driveParameter': ('drive-profile-reviews', 'parameters', 'parameter'),
                                     'technologyRelationship': ('technology-profile-reviews', 'relationships', 'relationship')}[s['kind']]
        data = (root / domain / (s['recordRef'] + '.json')).read_bytes()
        assert digest(data) == s['recordRef']; envelope = json.loads(data)
        assert envelope['reviewId'] == request[phase]['reviewId']
        data = (root / envelope['input']['path']).read_bytes()
        assert digest(data) == envelope['input']['sha256']; original = json.loads(data)
        selector_field = 'name' if s['kind'] == 'driveParameter' else 'id'
        expected = [r for r in original[phase][collection] if r[selector_field] == s[selector_field]]
        assert expected == [evidence[field]] and evidence['phase'] == phase
        if s['kind'] == 'driveParameter':
            assert expected[0]['number'] == s['number'] and expected[0]['arrayIndex'] == s['arrayIndex']
            assert row['profile']['identity'] == original[phase]['identity']
    assert len(report['mappings']) == (6 if typed else 2 if version2 else 8) and all(r['state'] == 'declaredAssociation' for r in report['mappings'])
    if typed:
        from composite_typed_oracle import verify_mappings
        verify_mappings(request, report)
    assert report['amlComparison']['sourceBytesChanged'] is True
    if not version2:
        assert any({selections[k]['selector']['kind'] for k in i['destinationKeys']} >= {'model', 'driveParameter', 'technologyRelationship'} for i in report['impacts'])
    assert {'declaredMappingGraphOnly', 'wholeDocumentReviewRequired'} <= {g['kind'] for g in report['coverageGaps']}
    for flag in ('nativeExecutable', 'nativeQualified', 'sourceAuthenticityVerified', 'liveFreshnessVerified', 'wholeImpactAnalysisComplete'):
        forged = copy.deepcopy(report); forged[flag] = True
        assert not VALIDATORS['review' + suffix].is_valid(forged)
    result = dict(completed=True, nativeExecuted=False, selections=len(selections), mappings=len(report['mappings']),
                  sourceBytesIndependentlyChecked=True, fullCompositeWorkflowComplete=False)
    if version2:
        from composite_consumer_oracle import verify_consumers
        result['consumerContextsIndependentlyChecked'] = verify_consumers(root, request, report)
    workflow = root / 'workflow-verification.json'
    if workflow.exists():
        evidence = read(workflow)
        assert evidence['completed'] and not evidence['nativeExecuted']
        expected_files = set()
        for process, name in zip(evidence['processes'], ['intent.json', 'request.json', 'report.json', 'receipt.json'], strict=True):
            assert process['childExitCode'] != 0 and process['nativeExecuted'] is False
            folder = Path(process['folder']); assert folder.parent == root.resolve() / 'composite-engineering-reviews'
            expected_files.add(name)
            assert {p.name for p in folder.iterdir()} == expected_files
            receipt = read(folder / 'intent.json')
            Draft202012Validator({**SCHEMAS['workflow'], 'oneOf': [{'$ref': '#/$defs/receipt'}]}).validate(receipt)
            for artifact in receipt['artifacts']:
                if artifact['name'] in expected_files:
                    data = (folder / artifact['name']).read_bytes()
                    assert len(data) == artifact['bytes'] and digest(data) == artifact['sha256']
            assert process['committed'] == (name == 'receipt.json')
            if process['committed']: assert (folder / 'receipt.json').read_bytes() == (folder / 'intent.json').read_bytes()
        result['interruptedPublicationFilesIndependentlyChecked'] = True
    (root / 'independent-verification.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print('PASS independent C02 schema, original AML and component input selectors; no native qualification.')
    if (root / 'consumer-v2/request.json').exists(): verify_fixture(root / 'consumer-v2')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--fixture'); args = parser.parse_args()
    if args.fixture:
        verify_fixture(args.fixture)
    else:
        unittest.main(argv=[__file__])
