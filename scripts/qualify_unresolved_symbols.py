"""Qualify #215 natively: an inconsistent SCL block with an undefined tag is inspected without changing it, the compiler's
unresolved symbols are listed with their location, and an explicit tag import repairs it. Owned scratch only."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid

from smoke_mcp import Client, ROOT

TABLE = '''<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Repair</Name></AttributeList>
<ObjectList><SW.Tags.PlcTag ID="1" CompositionName="Tags"><AttributeList><Name>UnresolvedFlag</Name><DataTypeName>Bool</DataTypeName>
<LogicalAddress>%M90.0</LogicalAddress></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>'''


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

    def apply(prepared):
        return client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                                            'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})

    def broken():
        return next(b for b in client.tool('project_inventory')['blocks'] if b['name'] == 'TagScopeBroken')

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'unresolved-symbols', 'version': '1'}})
        client.notify('notifications/initialized')
        started = client.tool('tia_start')
        assert record('owned start', started)['ownsPortalProcess'] is True
        record('owned scratch', client.tool('tia_create_scratch'))
        project = client.tool('tia_session_status')['projects'][0]
        plc = client.tool('project_inventory')['plcs'][0]['path']
        auth = root / 'authorization.json'
        auth.write_text(json.dumps(dict(ownsPortalProcess=True, creationTool='tia_create_scratch',
                                        processId=started['processId'], projectPath=project['path'])), encoding='utf-8')
        record('seed an SCL block using an undefined tag', subprocess.run([str(helper), 'seed', str(auth)], check=True, text=True, capture_output=True).stdout)
        assert broken()['isConsistent'] is False

        result = client.request('tools/call', {'name': 'block_read', 'arguments': {'plcPath': plc, 'blockName': 'TagScopeBroken'}})
        value = result.get('structuredContent') or {}
        if result.get('isError'):
            error = value['error']
            assert error['code'] == 'BlockExportUnavailable' and error['details']['generatedSourceAvailable'] is True, error
            record('XML export refused with the generated-source fallback', error)
        else:
            record('XML export of the inconsistent SCL block', {'bytes': value['artifact']['bytes']})

        generated = client.tool('block_read', {'plcPath': plc, 'blockName': 'TagScopeBroken', 'format': 'generatedSource'})
        source = Path(next(a['path'] for a in generated['export']['artifacts'] if str(a['path']).endswith('.scl'))).read_text(encoding='utf-8-sig')
        assert '"UnresolvedFlag"' in source, source
        assert broken()['isConsistent'] is False, 'Reading the source changed the block'
        record('generated source read without changing the block', {'containsSymbol': True, 'removedExternalSources': generated['export']['removedExternalSources']})

        prepared = client.tool('change_prepare', {'kind': 'plc_compile', 'plcPath': plc, 'payload': {}})
        compiled = apply(prepared)
        symbols = compiled['compile']['unresolvedSymbols']
        record('plc_compile lists the unresolved symbol', symbols)
        assert any(s['name'] == 'UnresolvedFlag' and s['kind'] == 'Tag' and any('TagScopeBroken' in p for p in s['location']) for s in symbols), symbols

        # The repair symbol is an explicit, journaled change with a user-confirmed type and address; nothing is guessed.
        prepared = client.tool('change_prepare', {'kind': 'tag_table_import_xml', 'plcPath': plc, 'payload': {'xml': TABLE, 'expectedTableNames': ['Repair']}})
        repaired = apply(prepared)
        delta = repaired['compile']['baselineComparison']
        record('explicit tag import repairs the block', {'journalPath': repaired['journalPath'], 'compilerSucceeded': repaired['compile']['compilerSucceeded'],
                                                         'resolvedCount': delta.get('resolvedCount'), 'unresolvedSymbols': repaired['compile']['unresolvedSymbols']})
        assert repaired['state'] == 'applied' and repaired['compile']['unresolvedSymbols'] == [] and delta['resolvedCount'] >= 1, delta
        assert broken()['isConsistent'] is True
        assert Path(repaired['journalPath']).is_file()
        report['completed'] = True
    finally:
        record('owned worker shutdown', client.close())
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
