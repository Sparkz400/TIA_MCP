"""Native check of totalItems and pageCount on the worker-side pagers (project_model search, project_index query,
library_query, hmi_references) on owned copies only.

One Host on Debug builds (or --bundle) in an owned Portal:

- a scratch gains 23 SCL functions named PgFc01..PgFc23 in one source_upsert change;
- project_model capture, then search for "PgFc" with maxItems 5 is paged through to the end: every page carries the
  same totalItems (= totalMatches) and pageCount = ceil(totalItems / 5), the walk takes exactly pageCount pages and
  returns totalItems distinct rows; a search with no match reports 0 and 0;
- project_index capture, then query "PgFc" with maxItems 5 paged by cursor, with the same checks;
- library_open kind=project and library_query on the project library (paged with maxItems 5 when it has rows);
- with --hmi-archive (an HmiSeeder baseline .zap21), the archive is retrieved into a new directory and hmi_references of
  one HMI is paged with limit 5: totalItems and pageCount when the scope is complete, both null otherwise.

The report has the shape scripts/curate_evidence.py reads.
"""
import argparse
import hashlib
import json
import math
import uuid
from pathlib import Path

from smoke_mcp import Client, ROOT

ROW_KEYS = ('items', 'rows', 'results', 'matches', 'nodes', 'edges', 'relationships', 'references')


def rows_of(page):
    for key in ROW_KEYS:
        if isinstance(page.get(key), list):
            return key, page[key]
    raise AssertionError('no row list in page keys %s' % sorted(page))


def walk(call, size, continuation):
    """Pages through call(token) until no continuation; returns the pages and the row count."""
    pages, token, seen = [], None, 0
    while True:
        page = call(token)
        pages.append(page)
        seen += len(rows_of(page)[1])
        token = page.get(continuation)
        if token in (None, '', 0) or len(pages) > 200:
            return pages, seen


def check_totals(label, pages, seen, size, total_key):
    totals = {(p.get('totalItems'), p.get('pageCount'), p.get(total_key)) for p in pages}
    assert len(totals) == 1, (label, totals)
    total, count, own = totals.pop()
    summary = {'pages': len(pages), 'rows': seen, 'totalItems': total, 'pageCount': count, total_key: own}
    if total is None:
        assert count is None, (label, summary)
        return summary
    assert total == own and count == math.ceil(total / size) and len(pages) == max(count, 1) and seen == total, (label, summary)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--hmi-archive', type=Path)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--sim-worker', type=Path, default=ROOT / 'src/TiaMcp.Simulator.V8/bin/Debug/net48/TiaMcp.Simulator.V8.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    if not root.is_relative_to((ROOT / 'artifacts').resolve()):
        raise SystemExit('Artifacts must stay under the repository artifacts directory.')
    root.mkdir(parents=True, exist_ok=False)
    client = Client(args.host.resolve(), 900, ['--worker', str(args.worker.resolve()), '--sim-worker', str(args.sim_worker.resolve()),
                                               '--artifacts', str(root)])
    report = {'completed': False, 'checks': {}, 'steps': [], 'toolCalls': client.tool_calls}

    def save():
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')

    def record(name, data=None):
        report['steps'].append({'name': name, 'result': data})
        save()
        print(name, flush=True)
        return data

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'paging-native', 'version': '1'}})
        client.notify('notifications/initialized')
        record('host_build_identity', client.tool('host_build_identity'))
        client.tool('tia_start')
        record('tia_create_scratch', client.tool('tia_create_scratch'))
        plc = client.tool('project_inventory')['plcs'][0]['path']
        source = '\n'.join('FUNCTION "PgFc%02d" : Int\nVERSION : 0.1\nBEGIN\n   #PgFc%02d := %d;\nEND_FUNCTION\n' % (i, i, i) for i in range(1, 24))
        prepared = client.tool('change_prepare', {'kind': 'source_upsert', 'plcPath': plc,
                                                  'payload': {'language': 'SCL', 'source': source, 'expectedBlockNames': ['PgFc%02d' % i for i in range(1, 24)]}})
        applied = client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                                               'checkpointSave': True, 'compile': True, 'save': True})
        record('23 functions created', applied['state'])

        snapshot = client.tool('project_model', {'request': {'action': 'capture'}})
        sid = snapshot['snapshotId']
        pages, seen = walk(lambda token: client.tool('project_model', {'request': {'action': 'search', 'snapshotId': sid, 'text': 'PgFc', 'maxItems': 5,
                                                                                    **({'offset': token} if token else {})}}), 5, 'nextOffset')
        report['checks']['project_model search'] = record('project_model search paged', check_totals('project_model search', pages, seen, 5, 'totalMatches'))
        none = client.tool('project_model', {'request': {'action': 'search', 'snapshotId': sid, 'text': 'NoSuchName_xyz', 'maxItems': 5}})
        assert (none.get('totalItems'), none.get('pageCount')) == (0, 0), none
        report['checks']['project_model search empty'] = record('project_model search empty', {'totalItems': 0, 'pageCount': 0})
        client.tool('project_model', {'request': {'action': 'release', 'snapshotId': sid}})

        index = client.tool('project_index', {'request': {'action': 'capture', 'lifetimeSeconds': 600}})
        iid = index['indexId']
        first = client.tool('project_index', {'request': {'action': 'query', 'indexId': iid, 'text': 'PgFc', 'maxItems': 5}})
        total_key = next(k for k in ('total', 'totalMatches', 'totalRows') if k in first)
        continuation = 'nextCursor' if 'nextCursor' in first else 'nextOffset'
        pages, seen = walk(lambda token: first if token is None else client.tool('project_index', {'request': {
            'action': 'query', 'indexId': iid, 'text': 'PgFc', 'maxItems': 5, ('cursor' if continuation == 'nextCursor' else 'offset'): token}}), 5, continuation)
        report['checks']['project_index query'] = record('project_index query paged', check_totals('project_index query', pages, seen, 5, total_key))
        client.tool('project_index', {'request': {'action': 'release', 'indexId': iid}})

        library = client.tool('library_open', {'request': {'kind': 'project'}})
        lid = library['libraryId']
        first = client.tool('library_query', {'request': {'libraryId': lid, 'maxItems': 5}})
        total_key = next(k for k in ('total', 'totalItems') if k in first)
        pages, seen = walk(lambda token: first if token is None else client.tool('library_query', {'request': {
            'libraryId': lid, 'maxItems': 5, 'offset': token, 'revision': first.get('revision')}}), 5, 'nextOffset')
        report['checks']['library_query'] = record('library_query paged', check_totals('library_query', pages, seen, 5, total_key))

        if args.hmi_archive:
            client.tool('project_close', {'discardUnsaved': True})
            archive = args.hmi_archive.resolve()
            client.tool('project_retrieve', {'archivePath': str(archive), 'expectedSha256': hashlib.sha256(archive.read_bytes()).hexdigest()})
            device = next(d for d in client.tool('hmi_inspect', {'request': {'section': 'devices'}})['items'] if d.get('name') == 'HMI_RT_1')
            def references(token):
                return client.tool('hmi_references', {'request': {'schemaVersion': 1, 'target': device['identity'], 'limit': 5, 'maxVisited': 50000,
                                                                   **({'offset': token} if token else {})}})
            first = references(None)
            total_key = next((k for k in ('totalEdges', 'total', 'totalRelationships') if k in first), 'totalItems')
            pages, seen = walk(lambda token: first if token is None else references(token), 5, 'nextOffset')
            report['checks']['hmi_references'] = record('hmi_references paged', {**check_totals('hmi_references', pages, seen, 5, total_key),
                                                                                  'scopeComplete': first.get('scopeComplete')})
        report['completed'] = True
    finally:
        record('tia_detach', client.request('tools/call', {'name': 'tia_detach', 'arguments': {}}).get('structuredContent'))
        report['hostClose'] = client.close()
        save()
        print(('PASSED ' if report['completed'] else 'FAILED ') + str(root / 'report.json'))


if __name__ == '__main__':
    main()
