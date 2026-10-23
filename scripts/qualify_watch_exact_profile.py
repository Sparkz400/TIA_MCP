"""WT-06 exact candidate + native negatives. One fresh owned run; never retries.

The real saved FB HeaderAuthor drift exercises the native pre-effect source guard in
the qualification helper. Host prepare collisions and a late dispatch collision
exercise their actual MCP boundaries. No generic profile is promoted.
"""
import argparse
import copy
import json
from pathlib import Path
import shutil

import qualify_watch_authoring as authoring
import qualify_watch_mcp as workflow
import qualify_watch_production_host as owned
import verify_watch_exact_profile as oracle
from retained_native_lifecycle import require


class Run(authoring.Run):
    def __init__(self, args):
        super().__init__(args)
        extra = []
        for name in ('qualify_watch_exact_profile.py', 'verify_watch_exact_profile.py'):
            source = Path(__file__).with_name(name)
            shutil.copyfile(source, self.root / 'controller-source' / name)
            row = {'path': str(source.resolve()), 'sha256': workflow.digest(source)}
            self.sources.append(row)
            extra.append(row)
        self.save('exact-profile-inputs', {'sources': extra, 'productionActivation': False,
            'profile': oracle.PROFILE, 'automaticRetry': False})

    def after_generate(self, generated, original):
        retained = authoring.oracle.artifact(self.root, generated['artifact'])
        self.binding = json.loads(retained.read_text(encoding='utf-8'))['capture']['binding']
        try:
            result = self.call('qualification_watch_authoring_source_negative',
                {'sourceBinding': self.binding}, 'source-negative')
        except workflow.ToolError:
            # Finally-restoration can succeed while the negative assertion fails.
            # Release its shared owner only after a fresh native baseline proof;
            # a still-changed original stays reserved. Never repeat the helper.
            unresolved = []
            directory = self.root/'engineering-admissions'
            for folder in directory.iterdir() if directory.exists() else ():
                records = sorted(folder.glob('*.json')) if folder.is_dir() else []
                if records:
                    record = json.loads(records[-1].read_text(encoding='utf-8'))
                    if record['scope']['operation'] == 'watch_authoring_source_negative' and record['state'] in ('dispatchIntent', 'outcomeUnknown'):
                        unresolved.append(record)
            require(len(unresolved) == 1, 'Cannot identify one exact source-negative owner; retain failure without retry')
            self.reopen(original)
            self.call('qualification_watch_authoring_source_reconcile', {'admissionId': unresolved[0]['id']}, 'failed-source-negative-reconciliation')
            raise
        oracle.source_negative(self.root, result)
        self.reopen(original)
        # A fresh process must independently reproduce every original source/peer.
        request = {k: self.binding[k] for k in ('projectPath', 'plcPath', 'blockPath', 'instanceDbPath')}
        request.update(tableName=authoring.oracle.TABLE, maxRows=64)
        self.call('watch_table_generate', request, 'source-restored-fresh-generation')

    def after_prepare(self, request, prepared):
        status = prepared['review']['exactProfileCandidate']
        require(status['scopeMatched'] and status['profile'] == oracle.PROFILE and not status['productionApplyEnabled'],
                'Reviewed operation is outside the exact still-disabled candidate')
        duplicate = copy.deepcopy(request)
        duplicate['idempotencyKey'] += '-late-collision'
        self.late = self.call('watch_table_prepare_authoring', {'request': duplicate}, 'late-collision-review')

    def expect_refusal(self, name, arguments, expected, label):
        try:
            self.call(name, arguments)
        except workflow.ToolError as error:
            require(error.packet['error']['code'] == expected, str(error.packet))
            self.save(label, {'code': expected, 'arguments': arguments, 'automaticReplay': False})
            return
        raise RuntimeError('Expected native/Host refusal: ' + expected)

    def after_apply(self, request, applied):
        for label, name in (('exact-name', authoring.oracle.TABLE), ('case-folded-name', authoring.oracle.TABLE.lower()), ('force-name', 'Force table')):
            generate = {k: self.binding[k] for k in ('projectPath', 'plcPath', 'blockPath', 'instanceDbPath')}
            generate.update(tableName=name, maxRows=64)
            generated = self.call('watch_table_generate', generate, 'collision-' + label + '-generation')
            collision = copy.deepcopy(request)
            collision.update(generationId=generated['generationId'], generationSha256=generated['generationSha256'], idempotencyKey='collision-' + label)
            collision['destination']['tableName'] = name
            self.expect_refusal('watch_table_prepare_authoring', {'request': collision}, 'WatchTableCollision', 'collision-' + label)
        self.call('watch_table_read', {'tableName': authoring.oracle.TABLE, 'plcPath': self.binding['plcPath']}, 'after-collision-snapshot')

    def after_verified(self):
        op = self.late['operation']
        self.expect_refusal('watch_table_apply', {'operationId': op['id'], 'reviewedPlanSha256': op['planSha256']},
            'StaleNativeTarget', 'late-collision-refusal')
        # A changed worker epoch may be the earliest authoritative refusal; no
        # checkpoint or import can occur after either epoch/content mismatch.
        result = self.call('engineering_operation_status', {'operationId': op['id']}, 'late-collision-status')
        require(result['state'] == 'stopped' and not result['checkpoints'], 'Late collision left an owned operation')

    def execute(self):
        super().execute()
        result = oracle.verify(self.root)
        self.save('exact-profile-verification', result)
        require(result['completed'], str(result['issues']))
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('host', 'worker', 'root'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    args.mode, args.comment_input, args.native_cleanup_helper = 'create-bool', None, None
    result = owned.run_owned(Run(args))
    print(json.dumps({'completed': result['completed'], 'root': str(args.root)}), flush=True)


if __name__ == '__main__':
    main()
