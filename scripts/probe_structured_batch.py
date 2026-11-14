"""Native probe: graphical_operand_set with items on an owned scratch.

Imports two copies of a typed LAD fixture block (Tgp_Boolean_LAD, titles and comments stripped so any project language
accepts them), then changes the contact operand of Access 21 in network 3 from A to Inhibit in both blocks with one
change. Checks one checkpoint/compile/save, a structured readback per item and the operand in each block afterwards, and
that two items naming one block are refused before anything is written. No other project is opened.
"""
import argparse
import json
import re
import uuid
from pathlib import Path

from smoke_mcp import Client, ROOT

FIXTURE = ROOT / 'fixtures/typed-graphical/native/6es7-513-1am03-0ab0-v4-1/Tgp_Boolean_LAD.xml'


def block_xml(name, number):
    text = FIXTURE.read_text(encoding='utf-8-sig')
    text = re.sub(r'<MultilingualText [^>]*>.*?</MultilingualText>', '', text, flags=re.S)
    text = text.replace('<Name>Tgp_Boolean_LAD</Name>', '<Name>%s</Name>' % name, 1).replace('<Number>2</Number>', '<Number>%d</Number>' % number, 1)
    assert '<Name>%s</Name>' % name in text
    return text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    client = Client(args.host.resolve(), 900, ['--worker', str(args.worker.resolve()), '--artifacts', str(root)])
    report = {'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')
        print(name, flush=True)
        return data

    def apply(prepared):
        return client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                                            'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})

    def operand(plc, name):
        read = client.tool('block_read', {'plcPath': plc, 'blockName': name})
        xml = Path(read['artifact']['path']).read_text(encoding='utf-8-sig')
        access = re.search(r'<Access Scope="LocalVariable" UId="21">\s*<Symbol>\s*<Component Name="([^"]+)"', xml)
        return access.group(1) if access else None

    try:
        client.tool('tia_start')
        record('owned scratch', client.tool('tia_create_scratch')['stages'])
        plc = client.tool('project_inventory')['plcs'][0]['path']
        for name, number in (('BatchLadA', 701), ('BatchLadB', 702)):
            done = apply(client.tool('change_prepare', {'kind': 'block_import_xml', 'plcPath': plc,
                                                        'payload': {'xml': block_xml(name, number), 'expectedBlockNames': [name]}}))
            record('import ' + name, done['state'])
        record('operands before', {n: operand(plc, n) for n in ('BatchLadA', 'BatchLadB')})

        item = lambda name: {'blockName': name, 'networkId': '3', 'accessUid': '21', 'components': ['Inhibit']}
        result = client.request('tools/call', {'name': 'change_prepare', 'arguments': {'kind': 'graphical_operand_set', 'plcPath': plc,
                                                                                       'payload': {'items': [item('BatchLadA'), item('BatchLadA')]}}})
        error = (result.get('structuredContent') or {}).get('error') or {}
        record('same block twice is refused', {'isError': result.get('isError'), 'code': error.get('code'), 'message': (error.get('message') or '')[:200]})

        prepared = client.tool('change_prepare', {'kind': 'graphical_operand_set', 'plcPath': plc,
                                                  'payload': {'items': [item('BatchLadA'), item('BatchLadB')]}})
        applied = apply(prepared)
        record('batch applied', {'state': applied['state'], 'stages': applied.get('stages'),
                                 'readbacks': [r for r in applied.get('readbacks', []) if r.get('role') == 'structured-edit-readback'].__len__(),
                                 'compilerSucceeded': (applied.get('compile') or {}).get('compilerSucceeded')})
        record('operands after', {n: operand(plc, n) for n in ('BatchLadA', 'BatchLadB')})
        client.tool('tia_detach', {'waitForExitSeconds': 60})
        report['completed'] = True
    finally:
        report['exit'] = client.close()
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')
    return 0 if report.get('completed') else 1


if __name__ == '__main__':
    raise SystemExit(main())
