"""Inspect credential-bearing operations through the real offline MCP Host; no secret or provider invocation."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError
from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    artifacts = Path(fixture['artifactRoot'])
    schemas = {name: Draft202012Validator(json.loads((ROOT / f'contracts/{name}.v1.schema.json').read_text(encoding='utf-8')))
               for name in ('engineering-operation', 'protected-credential')}
    for validator in schemas.values():
        Draft202012Validator.check_schema(validator.schema)
    report = {'completed': False, 'nativeExecuted': False, 'checks': [], 'shutdowns': []}
    def check(ok, label):
        assert ok, label
        report['checks'].append(label)
    def hashes():
        return {str(p.relative_to(artifacts)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in artifacts.rglob('*') if p.is_file() and p.name != '.lock'}
    before = hashes()
    snapshots = {}
    try:
        schemas['protected-credential'].validate(fixture['current'])
        check(len(fixture['current']['operationReferences']) == fixture.get('expectedPinCount', 4), 'Public retired status retains all operation locators')
        if 'legacyRecovery' in fixture:
            schemas['engineering-operation'].validate(fixture['legacyRecovery'])
            check('recoveryTarget' not in fixture['legacyRecovery'], 'Historical local recovery schema preserves absent context')
        for lifetime in range(2):
            client = Client(args.host.resolve(), 30, ['--artifacts', str(artifacts), '--worker', str(output/'absent.exe'), '--sim-worker', str(output/'absent-sim.exe')])
            try:
                client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'credential-operation-offline', 'version': '1'}})
                client.notify('notifications/initialized')
                if 'legacyId' in fixture:
                    legacy = client.tool('engineering_operation_status', {'operationId': fixture['legacyId']})
                    schemas['engineering-operation'].validate(legacy)
                    check(legacy['state'] == 'reconciled' and 'recoveryTarget' not in legacy, 'Host reads independently reconciled historical recovery without inventing context')
                for state, operation_id in fixture['operations'].items():
                    record = client.tool('engineering_operation_status', {'operationId': operation_id})
                    schemas['engineering-operation'].validate(record)
                    check(record['state'] == state, 'Exact retained operation state ' + state)
                    requirements = record['plan']['credentials']
                    check([r['usePhase'] for r in requirements] == ['dispatch', 'recovery'], 'Both intended credential roles survive MCP serialization')
                    check(requirements[0]['reference'] == fixture['current']['reference'], 'Exact immutable public reference exposed')
                    check('ciphertext' not in json.dumps(record), 'Operation status exposes no ciphertext')
                    if 'recoveryTarget' in fixture:
                        check(record.get('recoveryTarget') == fixture['recoveryTarget'], 'Exact independently admitted recovery target survives MCP serialization')
                        check(record['recoveryTarget']['sessionEpoch'] != record['plan']['target']['sessionEpoch'], 'Recovery retains its new epoch separately from the original plan')
                    if lifetime:
                        check(record == snapshots[state], 'Host restart preserves complete plan and credential identities')
                    else:
                        snapshots[state] = record
                        (output/f'{state}.json').write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8', newline='\n')
                if not lifetime:
                    for kind in ('empty', 'phase', 'name', 'secret', 'reference', 'binding'):
                        bad = copy.deepcopy(next(iter(snapshots.values())))
                        row = bad['plan']['credentials'][0]
                        if kind == 'empty': bad['plan']['credentials'] = []
                        elif kind == 'phase': row['usePhase'] = 'automatic'
                        elif kind == 'name': row['name'] = '../escape'
                        elif kind == 'secret': row['secret'] = 'SYNTHETIC-SURPLUS'
                        elif kind == 'reference': row['reference']['cipherSha256'] = 'invalid'
                        else: row['binding']['resource']['resourceId'] = None
                        try: schemas['engineering-operation'].validate(bad)
                        except ValidationError: check(True, 'Public schema refuses ' + kind)
                        else: raise AssertionError(kind)
                    if 'recoveryTarget' in fixture:
                        original = snapshots['reconciled']
                        for kind in ('null', 'surplus', 'state', 'plan'):
                            bad = copy.deepcopy(original)
                            if kind == 'null': bad['recoveryTarget'] = None
                            elif kind == 'surplus': bad['recoveryTarget']['authenticated'] = True
                            elif kind == 'state': bad['state'] = 'prepared'
                            else: del bad['plan']['credentials']
                            try: schemas['engineering-operation'].validate(bad)
                            except ValidationError: check(True, 'Recovery schema refuses ' + kind)
                            else: raise AssertionError(kind)
                        path = artifacts/'engineering-operations'/original['id']/f"{original['revision']:06d}.json"
                        retained = path.read_bytes()
                        try:
                            bad = json.loads(retained)
                            bad['recoveryTarget']['hardwareIdentity'] = 'wrong-owner'
                            path.write_text(json.dumps(bad), encoding='utf-8', newline='\n')
                            response = client.request('tools/call', {'name': 'engineering_operation_status', 'arguments': {'operationId': original['id']}})
                            check(response.get('isError') and 'JournalInvalid' in json.dumps(response), 'Actual Host refuses a changed retained recovery owner')
                        finally:
                            path.write_bytes(retained)
            finally:
                shutdown = client.close()
                report['shutdowns'].append(shutdown)
                check(shutdown['hostExitCode'] == 0 and not shutdown['forcedHostStop'] and shutdown['stderrEofObserved'] and not shutdown['workerExits'],
                      'Host exits normally without starting a native worker')
        check(hashes() == before, 'Status reads and restart preserve all retained journal and vault bytes')
        check(not list(output.glob('*absent*.exe')), 'No worker executable created')
        report['completed'] = True
    finally:
        (output/'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8', newline='\n')
    print(f"PASS credential operation MCP: {len(report['checks'])} checks across two Host lifetimes")


if __name__ == '__main__':
    main()
