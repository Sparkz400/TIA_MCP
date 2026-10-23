"""Actual supplied public access review, schemas, exact identities, retention and restart."""
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
    packet = fixture['packet']
    review_bytes = (Path(fixture['artifactRoot']) / 'project-reviews' / (fixture['reviewId'] + '.json')).read_bytes()
    packet_bytes = (json.dumps(packet, ensure_ascii=False) + '\n').encode()
    schema = json.loads((ROOT / 'contracts/access-review.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    validate(packet, 'request')
    check(True, 'Synthetic input satisfies strict request schema')
    for change in ('extra', 'coverage', 'domain', 'secret', 'state'):
        mutated = copy.deepcopy(packet)
        if change == 'extra': mutated['nativeQualified'] = True
        elif change == 'coverage': mutated['before']['entitiesComplete'] = 'true'
        elif change == 'domain': mutated['identity']['domain'] = 'everything'
        elif change == 'secret': mutated['before']['entities'][0]['password'] = 'REDACTION-TEST-SENTINEL'
        else: mutated['before']['entities'][0]['enabled'] = 'true'
        try: validate(mutated, 'request')
        except ValidationError: check(True, 'Schema refuses ' + change)
        else: raise AssertionError('Schema accepted ' + change)
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
        check('REDACTION-TEST-SENTINEL' not in json.dumps(response), 'Refusal never echoes supplied forbidden value')
        error = response['structuredContent']['error']
        check(response.get('isError') and error['code'] == code and error['nativeAccessAttempted'] is False, 'Structured offline refusal ' + code)
    try:
        client = start()
        try:
            check({'access_review', 'access_review_page'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both tools registered')
            saved = upload(client, review_bytes)
            source = client.tool('project_snapshot_import', {'uploadRef': saved['artifactRef']})
            check(source['reviewId'] == fixture['reviewId'], 'Saved review imports exact original bytes')
            incoming = upload(client, packet_bytes)
            arguments = {'reviewId': source['reviewId'], 'uploadRef': incoming['artifactRef']}
            receipt = client.tool('access_review', arguments)
            validate(receipt, 'receipt')
            check(receipt == client.tool('access_review', arguments), 'Duplicate review is idempotent')
            check(read(client, receipt['input']) == packet_bytes, 'Original Unicode and final newline preserved')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check(result['summary']['changedCount'] == 1 and result['summary']['beforeUnexpectedCount'] == 1 and result['summary']['afterUnexpectedCount'] == 0, 'Monitoring proposal removes exact supplied extra grant')
            check(result['assessments'][0]['visitedIds'] == ['user:Überwachung', 'group:operators', 'role:monitor'], 'Exact supplied transitive membership')
            check(result['assessments'][0]['potentialRights'] == [{'id': 'ModifyTag', 'scope': 'cpu:A'}, {'id': 'ReadTag', 'scope': 'cpu:A'}], 'Unrelated role cannot expand selected grants')
            check(not result['effectiveRightsVerified'] and not result['minimumRightsVerified'] and not result['lockoutSafetyVerified'], 'No effective authorization or safe removal claim')
            for section in ('observations', 'assessments', 'changes'):
                rows, offset = [], 0
                while True:
                    page = client.tool('access_review_page', {'recordId': receipt['recordId'], 'section': section, 'offset': offset, 'count': 1})
                    validate(page, 'page')
                    rows.extend(row['value'] for row in page['rows'])
                    if page['nextOffset'] is None: break
                    offset = page['nextOffset']
                check(rows == result[section], 'Paged ' + section + ' matches immutable report')
            adversary = copy.deepcopy(record)
            adversary['report']['nativeQualified'] = True
            try: validate(adversary, 'record')
            except ValidationError: check(True, 'Output schema cannot certify native qualification')
            else: raise AssertionError('Native claim accepted')
            refuse(client, 'access_review_page', {'recordId': receipt['recordId'], 'section': 'changes', 'offset': 100}, 'InvalidRequest')
            bad = copy.deepcopy(packet)
            bad['reviewId'] = '0' * 64
            invalid = upload(client, json.dumps(bad).encode())
            refuse(client, 'access_review', {**arguments, 'uploadRef': invalid['artifactRef']}, 'InvalidAccessReview')
            duplicate = upload(client, b'{"x":1,"x":2}')
            refuse(client, 'access_review', {**arguments, 'uploadRef': duplicate['artifactRef']}, 'InvalidAccessReview')
            mapping = copy.deepcopy(packet)
            mapping['after']['identity']['domain'] = 'legacyWeb'
            bad_mapping = upload(client, json.dumps(mapping).encode())
            refuse(client, 'access_review', {**arguments, 'uploadRef': bad_mapping['artifactRef']}, 'InvalidAccessReview')
            proposal = copy.deepcopy(packet)
            proposal['before']['entities'][0]['password'] = 'REDACTION-TEST-SENTINEL'
            bad_proposal = upload(client, json.dumps(proposal).encode())
            refuse(client, 'access_review', {**arguments, 'uploadRef': bad_proposal['artifactRef']}, 'InvalidAccessReview')
            observation = copy.deepcopy(packet)
            observation['afterKind'] = 'observation'
            observed = upload(client, json.dumps(observation).encode())
            observed_receipt = client.tool('access_review', {**arguments, 'uploadRef': observed['artifactRef']})
            validate(observed_receipt, 'receipt')
            observed_record = json.loads(read(client, observed_receipt['reportArtifact']))
            validate(observed_record, 'record')
            check(observed_record['report']['afterKind'] == 'observation', 'Observations stay distinct from proposals')
            for item in (incoming, saved, invalid, duplicate, bad_mapping, bad_proposal, observed):
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
        finally: close(client)
        client = start()
        try:
            check(client.tool('access_review_page', {'recordId': receipt['recordId']}) == receipt, 'Restart and upload release preserve receipt')
            check(read(client, receipt['input']) == packet_bytes, 'Original bytes survive restart')
            for key in ('input', 'reportArtifact'):
                path = (artifacts / receipt[key]['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'access_review_page', {'recordId': receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            check(client.tool('access_review_page', {'recordId': receipt['recordId']}) == receipt, 'Restored exact bytes reopen without mutation replay')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS public access MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__':
    main()
