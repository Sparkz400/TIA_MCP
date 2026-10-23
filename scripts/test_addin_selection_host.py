"""Actual stdio selection intake/reopen checks; no Siemens companion or UI is simulated."""
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
    model = json.loads((ROOT / 'fixtures/project-explorer/representative.project-model.json').read_text(encoding='utf-8'))
    review = dict(schemaVersion='project-review.v1', createdAtUtc='2026-10-18T00:00:00Z', model=model,
                  documents=[], capture={'source': 'synthetic fixture'}, consistency='Supplied historical metadata only.', wholeProjectContentComplete=False)
    encode = lambda value: (json.dumps(value, ensure_ascii=False) + '\n').encode()
    review_bytes = encode(review)
    review_id = hashlib.sha256(review_bytes).hexdigest()
    selected = [n for n in model['nodes'] if n.get('origin') == 'native' and n.get('objectPath')][:2]
    packet = dict(schemaVersion='addin-selection.v1', reviewId=review_id, selectionId='a' * 32,
                  selectedAtUtc='2026-10-18T00:00:00.0000000+00:00', surface='projectTree', projectHost=model['project']['host'],
                  producer=dict(productId='Synthetic Über companion', version='0.0.0', tiaVersion=21, assemblySha256='b' * 64, configurationSha256='c' * 64),
                  references=[dict(schemaVersion='project-model.v1', snapshotId=model['snapshotId'], sessionGeneration=model['sessionGeneration'],
                                   projectPath=model['project']['projectPath'], nodeId=n['nodeId'], objectPath=n['objectPath']) for n in selected])
    schema = json.loads((ROOT / 'contracts/addin-selection.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = dict(completed=False, nativeExecuted=False, nativeCompanionExecuted=False, checks=[], shutdowns=[])
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
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'addin-selection-check', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def upload(client, data):
        status = client.tool('artifact_upload_begin', dict(fileName='selection.json', expectedBytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
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
            check({'addin_selection_review', 'addin_selection_page'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both portable tools registered')
            source = upload(client, review_bytes)
            saved = client.tool('project_snapshot_import', dict(uploadRef=source['artifactRef']))
            check(saved['reviewId'] == review_id, 'Exact saved review bytes imported')
            packet_bytes = encode(packet)
            incoming = upload(client, packet_bytes)
            arguments = dict(reviewId=review_id, uploadRef=incoming['artifactRef'])
            receipt = client.tool('addin_selection_review', arguments)
            validate(receipt, 'receipt')
            check(receipt == client.tool('addin_selection_review', arguments), 'Repeated intake is byte-idempotent')
            check(read(client, receipt['input']) == packet_bytes, 'Original UTF-8 bytes and final newline retained')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check([r['reference'] for r in result['selections']] == packet['references'], 'No reference rewriting or name-based rebinding')
            check([r['name'] for r in result['selections']] == [n['name'] for n in selected], 'Resolved names come from exact saved nodes')
            check(not result['producerAuthenticated'] and not result['nativeExecutable'] and not result['liveSelectionVerified'], 'Declared producer and historical metadata cannot authorize native work')
            rows = []
            for offset in range(2):
                page = client.tool('addin_selection_page', dict(recordId=receipt['recordId'], section='selections', offset=offset, count=1))
                validate(page, 'page')
                rows.extend(r['value'] for r in page['rows'])
            check(rows == result['selections'], 'Bounded pages reproduce full ordered selection')
            for label in ('session', 'host', 'node', 'duplicate', 'extra', 'version', 'operation'):
                bad = copy.deepcopy(packet)
                if label == 'session': bad['references'][0]['sessionGeneration'] = 'changed'
                elif label == 'host': bad['projectHost'] = 'another-host'
                elif label == 'node': bad['references'][0]['nodeId'] = 'missing-node'
                elif label == 'duplicate': bad['references'].append(copy.deepcopy(bad['references'][0]))
                elif label == 'version': bad['producer']['tiaVersion'] = 20
                elif label == 'operation': bad['operation'] = 'compile'
                else: bad['nativeQualified'] = True
                item = upload(client, encode(bad))
                refuse(client, 'addin_selection_review', dict(reviewId=review_id, uploadRef=item['artifactRef']), 'InvalidAddInSelection')
                if label in ('extra', 'version', 'operation'):
                    try: validate(bad, 'request')
                    except ValidationError: check(True, 'Schema refuses ' + label)
                    else: raise AssertionError('Schema accepted ' + label)
            bad_bytes = packet_bytes.replace(b'"surface": "projectTree"', b'"surface": "projectTree", "surface": "projectTree"')
            check(bad_bytes != packet_bytes, 'Duplicate-key negative input constructed')
            item = upload(client, bad_bytes)
            refuse(client, 'addin_selection_review', dict(reviewId=review_id, uploadRef=item['artifactRef']), 'InvalidAddInSelection')
            adversary = copy.deepcopy(record); adversary['report']['producerAuthenticated'] = True
            try: validate(adversary, 'record')
            except ValidationError: check(True, 'Output schema refuses authentication claims')
            else: raise AssertionError('Schema authenticated a supplied producer')
            refuse(client, 'addin_selection_page', dict(recordId=receipt['recordId'], section='execute'), 'InvalidRequest')
            client.tool('artifact_upload_release', dict(uploadId=incoming['uploadId']))
        finally:
            close(client)
        client = start()
        try:
            check(client.tool('addin_selection_page', dict(recordId=receipt['recordId'])) == receipt, 'Fresh Host reopens after original upload release')
            path = artifacts / receipt['input']['path']; original = path.read_bytes()
            try:
                path.write_bytes(original + b' ')
                refuse(client, 'addin_selection_page', dict(recordId=receipt['recordId']), 'ArtifactChanged')
            finally: path.write_bytes(original)
            check(client.tool('addin_selection_page', dict(recordId=receipt['recordId'])) == receipt, 'Restored exact bytes restore observation only')
        finally:
            close(client)
        report['completed'] = True
    finally:
        report['checkCount'] = len(report['checks'])
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS Add-In selection MCP: {len(report['checks'])} checks; two Host lifetimes, supplied evidence only.")


if __name__ == '__main__':
    main()
