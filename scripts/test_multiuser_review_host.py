"""Actual supplied Multiuser observation intake/reopen checks; no native server is simulated."""
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
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    packet_bytes = (ROOT / "fixtures/multiuser/observations.json").read_bytes()
    packet = json.loads(packet_bytes)
    encode = lambda value: (json.dumps(value, ensure_ascii=False) + "\n").encode()
    schema = json.loads((ROOT / "contracts/multiuser-observations.v1.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[])
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    validate(packet, 'request')
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-worker.exe'
    def close(client):
        closed = client.close()
        report['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native worker')
    def start():
        client = Client(args.host.resolve(), 30, ['--no-native-workers', '--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        try:
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'multiuser-review-check', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def upload(client, data):
        status = client.tool('artifact_upload_begin', dict(fileName='observations.json', expectedBytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=status['uploadId'], offset=offset, data=base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()))
        return client.tool('artifact_upload_commit', dict(uploadId=status['uploadId']))
    def read(client, reference):
        chunks, offset = [], 0
        while True:
            part = client.tool('artifact_read', dict(path=reference['path'], offset=offset, count=65536))
            raw = base64.b64decode(part['data'], validate=True)
            assert part['sha256'] == reference['sha256'] and part['offset'] == offset and len(raw) == part['count']
            chunks.append(raw)
            if part['complete']: break
            offset = part['nextOffset']
        raw = b''.join(chunks)
        check(hashlib.sha256(raw).hexdigest() == reference['sha256'], 'Downloaded artifact hash matches exact bytes')
        return raw
    def refuse(client, tool, arguments, code):
        result = client.request('tools/call', dict(name=tool, arguments=arguments))
        error = result['structuredContent']['error']
        check(result.get('isError') and error['code'] == code and error['nativeAccessAttempted'] is False, 'Structured refusal: ' + code)
    try:
        client = start()
        try:
            check({'multiuser_observation_review', 'multiuser_observation_page'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both collaboration review tools registered')
            incoming = upload(client, packet_bytes)
            arguments = dict(uploadRef=incoming['artifactRef'])
            receipt = client.tool('multiuser_observation_review', arguments)
            validate(receipt, 'receipt')
            check(receipt == client.tool('multiuser_observation_review', arguments), 'Repeated intake is byte-idempotent')
            check(read(client, receipt['input']) == packet_bytes, 'Original bytes and Unicode preserved')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check(all(result[k] is False for k in ('nativeExecutable', 'sourceAuthenticityVerified', 'conflictFreeVerified', 'stableProjectIdentityVerified', 'liveFreshnessVerified')), 'Supplied declarations never gain native authority')
            check(sum(r['code'] == 'deletedObjectsNotRepresented' for r in result['issues']) == 2, 'Deleted-object omissions retained in both phases')
            check(any(r['section'] == 'markings' and r['status'] == 'onlyBefore' for r in result['changes']), 'Missing marking is not called a deletion')
            check(any(r['code'] == 'partial' for r in result['issues']), 'Partial coverage remains visible')
            for section in ('observations', 'changes', 'issues'):
                offset, rows = 0, []
                while True:
                    page = client.tool('multiuser_observation_page', dict(recordId=receipt['recordId'], section=section, offset=offset, count=2))
                    validate(page, 'page')
                    check(len(encode(page)) < 128 * 1024, 'Bounded ' + section + ' page')
                    rows.extend(r['value'] for r in page['rows'])
                    if page['nextOffset'] is None: break
                    check(page['nextOffset'] > offset, 'Page makes progress')
                    offset = page['nextOffset']
                check(rows == result[section], 'All ' + section + ' rows survive paging')
            for label in ('extra', 'unknownField', 'duplicate', 'endpoint', 'credentials', 'port', 'unknownCoverage', 'failedWithRows', 'unknownLock', 'unlockedOwner', 'sameCapture', 'control', 'sessionType'):
                bad = copy.deepcopy(packet)
                if label == 'extra': bad['nativeQualified'] = True
                elif label == 'unknownField': bad['after']['server']['sessionToken'] = 'forbidden-field'
                elif label == 'duplicate': bad['after']['projects']['rows'].append(copy.deepcopy(bad['after']['projects']['rows'][0]))
                elif label == 'endpoint': bad['after']['server']['host'] = 'https://another.invalid/'
                elif label == 'credentials': bad['after']['server']['host'] = 'https://user:password@server.invalid/'
                elif label == 'port': bad['after']['server']['port'] = 0
                elif label == 'unknownCoverage': bad['after']['groups']['coverage'] = 'approved'
                elif label == 'failedWithRows': bad['after']['projects']['coverage'] = 'failed'; bad['after']['projects']['diagnostic'] = 'failure'
                elif label == 'unknownLock': bad['after']['projects']['rows'][0]['lock']['state'] = 'mine'
                elif label == 'unlockedOwner': bad['after']['projects']['rows'][0]['lock']['state'] = 'unlocked'
                elif label == 'sameCapture': bad['after']['captureId'] = bad['before']['captureId']
                elif label == 'control': bad['after']['producer'] = 'invalid\nvalue'
                else: bad['after']['sessions']['rows'][0]['upToDate'] = 'true'
                incoming_bad = upload(client, encode(bad))
                refuse(client, 'multiuser_observation_review', dict(uploadRef=incoming_bad['artifactRef']), 'InvalidMultiuserObservation')
                if label not in ('duplicate', 'endpoint', 'sameCapture'):
                    try: validate(bad, 'request')
                    except ValidationError: check(True, 'Independent schema refuses ' + label)
                    else: raise AssertionError('Schema accepted ' + label)
            duplicate = packet_bytes.replace(b'"producer":', b'"producer": "duplicate", "producer":', 1)
            check(duplicate != packet_bytes, 'Duplicate-key negative control constructed')
            bad_upload = upload(client, duplicate)
            refuse(client, 'multiuser_observation_review', dict(uploadRef=bad_upload['artifactRef']), 'InvalidMultiuserObservation')
            adversary = copy.deepcopy(record); adversary['report']['conflictFreeVerified'] = True
            try: validate(adversary, 'record')
            except ValidationError: check(True, 'Output schema rejects conflict-free assertion')
            else: raise AssertionError('Output schema admitted false authority')
            refuse(client, 'multiuser_observation_page', dict(recordId=receipt['recordId'], section='execute'), 'InvalidRequest')
            refuse(client, 'multiuser_observation_page', dict(recordId=receipt['recordId'], section='issues', offset=999999), 'InvalidRequest')
            large = copy.deepcopy(packet)
            large['after']['markings']['rows'] = [dict(copy.deepcopy(packet['before']['markings']['rows'][0]), objectPath='P/' + str(i) + '/' + 'x' * 4000) for i in range(200)]
            validate(large, 'request')
            large_upload = upload(client, encode(large))
            large_receipt = client.tool('multiuser_observation_review', dict(uploadRef=large_upload['artifactRef']))
            bounded = client.tool('multiuser_observation_page', dict(recordId=large_receipt['recordId'], section='observations', count=200))
            validate(bounded, 'page')
            check(0 < len(bounded['rows']) < 200 and bounded['nextOffset'] == len(bounded['rows']) and len(encode(bounded)) < 128 * 1024, 'Large valid rows stop at byte budget with a progressing cursor')
            client.tool('artifact_upload_release', dict(uploadId=large_upload['uploadId']))
            client.tool('artifact_upload_release', dict(uploadId=incoming['uploadId']))
        finally:
            close(client)
        client = start()
        try:
            check(client.tool('multiuser_observation_page', dict(recordId=receipt['recordId'])) == receipt, 'Fresh Host reopens after original upload release')
            for reference in (receipt['input'], receipt['reportArtifact']):
                path = artifacts / reference['path']; original = path.read_bytes()
                try:
                    path.write_bytes(original + b' ')
                    refuse(client, 'multiuser_observation_page', dict(recordId=receipt['recordId']), 'ArtifactChanged')
                finally: path.write_bytes(original)
            # A newly hashed forged record still cannot alter the derived report.
            forged = copy.deepcopy(record); forged['report']['conflictFreeVerified'] = True
            raw = encode(forged); forged_id = hashlib.sha256(raw).hexdigest()
            (artifacts / 'multiuser-reviews' / (forged_id + '.json')).write_bytes(raw)
            refuse(client, 'multiuser_observation_page', dict(recordId=forged_id), 'ArtifactChanged')
            check(client.tool('multiuser_observation_page', dict(recordId=receipt['recordId'])) == receipt, 'Original evidence remains usable after rejected forgery')
        finally:
            close(client)
        report['completed'] = True
    finally:
        report['checkCount'] = len(report['checks'])
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS Multiuser review MCP: {len(report['checks'])} checks; two Host lifetimes, supplied evidence only.")


if __name__ == '__main__':
    main()
