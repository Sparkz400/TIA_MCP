"""Actual offline MCP conversion, independent YAML/domain oracles, refusal and restart."""
import argparse
import base64
import copy
import hashlib
import json
import shutil
from pathlib import Path

from smoke_mcp import Client
from hmi_set_evolution_checks import verify as verify_evolution, local_reader
from hmi_coordinated_generation_checks import verify as verify_v1
from hmi_integer_conversion_checks import negative_controls


def verify(request, receipt, read):
    (verify_v1 if request['schemaVersion'].endswith('.v1') else verify_evolution)(request, receipt, read)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--prior-fixture', type=Path, default=Path(__file__).resolve().parent.parent / 'fixtures/hmi-conversion/retained-v2')
    args = parser.parse_args()
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    source, artifacts = Path(fixture['artifactRoot']), output / 'host-artifacts'
    digest = lambda raw: hashlib.sha256(raw).hexdigest()
    def manifest(root):
        return {p.relative_to(root).as_posix(): digest(p.read_bytes()) for p in root.rglob('*') if p.is_file()}
    original_hashes = manifest(source)
    for folder in ('project-reviews', 'hmi-component-reviews'):
        shutil.copytree(source / folder, artifacts / folder)
    prior = json.loads((args.prior_fixture / 'fixture.json').read_text(encoding='utf-8'))
    for row in prior['files']:
        raw = (args.prior_fixture / 'store' / row['path']).read_bytes()
        assert len(raw) == row['bytes'] and digest(raw) == row['sha256']
        path = artifacts / row['path']; path.parent.mkdir(parents=True, exist_ok=True)
        assert not path.exists() or path.read_bytes() == raw
        path.write_bytes(raw)
    for case in fixture['cases']:
        path = artifacts / case['receipt']['recordArtifact']['path']
        if path.exists():
            path.unlink()
    read = local_reader(artifacts)
    proof = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[], priorProducer=prior['producer'])
    def check(value, label):
        assert value, label
        proof['checks'].append(label)
    def start():
        client = Client(args.host.resolve(), 180, ['--no-native-workers', '--artifacts', str(artifacts)])
        client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='integer-conversion', version='1')))
        client.notify('notifications/initialized')
        return client
    def close(client):
        result = client.close(); proof['shutdowns'].append(result)
        check(result['hostExitCode'] == 0 and not result['forcedHostStop'] and not result['workerExits'], 'Clean offline Host lifetime')
    def upload(client, value):
        raw = value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=False).encode('utf-8')
        begin = client.tool('artifact_upload_begin', dict(fileName='conversion.json', expectedBytes=len(raw), sha256=digest(raw)))
        for offset in range(0, len(raw), begin['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=begin['uploadId'], offset=offset,
                        data=base64.b64encode(raw[offset:offset + begin['maxChunkBytes']]).decode()))
        return begin['uploadId'], client.tool('artifact_upload_commit', dict(uploadId=begin['uploadId']))['artifactRef']
    def candidates():
        return {p.name: digest(p.read_bytes()) for p in (artifacts / 'hmi-component-reviews').iterdir()
                if p.is_file() and p.name.endswith(('.author.json', '.candidate'))}
    def refuse(client, arguments, label, code):
        response = client.request('tools/call', dict(name='hmi_text_list_generate_set', arguments=arguments))
        check(response.get('isError') and response['structuredContent']['error']['code'] == code, label)
    def prior_replay(client):
        for case in prior['cases']:
            receipt = client.tool('hmi_text_list_generate_set', dict(recordId=case['receipt']['recordId']))
            check(receipt == case['receipt'], 'Actual prior v2 exact response: ' + case['label'])
            verify(case['request'], receipt, read)
        for row in prior['files']:
            raw = (artifacts / row['path']).read_bytes()
            check(len(raw) == row['bytes'] and digest(raw) == row['sha256'], 'Original prior v2 byte unchanged')
    try:
        client = start()
        try:
            prior_replay(client)
            for case in fixture['cases']:
                before = candidates(); expected = case['receipt']
                raw = read(expected['input'])
                upload_id, reference = upload(client, raw)
                receipt = client.tool('hmi_text_list_generate_set', dict(uploadRef=reference))
                check(receipt == expected, 'Exact generated result: ' + case['label'])
                verify(case['request'], receipt, read)
                check(True, 'Independent original XML/domain/full YAML/peer oracle: ' + case['label'])
                if receipt['status'] == 'conflict':
                    check(candidates() == before, 'Conflict publishes no candidates: ' + case['label'])
                client.tool('artifact_upload_release', dict(uploadId=upload_id))
            for case in fixture['refusals']:
                before = candidates(); upload_id, reference = upload(client, case['request'])
                refuse(client, dict(uploadRef=reference), 'Explicit refusal: ' + case['label'], 'InvalidHmiComponentReview')
                client.tool('artifact_upload_release', dict(uploadId=upload_id))
                check(candidates() == before, 'Invalid request publishes no candidates')
        finally:
            close(client)
        client = start()
        try:
            prior_replay(client)
            for case in fixture['cases']:
                receipt = client.tool('hmi_text_list_generate_set', dict(recordId=case['receipt']['recordId']))
                check(receipt == case['receipt'], 'Exact released reopen after restart: ' + case['label'])
                verify(case['request'], receipt, read)
            case = next(c for c in fixture['cases'] if c['label'].startswith('convert SInt to Int'))
            proof['negativeControls'] = negative_controls(case['request'], case['receipt'], read, verify)
            for field, replacement in [('status', 'conflict'), ('sourceBindingConflicts', []), ('targetMaximum', 1),
                                       ('allListedValuesRepresentable', False), ('runtimeConversionVerified', True)]:
                forged = json.loads(read(case['receipt']['recordArtifact']))
                forged['outputs'][1]['typeConversion'][field] = replacement
                raw = json.dumps(forged, ensure_ascii=False).encode('utf-8'); identity = digest(raw)
                (artifacts / 'hmi-component-reviews' / (identity + '.text-set.json')).write_bytes(raw)
                refuse(client, dict(recordId=identity), 'Honestly rehashed conversion refuses: ' + field, 'ArtifactChanged')
            for reference in (case['receipt']['input'], case['receipt']['previousRecordArtifact'], case['receipt']['files'][2]['artifact']):
                path = artifacts / reference['path']; original = path.read_bytes()
                try:
                    path.write_bytes(original + b'altered')
                    refuse(client, dict(recordId=case['receipt']['recordId']), 'Original input/predecessor/candidate tamper refuses', 'ArtifactChanged')
                finally:
                    path.write_bytes(original)
            check(client.tool('hmi_text_list_generate_set', dict(recordId=case['receipt']['recordId'])) == case['receipt'], 'Original receipt restores after tamper controls')
        finally:
            close(client)
        check(manifest(source) == original_hashes, 'Managed fixture original bytes unchanged')
        proof['completed'] = True
    finally:
        proof['count'] = len(proof['checks'])
        proof['harnessSha256'] = digest(Path(__file__).read_bytes())
        proof['binaryHashes'] = {n: digest((args.host.resolve().parent / n).read_bytes()) for n in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    print('PASS integer text-list conversion MCP:', proof['count'], 'checks; two clean offline Host lifetimes')


if __name__ == '__main__':
    main()
