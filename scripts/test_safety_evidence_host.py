"""Actual offline safety evidence comparison, exact signatures, source retention and restart checks."""
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
    if output.exists(): raise ValueError('Fresh evidence directory required')
    output.mkdir(parents=True)
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    review_bytes = (Path(fixture['artifactRoot']) / 'project-reviews' / (fixture['reviewId'] + '.json')).read_bytes()
    before, after = (base64.b64decode(fixture[k]) for k in ('before', 'after'))
    schema = json.loads((ROOT / 'contracts/safety-evidence-review.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        if not value: raise AssertionError(label)
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
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'safety-evidence-offline', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def upload(client, data, file_name="supplied.json"):
        status = client.tool('artifact_upload_begin', {'fileName': file_name, 'expectedBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', {'uploadId': status['uploadId'], 'offset': offset, 'data': base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()})
        return client.tool('artifact_upload_commit', {'uploadId': status['uploadId']})
    def read(client, reference):
        chunks, offset = [], 0
        while True:
            part = client.tool('artifact_read', {'path': reference['path'], 'offset': offset, 'count': 65536})
            raw = base64.b64decode(part['data'], validate=True)
            check(part['sha256'] == reference['sha256'] and part['offset'] == offset and len(raw) == part['count'], 'Exact artifact chunk identity and length')
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
            check({'safety_evidence_review', 'safety_evidence_report'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both tools registered')
            saved = upload(client, review_bytes)
            source = client.tool('project_snapshot_import', {'uploadRef': saved['artifactRef']})
            check(source['reviewId'] == fixture['reviewId'], 'Exact saved review imported')
            incoming, comparison = upload(client, before), upload(client, after)
            opaque_bytes = bytes([0, 255, 1, 254, 2])
            supporting = upload(client, opaque_bytes, 'supplied-support.bin')
            arguments = {'reviewId': source['reviewId'], 'plcPath': fixture['plcPath'], 'uploadRef': incoming['artifactRef'], 'comparisonUploadRef': comparison['artifactRef'],
                         'sources': [{'uploadRef': supporting['artifactRef'], 'phase': 'context', 'purpose': 'Opaque synthetic supporting evidence'}]}
            receipt = client.tool('safety_evidence_review', arguments)
            validate(receipt, 'receipt')
            without_sources = {k: v for k, v in arguments.items() if k != 'sources'}
            omitted = client.tool('safety_evidence_review', without_sources)
            validate(omitted, 'receipt')
            check(omitted['sources'] == [], 'Omitted optional supporting sources remain empty')
            for optional_sources in (None, []):
                check(client.tool('safety_evidence_review', {**without_sources, 'sources': optional_sources}) == omitted,
                      'Null and empty supporting sources match omission')
            check(receipt == client.tool('safety_evidence_review', arguments), 'Idempotent exact receipt')
            check(read(client, receipt['input']) == before and read(client, receipt['comparisonInput']) == after, 'Original safety JSON bytes preserved')
            check(read(client, receipt['sources'][0]['artifact']) == opaque_bytes, 'Opaque supporting bytes retained')
            check(receipt['sources'][0]['fileName'] == 'supplied-support.bin', 'Original supporting filename retained')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check(result['summary']['changeCount'] == 2 and result['summary']['issueCount'] == 2, 'Exact signature differences and zero signature issues')
            check(result['signatures'][0]['valueDecimal'] == '18446744073709551615' and result['signatures'][2]['valueDecimal'] == '9007199254740993', 'Exact UInt64 display strings across MCP')
            check(result['changes'][0]['before']['value'] == 18446744073709551615, 'Original exact UInt64 retained in full report')
            for section in ('observations', 'signatures', 'issues', 'changes'):
                rows, offset = [], 0
                while True:
                    page = client.tool('safety_evidence_report', {'recordId': receipt['recordId'], 'section': section, 'offset': offset, 'count': 1})
                    validate(page, 'page')
                    rows.extend(row['value'] for row in page['rows'])
                    if page['nextOffset'] is None: break
                    check(page['nextOffset'] > offset, 'Report paging makes progress')
                    offset = page['nextOffset']
                check(rows == result[section], 'Paged ' + section + ' matches immutable artifact')
            flags = ('nativeExecutable', 'nativeExecuted', 'nativeQualified', 'liveFreshnessVerified', 'sourceAuthenticityVerified', 'requiredSignatureProfileVerified', 'safetyApproval', 'wholeProgramAnalysisComplete')
            for flag in flags:
                check(result[flag] is False and receipt[flag] is False, 'Unsupported claim remains false: ' + flag)
                for kind, target in (('record', record), ('receipt', receipt)):
                    adversary = copy.deepcopy(target)
                    (adversary['report'] if kind == 'record' else adversary)[flag] = True
                    try: validate(adversary, kind)
                    except ValidationError: check(True, 'Schema refuses ' + kind + '/' + flag)
                    else: raise AssertionError('Unsupported claim accepted')
            refuse(client, 'safety_evidence_report', {'recordId': receipt['recordId'], 'section': 'changes', 'offset': 100}, 'InvalidRequest')
            refuse(client, 'safety_evidence_review', {**arguments, 'plcPath': fixture['plcPath'] + 'Other'}, 'InvalidSafetyEvidence')
            refuse(client, 'safety_evidence_review', {**arguments, 'sources': {}}, 'InvalidSafetyEvidence')
            refuse(client, 'safety_evidence_review', {**arguments, 'sources': [arguments['sources'][0]] * 2}, 'InvalidSafetyEvidence')
            invalid_json = json.loads(before)
            invalid_json['programSignatures'][0]['value'] = '18446744073709551615'
            invalid = upload(client, json.dumps(invalid_json).encode())
            refuse(client, 'safety_evidence_review', {**arguments, 'uploadRef': invalid['artifactRef']}, 'InvalidSafetyEvidence')
            malformed = upload(client, b'{')
            refuse(client, 'safety_evidence_review', {**arguments, 'uploadRef': malformed['artifactRef']}, 'SafetyEvidenceReviewFailed')
            partial = json.loads(after)
            partial['completeness']['scopeComplete'] = False
            partial['warnings'] = [{'path': fixture['plcPath'], 'message': 'Synthetic missing attribute'}]
            partial_upload = upload(client, json.dumps(partial).encode())
            partial_receipt = client.tool('safety_evidence_review', {**arguments, 'comparisonUploadRef': partial_upload['artifactRef']})
            partial_record = json.loads(read(client, partial_receipt['reportArtifact']))
            validate(partial_record, 'record')
            check(partial_receipt['summary']['issueCount'] == 3 and partial_record['report']['observations'][1]['warnings'] == partial['warnings'], 'Partial native warning shape remains explicit')
            huge = json.loads(after)
            huge['futureEnvelope']['opaque'] = 'x' * (130 * 1024)
            huge_upload = upload(client, json.dumps(huge).encode())
            huge_receipt = client.tool('safety_evidence_review', {**arguments, 'comparisonUploadRef': huge_upload['artifactRef']})
            huge_page = client.tool('safety_evidence_report', {'recordId': huge_receipt['recordId'], 'section': 'changes'})
            validate(huge_page, 'page')
            check(huge_page['rows'][-1].get('omitted') is True and len(json.dumps(huge_page).encode()) <= 128 * 1024, 'Oversized page row omitted with bounded progress')
            check(read(client, huge_receipt['comparisonInput']) == json.dumps(huge).encode(), 'Oversized omitted content still downloadable exactly')
            for item in (incoming, comparison, supporting, saved, invalid, malformed, partial_upload, huge_upload):
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
        finally: close(client)
        client = start()
        try:
            check(client.tool('safety_evidence_report', {'recordId': receipt['recordId']}) == receipt, 'Restart and upload release preserve receipt')
            check(read(client, receipt['input']) == before, 'Original bytes survive restart')
            for reference in (receipt['input'], receipt['comparisonInput'], receipt['reportArtifact'], receipt['sources'][0]['artifact'], {'path': 'project-reviews/' + fixture['reviewId'] + '.json'}):
                path = (artifacts / reference['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'safety_evidence_report', {'recordId': receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            check(client.tool('safety_evidence_report', {'recordId': receipt['recordId']}) == receipt, 'Restored exact bytes reopen without mutation replay')
            check(read(client, receipt['sources'][0]['artifact']) == opaque_bytes, 'Opaque sources survive release and restart')
            forged = copy.deepcopy(record)
            forged['report']['safetyApproval'] = True
            data = json.dumps(forged).encode()
            digest = hashlib.sha256(data).hexdigest()
            (artifacts / 'safety-evidence-reviews' / (digest + '.json')).write_bytes(data)
            refuse(client, 'safety_evidence_report', {'recordId': digest}, 'ArtifactChanged')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS safety evidence MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__':
    main()
