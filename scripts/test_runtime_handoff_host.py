"""Actual C03 MCP observation intake, retained publication, original bytes and restart."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path

from smoke_mcp import Client
from test_runtime_handoff_contracts import SCHEMA, validate, verify


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--fixture', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    source = Path(fixture['artifactRoot'])
    request_bytes = Path(fixture['requestPath']).read_bytes()
    request = json.loads(request_bytes)
    artifacts = output / 'store'
    schema = json.loads(SCHEMA.read_text(encoding='utf-8'))
    result = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[])
    digest = lambda raw: hashlib.sha256(raw).hexdigest()
    encode = lambda value: (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode()
    client = None
    def check(ok, label):
        assert ok, label
        result['checks'].append(label)
    def start():
        nonlocal client
        client = Client(args.host.resolve(), 60, ['--artifacts', str(artifacts), '--worker', str(output / 'absent.exe'), '--sim-worker', str(output / 'absent.exe')])
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'handoff-offline', 'version': '1'}})
        client.notify('notifications/initialized')
    def stop():
        nonlocal client
        closed = client.close()
        client = None
        result['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native worker')
    def call(action, arguments=None):
        tool = 'runtime_handoff_' + action
        validate(arguments or {}, tool + 'Input')
        value = client.tool(tool, arguments or {})
        validate(value, schema['x-tool-contracts'][tool]['output']['$ref'].rsplit('/', 1)[1])
        return value
    def upload(raw):
        opened = client.tool('artifact_upload_begin', {'fileName': 'public.json', 'expectedBytes': len(raw), 'sha256': digest(raw)})
        for offset in range(0, len(raw), opened['maxChunkBytes']):
            client.tool('artifact_upload_chunk', {'uploadId': opened['uploadId'], 'offset': offset, 'data': base64.b64encode(raw[offset:offset + opened['maxChunkBytes']]).decode()})
        return client.tool('artifact_upload_commit', {'uploadId': opened['uploadId']})
    def release(incoming):
        client.tool('artifact_upload_release', {'uploadId': incoming['uploadId']})
    def download(record, name):
        raw = bytearray()
        while True:
            part = call('artifact_read', {'recordRef': record, 'name': name, 'offset': len(raw), 'count': 8192})
            data = base64.b64decode(part['data'], validate=True)
            assert part['offset'] == len(raw) and part['count'] == len(data)
            raw.extend(data)
            assert part['nextOffset'] == len(raw)
            if part['complete']:
                check(part['bytes'] == len(raw) and part['sha256'] == digest(raw), 'Downloaded exact bytes and digest: ' + name)
                return bytes(raw)
            assert data
    def refuse(action, arguments, code):
        response = client.request('tools/call', {'name': 'runtime_handoff_' + action, 'arguments': arguments})
        check('SECRET-SENTINEL' not in json.dumps(response), 'Failure never echoes forbidden value')
        error = response['structuredContent']
        validate(error, 'handoffError')
        check(response.get('isError') and error['error']['code'] == code, 'Structured refusal: ' + code)
    try:
        start()
        tools = {t['name']: t for t in client.request('tools/list')['tools']}
        check(set(schema['x-tool-contracts']) <= tools.keys(), 'All six handoff tools registered')
        for name in schema['x-tool-contracts']:
            annotations = tools[name]['annotations']
            check(annotations['readOnlyHint'] == (name not in ('runtime_handoff_observation', 'runtime_handoff_review')) and
                  annotations['destructiveHint'] is False and annotations['openWorldHint'] is False, 'Correct offline tool annotations: ' + name)
        incoming = upload((source / 'project-reviews' / (request['reviewId'] + '.json')).read_bytes())
        saved = client.tool('project_snapshot_import', {'uploadRef': incoming['artifactRef']})
        check(saved['reviewId'] == request['reviewId'], 'Exact saved review imported through MCP')
        release(incoming)
        access = json.loads((source / 'access-reviews' / (request['accessRecordId'] + '.json')).read_bytes())
        incoming = upload((source / access['input']['path']).read_bytes())
        access_receipt = client.tool('access_review', {'reviewId': request['reviewId'], 'uploadRef': incoming['artifactRef']})
        check(access_receipt['recordId'] == request['accessRecordId'], 'Exact original access review reproduced through existing route')
        release(incoming)
        originals = {}
        for phase in ('baseline', 'current'):
            for role in ('target', 'program', 'session'):
                name = phase + '.' + role + '.json'
                raw = (source / request[phase][role]['path']).read_bytes()
                originals[name] = raw
                incoming = upload(raw)
                receipt = call('observation', {'role': role, 'uploadRef': incoming['artifactRef']})
                check(receipt['artifact'] == request[phase][role] and receipt['bytes'] == len(raw), 'Original canonical evidence reference: ' + name)
                check(call('observation', {'role': role, 'uploadRef': incoming['artifactRef']}) == receipt, 'Idempotent exact evidence intake: ' + name)
                refuse('observation', {'role': 'program' if role == 'target' else 'target', 'uploadRef': incoming['artifactRef']}, 'InvalidRuntimeHandoff')
                release(incoming)
        for defect in ('native-producer', 'secret', 'duplicate-json', 'oversized'):
            bad = json.loads(originals['baseline.target.json'])
            if defect == 'native-producer': bad['sourceKind'] = 'nativeQualified'
            if defect == 'secret': bad['password'] = 'SECRET-SENTINEL'
            raw = b'{"schemaVersion":1,"schemaVersion":2}' if defect == 'duplicate-json' else encode(bad)
            if defect == 'oversized': raw += b' ' * (256 * 1024)
            incoming = upload(raw)
            refuse('observation', {'role': 'target', 'uploadRef': incoming['artifactRef']}, 'HandoffCapacity' if defect == 'oversized' else 'InvalidRuntimeHandoff')
            release(incoming)
        incoming = upload(request_bytes)
        summary = call('review', {'uploadRef': incoming['artifactRef']})
        record = summary['recordRef']
        release(incoming)
        check(download(record, 'request.json') == request_bytes, 'Original caller request including whitespace remains unchanged')
        report_bytes = download(record, 'report.json')
        report = json.loads(report_bytes)
        verify(artifacts, request, report)
        check(True, 'Report independently checked against all original observations, access input and saved model')
        manifest_bytes = download(record, 'receipt.json')
        manifest = json.loads(manifest_bytes)
        validate(manifest, 'recordManifest')
        check(digest(manifest_bytes) == record.split(':')[2], 'Exact public handle binds receipt')
        for name, raw in originals.items():
            check(download(record, name) == raw, 'Original released-upload bytes remain downloadable: ' + name)
        for section in ('constraints', 'permissions', 'baselineObservations', 'currentObservations'):
            expected = report[section] if section in report else [{'role': role, 'observation': row} for role, row in report['observations']['baseline' if section == 'baselineObservations' else 'current'].items()]
            rows, offset = [], 0
            while True:
                page = call('page', {'recordRef': record, 'section': section, 'offset': offset, 'count': 2})
                rows.extend(r['value'] for r in page['rows'])
                if page['nextOffset'] is None:
                    break
                assert page['nextOffset'] > offset
                offset = page['nextOffset']
            check(rows == expected, 'Complete exact paged section: ' + section)
        refuse('page', {'recordRef': record, 'count': 201}, 'HandoffPageLimit')
        refuse('artifact_read', {'recordRef': record, 'name': '../intent.json'}, 'HandoffArtifactMissing')
        for label in ('secret', 'foreign-owner', 'unowned-reference', 'unrequested-native'):
            bad = copy.deepcopy(request)
            if label == 'secret': bad['password'] = 'SECRET-SENTINEL'
            elif label == 'foreign-owner': bad['owner']['nodeId'] = 'other'
            elif label == 'unowned-reference': bad['baseline']['target']['path'] = 'elsewhere.json'
            else: bad['nativeQualified'] = True
            incoming = upload(encode(bad))
            refuse('review', {'uploadRef': incoming['artifactRef']}, 'InvalidRuntimeHandoff')
            release(incoming)
        incoming = upload(b'{"schemaVersion":1,"schemaVersion":2}')
        refuse('review', {'uploadRef': incoming['artifactRef']}, 'InvalidRuntimeHandoff')
        release(incoming)
        changed_program = json.loads(originals['current.program.json'])
        changed_program['identity']['value'] = 'changed-program'
        incoming = upload(encode(changed_program))
        new_program = call('observation', {'role': 'program', 'uploadRef': incoming['artifactRef']})
        release(incoming)
        invalid_request = copy.deepcopy(request)
        invalid_request['current']['program'] = new_program['artifact']
        incoming = upload(encode(invalid_request))
        invalid_summary = call('review', {'uploadRef': incoming['artifactRef']})
        release(incoming)
        invalid_report = json.loads(download(invalid_summary['recordRef'], 'report.json'))
        verify(artifacts, invalid_request, invalid_report)
        check(invalid_summary['summary']['state'] == 'requiresFreshEvidence' and invalid_summary['operationAdmitted'] is False, 'Changed program retains invalidation without live authority')
        large = json.loads(originals['current.session.json'])
        large['permissions'] = [{'id': str(i) + 'x' * 500, 'scope': 'y' * 500, 'outcome': 'denied'} for i in range(150)]
        large_bytes = encode(large)
        incoming = upload(large_bytes)
        large_receipt = call('observation', {'role': 'session', 'uploadRef': incoming['artifactRef']})
        release(incoming)
        wide_request = copy.deepcopy(request)
        for phase in ('baseline', 'current'): wide_request[phase]['session'] = large_receipt['artifact']
        incoming = upload(encode(wide_request))
        wide = call('review', {'uploadRef': incoming['artifactRef']})
        release(incoming)
        wide_page = call('page', {'recordRef': wide['recordRef'], 'section': 'currentObservations'})
        check(any(r.get('omitted') is True for r in wide_page['rows']) and len(json.dumps(wide_page).encode()) <= 128 * 1024,
              'Oversized observation is explicit within bounded structured page')
        check(download(wide['recordRef'], 'current.session.json') == large_bytes, 'Oversized original remains completely downloadable through MCP')
        incoming = upload(request_bytes)
        call('review', {'uploadRef': incoming['artifactRef']})  # Deliberately discard the reference; process interruption is covered by the managed harness.
        release(incoming)
        listing = call('list')
        check(sum(r['state'] == 'complete' for r in listing['rows']) == 4, 'Unretained response reference discoverable without replay')
        stop()
        start()
        check(call('reopen', {'recordRef': record}) == summary, 'Fresh Host reopens exact record without acquisition')
        check(download(record, 'report.json') == report_bytes, 'Report bytes survive Host restart')
        paths = [request['baseline'][role]['path'] for role in ('target', 'program', 'session')]
        paths += ['access-reviews/' + request['accessRecordId'] + '.json', access['input']['path'], 'project-reviews/' + request['reviewId'] + '.json']
        for name in paths:
            path = artifacts / name
            original = path.read_bytes()
            try:
                path.write_bytes(original + b' ')
                refuse('reopen', {'recordRef': record}, 'ArtifactChanged')
                check(any(r['state'] == 'verificationFailed' for r in call('list')['rows']), 'Discovery exposes corrupt original dependency')
            finally:
                path.write_bytes(original)
        check(call('reopen', {'recordRef': record}) == summary, 'Restoring exact dependency bytes recovers review only')
        record_dir = artifacts / 'runtime-handoff/records' / record.split(':')[1]
        forged = copy.deepcopy(report)
        forged['operationAdmitted'] = True
        forged_bytes = encode(forged)
        forged_manifest = copy.deepcopy(manifest)
        forged_manifest['artifacts'][1].update(bytes=len(forged_bytes), sha256=digest(forged_bytes))
        # Use original canonical receipt formatting so failure must reach rebuilt-report comparison.
        forged_manifest_bytes = json.dumps(forged_manifest, separators=(',', ':'), ensure_ascii=True).encode()
        saved_files = {name: (record_dir / name).read_bytes() for name in ('intent.json', 'receipt.json', 'report.json')}
        try:
            (record_dir / 'report.json').write_bytes(forged_bytes)
            for name in ('intent.json', 'receipt.json'): (record_dir / name).write_bytes(forged_manifest_bytes)
            forged_ref = ':'.join(record.split(':')[:2]) + ':' + digest(forged_manifest_bytes)
            refuse('reopen', {'recordRef': forged_ref}, 'HandoffIntegrityFailure')
        finally:
            for name, raw in saved_files.items(): (record_dir / name).write_bytes(raw)
        check(call('reopen', {'recordRef': record}) == summary, 'Original record still revalidates after forgery test')
        result['recordRef'] = record
        result['completed'] = True
    finally:
        if client is not None:
            stop()
        result['count'] = len(result['checks'])
        result['binaryHashes'] = {name: digest((args.host.parent / name).read_bytes()) for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print('PASS handoff MCP:', result['count'], 'checks; two Host lifetimes, no native execution')


if __name__ == '__main__':
    main()
