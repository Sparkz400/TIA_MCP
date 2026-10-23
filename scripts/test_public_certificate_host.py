"""Actual supplied public certificate review, schemas, exact identities, retention and restart."""
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
    schema = json.loads((ROOT / 'contracts/public-certificate-review.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    validate(packet, 'request')
    check(True, 'Synthetic input satisfies strict request schema')
    for change in ('extra', 'format', 'usage', 'secret', 'time'):
        mutated = copy.deepcopy(packet)
        if change == 'extra': mutated['chainVerified'] = True
        elif change == 'format': mutated['before']['format'] = 'pfx'
        elif change == 'usage': mutated['before']['association']['usage'] = 'nativeApproved'
        elif change == 'secret': mutated['password'] = 'REDACTION-TEST-SENTINEL'
        else: mutated['evaluatedAtUtc'] = '2026-10-17'
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
            check({'public_certificate_review', 'public_certificate_review_page'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both tools registered')
            saved = upload(client, review_bytes)
            source = client.tool('project_snapshot_import', {'uploadRef': saved['artifactRef']})
            check(source['reviewId'] == fixture['reviewId'], 'Saved review imports exact original bytes')
            arguments = {'request': packet}
            receipt = client.tool('public_certificate_review', arguments)
            validate(receipt, 'receipt')
            check(receipt == client.tool('public_certificate_review', arguments), 'Duplicate review is idempotent')
            check(json.loads(read(client, receipt['input'])) == packet, 'Complete direct public request retained')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check(result['observations'][0]['certificate']['sha256'] == fixture['beforeSha256'] and result['observations'][1]['certificate']['sha256'] == fixture['afterSha256'], 'Independent exact DER fingerprints')
            check(result['summary']['samePublicCertificate'] is False and result['summary']['associationChanged'] and result['summary']['fingerprintMismatchCount'] == 1, 'Replacement and association findings remain separate')
            check(result['observations'][0]['certificate']['subject'] == 'CN=Before' and result['observations'][0]['certificate']['validity'] == 'withinInterval', 'Independent subject and explicit-time oracle')
            check(not result['signatureVerified'] and not result['chainVerified'] and not result['endpointAuthenticated'], 'Parsing does not establish trust')
            rows, offset = [], 0
            while True:
                page = client.tool('public_certificate_review_page', {'recordId': receipt['recordId'], 'section': 'observations', 'offset': offset, 'count': 1})
                validate(page, 'page')
                rows.extend(row['value'] for row in page['rows'])
                if page['nextOffset'] is None: break
                offset = page['nextOffset']
            check(rows == result['observations'], 'Paged observations match immutable report')
            adversary = copy.deepcopy(record)
            adversary['report']['nativeQualified'] = True
            try: validate(adversary, 'record')
            except ValidationError: check(True, 'Output schema cannot certify native qualification')
            else: raise AssertionError('Native claim accepted')
            refuse(client, 'public_certificate_review_page', {'recordId': receipt['recordId'], 'section': 'observations', 'offset': 100}, 'InvalidRequest')
            retained = artifacts / 'public-certificate-reviews'
            def files():
                return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in retained.iterdir() if p.is_file()}
            der = base64.b64decode(packet['before']['contentBase64'])
            pem = b'-----BEGIN CERTIFICATE-----\n' + base64.encodebytes(der) + b'-----END CERTIFICATE-----'
            for label, data, form in [('trailing DER', der + b'\0', 'der'), ('two DER', der + der, 'der'), ('two PEM', pem + b'\n' + pem, 'pem'), ('PEM key suffix', pem + b'\n-----BEGIN PRIVATE KEY-----\nREDACTION-TEST-SENTINEL\n-----END PRIVATE KEY-----', 'pem')]:
                bad = copy.deepcopy(packet)
                bad['after']['contentBase64'] = base64.b64encode(data).decode()
                bad['after']['format'] = form
                prior = files()
                refuse(client, 'public_certificate_review', {'request': bad}, 'InvalidPublicCertificateReview')
                check(files() == prior, label + ' refused before operation-owned retention')
            for field in ('extraEnvelopeBase64', 'extraTbsBase64', 'duplicateExtensionBase64', 'malformedIssuerBase64'):
                bad = copy.deepcopy(packet)
                bad['after']['contentBase64'] = fixture[field]
                bad['after']['format'] = 'der'
                prior = files()
                refuse(client, 'public_certificate_review', {'request': bad}, 'InvalidPublicCertificateReview')
                check(files() == prior, field + ' refused without retaining malformed public input')
            for mutation in ('owner', 'domain', 'secret', 'clock'):
                bad = copy.deepcopy(packet)
                if mutation == 'owner': bad['owner']['nodeId'] = 'another'
                elif mutation == 'domain': bad['after']['association']['usage'] = 'universalTrust'
                elif mutation == 'secret': bad['password'] = 'REDACTION-TEST-SENTINEL'
                else: bad['evaluatedAtUtc'] = '2026-10-17T00:00:00'
                prior = files()
                refuse(client, 'public_certificate_review', {'request': bad}, 'InvalidPublicCertificateReview')
                check(files() == prior, mutation + ' refusal does not publish artifacts')
            equivalent = copy.deepcopy(packet)
            equivalent['after'] = copy.deepcopy(equivalent['before'])
            equivalent['after']['format'] = 'pem'
            equivalent['after']['contentBase64'] = base64.b64encode(pem).decode()
            same = client.tool('public_certificate_review', {'request': equivalent})
            validate(same, 'receipt')
            check(same['summary']['samePublicCertificate'] is True, 'DER and PEM preserve public identity')
            unknown = copy.deepcopy(packet)
            unknown['after']['contentBase64'] = None
            unknown['after']['format'] = None
            unknown['after']['unavailableReason'] = 'protected association'
            missing = client.tool('public_certificate_review', {'request': unknown})
            validate(missing, 'receipt')
            check(missing['summary']['samePublicCertificate'] is None and missing['summary']['unavailableCount'] == 1, 'Unavailable bytes remain unknown')
            client.tool('artifact_upload_release', {'uploadId': saved['uploadId']})
        finally: close(client)
        client = start()
        try:
            check(client.tool('public_certificate_review_page', {'recordId': receipt['recordId']}) == receipt, 'Restart and source upload release preserve receipt')
            check(json.loads(read(client, receipt['input'])) == packet, 'Original public content survives restart')
            for key in ('input', 'reportArtifact'):
                path = (artifacts / receipt[key]['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'public_certificate_review_page', {'recordId': receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            check(client.tool('public_certificate_review_page', {'recordId': receipt['recordId']}) == receipt, 'Restored exact bytes reopen without mutation replay')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS public certificate MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__':
    main()
