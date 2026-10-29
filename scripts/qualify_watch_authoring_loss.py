"""One exact creation response-loss trial; original handles close before restart."""
import argparse
import json
from pathlib import Path
import shutil

import qualify_watch_authoring as authoring
import qualify_watch_production_host as owned
import qualify_watch_mcp as workflow
import verify_watch_authoring_loss as oracle
from retained_native_lifecycle import require


class Run(authoring.Run):
    def __init__(self, args):
        super().__init__(args)
        rows = []
        for name in ('qualify_watch_authoring_loss.py', 'verify_watch_authoring_loss.py'):
            source = Path(__file__).with_name(name)
            shutil.copyfile(source, self.root/'controller-source'/name)
            row = {'path': str(source.resolve()), 'sha256': workflow.digest(source)}
            self.sources.append(row); rows.append(row)
        self.save('creation-loss-inputs', {'sources': rows, 'point': oracle.POINT, 'automaticReplay': False})

    def interrupted_apply(self, operation):
        try:
            self.call('watch_table_apply', {'operationId': operation['id'], 'reviewedPlanSha256': operation['digest']}, 'unexpected-applied')
        except Exception as error:
            self.save('expected-response-loss', {'error': str(error), 'automaticReplay': False})
            marker = self.root/'interruption-marker.json'
            require(marker.exists(), 'Unexpected failure without admitted creation reply-loss marker; never retry')
            retained = json.loads(marker.read_text(encoding="utf-8"))
            require(retained['point'] == oracle.POINT and retained['response']['saved'] is True
                    and retained['response']['target']['exists'] is True, 'Saved creation response-loss marker required')
            self.close_host(expected_loss=True)
            self.start_host('none')
            self.recover(operation)
            return
        raise RuntimeError('Admitted Host creation-response interruption did not fire')

    def execute(self):
        pid = self.start_host(oracle.POINT)
        self.save('authoring-host', {'hostProcessId': pid})
        admission = json.loads((self.root/'admission.json').read_text(encoding="utf-8"))
        admission['hostProcessId'] = pid
        (self.root/'admission.json').write_text(json.dumps(admission, indent=2), encoding='utf-8')
        self.start_tia()
        scratch = self.call('tia_create_scratch', label='scratch')
        require(scratch['completed'], 'Scratch creation incomplete')
        original = scratch['project']['path']
        exports = {e['block']['name']: e['block'] for e in scratch['exports']}
        fb, db = (exports[k] for k in ('TiaMcp_EnableGate', 'TiaMcp_EnableGate_DB'))
        generated = self.call('watch_table_generate', {'projectPath': original, 'plcPath': fb['plcPath'], 'blockPath': fb['path'],
            'instanceDbPath': db['path'], 'tableName': authoring.oracle.TABLE, 'maxRows': 64}, 'generated')
        rows = [r for r in generated['plan']['rows'] if r['candidate']['memberPath'] == ['Active'] and r['candidate']['datatype'] == 'Bool']
        require(len(rows) == 1, 'Exactly one captured Active Bool row required')
        request = {'generationId': generated['generationId'], 'generationSha256': generated['generationSha256'],
            'idempotencyKey': 'exact-creation-reply-loss', 'mode': 'create',
            'destination': {'projectPath': original, 'plcPath': fb['plcPath'], 'group': 'root', 'tableName': authoring.oracle.TABLE},
            'steps': [{'kind': 'add', 'candidateId': rows[0]['candidateId'], 'templateOrdinal': 0}]}
        self.save('authoring-request', request)
        prepared = self.call('watch_table_prepare_authoring', {'request': request}, 'review')
        require(prepared['review']['exactProfileCandidate']['scopeMatched'], 'Creation lies outside exact candidate')
        reviewed = authoring.oracle.reviewed_candidate(self.root)
        self.save('independent-review', dict(reviewed, approved=True))
        (self.root/'approved-digest').write_text(reviewed['reviewedPlanSha256'], encoding='utf-8')
        operation = self.save('operation', {'id': prepared['operation']['id'], 'projectPath': original, 'digest': reviewed['reviewedPlanSha256']})
        self.interrupted_apply(operation)
        self.close_host()
        result = oracle.verify(self.root)
        self.save('creation-loss-verification', result)
        require(result['completed'], str(result['issues']))
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('host', 'worker', 'root'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    args.mode, args.comment_input, args.native_cleanup_helper = oracle.POINT, None, None
    result = owned.run_owned(Run(args))
    print(json.dumps({'completed': result['completed'], 'root': str(args.root)}), flush=True)


if __name__ == '__main__': main()
