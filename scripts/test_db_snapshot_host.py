"""Actual offline DB snapshot XML review, original encodings, schemas, retention and restart checks."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError
from smoke_mcp import Client, ROOT


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
    review_bytes = (Path(fixture['artifactRoot']) / 'project-reviews' / (fixture['reviewId'] + '.json')).read_bytes()
    before, after = (base64.b64decode(fixture[k]) for k in ('before', 'after'))
    schema = json.loads((ROOT / 'contracts/db-snapshot-review.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-worker.exe'
    def close(client):
        closed = client.close()
        report['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native workers')
    def start():
        client = Client(args.host.resolve(), 30, ['--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        try:
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'connection-review-offline', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def upload(client, data):
        status = client.tool('artifact_upload_begin', {'fileName': 'supplied.json', 'expectedBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', {'uploadId': status['uploadId'], 'offset': offset, 'data': base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()})
        return client.tool('artifact_upload_commit', {'uploadId': status['uploadId']})
    def read(client, reference):
        chunks, offset = [], 0
        while True:
            part = client.tool('artifact_read', {'path': reference['path'], 'offset': offset, 'count': 65536})
            raw = base64.b64decode(part['data'], validate=True)
            assert part['sha256'] == reference['sha256'] and part['offset'] == offset and len(raw) == part['count']
            chunks.append(raw)
            offset = part['nextOffset']
            if part['complete']: break
        raw = b''.join(chunks)
        check(hashlib.sha256(raw).hexdigest() == reference['sha256'], 'Downloaded artifact exact digest')
        return raw
    def refuse(client, tool, arguments, code):
        response = client.request('tools/call', {'name': tool, 'arguments': arguments})
        error = response['structuredContent']['error']
        check(response.get('isError') and error['code'] == code and error['nativeAccessAttempted'] is False, 'Structured offline refusal ' + code)
    try:
        client = start()
        try:
            check({'db_snapshot_review', 'db_snapshot_review_page'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both tools registered')
            saved = upload(client, review_bytes)
            source = client.tool('project_snapshot_import', {'uploadRef': saved['artifactRef']})
            check(source['reviewId'] == fixture['reviewId'], 'Exact saved review imported')
            incoming, comparison = upload(client, before), upload(client, after)
            arguments = {'reviewId': source['reviewId'], 'objectPath': fixture['objectPath'], 'uploadRef': incoming['artifactRef'], 'comparisonUploadRef': comparison['artifactRef']}
            receipt = client.tool('db_snapshot_review', arguments)
            validate(receipt, 'receipt')
            check(receipt == client.tool('db_snapshot_review', arguments), 'Idempotent exact receipt')
            check(read(client, receipt['input']) == before and read(client, receipt['comparisonInput']) == after, 'Original UTF-8 and UTF-16 bytes preserved')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check(result['summary']['changedPathCount'] == 2, 'Text spelling and exact large integer differences retained')
            check(result['before']['values'][1]['text'] == ' 日本語 😀 ', 'Opaque Unicode and whitespace preserved')
            for section, expected in (('beforeValues', result['before']['values']), ('afterValues', result['after']['values']), ('changes', result['changes'])):
                rows, offset = [], 0
                while True:
                    page = client.tool('db_snapshot_review_page', {'recordId': receipt['recordId'], 'section': section, 'offset': offset, 'count': 1})
                    validate(page, 'page')
                    rows.extend(row['value'] for row in page['rows'])
                    if page['nextOffset'] is None: break
                    offset = page['nextOffset']
                check(rows == expected, 'Paged ' + section + ' matches immutable artifact')
            single = client.tool('db_snapshot_review', {k: v for k, v in arguments.items() if k != 'comparisonUploadRef'})
            validate(single, 'receipt')
            check(single['comparisonInput'] is None and not single['summary']['comparisonRequested'], 'Single inspection has no fabricated comparison')
            for flag in ('nativeQualified', 'liveFreshnessVerified', 'programIdentityVerified', 'structureCompatibilityVerified'):
                adversary = copy.deepcopy(record)
                adversary['report'][flag] = True
                try: validate(adversary, 'record')
                except ValidationError: check(True, 'Schema refuses ' + flag)
                else: raise AssertionError('Unsupported claim accepted')
            refuse(client, 'db_snapshot_review_page', {'recordId': receipt['recordId'], 'section': 'changes', 'offset': 100}, 'InvalidRequest')
            refuse(client, 'db_snapshot_review', {**arguments, 'objectPath': fixture['objectPath'].replace('CPU', 'OtherCPU')}, 'InvalidDbSnapshot')
            invalid = upload(client, before.replace(b'Path="Large"', b'Path="Flag"'))
            refuse(client, 'db_snapshot_review', {**arguments, 'uploadRef': invalid['artifactRef']}, 'InvalidDbSnapshot')
            malformed = upload(client, b'<Document>')
            refuse(client, 'db_snapshot_review', {**arguments, 'uploadRef': malformed['artifactRef']}, 'DbSnapshotReviewFailed')
            for item in (incoming, comparison, saved, invalid, malformed):
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
        finally: close(client)
        client = start()
        try:
            check(client.tool('db_snapshot_review_page', {'recordId': receipt['recordId']}) == receipt, 'Restart and upload release preserve receipt')
            check(read(client, receipt['input']) == before, 'Original bytes survive restart')
            for key in ('input', 'comparisonInput', 'reportArtifact'):
                path = (artifacts / receipt[key]['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'db_snapshot_review_page', {'recordId': receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            check(client.tool('db_snapshot_review_page', {'recordId': receipt['recordId']}) == receipt, 'Restored exact bytes reopen without mutation replay')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS DB snapshot MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__':
    main()
