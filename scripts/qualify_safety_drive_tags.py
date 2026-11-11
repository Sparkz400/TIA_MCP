"""Qualify #212 natively: safety_drive_tags creates a G120C PN's PROFIsafe telegram 30 S_ZSW1/S_STW1 safety tags on the owning F-CPU 1511F-1 PN
at the addresses Startdrive reports, while generic tag routes keep refusing them. The telegram is inserted by the native fixture
helper (drive_telegram_set refuses safety telegrams). With the two real safety tags in place, tag_set, tag_delete and
tag_create batches (payload.items) that touch one of them or its address must refuse as a whole and change no tag.
Owned scratch only; never a user project, no download."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    helper = ROOT / 'scripts/native/TagImportFixture/bin/Debug/net48/TagImportFixture.exe'
    client = Client(args.host.resolve(), 300, ['--worker', str(args.worker.resolve()), '--artifacts', str(root)])
    report = {'completed': False, 'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(name, flush=True)
        return data

    def change(kind, payload, plc=None, compile=True):
        prepared = client.tool('change_prepare', {'kind': kind, 'payload': payload, **({'plcPath': plc} if plc else {})})
        applied = client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                                               'checkpointSave': True, 'compile': compile, 'save': compile})
        assert applied['state'] == 'applied', applied['state']
        return applied

    def port(device):
        rows = sorted((r for r in client.tool('hardware_inspect', {'maxItems': 2000})['items']
                       if r['path'].startswith(device + '/') and r.get('network', {}).get('nodes')), key=lambda r: r['path'])
        return rows[0]

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'safety-drive-tags', 'version': '1'}})
        client.notify('notifications/initialized')
        started = client.tool('tia_start')
        record('owned scratch', client.tool('tia_create_scratch'))
        project = client.tool('tia_session_status')['projects'][0]
        devices = '/projects/%s/devices/' % project['name']
        cpus = [e for e in client.tool('hardware_catalog_search', {'filter': '1511F-1 PN', 'maxItems': 200})['entries']
                if '1511F-1 PN' in (e.get('typeName') or '') and '6ES7 ' in (e.get('articleNumber') or '')]
        fcpu = sorted(cpus, key=lambda e: e['typeIdentifier'])[-1]['typeIdentifier']
        change('hardware_add', {'typeIdentifier': fcpu, 'deviceName': 'FStation', 'itemName': 'FPlc', 'configureScratchSecurity': True, 'failsafeRuntimeAccess': True})
        fplc = next(p['path'] for p in client.tool('project_inventory', {'maxItems': 2000})['plcs'] if '/FStation/' in p['path'])
        drives = [e for e in client.tool('hardware_catalog_search', {'filter': 'G120C PN', 'maxItems': 200})['entries']
                  if 'G120C PN' in (e.get('typeName') or '') and e.get('typeIdentifier', '').startswith('OrderNumber:')]
        drive = sorted(drives, key=lambda e: [int(x) for x in e['typeIdentifier'].rsplit('/', 1)[1].split('.') if x.isdigit()])[-1]['typeIdentifier']
        record('types', {'fcpu': fcpu, 'drive': drive})
        controller = port(devices + 'FStation')
        change('network_configure', {'hardwarePath': controller['path'], 'nodeName': controller['network']['nodes'][0]['name'], 'subnetName': 'SafeNet',
                                     'attributes': {'Address': '192.0.2.10', 'SubnetMask': '255.255.255.0'}})
        controller = port(devices + 'FStation')
        change('hardware_add', {'typeIdentifier': drive, 'deviceName': 'SafeDrive', 'itemName': 'SafeDriveItem'}, compile=False)
        p = port(devices + 'SafeDrive')
        change('network_configure', {'hardwarePath': p['path'], 'nodeName': p['network']['nodes'][0]['name'], 'subnetName': 'SafeNet',
                                     'attributes': {'Address': '192.0.2.21', 'SubnetMask': '255.255.255.0'}}, compile=False)
        p = port(devices + 'SafeDrive')
        change('network_io_assign', {'hardwarePath': p['path'], 'connectorIndex': 0, 'controllerHardwarePath': controller['path'], 'controllerIndex': 0,
                                     'createIoSystemName': 'SafeIoSystem'}, plc=fplc)
        record('inventory before safety telegram', client.tool('drive_inventory'))
        auth = root / 'authorization.json'
        auth.write_text(json.dumps(dict(ownsPortalProcess=True, creationTool='tia_create_scratch', processId=started['processId'],
                                        projectPath=project['path'], driveDevice='SafeDrive', safetyTelegram=30)), encoding='utf-8')
        out = subprocess.run([str(helper), 'safety-telegram', str(auth)], text=True, capture_output=True)
        record('fixture inserts PROFIsafe telegram 30', {'exit': out.returncode, 'stdout': out.stdout[-2000:]})
        assert out.returncode == 0, out.stdout
        inventory = record('inventory after safety telegram', client.tool('drive_inventory'))
        safe = [t for d in inventory['drives'] for o in d['driveObjects'] for t in o['telegrams'] if t['number'] == 30]
        assert len(safe) == 1 and inventory['writeReady'] is True, (safe, inventory['blockers'])
        ranges = {a['direction']: a['startAddress'] for a in safe[0]['addresses']}
        drive_path = next(d['hardwarePath'] for d in inventory['drives'])
        drive_object = next(o for d in inventory['drives'] for o in d['driveObjects'])
        handle = {'driveObjectNumber': drive_object['driveObjectNumber']} if drive_object['driveObjectNumber'] is not None else {'driveObjectIndex': drive_object['driveObjectIndex']}

        def refused(kind, payload, code):
            result = client.request('tools/call', {'name': 'change_prepare', 'arguments': {'kind': kind, 'plcPath': fplc, 'payload': payload}})
            error = (result.get('structuredContent') or {}).get('error') or {}
            assert result.get('isError') and error.get('code') == code, error
            return record('%s refused with %s' % (kind, code), error)
        # The default tag table's name follows the TIA installation language, so the run uses its own table.
        table = 'SafeDriveTags'
        change('tag_table_create', {'tagTableName': table}, plc=fplc)
        refused('tag_create', {'tagTableName': table, 'name': 'Generic_S_ZSW1', 'dataTypeName': 'Word', 'logicalAddress': '%%IW%d' % ranges['Input']}, 'SafetyBoundary')
        tags = {'status': 'SafeDrive_S_ZSW1', 'control': 'SafeDrive_S_STW1'}
        payload = {'hardwarePath': drive_path, **handle, 'tagTableName': table, 'tags': tags}
        refused('safety_drive_tags', payload, 'InvalidRequest')
        applied = change('safety_drive_tags', {**payload, 'safetyAcknowledged': True}, plc=fplc)
        plan = applied['preview']['safetyDriveTags']
        record('safety_drive_tags applied', {'plan': plan, 'stages': applied['stages'], 'compilerSucceeded': applied['compile']['compilerSucceeded'],
                                             'baseline': applied['compile']['baselineComparison'].get('introducedErrorCount')})
        assert 'safety_drive_tags_verified' in applied['stages'] and 'changed_project_saved' in applied['stages'], applied['stages']
        status = record('change_status confirms the applied change', client.tool('change_status', {'changeId': applied['changeId']}))
        assert status['state'] == 'applied' and status['stages'] == applied['stages'], status['state']
        def tag_rows():
            return {t['name']: (t['logicalAddress'], t['dataType'], t['isSafety'])
                    for row in client.tool('software_inspect', {'plcPath': fplc, 'maxItems': 2000})['tagTables'] for t in row['tags']}
        rows = tag_rows()
        got = {n: rows[n] for n in tags.values()}
        record('safety tags read back', got)
        assert got == {tags['status']: ('%%IW%d' % ranges['Input'], 'Word', True), tags['control']: ('%%QW%d' % ranges['Output'], 'Word', True)}, got

        # payload.items beside real safety tags: one item on a safety tag, or on its address, refuses the whole batch.
        standard = lambda name, bit: {'tagTableName': table, 'name': name, 'dataTypeName': 'Bool', 'logicalAddress': '%%M10.%d' % bit}
        batch = change('tag_create', {'items': [standard('StdA', 0), standard('StdB', 1)]}, plc=fplc)
        before = tag_rows()
        record('a tag_create batch of two standard tags applied beside the safety tags', {'stages': batch['stages'], 'tags': before})
        assert before['StdA'] == ('%M10.0', 'Bool', False) and before['StdB'] == ('%M10.1', 'Bool', False) and all(before[n][2] for n in tags.values()), before
        edit = lambda member, **attributes: {'tagTableName': table, 'memberName': member, 'attributes': attributes}
        drop = lambda member: {'tagTableName': table, 'memberName': member, 'dependencyPolicy': 'rejectReferenced'}
        batches = [
            ('tag_set', 'renames a safety tag', [edit('StdA', Name='StdA_R'), edit(tags['status'], Name='Renamed_S_ZSW1')]),
            ('tag_set', 'moves a standard tag onto the safety telegram', [edit('StdA', Name='StdA_R'), edit('StdB', LogicalAddress='%%I%d.0' % ranges['Input'])]),
            ('tag_delete', 'deletes a safety tag', [drop('StdB'), drop(tags['control'])]),
            ('tag_create', 'creates a tag on the safety telegram', [standard('StdC', 2), {'tagTableName': table, 'name': 'Generic_S_STW1', 'dataTypeName': 'Word', 'logicalAddress': '%%QW%d' % ranges['Output']}]),
        ]
        for kind, label, items in batches:
            result = client.request('tools/call', {'name': 'change_prepare', 'arguments': {'kind': kind, 'plcPath': fplc, 'payload': {'items': items}}})
            error = (result.get('structuredContent') or {}).get('error') or {}
            record('%s batch whose second item %s refused' % (kind, label), error)
            assert result.get('isError') and error.get('code') == 'SafetyBoundary' and error.get('message', '').startswith('items[1]'), error
            assert tag_rows() == before, 'A refused batch changed a tag'
        record('every tag is unchanged after the refused batches', before)
        refused('safety_drive_tags', {**payload, 'safetyAcknowledged': True}, 'ObjectExists')
        restored = client.tool('change_restore', {'changeId': applied['changeId'], 'previewHash': applied['previewHash']})
        record('restore from the checkpoint', {'state': restored['state'], 'recoveredProject': restored.get('recoveredProject')})
        assert restored['state'] == 'restored', restored['state']
        report['completed'] = True
    finally:
        record('owned worker shutdown', client.close())
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
