"""Native probe for the block_import_xml content readback (TODO: "P3 — Content readback for block_import_xml").

Question: when block_import_xml imports SimaticML that a user supplied, how does TIA's re-export of the imported block
differ from that XML? A readback can only report `done` if those differences follow a rule that never hides a real change.

On one owned scratch (no user project is opened or changed), with a packaged bundle (--bundle):

- blocks of four kinds are created: an SCL FC and an optimized global DB through source_upsert; a LAD FC and an FBD FC from
  the repository's typed-graphical fixtures through block_import_xml (their en-US multilingual texts removed, as
  probe_followups.py does for a scratch in another language);
- round trip: each block is exported (A), A is imported again with block_import_xml, and the block is exported again (B);
- supplied text: for the fixture blocks the supplied XML (S) is compared with the export after the import (B);
- an edited supplied text: A of the SCL FC with a changed constant is imported, and B must show the change;
- every block_import_xml apply must report the worker's content readback (block-import-content) equal and its
  readbackVerified milestone done.

Every supplied and exported XML is kept under --artifacts; report.json lists, per pair, the differing XML paths, grouped
into attribute, text and structure differences. Nothing is normalized before the comparison: the point is to see which
differences TIA introduces. The report has the shape scripts/curate_evidence.py reads.
"""
import argparse
import json
import re
import uuid
from pathlib import Path
from xml.etree import ElementTree as ET

from smoke_mcp import Client, ROOT

FIXTURES = ROOT / 'fixtures/typed-graphical/native'
SCL = '''FUNCTION "RbScl" : Int
VERSION : 0.1
   VAR_INPUT
      X : Int;
   END_VAR
BEGIN
   #RbScl := #X + %d;
END_FUNCTION
'''
DB = '''DATA_BLOCK "RbDb"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   STRUCT
      Count : Int := 3;
      Flag : Bool;
      Ratio : Real := 1.5;
   END_STRUCT;
BEGIN
END_DATA_BLOCK
'''


def strip(tag):
    return tag.rsplit('}', 1)[-1]


def differences(left, right, limit=400):
    """Element-by-element comparison in document order; returns (kind, path, left, right) tuples."""
    out = []

    def walk(a, b, path):
        if len(out) >= limit:
            return
        here = f'{path}/{strip(a.tag)}'
        if strip(a.tag) != strip(b.tag):
            out.append(('structure', here, strip(a.tag), strip(b.tag)))
            return
        for key in sorted(set(a.attrib) | set(b.attrib)):
            if a.attrib.get(key) != b.attrib.get(key):
                out.append(('attribute', f'{here}@{key}', a.attrib.get(key), b.attrib.get(key)))
        ta, tb = (a.text or '').strip(), (b.text or '').strip()
        if ta != tb:
            out.append(('text', here, ta[:200], tb[:200]))
        ca, cb = list(a), list(b)
        if len(ca) != len(cb):
            out.append(('structure', here + '/*', [strip(c.tag) for c in ca][:40], [strip(c.tag) for c in cb][:40]))
        for x, y in zip(ca, cb):
            walk(x, y, here)

    walk(left, right, '')
    return out


def summarize(diffs):
    groups = {}
    for kind, path, a, b in diffs:
        generic = re.sub(r'\d+', 'N', path)
        groups.setdefault(f'{kind} {generic}', []).append({'path': path, 'supplied': a, 'exported': b})
    return {key: {'count': len(values), 'examples': values[:3]} for key, values in sorted(groups.items())}


def without_texts(xml):
    return re.sub(r'<MultilingualText [^>]*>.*?</MultilingualText>', '', xml, flags=re.S)


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
    client = Client(host, 900,
                    ['--worker', str(worker),
                     '--sim-worker', str(sim_worker), '--artifacts', str(root)])
    report = {'completed': False, 'binaries': {'host': str(host), 'worker': str(worker), 'simWorker': str(sim_worker)}, 'steps': [], 'pairs': {}, 'toolCalls': client.tool_calls}

    def save_report():
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')

    def record(name, data=None):
        report['steps'].append({'name': name, 'result': data})
        save_report()
        print(name, flush=True)
        return data

    def apply(kind, plc, payload):
        prepared = client.tool('change_prepare', {'kind': kind, 'plcPath': plc, 'payload': payload})
        applied = client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                                               'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
        assert applied['state'] == 'applied', applied
        readback = next((m for m in (applied.get('progress') or {}).get('milestones', []) if m.get('milestone') == 'readbackVerified'), None)
        content = next((r for r in applied.get('readbacks') or [] if r.get('role') == 'block-import-content'), None)
        if kind == 'block_import_xml':
            # The worker's content readback (EngineeringSources.ImportedBlockDifference) must find every supplied block equal.
            assert content is not None and content['equal'] is True and readback and readback['status'] == 'done', (readback, content)
        return {'state': applied['state'], 'readbackVerified': readback, 'blockImportContent': content}

    def export(plc, name, label):
        value = client.tool('block_read', {'plcPath': plc, 'blockName': name, 'includeXml': True})
        xml = value.get('xml')
        if xml is None:
            artifacts = (value.get('export') or {}).get('artifacts') or ([value['artifact']] if value.get('artifact') else [])
            xml = Path(artifacts[0]['path']).read_text(encoding='utf-8-sig')
        (root / f'{name}.{label}.xml').write_text(xml, encoding='utf-8')
        return xml

    def compare(label, supplied, exported):
        diffs = differences(ET.fromstring(supplied.encode('utf-8')), ET.fromstring(exported.encode('utf-8')))
        report['pairs'][label] = {'differences': len(diffs), 'groups': summarize(diffs)}
        save_report()
        return len(diffs)

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                      'clientInfo': {'name': 'block-import-readback-probe', 'version': '1'}})
        client.notify('notifications/initialized')
        record('host_build_identity', client.tool('host_build_identity'))
        client.tool('tia_start')
        record('tia_create_scratch', client.tool('tia_create_scratch'))
        plc = client.tool('project_inventory')['plcs'][0]['path']

        record('source_upsert SCL FC', apply('source_upsert', plc, {'language': 'SCL', 'source': SCL % 1, 'expectedBlockNames': ['RbScl']}))
        record('source_upsert DB', apply('source_upsert', plc, {'language': 'SCL', 'source': DB, 'expectedBlockNames': ['RbDb']}))
        fixtures = {}
        for name in ('Tgp_Boolean_LAD', 'Tgp_Boolean_FBD'):
            supplied = without_texts((FIXTURES / f'{name}.xml').read_text(encoding='utf-8-sig'))
            (root / f'{name}.supplied.xml').write_text(supplied, encoding='utf-8')
            try:
                record(f'block_import_xml {name}', apply('block_import_xml', plc, {'xml': supplied, 'expectedBlockNames': [name]}))
                fixtures[name] = supplied
            except RuntimeError as error:
                record(f'block_import_xml {name} failed', str(error)[:2000])
        for name, supplied in fixtures.items():
            record(f'supplied vs export {name}', compare(f'{name}: supplied vs export', supplied, export(plc, name, 'imported')))

        for name in ['RbScl', 'RbDb', *fixtures]:
            first = export(plc, name, 'A')
            record(f'round trip import {name}', apply('block_import_xml', plc, {'xml': first, 'expectedBlockNames': [name]}))
            second = export(plc, name, 'B')
            record(f'round trip {name}', compare(f'{name}: export A vs re-export B', first, second))

        first = (root / 'RbScl.B.xml').read_text(encoding='utf-8')
        edited = first.replace('#X + 1', '#X + 2') if '#X + 1' in first else re.sub(r'(<ConstantValue>|<Token Text=")1(")?', lambda m: m.group(0).replace('1', '2'), first, count=1)
        record('edited supplied text differs', edited != first)
        record('import edited RbScl', apply('block_import_xml', plc, {'xml': edited, 'expectedBlockNames': ['RbScl']}))
        after = export(plc, 'RbScl', 'edited')
        record('edited import visible in re-export', compare('RbScl: edited supplied vs re-export', edited, after))
        report['completed'] = True
    finally:
        record('tia_detach', client.request('tools/call', {'name': 'tia_detach', 'arguments': {}}).get('structuredContent'))
        report['hostClose'] = client.close()
        save_report()
        print(('PASSED ' if report['completed'] else 'FAILED ') + str(root / 'report.json'))


if __name__ == '__main__':
    main()
