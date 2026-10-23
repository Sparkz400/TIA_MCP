"""Actual prior-producer v1 set bytes and mixed v1/v2 evolution through fresh Hosts."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path

from smoke_mcp import Client, ROOT
from hmi_coordinated_generation_checks import verify as verify_v1
from hmi_set_evolution_checks import verify, local_reader


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, default=ROOT / 'fixtures/hmi-evolution/retained-v1-sets')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    fixture, output = args.fixture.resolve(), args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    source = json.loads((fixture / 'fixture.json').read_text(encoding='utf-8'))
    digest = lambda raw: hashlib.sha256(raw).hexdigest()
    proof = dict(completed=False, nativeExecuted=False, producer=source['producer'], checks=[], shutdowns=[], evolvedRecords=[])
    artifacts = output / 'store'
    def check(value, label):
        assert value, label
        proof['checks'].append(label)
    for row in source['files']:
        raw = (fixture / 'store' / row['path']).read_bytes()
        check(len(raw) == row['bytes'] and digest(raw) == row['sha256'], 'Original actual-producer byte')
        path = artifacts / row['path']; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(raw)
    read = local_reader(artifacts)
    def retained(identity, suffix):
        return json.loads(read(dict(path='hmi-component-reviews/' + identity + suffix, sha256=identity)))
    def upload(client, value):
        raw = value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=False).encode('utf-8')
        begin = client.tool('artifact_upload_begin', dict(fileName='actual-prior.json', expectedBytes=len(raw), sha256=digest(raw)))
        for offset in range(0, len(raw), begin['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=begin['uploadId'], offset=offset,
                data=base64.b64encode(raw[offset:offset + begin['maxChunkBytes']]).decode()))
        return begin['uploadId'], client.tool('artifact_upload_commit', dict(uploadId=begin['uploadId']))['artifactRef']
    try:
        for lifetime in range(2):
            client = Client(args.host.resolve(), 180, ['--no-native-workers', '--artifacts', str(artifacts)])
            try:
                client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='actual-prior-hmi-sets', version='2')))
                client.notify('notifications/initialized')
                for record in source['records']:
                    receipt = client.tool('hmi_text_list_generate_set', dict(recordId=record['recordId']))
                    check(receipt == record['receipt'], 'Exact actual prior v1 set receipt lifetime ' + str(lifetime))
                    verify_v1(json.loads(read(receipt['input'])), receipt, read)
                    check(True, 'Independent full original v1 source and YAML oracle')
                    for child in receipt['outputs']:
                        identity = child['authorRecordId']
                        original = retained(identity, '.author.json')
                        author = client.tool('hmi_text_list_author', dict(recordId=identity))
                        check(author == dict(**original, recordId=identity, recordArtifact=dict(path='hmi-component-reviews/' + identity + '.author.json', sha256=identity)),
                              'Exact actual prior original child author response')
                if lifetime == 0:
                    prior = source['records'][1]['receipt']
                    original_set = json.loads(read(prior['input']))
                    component = retained(prior['componentReviewId'], '.json')
                    packet = json.loads(read(component['input']))
                    packet['beforeComponent'] = copy.deepcopy(prior['outputs'][0]['componentContext']['afterComponent']['selection'])
                    packet['afterComponent'] = copy.deepcopy(packet['beforeComponent'])
                    packet['files'] = []
                    upload_ids = []
                    prior_by_id = {o['outputId']: o for o in prior['outputs']}
                    for output_row in packet['outputs']:
                        identity = output_row['id']
                        source_files = [f for f in prior['files' if identity in prior_by_id else 'preservedFiles'] if f['outputId'] == identity]
                        ids = []
                        for i, f in enumerate(source_files):
                            raw = read(f['artifact'])
                            upload_id, reference = upload(client, raw); upload_ids.append(upload_id)
                            file_id = identity + '-' + str(i); ids.append(file_id)
                            packet['files'].append(dict(id=file_id, fileName=f['fileName'], bytes=len(raw), sha256=digest(raw), uploadRef=reference))
                        output_row.update(current=ids, currentComplete=True, proposed=[], proposedComplete=False)
                        if identity in prior_by_id:
                            output_row.update(baseline=ids, baselineComplete=True, action='update')
                            binding = next(b for b in packet['bindings'] if b['outputId'] == identity)
                            binding['beforeMember'] = copy.deepcopy(prior_by_id[identity]['componentContext']['binding']['selection']['afterMember'])
                            binding['afterMember'] = copy.deepcopy(binding['beforeMember'])
                    upload_id, reference = upload(client, packet); upload_ids.append(upload_id)
                    component_receipt = client.tool('hmi_component_review', dict(uploadRef=reference))
                    for upload_id in upload_ids:
                        client.tool('artifact_upload_release', dict(uploadId=upload_id))
                    request = dict(schemaVersion='unified-text-list-set-request.v2', componentReviewId=component_receipt['recordId'],
                        previousRecordId=prior['recordId'], outputs=[], removeOutputIds=[], retainOutputIds=original_set['retainOutputIds'],
                        requireObservedDependencies=False)
                    for row in original_set['outputs']:
                        selected = {k: copy.deepcopy(row[k]) for k in ('outputId', 'bindingId', 'profile', 'template')}
                        selected.update(previousOutputId=row['outputId'], operation='update', bindingAction='preserve',
                                        listName=prior_by_id[row['outputId']]['intent']['lists'][0]['name'])
                        request['outputs'].append(selected)
                    for transition in ('actual v1 adoption', 'v2 explicit rename after actual v1'):
                        upload_id, reference = upload(client, request)
                        evolved = client.tool('hmi_text_list_generate_set', dict(uploadRef=reference))
                        check(evolved['status'] == 'candidatesReady', transition)
                        verify(request, evolved, read)
                        client.tool('artifact_upload_release', dict(uploadId=upload_id))
                        proof['evolvedRecords'].append(dict(request=copy.deepcopy(request), receipt=evolved))
                        request['previousRecordId'] = evolved['recordId']
                        request['outputs'][0].update(operation='rename', listName='ActualPriorRenamed')
                        request['outputs'][0]['template'].update(fileBase='ActualPriorRenamed', textLibrary='ActualPriorRenamedTexts')
                else:
                    for row in proof['evolvedRecords']:
                        actual = client.tool('hmi_text_list_generate_set', dict(recordId=row['receipt']['recordId']))
                        check(actual == row['receipt'], 'Exact mixed actual-prior v1/v2 chain after restart and release')
                        verify(row['request'], actual, read)
                for row in source['files']:
                    check(digest((artifacts / row['path']).read_bytes()) == row['sha256'], 'All original copied prior bytes unchanged')
            finally:
                shutdown = client.close(); proof['shutdowns'].append(shutdown)
                check(shutdown['hostExitCode'] == 0 and not shutdown['forcedHostStop'] and not shutdown['workerExits'], 'Clean offline prior compatibility Host')
        for row in source['files']:
            check(digest((fixture / 'store' / row['path']).read_bytes()) == row['sha256'], 'Original prior fixture remains immutable')
        proof['completed'] = True
    finally:
        proof['count'] = len(proof['checks']); proof['recordCount'] = len(source['records'])
        proof['fixtureManifestSha256'] = digest((fixture / 'fixture.json').read_bytes())
        proof['binaryHashes'] = {n: digest((args.host.resolve().parent / n).read_bytes()) for n in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    print('PASS actual prior v1 sets and mixed v1/v2 evolution:', proof['recordCount'], 'original sets;', proof['count'], 'checks')


if __name__ == '__main__':
    main()
