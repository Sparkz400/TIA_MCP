"""Actual supplied web application review, schemas, exact identities, retention and restart."""
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
    schema = json.loads((ROOT / 'contracts/web-application-review.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    validate(packet, 'request')
    check(True, 'Synthetic input satisfies strict request schema')
    for change in ('extra', 'purpose', 'secret', 'boolean'):
        mutated = copy.deepcopy(packet)
        if change == 'extra': mutated['nativeQualified'] = True
        elif change == 'purpose': mutated['before']['archive']['purpose'] = 'nativeApproved'
        elif change == 'secret': mutated['password'] = 'REDACTION-TEST-SENTINEL'
        else: mutated['before']['configuration']['webEnabled'] = 'true'
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
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'web-application-review-offline', 'version': '1'}})
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
            check({'web_application_review', 'web_application_review_page'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both tools registered')
            saved = upload(client, review_bytes)
            source = client.tool('project_snapshot_import', {'uploadRef': saved['artifactRef']})
            check(source['reviewId'] == fixture['reviewId'], 'Saved review imports exact original bytes')
            originals = {}
            for phase in ('before', 'after'):
                raw = base64.b64decode(fixture[phase + 'Base64'])
                originals[phase] = upload(client, raw)
                packet[phase]['archive']['uploadRef'] = originals[phase]['artifactRef']
                check(hashlib.sha256(raw).hexdigest() == packet[phase]['archive']['sha256'], 'Independent original ZIP digest ' + phase)
            arguments = {'request': packet}
            receipt = client.tool('web_application_review', arguments)
            validate(receipt, 'receipt')
            check(receipt == client.tool('web_application_review', arguments), 'Duplicate review is idempotent')
            check(json.loads(read(client, receipt['input'])) == packet, 'Complete direct public request retained')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check(result['summary']['changedFiles'] == 3 and result['summary']['changedApplications'] == 1 and result['summary']['configurationChanges'] == 1, 'Independent file application and configuration delta oracle')
            statuses = {r['relativePath']: r['status'] for r in result['fileChanges']}
            check(statuses == {'app.js':'unchanged', 'download.zip':'unchanged', 'index.html':'changed', 'old.bin':'removed', 'über.bin':'added'}, 'Exact selected-root file changes')
            check(result['observations'][0]['content']['excludedTopLevelFiles'] == 1 and result['observations'][0]['content']['nestedExtractedFiles'] == 1, 'Other root and nested contents remain separate')
            for phase in ('before', 'after'):
                check(read(client, receipt['archives'][phase]) == base64.b64decode(fixture[phase + 'Base64']), 'Exact retained ZIP ' + phase)
            for section in ('observations', 'configurationChanges', 'applicationChanges', 'fileChanges'):
                rows, offset = [], 0
                while True:
                    page = client.tool('web_application_review_page', {'recordId':receipt['recordId'], 'section':section, 'offset':offset, 'count':1})
                    validate(page, 'page')
                    rows.extend(r['value'] for r in page['rows'])
                    if page['nextOffset'] is None: break
                    offset = page['nextOffset']
                check(rows == result[section], 'Paged ' + section + ' matches immutable report')
            for flag in ('nativeQualified', 'associationVerified', 'siemensCompatibilityVerified', 'generatedBlocksVerified', 'deploymentVerified', 'endpointVerified'):
                check(result[flag] is False, flag + ' remains false')
                bad = copy.deepcopy(record)
                bad['report'][flag] = True
                try: validate(bad, 'record')
                except ValidationError: check(True, 'Output schema refuses ' + flag)
                else: raise AssertionError('Native claim accepted')
            refuse(client, 'web_application_review_page', {'recordId':receipt['recordId'], 'section':'fileChanges', 'offset':100}, 'InvalidRequest')
            retained = artifacts / 'web-application-reviews'
            def files(): return {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in retained.iterdir() if p.is_file()}
            for mutation in ('owner', 'selected', 'secret', 'purpose', 'default', 'fingerprint'):
                bad = copy.deepcopy(packet)
                if mutation == 'owner': bad['owner']['nodeId'] = 'another'
                elif mutation == 'selected': bad['selectedApplicationId'] = 'Monitoring'
                elif mutation == 'secret': bad['password'] = 'REDACTION-TEST-SENTINEL'
                elif mutation == 'purpose': bad['after']['archive']['purpose'] = 'deployed'
                elif mutation == 'default': bad['after']['archive']['defaultPage'] = 'Index.html'
                else: bad['after']['archive']['sha256'] = 'a'*64
                prior = files()
                refuse(client, 'web_application_review', {'request':bad}, 'ArtifactChanged' if mutation == 'fingerprint' else 'InvalidWebApplicationReview')
                check(files() == prior, mutation + ' refusal leaves immutable reviews unchanged')
            import io, zipfile
            for label, names in [('traversal', ['../escape']), ('case', ['site/index.html', 'Site/extra']), ('collision', ['site', 'site/index.html'])]:
                buffer = io.BytesIO()
                with zipfile.ZipFile(buffer, 'w') as z:
                    for name in names: z.writestr(name, b'public fixture')
                raw = buffer.getvalue()
                staged = upload(client, raw)
                bad = copy.deepcopy(packet)
                bad['after']['archive'].update(uploadRef=staged['artifactRef'], sha256=hashlib.sha256(raw).hexdigest())
                prior = files()
                refuse(client, 'web_application_review', {'request':bad}, 'PackageInvalid')
                check(files() == prior, label + ' refused without a retained review')
                client.tool('artifact_upload_release', {'uploadId':staged['uploadId']})
            unknown = copy.deepcopy(packet)
            unknown['after']['archive'].update(uploadRef=None, sha256=None, rootRelative=None, defaultPage=None, unavailableReason='not captured')
            missing = client.tool('web_application_review', {'request':unknown})
            validate(missing, 'receipt')
            check(missing['summary']['filesComparable'] is False and missing['summary']['unavailableArchives'] == 1, 'Unavailable ZIP cannot prove file removal')
            client.tool('artifact_upload_release', {'uploadId':saved['uploadId']})
            for staged in originals.values(): client.tool('artifact_upload_release', {'uploadId':staged['uploadId']})
        finally: close(client)
        client = start()
        try:
            check(client.tool('web_application_review_page', {'recordId':receipt['recordId']}) == receipt, 'Restart and released input uploads preserve exact receipt')
            for reference in (receipt['input'], receipt['reportArtifact'], receipt['archives']['before']):
                path = (artifacts / reference['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'web_application_review_page', {'recordId':receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            check(client.tool('web_application_review_page', {'recordId':receipt['recordId']}) == receipt, 'Restored originals reopen without mutation replay')
            check(not list((artifacts/'web-application-analysis').glob('*/')), 'Transient extraction directories cleaned')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name:hashlib.sha256((args.host.parent/name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll','TiaMcp.Core.dll')}
        (output/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8',newline='\n')
    print(f"PASS web application MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__':
    main()
