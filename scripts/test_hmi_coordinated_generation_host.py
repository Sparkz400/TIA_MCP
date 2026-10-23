"""Actual MCP coordinated generation, complete independent outputs and restart/tamper."""
import argparse
import base64
import copy
import hashlib
import json
import shutil
from pathlib import Path

from smoke_mcp import Client
from hmi_coordinated_generation_checks import verify, local_reader, negative_controls


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    source, artifacts = Path(fixture['artifactRoot']).resolve(), output / 'host-artifacts'
    digest = lambda raw: hashlib.sha256(raw).hexdigest()
    def manifest(root):
        return {p.relative_to(root).as_posix(): digest(p.read_bytes()) for p in root.rglob('*') if p.is_file()}
    before = manifest(source)
    for directory in ('project-reviews', 'hmi-component-reviews'):
        shutil.copytree(source / directory, artifacts / directory)
    for case in fixture['cases']:
        path = artifacts / case['receipt']['recordArtifact']['path']
        if path.exists():
            path.unlink()
        for child in case['receipt']['outputs']:
            if child['authorRecordId']:
                path = artifacts / 'hmi-component-reviews' / (child['authorRecordId'] + '.author.json')
                if path.exists():
                    path.unlink()
    read = local_reader(artifacts)
    proof = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[])
    def check(value, label):
        assert value, label
        proof['checks'].append(label)
    def upload(client, value):
        raw = value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=False).encode()
        start = client.tool('artifact_upload_begin', dict(fileName='set.json', expectedBytes=len(raw), sha256=digest(raw)))
        for offset in range(0, len(raw), start['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=start['uploadId'], offset=offset,
                        data=base64.b64encode(raw[offset:offset + start['maxChunkBytes']]).decode()))
        return start['uploadId'], client.tool('artifact_upload_commit', dict(uploadId=start['uploadId']))['artifactRef']
    def publish(value, suffix):
        raw = value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=False).encode()
        identity = digest(raw)
        reference = dict(path='hmi-component-reviews/' + identity + suffix, sha256=identity)
        (artifacts / reference['path']).write_bytes(raw)
        return reference
    def refuse(client, arguments, label):
        result = client.request('tools/call', dict(name='hmi_text_list_generate_set', arguments=arguments))
        check(result.get('isError') and result['structuredContent']['error']['code'] in ('ArtifactChanged', 'ArtifactUnavailable'), label)
    def start():
        client = Client(args.host.resolve(), 180, ['--no-native-workers', '--artifacts', str(artifacts)])
        client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='coordinated-text-lists', version='1')))
        client.notify('notifications/initialized')
        return client
    def close(client):
        result = client.close()
        proof['shutdowns'].append(result)
        check(result['hostExitCode'] == 0 and not result['forcedHostStop'] and not result['workerExits'], 'Clean offline Host lifetime')
    try:
        client = start()
        try:
            for case in fixture['cases']:
                request, expected = case['request'], case['receipt']
                raw = read(expected['input'])
                check(json.loads(raw) == request, 'Exact retained input: ' + case['label'])
                published_before = {p.name: digest(p.read_bytes()) for p in (artifacts / 'hmi-component-reviews').iterdir()
                                    if p.is_file() and p.name.endswith(('.author.json', '.candidate'))}
                upload_id, reference = upload(client, raw)
                receipt = client.tool('hmi_text_list_generate_set', dict(uploadRef=reference))
                check(receipt == expected, 'Exact generated set receipt: ' + case['label'])
                verify(request, receipt, read)
                check(True, 'Independent source/member/dependency/consumer/full-YAML oracle: ' + case['label'])
                check(client.tool('hmi_text_list_generate_set', dict(uploadRef=reference)) == receipt, 'Deterministic same input')
                if receipt['status'] == 'conflict':
                    after = {p.name: digest(p.read_bytes()) for p in (artifacts / 'hmi-component-reviews').iterdir()
                             if p.is_file() and p.name.endswith(('.author.json', '.candidate'))}
                    check(after == published_before, 'Whole-set conflict publishes no child authors or candidates')
                client.tool('artifact_upload_release', dict(uploadId=upload_id))
        finally:
            close(client)
        client = start()
        try:
            for case in fixture['cases']:
                actual = client.tool('hmi_text_list_generate_set', dict(recordId=case['receipt']['recordId']))
                check(actual == case['receipt'], 'Exact fresh Host released reopen: ' + case['label'])
                verify(case['request'], actual, read)
            first = fixture['cases'][0]
            controls = negative_controls(first['request'], first['receipt'], read)
            proof['independentNegativeControls'] = controls
            check(len(controls) == 7, 'Seven independent schema/original-output mutation controls')
            case = fixture['cases'][1]
            receipt, request = case['receipt'], case['request']
            for field in ('coverage', 'outputs', 'files', 'previousRecordId', 'conflicts'):
                record = json.loads(read(receipt['recordArtifact']))
                record[field] = None if field == 'previousRecordId' else {} if field == 'coverage' else ['invented'] if field == 'conflicts' else []
                forged = publish(record, '.text-set.json')
                refuse(client, dict(recordId=forged['sha256']), 'Honestly rehashed coordinated record refuses: ' + field)
            references = [receipt['input'], *[f['artifact'] for f in receipt['files']], *[f['artifact'] for f in receipt['preservedFiles']]]
            references += [dict(path='hmi-component-reviews/' + o['authorRecordId'] + '.author.json', sha256=o['authorRecordId']) for o in receipt['outputs']]
            component = json.loads(read(dict(path='hmi-component-reviews/' + request['componentReviewId'] + '.json', sha256=request['componentReviewId'])))
            references += [f['artifact'] for f in component['report']['files']]
            unique = {r['path']: r for r in references}
            for reference in unique.values():
                path = artifacts / reference['path']
                original = path.read_bytes()
                for mode in ('altered', 'removed'):
                    try:
                        if mode == 'altered':
                            path.write_bytes(original + b'tampered')
                        else:
                            path.unlink()
                        refuse(client, dict(recordId=receipt['recordId']), 'One retained input/candidate/peer/child/source ' + mode)
                    finally:
                        path.write_bytes(original)
            for change_input in (True, False):
                forged_review = copy.deepcopy(component)
                if change_input:
                    original = json.loads(read(forged_review['input']))
                    original['bindings'][0]['declaredHmiType'] = 'DInt'
                    forged_review['input'] = publish(original, '.input')
                else:
                    forged_review['report']['bindings'][0]['afterDeclaration']['dataType'] = 'DInt'
                changed = publish(forged_review, '.json')
                changed_request = copy.deepcopy(request)
                changed_request['componentReviewId'] = changed['sha256']
                upload_id, reference = upload(client, changed_request)
                refuse(client, dict(uploadRef=reference), 'Honestly rehashed original/derived review chain refuses')
                client.tool('artifact_upload_release', dict(uploadId=upload_id))
            check(client.tool('hmi_text_list_generate_set', dict(recordId=receipt['recordId'])) == receipt, 'Exact original set reopens after restored controls')
        finally:
            close(client)
        check(manifest(source) == before, 'Managed original fixture bytes remain immutable')
        proof['completed'] = True
    finally:
        proof['count'] = len(proof['checks'])
        proof['harnessSha256'] = digest(Path(__file__).read_bytes())
        proof['binaryHashes'] = {name: digest((args.host.resolve().parent / name).read_bytes()) for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    print('PASS coordinated text-list MCP:', proof['count'], 'checks; two clean offline Host lifetimes')


if __name__ == '__main__':
    main()
