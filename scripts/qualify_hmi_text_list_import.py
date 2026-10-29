"""Native import of Host-authored Unified text-list candidates.

Phase 1 runs the production Host without native workers: the hmi_text_list_author MCP oracle authors its
three variants, checks the refusals of content V21 changes, authors three boundary requests at the edge of
what the tool accepts, and reopens the retained v1-v3 author and v1 set records.
Phase 2 runs `HmiSeeder textlist-import`, which imports every candidate pair into a new owned MTP700 Unified
Comfort fixture through HmiTextListComposition.Import (main sequence), saves, archives, reopens in a fresh
Portal, retrieves both archives and runs the boundary probes on the restored checkpoint. Phase 3 compares
the native exports with the candidates; phase 4 reopens every record on a fresh Host.
"""
import argparse
import base64
import copy
import hashlib
import json
import subprocess
from pathlib import Path

import yaml
from smoke_mcp import Client, ROOT
from hmi_text_list_author_checks import run as author_text_lists, reopen as reopen_text_lists

RETAINED = {'author': ROOT / 'fixtures/hmi-coordinated/retained-v1-v3', 'set': ROOT / 'fixtures/hmi-evolution/retained-v1-sets'}
AUTHOR_LABELS = ['author-v1-retained', 'author-v2-initial', 'author-v2-regenerated', 'author-v3-initial', 'author-v3-regenerated']
SET_LABELS = ['set-v1', 'set-v1-evolved']
MAIN = AUTHOR_LABELS + ['set-v1-run', 'set-v1-state', 'set-v1-evolved-run', 'set-v1-evolved-state', 'author-v1-base']
PROBES = ['boundary-values', 'boundary-texts', 'boundary-names', 'author-v1-expanded', 'author-v1-extremes']
# V21 exports omit these entry fields at their default value (observed in run q/tl1: an imported SingleValue 0 entry exports bare).
ENTRY_DEFAULTS = {'Type': 'SingleValue', 'Value': 0, 'FromValue': 0, 'ToValue': 0, 'IsDefaultEntry': False}


class TextLibraryLoader(yaml.SafeLoader):
    """Every plain scalar of a text library is text: V21 exports texts such as Off, On or Null unquoted, which YAML 1.1 reads as bool/null."""
TextLibraryLoader.yaml_implicit_resolvers = {}


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    # JSON keeps YAML booleans distinct from integers 0/1.
    return json.dumps(value, ensure_ascii=False, sort_keys=True)


def boundary_requests():
    """hmi_text_list_author requests at the edge of what the tool accepts; each list must import exactly."""
    base = json.loads((ROOT / 'fixtures/hmi/unified-text-list/author-request.json').read_text(encoding='utf-8'))
    def request(file_base, texts, lists):
        return dict(copy.deepcopy(base), fileBase=file_base, textLibrary=file_base + 'Texts', lists=lists,
                    texts=[dict(id=key, translations={'en-US': value, 'de-DE': value + ' DE' if value else value}) for key, value in texts.items()])
    def entry(identity, text, kind='SingleValue', low=0, high=0, default=False):
        return dict(id=identity, type=kind, fromValue=low, toValue=high, isDefault=default, textId=text)
    def plain(name, *entries, kind='Decimal'):
        return dict(name=name, rangeType=kind, entries=list(entries))
    # Every C0/C1 control and DEL the tool still accepts (not NUL, CR or NEL), one list each.
    controls = [code for code in [*range(1, 32), *range(127, 160)] if code not in (10, 13, 133)]
    texts = {'Quote': '"quoted" \\ backslash', 'Whitespace': 'tab\there\nLF', 'Unicode': 'Über 日本語 😀', 'Empty': '',
             'Yaml': '---\n!tag &anchor #hash: colon', 'Separators': 'x' + chr(0x2028) + 'y' + chr(0x2029) + 'z', 'Long': 'L' * 16381}
    # Every printable ASCII punctuation the tool still accepts in a list name, plus spacing and the 256-character bound.
    names = ['Plain_Name1', 'Name with space', 'Umlaut_Ä', 'Unicode_日本語', ' Leading', 'Trailing ', 'N' * 256] + \
            ['P' + chr(code) + 'Name' for code in range(33, 127) if not chr(code).isalnum() and chr(code) not in '.:#"[_']
    return {
        'boundary-values': request('ProbeValues', {'T': 'Value'}, [
            plain('EntryTypes', entry('Low', 'T', 'To', None, -1), entry('Middle', 'T', 'Range', 0, 10), entry('High', 'T', 'From', 11, None)),
            plain('Int32Bounds', entry('Min', 'T', low=-2**31, high=-2**31), entry('Max', 'T', low=2**31 - 1, high=2**31 - 1)),
            plain('BoolNoDefault', entry('False', 'T'), entry('True', 'T', low=1, high=1), kind='Bool'),
            plain('BitDefault', entry('Bit0', 'T', default=True), entry('Bit31', 'T', low=31, high=31), kind='BitNumber')]),
        'boundary-texts': request('ProbeTexts', {**texts, **{f'C{code:02X}': 'a' + chr(code) + 'b' for code in controls}}, [
            plain('TextChars', *(entry(key, key, low=index, high=index) for index, key in enumerate(texts))),
            *(plain(f'Control_{code:02X}', entry('Zero', f'C{code:02X}')) for code in controls)]),
        'boundary-names': request('ProbeNames', {'T': 'Name'}, [plain(name, entry('Zero', 'T')) for name in names])}


def resolve(config_raw, library_raw):
    """Text lists by name as V21 reads them. V21 renames entries and pools all texts in one library on export and omits
    default fields, so entries become a sorted multiset with defaults applied and each Text reference replaced by
    {language: text}. Every stated value stays significant."""
    config, library = yaml.safe_load(config_raw) or {}, yaml.load(library_raw, Loader=TextLibraryLoader) or {}
    libraries = library.get('TextLibraries') or {}
    result = {}
    for container in (config.get('TextListContainers') or {}).values():
        for name, row in (container.get('ResourceLists') or {}).items():
            assert name not in result, 'Duplicate text-list name across containers: ' + str(name)
            entries = []
            for entry in (row.get('Entries') or {}).values():
                value = {**ENTRY_DEFAULTS, **entry}
                reference = value.pop('Text', None)
                owner = next((lib for lib in libraries if isinstance(reference, str) and reference.startswith(str(lib) + '.')), None)
                texts = ((libraries[owner].get('Entries') or {}).get(reference[len(str(owner)) + 1:]) or {}).get('Text') if owner is not None else None
                languages = libraries[owner].get('Languages') if owner is not None else None
                value['Text'] = dict(zip(languages, texts)) if texts is not None and len(texts) == len(languages) else {'unresolved': reference, 'texts': texts}
                entries.append(value)
            result[name] = {'RangeType': 'Decimal', **{k: v for k, v in row.items() if k != 'Entries'}, 'Entries': sorted(entries, key=canonical)}
    return result


def orphan_texts(library_raw, config_raw):
    """Library entries no list references; reported, never excused."""
    config, library = yaml.safe_load(config_raw) or {}, yaml.safe_load(library_raw) or {}
    used = {entry.get('Text') for container in (config.get('TextListContainers') or {}).values()
            for row in (container.get('ResourceLists') or {}).values() for entry in (row.get('Entries') or {}).values()}
    return sorted(f'{lib}.{key}' for lib, row in (library.get('TextLibraries') or {}).items()
                  for key in (row.get('Entries') or {}) if f'{lib}.{key}' not in used)


def evaluate_import(before, after, candidate, accepted):
    """Per-list result: candidate lists must read back exactly; every other list must be unchanged."""
    lists = {name: 'exact' if canonical(row) == canonical(after.get(name)) else 'absent' if name not in after else 'changed'
             for name, row in candidate.items()}
    result = dict(accepted=accepted, lists=lists,
                  mismatches={name: dict(candidate=candidate[name], native=after.get(name)) for name, state in lists.items() if state == 'changed'},
                  peersChanged=sorted(name for name in set(before) - set(candidate) if canonical(before[name]) != canonical(after.get(name))),
                  unexpectedLists=sorted(set(after) - set(before) - set(candidate)))
    if accepted:
        result['outcome'] = 'exact' if all(s == 'exact' for s in lists.values()) and not result['peersChanged'] and not result['unexpectedLists'] else 'inexact'
    else:
        result['outcome'] = 'refusedUnchanged' if canonical(before) == canonical(after) else 'refusedChanged'
    return result


def unrelated(inspection):
    """Everything the independent walk saw except the text-list section, without stage-named file paths."""
    return stripped({'attributes': inspection['attributes'], 'sections': {k: v for k, v in inspection['sections'].items() if k != 'HmiTextLists'}})


def stripped(value):
    if isinstance(value, dict): return {k: stripped(v) for k, v in value.items() if k != 'path'}
    if isinstance(value, list): return [stripped(v) for v in value]
    return value


def compile_messages(results):
    """Complete flattened (state, description, path) messages per owner; None when a capture is incomplete."""
    def flatten(rows):
        for row in rows:
            yield (row.get('state'), row.get('description'), row.get('path'))
            yield from flatten(row.get('children') or [])
    if not results or any('error' in r or r.get('messagesComplete') is not True for r in results): return None
    return {r['name']: sorted(flatten(r['messages']), key=canonical) for r in results}


def problems(messages):
    # TIA compiles incrementally, so Information rows (up-to-date hardware, tag/screen statistics) depend on the previous compile.
    return None if messages is None else {name: [m for m in rows if m[0] in ('Error', 'Warning')] for name, rows in messages.items()}


def export(row):
    """The paired native export bytes (configuration, library) recorded by the seeder, hash-checked."""
    files = {Path(f['path']).name: Path(f['path']).read_bytes() for f in (row or {}).get('export') or []}
    assert row and row.get('status') == 'observed' and len(files) == 2, 'Native text-list export unavailable'
    for f in row['export']: assert digest(files[Path(f['path']).name]) == f['sha256'], 'Native export changed after capture'
    library = next(name for name in files if name.endswith('.TextLibrary.hmi.yml'))
    return files[next(name for name in files if name != library)], files[library]


def assess(nat, manifest, candidates_dir, gate):
    """Phase 3: compare the seeder's recorded native exports and walks with the candidates."""
    resolved = lambda row: resolve(*export(row))
    rows = {row['label']: row for group in manifest.values() for row in group}
    def candidate(label):
        row = rows[label]; directory = candidates_dir / row['directory']
        library = next(f['name'] for f in row['files'] if f['name'] != row['configuration'])
        return resolve((directory / row['configuration']).read_bytes(), (directory / library).read_bytes())
    result = {}
    baseline = resolved(nat['baseline']['textListEvidence'])
    gate(canonical(baseline) == canonical(resolved(nat['baselineRepeat']['textListEvidence'])), 'Repeated baseline text-list export is equal')
    gate(stripped(nat['baseline']) == stripped(nat['baselineRepeat']), 'Repeated independent baseline walk is equal')
    peer = resolve((ROOT / 'fixtures/hmi/unified-text-list/QualificationTextLists.hmi.yml').read_bytes(),
                   (ROOT / 'fixtures/hmi/unified-text-list/QualificationTextLists.TextLibrary.hmi.yml').read_bytes())
    gate(canonical(baseline) == canonical(peer), 'Baseline holds exactly the documented bilingual peer list', list(baseline))
    gate([row['label'] for row in nat['imports']] == [row['label'] for row in manifest['main']], 'Seeder imported every main candidate in order')
    result['imports'], before = [], baseline
    for row in nat['imports']:
        after = resolved(row['export'])
        outcome = evaluate_import(before, after, candidate(row['label']), row.get('accepted') is True)
        outcome.update(label=row['label'], error=row.get('error'), elapsedMs=row.get('elapsedMs'), orphanTexts=orphan_texts(*reversed(export(row['export']))))
        result['imports'].append(outcome)
        gate(outcome['outcome'] == 'exact', 'Native import reads back exactly, other lists unchanged: ' + row['label'],
             {k: outcome[k] for k in ('outcome', 'lists', 'peersChanged', 'unexpectedLists', 'error')})
        before = after
    final = before
    roundtrip = nat['roundtrip']
    gate(roundtrip.get('accepted') is True and canonical(resolved(roundtrip['export'])) == canonical(final),
         'No-change roundtrip of the whole native export is semantically unchanged', roundtrip.get('error'))
    gate(export(roundtrip['export']) == export(nat['imports'][-1]['export']), 'No-change roundtrip re-exports byte-identical files')
    gate(unrelated(nat['after']) == unrelated(nat['baseline']), 'Unrelated HMI content (screens, items, events, tags, alarms, scripts, connections, graphic lists) unchanged by the imports')
    gate(canonical(resolved(nat['after']['textListEvidence'])) == canonical(final), 'Post-import walk export equals the last import readback')
    for stage, expected, walk in (('reopened', final, nat['after']), ('retrieved', final, nat['after']),
                                  ('restored', baseline, nat['baseline']), ('restoredAgain', baseline, nat['baseline'])):
        row = nat.get(stage)
        gate(row is not None and canonical(resolved(row['textListEvidence'])) == canonical(expected), f'{stage}: text lists equal the expected state')
        gate(row is not None and stripped({k: v for k, v in row.items() if k not in ('textListEvidence', 'compile', 'projectPath')}) ==
             stripped({k: v for k, v in walk.items() if k != 'textListEvidence'}), f'{stage}: independent HMI walk equals the expected state')
    gate(nat.get('checkpointUnchanged') is True, 'Checkpoint archive unchanged by the restores')
    compiles = {'baseline': compile_messages(nat.get('compileBaseline')), 'after': compile_messages(nat.get('compileAfter')),
                'reopened': compile_messages((nat.get('reopened') or {}).get('compile'))}
    result['compile'] = {name: dict(complete=value is not None, problems=problems(value), messages=value) for name, value in compiles.items()}
    gate(all(value is not None for value in compiles.values()), 'Complete native compiler diagnostics at baseline, after import and after reopen')
    gate(problems(compiles['after']) == problems(compiles['baseline']) == problems(compiles['reopened']), 'Imports add, remove or change no compiler error or warning')
    result['probes'], before = [], baseline
    for row in nat['probes']:
        after = resolved(row['export'])
        outcome = evaluate_import(before, after, candidate(row['label']), row.get('accepted') is True)
        outcome.update(label=row['label'], error=row.get('error'))
        result['probes'].append(outcome)
        gate(outcome['outcome'] == 'exact', 'Boundary candidate the tool accepts imports exactly: ' + row['label'],
             {k: outcome[k] for k in ('outcome', 'peersChanged', 'unexpectedLists', 'error')} | {'notExact': {n: s for n, s in outcome['lists'].items() if s != 'exact'}})
        before = after
    gate([row['label'] for row in nat['probes']] == [row['label'] for row in manifest['probes']], 'Seeder ran every boundary probe in order')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--seeder', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--timeout', type=int, default=5400, help='Seconds for the native seeder run.')
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    native, candidates_dir = output / 'n', output / 'c'
    assert len(str(native)) <= 60, 'TIA refuses long project paths; choose a shorter output directory'
    output.mkdir(parents=True)
    store = output / 'store'
    report = dict(completed=False, nativeExecuted=False, runtimeVerified=False, checks=[], failures=[], shutdowns=[])

    def persist():
        (output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')

    def check(value, label):
        assert value, label
        report['checks'].append(label)

    def gate(value, label, detail=None):
        (report['checks'] if value else report['failures']).append(label if value or detail is None else f'{label}: {detail}')

    def upload(client, data):
        status = client.tool('artifact_upload_begin', dict(fileName='request.json', expectedBytes=len(data), sha256=digest(data)))
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=status['uploadId'], offset=offset,
                        data=base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()))
        return client.tool('artifact_upload_commit', dict(uploadId=status['uploadId']))['artifactRef']

    def read(client, reference):
        chunks, offset = [], 0
        while True:
            part = client.tool('artifact_read', dict(path=reference['path'], offset=offset, count=65536))
            raw = base64.b64decode(part['data'], validate=True)
            assert part['sha256'] == reference['sha256'] and part['offset'] == offset and len(raw) == part['count']
            chunks.append(raw); offset = part['nextOffset']
            if part['complete']: break
        raw = b''.join(chunks)
        check(digest(raw) == reference['sha256'], 'Downloaded artifact exact digest')
        return raw

    def refuse(client, tool, arguments, code):
        response = client.request('tools/call', dict(name=tool, arguments=arguments))
        error = response['structuredContent']['error']
        check(response.get('isError') and error['code'] == code and error['nativeAccessAttempted'] is False, 'Structured offline refusal ' + code)

    def start():
        client = Client(args.host.resolve(), 180, ['--no-native-workers', '--artifacts', str(store)])
        client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='hmi-text-list-import', version='1')))
        client.notify('notifications/initialized')
        return client

    def close(client):
        closed = client.close()
        report['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native workers')

    def retained_records():
        for kind, root in RETAINED.items():
            for record in json.loads((root / 'fixture.json').read_text(encoding='utf-8'))['records']:
                yield kind, record

    try:
        # Phase 1: author and reopen candidates on the production Host.
        for root in RETAINED.values():
            for row in json.loads((root / 'fixture.json').read_text(encoding='utf-8'))['files']:
                raw = (root / 'store' / row['path']).read_bytes()
                check(digest(raw) == row['sha256'], 'Retained actual-producer byte')
                target = store / row['path']; target.parent.mkdir(parents=True, exist_ok=True)
                assert not target.exists() or target.read_bytes() == raw
                target.write_bytes(raw)
        pairs, receipts = {}, {}
        client = start()
        try:
            (output / 'authoring').mkdir()
            authored = author_text_lists(client, upload, read, check, refuse, output / 'authoring')
            for (_, receipt, candidates), label in zip(authored, ('author-v1-base', 'author-v1-expanded', 'author-v1-extremes')):
                pairs[label] = list(zip((row['fileName'] for row in receipt['files']), candidates))
            for label, request in boundary_requests().items():
                receipt = client.tool('hmi_text_list_author', dict(uploadRef=upload(client, json.dumps(request, ensure_ascii=False).encode('utf-8'))))
                check(receipt['listCount'] == len(request['lists']), 'Boundary request authored: ' + label)
                receipts[label] = receipt
                pairs[label] = [(row['fileName'], read(client, row['artifact'])) for row in receipt['files']]
            labels = iter(AUTHOR_LABELS + SET_LABELS)
            for kind, record in retained_records():
                tool = 'hmi_text_list_author' if kind == 'author' else 'hmi_text_list_generate_set'
                receipt = client.tool(tool, dict(recordId=record['recordId']))
                check(receipt == record['receipt'], 'Retained record reopens with its exact receipt: ' + record['recordId'][:12])
                receipts[record['recordId']] = receipt
                label = next(labels)
                files = [(row['fileName'], read(client, row['artifact']), row.get('outputId')) for row in receipt['files']]
                for output_id in dict.fromkeys(row[2] for row in files):
                    pairs[label + ('-' + output_id if output_id else '')] = [(name, raw) for name, raw, owner in files if owner == output_id]
        finally:
            close(client)
        assert sorted(MAIN + PROBES) == sorted(pairs), sorted(pairs)
        manifest = {}
        for group, labels in (('main', MAIN), ('probes', PROBES)):
            manifest[group] = []
            for index, label in enumerate(labels):
                directory = candidates_dir / f'{group[0]}{index}'; directory.mkdir(parents=True)
                files = []
                for name, raw in pairs[label]:
                    (directory / name).write_bytes(raw); files.append(dict(name=name, sha256=digest(raw)))
                configuration = next(f['name'] for f in files if not f['name'].endswith('.TextLibrary.hmi.yml'))
                manifest[group].append(dict(label=label, directory=directory.name, configuration=configuration, files=files))
        (candidates_dir / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        report['candidates'] = manifest
        persist()

        # Phase 2: native import in owned Portals, one at a time.
        with open(output / 'seeder.log', 'wb') as log:
            seeder = subprocess.run([str(args.seeder.resolve()), 'textlist-import', str(native), str(candidates_dir)],
                                    stdout=log, stderr=subprocess.STDOUT, timeout=args.timeout)
        report['nativeExecuted'] = True
        native_report_path = native / 'native-report.json'
        nat = json.loads(native_report_path.read_text(encoding='utf-8'))
        report['nativeReport'] = dict(path=str(native_report_path), sha256=digest(native_report_path.read_bytes()), exitCode=seeder.returncode)
        gate(seeder.returncode == 0 and nat.get('completed') is True, 'Seeder completed and every owned Portal process exited', nat.get('portals'))

        # Phase 3: compare native exports with the candidates.
        report.update(assess(nat, manifest, candidates_dir, gate))
        persist()

        # Phase 4: every record reopens on a fresh Host after the native run.
        client = start()
        try:
            reopen_text_lists(client, authored, read, check, refuse, store)
            for kind, record in retained_records():
                tool = 'hmi_text_list_author' if kind == 'author' else 'hmi_text_list_generate_set'
                check(client.tool(tool, dict(recordId=record['recordId'])) == receipts[record['recordId']], 'Retained record reopens unchanged after restart: ' + record['recordId'][:12])
            for label in boundary_requests():
                check(client.tool('hmi_text_list_author', dict(recordId=receipts[label]['recordId'])) == receipts[label], 'Boundary record reopens unchanged after restart: ' + label)
        finally:
            close(client)
        report['completed'] = not report['failures']
    finally:
        persist()
    print(json.dumps(dict(completed=report['completed'], checks=len(report['checks']), failures=report['failures']), ensure_ascii=False))
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
