"""Reopen exact actual prior-producer v1/v2/v3 author bytes through new Hosts."""
import argparse
import hashlib
import json
from pathlib import Path

from smoke_mcp import Client, ROOT
from hmi_text_list_author_checks import expected_documents, parse_candidate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, default=ROOT / 'fixtures/hmi-coordinated/retained-v1-v3')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    fixture, output = args.fixture.resolve(), args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    manifest = json.loads((fixture / 'fixture.json').read_text(encoding='utf-8'))
    digest = lambda raw: hashlib.sha256(raw).hexdigest()
    proof = dict(completed=False, nativeExecuted=False, producer=manifest['producer'], checks=[], shutdowns=[])
    def check(value, label):
        assert value, label
        proof['checks'].append(label)
    for row in manifest['files']:
        raw = (fixture / 'store' / row['path']).read_bytes()
        check(len(raw) == row['bytes'] and digest(raw) == row['sha256'], 'Actual prior fixture exact bytes')
        path = (output / 'store' / row['path']).resolve()
        path.relative_to((output / 'store').resolve())
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
    try:
        for lifetime in range(2):
            client = Client(args.host.resolve(), 120, ['--no-native-workers', '--artifacts', str(output / 'store')])
            try:
                client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='actual-prior-hmi-authors', version='1')))
                client.notify('notifications/initialized')
                for record in manifest['records']:
                    receipt = client.tool('hmi_text_list_author', dict(recordId=record['recordId']))
                    check(receipt == record['receipt'], 'Exact actual prior ' + record['schemaVersion'] + ' receipt, lifetime ' + str(lifetime))
                    original = json.loads((output / 'store' / receipt['input']['path']).read_bytes())
                    intent = original if receipt['schemaVersion'].endswith('.v1') else receipt['intent']
                    for file, wanted in zip(receipt['files'], expected_documents(intent)):
                        raw = (output / 'store' / file['artifact']['path']).read_bytes()
                        check(digest(raw) == file['artifact']['sha256'] and len(raw) == file['bytes'], 'Exact prior candidate bytes')
                        check(parse_candidate(raw) == wanted, 'Independent full prior YAML output tree')
                for row in manifest['files']:
                    check(digest((output / 'store' / row['path']).read_bytes()) == row['sha256'], 'New Host preserved exact prior stored byte')
            finally:
                result = client.close()
                proof['shutdowns'].append(result)
                check(result['hostExitCode'] == 0 and not result['forcedHostStop'] and not result['workerExits'], 'Clean offline prior-record Host lifetime')
        for row in manifest['files']:
            check(digest((fixture / 'store' / row['path']).read_bytes()) == row['sha256'], 'Original actual-producer fixture unchanged')
        proof['completed'] = True
    finally:
        proof['count'] = len(proof['checks'])
        proof['recordCount'] = len(manifest['records'])
        proof['fixtureManifestSha256'] = digest((fixture / 'fixture.json').read_bytes())
        proof['binaryHashes'] = {name: digest((args.host.resolve().parent / name).read_bytes()) for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    print('PASS actual prior v1/v2/v3 authors:', proof['recordCount'], 'records;', proof['count'], 'checks')


if __name__ == '__main__':
    main()
