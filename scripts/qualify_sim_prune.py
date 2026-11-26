"""Native qualification of sim_prune (L03) against a live PLCSIM Advanced V8 installation, on instances this run creates.

One Host from a packaged bundle (--bundle); only the simulator worker is used (no TIA Portal, no project, no download):

- five fresh owned instances A..E are created; A, B, C and E are released with deleteOwned (unregistered, storage left),
  D stays registered and selected;
- D is powered on and off once (a never-powered card cannot be archived: -37 ArchiveStorageNotCreated), then sim_storage
  archives its memory card twice; the second archive is then changed by one byte;
- C's ownership marker is changed by one byte and B's files are dated three days back (only folders this run created);
  a directory junction is put inside E's folder;
- the storage listing must mark A and B eligible and refuse C (ownershipMarkerChanged), D (registeredOrSelected) and E
  (containsReparsePoint); delete by name deletes A and refuses C and D; delete by age (olderThanDays 1) selects and
  deletes only B; delete by size cap (maxTotalMegabytes 0) selects nothing ineligible; combining names with an age and
  delete without a selector are refused;
- the archive listing marks the unchanged archive eligible and refuses the changed one; delete by name deletes the
  first and refuses the second;
- D is released with deleteOwned and then deleted by name; every remaining folder of this run is listed at the end.

Nothing outside the Host's artifacts directory is touched. The report has the shape scripts/curate_evidence.py reads.
"""
import argparse
import json
import os
import subprocess
import time
from pathlib import Path

from smoke_mcp import Client, ROOT


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
    args = parser.parse_args()
    root = args.artifacts.resolve()
    if not root.is_relative_to((ROOT / 'artifacts').resolve()):
        raise SystemExit('Artifacts must stay under the repository artifacts directory.')
    root.mkdir(parents=True, exist_ok=False)
    host, worker, sim_worker = binaries(args)
    client = Client(host, 600,
                    ['--worker', str(worker),
                     '--sim-worker', str(sim_worker), '--artifacts', str(root)])
    report = {'completed': False, 'binaries': {'host': str(host), 'worker': str(worker), 'simWorker': str(sim_worker)}, 'steps': [], 'toolCalls': client.tool_calls}
    storage = root / 'simulation'

    def save_report():
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')

    def record(name, data=None):
        report['steps'].append({'name': name, 'result': data})
        save_report()
        print(name, flush=True)
        return data

    def raw(name, arguments=None):
        result = client.request('tools/call', {'name': name, 'arguments': arguments or {}})
        return (result.get('structuredContent') or {}), bool(result.get('isError'))

    def refused(step, name, arguments, code):
        value, failed = raw(name, arguments)
        assert failed and value.get('error', {}).get('code') == code, f'{step}: {value}'
        return record(step, value['error'])

    def by_name(listing):
        return {c['name']: c for c in listing['candidates']}

    live = {}
    junction = None
    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                      'clientInfo': {'name': 'sim-prune-qualification', 'version': '1'}})
        client.notify('notifications/initialized')
        identity = record('host_build_identity', client.tool('host_build_identity'))
        assert identity['runningMatchesDisk'] is True, identity
        status = record('sim_status', client.tool('sim_status'))
        assert not status['instances'], 'run only with no registered PLCSIM instance'
        names = {}
        for label in 'ABCDE':
            created = client.tool('sim_create', {'cpuType': 'CPU1511'})
            names[label] = created['instance']['name']
            live[label] = created['instance']['id']
        record('created', names)
        for label in 'ABCE':
            record(f'sim_release {label}', client.tool('sim_release', {'instanceId': live.pop(label), 'deleteOwned': True}))

        # A never-powered instance has no card content: ArchiveStorage fails with -37 ArchiveStorageNotCreated.
        record('sim_state D powerOn', client.tool('sim_state', {'instanceId': live['D'], 'action': 'powerOn'}))
        record('sim_state D powerOff', client.tool('sim_state', {'instanceId': live['D'], 'action': 'powerOff'}))
        first = record('sim_storage archive D',client.tool('sim_storage', {'instanceId': live['D'], 'action': 'archive'}))
        second = record('sim_storage archive D again', client.tool('sim_storage', {'instanceId': live['D'], 'action': 'archive'}))
        good, changed = Path(first['archive']['archivePath']), Path(second['archive']['archivePath'])
        assert good.resolve().is_relative_to(root) and changed.resolve().is_relative_to(root)
        with open(changed, 'r+b') as stream:
            stream.seek(0, os.SEEK_END); stream.write(b'\0')

        marker = storage / names['C'] / '.tiamcp-instance.json'
        marker.write_bytes(marker.read_bytes() + b' ')
        old = time.time() - 3 * 86400
        for path in [*(storage / names['B']).rglob('*'), storage / names['B']]:
            os.utime(path, (old, old))
        junction = storage / names['E'] / 'held-link'
        target = root / 'junction-target'
        target.mkdir()
        subprocess.run(['cmd', '/c', 'mklink', '/J', str(junction), str(target)], check=True, capture_output=True)
        record('prepared folders', {'markerChanged': names['C'], 'datedBack': names['B'], 'junction': names['E'], 'archiveChanged': changed.name})

        listing = record('sim_prune storage list', client.tool('sim_prune', {'scope': 'storage'}))
        candidates = by_name(listing)
        assert listing['delete'] is False
        assert candidates[names['A']]['eligible'] and candidates[names['B']]['eligible'], candidates
        assert candidates[names['C']]['refusal'] == 'ownershipMarkerChanged', candidates[names['C']]
        assert candidates[names['D']]['refusal'] == 'registeredOrSelected', candidates[names['D']]
        assert candidates[names['E']]['refusal'] == 'containsReparsePoint', candidates[names['E']]
        assert (storage / names['A']).is_dir(), 'listing deleted something'

        refused('names with age refused', 'sim_prune', {'scope': 'storage', 'names': [names['A']], 'olderThanDays': 1, 'delete': True}, 'InvalidRequest')
        refused('delete without selector refused', 'sim_prune', {'scope': 'storage', 'delete': True}, 'InvalidRequest')

        deleted = record('sim_prune delete by name', client.tool('sim_prune', {'scope': 'storage', 'delete': True,
                                                                              'names': [names['A'], names['C'], names['D']]}))
        assert [d['name'] for d in deleted['deleted']] == [names['A']], deleted
        assert not (storage / names['A']).exists() and (storage / names['C']).is_dir() and (storage / names['D']).is_dir()

        aged = record('sim_prune delete by age', client.tool('sim_prune', {'scope': 'storage', 'delete': True, 'olderThanDays': 1}))
        assert [d['name'] for d in aged['deleted']] == [names['B']], aged
        assert not (storage / names['B']).exists()

        capped = record('sim_prune delete by size cap', client.tool('sim_prune', {'scope': 'storage', 'delete': True, 'maxTotalMegabytes': 0}))
        assert not [d for d in capped['deleted'] if d['name'] in (names['C'], names['D'], names['E'])], capped
        assert (storage / names['C']).is_dir() and (storage / names['D']).is_dir() and (storage / names['E']).is_dir()

        archives = record('sim_prune archives list', client.tool('sim_prune', {'scope': 'archives'}))
        listed = by_name(archives)
        assert listed[good.name]['eligible'] is True, listed
        assert listed[changed.name]['refusal'] == 'archiveReceiptMismatch', listed[changed.name]
        removed = record('sim_prune archives delete by name', client.tool('sim_prune', {'scope': 'archives', 'delete': True,
                                                                                       'names': [good.name, changed.name]}))
        assert [d['name'] for d in removed['deleted']] == [good.name], removed
        assert not good.exists() and changed.exists()

        record('sim_release D', client.tool('sim_release', {'instanceId': live.pop('D'), 'deleteOwned': True}))
        final = record('sim_prune delete D by name', client.tool('sim_prune', {'scope': 'storage', 'delete': True, 'names': [names['D']]}))
        assert [d['name'] for d in final['deleted']] == [names['D']], final
        record('sim_prune storage list at end', client.tool('sim_prune', {'scope': 'storage'}))
        report['completed'] = True
    finally:
        if junction is not None and junction.exists():
            os.rmdir(junction)  # Removes only the junction, never its target's content.
        for label, instance_id in live.items():
            record(f'cleanup sim_release {label}', raw('sim_release', {'instanceId': instance_id, 'deleteOwned': True}))
        report['hostClose'] = client.close()
        save_report()
        print(('PASSED ' if report['completed'] else 'FAILED ') + str(root / 'report.json'))


if __name__ == '__main__':
    main()
