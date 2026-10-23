"""Verify the seeder's pre-mutation archive receipt; never derive a new expected digest."""
import json
import hashlib
from pathlib import Path
import re

from audit_retained_evidence import evidence_path
from engineering_preservation import require
from run_block_tests import file_record
from smoke_mcp import ROOT


def verified_file(root, expected, limit=None):
    require(isinstance(expected, dict) and isinstance(expected.get('path'), str) and
            type(expected.get('bytes')) is int and expected['bytes'] > 0 and
            isinstance(expected.get('sha256'), str) and re.fullmatch('[0-9a-f]{64}', expected['sha256']),
            'Checkpoint artifact requires an exact path, byte count and SHA-256')
    supplied = Path(expected['path'])
    require(supplied.is_absolute() and supplied.is_relative_to(root), 'Checkpoint artifact escaped its run directory')
    path = evidence_path(root, supplied.relative_to(root).as_posix())
    require(path.is_file(), 'Checkpoint artifact must be a regular file')
    require(limit is None or path.stat().st_size <= limit, 'Checkpoint metadata exceeds its read bound')
    actual = file_record(path)
    require(actual['bytes'] == expected['bytes'] and actual['sha256'] == expected['sha256'], 'Pre-mutation checkpoint artifact bytes changed')
    return path


def verify_seeder_checkpoint(report):
    root = Path(report['runDirectory'])
    require(root.is_absolute() and root.is_relative_to(ROOT / 'artifacts'), 'Checkpoint run escaped workspace artifacts')
    packets = [packet for packet in report.get('seederLifetimes', []) if packet.get('mode') == 'connect']
    require(len(packets) == 1, 'One exact connect invocation must own the checkpoint')
    packet = packets[0]
    require(packet.get('terminal', {}).get('completed') is True and type(packet.get('terminal', {}).get('exitCode')) is int and packet['terminal']['exitCode'] == 0
            and packet.get('unknown') == [] and packet.get('outcomeCaptured') is True
            and 'captureFailure' not in packet and 'launchFailure' not in packet, 'Seeder outcome is not known and terminal')
    value = report['seederConnect']
    require(type(packet.get('authorizationProcessId')) is int and packet['authorizationProcessId'] > 0 and
            packet['authorizationProcessId'] == report['sessions'][0]['processId'] and
            packet.get('authorizationProjectPath') == report['projectPath'], 'Checkpoint dispatch differs from the original owned scratch')
    reference = value.get('checkpointReceipt')
    receipt_path = verified_file(root, reference, 1024 * 1024)
    require(receipt_path == Path(packet['authorization'] + '.checkpoint.json'), 'Checkpoint belongs to another authorization')
    # Parse the same bounded bytes whose digest is checked, even if the file changes after the first read.
    with receipt_path.open('rb') as stream:
        data = stream.read(1024 * 1024 + 1)
    require(len(data) == reference['bytes'] and len(data) <= 1024 * 1024 and
            hashlib.sha256(data).hexdigest() == reference['sha256'], 'Checkpoint receipt changed before parsing')
    receipt = json.loads(data)
    require(type(receipt.get('schemaVersion')) is int and receipt['schemaVersion'] == 1 and
            receipt.get('phase') == 'beforeEncoderMutation' and type(receipt.get('processId')) is int and
            receipt['processId'] == packet.get('authorizationProcessId') and
            receipt.get('projectPath') == packet.get('authorizationProjectPath'), 'Checkpoint source context differs from dispatch')
    auth = receipt.get('authorization', {})
    require(auth.get('path') == packet['authorization'] and auth.get('sha256') == packet.get('authorizationSha256'),
            'Checkpoint authorization differs from the original dispatched bytes')
    verified_file(root, auth, 1024 * 1024)
    archive = receipt.get('archive')
    path = verified_file(root, archive)
    require(path.suffix == '.zap21' and path == Path(value.get('checkpointArchive', '')),
            'Checkpoint archive differs from the returned seeder result')
    return archive
