"""Audit retained safety XML, compiler diagnostics and lifecycle signature observations."""
import argparse
import hashlib
import json
import pathlib

import engineering_preservation
import compilation_evidence
import smoke_safety_direct
import safety_lifecycle
from engineering_preservation import require, native_payload_hash
from compilation_evidence import require_clean_compile, require_compile_only, require_baseline_compile
from smoke_safety_direct import require_signature_evidence, require_safety_equal, require_applied_evidence


def audit(source):
    require(source.get('completed') is True, 'A completed native safety qualification report is required')
    safety_lifecycle.verify(source)
    snapshots = source['nativePreservation']['before']
    baseline = {}
    for path, row in snapshots.items():
        artifact = row['artifact']
        data = pathlib.Path(artifact['path']).read_bytes()
        require(hashlib.sha256(data).hexdigest() == artifact['sha256'], 'Original artifact bytes changed')
        baseline[path] = data.decode('utf-8-sig')
    prepared = next(row for row in source['steps'] if row['name'] == 'change_prepare' and not row.get('isError')
                    and row['arguments'].get('kind') == 'safety_import_xml')
    selected_path = prepared['arguments']['payload']['blockPath']
    plc_path = selected_path.rsplit('/blocks/', 1)[0]
    fresh_baseline = 'baselineCompilePolicy' in source or 'baselineCompile' in source
    baseline_id = None
    preparations = {}
    if fresh_baseline:
        projected = {'sessions': source['sessions']}; safety_lifecycle.sync_steps(projected)
        require(projected['steps'] == source['steps'], 'Safety steps differ from original baseline sessions')
        baseline_id = require_baseline_compile(source, plc_path)['changeId']

    require(selected_path in baseline, 'Selected safety block is absent from the original exports')
    changed = {**baseline, selected_path: prepared['arguments']['payload']['xml']}
    expected = baseline
    stage = 'before'
    checks = []
    compilations = []
    signature_checks = []
    signature_snapshots = {}
    applied_evidence = None
    project_path = pathlib.Path(source['projectPath']).resolve()
    recovered_path = None
    unchanged = native_payload_hash(changed[selected_path]) == native_payload_hash(baseline[selected_path])
    sessions = []
    current_process_id = None
    first_change_id = prepared['result']['changeId']
    mutation_observed = False
    for index, step in enumerate(source['steps']):
        if step['name'] == 'safety_inspect' and (step.get('arguments', {}).get('blockPath') or step.get('arguments', {}).get('blockName')):
            require(not step.get('isError'), 'Failed selected-block signature observation cannot qualify safety evidence')
        if step['name'] in ('tia_create_scratch', 'change_apply'):
            require(not step.get('isError'), 'Failed compiler-bearing operation cannot qualify safety evidence')
            compiled = step['result']
            require(compiled.get('completed') is True if step['name'] == 'tia_create_scratch'
                    else compiled.get('state') == 'applied', 'Compiler-bearing safety operation did not complete')
            require_clean_compile(compiled.get('compile'), expected_targets=[plc_path])
            if step['name'] == 'change_apply':
                require({'compile_finished', 'changed_project_saved'}.issubset(compiled.get('stages', [])), 'Safety change was not compiled and saved')
            compilations.append({'step': index, 'tool': step['name'], 'beforeMutation': (step['name'] == 'tia_create_scratch' or compiled.get('changeId') == baseline_id and baseline_id is not None) and not mutation_observed, 'compile': compiled['compile']})
        if step.get('isError'):
            continue
        tool, result = step['name'], step['result']
        if tool == 'tia_start':
            require(result['ownsPortalProcess'] is True, 'Native qualification must use owned TIA processes')
            current_process_id = result['processId']
            require(type(current_process_id) is int and current_process_id > 0, 'Owned process identity must be a positive integer')
            sessions.append({'processId': current_process_id, 'elapsedSeconds': step.get('elapsedSeconds')})
        elif tool == 'change_prepare' and fresh_baseline:
            identity = result.get('changeId')
            require(identity not in preparations, 'Safety preparation identity was reused')
            preparations[identity] = step
        elif tool == 'change_apply':
            if fresh_baseline and result.get('changeId') != first_change_id:
                preparation = preparations.get(step['arguments'].get('changeId'))
                require(preparation is not None and preparation['arguments'].get('kind') == 'plc_compile'
                        and preparation['arguments'].get('payload') == {} and preparation['arguments'].get('plcPath') == plc_path
                        and step['arguments'].get('previewHash') == preparation['result'].get('previewHash')
                        and all(step['arguments'].get(flag) is True for flag in ('checkpointSave', 'compile', 'save')),
                        'Safety compilation must use its original compile-only request')
                require_compile_only(preparation['result'], result, plc_path)
                require(result['processId'] == current_process_id
                        and pathlib.Path(preparation['result']['preview']['project']['path']).resolve() == project_path,
                        'Safety compile-only owner differs')
                if result['changeId'] != baseline_id:
                    require(source['recovery'].get('compileOnly') == {'prepared': preparation['result'], 'applied': result},
                            'Safety recovery compiler summary differs from original calls')
            if result.get('changeId') == baseline_id and baseline_id is not None:
                applied_evidence = result
                continue
            require(stage in signature_snapshots, 'Missing safety signature observation before application: ' + stage)
            applied_evidence = result
            if result['changeId'] == first_change_id:
                mutation_observed = True
                expected, stage = changed, 'after'
            else:
                expected, stage = baseline, 'recovered-compiled'
        elif tool == 'change_restore':
            require(result.get('state') == 'restored', 'Safety recovery did not complete')
            require(isinstance(result.get('recoveredProject', {}).get('path'), str)
                    and result['recoveredProject']['path'], 'Missing exact recovered project path')
            recovered_path = pathlib.Path(result['recoveredProject']['path']).resolve()
            require(recovered_path != pathlib.Path(source['projectPath']).resolve(), 'Safety recovery must use a separate project copy')
            project_path = recovered_path
            expected, stage = baseline, 'recovered'
        elif tool == 'project_open' and mutation_observed:
            project_path = pathlib.Path(step['arguments']['path']).resolve()
            if project_path == pathlib.Path(source['projectPath']).resolve():
                expected, stage = changed, 'changed-reopened'
            else:
                require(recovered_path is not None and project_path == recovered_path, 'Reopened project is not the recorded recovery copy')
                expected, stage = baseline, 'recovered-reopened'
        elif tool == 'safety_inspect' and (step.get('arguments', {}).get('blockPath') or step.get('arguments', {}).get('blockName')):
            require(step['arguments'].get('blockPath') == selected_path, 'Safety signature selection differs from the selected block')
            require_signature_evidence(result)
            require(type(result.get('processId')) is int and result['processId'] == current_process_id,
                    'Safety signature process identity differs from owned session')
            block = result.get('block')
            require(result.get('plcPath') == plc_path and isinstance(block, dict) and block.get('path') == selected_path
                    and block.get('plcPath') == plc_path, 'Safety signatures belong to another PLC or block')
            project = result.get('project')
            require(isinstance(project, dict), 'Missing safety signature project identity')
            observed_project = project.get('path')
            require(isinstance(observed_project, str) and observed_project and pathlib.Path(observed_project).resolve() == project_path,
                    'Safety signature project differs from the current project copy')
            if stage in ('after', 'recovered-compiled') or stage == 'before' and fresh_baseline:
                require_applied_evidence(applied_evidence, result, selected_path)
            comparison_stage = ('after' if stage == 'changed-reopened' else
                                'before' if stage.startswith('recovered') or stage == 'after' and unchanged else stage)
            if comparison_stage != stage:
                require(comparison_stage in signature_snapshots, 'Missing safety signature baseline: ' + comparison_stage)
            if comparison_stage in signature_snapshots:
                require_safety_equal(signature_snapshots[comparison_stage], result)
            signature_snapshots.setdefault(stage, result)
            signature_checks.append({'step': index, 'stage': stage, 'processId': current_process_id,
                                     'projectPath': str(project_path), 'plcPath': plc_path, 'blockPath': selected_path,
                                     'observation': result, 'passed': True})
        elif tool == 'block_read':
            require(current_process_id is not None and type(result.get('processId')) is int and result['processId'] == current_process_id,
                    'Readback process identity differs from owned session')
            path = step['arguments']['blockPath']
            require(path in expected, 'Readback is outside the recorded fixture baseline: ' + path)
            artifact = result['artifact']
            raw = pathlib.Path(artifact['path']).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == artifact['sha256'], 'Export artifact bytes changed')
            # Compare inline native result and its byte-verified artifact as well.
            artifact_hash = native_payload_hash(raw.decode('utf-8-sig'))
            actual_hash = native_payload_hash(result['xml'])
            expected_hash = native_payload_hash(expected[path])
            require(artifact_hash == actual_hash == expected_hash, 'Full native payload differs: ' + stage + ': ' + path)
            checks.append({'step': index, 'stage': stage, 'processId': current_process_id, 'blockPath': path, 'artifact': artifact,
                           'expectedPayloadSha256': expected_hash, 'actualPayloadSha256': actual_hash, 'passed': True})
    stages = {row['stage'] for row in checks}
    required_stages = {'before', 'after', 'changed-reopened'}
    if source.get('recovery'):
        required_stages |= {'recovered', 'recovered-compiled', 'recovered-reopened'}
    require(stages >= required_stages, 'Missing required native audit stages')
    for stage in required_stages:
        require({row['blockPath'] for row in checks if row['stage'] == stage} == set(baseline), 'Incomplete block coverage in stage ' + stage)
    require(set(signature_snapshots) >= required_stages, 'Missing required safety signature audit stages')
    require(len(sessions) == (3 if source.get('recovery') else 2), 'Expected fresh owned sessions for each saved-copy reopen')
    require(len({session['processId'] for session in sessions}) == len(sessions), 'Saved-copy checks reused a prior TIA process')
    require([row['processId'] for row in sessions] == [row['processId'] for row in source['sessions']],
            'Audited native sessions differ from exact terminal evidence')
    return {'completed': True, 'comparison': 'Full native object XML including comments, processing instructions, IDs, namespace-bound names and xml:space; only top-level DocumentInfo and element-only formatting excluded',
            'stages': sorted(stages), 'blockCount': len(baseline), 'sessions': sessions, 'checks': checks,
            'compileDiagnosticsVerified': True, 'compilations': compilations,
            'observedCompileTargetCoverageVerified': True,
            'signaturePhaseCoverageVerified': True, 'signatureChecks': signature_checks,
            'signatureNativeAuthenticityVerified': False,
            'processLifetimeVerified': True, 'lifetimePolicy': source['lifetimePolicy'],
            'baselineCompilationObserved': fresh_baseline or any(row['tool'] == 'tia_create_scratch' and row['beforeMutation'] for row in compilations),
            'freshBaselineCompilationVerified': fresh_baseline,
            'compileTargetCoverageVerified': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-report', type=pathlib.Path, required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    args = parser.parse_args()
    raw = args.source_report.read_bytes()
    result = audit(json.loads(raw.decode('utf-8-sig')))
    result['sourceReport'] = {'path': str(args.source_report.resolve()), 'sha256': hashlib.sha256(raw).hexdigest()}
    result['implementation'] = {path.name: {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
                                for path in (pathlib.Path(__file__), pathlib.Path(engineering_preservation.__file__), pathlib.Path(compilation_evidence.__file__), pathlib.Path(smoke_safety_direct.__file__),
                                             pathlib.Path(safety_lifecycle.__file__), pathlib.Path(safety_lifecycle.verify_lifecycle.__code__.co_filename))}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print('PASS retained safety XML/signature audit: ' + str(len(result['checks'])) + ' readbacks, '
          + str(len(result['signatureChecks'])) + ' signature observations')


if __name__ == '__main__':
    main()
