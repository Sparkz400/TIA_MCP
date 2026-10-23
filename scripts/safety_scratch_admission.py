"""Admit only byte-bound, closed scratch projects from completed safety runs."""
import hashlib
import json
from pathlib import Path
import re

from api_coverage import read_bytes
from compilation_evidence import require_clean_compile
from engineering_preservation import require
from hmi_fixture_copy import project_tree, safe_path
from run_block_tests import file_record
import safety_lifecycle


CPU = 'OrderNumber:6ES7 511-1FL03-0AB0/V4.1'


def scratch_path(project, artifacts, workspace):
    artifacts, workspace = safe_path(Path(artifacts).absolute()), safe_path(Path(workspace).absolute())
    path = safe_path(Path(project).absolute())
    require(artifacts.is_relative_to(workspace) and path.is_relative_to(artifacts/'scratch')
            and path.suffix == '.ap21' and path.stem.startswith('TiaMcpScratch_')
            and path.parent.name == path.stem and path.is_file(), 'An exact generated scratch project is required')
    return path


def closed_snapshot(project, artifacts, workspace):
    path = scratch_path(project, artifacts, workspace)
    first = project_tree(path.parent)
    require(first == project_tree(path.parent), 'Closed scratch changed during capture')
    return {'projectPath': str(path), 'tree': first}


def load_source(project, source_path, expected_hash, artifacts, workspace):
    from safety_recovery_evidence import unique_object
    path = scratch_path(project, artifacts, workspace)
    root = safe_path(Path(artifacts).absolute())
    records, visited = [], set()

    def read_report(location, digest):
        location = safe_path(Path(location).absolute())
        require(location.is_relative_to(root) and not location.is_relative_to(path.parent),
                'Scratch source reports must be retained outside the mutable project tree')
        require(str(location) not in visited and len(visited) < 16, 'Cyclic or oversized scratch source lineage')
        visited.add(str(location))
        require(isinstance(digest, str) and re.fullmatch('[0-9a-f]{64}', digest), 'An exact source report SHA-256 is required')
        raw = read_bytes(location, 64 * 1024 * 1024)
        require(hashlib.sha256(raw).hexdigest() == digest, 'Scratch source report hash differs')
        record = {'path': str(location), 'bytes': len(raw), 'sha256': digest}; records.append(record)
        source = json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique_object)
        safety_lifecycle.verify(source)
        require(source.get('completed') is True and 'failure' not in source, 'Only completed safety runs authorize scratch reuse')
        projected = {'sessions': source['sessions']}; safety_lifecycle.sync_steps(projected)
        require(source.get('steps') == projected['steps'], 'Scratch source steps differ from retained calls')
        require(source.get('projectPath') == str(path) and source.get('closedScratch', {}).get('projectPath') == str(path),
                'Scratch source identifies another project')
        creates = [step for step in source['steps'] if step['name'] == 'tia_create_scratch']
        if creates:
            require(len(creates) == 1, 'Scratch creation is ambiguous')
            create = creates[0]; result = create['result']
            require(create.get('isError') is False and create['arguments'].get('cpuTypeIdentifier') == CPU
                    and result.get('completed') is True and result.get('project', {}).get('path') == str(path)
                    and type(result.get('processId')) is int and result['processId'] == source['sessions'][0]['processId'],
                    'Source does not prove the exact recorded scratch creation')
            inventories = [step['result'] for step in source['steps'] if step['name'] == 'project_inventory' and step.get('isError') is False]
            require(inventories and inventories[0].get('truncated') is not True
                    and inventories[0].get('project', {}).get('path') == str(path)
                    and type(inventories[0].get('processId')) is int
                    and inventories[0]['processId'] == source['sessions'][0]['processId'], 'Original scratch inventory owner is missing')
            plcs = inventories[0].get('plcs')
            require(isinstance(plcs, list) and len(plcs) == 1, 'Original scratch requires one inventoried PLC')
            require_clean_compile(result.get('compile'), expected_targets=[plcs[0]['path']])
        else:
            parent = source.get('scratchAdmission', {}).get('sourceReport', {})
            opens = [step for step in source['steps'] if step['name'] == 'project_open']
            require(opens and opens[0].get('isError') is False and opens[0]['arguments'].get('path') == str(path),
                    'Reused source has no successful exact project open')
            opened = opens[0]['result']
            require(opened.get('project', {}).get('path') == str(path) and type(opened.get('processId')) is int
                    and opened['processId'] == source['sessions'][0]['processId'], 'Reused source open owner differs')
            _, retained = read_report(parent['path'], parent['sha256'])
            require(parent == retained, 'Parent scratch source record differs')
        return source, record

    source, record = read_report(source_path, expected_hash)
    snapshot = closed_snapshot(path, artifacts, workspace)
    require(source['closedScratch'] == snapshot, 'Closed scratch files differ from the original terminal snapshot')
    return {'sourceReport': record, 'inputs': records, 'closedScratch': snapshot,
            'recordedSourceBindingVerified': True, 'sourceAuthenticityVerified': False}


def recheck(packet, artifacts, workspace, before_open=False):
    for record in packet['inputs']:
        require(file_record(safe_path(record['path'])) == record, 'Scratch source report changed after admission')
    if before_open:
        expected = packet['closedScratch']
        require(closed_snapshot(expected['projectPath'], artifacts, workspace) == expected,
                'Closed scratch changed before opening')
