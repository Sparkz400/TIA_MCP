"""Measure how long a PLCSIM Advanced V8 instance's virtual memory card folder stays locked after power-off.

Credential provisioning moves the card folder (SIMATIC_MC) of an owned, powered-off but still registered instance with
Directory.Move and retries access-denied refusals for 15 s (SimulationCardMoveRetry). On 26 November 2026 three moves
were refused for the whole budget with no Restart Manager holder. This probe isolates that: per cycle it powers an owned
fresh instance on and off through sim_state, then renames SIMATIC_MC to a sibling and back, retrying each rename every
100 ms for up to --budget seconds, and records the time to success, the Windows error and the PLCSIM processes running.

Only instances this run creates (sim_create) and their own storage folders are touched; nothing is downloaded or
provisioned and no TIA Portal is started. The report has the shape scripts/curate_evidence.py reads.
"""
import argparse
import json
import os
import subprocess
import time
from pathlib import Path

from smoke_mcp import Client, ROOT


def plcsim_processes():
    out = subprocess.run(['tasklist', '/FO', 'CSV', '/NH'], capture_output=True, text=True, errors='replace').stdout
    rows = [line.split('","') for line in out.splitlines() if 'Simulation' in line or 'S7' in line or 'Plcsim' in line]
    return sorted({(r[0].strip('"'), r[1]) for r in rows if len(r) > 1})


def timed_rename(source, destination, budget):
    start, attempts, last = time.monotonic(), 0, None
    while True:
        attempts += 1
        try:
            os.rename(source, destination)
            return {'ok': True, 'attempts': attempts, 'ms': round((time.monotonic() - start) * 1000), 'lastError': last}
        except OSError as error:
            last = {'errno': error.errno, 'winerror': getattr(error, 'winerror', None), 'message': str(error)[:200]}
            if time.monotonic() - start >= budget:
                return {'ok': False, 'attempts': attempts, 'ms': round((time.monotonic() - start) * 1000), 'lastError': last,
                        'processes': plcsim_processes()}
            time.sleep(0.1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--cycles', type=int, default=10)
    parser.add_argument('--budget', type=float, default=120.0, help='Seconds each rename is retried.')
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--sim-worker', type=Path, default=ROOT / 'src/TiaMcp.Simulator.V8/bin/Debug/net48/TiaMcp.Simulator.V8.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    if not root.is_relative_to((ROOT / 'artifacts').resolve()):
        raise SystemExit('Artifacts must stay under the repository artifacts directory.')
    root.mkdir(parents=True, exist_ok=False)
    client = Client(args.host.resolve(), 300, ['--worker', str(args.worker.resolve()), '--sim-worker', str(args.sim_worker.resolve()),
                                               '--artifacts', str(root)])
    report = {'completed': False, 'budgetSeconds': args.budget, 'cycles': [], 'steps': [], 'toolCalls': client.tool_calls}

    def save():
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')

    instance_id = None
    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'card-lock-probe', 'version': '1'}})
        client.notify('notifications/initialized')
        report['steps'].append({'name': 'host_build_identity', 'result': client.tool('host_build_identity')})
        for cycle in range(args.cycles):
            created = client.tool('sim_create', {'cpuType': 'CPU1511'})
            instance_id = created['instance']['id']
            storage = Path(created['instance']['storagePath'])
            row = {'cycle': cycle, 'instance': created['instance']['name']}
            client.tool('sim_state', {'instanceId': instance_id, 'action': 'powerOn'})
            client.tool('sim_state', {'instanceId': instance_id, 'action': 'powerOff'})
            card = storage / 'SIMATIC_MC'
            row['cardExists'] = card.is_dir()
            if card.is_dir():
                moved = storage / 'card-lock-probe'
                row['away'] = timed_rename(card, moved, args.budget)
                if row['away']['ok']:
                    row['back'] = timed_rename(moved, card, args.budget)
            client.tool('sim_release', {'instanceId': instance_id, 'deleteOwned': True})
            instance_id = None
            report['cycles'].append(row)
            save()
            print(cycle, json.dumps({k: (v if not isinstance(v, dict) else {x: v.get(x) for x in ('ok', 'attempts', 'ms')}) for k, v in row.items()}), flush=True)
        away = [c['away'] for c in report['cycles'] if 'away' in c]
        report['summary'] = {'cycles': len(report['cycles']), 'firstTry': sum(1 for a in away if a['ok'] and a['attempts'] == 1),
                             'delayed': [a['ms'] for a in away if a['ok'] and a['attempts'] > 1],
                             'neverWithinBudget': sum(1 for a in away if not a['ok'])}
        report['completed'] = True
    finally:
        if instance_id is not None:
            client.request('tools/call', {'name': 'sim_release', 'arguments': {'instanceId': instance_id, 'deleteOwned': True}})
        report['hostClose'] = client.close()
        save()
        print(('PASSED ' if report['completed'] else 'FAILED ') + str(root / 'report.json'), json.dumps(report.get('summary')))


if __name__ == '__main__':
    main()
