"""Native qualification of hmi_content_fingerprint (X07/X08) on seeded HMI baselines, owned copies only.

Input: an HmiSeeder positive qualify report (scripts/native/HmiSeeder: qualify <dir> <profiles> positive), whose rows name a
retrieval-verified archive per profile with two HMIs (HMI_RT_1, HMI_RT_2), Overview_A and Overview_B. Per profile, in one
owned Portal of a Host started from a packaged bundle (--bundle), the archive is retrieved into a new directory and:

- hmi_content_fingerprint for the profile's production kind (Unified: hmi_screen_create and hmi_external_tag_create;
  Classic: hmi_classic_import with operation scalar_property_set) on HMI_RT_1: contentHash, complete, scopeAccepted,
  usableAsExpectedContentHash and missingCoverage are recorded; a second call returns the same hash; HMI_RT_2 and the other
  kind are fingerprinted too (identity and kind separation are recorded, not assumed);
- change_prepare of that kind with expectedContentHash = the fingerprint prepares without StaleRevision;
- the prepared change is applied (checkpoint, compile, save; the seeded baseline keeps its compile errors, which the kind's
  compile rule requires unchanged); the fingerprint is taken again and must differ; a second prepare bound to the OLD hash is
  refused with StaleRevision whose details carry the current contentHash, which equals the new fingerprint;
- a wrong-kind or unknown-target request is refused.

Profiles without a row in the report are skipped and listed. The report has the shape scripts/curate_evidence.py reads.
"""
import argparse
import hashlib
import json
import uuid
from pathlib import Path

from smoke_mcp import Client, ROOT
from qualify_hmi_gate_route import HMIS, classic_fixture, rows, scalar, screen_fixture, screen_payload

FAMILY = {"classic-comfort": "classic", "classic-rt-advanced": "classic", "classic-basic": "classic",
          "unified-comfort": "unified", "unified-pc": "unified"}


def binaries(args):
    """Host, engineering worker and simulator worker: a packaged bundle (--bundle) or explicit paths (Debug builds by default,
    which Register-DevOpennessWorker.ps1 keeps in Siemens' Openness AllowList)."""
    if args.bundle:
        bundle = args.bundle.resolve()
        return bundle / 'host/TiaMcp.Host.exe', bundle / 'workers/openness/TiaMcp.Openness.V21.exe', bundle / 'workers/simulator/TiaMcp.Simulator.V8.exe'
    return args.host.resolve(), args.worker.resolve(), args.sim_worker.resolve()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--bundle', type=Path, help='Packaged engineering bundle root (host/, workers/); otherwise --host/--worker/--sim-worker.')
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--sim-worker', type=Path, default=ROOT / 'src/TiaMcp.Simulator.V8/bin/Debug/net48/TiaMcp.Simulator.V8.exe')
    parser.add_argument('--seeded-report', type=Path, required=True)
    parser.add_argument('--profiles', default='unified-pc,unified-comfort,classic-comfort,classic-rt-advanced,classic-basic')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    if not root.is_relative_to((ROOT / 'artifacts').resolve()):
        raise SystemExit('Artifacts must stay under the repository artifacts directory.')
    root.mkdir(parents=True, exist_ok=False)
    seeded = json.loads(args.seeded_report.read_text(encoding='utf-8'))
    host, worker, sim_worker = binaries(args)
    client = Client(host, 1800,
                    ['--worker', str(worker),
                     '--sim-worker', str(sim_worker), '--artifacts', str(root)])
    report = {'completed': False, 'binaries': {'host': str(host), 'worker': str(worker), 'simWorker': str(sim_worker)}, 'seededReport': str(args.seeded_report.resolve()), 'profiles': {},
              'skipped': [], 'steps': [], 'toolCalls': client.tool_calls}

    def save_report():
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')

    def record(name, data=None):
        report['steps'].append({'name': name, 'result': data})
        save_report()
        print(name, flush=True)
        return data

    def raw(name, arguments):
        result = client.request('tools/call', {'name': name, 'arguments': arguments})
        return (result.get('structuredContent') or {}), bool(result.get('isError'))

    def fingerprint(device, kind, operation=None):
        request = {'target': device['identity'], 'kind': kind}
        if operation:
            request['operation'] = operation
        value = client.tool('hmi_content_fingerprint', {'request': request})
        return {k: value.get(k) for k in ('contentHash', 'complete', 'scopeAccepted', 'usableAsExpectedContentHash', 'missingCoverage')}

    def apply(prepared):
        return client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                                            'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                      'clientInfo': {'name': 'hmi-content-fingerprint-qualification', 'version': '1'}})
        client.notify('notifications/initialized')
        record('host_build_identity', client.tool('host_build_identity'))
        client.tool('tia_start')
        for profile in args.profiles.split(','):
            found = [r for r in seeded.get('profiles', []) if r.get('profile') == profile and r.get('archiveRetrievalVerified') is True]
            if not found:
                report['skipped'].append(profile)
                record(f'{profile}: no retrieval-verified seeded archive, skipped')
                continue
            family = FAMILY[profile]
            archive = Path(found[0]['archive']['path'])
            sha = hashlib.sha256(archive.read_bytes()).hexdigest()
            retrieved = record(f'{profile}: project_retrieve', client.tool('project_retrieve', {'archivePath': str(archive), 'expectedSha256': sha}))
            devices = {d['name']: d for d in client.tool('hmi_inspect', {'request': {'section': 'devices'}})['items'] if d.get('family') == family}
            assert sorted(devices) == list(HMIS), devices
            first, second = devices['HMI_RT_1'], devices['HMI_RT_2']
            row = report['profiles'][profile] = {'project': retrieved['project']['path']}
            if family == 'unified':
                kind, other, operation = 'hmi_screen_create', 'hmi_external_tag_create', None
                fx = screen_fixture(client, devices)
                payload = lambda name: screen_payload(fx['HMI_RT_1'], name)  # noqa: E731
            else:
                kind, other, operation = 'hmi_classic_import', 'hmi_classic_import', 'scalar_property_set'
                fx = classic_fixture(client, devices)
                payload = lambda value: scalar(fx['HMI_RT_1']['Overview_A'], value)  # noqa: E731
            before = row['first'] = record(f'{profile}: fingerprint {kind}', fingerprint(first, kind, operation))
            assert before['contentHash'] and before['usableAsExpectedContentHash'] is True, before
            again = row['repeat'] = record(f'{profile}: fingerprint repeated', fingerprint(first, kind, operation))
            assert again['contentHash'] == before['contentHash'], (before, again)
            row['otherHmi'] = record(f'{profile}: fingerprint HMI_RT_2', fingerprint(second, kind, operation))
            if other != kind:
                row['otherKind'] = record(f'{profile}: fingerprint {other}', fingerprint(first, other))
            row['otherHmiDiffers'] = row['otherHmi']['contentHash'] != before['contentHash']
            if 'otherKind' in row:
                row['otherKindDiffers'] = row['otherKind']['contentHash'] != before['contentHash']

            first_payload = payload('FingerprintScreenA' if family == 'unified' else '200, 210, 220')
            prepared = client.tool('change_prepare', {'kind': kind, 'payload': {**first_payload, 'expectedContentHash': before['contentHash']}})
            record(f'{profile}: prepare bound to the fingerprint', {'changeId': prepared['changeId'], 'state': prepared.get('state')})
            applied = apply(prepared)
            record(f'{profile}: apply', {'state': applied.get('state')})
            assert applied['state'] == 'applied', applied
            after = row['afterApply'] = record(f'{profile}: fingerprint after apply', fingerprint(first, kind, operation))
            assert after['contentHash'] != before['contentHash'], (before, after)
            second_payload = payload('FingerprintScreenB' if family == 'unified' else '210, 220, 230')
            value, failed = raw('change_prepare', {'kind': kind, 'payload': {**second_payload, 'expectedContentHash': before['contentHash']}})
            error = value.get('error', {})
            current = ((error.get('details') or {}).get('baseline') or {}).get('contentHash')
            row['stale'] = record(f'{profile}: prepare bound to the old hash', {'code': error.get('code'), 'currentContentHash': current})
            assert failed and error.get('code') == 'StaleRevision' and current == after['contentHash'], value
            value, failed = raw('hmi_content_fingerprint', {'request': {'target': first['identity'], 'kind': 'hmi_not_a_kind'}})
            row['unknownKind'] = record(f'{profile}: unknown kind refused', {'failed': failed, 'code': value.get('error', {}).get('code')})
            assert failed, value
            record(f'{profile}: project_close', client.tool('project_close'))
        assert report['profiles'], 'no profile was fingerprinted'
        report['completed'] = True
    finally:
        record('tia_detach', raw('tia_detach', {}))
        report['hostClose'] = client.close()
        save_report()
        print(('PASSED ' if report['completed'] else 'FAILED ') + str(root / 'report.json'))


if __name__ == '__main__':
    main()
