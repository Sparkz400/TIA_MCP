"""D02: the six watch-table lifecycle profiles natively, with exact owned lifetimes.

One owned scratch CPU 1511-1 PN, seeded with the MilestoneWatch fixture (which registers the
scratch for mutations after a reopen and leaves two root peer tables that must stay unchanged);
the operations chain on the saved original project:
create group, create a table in it, rename the group (the table must keep its content
under the new path), rename the table, delete it, delete the group. Each operation is
prepared, independently reviewed from the retained files (plan digest, inputs, an
independently computed expected group subtree), applied once, verified after a fresh
Portal reopen and recovered from its checkpoint into a new location, whose fresh reopen
must equal the pre-operation subtree; the original is then reconciled. The next
operation's captured baseline must equal the previous verified subtree, so recovery and
refusals left the original unchanged. Typed prepare refusals cover collisions, a
non-empty group and missing targets. No online action, monitoring or forcing.
"""
import argparse
import json
from pathlib import Path
import shutil

import qualify_watch_production_host as owned
import qualify_watch_mcp as workflow
import verify_watch_workflow as base
from retained_native_lifecycle import require

GROUP, GROUP2, TABLE, TABLE2 = 'WTL_Group', 'WTL_Renamed', 'WTL_Table', 'WTL_Moved'
SEED = Path(__file__).resolve().parents[1] / 'fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml'
TOOLS = {'createTable': ('watch_table_prepare_create', 'tableName'), 'renameTable': ('watch_table_prepare_rename', 'tableName'),
         'deleteTable': ('watch_table_prepare_delete', 'tableName'), 'createGroup': ('watch_table_group_prepare_create', 'groupName'),
         'renameGroup': ('watch_table_group_prepare_rename', 'groupName'), 'deleteGroup': ('watch_table_group_prepare_delete', 'groupName')}
# (operation, selector group, name, newName, refusals prepared afterwards: (operation, group, name, newName, code))
STEPS = [
    ('createGroup', 'root', GROUP, None, [('createGroup', 'root', GROUP, None, 'WatchGroupCollision')]),
    ('createTable', 'root/' + GROUP, TABLE, None, [('createTable', 'root', TABLE, None, 'WatchTableCollision'),
                                                    ('deleteGroup', 'root', GROUP, None, 'WatchGroupNotEmpty')]),
    ('renameGroup', 'root', GROUP, GROUP2, [('renameGroup', 'root', GROUP, 'WTL_Other', 'WatchGroupNotFound')]),
    ('renameTable', 'root/' + GROUP2, TABLE, TABLE2, [('renameTable', 'root/' + GROUP2, TABLE, 'WTL_Other', 'WatchTableNotFound')]),
    ('deleteTable', 'root/' + GROUP2, TABLE2, None, [('deleteTable', 'root/' + GROUP2, TABLE2, None, 'WatchTableNotFound')]),
    ('deleteGroup', 'root', GROUP2, None, [('deleteGroup', 'root', GROUP2, None, 'WatchGroupNotFound')]),
]


def profile(operation):
    return 'watch.%s-%s.v1' % (operation[-5:].lower(), operation[:-5])


def lines(root, snapshot):
    """Manifest lines of the captured group subtree (watch-group.v1)."""
    target = snapshot['target']
    require(target['kind'] == 'watchGroup' and target['isConsistent'] is True, 'Group capture required')
    text = base.artifact(Path(root), target['artifact']).read_bytes().decode('utf-8')
    require(text.startswith('watch-group.v1\n'), 'Not a watch group manifest')
    return sorted(text[len('watch-group.v1\n'):].split('\n')[:-1])


def expect(before, operation, name, new, renamed):
    """Independent expected subtree after one lifecycle operation on a direct child; '*' is the unknown export hash."""
    rel = lambda line: line.split('\t')[1]
    after = list(before)
    if operation == 'createTable':
        after.append('watch\t%s\t*\t0' % name)
    elif operation in ('renameTable', 'deleteTable'):
        old = next(line for line in before if line.startswith('watch\t%s\t' % name))
        after.remove(old)
        if operation == 'renameTable':
            after.append('\t'.join(['watch', new, renamed, old.split('\t')[3]]))
    elif operation == 'createGroup':
        after.append('group\t' + name)
    elif operation == 'deleteGroup':
        require(not any(rel(line).startswith(name + '/') for line in before), 'Group not empty')
        after.remove('group\t' + name)
    else:
        after = [line.replace('\t' + name, '\t' + new, 1) if rel(line) == name or rel(line).startswith(name + '/') else line for line in before]
    return sorted(after)


def matches(expected, actual):
    return len(expected) == len(actual) and all(
        len(e.split('\t')) == len(a.split('\t')) and all(x in ('*', y) for x, y in zip(e.split('\t'), a.split('\t')))
        for e, a in zip(expected, actual))


def absolute(manifest, group):
    """Root-relative lines of a group-scoped manifest."""
    prefix = group[len('root/'):] + '/' if group != 'root' else ''
    out = set()
    for line in manifest:
        parts = line.split('\t')
        parts[1] = prefix + parts[1]
        out.add('\t'.join(parts))
    return out


def within(manifest, group):
    prefix = group[len('root/'):] + '/' if group != 'root' else ''
    return {line for line in manifest if line.split('\t')[1].startswith(prefix)}


def review(root, outer, operation, name, new):
    """Independent file-only review of one prepared lifecycle operation; returns the digest to approve."""
    record, proposal = outer['operation'], outer['review']
    plan, before, edit = record['plan'], proposal['before'], proposal['edit']
    require(record['state'] == 'prepared' and record['revision'] == 0 and record['previousSha256'] is None
            and plan['operation'] == 'watchLifecycle', 'Initial lifecycle operation required')
    require(base.canonical_sha(plan) == record['planSha256'], 'Plan digest mismatch')
    require(plan['target'] == before['observation'], 'Plan target mismatch')
    require(base.load(base.artifact(root, outer['reviewArtifact'])) == proposal, 'Retained review mismatch')
    for entry in plan['inputs']:
        base.artifact(root, entry)
    identity = lambda item: (base.artifact(root, item).resolve(), item['sha256'])
    require([identity(i) for i in plan['inputs'][:2]] == [identity(outer['reviewArtifact']), identity(before['target']['artifact'])], 'Plan inputs differ')
    require(outer['productionApplyEnabled'] is False and [g['profile'] for g in proposal['profileGates']] == [profile(operation)]
            and edit['requiredProfiles'] == [profile(operation)], 'Profile gate differs')
    require(edit['operation'] == operation and edit['name'] == name and edit['newName'] == new
            and proposal['mode'] == 'lifecycle' and proposal['selector']['targetKind'] == 'watchTableGroup', 'Edit differs from the request')
    require((edit['renamedSemanticSha256'] is not None) == (operation == 'renameTable'), 'Renamed table hash only for renameTable')
    expected = expect(lines(root, before), operation, name, new, edit['renamedSemanticSha256'])
    require(edit['expectedGroup'] == expected, 'Expected subtree differs: %s != %s' % (edit['expectedGroup'], expected))
    return {'operation': operation, 'planSha256': record['planSha256'], 'expectedGroup': expected, 'reviewedPlanSha256': record['planSha256']}


class Run(owned.Run):
    def __init__(self, args):
        super().__init__(args)
        source = Path(__file__)
        shutil.copyfile(source, self.root / 'controller-source' / source.name)
        row = {'path': str(source.resolve()), 'sha256': workflow.digest(source)}
        self.sources.append(row)
        self.save('lifecycle-inputs', {'sources': [row], 'steps': STEPS, 'automaticRetry': False})

    def prepare(self, operation, group, name, new, key, label=None):
        tool, field = TOOLS[operation]
        arguments = {field: name, 'idempotencyKey': key, 'plcPath': self.plc, 'group': group}
        if new is not None:
            arguments['newName'] = new
        return self.call(tool, arguments, label)

    def refuse_prepare(self, operation, group, name, new, code, label):
        try:
            self.prepare(operation, group, name, new, 'wtl-' + label)
        except workflow.ToolError as error:
            require(error.packet['error']['code'] == code, '%s refused with %s, expected %s' % (label, error.packet['error'], code))
            return self.save(label, {'operation': operation, 'group': group, 'name': name, 'newName': new, 'code': code, 'operationPrepared': False})
        raise AssertionError(label + ' was not refused')

    def execute(self):
        pid = self.start_host('none')
        self.save('lifecycle-host', {'hostProcessId': pid})
        self.start_tia()
        scratch = self.call('tia_create_scratch', label='scratch')
        require(scratch['completed'], 'Scratch creation incomplete')
        original = scratch['project']['path']
        self.plc = scratch['exports'][0]['block']['plcPath']
        seed = self.root / 'seed.xml'
        shutil.copyfile(SEED, seed)
        self.call('qualification_watch_fixture_seed', {'inputPath': str(seed), 'inputSha256': workflow.digest(seed)}, 'seeded')
        self.reopen(original)
        results, previous = [], None
        for index, (operation, group, name, new, refusals) in enumerate(STEPS, start=1):
            label = '%02d-%s' % (index, operation)
            outer = self.prepare(operation, group, name, new, 'wtl-' + label, label + '-review')
            before = lines(self.root, outer['review']['before'])
            if previous is not None:
                # Recovery, reconciliation and refusals must leave the verified original subtree unchanged.
                scope = max(group, previous[0], key=len)
                require(within(absolute(before, group), scope) == within(absolute(previous[1], previous[0]), scope),
                        label + ': captured baseline differs from the previous verified subtree')
            reviewed = self.save(label + '-independent-review', {**review(self.root, outer, operation, name, new), 'approved': True})
            oid = outer['operation']['id']
            applied = self.call('watch_table_apply', {'operationId': oid, 'reviewedPlanSha256': reviewed['reviewedPlanSha256']}, label + '-applied')
            after = lines(self.root, applied['applied'])
            require(matches(reviewed['expectedGroup'], after) and applied['applied']['peerSha256'] == outer['review']['before']['peerSha256'],
                    label + ': applied subtree or peers differ')
            self.reopen(original)
            verified = self.call('watch_table_verify_reopen', {'operationId': oid}, label + '-verified')
            require(lines(self.root, verified['reopened']) == after, label + ': fresh reopen differs from the applied subtree')
            status = self.call('engineering_operation_status', {'operationId': oid}, label + '-status')
            require(len(status['checkpoints']) == 1, label + ': one checkpoint required')
            self.close_tia()
            self.start_tia()
            recovered = self.call('watch_table_recover', {'operationId': oid}, label + '-recovered')
            restored = recovered['recovered']['project']['path']
            require(lines(self.root, recovered['restored']) == before, label + ': restored subtree differs from the pre-operation subtree')
            self.reopen(restored)
            self.call('watch_table_reconcile_recovery', {'operationId': oid, 'role': 'restored'}, label + '-restored-verified')
            self.reopen(original)
            self.call('watch_table_reconcile_recovery', {'operationId': oid, 'role': 'original'}, label + '-reconciled')
            final = self.call('engineering_operation_status', {'operationId': oid}, label + '-final-status')
            for refusal_index, refusal in enumerate(refusals, start=1):
                self.refuse_prepare(*refusal, label='%s-refusal-%d' % (label, refusal_index))
            results.append({'operation': operation, 'profile': profile(operation), 'id': oid, 'state': final['state'],
                            'recoveryPath': restored, 'subtree': after, 'refusals': [r[-1] for r in refusals]})
            previous = (group, after)
        self.close_tia()
        self.close_host()
        return self.save('result', {'completed': True, 'projectPath': original, 'operations': results, 'nativeOnline': False})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('host', 'worker', 'root'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    args.mode, args.comment_input, args.native_cleanup_helper = 'lifecycle', None, None
    result = owned.run_owned(Run(args))
    print(json.dumps({'completed': result['completed'], 'root': str(args.root)}), flush=True)


if __name__ == '__main__':
    main()
