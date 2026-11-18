"""Native probe of the 18 November v0.9.0 feedback fixes on one owned scratch (no real project is opened or changed):

- scratch level: host_build_identity, project_save with nothing to save, a compile-only change and its projectSave view,
  project_inventory completeness.meaning, target_capabilities' plc_loadable_generate probe on the scratch CPU 1511,
  tia_detach's plcsimRegistrations;
- refused provisioning: a fresh owned instance is downloaded, saved with project_save and provisioned WITHOUT TLS trust,
  so TIA's certificate challenge is refused; simulation_provision_status must then show the attempt, project_save must be
  refused while it is held, and simulation_provision_recover must detach, start, reconcile and reopen the scratch.

The successful protected path (with the user's first-use TLS authorization) is scripts/validate_simulation.py
--provision-runtime-credential --trust-owned-empty-cpu-certificate --golden-path-checks on the scratch this probe leaves.
"""
import argparse
import json
import uuid
from pathlib import Path

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--sim-worker', type=Path, default=ROOT / 'src/TiaMcp.Simulator.V8/bin/Debug/net48/TiaMcp.Simulator.V8.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    host_args = ['--worker', str(args.worker.resolve()), '--sim-worker', str(args.sim_worker.resolve()), '--artifacts', str(root), '--timeout-seconds', '900']
    client = Client(args.host.resolve(), 900, host_args)
    report = {'artifacts': str(root), 'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')
        print(name, flush=True)
        return data

    def raw(name, arguments=None):
        result = client.request('tools/call', {'name': name, 'arguments': arguments or {}})
        return result.get('structuredContent'), bool(result.get('isError'))

    def code(value):
        return (value or {}).get('error', {}).get('code')

    instance_id = None
    try:
        record('host_build_identity', client.tool('host_build_identity'))
        client.tool('tia_start')
        scratch = record('tia_create_scratch', client.tool('tia_create_scratch'))
        project = client.tool('tia_session_status')['projects'][0]['path']
        cpus = (scratch.get('scratchSecurity') or {}).get('cpus') or []
        report['scratch'] = {'project': project, 'credentialMetadataPath': cpus[0].get('credentialMetadataPath') if cpus else None}
        inventory = client.tool('project_inventory')
        plc = inventory['plcs'][0]['path']
        record('project_inventory completeness', inventory.get('completeness'))
        record('project_save with nothing to save', client.tool('project_save'))
        caps = client.tool('target_capabilities', {'selectors': {'kind': 'plc', 'plcPath': plc}})
        record('target_capabilities plc_loadable_generate', [f for f in caps.get('features', []) if f.get('featureId') == 'plc_loadable_generate'])
        prepared = client.tool('change_prepare', {'kind': 'plc_compile', 'plcPath': plc, 'payload': {}})
        applied = client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                                               'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
        record('change_apply projectSave', {'state': applied.get('state'), 'projectSave': applied.get('projectSave'),
                                            'previewIsModified': (applied.get('preview') or {}).get('isModified')})
        enable = client.tool('change_prepare', {'kind': 'project_simulation_enable', 'payload': {'enabled': True}})
        enabled = client.tool('change_apply', {'changeId': enable['changeId'], 'previewHash': enable['previewHash'],
                                               'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
        record('project_simulation_enable', enabled.get('state'))

        # Refused provisioning: no TLS trust, so the certificate challenge is refused and the instance is spent.
        status = record('sim_status before', client.tool('sim_status'))
        record('simulation_discovery_baseline', raw('simulation_discovery_baseline'))
        created = client.tool('sim_create', {'cpuType': 'CPU1511'})
        instance_id = created['instance']['id']
        download_args = {'downloadTicket': created['downloadTicket'], 'credentialMetadataPath': report['scratch']['credentialMetadataPath']}
        download = client.tool('simulation_download', download_args)
        record('simulation_download save fields', {k: download.get(k) for k in ('completed', 'projectModifiedAfter', 'saveRequiredBeforeProvisioning', 'nextStep')})
        if download.get('saveRequiredBeforeProvisioning'):
            record('project_save after download', client.tool('project_save'))
        refused, failed = raw('simulation_provision_scratch_credential', download_args)
        record('provisioning without TLS trust', {'failed': failed, 'code': code(refused),
               'admission': (refused or {}).get('error', {}).get('details', {}).get('mutationAdmission')})
        status = record('simulation_provision_status', client.tool('simulation_provision_status', {'maxItems': 3}))
        held = [a for a in status['attempts'] if a['stage'] == 'held']
        if held:
            blocked, failed = raw('project_save')
            record('project_save while held', {'failed': failed, 'code': code(blocked),
                   'blockingAdmissions': (blocked or {}).get('error', {}).get('details', {}).get('blockingAdmissions')})
            recovered, failed = raw('simulation_provision_recover', {'admissionId': held[0]['admissionId']})
            record('simulation_provision_recover', {'failed': failed, 'result': recovered})
        record('sim_release refused instance', raw('sim_release', {'instanceId': instance_id, 'deleteOwned': True}))
        instance_id = None
        record('simulation_provision_status after', client.tool('simulation_provision_status', {'maxItems': 3}))
        record('tia_session_status after', client.tool('tia_session_status'))
    finally:
        if instance_id is not None:
            record('cleanup sim_release', raw('sim_release', {'instanceId': instance_id, 'deleteOwned': True}))
        record('tia_detach', raw('tia_detach'))
        client.close()
        print(root / 'report.json')


if __name__ == '__main__':
    main()
