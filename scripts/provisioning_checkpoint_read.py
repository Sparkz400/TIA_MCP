"""Verified read/copy of one failed provisioning checkpoint; never reconciliation.

Used only inside StoreExclusiveWindow after the original native job is terminal.
The source reservation remains pending and TIA receives a separate frozen copy.
"""
import hashlib
import json
from pathlib import Path
import re

import native_dispatch_guard as guard
from qualify_translation_category_probe import canonical_sha, overlaps, ref, require, safe_path, save


def file_hash(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def archive_ref(path):
    path = safe_path(path)
    return dict(path=str(path), bytes=path.stat().st_size, sha256=file_hash(path))


def decode_json(text):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'Duplicate checkpoint evidence property')
            result[key] = value
        return result
    return json.loads(text, object_pairs_hook=unique)


def read_json(path):
    path = safe_path(path)
    require(path.is_file() and path.stat().st_size <= 8 * 1024 * 1024, 'Checkpoint evidence is missing or oversized')
    return decode_json(path.read_text(encoding='utf-8-sig'))


class ProvisioningCheckpointRead:
    def __init__(self, store, admission_id, report_path, dispatch_intent):
        require(isinstance(admission_id, str) and re.fullmatch(r'[0-9a-f]{32}', admission_id), 'Exact admission ID required')
        self.store, self.report_path, self.intent_path = map(safe_path, (store, report_path, dispatch_intent))
        self.admission_id = admission_id
        self.handles, self.pins = [], []
        self.window = self.archive = self.copy = None
        self.used = self.admitted = False
        self.close_receipt = None

    def acquire(self, window):
        require(not self.used and self.store in window.roots, 'Checkpoint read needs its original locked store and one unused policy')
        self.used, self.window = True, window
        observations = window.evidence['nativeDispatchGuard']['observations']
        observed = [r for r in observations if r.get('intent') == guard.reference(self.intent_path)]
        require(len(observed) == 1 and 'terminal' in observed[0], 'Original native job is not verified terminal in this registry')
        intent = read_json(self.intent_path)
        terminal_path = Path(observed[0]['terminal']['path'])
        terminal = read_json(terminal_path)
        job_path = Path(terminal['jobEvidence']['path'])
        job = read_json(job_path)
        require(job.get('activeProcesses') == 0 and job.get('createdAtomicallyInJob') is True
                and job.get('helperHandleSignaled') is True and job.get('totalProcesses', 0) > 0,
                'A never-launched or live job cannot authorize checkpoint recovery')
        source_path = safe_path(intent['source']['path'])
        require(guard.reference(source_path) == intent['source'], 'Original dispatch request changed')
        source = read_json(source_path)

        directory = self.store / 'engineering-admissions' / self.admission_id
        require(sorted(p.name for p in directory.glob('*.json')) == ['00000000.json', '00000001.json'],
                'Checkpoint reading requires the original unreconciled admission')
        latest_path = directory / '00000001.json'
        record = read_json(latest_path)
        require(record.get('id') == self.admission_id and record.get('state') == 'outcomeUnknown'
                and record.get('reconciliation') == [], 'Only an unreconciled failed admission may use this read path')
        relative_failure = 'mutation-responses/' + self.admission_id + '/failure.json'
        failure_path = self.store / relative_failure
        require(record.get('response', {}).get('relativePath') == relative_failure
                and file_hash(failure_path) == record['response']['sha256'], 'Durable failure reference differs')
        failure = read_json(failure_path)
        report = read_json(self.report_path)
        require(failure.get('admissionId') == self.admission_id and failure.get('nativeOutcome') == 'unknown'
                and failure.get('automaticReplay') is False
                and isinstance(failure.get('nativeErrorCode'), str) and failure['nativeErrorCode']
                and failure.get('nativeDetailsSha256') == canonical_sha({'details': report})
                and failure.get('nativeErrorCode') == report.get('failure', {}).get('code'),
                'Original provisioning report does not match the durable native failure')
        baseline, scope = report['recoveryBaseline'], record['scope']
        require(scope.get('operation') == 'simulation_provision_scratch_credential'
                and baseline.get('nativeScope') == scope, 'Checkpoint scope differs from the failed provisioning admission')
        resources = scope['resources']
        projects = [r for r in resources if r['kind'] == 'project']
        stores = [r for r in resources if r['kind'] == 'workspace']
        require(len(resources) == 2 and len(projects) == len(stores) == 1, 'Unsupported provisioning resource scope')
        storage = safe_path(stores[0]['path'])
        project = safe_path(projects[0]['path'])
        require(storage.is_relative_to(self.store) and safe_path(baseline['project']['path']) == project,
                'Checkpoint project/storage does not match its original store')
        require(self.report_path.parent == storage and safe_path(report['reportPath']) == self.report_path
                and re.fullmatch(r'simulation-provision-[0-9a-f]{32}\.json', self.report_path.name), 'Checkpoint report is outside its owned storage')
        worker = source['binaries']['worker']
        executable = safe_path(worker['path'])
        bound = [f for f in worker['files'] if safe_path(f['path']) == executable]
        require(len(bound) == 1 and bound[0]['sha256'] == scope['adapterSha256']
                and file_hash(executable) == scope['adapterSha256'], 'Original native adapter bytes differ')
        require(source.get('automaticReplay') is False
                and any(r['kind'] == 'workspace' and safe_path(r['path']) == self.store for r in source['resources'])
                and any(r['kind'] == 'project' and project.is_relative_to(safe_path(r['path'])) for r in source['resources']),
                'Original terminal dispatch did not own this scratch project/storage')

        checkpoint_path = safe_path(report['checkpointPath'])
        require(checkpoint_path == self.report_path.with_suffix('.checkpoints.jsonl')
                and checkpoint_path.stat().st_size <= 8 * 1024 * 1024, 'Checkpoint stream differs or exceeds its bound')
        lines = checkpoint_path.read_text(encoding='utf-8-sig').splitlines()
        require(1 <= len(lines) <= 4096, 'Checkpoint stream exceeds its record bound')
        rows = [decode_json(line) for line in lines]
        phases = [row['phase'] for row in rows]
        required = ['attemptPrepared', 'beforeProjectArchive', 'afterProjectArchive']
        require(all(phases.count(p) == 1 for p in required)
                and [p for p in phases if p in required] == required, 'Archive intent/return order is unverified')
        after = phases.index('afterProjectArchive')
        require(rows[after]['recoveryBaseline'] == baseline
                and all(rows[phases.index(p)]['recoveryBaseline']['projectArchive'] is None for p in required[:2])
                and all(i > after for i,p in enumerate(phases) if p in ('beforeStageProtectedCard', 'beforeEmptyPowerOn')),
                'Archive return is not the recorded before-effect checkpoint')
        archive = baseline['projectArchive']
        self.archive = safe_path(archive['path'])
        require(self.archive == storage / 'before-provision.zap21'
                and self.archive == safe_path(baseline['projectArchivePath'])
                and type(archive.get('bytes')) is int and 0 < archive['bytes'] <= 2 * 1024**3,
                'Unsupported checkpoint archive identity or size')
        from qualify_translation_category_probe import resource_key
        self.archive_key = resource_key('workspace', self.archive)
        declared = [r for r in window.evidence['resources'] if resource_key(r['kind'], r['path']) == self.archive_key]
        require(len(declared) == 1 and declared[0].get('access') == 'readCheckpoint', 'Exact archive read intent required')
        self.latest, self.baseline = ref(latest_path), baseline
        pin_paths = [directory / '00000000.json', latest_path, failure_path, self.report_path, checkpoint_path,
                     self.intent_path, terminal_path, job_path, source_path]
        self.pins = [ref(path) for path in pin_paths]
        self.handles.append(window.api.read_only(self.archive))
        require(safe_path(self.archive) == self.archive and self.archive.stat().st_size == archive['bytes']
                and file_hash(self.archive) == archive['sha256'], 'Read-locked archive differs from its native checkpoint')
        return dict(policy='provisioning-checkpoint-read.v1', admissionId=self.admission_id,
                    archive=archive, originalEvidence=self.pins, writeDeleteSharingDenied=True,
                    admissionReleased=False, nativeOutcomeReconciled=False)

    def permits(self, root, directory, record, owned, requested):
        allowed = (self.handles and not self.admitted and root == self.store
                   and directory == self.store / 'engineering-admissions' / self.admission_id
                   and ref(directory / '00000001.json') == self.latest
                   and record.get('state') == 'outcomeUnknown' and record.get('scope') == self.baseline['nativeScope']
                   and all(key == self.archive_key for key in requested if any(overlaps(key, owner) for owner in owned)))
        if allowed:
            self.admitted = True
        return bool(allowed)

    def copy_to(self, destination):
        """Create one hash-bound recovery input only after the complete store scan."""
        require(self.window is not None and self.window.entered and self.admitted and self.copy is None,
                'Checkpoint copy requires the held admitted window and a fresh destination')
        from qualify_translation_category_probe import resource_key
        destination = safe_path(destination)
        key = resource_key('workspace', destination)
        require(key in self.window.keys and key != self.archive_key and destination.suffix.lower() == '.zap21'
                and destination.parent.is_dir() and not destination.exists()
                and not destination.is_relative_to(self.store), 'Recovery copy must be a separately declared new archive outside the original store')
        prefix = str(self.window.prefix) + '-checkpoint-copy'
        save(prefix + '-intent.json', dict(source=self.baseline['projectArchive'], destination=str(destination), admissionReleased=False))
        self.copy = destination
        with self.archive.open('rb') as source, destination.open('xb') as target:
            for chunk in iter(lambda: source.read(1024 * 1024), b''):
                target.write(chunk)
            target.flush()
            import os
            os.fsync(target.fileno())
        self.handles.append(self.window.api.read_only(destination))
        require(destination.stat().st_size == self.baseline['projectArchive']['bytes']
                and file_hash(destination) == self.baseline['projectArchive']['sha256'], 'Recovery copy differs from the immutable checkpoint')
        self.copy_ref = archive_ref(destination)
        save(prefix + '-return.json', dict(archive=self.copy_ref, source=self.baseline['projectArchive'], admissionReleased=False))
        return self.copy_ref

    def release(self):
        error = None
        closed = []
        acquired = bool(self.handles)
        try:
            if self.handles:
                require([ref(p['path']) for p in self.pins] == self.pins, 'Original checkpoint/admission evidence changed during recovery reading')
                require(file_hash(self.archive) == self.baseline['projectArchive']['sha256'], 'Original recovery archive changed')
                if self.copy is not None:
                    require(hasattr(self, 'copy_ref') and archive_ref(self.copy) == self.copy_ref, 'Recovery copy is partial or changed')
        except Exception as caught:
            error = caught
        finally:
            for handle in reversed(self.handles):
                closed.append(self.window.api.close(handle))
            self.handles = []
        self.close_receipt = dict(acquired=acquired, allReadHandlesClosed=all(closed), originalEvidenceUnchanged=acquired and error is None,
                                  admissionReleased=False, failure=None if error is None else str(error))
        require(all(closed), 'A checkpoint read handle did not close')
        if error is not None:
            raise error
        return self.close_receipt
