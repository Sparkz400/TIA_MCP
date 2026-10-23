"""Actual MCP component-bound generation, retained lineage and restart; no native worker."""
import argparse
import base64
import copy
import hashlib
import json
import shutil
from pathlib import Path

from smoke_mcp import Client
from hmi_text_list_generation_checks import verify, local_reader


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--fixture', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    artifacts = output / 'host-artifacts'
    for name in ('project-reviews', 'hmi-component-reviews'):
        shutil.copytree(Path(fixture['artifactRoot']) / name, artifacts / name)
    read = local_reader(artifacts)
    report = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[])
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    def upload(client, request):
        raw = json.dumps(request, ensure_ascii=False).encode()
        begin = client.tool('artifact_upload_begin', dict(fileName='generation.json', expectedBytes=len(raw), sha256=hashlib.sha256(raw).hexdigest()))
        for offset in range(0, len(raw), begin['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=begin['uploadId'], offset=offset, data=base64.b64encode(raw[offset:offset + begin['maxChunkBytes']]).decode()))
        return begin['uploadId'], client.tool('artifact_upload_commit', dict(uploadId=begin['uploadId']))['artifactRef']
    def refuse(client, identity):
        result = client.request('tools/call', dict(name='hmi_text_list_author', arguments=dict(recordId=identity)))
        check(result.get('isError') and result['structuredContent']['error']['code'] == 'ArtifactChanged', 'Rehashed/tampered authoring refuses')
    def start():
        missing = output / 'absent-worker.exe'
        client = Client(args.host.resolve(), 60, ['--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='bool-generation-test', version='1')))
        client.notify('notifications/initialized')
        return client
    def close(client):
        result = client.close(); report['shutdowns'].append(result)
        check(result['hostExitCode'] == 0 and not result['forcedHostStop'] and not result['workerExits'], 'Clean offline Host lifetime')
    rows = []
    try:
        client = start()
        try:
            requests = [fixture['installRequest'], fixture['updateRequest'], fixture['renameRequest']]
            for index, request in enumerate(requests):
                request = copy.deepcopy(request)
                if index: request['previousRecordId'] = rows[0][1]['recordId']
                upload_id, reference = upload(client, request)
                receipt = client.tool('hmi_text_list_author', dict(uploadRef=reference))
                check(receipt['status'] == 'candidatesReady', 'Fresh MCP candidate generation')
                verify(request, receipt, read)
                check(True, 'Independent original PLC/binding/intent/YAML/peer verification')
                check(receipt == client.tool('hmi_text_list_author', dict(uploadRef=reference)), 'Generation retry deterministic')
                client.tool('artifact_upload_release', dict(uploadId=upload_id))
                rows.append((request, receipt))
            for conflict in fixture['conflicts']:
                receipt = client.tool('hmi_text_list_author', dict(recordId=conflict['receipt']['recordId']))
                check(receipt == conflict['receipt'] and receipt['status'] == 'conflict' and receipt['files'] == [], 'Conflict survives Host reopen: ' + conflict['label'])
                verify(conflict['request'], receipt, read)
            prior = client.tool('hmi_text_list_author', dict(recordId=fixture['migration']['recordId']))
            check(prior == fixture['migration'], 'Prior v1 author bytes accepted with explicit missing-lineage gap')
        finally:
            close(client)
        client = start()
        try:
            for request, receipt in rows:
                reopened = client.tool('hmi_text_list_author', dict(recordId=receipt['recordId']))
                check(reopened == receipt, 'Fresh Host reopens exact released generation result')
                verify(request, reopened, read)
            request, receipt = rows[1]
            for reference in (receipt['input'], receipt['files'][1]['artifact'], receipt['preservedFiles'][0]['artifact']):
                path = artifacts / reference['path']; original = path.read_bytes()
                try:
                    path.write_bytes(original + b'tampered')
                    refuse(client, receipt['recordId'])
                finally:
                    path.write_bytes(original)
            for field in ('nativeQualified', 'previousRecordId', 'componentContext', 'preservedFiles'):
                forged = json.loads(read(receipt['recordArtifact']))
                if field == 'nativeQualified': forged[field] = True
                elif field == 'previousRecordId': forged[field] = None
                elif field == 'componentContext': forged[field]['binding']['afterDeclaration']['dataType'] = 'Real'
                else: forged[field][0]['bytes'] += 1
                raw = json.dumps(forged).encode(); identity = hashlib.sha256(raw).hexdigest()
                (artifacts / 'hmi-component-reviews' / (identity + '.author.json')).write_bytes(raw)
                refuse(client, identity)
            # Changed YAML with an honest digest must fail the independent intent oracle.
            forged = copy.deepcopy(receipt); source = read(forged['files'][1]['artifact']); raw = source.replace(b'Ready', b'Wrong')
            assert raw != source
            identity = hashlib.sha256(raw).hexdigest()
            ref = dict(path='hmi-component-reviews/' + identity + '.candidate', sha256=identity)
            (artifacts / ref['path']).write_bytes(raw); forged['files'][1].update(artifact=ref, bytes=len(raw))
            try: verify(request, forged, read)
            except AssertionError: check(True, 'Independent YAML oracle rejects changed content with honest hash')
            else: raise AssertionError('Changed YAML accepted')
            check(client.tool('hmi_text_list_author', dict(recordId=receipt['recordId'])) == receipt, 'Restored source reopens unchanged')
        finally:
            close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('PASS component-bound text-list MCP:', report['count'], 'checks; two clean offline Host lifetimes')


if __name__ == '__main__':
    main()
