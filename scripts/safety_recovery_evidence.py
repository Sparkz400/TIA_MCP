"""Bind safety retrieval to recorded original evidence before starting a Host."""
import hashlib
import json
from pathlib import Path
import re

from api_coverage import read_bytes
from audit_retained_evidence import evidence_path
from run_block_tests import file_record
from engineering_preservation import require, native_payload_hash
from smoke_safety_direct import require_signature_evidence, require_safety_equal, native_payload_hash as legacy_payload_hash
import safety_lifecycle
from compilation_evidence import require_baseline_compile


def require_target(value, block_path, plc_path, project_path, process_id):
    require(isinstance(value, dict), 'Safety evidence must be an object')
    require(type(value.get('processId')) is int and value['processId'] == process_id,
            'Safety evidence belongs to another native process')
    block, project = value.get('block'), value.get('project')
    require(isinstance(block, dict) and block.get('path') == block_path and block.get('plcPath') == plc_path,
            'Safety evidence belongs to another selected block or PLC')
    require(isinstance(project, dict) and isinstance(project.get('path'), str)
            and Path(project['path']) == Path(project_path), 'Safety evidence belongs to another project')


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, 'Duplicate safety evidence JSON key: ' + key)
        value[key] = item
    return value


def load_source(source_path, artifacts, workspace):
    root, workspace = Path(artifacts).absolute(), Path(workspace).absolute()
    require(root.is_relative_to(workspace), 'Only fixture archives under this workspace are eligible')
    if root != workspace: evidence_path(workspace, root.relative_to(workspace).as_posix())
    inputs = {}

    def checked_path(path):
        path = Path(path)
        require(path.is_absolute() and path.is_relative_to(root), 'Safety evidence escaped the fixture artifact root')
        return evidence_path(root, path.relative_to(root).as_posix())

    def capture(path, expected=None, metadata=True):
        path = checked_path(path)
        data = read_bytes(path, 64 * 1024 * 1024) if metadata else None
        record = ({'path': str(path), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                  if metadata else file_record(path))
        if expected is not None:
            require(record['sha256'] == expected, 'Original safety evidence bytes changed: ' + str(path))
        require(str(path) not in inputs or inputs[str(path)] == record, 'Safety evidence changed during admission')
        inputs[str(path)] = record
        return data, record

    data, source_record = capture(Path(source_path).absolute())
    source = json.loads(data.decode('utf-8-sig'), object_pairs_hook=unique_object)
    safety_lifecycle.verify(source)
    projected = {'sessions': source['sessions']}; safety_lifecycle.sync_steps(projected)
    require(source['steps'] == projected['steps'], 'Safety steps differ from original session call evidence')
    steps = source['steps']
    prepared_index = next((i for i, row in enumerate(steps) if row['name'] == 'change_prepare'
        and row.get('isError') is False and row['arguments'].get('kind') == 'safety_import_xml'), None)
    require(prepared_index is not None, 'Missing successful original safety preparation')
    preparation = steps[prepared_index]; prepared = preparation['result']; preview = prepared['preview']
    change_id, preview_hash = prepared['changeId'], prepared['previewHash']
    require(isinstance(change_id, str) and re.fullmatch('[0-9a-f]{32}', change_id)
            and isinstance(preview_hash, str) and re.fullmatch('[0-9a-f]{64}', preview_hash), 'Malformed original safety change identity')
    payload = preparation['arguments']['payload']; selected = payload.get('blockPath')
    require(isinstance(selected, str) and '/blocks/' in selected and payload.get('safetyAcknowledged') is True,
            'An exact acknowledged original safety block is required')
    plc = selected.rsplit('/blocks/', 1)[0]
    require_baseline_compile(source, plc)
    project = source['projectPath']; process_id = source['sessions'][0]['processId']
    require(prepared.get('state') == 'prepared' and type(prepared.get('processId')) is int
            and prepared['processId'] == process_id and preview.get('kind') == 'safety_import_xml'
            and preview.get('changeId') == change_id and preview.get('project', {}).get('path') == project
            and preview.get('payload') == payload, 'Original safety preview context differs')
    inspections = [row['result'] for row in steps[:prepared_index] if row['name'] == 'safety_inspect'
        and row.get('isError') is False and row['arguments'].get('blockPath') == selected]
    require(inspections, 'Missing successful pre-change selected-block signatures')
    before = inspections[0]
    for inspection in inspections:
        require_target(inspection, selected, plc, project, process_id)
        require(inspection.get('plcPath') == plc, 'Program signatures belong to another PLC')
        require_safety_equal(before, inspection)
    require_signature_evidence(before)
    baseline = source.get('nativePreservation', {}).get('before')
    require(isinstance(baseline, dict) and selected in baseline, 'Missing original preservation block set')
    exports = {}
    for path, row in baseline.items():
        require(isinstance(path, str) and path.startswith(plc + '/'), 'Preservation block escaped the original PLC')
        matching = [step['result'] for step in steps[:prepared_index] if step['name'] == 'block_read'
            and step.get('isError') is False and step['arguments'].get('blockPath') == path]
        require(matching, 'Missing successful pre-change export: ' + path)
        data, record = capture(row['artifact']['path'], row['artifact']['sha256'])
        require(type(row['artifact'].get('bytes')) is int and row['artifact']['bytes'] == record['bytes'], 'Original export byte count differs')
        xml = data.decode('utf-8-sig')
        require(native_payload_hash(xml) == row.get('completeNativePayloadHash')
                and legacy_payload_hash(xml) == row.get('nativePayloadHash'), 'Original preservation digest differs')
        for exported in matching:
            require_target(exported, path, plc, project, process_id)
            raw, record = capture(exported['artifact']['path'], exported['artifact']['sha256'])
            require(type(exported['artifact'].get('bytes')) is int and exported['artifact']['bytes'] == record['bytes'], 'Recorded export byte count differs')
            require(native_payload_hash(raw.decode('utf-8-sig')) == native_payload_hash(exported['xml']) == native_payload_hash(xml),
                    'Original inline/exported safety XML differs')
        exports[path] = matching[0]
    block_name = exports[selected]['block']['name']
    require(payload.get('expectedBlockNames') == [block_name], 'Prepared block name differs from original export')
    applications = [row for row in steps[prepared_index + 1:] if row['name'] == 'change_apply'
                    and row['arguments'].get('changeId') == change_id]
    require(len(applications) == 1, 'One recorded original safety apply outcome is required')
    applied = applications[0]; result = applied.get('result')
    require(isinstance(result, dict), 'Safety apply outcome is unknown')
    if applied.get('isError'):
        result = result.get('error', {}).get('details')
    require(isinstance(result, dict) and result.get('state') in ('applied', 'recovery_required')
            and result.get('automaticRetryAllowed') is False, 'Safety apply outcome has no known checkpoint receipt')
    require(result['state'] == ('recovery_required' if applied.get('isError') else 'applied'), 'Safety apply envelope contradicts its outcome')
    require(applied['arguments'].get('previewHash') == preview_hash and applied['arguments'].get('checkpointSave') is True,
            'Original apply does not match the reviewed checkpoint request')
    journal_path = checked_path(prepared['journalPath'])
    require(journal_path.name == 'journal.json' and journal_path.parent.name == 'change-' + change_id,
            'Safety journal does not belong to the selected change')
    data, journal_record = capture(journal_path)
    journal = json.loads(data.decode('utf-8-sig'), object_pairs_hook=unique_object)
    for item in (result, journal):
        require(item.get('changeId') == change_id and item.get('previewHash') == preview_hash
                and item.get('preview') == preview, 'Safety journal/receipt differs from original preparation')
        stages = item.get('stages')
        require(isinstance(stages, list) and 'prechange_project_saved' in stages and 'recovery_archive_created' in stages
                and stages.index('prechange_project_saved') < stages.index('recovery_archive_created'), 'Missing original checkpoint stages')
    require(result.get('processId') == process_id and type(result.get('processId')) is int
            and Path(result.get('journalPath', '')) == journal_path, 'Checkpoint response belongs to another process or journal')
    require(journal.get('kind') == 'safety_import_xml' and journal.get('projectPath') == project
            and journal.get('state') in ('applied', 'recovery_required', 'restored'), 'Safety journal context or outcome is unknown')
    archive = checked_path(result['recoveryArchive'])
    digest = result.get('recoveryArchiveSha256')
    require(archive == journal_path.parent/'before.zap21' and isinstance(digest, str) and re.fullmatch('[0-9a-f]{64}', digest)
            and journal.get('recoveryArchive') == str(archive) and journal.get('recoveryArchiveSha256') == digest,
            'Journal checkpoint differs from the original apply receipt')
    _, archive_record = capture(archive, digest, metadata=False)
    require(archive_record['bytes'] > 0, 'Original safety checkpoint is empty')
    return {'source': source, 'prepared': prepared, 'before': before, 'exported': exports[selected], 'exports': exports,
            'selectedPath': selected, 'plcPath': plc, 'blockName': block_name, 'projectPath': project,
            'archive': archive_record, 'sourceRecord': source_record, 'journalRecord': journal_record,
            'inputs': list(inputs.values()), 'artifactRoot': str(root), 'sourceAuthenticityVerified': False}


def recheck_inputs(packet):
    root = Path(packet['artifactRoot'])
    for record in packet['inputs']:
        path = evidence_path(root, Path(record['path']).relative_to(root).as_posix())
        require(file_record(path) == record, 'Original safety input changed after admission: ' + str(path))
