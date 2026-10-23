"""Closed encoder file admission and source-preserving inspection copies."""
import hashlib
import json
from pathlib import Path
import re

from api_coverage import read_bytes
from encoder_checkpoint import verify_seeder_checkpoint
from encoder_lifecycle import verify_lifecycle
from engineering_preservation import require
from hmi_fixture_copy import safe_path, project_tree, copy_closed_project, verify_source_preserved
from qualify_technology_families import same
from run_block_tests import file_record
from safety_recovery_evidence import unique_object
from smoke_mcp import ROOT

POLICY = 'encoder-closed-inspection-copies-v1'


def project_paths(source):
    root = safe_path(Path(source['runDirectory']))
    require(root.is_relative_to(ROOT / 'artifacts'), 'Encoder run must belong to workspace artifacts')
    paths = {label: safe_path(Path(source[key]))
             for label, key in (('changed', 'projectPath'), ('recovered', 'recoveredProjectPath'))}
    for path in paths.values():
        require(path.is_relative_to(root) and path.suffix == '.ap21' and path.stem.startswith('TiaMcpScratch_') and
                path.parent.name == path.stem and path.is_file(), 'An exact generated encoder project is required')
    first, second = (path.parent for path in paths.values())
    require(not first.is_relative_to(second) and not second.is_relative_to(first), 'Encoder saved project roots overlap')
    return paths


def snapshot(path):
    first = project_tree(path.parent)
    require(same(first, project_tree(path.parent)), 'Closed encoder project changed during capture')
    return {'projectPath': str(path), 'tree': first}


def edit_checkpoint(source):
    applied, restored = source['edit']['applied'], source['recovery']['restored']
    path, digest = applied.get('recoveryArchive'), applied.get('recoveryArchiveSha256')
    require(isinstance(path, str) and isinstance(digest, str) and re.fullmatch('[0-9a-f]{64}', digest) and
            restored.get('recoveryArchive') == path and restored.get('recoveryArchiveSha256') == digest,
            'Recovery archive differs from the original pre-edit checkpoint receipt')
    path = safe_path(Path(path))
    require(path.is_relative_to(Path(source['runDirectory'])) and path.suffix == '.zap21', 'Edit checkpoint escaped its run')
    record = file_record(path)
    require(record['sha256'] == digest and record['bytes'] > 0, 'Original pre-edit checkpoint bytes changed')
    return record


def capture_closed(source, report_path):
    from qualify_encoder_hardware import require_source_compiles
    verify_lifecycle(source)
    require_source_compiles(source)
    paths = project_paths(source)
    output = safe_path(Path(report_path).absolute())
    require(all(not output.is_relative_to(path.parent) for path in paths.values()), 'Source report cannot be stored inside a project tree')
    return {'policy': POLICY, 'copies': {label: snapshot(path) for label, path in paths.items()},
            'checkpoint': edit_checkpoint(source), 'terminalSessionCount': len(source['sessions']),
            'sourceAuthenticityVerified': False, 'atomicSnapshot': False}


def load_source(location, digest):
    import encoder_software_preservation as software_peers
    import encoder_hardware_preservation as hardware_peers
    from qualify_encoder_hardware import require_source_compiles
    location = safe_path(Path(location).absolute())
    require(location.is_relative_to(ROOT / 'artifacts') and isinstance(digest, str) and re.fullmatch('[0-9a-f]{64}', digest),
            'An exact retained source report and SHA-256 are required')
    raw = read_bytes(location, 64 * 1024 * 1024)
    require(hashlib.sha256(raw).hexdigest() == digest, 'Encoder source report hash differs')
    source = json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique_object)
    verify_lifecycle(source)
    require(source.get('completed') is True and source.get('qualificationChecksPassed') is True and
            'failure' not in source and 'postCleanupFailure' not in source, 'Only a completed encoder run authorizes copied inspection')
    require_source_compiles(source)
    require(source.get('softwarePeerPolicy') == software_peers.POLICY, 'Current software peer evidence is required for copied inspection')
    require(source.get('hardwarePeerPolicy') == hardware_peers.POLICY, 'Current hardware peer evidence is required for copied inspection')
    expected = source.get('closedCopies')
    require(isinstance(expected, dict) and same(expected, capture_closed(source, location)),
            'Closed encoder files or original terminal snapshot differ')
    inputs = [{'path': str(location), 'bytes': len(raw), 'sha256': digest}, expected['checkpoint'], verify_seeder_checkpoint(source)]
    helper = next(row for row in source['seederLifetimes'] if row['mode'] == 'connect')
    for path, expected_hash in ((helper['authorization'], helper['authorizationSha256']), (helper['stdout'], helper['outcomeSha256']),
                                (source['seederConnect']['checkpointReceipt']['path'], source['seederConnect']['checkpointReceipt']['sha256'])):
        record = file_record(safe_path(Path(path).absolute()))
        require(record['sha256'] == expected_hash, 'Encoder prerequisite input changed during admission')
        inputs.append(record)
    phases = [source['nativeBefore'], source['edit']['nativeAfter'], source['recovery']['nativeRecovered'],
              source['recovery']['reopen']['nativeReopened'], source['saveReopen']['nativeReopened']]
    exports = []
    for value in phases:
        exports.append(value)
        exports.extend({**row['read'], 'xml': row['xml']} for row in value['softwarePeers']['exports'])
    for value in exports:
        path = safe_path(Path(value['artifact']['path']).absolute())
        require(path.is_relative_to(Path(source['runDirectory'])) and all(not path.is_relative_to(Path(row['projectPath']).parent)
                for row in expected['copies'].values()), 'Native export escaped retained run evidence')
        data = read_bytes(path, 32 * 1024 * 1024)
        require(hashlib.sha256(data).hexdigest() == value['artifact']['sha256'] and data.decode('utf-8-sig') == value['xml'],
                'Encoder native export differs from its original captured bytes')
        inputs.append({'path': str(path), 'bytes': len(data), 'sha256': value['artifact']['sha256']})
    packet = {'policy': POLICY, 'sourceReport': inputs[0], 'inputs': inputs, 'closedCopies': expected,
              'sourceAuthenticityVerified': False, 'atomicSnapshot': False}
    recheck(packet)
    return source, packet


def recheck(packet):
    require(packet.get('policy') == POLICY, 'Encoder copy admission policy is missing')
    for record in packet['inputs']:
        require(file_record(safe_path(Path(record['path']).absolute())) == record, 'Encoder admission input changed')
    for expected in packet['closedCopies']['copies'].values():
        path = safe_path(Path(expected['projectPath']).absolute())
        require(same(snapshot(path), expected), 'Closed encoder source changed after admission')


def prepare_copy(packet, label, run_directory, receipt):
    recheck(packet)
    expected = packet['closedCopies']['copies'][label]
    source = safe_path(Path(expected['projectPath']))
    run = safe_path(Path(run_directory).absolute())
    require(run.is_relative_to(ROOT / 'artifacts') and all(not run.is_relative_to(Path(row['projectPath']).parent) and
            not Path(row['projectPath']).parent.is_relative_to(run)
            for row in packet['closedCopies']['copies'].values()), 'Inspection output overlaps an original project')
    path = copy_closed_project(source, run / label / source.parent.name, receipt)
    require(same(receipt['sourceBefore'], expected['tree']) and same(receipt['copyBeforeOpen'], expected['tree']),
            'Inspection copy does not contain the admitted original bytes')
    recheck(packet)
    return path


def before_open(packet, label, receipt, session):
    recheck(packet)
    path = safe_path(Path(receipt['copyProject']))
    expected = packet['closedCopies']['copies'][label]['tree']
    require(same(snapshot(path)['tree'], expected), 'Inspection copy changed before opening')
    return {'label': label, 'projectPath': str(path), 'processId': session['processId'], 'callIndex': len(session['calls']),
            'sourceReportSha256': packet['sourceReport']['sha256'], 'copyTreeSha256': expected['sha256']}


def finish_copies(packet, copies):
    recheck(packet)
    for receipt in copies.values():
        if 'sourceBefore' in receipt:
            verify_source_preserved(receipt)


def audit_copies(source, companion, source_digest):
    packet = companion.get('copyAdmission', {})
    require(packet.get('sourceReport', {}).get('sha256') == source_digest, 'Copy admission identifies another source report')
    original, expected_packet = load_source(packet['sourceReport']['path'], source_digest)
    require(same(source, original) and same(packet, expected_packet), 'Copy admission differs from its original source evidence')
    run = safe_path(Path(companion['runDirectory']).absolute())
    require(run.is_relative_to(ROOT / 'artifacts') and all(not run.is_relative_to(Path(row['projectPath']).parent) and
            not Path(row['projectPath']).parent.is_relative_to(run) for row in packet['closedCopies']['copies'].values()),
            'Inspection run overlaps original source trees')
    receipts = companion.get('inspectionCopies', {})
    require(set(receipts) == {'changed', 'recovered'}, 'Both complete inspection copies are required')
    opens = companion.get('openAdmissions')
    require(isinstance(opens, list), 'Original pre-open file admissions are missing')
    expected_opens = []
    for label in ('recovered', 'changed'):
        copy = companion['copies'][label]
        receipt = receipts[label]
        original_path = Path(packet['closedCopies']['copies'][label]['projectPath'])
        copied_path = safe_path(Path(companion['runDirectory']) / label / original_path.parent.name / original_path.name)
        require(copy.get('sourceProjectPath') == str(original_path) and copy['projectPath'] == str(copied_path) and
                receipt.get('sourceProject') == str(original_path) and receipt.get('sourceRoot') == str(original_path.parent) and
                receipt.get('copyProject') == str(copied_path) and receipt.get('copyRoot') == str(copied_path.parent) and
                receipt.get('copyVerified') is True and receipt.get('sourcePreserved') is True,
                'Inspection copy path or preservation receipt differs')
        tree = packet['closedCopies']['copies'][label]['tree']
        require(all(same(receipt.get(key), tree) for key in ('sourceBefore', 'sourceAfterCopy', 'sourceAfterCleanup', 'copyBeforeOpen')),
                'Inspection copy bytes differ from the original closed source')
        sessions = [session for session in companion['sessions'] if session['processId'] == copy['processId']]
        require(len(sessions) == 1, 'Inspection copy owner is ambiguous')
        for index, call in enumerate(sessions[0]['calls']):
            if call['tool'] == 'project_open':
                require(call['arguments'] == {'path': str(copied_path)}, 'Open targeted an unadmitted project')
                expected_opens.append({'label': label, 'projectPath': str(copied_path), 'processId': copy['processId'], 'callIndex': index,
                    'sourceReportSha256': source_digest, 'copyTreeSha256': tree['sha256']})
    require(same(opens, expected_opens), 'Pre-open admissions differ from the original open attempts')
    recheck(packet)
