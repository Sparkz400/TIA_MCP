"""WT-05: one planner-backed native Bool creation, with exact owned lifetimes.

No seed watch import, online action, automatic retry or production gate override
outside the explicitly isolated qualification Host.
"""
import argparse
import json
from pathlib import Path
import shutil

import qualify_watch_production_host as owned
import qualify_watch_mcp as workflow
import verify_watch_authoring as oracle
from retained_native_lifecycle import require


class Run(owned.Run):
    def __init__(self, args):
        super().__init__(args)
        extra = []
        for name in ('qualify_watch_authoring.py', 'verify_watch_authoring.py'):
            source = Path(__file__).with_name(name)
            target = self.root / 'controller-source' / name
            shutil.copyfile(source, target)
            row = {'path': str(source.resolve()), 'sha256': workflow.digest(source)}
            self.sources.append(row)
            extra.append(row)
        self.save('authoring-inputs', {'sources': extra, 'case': 'create-bool',
            'template': 'packagedGenuineNativeCapture', 'automaticRetry': False,
            'scope': 'One direct root instance Bool row; default reason comment; no maintenance or explicit comment profile.'})

    def after_generate(self, generated, original):
        pass

    def after_prepare(self, request, prepared):
        pass

    def after_apply(self, request, applied):
        pass

    def after_verified(self):
        pass

    def execute(self):
        pid = self.start_host('none')
        self.save('authoring-host', {'hostProcessId': pid})
        self.start_tia()
        scratch = self.call('tia_create_scratch', label='scratch')
        require(scratch['completed'], 'Scratch creation incomplete')
        original = scratch['project']['path']
        # Keep the worker's current newly-created scratch ownership for the
        # first-table import. ScratchCreate already saved the baseline. Unlike
        # the seeded edit harness, no fixture seed has registered a reopen path.
        exports = {e['block']['name']: e['block'] for e in scratch['exports']}
        fb, db = (exports[k] for k in ('TiaMcp_EnableGate', 'TiaMcp_EnableGate_DB'))
        require(fb['plcPath'] == db['plcPath'], 'Source PLC mismatch')
        generated = self.call('watch_table_generate', {'projectPath': original,
            'plcPath': fb['plcPath'], 'blockPath': fb['path'], 'instanceDbPath': db['path'],
            'tableName': oracle.TABLE, 'maxRows': 64}, 'generated')
        self.after_generate(generated, original)
        rows = [r for r in generated['plan']['rows']
                if r['candidate']['memberPath'] == ['Active'] and r['candidate']['datatype'] == 'Bool']
        require(len(rows) == 1, 'Exactly one captured Active Bool candidate required')
        request = {'generationId': generated['generationId'], 'generationSha256': generated['generationSha256'],
            'idempotencyKey': 'wt05-create-bool', 'mode': 'create',
            'destination': {'projectPath': original, 'plcPath': fb['plcPath'], 'group': 'root', 'tableName': oracle.TABLE},
            'steps': [{'kind': 'add', 'candidateId': rows[0]['candidateId'], 'templateOrdinal': 0}]}
        self.save('authoring-request', request)
        prepared = self.call('watch_table_prepare_authoring', {'request': request}, 'review')
        reviewed = oracle.reviewed_candidate(self.root)
        self.save('independent-review', {**reviewed, 'approved': True})
        (self.root / 'approved-digest').write_text(reviewed['reviewedPlanSha256'], encoding='utf-8')
        operation = self.save('operation', {'id': prepared['operation']['id'], 'projectPath': original,
                                          'digest': reviewed['reviewedPlanSha256']})
        oid = operation['id']
        self.after_prepare(request, prepared)
        failure = None
        try:
            applied = self.call('watch_table_apply', {'operationId': oid, 'reviewedPlanSha256': operation['digest']}, 'applied')
            self.after_apply(request, applied)
            self.reopen(original)
            self.call('watch_table_verify_reopen', {'operationId': oid}, 'verified')
            self.after_verified()
        except workflow.ToolError as error:
            # A retained failed readback must still permit explicit checkpoint
            # recovery. Never replay apply or rewrite the failed trial as passed.
            failure = error
            self.save('failure', {'error': str(error), 'automaticReplay': False})
        status = self.call('engineering_operation_status', {'operationId': oid}, 'before-recovery-status')
        if not status['checkpoints'] and failure is not None:
            raise failure
        require(len(status['checkpoints']) == 1, 'No retained checkpoint; recovery cannot proceed')
        self.close_tia()
        self.start_tia()
        restored = self.restore(oid, original)
        self.close_tia()
        self.close_host()
        self.save('result', {'completed': failure is None, 'projectPath': original,
            'recoveryPath': restored, 'id': oid, 'nativeOnline': False})
        verdict = oracle.verify(self.root)
        self.save('independent-verification', verdict)
        require(verdict['completed'], str(verdict['issues']))
        return verdict


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
