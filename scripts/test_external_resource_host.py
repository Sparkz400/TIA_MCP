"""Read external-resource journals through the real offline Host; never dispatch a provider."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError
from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--fixture', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    artifacts = Path(fixture['artifactRoot'])
    report = {'completed': False, 'nativeExecuted': False, 'checks': [], 'shutdowns': []}
    def check(ok, label):
        assert ok, label
        report['checks'].append(label)
    validators = {}
    for name in ('engineering-operation', 'engineering-external-observation'):
        schema = json.loads((ROOT / ('contracts/' + name + '.v1.schema.json')).read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema)
        validators[name] = Draft202012Validator(schema)
    def rejected(value, name, label):
        try: validators[name].validate(value)
        except ValidationError: check(True, label)
        else: raise AssertionError(label)
    def hashes():
        return {str(p.relative_to(artifacts)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in artifacts.rglob('*') if p.is_file() and (p.suffix == '.json' or p.name == 'provider-calls.txt')}
    original = hashes()
    snapshots = {}
    def status(client, key): return client.tool('engineering_operation_status', {'operationId': fixture[key]})
    try:
        for lifetime in range(2):
            client = Client(args.host.resolve(), 30, ['--artifacts', str(artifacts), '--worker', str(output/'absent.exe'), '--sim-worker', str(output/'absent-sim.exe')])
            try:
                client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'external-resource-offline', 'version': '1'}})
                client.notify('notifications/initialized')
                check('engineering_operation_status' in {t['name'] for t in client.request('tools/list')['tools']}, 'Existing status tool registered')
                for key, state in [('legacyId','prepared'), ('reconciledId','reconciled'), ('successId','succeeded'), ('pendingId','outcomeUnknown')]:
                    record = status(client, key)
                    validators['engineering-operation'].validate(record)
                    check(record['state'] == state, 'Exact persisted status ' + key)
                    if lifetime:
                        check(record == snapshots[key], 'Restart preserves entire record ' + key)
                    else:
                        snapshots[key] = record
                        (output/(key+'.json')).write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8',newline='\n')
                check('external' not in snapshots['legacyId']['plan']['resources'][0], 'Legacy wire shape preserved')
                external = snapshots['successId']['plan']['resources'][1]
                check(external['resourceKey'] == fixture['externalKey'] and external['external']['before'] != external['external']['after'], 'External identity and exact before/after state retained')
                if not lifetime:
                    for change in ('missing', 'unknown', 'local', 'nullPair', 'strategy', 'claim'):
                        bad = copy.deepcopy(snapshots['successId'])
                        resource = bad['plan']['resources'][1]
                        if change == 'missing': del resource['external']
                        elif change == 'unknown': resource['external']['before']['presence'] = 'unreadable'
                        elif change == 'local': resource['kind'] = 'project'
                        elif change == 'nullPair': resource['external']['identity']['resourceKind'] = None
                        elif change == 'strategy': resource['recoveryStrategy'] = 'projectArchiveToNewLocation'
                        else: resource['external']['nativeQualified'] = True
                        rejected(bad, 'engineering-operation', 'Strict journal schema refuses ' + change)
                    for name in ('baseline', 'outcome'):
                        ref = fixture[name]
                        path = artifacts/ref['relativePath']
                        observation = json.loads(path.read_text(encoding='utf-8'))
                        validators['engineering-external-observation'].validate(observation)
                        check(True, 'Typed ' + name + ' satisfies schema')
                        for field, value in [('phase','restoreSucceeded'), ('nativeQualified',True), ('revision',-1), ('state',{'presence':'unknown','sha256':'a'*64})]:
                            bad = copy.deepcopy(observation)
                            bad[field] = value
                            rejected(bad, 'engineering-external-observation', 'Strict observation schema refuses ' + field)
                        state_path = artifacts/observation['stateArtifact']['relativePath']
                        for target in (path,state_path):
                            raw = target.read_bytes()
                            try:
                                target.write_bytes(b'corrupted')
                                result = client.request('tools/call', {'name':'engineering_operation_status','arguments':{'operationId':fixture['successId'] if name == 'outcome' else fixture['reconciledId']}})
                                check(result.get('isError') and result['structuredContent']['error']['code'] == 'ArtifactChanged', 'Host refuses corrupted ' + name + ' ' + ('wrapper' if target == path else 'provider bytes'))
                            finally: target.write_bytes(raw)
                        check(status(client, 'successId' if name == 'outcome' else 'reconciledId') == snapshots['successId' if name == 'outcome' else 'reconciledId'], 'Restored proof reopens without replay')
            finally:
                closed = client.close()
                report['shutdowns'].append(closed)
                check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit with no native workers')
        check(hashes() == original, 'All journal, observation and provider bytes unchanged across Host lifetimes')
        check((artifacts/'provider-calls.txt').read_text().splitlines() == ['one synthetic mutation'], 'Status never repeats the separate fixture-provider call')
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name:hashlib.sha256((args.host.parent/name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll','TiaMcp.Core.dll')}
        (output/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8',newline='\n')
    print(f"PASS external resource MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__': main()
