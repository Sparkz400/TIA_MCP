"""#212 feasibility probe in an owned scratch: an F-CPU 1511F-1 PN as IO controller of a SINAMICS G120C PN, PROFIsafe telegram
30 inserted by the native fixture helper, then drive_inventory. Records what Startdrive and TIA report; never a user project."""
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
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'profisafe-probe', 'version': '1'}})
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
        compiled = client.request('tools/call', {'name': 'change_prepare', 'arguments': {'kind': 'plc_compile', 'plcPath': fplc, 'payload': {}}})
        record('prepare F-PLC compile', compiled.get('structuredContent'))
        report['completed'] = True
    finally:
        record('owned worker shutdown', client.close())
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
