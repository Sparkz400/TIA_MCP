"""D02: qualification_watch_authoring_source_reconcile after an actual Host loss, with exact owned lifetimes.

One owned scratch CPU 1511-1 PN. watch_table_generate captures the root EnableGate source binding. The
qualification source-negative helper then runs under a Host started with --watch-qualification
host.watch_authoring_source_negative. The worker completes the saved HeaderAuthor drift, the stale-source
refusal and the restoration, and the Host exits (74) after the native dispatch returned but before it records
the outcome, so the shared admission stays in dispatchIntent and owns the project. A second Host opens the
original in a fresh owned Portal. A replay is refused with ResourceBusy before any native effect. The
reconcile tool compares the retained pre-effect baseline with the fresh snapshot and releases the project as
unchanged; a second reconcile is refused, and a fresh capture reproduces the original source hashes.
"""
import argparse
import json
from pathlib import Path
import shutil
import types

import qualify_watch_production_host as owned
import qualify_watch_mcp as workflow
import verify_watch_workflow as base
from retained_native_lifecycle import require

POINT = 'host.watch_authoring_source_negative'
TABLE = 'WT05_BoolCreation'
# This runner's Host-loss point joins the watch harness's Host points for this process only; the watch-edit
# oracle (verify_watch_expansion) keeps its own set.
owned.oracle = types.SimpleNamespace(NATIVE_POINTS=owned.oracle.NATIVE_POINTS, HOST_POINTS=owned.oracle.HOST_POINTS | {POINT})


class Run(owned.Run):
    def __init__(self, args):
        super().__init__(args)
        source = Path(__file__)
        shutil.copyfile(source, self.root / 'controller-source' / source.name)
        row = {'path': str(source.resolve()), 'sha256': workflow.digest(source)}
        self.sources.append(row)
        self.save('reconcile-inputs', {'sources': [row], 'point': POINT, 'automaticRetry': False})

    def admissions(self):
        """The latest revision of every source-negative admission in the shared store."""
        records, directory = [], self.root / 'engineering-admissions'
        for folder in sorted(directory.iterdir()) if directory.exists() else ():
            files = sorted(folder.glob('*.json')) if folder.is_dir() else []
            if files:
                records.append(json.loads(files[-1].read_text(encoding='utf-8')))
        return [r for r in records if r['scope']['operation'] == 'watch_authoring_source_negative']

    def refuse(self, name, arguments, code, label):
        try:
            self.call(name, arguments)
        except workflow.ToolError as error:
            require(error.packet['error']['code'] == code, '%s: expected %s, observed %s' % (label, code, error.packet['error']))
            return self.save(label, {'code': code, 'arguments': arguments, 'automaticReplay': False})
        raise RuntimeError(label + ' was accepted')

    def generate(self, original, fb, db, label):
        generated = self.call('watch_table_generate', {'projectPath': original, 'plcPath': fb['plcPath'], 'blockPath': fb['path'],
                                                       'instanceDbPath': db['path'], 'tableName': TABLE, 'maxRows': 64}, label)
        return json.loads(base.artifact(self.root, generated['artifact']).read_text(encoding='utf-8'))['capture']['binding']

    def execute(self):
        self.save('reconcile-host', {'hostProcessId': self.start_host(POINT)})
        self.start_tia()
        scratch = self.call('tia_create_scratch', label='scratch')
        require(scratch['completed'], 'Scratch creation incomplete')
        original = scratch['project']['path']
        exports = {e['block']['name']: e['block'] for e in scratch['exports']}
        fb, db = exports['TiaMcp_EnableGate'], exports['TiaMcp_EnableGate_DB']
        binding = self.generate(original, fb, db, 'generated')
        try:
            self.call('qualification_watch_authoring_source_negative', {'sourceBinding': binding}, 'source-negative')
        except Exception as error:  # the Host exits at its admitted point; the request has no reply
            self.save('host-loss', {'error': str(error), 'errorType': type(error).__name__, 'automaticReplay': False})
        marker = json.loads((self.root / 'interruption-marker.json').read_text(encoding='utf-8'))
        require(marker['point'] == POINT and marker['automaticReplay'] is False, 'Host did not stop at the admitted point')
        self.close_host(expected_loss=True)
        # The worker finished before the Host stopped: drift saved and refused, original restored.
        response = marker['response']
        require(response['refusal']['code'] == 'StaleSource' and response['refusal']['guard'] == 'VerifyWatchSource'
                and response['restorationPassed'] is True and response['nativeWatchImportInvoked'] is False
                and response['changedCapture']['binding']['blockSha256'] != binding['blockSha256']
                and response['restoredCapture']['binding']['blockSha256'] == binding['blockSha256'], 'Worker response differs from a restored negative')
        unresolved = [r for r in self.admissions() if r['state'] in ('dispatchIntent', 'outcomeUnknown')]
        require(len(unresolved) == 1 and unresolved[0]['state'] == 'dispatchIntent', 'Expected one unresolved source-negative admission')
        admission = self.save('unresolved-admission', unresolved[0])['id']

        self.start_host('none')
        self.start_tia()
        self.call('project_open', {'path': original}, 'reopened')
        self.refuse('qualification_watch_authoring_source_negative', {'sourceBinding': binding}, 'ResourceBusy', 'replay-refusal')
        reconciled = self.call('qualification_watch_authoring_source_reconcile', {'admissionId': admission}, 'reconciled')
        require(reconciled['admission']['state'] == 'reconciled' and reconciled['nativeMutationInvoked'] is False
                and reconciled['automaticReplay'] is False, 'Reconciliation did not release the admission without native mutation')
        evidence = json.loads(base.artifact(self.root, reconciled['evidence']).read_text(encoding='utf-8'))
        require(evidence['before']['peers'] == evidence['current']['peers'] and evidence['current']['target']['exists'] is False
                and evidence['current']['processId'] != evidence['before']['processId'], 'Reconciliation evidence is not a fresh unchanged baseline')
        self.refuse('qualification_watch_authoring_source_reconcile', {'admissionId': admission}, 'InvalidOperationState', 'second-reconcile-refusal')
        status = self.call('engineering_operation_status', {'operationId': admission}, 'final-status')
        require(status['state'] == 'reconciled', 'Admission is not reconciled')
        fresh = self.generate(original, fb, db, 'fresh-generation')
        require(all(fresh[k] == binding[k] for k in ('blockSha256', 'instanceSha256', 'projectPath', 'plcPath')),
                'A fresh capture differs from the pre-negative source')
        self.close_tia()
        self.close_host()
        return self.save('result', {'completed': True, 'projectPath': original, 'admissionId': admission, 'point': POINT,
                                    'nativeOnline': False, 'automaticReplay': False})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('host', 'worker', 'root'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    args.mode, args.comment_input, args.native_cleanup_helper = POINT, None, None
    result = owned.run_owned(Run(args))
    print(json.dumps({'completed': result['completed'], 'root': str(args.root)}), flush=True)


if __name__ == '__main__':
    main()
