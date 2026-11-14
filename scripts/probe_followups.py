"""Native probe of the v0.9.0 follow-ups on one owned scratch (no real project is opened or changed):

- graphical recovery: a LAD block that reads an undefined global tag is made inconsistent by the TagImportFixture
  helper (seed-lad); block_read is then tried with the default XML export, format=simaticSd and format=generatedSource;
- block_delete with items deletes two unreferenced blocks in one change;
- project_close closes the saved scratch, project_open reopens it read-only and project_snapshot_capture with
  fromTemporaryCopy captures block content from a read-write copy while the scratch stays unchanged.
"""
import argparse
import hashlib
import json
import subprocess
import uuid
from pathlib import Path

from smoke_mcp import Client, ROOT

FIXTURE = ROOT / 'fixtures/typed-graphical/native/6es7-513-1am03-0ab0-v4-1/Tgp_Boolean_LAD.xml'
BROKEN_ACCESS = '''<Access Scope="GlobalVariable" UId="21">
                  <Symbol>
                    <Component Name="NoSuchLadTag" />
                  </Symbol>
                </Access>'''


def folder_hash(folder):
    digest = hashlib.sha256()
    for path in sorted(p for p in folder.rglob('*') if p.is_file()):
        digest.update(str(path.relative_to(folder)).encode()); digest.update(path.read_bytes())
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    helper = ROOT / 'scripts/native/TagImportFixture/bin/Debug/net48/TagImportFixture.exe'
    client = Client(args.host.resolve(), 900, ['--worker', str(args.worker.resolve()), '--artifacts', str(root)])
    report = {'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')
        print(name, flush=True)
        return data

    def raw(name, arguments):
        result = client.request('tools/call', {'name': name, 'arguments': arguments})
        return result.get('structuredContent'), bool(result.get('isError'))

    def apply(prepared):
        return client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                                            'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})

    try:
        started = client.tool('tia_start')
        record('owned scratch', client.tool('tia_create_scratch'))
        project = client.tool('tia_session_status')['projects'][0]
        plc = client.tool('project_inventory')['plcs'][0]['path']

        # Blocks for the delete batch, then the broken LAD block (after them, so the batch's compile baseline is clean).
        source = '\n'.join('FUNCTION "DelProbe%d" : Bool\nVERSION : 0.1\nBEGIN\n #DelProbe%d := TRUE;\nEND_FUNCTION\n' % (i, i) for i in range(3))
        created = apply(client.tool('change_prepare', {'kind': 'source_upsert', 'plcPath': plc,
                                                       'payload': {'language': 'SCL', 'source': source, 'expectedBlockNames': ['DelProbe0', 'DelProbe1', 'DelProbe2']}}))
        record('three probe blocks created', created['state'])
        prepared = client.tool('change_prepare', {'kind': 'block_delete', 'plcPath': plc,
                                                  'payload': {'items': [{'blockName': 'DelProbe0'}, {'blockName': 'DelProbe1'}]}})
        deleted = apply(prepared)
        names = {b['name'] for b in client.tool('project_inventory')['blocks']}
        record('block_delete batch', {'state': deleted['state'], 'stages': deleted.get('stages'),
                                      'remaining': sorted(n for n in names if n.startswith('DelProbe'))})

        broken = FIXTURE.read_text(encoding='utf-8-sig').replace('''<Access Scope="LocalVariable" UId="21">
                  <Symbol>
                    <Component Name="A" />
                  </Symbol>
                </Access>''', BROKEN_ACCESS, 1)
        assert 'NoSuchLadTag' in broken
        # The fixture's titles and comments are en-US texts; a scratch project in another language refuses them on import.
        import re
        broken = re.sub(r'<MultilingualText [^>]*>.*?</MultilingualText>', '', broken, flags=re.S)
        (root / 'broken-lad.xml').write_text(broken, encoding='utf-8')
        auth = root / 'authorization.json'
        auth.write_text(json.dumps(dict(ownsPortalProcess=True, creationTool='tia_create_scratch', processId=started['processId'],
                                        projectPath=project['path'], ladBlockName='Tgp_Boolean_LAD')), encoding='utf-8')
        seeded = subprocess.run([str(helper), 'seed-lad', str(auth)], text=True, capture_output=True)
        record('seed inconsistent LAD block', {'exit': seeded.returncode, 'stdout': seeded.stdout[-3000:], 'stderr': seeded.stderr[-3000:]})
        if seeded.returncode != 0: raise RuntimeError('seed-lad failed')
        lad = next(b for b in client.tool('project_inventory')['blocks'] if b['name'] == 'Tgp_Boolean_LAD')
        record('LAD block state', {'isConsistent': lad.get('isConsistent'), 'language': lad.get('programmingLanguage')})
        for fmt in (None, 'simaticSd', 'generatedSource'):
            arguments = {'plcPath': plc, 'blockName': 'Tgp_Boolean_LAD'}
            if fmt: arguments['format'] = fmt
            value, failed = raw('block_read', arguments)
            if failed:
                record('block_read ' + (fmt or 'xml'), {'error': value['error']['code'], 'message': value['error']['message'][:600],
                                                        'details': {k: v for k, v in (value['error'].get('details') or {}).items() if k != 'exceptionType'}})
            else:
                artifacts = (value.get('export') or {}).get('artifacts') or ([value['artifact']] if value.get('artifact') else [])
                texts = []
                for artifact in artifacts:
                    path = Path(artifact['path'])
                    if path.is_file() and path.stat().st_size < 2_000_000:
                        texts.append(path.read_text(encoding='utf-8-sig', errors='replace'))
                record('block_read ' + (fmt or 'xml'), {'artifacts': [a.get('path') for a in artifacts],
                                                        'containsUnresolvedTag': any('NoSuchLadTag' in t for t in texts)})
        lad = next(b for b in client.tool('project_inventory')['blocks'] if b['name'] == 'Tgp_Boolean_LAD')
        record('LAD block unchanged by the reads', {'isConsistent': lad.get('isConsistent')})

        # Close the saved scratch, reopen it read-only and capture from a temporary copy.
        scratch = Path(project['path'])
        value, failed = raw('project_close', {})
        record('project_close refuses unsaved edits', {'refused': failed, 'code': (value or {}).get('error', {}).get('code')})
        closed = client.tool('project_close', {'discardUnsaved': True})
        record('project_close discardUnsaved', {k: closed.get(k) for k in ('closed', 'saved', 'unsavedEditsDiscarded', 'openProjects')})
        before = folder_hash(scratch.parent)
        record('reopen read-only', client.tool('project_open', {'path': str(scratch), 'readOnly': True})['readOnly'])
        value, failed = raw('project_snapshot_capture', {'request': {'maxBlocks': 10, 'maxRelationshipRoots': 0, 'fromTemporaryCopy': True}})
        if failed: record('capture from temporary copy', value)
        else:
            record('capture from temporary copy', {'summary': value.get('summary'), 'content': value['capture']['content'],
                                                   'temporaryCopy': value['capture'].get('temporaryCopy')})
        record('scratch folder unchanged', folder_hash(scratch.parent) == before)
        record('detach', client.tool('tia_detach', {'waitForExitSeconds': 60}))
        report['completed'] = True
    finally:
        report['exit'] = client.close()
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')
    return 0 if report.get('completed') else 1


if __name__ == '__main__':
    raise SystemExit(main())
