"""Reopen exact records captured from the actual prior packaged Host, without regenerating them."""
import argparse
import base64
import hashlib
import json
import shutil
from pathlib import Path

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, default=ROOT / 'fixtures/composite-engineering/retained-v1')
    args = parser.parse_args()
    output = args.output.resolve(); assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    source = args.fixture.resolve()
    fixture = json.loads((source / 'fixture.json').read_text(encoding='utf-8'))
    digest = lambda data: hashlib.sha256(data).hexdigest()
    for row in fixture['files']:
        data = (source / 'store' / row['path']).read_bytes()
        assert len(data) == row['bytes'] and digest(data) == row['sha256']
    assert {p.relative_to(source / 'store').as_posix() for p in (source / 'store').rglob('*') if p.is_file()} == {r['path'] for r in fixture['files']}
    artifacts = output / 'store'; shutil.copytree(source / 'store', artifacts)
    records = fixture.get('records', [fixture])
    report = dict(completed=False, nativeExecuted=False, priorProducer=fixture['producer'], checks=[], shutdowns=[])
    client = None

    def check(ok, label):
        assert ok, label
        report['checks'].append(label)

    def start():
        nonlocal client
        missing = output / 'absent-native-worker.exe'
        client = Client(args.host.resolve(), 90, ['--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'prior-composite-v1', 'version': '1'}})
        client.notify('notifications/initialized')

    def stop():
        nonlocal client
        receipt = client.close(); client = None; report['shutdowns'].append(receipt)
        check(receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'] and not receipt['workerExits'], 'Clean current Host exit without native workers')

    try:
        for lifetime in range(2):
            start()
            for record in records:
                reference = record['recordRef']; folder = artifacts / 'composite-engineering-reviews' / reference.split(':')[1]
                original = json.loads((folder / 'report.json').read_bytes())
                assert original['schemaVersion'] in ('composite-engineering-review.v1', 'composite-engineering-review.v2')
                check(client.tool('composite_engineering_reopen', {'recordRef': reference}) == record['summary'], 'Actual prior-producer summary remains exact across restart: ' + str(lifetime))
                for name in ('request.json', 'report.json', 'receipt.json'):
                    retained = bytearray()
                    while True:
                        chunk = client.tool('composite_engineering_artifact_read', dict(recordRef=reference, name=name, offset=len(retained), count=8192))
                        data = base64.b64decode(chunk['data'], validate=True)
                        assert len(data) == chunk['count'] and chunk['offset'] == len(retained)
                        retained.extend(data); assert len(retained) == chunk['nextOffset']
                        if chunk['complete']:
                            assert len(retained) == chunk['bytes'] and digest(retained) == chunk['sha256']; break
                        assert data
                    check(retained == (folder / name).read_bytes(), 'Exact prior-producer bytes: ' + name)
                if lifetime == 0:
                    sections = {key: value for key, value in original.items() if isinstance(value, list)}
                    sections.update({p + 'Components': original['contexts'][p]['components'] for p in ('before', 'after')})
                    sections.update(amlChanges=original['amlComparison']['changes'], beforeAmlImpacts=original['amlComparison']['beforeImpacts'], afterAmlImpacts=original['amlComparison']['afterImpacts'])
                    if 'sections' in record: sections = {key: [r['value'] for r in rows] for key, rows in record['sections'].items()}
                    for section, expected in sections.items():
                        rows, offset = [], 0
                        while True:
                            page = client.tool('composite_engineering_page', dict(recordRef=reference, section=section, offset=offset, count=2))
                            for row in page['rows']: assert row['index'] == len(rows); rows.append(row['value'])
                            if page['nextOffset'] is None: break
                            assert page['nextOffset'] > offset; offset = page['nextOffset']
                        check(rows == expected, 'Prior producer paged array unchanged: ' + section)
                    if original['schemaVersion'] == 'composite-engineering-review.v1':
                        refused = client.request('tools/call', {'name': 'composite_engineering_page', 'arguments': dict(recordRef=reference, section='consumerCoverage')})
                        check(refused.get('isError') and refused['structuredContent']['error']['code'] == 'CompositeInvalidSection', 'Prior v1 receipt cannot be reinterpreted as v2 consumer context')
            stop()
        check(all(digest((artifacts / row['path']).read_bytes()) == row['sha256'] for row in fixture['files']), 'All prior bytes remain unchanged after current Host reads')
        report['completed'] = True
    finally:
        if client is not None: stop()
        report['count'] = len(report['checks'])
        report['fixtureManifestSha256'] = digest((source / 'fixture.json').read_bytes())
        report['harnessSha256'] = digest(Path(__file__).read_bytes())
        report['managedBinaryHashes'] = {name: digest((args.host.parent / name).read_bytes()) for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS actual prior composite producer: {report['count']} checks; two current Host lifetimes; no regenerated legacy records.")


if __name__ == '__main__':
    main()
