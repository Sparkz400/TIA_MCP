"""Qualify the rest of #221 natively: beside an unrelated inconsistent SCL block on the same PLC, reads keep working, unrelated
changes prepare and apply with that block covered by metadata only, automatic whole-project restore of such a change is
refused, and a change targeting the inconsistent block still refuses. Owned scratch only."""
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
    client = Client(args.host.resolve(), 180, ['--worker', str(args.worker.resolve()), '--artifacts', str(root)])
    report = {'completed': False, 'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(name, flush=True)
        return data

    def error(name, arguments):
        result = client.request('tools/call', {'name': name, 'arguments': arguments})
        assert result.get('isError'), name + ' unexpectedly succeeded'
        return (result.get('structuredContent') or {}).get('error') or {}

    def apply(prepared):
        applied = client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                                               'checkpointSave': True, 'compile': True, 'save': True})
        assert applied['state'] == 'applied' and 'changed_project_saved' in applied['stages'], applied['state']
        return applied

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'unrelated-blocks', 'version': '1'}})
        client.notify('notifications/initialized')
        started = client.tool('tia_start')
        record('owned scratch', client.tool('tia_create_scratch'))
        project = client.tool('tia_session_status')['projects'][0]
        plc = client.tool('project_inventory')['plcs'][0]['path']
        auth = root / 'authorization.json'
        auth.write_text(json.dumps(dict(ownsPortalProcess=True, creationTool='tia_create_scratch', processId=started['processId'], projectPath=project['path'])), encoding='utf-8')
        record('seed an unrelated inconsistent SCL block', subprocess.run([str(helper), 'seed', str(auth)], check=True, text=True, capture_output=True).stdout)

        blocks = client.tool('project_inventory')['blocks']
        assert any(b['name'] == 'TagScopeBroken' and b['isConsistent'] is False for b in blocks)
        main = next(b['name'] for b in blocks if b['isConsistent'] is True and b['name'] != 'TagScopeBroken')
        reads = {'project_inventory': len(blocks),
                 'software_inspect': len(client.tool('software_inspect', {'plcPath': plc, 'maxItems': 2000})['blocks']),
                 'tag_table_read': len(client.tool('tag_table_read', {'plcPath': plc, 'tagTableName': 'Signals'})['xml']),
                 'block_read ' + main: client.tool('block_read', {'plcPath': plc, 'blockName': main})['artifact']['bytes'],
                 'block_read TagScopeBroken': error('block_read', {'plcPath': plc, 'blockName': 'TagScopeBroken'}).get('code')}
        assert reads['block_read TagScopeBroken'] == 'BlockExportUnavailable', reads
        record('reads beside the inconsistent block', reads)

        first = client.tool('change_prepare', {'kind': 'tag_create', 'plcPath': plc, 'payload': {
            'tagTableName': 'Signals', 'name': 'Unrelated', 'dataTypeName': 'Bool', 'logicalAddress': '%M120.0'}})
        bystanders = first['fingerprintCoverage']['inconsistentBlocksMetadataOnly']
        assert any('TagScopeBroken' in p for p in bystanders), first['fingerprintCoverage']
        applied = apply(first)
        delta = applied['compile']['baselineComparison']
        assert delta['complete'] is True and delta['introducedErrorCount'] == 0 and delta['existingCount'] >= 1, delta
        record('an unrelated tag_create applies beside the inconsistent block', {'inconsistentBlocksMetadataOnly': bystanders, 'compilerSucceeded': applied['compile']['compilerSucceeded'],
                                                                              'existingCount': delta['existingCount']})
        refused = error('change_restore', {'changeId': first['changeId'], 'previewHash': first['previewHash']})
        assert refused.get('code') == 'ScopedRecoveryRequired' and refused['details']['recoveryArchive'], refused
        record('whole-project restore of such a change is refused', refused)
        second = client.tool('change_prepare', {'kind': 'source_upsert', 'plcPath': plc, 'payload': {'language': 'SCL', 'expectedBlockNames': ['Unrelated_FC'],
                              'source': 'FUNCTION "Unrelated_FC" : Bool\nVERSION : 0.1\nBEGIN\n #Unrelated_FC := "Unrelated";\nEND_FUNCTION\n'}})
        apply(second)
        record('a second unrelated change applies in the same session', {'changeId': second['changeId']})

        target = error('change_prepare', {'kind': 'source_upsert', 'plcPath': plc, 'payload': {'language': 'SCL', 'expectedBlockNames': ['TagScopeBroken'],
                       'source': 'FUNCTION "TagScopeBroken" : Bool\nVERSION : 0.1\nBEGIN\n #TagScopeBroken := FALSE;\nEND_FUNCTION\n'}})
        assert target.get('code') == 'FingerprintUnavailable', target
        record('a change targeting the inconsistent block still refuses', target)
        report['completed'] = True
    finally:
        record('owned worker shutdown', client.close())
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
