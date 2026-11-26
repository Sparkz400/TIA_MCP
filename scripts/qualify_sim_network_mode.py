"""Native qualification of sim_network_mode_set (L03) on this PC's PLCSIM Advanced V8 installation.

PLCSIM's network mode is MACHINE-WIDE. Run this only with the user's agreement to switch it and with no PLCSIM instance
registered. One Host from a packaged bundle (--bundle); only the simulator worker is used:

- sim_status records the original networkMode (must be Softbus here) and that no instance is registered;
- refusals that change nothing: no acknowledgement (AcknowledgementRequired), a wrong expectedCurrentMode
  (SimulationNetworkModeStale), an unknown mode (InvalidRequest), each followed by sim_status showing the same mode;
- the same mode as now is left alone (changed false);
- the switch to TCPIPSingleAdapter with acknowledgeMachineWide and expectedCurrentMode: readbackVerified, the time the
  call took, and sim_status tcpIpReady immediately afterwards (whether a Runtime Manager restart was needed);
- sim_create with communicationInterface TCPIP succeeds in that mode; while it is registered a switch back is refused
  with SimulationRegistrationsPresent naming it; the instance is released with deleteOwned;
- --final-mode decides where the run ends: Softbus (default; the original mode is restored through the tool and read
  back) or TCPIPSingleAdapter (left for a following TCP/IP run, which must restore Softbus itself). On any failure the
  original mode is restored.

Adapters, IP addresses and certificate stores are never changed. The process runs as the current (standard) user; the
administrator leg of the TODO is not part of this run. The report has the shape scripts/curate_evidence.py reads.
"""
import argparse
import json
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
    parser.add_argument('--final-mode', choices=['Softbus', 'TCPIPSingleAdapter'], default='Softbus')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    if not root.is_relative_to((ROOT / 'artifacts').resolve()):
        raise SystemExit('Artifacts must stay under the repository artifacts directory.')
    root.mkdir(parents=True, exist_ok=False)
    host, worker, sim_worker = binaries(args)
    client = Client(host, 120,
                    ['--worker', str(worker),
                     '--sim-worker', str(sim_worker), '--artifacts', str(root)])
    report = {'completed': False, 'binaries': {'host': str(host), 'worker': str(worker), 'simWorker': str(sim_worker)}, 'finalModeRequested': args.final_mode, 'steps': [], 'toolCalls': client.tool_calls}

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

    def mode():
        return client.tool('sim_status')['networkMode']

    def refused(step, arguments, code):
        value, failed = raw('sim_network_mode_set', arguments)
        assert failed and value.get('error', {}).get('code') == code, f'{step}: {value}'
        current = mode()
        record(step, {'error': value['error'], 'modeAfter': current})
        return current

    original = None
    instance_id = None
    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                      'clientInfo': {'name': 'sim-network-mode-qualification', 'version': '1'}})
        client.notify('notifications/initialized')
        identity = record('host_build_identity', client.tool('host_build_identity'))
        assert identity['runningMatchesDisk'] is True, identity
        status = record('sim_status original', client.tool('sim_status'))
        original = status['networkMode']
        assert original == 'Softbus' and not status['instances'], status

        assert refused('refused without acknowledgement', {'mode': 'TCPIPSingleAdapter', 'expectedCurrentMode': original}, 'AcknowledgementRequired') == original
        assert refused('refused with a stale expectedCurrentMode', {'mode': 'TCPIPSingleAdapter', 'acknowledgeMachineWide': True,
                                                                     'expectedCurrentMode': 'TCPIPMultipleAdapter'}, 'SimulationNetworkModeStale') == original
        assert refused('refused unknown mode', {'mode': 'Ethernet', 'acknowledgeMachineWide': True}, 'InvalidRequest') == original
        same = record('same mode left alone', client.tool('sim_network_mode_set', {'mode': original, 'acknowledgeMachineWide': True,
                                                                                   'expectedCurrentMode': original}))
        assert same['changed'] is False and same['mode'] == original, same

        clock = time.monotonic()
        switched = client.tool('sim_network_mode_set', {'mode': 'TCPIPSingleAdapter', 'acknowledgeMachineWide': True, 'expectedCurrentMode': original})
        record('switch to TCPIPSingleAdapter', {**switched, 'callSeconds': round(time.monotonic() - clock, 3)})
        assert switched['changed'] is True and switched['readbackVerified'] is True and switched['bindingsChanged'] is False, switched
        status = record('sim_status after switch', client.tool('sim_status'))
        assert status['networkMode'] == 'TCPIPSingleAdapter' and status['tcpIpReady'] is True, status
        record('runtime manager restart needed', False)

        created = record('sim_create TCPIP', client.tool('sim_create', {'cpuType': 'CPU1511', 'communicationInterface': 'TCPIP'}))
        instance_id = created['instance']['id']
        value, failed = raw('sim_network_mode_set', {'mode': 'Softbus', 'acknowledgeMachineWide': True, 'expectedCurrentMode': 'TCPIPSingleAdapter'})
        record('refused while an instance is registered', value)
        assert failed and value['error']['code'] == 'SimulationRegistrationsPresent', value
        assert [i for i in value['error']['details']['registeredInstances'] if i['id'] == instance_id], value
        assert mode() == 'TCPIPSingleAdapter'
        record('sim_release TCPIP instance', client.tool('sim_release', {'instanceId': instance_id, 'deleteOwned': True}))
        instance_id = None

        if args.final_mode == 'Softbus':
            restored = record('restore Softbus', client.tool('sim_network_mode_set', {'mode': 'Softbus', 'acknowledgeMachineWide': True,
                                                                                      'expectedCurrentMode': 'TCPIPSingleAdapter'}))
            assert restored['changed'] is True and restored['readbackVerified'] is True, restored
            status = record('sim_status restored', client.tool('sim_status'))
            assert status['networkMode'] == original and status['tcpIpReady'] is False, status
        report['completed'] = True
    finally:
        if instance_id is not None:
            record('cleanup sim_release', raw('sim_release', {'instanceId': instance_id, 'deleteOwned': True}))
        if original is not None and not report['completed']:
            try:
                current = mode()
                if current != original:
                    record('failure restore', raw('sim_network_mode_set', {'mode': original, 'acknowledgeMachineWide': True, 'expectedCurrentMode': current}))
            except Exception as error:  # Report it; the operator must restore the mode in the PLCSIM Control Panel.
                record('failure restore failed', repr(error))
        try:
            report['finalMode'] = mode()
        except Exception as error:
            report['finalMode'] = repr(error)
        report['hostClose'] = client.close()
        save_report()
        print(('PASSED ' if report['completed'] else 'FAILED ') + str(root / 'report.json') + ' finalMode=' + str(report['finalMode']))


if __name__ == '__main__':
    main()
