"""D02 watch_table_force_document_edit natively, on force rows an operator authors in the TIA editor.

V21 has no API that creates force rows, so a person authors them once in an owned scratch. The runner:
creates and seeds an owned scratch CPU 1511-1 PN (the seed registers it for edits and adds two watch
peers), closes its headless Portal, starts TIA Portal with its user interface on that project and waits
for the operator's confirmation file. It then reopens the project headless, captures the authored force
table independently (exported XML against typed rows) and runs one reviewed edit per operation:
field ForceValue, Address and DisplayFormat, comment, clone of the comment line, delete, reorder and
clone of a signal row. Each is prepared, reviewed independently from the retained files, applied once
(checkpoint, import, readback, unchanged peers, save) and verified after a fresh reopen. Its checkpoint
is restored to a new location and both projects are reconciled. An apply that TIA's readback refuses
is recorded with its typed code and recovered the same way, so one desk session shows every kind.
Typed refusals: unknown table, ForceIntention, ModifyValue, unchanged value, unknown kind, ID reindex,
missing value, wrong digest, stale plan and replay. Nothing is forced, monitored or written online.

Operator steps (also written to <root>/operator-instructions.txt when TIA opens):
1. In the TIA Portal window the runner opened, open ScratchPLC > Watch and force tables > Force table.
2. Enter these rows top to bottom; type the address in the Address column and leave Name empty:
   %I0.0:P, display format Bool, force value TRUE, comment "Force row A";
   %Q0.1:P, display format Bool, force value FALSE, comment "Force row B";
   %IW2:P, display format Hex, no force value, comment "Force row C";
   then a comment line (right-click, Insert comment line) with the text "Force fixture".
3. Leave every F (force) checkbox unticked. Do not go online, monitor, force or download.
4. Save the project (Ctrl+S) and close TIA Portal.
5. Run: python scripts/qualify_watch_force_edits.py --confirm <root>, and answer its four questions.
"""
import argparse
import copy
import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

import psutil
import qualify_watch_production_host as owned
import qualify_watch_mcp as workflow
import verify_watch_expansion as expansion
import verify_watch_workflow as base
from qualify_worker_startup_exact import identity
from retained_native_lifecycle import require

SEED = Path(__file__).resolve().parents[1] / 'fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml'
FORCE, COMMENT = base.PREFIX + 'PlcForceTableEntry', base.PREFIX + 'PlcTableCommentEntry'
FIELDS = ('Name', 'Address', 'DisplayFormat', 'ForceValue')
ROWS = (('%I0.0:P', 'Bool', 'TRUE', 'Force row A'), ('%Q0.1:P', 'Bool', 'FALSE', 'Force row B'), ('%IW2:P', 'Hex', '', 'Force row C'))
COMMENT_LINE = 'Force fixture'
QUESTIONS = (('rowsAsListed', 'Are the rows entered exactly as listed (none for a dry run)?'),
             ('noForceSelection', 'Is every F (force) checkbox unticked?'),
             ('offlineOnly', 'Did you stay offline (no go online, monitor, force or download)?'),
             ('savedAndClosed', 'Did you save the project and close TIA Portal?'))
INSTRUCTIONS = __doc__[__doc__.index('Operator steps'):].replace('<root>', '"{root}"')
DRY_INSTRUCTIONS = 'Dry run: author nothing. Close TIA Portal, then run\npython scripts/qualify_watch_force_edits.py --confirm "{root}"\n'
# (label, prepare arguments, code) refused before any checkpoint; None as value means the current ForceValue of row 0.
REFUSALS = (
    ('refusal-unknown-table', {'tableName': 'MilestoneWatch', 'editKind': 'field', 'field': 'ForceValue', 'value': 'TRUE'}, 'WatchTableNotFound'),
    ('refusal-force-intention', {'editKind': 'field', 'field': 'ForceIntention', 'value': 'true'}, 'UnsupportedProfile'),
    ('refusal-modify-value', {'modifyValue': 'TRUE'}, 'UnsupportedProfile'),
    ('refusal-unknown-kind', {'editKind': 'rename'}, 'UnsupportedProfile'),
    ('refusal-reindex', {'editKind': 'field', 'field': 'ForceValue', 'value': 'FALSE', 'reindexDocumentIds': True}, 'UnsupportedProfile'),
    ('refusal-missing-value', {'editKind': 'field', 'field': 'ForceValue'}, 'InvalidRequest'),
    ('refusal-no-change', {'editKind': 'field', 'field': 'ForceValue', 'value': None}, 'NoChange'))


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def force_table(root):
    tables = [e for e in root if isinstance(e.tag, str) and e.tag.startswith(base.PREFIX)]
    require(len(tables) == 1 and tables[0].tag == base.PREFIX + 'PlcForceTable', 'Exactly one native force table required')
    return tables[0]


def entries(root):
    objects = force_table(root).find('ObjectList')
    return [] if objects is None else [row for row in objects if isinstance(row.tag, str)]


def describe(data):
    """Rows of a force export as plain values, parsed without the production editor."""
    rows = []
    for row in entries(base.xml(data)):
        require(row.tag in (FORCE, COMMENT), 'Unexpected force-table row ' + row.tag)
        items = row.findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem/AttributeList")
        rows.append({'kind': row.tag[len(base.PREFIX):], **{f: row.findtext('AttributeList/' + f) for f in FIELDS + ('ForceIntention',)},
                     'comments': {item.findtext('Culture'): item.findtext('Text') or '' for item in items}})
    return rows


def captured(snapshot, data):
    """Independent capture check: typed rows equal the exported XML and nothing is selected for forcing."""
    target = snapshot['target']
    require(target['kind'] == 'force' and target['isConsistent'] is True, 'A consistent force table is required')
    rows = describe(data)
    require([r['kind'] for r in rows] == [t['kind'] for t in target['rows']], 'Typed and exported rows differ in kind or order')
    for row, typed in zip(rows, target['rows']):
        if row['kind'] == 'PlcForceTableEntry':
            for field in FIELDS:
                require((row[field] or '') == (typed.get(field[0].lower() + field[1:]) or ''), 'Typed and exported %s differ' % field)
            require(typed['forceIntention'] is False and (row['ForceIntention'] or 'false').lower() == 'false', 'A row is selected for forcing')
    return rows


def plan(rows):
    """The fixed edit sequence on the instructed table; refuses an authored table that differs from the instructions."""
    shape = [(r['kind'], (r['Address'] or '').upper(), r['DisplayFormat'], (r['ForceValue'] or '').upper()) for r in rows]
    expected = [('PlcForceTableEntry', a.upper(), f, v) for a, f, v, _ in ROWS] + [('PlcTableCommentEntry', '', None, '')]
    require(shape == expected, 'Authored rows differ from the instructions: %s' % shape)
    for row, text in zip(rows, [r[3] for r in ROWS] + [COMMENT_LINE]):
        require(any(text in value for value in row['comments'].values()), 'Comment %r is missing' % text)
    culture = next(c for c, value in rows[0]['comments'].items() if 'Force row A' in value)
    return [('field-forcevalue', {'editKind': 'field', 'ordinal': 0, 'field': 'ForceValue', 'value': 'FALSE'}),
            ('field-address', {'editKind': 'field', 'ordinal': 1, 'field': 'Address', 'value': '%Q0.2:P'}),
            ('field-displayformat', {'editKind': 'field', 'ordinal': 2, 'field': 'DisplayFormat', 'value': 'DEC_unsigned'}),
            ('comment', {'editKind': 'comment', 'ordinal': 0, 'culture': culture, 'text': 'Force row A reviewed'}),
            ('clone-comment-line', {'editKind': 'clone', 'ordinal': 3, 'insertAt': 4}),
            ('delete-last', {'editKind': 'delete', 'ordinal': 4}),
            ('reorder', {'editKind': 'reorder', 'order': [1, 0, 2, 3]}),
            ('clone-signal', {'editKind': 'clone', 'ordinal': 0, 'insertAt': 4})]


def force_delta(before, candidate, edit):
    """Recompute the reviewed document from the captured export; IDs, order, cultures and unknown payload stay significant."""
    old, actual = base.xml(before), base.xml(candidate)
    require(base.external_xml_nodes(before) == base.external_xml_nodes(candidate), 'Candidate changes prolog or epilog nodes')
    expected = copy.deepcopy(old); objects = force_table(expected).find('ObjectList'); rows = entries(expected)
    kind = edit['kind']
    if kind != 'reorder':
        row = rows[expansion.ordinal(edit['ordinal'], len(rows))]
    if kind == 'field':
        values = row.findall('AttributeList/' + edit['field'])
        require(row.tag == FORCE and edit['field'] in FIELDS and len(values) == 1 and len(values[0]) == 0 and
                (values[0].text or '') == (edit['before'] or ''), 'One existing plain force field with the reviewed value required')
        values[0].text = edit['after'] or None
    elif kind == 'comment':
        texts = [a.find('Text') for a in row.findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem/AttributeList")
                 if a.findtext('Culture') == edit['culture']]
        require(len(texts) == 1 and texts[0] is not None and len(texts[0]) == 0, 'One existing plain comment item required')
        texts[0].text = edit['text']
    elif kind == 'delete':
        expansion.remove_element_preserving_tail(objects, row)
    elif kind == 'reorder':
        require(sorted(edit['order']) == list(range(len(rows))), 'Exact row permutation required')
        for previous, replacement in zip(rows, [copy.deepcopy(rows[i]) for i in edit['order']]):
            index = list(objects).index(previous); replacement.tail = previous.tail
            objects.remove(previous); objects.insert(index, replacement)
    elif kind == 'clone':
        at = expansion.ordinal(edit['insertAt'], len(rows), allow_end=True)
        added = entries(actual)
        require(len(added) == len(rows) + 1, 'Clone must add exactly one row')
        clone, source = copy.deepcopy(added[at]), copy.deepcopy(row)
        used = {int(n.attrib['ID'], 16) for n in old.iter() if 'ID' in n.attrib}
        pairs = list(zip(source.iter(), clone.iter()))
        require(len(pairs) == len(list(clone.iter())) == len(list(source.iter())), 'Clone payload shape changed')
        for original, copied in pairs:
            require(original.tag == copied.tag, 'Clone payload order changed')
            if 'ID' in original.attrib:
                require('ID' in copied.attrib and int(copied.attrib['ID'], 16) not in used, 'Clone must take fresh IDs')
                original.attrib['ID'] = copied.attrib['ID']
        source.tail = clone.tail = None
        require(base.xml_key(source, False) == base.xml_key(clone, False), 'Clone changed the payload beyond fresh IDs')
        if at == len(rows): objects.append(clone)
        else: objects.insert(list(objects).index(rows[at]), clone)
    else:
        raise RuntimeError('Unsupported force edit ' + kind)
    require(base.xml_key(old, False) != base.xml_key(actual, False), 'Reviewed edit makes no change')
    require(base.xml_key(expected, False) == base.xml_key(actual, False), 'Candidate differs beyond the reviewed edit')


def same_document(a, b):
    """Equal native exports apart from the export timestamp."""
    return base.xml_key(base.xml(a)) == base.xml_key(base.xml(b)) and base.external_xml_nodes(a) == base.external_xml_nodes(b)


def renumbered(data):
    root = base.xml(data); ids = (node for node in root.iter() if isinstance(node.tag, str) and 'ID' in node.attrib)
    for number, node in enumerate(ids): node.attrib['ID'] = format(number, 'X')
    return base.xml_key(root)


def ids_only_difference(candidate, readback):
    """Whether a refused readback differs from the candidate only by IDs renumbered in document order."""
    return not same_document(candidate, readback) and renumbered(candidate) == renumbered(readback)


def peers_unchanged(before, after):
    others = lambda s: sorted((t['kind'], t['group'], t['name'], t['semanticSha256']) for t in s['tables'] if t['kind'] != 'force')
    return before['peers'] == after['peers'] and before['peerSha256'] == after['peerSha256'] and others(before) == others(after)


def read_confirmation(path):
    value = json.loads(Path(path).read_text(encoding='utf-8'))
    require(isinstance(value, dict) and set(value) == {'answers', 'confirmedAtUtc'} and isinstance(value['answers'], dict) and
            set(value['answers']) == {key for key, _ in QUESTIONS} and all(type(v) is bool for v in value['answers'].values()),
            'Malformed operator confirmation')
    return value


def confirm(root):
    """Operator helper: shows the steps, asks the questions and writes the confirmation the runner waits for."""
    root = root.resolve()
    print((root / 'operator-instructions.txt').read_text(encoding='utf-8'))
    answers = {}
    for key, question in QUESTIONS:
        while (answer := input(question + ' [y/n] ').strip().lower()) not in ('y', 'n'): pass
        answers[key] = answer == 'y'
    temporary = root / 'operator-confirmation.json.tmp'
    temporary.write_text(json.dumps({'answers': answers, 'confirmedAtUtc': utc()}, indent=2), encoding='utf-8')
    os.replace(temporary, root / 'operator-confirmation.json')  # The runner polls the final name only.
    print('Confirmation written; the runner continues.' if all(answers.values()) else 'Not confirmed; the runner stops.')


class Run(owned.Run):
    def __init__(self, args):
        super().__init__(args)
        source = Path(__file__)
        shutil.copyfile(source, self.root / 'controller-source' / source.name)
        row = {'path': str(source.resolve()), 'sha256': workflow.digest(source)}
        self.sources.append(row)
        self.save('force-edit-inputs', {'sources': [row], 'rows': ROWS, 'commentLine': COMMENT_LINE, 'dryRun': args.dry_run,
                                        'automaticRetry': False, 'nativeOnline': False})

    def prepare(self, request, key, label=None):
        arguments = {'tableName': self.table, 'group': self.group, 'plcPath': self.plc, 'forceTable': True,
                     'ordinal': 0, 'idempotencyKey': 'wtf-' + key, **request}
        return self.call('watch_table_prepare', arguments, label)

    def read(self, label):
        snapshot = self.call('watch_table_read', {'tableName': self.table, 'group': self.group, 'plcPath': self.plc, 'forceTable': True}, label)
        return snapshot, base.artifact(self.root, snapshot['target']['artifact']).read_bytes()

    def expect(self, label, code, call):
        try:
            call()
        except workflow.ToolError as error:
            require(error.packet['error']['code'] == code, '%s refused with %s, expected %s' % (label, error.packet['error'], code))
            return self.save(label, {'code': code, 'nativeEffect': False})
        raise AssertionError(label + ' was not refused')

    def author(self, project, exe):
        instructions = (DRY_INSTRUCTIONS if self.args.dry_run else INSTRUCTIONS).format(root=self.root)
        (self.root / 'operator-instructions.txt').write_text(instructions, encoding='utf-8')
        ui = subprocess.Popen([exe, project], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.save('authoring-ui-start', {'identity': identity(psutil.Process(ui.pid)), 'project': project, 'startedAtUtc': utc()})
        print('TIA Portal (PID %d) opens %s\n%s' % (ui.pid, project, instructions), flush=True)
        path, deadline = self.root / 'operator-confirmation.json', time.monotonic() + self.args.authoring_minutes * 60
        while not path.exists():
            require(time.monotonic() < deadline, 'No operator confirmation; TIA Portal PID %d is left to the operator' % ui.pid)
            time.sleep(2)
        confirmation = read_confirmation(path)
        require(all(confirmation['answers'].values()), 'The operator did not confirm: %s' % confirmation['answers'])
        while ui.poll() is None:
            require(time.monotonic() < deadline, 'TIA Portal PID %d is still open' % ui.pid)
            print('Waiting for TIA Portal PID %d to close.' % ui.pid, flush=True)
            time.sleep(5)
        return self.save('authoring-ui-exit', {'processId': ui.pid, 'exitCode': ui.returncode, 'sameOsHandle': True,
                                               'confirmation': confirmation, 'closedAtUtc': utc()})

    def review(self, label, outer, request, semantic, peer):
        """Independent file-only review of one prepared force edit; returns the reviewed candidate bytes."""
        record, proposal = outer['operation'], outer['review']
        plan_, before, edit = record['plan'], proposal['before'], proposal['edit']
        require(record['state'] == 'prepared' and record['revision'] == 0 and record['previousSha256'] is None
                and plan_['operation'] == 'watchDocumentEdit', 'Initial document-edit operation required')
        require(base.canonical_sha(plan_) == record['planSha256'] and plan_['target'] == before['observation'], 'Plan digest or target differs')
        require(base.load(base.artifact(self.root, outer['reviewArtifact'])) == proposal, 'Retained review differs')
        identity_ = lambda item: (base.artifact(self.root, item).resolve(), item['sha256'])
        require([identity_(i) for i in plan_['inputs']] == [identity_(outer['reviewArtifact']), identity_(proposal['input'])], 'Plan inputs differ')
        require(proposal['selector']['targetKind'] == 'forceTable' and before['projectModified'] is False and
                before['target']['semanticSha256'] == semantic and before['peerSha256'] == peer,
                'Captured baseline differs from the previous verified force table or peers')
        asked = {('kind' if k == 'editKind' else 'after' if k == 'value' else k): v for k, v in request.items()}
        require(all(edit.get(k) == v for k, v in asked.items()), 'Reviewed edit differs from the request: %s' % edit)
        baseline = base.artifact(self.root, before['target']['artifact']).read_bytes()
        captured(before, baseline)
        candidate = base.artifact(self.root, proposal['input']).read_bytes()
        force_delta(baseline, candidate, edit)
        self.save(label + '-independent-review', {'planSha256': record['planSha256'], 'edit': edit, 'approved': True})
        return candidate

    def check(self, snapshot, candidate, before):
        data = base.artifact(self.root, snapshot['target']['artifact']).read_bytes()
        captured(snapshot, data)
        require(same_document(candidate, data) and peers_unchanged(before, snapshot), 'Readback or peers differ from the review')

    def operate(self, label, request, original, semantic, peer):
        outer = self.prepare(request, label, label + '-review')
        candidate = self.review(label, outer, request, semantic, peer)
        before, oid = outer['review']['before'], outer['operation']['id']
        result = {'label': label, 'edit': outer['review']['edit'], 'id': oid, 'planSha256': outer['operation']['planSha256']}
        try:
            applied = self.call('watch_table_apply', {'operationId': oid, 'reviewedPlanSha256': result['planSha256']}, label + '-applied')
        except workflow.ToolError as error:
            details = (error.packet['error'].get('details') or {}).get('target') or {}
            readback = base.artifact(self.root, details['artifact']).read_bytes() if 'artifact' in details else None
            result.update(outcome='refused', code=error.packet['error']['code'], readbackIsConsistent=details.get('isConsistent'),
                          readbackDiffersOnlyInDocumentIds=None if readback is None else ids_only_difference(candidate, readback))
        else:
            require(applied['applied']['saved'] is True, label + ': unsaved apply')
            self.check(applied['applied'], candidate, before)
            self.reopen(original)
            verified = self.call('watch_table_verify_reopen', {'operationId': oid}, label + '-verified')
            self.check(verified['reopened'], candidate, before)
            result.update(outcome='verified', semanticSha256=verified['reopened']['target']['semanticSha256'])
        status = self.call('engineering_operation_status', {'operationId': oid}, label + '-status')
        if not status['checkpoints']:  # Refused before its checkpoint: nothing native happened, nothing to restore.
            require(result['outcome'] == 'refused' and status['state'] == 'stopped', label + ': unexpected state without checkpoint')
            return result
        require(len(status['checkpoints']) == 1, label + ': one checkpoint required')
        self.close_tia(); self.start_tia()
        recovered = self.call('watch_table_recover', {'operationId': oid}, label + '-recovered')
        restored = recovered['recovered']['project']['path']
        require(recovered['restored']['target']['semanticSha256'] == semantic and peers_unchanged(before, recovered['restored']),
                label + ': the restored checkpoint differs from the pre-edit state')
        self.reopen(restored)
        self.call('watch_table_reconcile_recovery', {'operationId': oid, 'role': 'restored'}, label + '-restored-verified')
        self.reopen(original)
        self.call('watch_table_reconcile_recovery', {'operationId': oid, 'role': 'original'}, label + '-reconciled')
        final = self.call('engineering_operation_status', {'operationId': oid}, label + '-final-status')
        require(final['state'] == 'reconciled', label + ': not reconciled')
        return {**result, 'recoveryPath': restored, 'state': final['state']}

    def execute(self):
        self.start_host('none')
        self.start_tia()
        scratch = self.call('tia_create_scratch', label='scratch')
        require(scratch['completed'], 'Scratch creation incomplete')
        original, self.plc = scratch['project']['path'], scratch['exports'][0]['block']['plcPath']
        seed = self.root / 'seed.xml'
        shutil.copyfile(SEED, seed)
        seeded = self.call('qualification_watch_fixture_seed', {'inputPath': str(seed), 'inputSha256': workflow.digest(seed)}, 'seeded')
        forces = [t for t in seeded['tables'] if t['kind'] == 'force']
        require(len(forces) == 1 and not forces[0]['rows'], 'One empty default force table required')
        self.table, self.group = forces[0]['name'], forces[0]['group']
        empty, _ = self.read('force-baseline')
        exe = self.native['identity']['exe']
        self.close_tia()
        self.author(original, exe)
        self.start_tia()
        self.call('project_open', {'path': original})
        authored, data = self.read('authored')
        (self.root / 'authored-force-table.xml').write_bytes(data)  # Kept even when the capture check below refuses it.
        rows = captured(authored, data)
        checks = ['authored table captured independently: %d rows' % len(rows),
                  'peers unchanged by the editor session: %s' % peers_unchanged(empty, authored)]
        self.save('authored-capture', {'rows': rows, 'checks': checks})
        semantic, peer = authored['target']['semanticSha256'], authored['peerSha256']
        if self.args.dry_run:
            require(not rows, 'A dry run expects the empty default force table')
            for label, request, code in REFUSALS[:-1]:  # Without rows, NoChange cannot occur; the others still refuse typed.
                self.expect(label, code, lambda: self.prepare(request, label))
            self.close_tia()
            self.close_host()
            return {'completed': True, 'dryRun': True, 'checks': checks + [label + ': ' + code for label, _, code in REFUSALS[:-1]]}
        operations = plan(rows)
        for label, request, code in REFUSALS:
            request = {**request, 'value': rows[0]['ForceValue']} if request.get('value', '') is None else request
            self.expect(label, code, lambda: self.prepare(request, label))
            checks.append('%s: %s' % (label, code))
        stale = self.prepare({'editKind': 'field', 'ordinal': 1, 'field': 'ForceValue', 'value': 'TRUE'}, 'stale', 'stale-review')['operation']
        results = []
        for label, request in operations:
            result = self.operate(label, request, original, semantic, peer)
            results.append(result)
            checks.append('%s: %s%s' % (label, result['outcome'], '' if result['outcome'] == 'verified' else
                          ' %s (only document IDs differ: %s)' % (result['code'], result['readbackDiffersOnlyInDocumentIds'])))
            if result['outcome'] == 'verified':
                semantic = result['semanticSha256']
            if len(results) == 1:  # The plan prepared before the first edit is now stale; the first edit cannot be replayed.
                apply = lambda operation, digest: lambda: self.call('watch_table_apply', {'operationId': operation, 'reviewedPlanSha256': digest})
                self.expect('refusal-wrong-digest', 'ReviewMismatch', apply(stale['id'], '0' * 64))
                self.expect('refusal-stale-plan', 'StaleNativeTarget', apply(stale['id'], stale['planSha256']))
                require(self.call('engineering_operation_status', {'operationId': stale['id']}, 'stale-status')['state'] == 'stopped', 'Stale plan left an owned operation')
                self.expect('refusal-replay', 'RecoveryRequired', apply(result['id'], result['planSha256']))
                checks += ['refusal-wrong-digest: ReviewMismatch', 'refusal-stale-plan: StaleNativeTarget', 'refusal-replay: RecoveryRequired']
        self.close_tia()
        self.close_host()
        return {'completed': True, 'projectPath': original, 'operations': results, 'checks': checks, 'nativeOnline': False}

    def report(self, result):
        # Written only after the whole run passed; scripts/curate_evidence.py reads it. Expected refusals stay listed as rejected calls.
        return self.save('dry-run' if result.get('dryRun') else 'verification', {**result, 'mode': self.args.mode, 'toolCalls': self.tool_calls})


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--confirm', type=Path, help='Operator helper: confirm the authoring for this run root.')
    for name in ('host', 'worker', 'root'):
        parser.add_argument('--' + name, type=Path)
    parser.add_argument('--dry-run', action='store_true', help='Author nothing; stop after the capture and the row-independent refusals.')
    parser.add_argument('--authoring-minutes', type=int, default=60)
    args = parser.parse_args()
    if args.confirm:
        return confirm(args.confirm)
    require(args.host and args.worker and args.root, '--host, --worker and --root are required')
    args.mode, args.comment_input, args.native_cleanup_helper = 'force-edits', None, None
    result = owned.run_owned(Run(args))
    print(json.dumps({'completed': result['completed'], 'root': str(args.root)}), flush=True)


if __name__ == '__main__':
    main()
