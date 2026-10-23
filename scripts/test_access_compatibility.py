"""Reopen original access records produced before the shared-reader extraction."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    fixture = ROOT / 'fixtures/access-review/retained-v1'
    manifest = json.loads((fixture / 'fixture.json').read_text(encoding='utf-8'))
    digest = lambda raw: hashlib.sha256(raw).hexdigest()
    for row in manifest['files']:
        raw = (fixture / 'store' / row['path']).read_bytes()
        assert digest(raw) == row['sha256'] and len(raw) == row['bytes']
    artifacts = output / 'store'
    shutil.copytree(fixture / 'store', artifacts)
    report = dict(completed=False, nativeExecuted=False, producer=manifest['producer'], checks=[], shutdowns=[])
    def check(ok, name):
        assert ok, name
        report['checks'].append(name)
    try:
        for lifetime in range(2):
            client = Client(args.host.resolve(), 60, ['--artifacts', str(artifacts), '--worker', str(output / 'absent.exe'), '--sim-worker', str(output / 'absent.exe')])
            try:
                client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'prior-access-records', 'version': '1'}})
                client.notify('notifications/initialized')
                for record in manifest['records']:
                    check(client.tool('access_review_page', {'recordId': record['recordId']}) == record['summary'], 'Exact prior summary: ' + str(lifetime))
                    for section, page in record['pages'].items():
                        check(client.tool('access_review_page', {'recordId': record['recordId'], 'section': section, 'count': 200}) == page, 'Exact prior page ' + section)
                    for key in ('input', 'reportArtifact'):
                        reference = record['summary'][key]
                        raw = bytearray()
                        while True:
                            part = client.tool('artifact_read', {'path': reference['path'], 'offset': len(raw), 'count': 4096})
                            data = base64.b64decode(part['data'], validate=True)
                            assert part['offset'] == len(raw) and part['sha256'] == reference['sha256'] and part['count'] == len(data)
                            raw.extend(data)
                            if part['complete']:
                                break
                            assert data and part['nextOffset'] == len(raw)
                        check(bytes(raw) == (fixture / 'store' / reference['path']).read_bytes(), 'Original artifact bytes preserved: ' + key)
                for row in manifest['files']:
                    check(digest((artifacts / row['path']).read_bytes()) == row['sha256'], 'Reopening did not rewrite ' + row['path'])
            finally:
                closed = client.close()
                report['shutdowns'].append(closed)
                check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host lifetime without native worker')
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['binaryHashes'] = {name: digest((args.host.parent / name).read_bytes()) for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('PASS actual prior access compatibility:', report['count'], 'checks')


if __name__ == '__main__':
    main()
