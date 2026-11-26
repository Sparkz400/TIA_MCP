"""Native qualification of the project lifecycle tools and the provisioning recovery tools on owned scratch projects only
(no user project is opened or changed). Rows: project_save, project_close, project_archive, project_open_copy (X01) and
simulation_provision_status, simulation_provision_recover (L03).

One Host from a packaged bundle (--bundle) starts an owned Portal and creates a scratch:

- project_save with nothing to save (saved false); an unsaved edit (project_simulation_enable applied with save false)
  is refused by project_archive, project_close and project_open_copy (UnsavedProject); project_save then saves it, and
  a second project_save writes nothing;
- project_archive in both modes, an unknown mode refused; the files TIA's Project.Archive writes into the open project's
  folder are recorded, the inventory is unchanged and the project has nothing unsaved afterwards (nothing was saved);
- project_open_copy of the read-write scratch (archiveAndRetrieve): the copy is open and primary, the files written in the
  original folder are recorded; an unsaved edit in the copy refuses project_close until discardUnsaved; the original is reopened read-only,
  project_archive on it is attempted (TIA refuses archiving a secondary project), and project_open_copy takes the
  closeAndCopyFolder route; the copy's inventory equals the original's;
- project_retrieve of the compressed archive in the then empty Portal, with the same inventory;
- the scratch reopened read-write; a fresh owned PLCSIM instance is downloaded and provisioned WITHOUT TLS trust, so the
  attempt is held: simulation_provision_status lists it, project_save is refused (ResourceBusy) and
  simulation_provision_recover detaches, starts, reconciles and reopens the scratch;
- with --trust-owned-empty-cpu-certificate (the user's explicit first-use authorization for fresh owned empty instances)
  a second fresh instance is provisioned successfully and simulation_provision_status lists the completed attempt.

The report is written in the shape scripts/curate_evidence.py reads (completed, steps, toolCalls). Artifacts stay under
the repository's artifacts/ directory.
"""
import argparse
import hashlib
import json
import uuid
from pathlib import Path

from smoke_mcp import Client, ROOT


def manifest(folder):
    """Relative path -> SHA-256 of every file in a project folder."""
    return {path.relative_to(folder).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(Path(folder).rglob('*')) if path.is_file()}


def changed(before, after):
    return {'changed': sorted(k for k in before.keys() & after.keys() if before[k] != after[k]),
            'added': sorted(after.keys() - before.keys()), 'removed': sorted(before.keys() - after.keys())}


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
    parser.add_argument('--trust-owned-empty-cpu-certificate', action='store_true')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    if not root.is_relative_to((ROOT / 'artifacts').resolve()):
        raise SystemExit('Artifacts must stay under the repository artifacts directory.')
    root.mkdir(parents=True, exist_ok=False)
    host, worker, sim_worker = binaries(args)
    host_args = ['--worker', str(worker),
                 '--sim-worker', str(sim_worker), '--artifacts', str(root)]
    client = Client(host, 900, host_args)
    report = {'completed': False, 'binaries': {'host': str(host), 'worker': str(worker), 'simWorker': str(sim_worker)}, 'steps': [], 'toolCalls': client.tool_calls}

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

    def refused(step, name, arguments, *codes):
        value, failed = raw(name, arguments)
        code = value.get('error', {}).get('code')
        assert failed and code in codes, f'{step}: expected {codes}, got {value}'
        return record(step, {'code': code, 'message': value['error'].get('message')})

    def change(kind, payload, save):
        prepared = client.tool('change_prepare', {'kind': kind, 'payload': payload})
        return client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                                            'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': save})

    def inventory_shape():
        inventory = client.tool('project_inventory')
        return {'plcs': sorted(p['path'] for p in inventory.get('plcs', [])),
                'blocks': sorted(b['name'] for b in inventory.get('blocks', [])),
                'devices': sorted(d.get('name', '') for d in inventory.get('devices', []))}

    instance_ids = []
    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                      'clientInfo': {'name': 'platform-lifecycle-qualification', 'version': '1'}})
        client.notify('notifications/initialized')
        identity = record('host_build_identity', client.tool('host_build_identity'))
        assert identity['runningMatchesDisk'] is True, identity
        started = record('tia_start', client.tool('tia_start'))
        assert started['ownsPortalProcess'] is True
        scratch = record('tia_create_scratch', client.tool('tia_create_scratch'))
        project = client.tool('tia_session_status')['projects'][0]
        project_path = Path(project['path'])
        folder = project_path.parent
        cpus = (scratch.get('scratchSecurity') or {}).get('cpus') or []
        credential = cpus[0].get('credentialMetadataPath') if cpus else None
        report['scratch'] = {'project': str(project_path), 'credentialMetadataPath': credential}
        original = record('inventory original', inventory_shape())

        # project_save: nothing to save, then an unsaved edit refused elsewhere and saved here.
        first = record('project_save first', client.tool('project_save'))
        if first['saved']:
            first = record('project_save nothing to save', client.tool('project_save'))
        assert first['saved'] is False and first['modifiedBefore'] is False and first['isModifiedAfter'] is False, first
        applied = record('project_simulation_enable save false', change('project_simulation_enable', {'enabled': True}, False))
        assert applied['state'] == 'applied', applied
        refused('project_archive unsaved', 'project_archive', {}, 'UnsavedProject')
        refused('project_close unsaved', 'project_close', {}, 'UnsavedProject')
        refused('project_open_copy unsaved', 'project_open_copy', {}, 'UnsavedProject')
        saved = record('project_save with edits', client.tool('project_save'))
        assert saved['saved'] is True and saved['modifiedBefore'] is True and saved['isModifiedAfter'] is False, saved
        again = record('project_save after save', client.tool('project_save'))
        assert again['saved'] is False, again

        # project_archive: both modes, unknown mode refused. TIA's Project.Archive writes into the open project's folder
        # (observed: the .ap21, XRef.db, ProjectInfo.txt, System/PEData.*), so byte equality of the folder is recorded,
        # not required; the content check is the inventory and that the project has nothing unsaved and was not saved.
        before = manifest(folder)
        compressed = record('project_archive compressed', client.tool('project_archive', {'mode': 'compressed'}))
        assert compressed['archived'] is True and compressed['projectSaved'] is False, compressed
        small = record('project_archive discardRestorableDataAndCompressed',
                       client.tool('project_archive', {'mode': 'discardRestorableDataAndCompressed'}))
        assert small['archived'] is True, small
        refused('project_archive unknown mode', 'project_archive', {'mode': 'zip'}, 'InvalidRequest')
        for entry in (compressed, small):
            archive = Path(entry['archive']['path'])
            assert archive.is_file() and hashlib.sha256(archive.read_bytes()).hexdigest() == entry['archive']['sha256']
            assert archive.resolve().is_relative_to(root), archive
        record('project folder files written by Project.Archive', changed(before, manifest(folder)))
        assert record('inventory after archives', inventory_shape()) == original
        unchanged = record('project_save after archives', client.tool('project_save'))
        assert unchanged['modifiedBefore'] is False and unchanged['saved'] is False, unchanged
        before = manifest(folder)

        # project_open_copy, read-write source: archive, close, retrieve; the copy is open, the original unchanged.
        copy = record('project_open_copy read-write', client.tool('project_open_copy'))
        assert copy['copyRoute'] == 'archiveAndRetrieve' and copy['readOnly'] is False and copy['temporaryCopy'] is True, copy
        copy_path = Path(copy['project']['path'])
        assert copy_path != project_path and copy_path.resolve().is_relative_to(root), copy_path
        assert record('inventory read-write copy', inventory_shape()) == original
        record('original folder files written by project_open_copy (read-write)', changed(before, manifest(folder)))
        before = manifest(folder)
        # An unsaved edit in the copy: project_close refuses it until discardUnsaved.
        applied = change('project_simulation_enable', {'enabled': False}, False)
        record('copy edit save false', applied.get('state'))
        refused('project_close copy unsaved', 'project_close', {}, 'UnsavedProject')
        closed = record('project_close discardUnsaved', client.tool('project_close', {'discardUnsaved': True}))
        assert closed['closed'] is True and closed['saved'] is False and closed['unsavedEditsDiscarded'] is True and closed['openProjects'] == [], closed

        # project_open_copy, read-only source: close and copy the saved folder.
        opened = record('project_open read-only', client.tool('project_open', {'path': str(project_path), 'readOnly': True}))
        assert opened['readOnly'] is True
        record('project_archive read-only source (attempted)', raw('project_archive', {}))
        refused('project_save read-only', 'project_save', {}, 'ReadOnlyProject')
        copy = record('project_open_copy read-only', client.tool('project_open_copy'))
        assert copy['copyRoute'] == 'closeAndCopyFolder' and copy['sourceReadOnly'] is True and copy['readOnly'] is False, copy
        assert record('inventory read-only copy', inventory_shape()) == original
        closed = record('project_close clean', client.tool('project_close'))
        assert closed['closed'] is True and closed['unsavedEditsDiscarded'] is False, closed
        record('original folder files written by the read-only open and copy', changed(before, manifest(folder)))
        before = manifest(folder)

        # project_retrieve of the archive in the now empty Portal.
        retrieved = record('project_retrieve', client.tool('project_retrieve', {'archivePath': compressed['archive']['path'],
                                                                              'expectedSha256': compressed['archive']['sha256']}))
        assert retrieved['retrieved'] is True
        assert record('inventory retrieved', inventory_shape()) == original
        record('project_close retrieved', client.tool('project_close'))
        record('original folder files written by project_retrieve', changed(before, manifest(folder)))

        # Provisioning: refused TLS holds the attempt; status lists it, project_save is refused, recover reopens.
        record('project_open scratch', client.tool('project_open', {'path': str(project_path)}))
        record('sim_status', client.tool('sim_status'))
        record('simulation_discovery_baseline', raw('simulation_discovery_baseline'))
        created = client.tool('sim_create', {'cpuType': 'CPU1511'})
        instance_ids.append(created['instance']['id'])
        download_args = {'downloadTicket': created['downloadTicket'], 'credentialMetadataPath': credential}
        download = record('simulation_download', {k: v for k, v in client.tool('simulation_download', download_args).items()
                                                  if k in ('completed', 'projectModifiedAfter', 'saveRequiredBeforeProvisioning', 'nextStep')})
        if download.get('saveRequiredBeforeProvisioning'):
            record('project_save after download', client.tool('project_save'))
        value, failed = raw('simulation_provision_scratch_credential', download_args)
        record('provisioning without TLS trust', {'failed': failed, 'code': value.get('error', {}).get('code')})
        assert failed, value
        status = record('simulation_provision_status held', client.tool('simulation_provision_status', {'maxItems': 3}))
        held = [a for a in status['attempts'] if a['stage'] == 'held']
        assert held, status
        refused('project_save while held', 'project_save', {}, 'ResourceBusy')
        recovered = record('simulation_provision_recover', client.tool('simulation_provision_recover', {'admissionId': held[0]['admissionId']}))
        record('sim_release refused instance', raw('sim_release', {'instanceId': instance_ids.pop(), 'deleteOwned': True}))
        status = record('simulation_provision_status after recover', client.tool('simulation_provision_status', {'maxItems': 3}))
        assert not [a for a in status['attempts'] if a['stage'] == 'held'], status
        session = record('tia_session_status after recover', client.tool('tia_session_status'))
        assert [p for p in session['projects'] if Path(p['path']) == project_path], session
        record('recover summary', {k: recovered.get(k) for k in recovered if k in ('recovered', 'reopened', 'stages', 'project')})

        if args.trust_owned_empty_cpu_certificate:
            created = client.tool('sim_create', {'cpuType': 'CPU1511'})
            instance_ids.append(created['instance']['id'])
            download_args = {'downloadTicket': created['downloadTicket'], 'credentialMetadataPath': credential}
            download = client.tool('simulation_download', download_args)
            if download.get('saveRequiredBeforeProvisioning'):
                record('project_save after second download', client.tool('project_save'))
            provisioned = record('provisioning with first-use trust', client.tool('simulation_provision_scratch_credential',
                                                                                 {**download_args, 'trustOwnedEmptyCpuCertificate': True}))
            status = record('simulation_provision_status after success', client.tool('simulation_provision_status', {'maxItems': 3}))
            assert not [a for a in status['attempts'] if a['stage'] == 'held'], status
            record('provisioned summary', {k: provisioned.get(k) for k in ('completed', 'provisioned', 'certificate') if k in provisioned})
            record('sim_release provisioned instance', raw('sim_release', {'instanceId': instance_ids.pop(), 'deleteOwned': True}))
        report['completed'] = True
    finally:
        for instance_id in instance_ids:
            record('cleanup sim_release', raw('sim_release', {'instanceId': instance_id, 'deleteOwned': True}))
        record('tia_detach', raw('tia_detach'))
        report['hostClose'] = client.close()
        save_report()
        print(('PASSED ' if report['completed'] else 'FAILED ') + str(root / 'report.json'))


if __name__ == '__main__':
    main()
