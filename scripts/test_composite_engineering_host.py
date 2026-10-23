"""Actual offline C02 MCP intake, independent source checks, recovery and bounded transfer."""
import argparse
import copy
import hashlib
import json
import base64
from pathlib import Path

from jsonschema import Draft202012Validator
from smoke_mcp import Client, ROOT
from test_composite_engineering_contracts import verify_fixture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    source = Path(fixture['artifactRoot'])
    request = json.loads(Path(fixture['requestPath']).read_bytes())
    version2 = request['schemaVersion'] != 'composite-engineering-request.v1'
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-worker.exe'
    schema_path = ROOT / 'contracts/composite-engineering-workflow.v1.schema.json'
    schema = json.loads(schema_path.read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    digest = lambda data: hashlib.sha256(data).hexdigest()
    encode = lambda value: (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode()
    report = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[])
    client = None

    def check(ok, label):
        assert ok, label
        report['checks'].append(label)

    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind, 'oneOf': [{}]}).validate(value)

    def start():
        nonlocal client
        client = Client(args.host.resolve(), 90, ['--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'composite-offline', 'version': '1'}})
        client.notify('notifications/initialized')

    def stop():
        nonlocal client
        closed = client.close(); client = None
        report['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native workers')

    def call(name, arguments=None):
        tool = 'composite_engineering_' + name
        contract = schema['x-tool-contracts'][tool]
        validate(arguments or {}, tool + 'Input')
        value = client.tool(tool, arguments or {})
        validate(value, contract['output']['$ref'].rsplit('/', 1)[1])
        return value

    def upload(data, name='supplied.json'):
        status = client.tool('artifact_upload_begin', {'fileName': name, 'expectedBytes': len(data), 'sha256': digest(data)})
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', {'uploadId': status['uploadId'], 'offset': offset, 'data': base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()})
        return client.tool('artifact_upload_commit', {'uploadId': status['uploadId']})

    def download(reference, name):
        raw = bytearray()
        while True:
            part = call('artifact_read', dict(recordRef=reference, name=name, offset=len(raw), count=65536))
            data = base64.b64decode(part['data'], validate=True)
            assert part['offset'] == len(raw) and part['count'] == len(data)
            raw.extend(data)
            assert part['nextOffset'] == len(raw)
            if part['complete']:
                check(part['bytes'] == len(raw) and part['sha256'] == digest(raw), 'Exact downloaded length and digest: ' + name)
                return bytes(raw)
            assert data, 'Nonterminal chunk must make progress'

    def refuse(name, arguments, code):
        response = client.request('tools/call', {'name': 'composite_engineering_' + name, 'arguments': arguments})
        value = response['structuredContent']; validate(value, 'error')
        check(response.get('isError') and value['error']['code'] == code, 'Structured offline refusal: ' + name + '/' + code)

    try:
        start()
        tool_rows = {t['name']: t for t in client.request('tools/list')['tools']}
        check(set(schema['x-tool-contracts']) <= tool_rows.keys(), 'Five composite routes registered')
        for tool in schema['x-tool-contracts']:
            check(tool_rows[tool]['annotations']['readOnlyHint'] == (tool != 'composite_engineering_review'), 'Tool mutation hint: ' + tool)
        for review_id in {request[p]['reviewId'] for p in ('before', 'after')}:
            incoming = upload((source / 'project-reviews' / (review_id + '.json')).read_bytes())
            check(client.tool('project_snapshot_import', {'uploadRef': incoming['artifactRef']})['reviewId'] == review_id, 'Exact saved review imported through MCP')
            client.tool('artifact_upload_release', {'uploadId': incoming['uploadId']})
        references = {}
        for phase in ('before', 'after'):
            old = request[phase]['automationMlRef']
            incoming = upload((source / 'automationml-workflow' / old.split(':')[1] / 'original.aml').read_bytes(), 'source.aml')
            references[old] = client.tool('automationml_inspect', {'xmlUploadRef': incoming['artifactRef']})['recordRef']
            request[phase]['automationMlRef'] = references[old]
            client.tool('artifact_upload_release', {'uploadId': incoming['uploadId']})
        components = {}
        for phase in ('before', 'after'):
            for field, domain, tool in [('connections', 'connection-reviews', 'connection_review'), ('drives', 'drive-profile-reviews', 'drive_profile_review'), ('technology', 'technology-profile-reviews', 'technology_profile_review')]:
                for record in request[phase][field]:
                    if record in components: continue
                    envelope = json.loads((source / domain / (record + '.json')).read_bytes())
                    raw = (source / envelope['input']['path']).read_bytes()
                    incoming = upload(raw)
                    result = client.tool(tool, {'reviewId': envelope['reviewId'], 'uploadRef': incoming['artifactRef']})
                    check(result['recordId'] == record, 'Exact component input reproduces retained record through MCP: ' + field)
                    components[record] = envelope
                    client.tool('artifact_upload_release', {'uploadId': incoming['uploadId']})
        for mapping in request['mappings']:
            for endpoint in ('source', 'target'):
                selector = mapping[endpoint]
                if selector['kind'].startswith('aml'): selector['recordRef'] = references[selector['recordRef']]
        request['mappings'][0]['reason'] = 'Explicit supplied association: Überprüfung — 保留'
        original_request = encode(request)
        incoming = upload(original_request)
        summary = call('review', {'uploadRef': incoming['artifactRef']}); reference = summary['recordRef']
        client.tool('artifact_upload_release', {'uploadId': incoming['uploadId']})
        stop(); start()
        check(call('reopen', {'recordRef': reference}) == summary, 'Restart and released uploads preserve exact summary')
        check(download(reference, 'request.json') == original_request, 'Request whitespace, Unicode and final newline preserved')
        result_bytes = download(reference, 'report.json'); result = json.loads(result_bytes)
        receipt_bytes = download(reference, 'receipt.json'); validate(json.loads(receipt_bytes), 'receipt')
        check(digest(receipt_bytes) == reference.split(':')[2], 'Exact receipt digest binds handle')
        (artifacts / 'request.json').write_bytes(original_request); (artifacts / 'report.json').write_bytes(result_bytes)
        verify_fixture(artifacts)
        check(True, 'Independent original AML, saved review and all component selector oracle passed')
        if request['schemaVersion'] == 'composite-engineering-request.v3':
            for mutation in ('oldVersion', 'wrongPhase', 'outsideContext'):
                invalid = copy.deepcopy(request)
                if mutation == 'oldVersion': invalid['schemaVersion'] = 'composite-engineering-request.v2'
                elif mutation == 'wrongPhase': invalid['mappings'][1]['source']['phase'] = 'after'
                else: invalid['before']['drives'] = []
                incoming = upload(encode(invalid))
                refuse('review', dict(uploadRef=incoming['artifactRef']), 'InvalidCompositeEngineeringReview')
                client.tool('artifact_upload_release', dict(uploadId=incoming['uploadId']))
        sections = {k: v for k, v in result.items() if isinstance(v, list)}
        sections.update({p + 'Components': result['contexts'][p]['components'] for p in ('before', 'after')})
        sections.update(amlChanges=result['amlComparison']['changes'], beforeAmlImpacts=result['amlComparison']['beforeImpacts'], afterAmlImpacts=result['amlComparison']['afterImpacts'])
        if version2:
            sections['consumerCoverage'] = [dict(phase=c['phase'], coverage=c['coverage']) for c in result['consumerContexts']]
            for context in result['consumerContexts']:
                for name, field in [('Roots', 'rootBindings'), ('Associations', 'associations'), ('SavedLinks', 'savedLinks'), ('Candidates', 'candidates')]:
                    sections[context['phase'] + 'Consumer' + name] = context[field]
        for section, expected in sections.items():
            rows, offset = [], 0
            while True:
                page = call('page', dict(recordRef=reference, section=section, offset=offset, count=1))
                assert page['reportSha256'] == digest(result_bytes) and page['total'] == len(expected)
                for row in page['rows']:
                    assert row['index'] == len(rows); rows.append(row['value'])
                if page['nextOffset'] is None: break
                assert page['nextOffset'] > offset; offset = page['nextOffset']
            check(rows == expected, 'Paged array equals exact downloaded report: ' + section)
        refuse('page', dict(recordRef=reference, count=201), 'CompositePageLimit')
        refuse('artifact_read', dict(recordRef=reference, name='../report.json'), 'CompositeArtifactMissing')
        refuse('reopen', dict(recordRef=reference.upper()), 'CompositeInvalidReference')
        if not version2: refuse('page', dict(recordRef=reference, section='consumerCoverage'), 'CompositeInvalidSection')
        store = artifacts / 'composite-engineering-reviews'; folder = store / reference.split(':')[1]
        aml = artifacts / 'automationml-workflow' / request['before']['automationMlRef'].split(':')[1] / 'original.aml'
        component = next(iter(components.values()))
        damaged = [(aml, 'AutomationMlIntegrityFailure'), (artifacts / component['input']['path'], 'ArtifactChanged'), (folder / 'report.json', 'CompositeIntegrityFailure')]
        if request['schemaVersion'] == 'composite-engineering-request.v3':
            damaged += [(artifacts / components[request['before'][kind][0]]['input']['path'], 'ArtifactChanged') for kind in ('drives', 'technology')]
        for path, code in damaged:
            before = path.read_bytes()
            try:
                path.write_bytes(before + b' ')
                for name, args_ in [('reopen', {}), ('page', {}), ('artifact_read', {'name': 'report.json'})]:
                    refuse(name, dict(recordRef=reference, **args_), code)
                check(any(r['state'] == 'verificationFailed' for r in call('list')['rows']), 'Discovery shows failed retained dependency: ' + path.name)
            finally: path.write_bytes(before)
        # Withhold a receipt to exercise public discovery; managed tests separately kill actual publishers.
        receipt_path = folder / 'receipt.json'; receipt_path.unlink()
        try: check(call('list')['rows'][0]['state'] == 'incomplete', 'Synthetic missing receipt remains incomplete through MCP')
        finally: receipt_path.write_bytes(receipt_bytes)
        check(call('reopen', {'recordRef': reference}) == summary, 'Restoring exact retained bytes reopens without replay')
        # A large original evidence row must remain downloadable when paging omits it.
        field = 'connections' if version2 else 'drives'
        component_id = request['before'][field][0]; envelope = components[component_id]
        component_request = json.loads((source / envelope['input']['path']).read_bytes())
        marker = 'opaque-fixture-' * 10000
        for phase in ('before', 'after'):
            target = component_request[phase]['connections'][0]['properties'] if version2 else component_request[phase]['parameters'][0]['metadata']
            target['largeFixture'] = marker
        uploaded = upload(encode(component_request))
        large_id = client.tool('connection_review' if version2 else 'drive_profile_review', dict(reviewId=envelope['reviewId'], uploadRef=uploaded['artifactRef']))['recordId']
        large = copy.deepcopy(request)
        for phase in ('before', 'after'): large[phase][field] = [large_id]
        for mapping in large['mappings']:
            for endpoint in ('source', 'target'):
                if mapping[endpoint].get('recordRef') == component_id: mapping[endpoint]['recordRef'] = large_id
        for correspondence in large['correspondences']:
            for phase in ('before', 'after'):
                if correspondence[phase].get('recordRef') == component_id: correspondence[phase]['recordRef'] = large_id
        large_input = upload(encode(large)); large_ref = call('review', dict(uploadRef=large_input['artifactRef']))['recordRef']
        page = call('page', dict(recordRef=large_ref, section='beforeConsumerAssociations' if version2 else 'selections', count=200))
        check(len(encode(page)) <= 128 << 10 and any(r.get('omitted') for r in page['rows']), 'Oversized source evidence has explicit bounded page omission')
        large_report = json.loads(download(large_ref, 'report.json'))
        rows = large_report['consumerContexts'][0]['associations'] if version2 else large_report['selections']
        check(any((r.get('evidence') or {}).get('connection' if version2 else 'parameter', {}).get('properties' if version2 else 'metadata', {}).get('largeFixture') == marker for r in rows), 'Exact download retains oversized original metadata')
        stop()
        report['completed'] = True
    finally:
        if client is not None: stop()
        report['count'] = len(report['checks'])
        report['harnessSha256'] = digest(Path(__file__).read_bytes())
        report['contractSha256'] = digest(schema_path.read_bytes())
        report['managedBinaryHashes'] = {name: digest((args.host.parent / name).read_bytes()) for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS composite MCP: {report['count']} checks; two clean Host lifetimes; no native execution")


if __name__ == '__main__':
    main()
