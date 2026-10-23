"""Recheck retained fixture exports with the current comparator; never starts TIA."""
import argparse
import hashlib
import json
import pathlib

import engineering_preservation
from engineering_preservation import require, native_payload_hash
from smoke_mcp import ROOT


def verified_artifact(artifact):
    path = pathlib.Path(artifact['path']).resolve()
    require(path.is_relative_to((ROOT / 'artifacts').resolve()), 'Export/checkpoint escaped workspace artifacts')
    data = path.read_bytes()
    require(hashlib.sha256(data).hexdigest() == artifact['sha256'], 'Retained artifact hash mismatch: ' + str(path))
    return data


def audit_report(path, result=None):
    source_bytes = path.read_bytes()
    source = json.loads(source_bytes.decode('utf-8-sig'))
    if result is None:
        result = {}
    result.update({'sourceReport': str(path.resolve()), 'sourceReportSha256': hashlib.sha256(source_bytes).hexdigest(),
              'executedHarness': source.get('harness', 'Initial harness revision, before comparator audit; no executing script digest recorded'),
              'sourceCompleted': source['completed'], 'fixtures': [], 'passed': False})
    require(source['completed'] is True and not source.get('failures'), 'Native qualification did not complete')
    steps = source['steps']
    current_pid = None
    processes = []
    for step in steps:
        if step['tool'] == 'tia_start' and not step.get('isError'):
            require(step['result']['ownsPortalProcess'] is True and step['result']['projects'] == [], "Native evidence requirement failed: step['result']['ownsPortalProcess'] is True and step['result']['projects'] == []")
            current_pid = step['result']['processId']
        processes.append(current_pid)
    for fixture in source.get('graphicalFixtures', []) + source.get('callFixtures', []):
        name = fixture.get('blockName') or fixture['caller']
        audited = {'fixture': name, 'status': 'running', 'phases': {}}
        result['fixtures'].append(audited)
        require(fixture['status'] == 'passed', fixture)
        prepare_index = next(index for index, step in enumerate(steps) if step['tool'] == 'change_prepare'
                             and step['result'].get('changeId') == fixture['changeId'])
        primary_pid = processes[prepare_index]
        applied = next(step['result'] for step in steps[prepare_index:] if step['tool'] == 'change_apply'
                       and step['arguments']['changeId'] == fixture['changeId'])
        require(applied['state'] == 'applied' and applied['compile']['errorCount'] == 0, "Native evidence requirement failed: applied['state'] == 'applied' and applied['compile']['errorCount'] == 0")
        require('changed_project_saved' in applied['stages'], "Native evidence requirement failed: 'changed_project_saved' in applied['stages']")
        require(fixture['compile'] == applied['compile'], "Native evidence requirement failed: fixture['compile'] == applied['compile']")
        audited['compile'] = applied['compile']
        audited['compilePhase'] = 'Native apply before save; this offline audit does not compile'
        baseline = {}
        for step in steps[:prepare_index]:
            if step['tool'] == 'block_read' and not step.get('isError'):
                block_name = step['arguments'].get('blockName')
                if block_name:
                    baseline[block_name] = step['result']['xml']
        require(baseline[name] == fixture['beforeNativeXml'], 'Recorded baseline does not match native read')
        changed = {**baseline, name: fixture['expectedNativeXml']}
        for phase, expected in [('appliedReadback', changed), ('recoveredReadback', baseline),
                                ('appliedReopen', changed), ('recoveredReopen', baseline)]:
            rows = fixture[phase]['blocks']
            expected_names = {name, *fixture.get('supportingBlocksPreserved', [])}
            require(set(rows) == expected_names, 'Missing selected/dependency export in ' + phase)
            phase_evidence = {}
            audited['phases'][phase] = phase_evidence
            for block_name, row in rows.items():
                data = verified_artifact(row['nativeExport'])
                actual_xml = data.decode('utf-8-sig')
                raw = next(step['result'] for step in steps if step['tool'] == 'block_read'
                           and step['result'].get('artifact', {}).get('path') == row['nativeExport']['path'])
                require(actual_xml == raw['xml'], 'Report XML differs from retained export')
                expected_hash, actual_hash = native_payload_hash(expected[block_name]), native_payload_hash(actual_xml)
                phase_evidence[block_name] = {'expectedPayloadSha256': expected_hash, 'actualPayloadSha256': actual_hash,
                                              'nativeExport': row['nativeExport'], 'passed': expected_hash == actual_hash}
                require(expected_hash == actual_hash, phase + ': native XML changed for ' + block_name)
        restored = fixture['recovery']
        require(restored['state'] == 'restored', "Native evidence requirement failed: restored['state'] == 'restored'")
        verified_artifact({'path': restored['recoveryArchive'], 'sha256': restored['recoveryArchiveSha256']})
        audited['recovery'] = {'state': restored['state'], 'archivePath': restored['recoveryArchive'],
                               'archiveSha256': restored['recoveryArchiveSha256'], 'recoveredProject': restored['recoveredProject']}
        pids = [primary_pid]
        for phase in ('appliedReopen', 'recoveredReopen'):
            reopen = fixture[phase]
            matches = [index for index, step in enumerate(steps) if step['tool'] == 'project_open' and not step.get('isError')
                       and step['result']['project']['path'] == reopen['project']['path'] and processes[index] == reopen['processId']]
            require(len(matches) == 1 and matches[0] > prepare_index, 'Fresh reopen evidence missing/ambiguous')
            pids.append(reopen['processId'])
        require(len(set(pids)) == 3, 'Saved copy checks reused the original TIA process')
        audited['processIds'] = dict(zip(('apply', 'appliedReopen', 'recoveredReopen'), pids))
        if 'caller' in fixture:
            require(fixture['appliedReopen']['call'] == fixture['afterCall'], "Native evidence requirement failed: fixture['appliedReopen']['call'] == fixture['afterCall']")
            require(fixture['recoveredReopen']['call'] == fixture['beforeCall'] == fixture['recoveredCall'], "Native evidence requirement failed: fixture['recoveredReopen']['call'] == fixture['beforeCall'] == fixture['recoveredCall']")
            audited['callAndInstanceMetadataPreserved'] = True
        audited['status'] = 'passed'
    require(result['fixtures'], 'No selected native preservation fixtures')
    result['passed'] = True
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=pathlib.Path, action='append', required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    args = parser.parse_args()
    result = {'completed': False, 'scope': 'Offline audit of exact retained native exports, archives, compile responses and fresh process IDs',
              'auditSourceSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              'comparatorSha256': hashlib.sha256(pathlib.Path(engineering_preservation.__file__).read_bytes()).hexdigest(), 'reports': []}
    try:
        for path in args.input:
            entry = {'sourceReport': str(path.resolve()), 'passed': False}
            result['reports'].append(entry)
            audit_report(path, entry)
        result['completed'] = True
    except Exception as error:
        result['failure'] = type(error).__name__ + ': ' + str(error)
        raise
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print('PASS retained native preservation audit', flush=True)


if __name__ == '__main__':
    main()
