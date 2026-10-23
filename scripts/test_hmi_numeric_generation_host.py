"""Actual offline MCP integer-state generation, independent oracle and retained restart."""
import argparse
import base64
import copy
import hashlib
import json
import shutil
from pathlib import Path

from smoke_mcp import Client
from hmi_numeric_generation_checks import verify, local_reader


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
    source = Path(fixture['artifactRoot']).resolve()
    def manifest(root):
        return {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in root.rglob('*') if p.is_file()}
    before = manifest(source)
    for name in ('project-reviews', 'hmi-component-reviews'):
        shutil.copytree(source / name, artifacts / name)
    read = local_reader(artifacts)
    # Exact copied predecessor v1 remains available. Each tested v3 record is
    # removed from this isolated copy so MCP genuinely generates it again.
    for case in fixture['cases']:
        path = (artifacts / case['receipt']['recordArtifact']['path']).resolve()
        path.relative_to(artifacts.resolve())
        if path.exists():
            path.unlink()
    report = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[])
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    def upload(client, raw):
        begin = client.tool('artifact_upload_begin', dict(fileName='numeric.json', expectedBytes=len(raw), sha256=hashlib.sha256(raw).hexdigest()))
        for offset in range(0, len(raw), begin['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=begin['uploadId'], offset=offset,
                        data=base64.b64encode(raw[offset:offset + begin['maxChunkBytes']]).decode()))
        return begin['uploadId'], client.tool('artifact_upload_commit', dict(uploadId=begin['uploadId']))['artifactRef']
    def publish(raw, suffix):
        identity = hashlib.sha256(raw).hexdigest()
        reference = dict(path='hmi-component-reviews/' + identity + suffix, sha256=identity)
        (artifacts / reference['path']).write_bytes(raw)
        return reference
    def encoded(value):
        return json.dumps(value, ensure_ascii=False).encode()
    def refuse(client, arguments, label):
        result = client.request('tools/call', dict(name='hmi_text_list_author', arguments=arguments))
        check(result.get('isError') and result['structuredContent']['error']['code'] == 'ArtifactChanged', label)
    def start():
        missing = output / 'absent-worker.exe'
        client = Client(args.host.resolve(), 90, ['--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={},
                       clientInfo=dict(name='integer-state-generation-test', version='1')))
        client.notify('notifications/initialized')
        return client
    def close(client):
        result = client.close()
        report['shutdowns'].append(result)
        check(result['hostExitCode'] == 0 and not result['forcedHostStop'] and not result['workerExits'], 'Clean offline Host lifetime')
    try:
        client = start()
        try:
            for case in fixture['cases']:
                raw = read(case['receipt']['input'])
                check(json.loads(raw) == case['request'], 'Fixture request matches retained original: ' + case['label'])
                upload_id, reference = upload(client, raw)
                receipt = client.tool('hmi_text_list_author', dict(uploadRef=reference))
                check(receipt == case['receipt'], 'MCP creates exact expected retained result: ' + case['label'])
                verify(case['request'], receipt, read)
                check(True, 'Independent original source/dependency/state/YAML oracle: ' + case['label'])
                check(client.tool('hmi_text_list_author', dict(uploadRef=reference)) == receipt, 'Upload retry deterministic')
                client.tool('artifact_upload_release', dict(uploadId=upload_id))
        finally:
            close(client)
        client = start()
        try:
            for case in fixture['cases']:
                receipt = client.tool('hmi_text_list_author', dict(recordId=case['receipt']['recordId']))
                check(receipt == case['receipt'], 'New Host exact released reopen: ' + case['label'])
                verify(case['request'], receipt, read)
            case = fixture['cases'][1]
            request, receipt = case['request'], case['receipt']
            for field in ('valueCoverage', 'dependencyCoverage', 'stateChanges', 'componentContext', 'previousRecordId'):
                record = json.loads(read(receipt['recordArtifact']))
                if field == 'stateChanges':
                    record[field] = []
                elif field == 'previousRecordId':
                    record[field] = None
                elif field == 'componentContext':
                    record[field]['binding']['afterDeclaration']['dataType'] = 'DInt'
                else:
                    record[field] = {}
                forged = publish(encoded(record), '.author.json')
                refuse(client, dict(recordId=forged['sha256']), 'Honestly rehashed derived record refuses: ' + field)
            for reference in (receipt['input'], receipt['files'][0]['artifact'], receipt['preservedFiles'][0]['artifact']):
                path = artifacts / reference['path']
                raw = path.read_bytes()
                try:
                    path.write_bytes(raw + b'tampered')
                    refuse(client, dict(recordId=receipt['recordId']), 'Changed retained input/candidate/peer refuses')
                finally:
                    path.write_bytes(raw)
            # A new honest hash cannot disguise a changed original review input
            # or a forged derived summary: both are independently re-derived.
            for change_input in (True, False):
                component = json.loads(read(dict(path='hmi-component-reviews/' + request['componentReviewId'] + '.json', sha256=request['componentReviewId'])))
                if change_input:
                    original = json.loads(read(component['input']))
                    original['bindings'][0]['declaredHmiType'] = 'DInt'
                    component['input'] = publish(encoded(original), '.input')
                else:
                    component['report']['bindings'][0]['afterDeclaration']['dataType'] = 'DInt'
                changed = publish(encoded(component), '.json')
                forged_request = copy.deepcopy(request)
                forged_request['componentReviewId'] = changed['sha256']
                upload_id, reference = upload(client, encoded(forged_request))
                refuse(client, dict(uploadRef=reference), 'Rehashed original/derived review chain refuses')
                client.tool('artifact_upload_release', dict(uploadId=upload_id))
            for index, old, new in ((0, b'20', b'21'), (1, b'Stopped', b'Wrong')):
                forged = copy.deepcopy(receipt)
                raw = read(forged['files'][index]['artifact'])
                changed = raw.replace(old, new)
                assert changed != raw
                forged['files'][index].update(artifact=publish(changed, '.candidate'), bytes=len(changed))
                try:
                    verify(request, forged, read)
                except AssertionError:
                    check(True, 'Independent numeric/translation YAML oracle rejects honest hash')
                else:
                    raise AssertionError('Changed YAML passed')
                record = {k: v for k, v in forged.items() if k not in ('recordId', 'recordArtifact')}
                reference = publish(encoded(record), '.author.json')
                refuse(client, dict(recordId=reference['sha256']), 'Rehashed candidate record differs from regenerated original intent')
            check(client.tool('hmi_text_list_author', dict(recordId=receipt['recordId'])) == receipt, 'Original record still reopens exactly after restored tamper controls')
        finally:
            close(client)
        check(manifest(source) == before, 'Original managed fixture store remained byte immutable')
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest()
                                        for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('PASS integer-state MCP:', report['count'], 'checks; two clean offline Host lifetimes')


if __name__ == '__main__':
    main()
