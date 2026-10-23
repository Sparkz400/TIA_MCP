"""Retrieve a retained safety-fixture checkpoint into a new owned project and compare evidence."""
import argparse
import hashlib
import json
import pathlib
import uuid

from smoke_mcp import ROOT
import safety_lifecycle
from smoke_safety_direct import native_payload_hash, require_safety_equal, require_applied_evidence
from engineering_preservation import require, native_payload_hash as complete_native_payload_hash
from compilation_evidence import require_compile_only
from safety_recovery_evidence import load_source, recheck_inputs, require_target


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-report', type=pathlib.Path, required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    parser.add_argument('--host', type=pathlib.Path, default=ROOT / 'src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=pathlib.Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--artifacts', type=pathlib.Path, default=ROOT / 'artifacts')
    parser.add_argument('--timeout', type=int, default=300)
    args = parser.parse_args()
    require(not args.report.exists(), "Choose a new report path; prior safety evidence is immutable")
    packet = load_source(args.source_report, args.artifacts, ROOT)
    source, prepared, before, exported = (packet[key] for key in ('source', 'prepared', 'before', 'exported'))
    selected_path, plc_path = packet['selectedPath'], packet['plcPath']
    archive = pathlib.Path(packet['archive']['path'])
    report = {'completed': False, 'sourceReport': str(args.source_report.resolve()), 'changeId': prepared['changeId'], 'steps': [],
              'scope': 'Explicit archive retrieval into a new empty owned instance; original failed project and journal retained'}
    report['checkpoint'] = packet['archive']
    report['sourceEvidence'] = {key: packet[key] for key in ('sourceRecord', 'journalRecord', 'inputs', 'sourceAuthenticityVerified')}
    report['sourceEvidence']['recordedSourceBindingVerified'] = True
    def persist():
        safety_lifecycle.sync_steps(report)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')

    session = safety_lifecycle.new_session(args.host.resolve(), args.worker.resolve(), args.artifacts.resolve(),
        report, persist, args.artifacts / ('safety-lifetimes-' + uuid.uuid4().hex), args.timeout)

    def call(name, arguments=None):
        return safety_lifecycle.call(session, name, arguments)

    def verify_native():
        actual = call('safety_inspect', {'plcPath': plc_path, 'blockPath': selected_path, 'maxItems': 1000})
        native = call('block_read', {'plcPath': plc_path, 'blockPath': selected_path})
        for value in (actual, native):
            require_target(value, selected_path, plc_path, session.current_project, session.session['processId'])
        require_safety_equal(before, actual)
        require(native_payload_hash(native['xml']) == native_payload_hash(exported['xml']), 'Recovered native F-block XML differs')
        require(complete_native_payload_hash(native['xml']) == complete_native_payload_hash(exported['xml']), 'Recovered full native F-block XML differs')
        require(complete_native_payload_hash(pathlib.Path(native['artifact']['path']).read_text(encoding='utf-8-sig')) == complete_native_payload_hash(native['xml']), 'Recovered inline XML differs from its artifact')
        require(hashlib.sha256(pathlib.Path(native['artifact']['path']).read_bytes()).hexdigest() == native['artifact']['sha256'], "Safety evidence requirement failed: hashlib.sha256(pathlib.Path(native['artifact']['path']).read_bytes()).hexdigest() == native['artifact']['sha256']")
        blocks = {}
        for path, expected in source.get('nativePreservation', {}).get('before', {}).items():
            value = call('block_read', {'blockPath': path})
            require_target(value, path, plc_path, session.current_project, session.session['processId'])
            raw = pathlib.Path(value['artifact']['path']).read_bytes()
            require(hashlib.sha256(raw).hexdigest() == value['artifact']['sha256'] and complete_native_payload_hash(raw.decode('utf-8-sig')) == complete_native_payload_hash(value['xml']), 'Recovered supporting artifact differs')
            digest = native_payload_hash(value['xml'])
            require(digest == expected['nativePayloadHash'], 'Recovered supporting block differs: ' + path)
            baseline_artifact = expected['artifact']
            baseline_bytes = pathlib.Path(baseline_artifact['path']).read_bytes()
            require(hashlib.sha256(baseline_bytes).hexdigest() == baseline_artifact['sha256'], 'Original artifact bytes changed')
            full_digest = complete_native_payload_hash(value['xml'])
            require(full_digest == complete_native_payload_hash(baseline_bytes.decode('utf-8-sig')), 'Recovered full native supporting block differs: ' + path)
            blocks[path] = {'nativePayloadHash': digest, 'completeNativePayloadHash': full_digest, 'artifact': value['artifact']}
        evidence = {'nativePayloadHash': native_payload_hash(native['xml']), 'completeNativePayloadHash': complete_native_payload_hash(native['xml']), 'artifact': native['artifact'],
                    'programSignatures': actual['programSignatures'], 'blockSignatures': actual['blockSignatures'], 'blocks': blocks}
        return actual, native, evidence

    try:
        started = session.start()
        recheck_inputs(packet)
        recovered = call('project_retrieve', {'archivePath': str(archive), 'expectedSha256': packet['archive']['sha256']})
        require(recovered.get('retrieved') is True and type(recovered.get('processId')) is int and recovered['processId'] == session.session['processId'], 'Retrieval belongs to another owned session')
        receipt = recovered.get('archive', {})
        require(all(receipt.get(key) == packet['archive'][key] for key in ('path', 'bytes', 'sha256')) and type(receipt.get('bytes')) is int, 'Retrieval archive receipt differs from original checkpoint')
        require(pathlib.Path(recovered['project']['path']).resolve() != pathlib.Path(packet['projectPath']).resolve(), 'Recovery must use a new project copy')
        session.remember(recovered['project']['path'])
        session.guard()
        actual, native, report['readback'] = verify_native()
        recompile = call('change_prepare', {'kind': 'plc_compile', 'plcPath': plc_path, 'payload': {}})
        compiled = call('change_apply', {'changeId': recompile['changeId'], 'previewHash': recompile['previewHash'],
            'idempotencyKey': 'f-retrieval-compile-' + uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
        require_compile_only(recompile, compiled, plc_path)
        actual, native, report['compiledReadback'] = verify_native()
        require_applied_evidence(compiled, actual, selected_path)
        report['compile'] = compiled['compile']
        report['compileOnly'] = {'prepared': recompile, 'applied': compiled}
        report['recoveredProject'] = recovered['project']
        report['nativePayloadHash'] = native_payload_hash(native['xml'])
        session.reopen(recovered['project']['path'])
        reopened = session.session
        require(reopened['processId'] != started['processId'], 'Reopen must use a fresh owned TIA instance')
        _, _, report['saveReopen'] = verify_native()
        report['saveReopen'].update(status='passed', processId=reopened['processId'], previousProcessId=started['processId'])
        recheck_inputs(packet)
        report['qualificationChecksPassed'] = True
    except BaseException as error:
        report['failure'] = type(error).__name__ + ': ' + str(error)
        raise
    finally:
        try:
            safety_lifecycle.finish(session, report, persist)
        finally:
            try:
                recheck_inputs(packet)
                report['sourceEvidence']['inputsUnchangedAtClose'] = True
            except BaseException as error:
                report['completed'] = False
                report['sourceEvidence']['preservationFailure'] = str(error)
                raise
            finally:
                persist()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
