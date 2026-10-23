"""Real stdio Host and restricted local Git; native worker paths deliberately do not exist."""
import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import zipfile
from jsonschema import Draft202012Validator
from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--git-executable', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists(): raise ValueError('Use a fresh output directory.')
    output.mkdir(parents=True)
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-native-worker.exe'
    schema_path = ROOT / 'contracts/vci-workspace.v1.schema.json'
    schema = json.loads(schema_path.read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    tools = set(schema['x-tool-contracts'])
    allowed = tools | {'artifact_upload_begin', 'artifact_upload_chunk', 'artifact_upload_commit', 'artifact_upload_release'}
    digest = lambda data: hashlib.sha256(data).hexdigest()
    report = dict(completed=False, nativeExecuted=False, checks=[], hostExits=[],
                  hostSha256=digest(args.host.read_bytes()), contractSha256=digest(schema_path.read_bytes()),
                  gitSha256=digest(args.git_executable.read_bytes()), harnessSha256=digest(Path(__file__).read_bytes()),
                  hostFiles=[dict(name=p.name,bytes=p.stat().st_size,sha256=digest(p.read_bytes()))
                             for p in sorted(args.host.parent.iterdir()) if p.is_file() and p.suffix in ('.exe','.dll','.json')])
    client = None

    def check(name, condition):
        if not condition: raise AssertionError(name)
        report['checks'].append(name)

    def start(configured=True):
        extra = ['--worker', str(missing), '--sim-worker', str(missing), '--artifacts', str(artifacts)]
        if configured: extra += ['--git-executable', str(args.git_executable.resolve())]
        connection = Client(args.host.resolve(), 90, extra)
        connection.request('initialize', {'protocolVersion':'2025-11-25', 'capabilities':{},
                                          'clientInfo':{'name':'vci-offline-contract','version':'1'}})
        connection.notify('notifications/initialized')
        return connection

    def stop():
        receipt = client.close(); report['hostExits'].append(receipt)
        check('Host exits normally without native workers', receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'] and
              all(row.get('started') is not True for row in receipt['workerExits']))

    def call(name, arguments=None):
        if name not in allowed: raise AssertionError('Non-offline tool attempted')
        if name in tools:
            Draft202012Validator(dict(schema, oneOf=[schema['x-tool-contracts'][name]['input']])).validate(arguments or {})
        result = client.tool(name, arguments or {})
        if name in tools:
            validator.validate(result)
            Draft202012Validator(dict(schema, oneOf=[schema['x-tool-contracts'][name]['output']])).validate(result)
        return result

    def reject(name, arguments, code):
        if name not in allowed: raise AssertionError('Non-offline tool attempted')
        result = client.request('tools/call', {'name':name,'arguments':arguments})
        value = result.get('structuredContent', {})
        validator.validate(value)
        check(name + ' refuses ' + code, result.get('isError') and value.get('error',{}).get('code') == code)

    def upload(data):
        status = call('artifact_upload_begin', {'fileName':'source.bin','expectedBytes':len(data),'sha256':digest(data)})
        for offset in range(0, len(data), status['maxChunkBytes']):
            call('artifact_upload_chunk', {'uploadId':status['uploadId'],'offset':offset,
                                          'data':base64.b64encode(data[offset:offset+status['maxChunkBytes']]).decode()})
        return call('artifact_upload_commit', {'uploadId':status['uploadId']})

    def download(record, name=None, path=None):
        result = bytearray()
        while True:
            page = call('vci_artifact_read' if name else 'vci_file_read',
                        dict(recordRef=record, offset=len(result), **({'name':name} if name else {'relativePath':path})))
            result.extend(base64.b64decode(page['data'], validate=True))
            if page['complete']:
                check('download is complete and SHA-256 matched', len(result) == page['bytes'] and digest(result) == page['sha256'])
                return bytes(result)

    def directory(reference): return artifacts / 'vci-workspaces/records' / reference.split(':')[2]

    author = dict(name='MCP Fixture', email='fixture@example.invalid')
    try:
        client = start(False)
        reject('vci_list', {}, 'GitNotConfigured')
        stop(); client = start()
        listed = {row['name'] for row in client.request('tools/list')['tools']}
        check('all VCI tools registered in normal Host', tools <= listed)
        raw = b'\xef\xbb\xbforiginal\r\n\x00\xff'
        uploaded = upload(raw)
        preview = call('vci_prepare', dict(files=[dict(relativePath='PLC/Über Motor.s7dcl',uploadRef=uploaded['artifactRef']),
                                                dict(relativePath='empty.txt')],author=author,message='first'))
        check('preview discloses untracked-diff limitation', 'new files' in preview['diffCoverage'])
        check('preview raw-file review preserves independent source bytes', download(preview['recordRef'],path='PLC/Über Motor.s7dcl') == raw)
        check('explicit empty file is preserved', download(preview['recordRef'],path='empty.txt') == b'')
        call('artifact_upload_release', {'uploadId':uploaded['uploadId']})
        stop(); client = start()
        check('restart and upload release retain independent candidate', call('vci_reopen',{'recordRef':preview['recordRef']})['planSha256'] == preview['planSha256'])
        reject('vci_commit',dict(previewRef=preview['recordRef'],reviewedPlanSha256='a'*64),'PreviewStale')
        first = call('vci_commit',dict(previewRef=preview['recordRef'],reviewedPlanSha256=preview['planSha256']))
        receipt_bytes = download(first['recordRef'],name=first['receiptName'])
        check('complete receipt independently hashes to its handle and identifies configured Git',
              digest(receipt_bytes)==first['recordRef'].split(':')[-1] and first['executableSha256']==report['gitSha256'] and first['gitVersion'].startswith('git version'))
        reject('vci_commit',dict(previewRef=preview['recordRef'],reviewedPlanSha256=preview['planSha256']),'RecoveryRequired')
        first_path = directory(first['recordRef'])
        (first_path/'commit.json').rename(first_path/'commit.lost.json')
        stop(); client = start()
        unknown = call('vci_reopen',dict(recordRef=preview['recordRef']))
        check('missing result stays unknown while exact verified callback remains inspectable', unknown['state']=='outcomeUnknown' and unknown['commitJournal']['phase']=='verified')
        reject('vci_reconcile',dict(previewRef=preview['recordRef'],expectedCallbackSha256='b'*64),'RecoveryRequired')
        recovered = call('vci_reconcile',dict(previewRef=preview['recordRef'],expectedCallbackSha256=unknown['commitJournal']['sha256']))
        check('fresh-readback reconciliation restores original revision without mutation replay', recovered['recordRef']==first['recordRef'])
        second_upload = upload(b'second\r\n')
        second = call('vci_prepare',dict(files=[dict(relativePath='PLC/Über Motor.s7dcl',uploadRef=second_upload['artifactRef']),
                                              dict(relativePath='empty.txt',delete=True)],author=author,message='second',parentRevisionRef=first['recordRef']))
        check('tracked diff includes selected deletion', b'empty.txt' in download(second['recordRef'],name='diff.txt'))
        second = call('vci_commit',dict(previewRef=second['recordRef'],reviewedPlanSha256=second['planSha256']))
        history = call('vci_history',dict(revisionRef=second['recordRef']))
        check('descendant Git history retains both exact commits', first['commit'] in history['history'] and second['commit'] in history['history'])
        export = call('vci_materialize',dict(revisionRef=first['recordRef']))
        archive = download(export['recordRef'],name='revision.zip')
        with zipfile.ZipFile(io.BytesIO(archive)) as zip_file:
            check('old revision export preserves original BOM CRLF binary and Unicode path', zip_file.read('PLC/Über Motor.s7dcl')==raw and zip_file.read('empty.txt')==b'')
        reject('vci_file_read',dict(recordRef=first['recordRef'],relativePath='../private'),'VciArtifactMissing')
        reject('vci_prepare',dict(files=[dict(relativePath='.git/config')],author=author,message='unsafe'),'GitUnsafePath')
        reject('vci_prepare',dict(files=[dict(relativePath='a'),dict(relativePath='A')],author=author,message='collision'),'GitPathCollision')
        reject('vci_history',dict(revisionRef=second['recordRef'],count=51),'VciInputLimit')
        named=call('vci_prepare',dict(files=[dict(relativePath='branch.txt')],author=author,message='named candidate',
                                     parentRevisionRef=first['recordRef'],branchName='review/offline'))
        check('named branch is reviewable before commit', named['headReference']=='refs/heads/review/offline')
        named=call('vci_commit',dict(previewRef=named['recordRef'],reviewedPlanSha256=named['planSha256']))
        stop();client=start()
        check('named branch survives actual Host restart',call('vci_reopen',dict(recordRef=named['recordRef']))['headReference']=='refs/heads/review/offline')
        page=call('vci_branches',dict(revisionRef=named['recordRef'],count=1))
        second_page=call('vci_branches',dict(revisionRef=named['recordRef'],count=1,offset=page['nextOffset'],expectedInventorySha256=page['inventorySha256']))
        check('paged branches bind parent and named commits exactly',page['rows']==[dict(reference='refs/heads/main',commit=first['commit'])] and
              second_page['rows']==[dict(reference='refs/heads/review/offline',commit=named['commit'])] and second_page['nextOffset'] is None)
        reject('vci_branches',dict(revisionRef=named['recordRef'],offset=1),'VciInputLimit')
        reject('vci_branches',dict(revisionRef=named['recordRef'],expectedInventorySha256='a'*64),'PreviewStale')
        check('parent repository has no child branch',call('vci_branches',dict(revisionRef=first['recordRef']))['total']==1)
        check('fork source bytes are exact',download(named['recordRef'],path='PLC/Über Motor.s7dcl')==raw)
        callback=json.loads(download(named['recordRef'],name='fork-journal/0002.json'))
        check('fork callback is retained and bound',callback['record']['operation']=='fork' and callback['record']['phase']=='verified' and
              callback['record']['candidateCommit']==first['commit'])
        continued=call('vci_prepare',dict(files=[dict(relativePath='next.txt')],author=author,message='continue branch',parentRevisionRef=named['recordRef']))
        check('descendant inherits named branch',continued['headReference']=='refs/heads/review/offline')
        reject('vci_prepare',dict(files=[dict(relativePath='bad.txt')],author=author,message='no reset',parentRevisionRef=named['recordRef'],branchName='main'),'GitReferenceConflict')
        reject('vci_prepare',dict(files=[dict(relativePath='bad.txt')],author=author,message='no option',branchName='--orphan'),'GitInvalidReference')
        changed_ref=directory(named['recordRef'])/'repository/.git/refs/heads/external'
        changed_ref.write_text(first['commit']+'\n',encoding='ascii')
        reject('vci_branches',dict(revisionRef=named['recordRef'],offset=1,expectedInventorySha256=page['inventorySha256']),'PreviewStale')
        changed_ref.unlink()
        named_export=call('vci_materialize',dict(revisionRef=named['recordRef']))
        with zipfile.ZipFile(io.BytesIO(download(named_export['recordRef'],name='revision.zip'))) as named_zip:
            check('named revision materialization preserves inherited and new files',named_zip.read('PLC/Über Motor.s7dcl')==raw and named_zip.read('branch.txt')==b'')
        interrupted=artifacts/'vci-workspaces/records'/('f'*32)/'fork-journal';interrupted.mkdir(parents=True)
        (interrupted/'intent.json').write_text('{}',encoding='ascii')
        check('interrupted fork stays unknown and discoverable',any(row['id']=='f'*32 and row['state']=='outcomeUnknown' for row in call('vci_list',dict(count=50))['rows']))
        snapshot = dict(observationId='authored-stdio-mapping',sourceSha256='a'*64,tiaVersion=21)
        mapping = dict(schemaVersion='vci-mapping-evidence.v1',workspaceId='authored-workspace',snapshot=snapshot,enumerationComplete=True,packages=[dict(
            packageId='motor',object=dict(object=dict(projectPath='D:/Fixture/P.ap21',plcPath='/PLC',scopePath=None,objectPath='/PLC/Motor',kind='block'),
                                         snapshot=snapshot),nativeStatus='Equal',profile=dict(profileId='authored-profile',family='nativeDocument',objectKind='block',
            nativeFormat='AuthoredFixtureFormat',evidenceSha256='b'*64,evidenceOrigin='authoredFixture',complete=True,
            expectedFiles=[dict(relativePath='PLC/Über Motor.s7dcl',role='document')]))])
        mapping_validator=Draft202012Validator(json.loads((ROOT/'contracts/vci-mapping-evidence.v1.schema.json').read_text(encoding='utf-8')))
        mapping_validator.validate(mapping)
        mapping_bytes=json.dumps(mapping,ensure_ascii=False).encode()
        mapping_upload=upload(mapping_bytes)
        baseline=call('vci_package_capture',dict(sourceRecordRef=first['recordRef'],mappingUploadRef=mapping_upload['artifactRef']))
        project=call('vci_package_capture',dict(sourceRecordRef=second['recordRef'],mappingUploadRef=mapping_upload['artifactRef']))
        third_upload=upload(b'third\r\n')
        third=call('vci_prepare',dict(files=[dict(relativePath='PLC/Über Motor.s7dcl',uploadRef=third_upload['artifactRef'])],
                                    author=author,message='third side',parentRevisionRef=first['recordRef']))
        workspace=call('vci_package_capture',dict(sourceRecordRef=third['recordRef'],mappingUploadRef=mapping_upload['artifactRef']))
        call('artifact_upload_release',dict(uploadId=mapping_upload['uploadId']))
        stop(); client=start()
        check('package mappings survive original upload release and Host restart',
              download(baseline['recordRef'],name='mapping.json')==mapping_bytes and call('vci_reopen',dict(recordRef=baseline['recordRef']))['targetProbed'] is False)
        manifest=json.loads(download(baseline['recordRef'],name='manifest.json'))
        Draft202012Validator(json.loads((ROOT/'contracts/vci-manifest.v1.schema.json').read_text(encoding='utf-8'))).validate(manifest)
        check('package manifest hashes independently supplied source bytes and reports unmanaged files',
              next(f for f in manifest['files'] if f['relativePath']=='PLC/Über Motor.s7dcl')['sha256']==digest(raw) and baseline['unmanagedFileCount']==1)
        compare_args=dict(baselineRef=baseline['recordRef'],projectRef=project['recordRef'],workspaceRef=workspace['recordRef'],
                          expectedHashes=dict(baseline=baseline['manifestSha256'],project=project['manifestSha256'],workspace=workspace['manifestSha256']))
        comparison=call('vci_package_compare',compare_args)
        conflict=json.loads(download(comparison['recordRef'],name='comparison.json'))
        Draft202012Validator(json.loads((ROOT/'contracts/vci-conflict-report.v1.schema.json').read_text(encoding='utf-8'))).validate(conflict)
        check('three actual Host snapshots report divergent raw changes despite identical native-status assertions',
              comparison['conflictCount']==1 and conflict['rows'][0]['state']=='divergentChange' and
              all(conflict['rows'][0][k]=='Equal' for k in ('baselineNativeStatus','projectNativeStatus','workspaceNativeStatus')))
        check('package comparison cannot authorize synchronization or apply a resolution',
              comparison['nativeQualified'] is False and comparison['canSynchronize'] is False and comparison['resolutionApplied'] is False)
        stale=call('vci_package_compare',dict(compare_args,expectedHashes=dict(compare_args['expectedHashes'],baseline='f'*64)))
        check('wrong expected package hash remains an explicit stale conflict',stale['stateCounts']=={'stale':1})
        invalid_mapping=upload(json.dumps(dict(mapping,nativeQualified=True)).encode())
        reject('vci_package_capture',dict(sourceRecordRef=first['recordRef'],mappingUploadRef=invalid_mapping['artifactRef']),'VciInvalidMapping')
        saved_mapping=(directory(baseline['recordRef'])/'mapping.json').read_bytes()
        (directory(baseline['recordRef'])/'mapping.json').write_bytes(saved_mapping+b' ')
        reject('vci_reopen',dict(recordRef=comparison['recordRef']),'VciIntegrityFailure')
        (directory(baseline['recordRef'])/'mapping.json').write_bytes(saved_mapping)
        check('exact mapping restoration revalidates comparison provenance',call('vci_reopen',dict(recordRef=comparison['recordRef']))['reportSha256']==comparison['reportSha256'])
        damaged = call('vci_prepare',dict(files=[dict(relativePath='new.txt')],author=author,message='damage',parentRevisionRef=first['recordRef']))
        (directory(damaged['recordRef'])/'repository/new.txt').write_bytes(b'damaged')
        reject('vci_commit',dict(previewRef=damaged['recordRef'],reviewedPlanSha256=damaged['planSha256']),'VciIntegrityFailure')
        check('damaged candidates remain discoverable', any(row['state']=='integrity-failed' for row in call('vci_list')['rows']))
        check('parent remains independently intact', download(first['recordRef'],path='PLC/Über Motor.s7dcl') == raw)
        # Stop only this harness's live Host tree after its durable dispatch record.
        interrupted = call('vci_prepare',dict(files=[dict(relativePath='interrupted/'+str(i)+'.txt') for i in range(50)],
                                              author=author,message='owned Host interruption',parentRevisionRef=first['recordRef']))
        interrupted_ref = interrupted['recordRef']
        call_outcome = {}
        owned_client = client
        def commit_in_background():
            try:
                call_outcome['response'] = owned_client.request('tools/call',{'name':'vci_commit','arguments':{
                    'previewRef':interrupted_ref,'reviewedPlanSha256':interrupted['planSha256']}})
            except Exception as error: call_outcome['transportError'] = str(error)
        commit_thread = threading.Thread(target=commit_in_background)
        commit_thread.start()
        dispatch = directory(interrupted_ref)/'commit-journal/0001.json'
        deadline = time.monotonic()+30
        observed = None
        while time.monotonic()<deadline and commit_thread.is_alive():
            try:
                observed = json.loads(dispatch.read_text(encoding='utf-8'))
                if observed['record']['phase']=='dispatchPrepared': break
            except (OSError, ValueError, KeyError): pass
            time.sleep(.002)
        check('actual Host interruption follows a flushed local Git dispatch callback', observed is not None and
              observed.get('record',{}).get('phase')=='dispatchPrepared' and owned_client.process.poll() is None)
        if os.name != 'nt': raise RuntimeError('The actual Host process-tree interruption check requires Windows.')
        killed = subprocess.run(['taskkill','/PID',str(owned_client.process.pid),'/T','/F'],capture_output=True,
                                timeout=15,creationflags=subprocess.CREATE_NO_WINDOW)
        report['intentionalStopCommand'] = dict(returnCode=killed.returncode,
            stdout=killed.stdout.decode(errors='replace'),stderr=killed.stderr.decode(errors='replace'))
        owned_client.process.wait(timeout=10)
        commit_thread.join(timeout=10)
        receipt = owned_client.close(); receipt['intentionalTestTermination']=True
        report['hostExits'].append(receipt); report['interruptedCall']=call_outcome
        client = None
        check('original owned Host handle confirms requested interruption', receipt['hostExitCode'] is not None and receipt['hostExitCode'] != 0)
        check('interrupted stdio request terminates without a successful commit response', not commit_thread.is_alive() and 'response' not in call_outcome)
        check('interrupted Host never started native workers', not receipt['workerExits'])
        client = start()
        unknown = call('vci_reopen',dict(recordRef=interrupted_ref))
        check('fresh Host retains interrupted dispatch as unknown', unknown['state']=='outcomeUnknown')
        reject('vci_commit',dict(previewRef=interrupted_ref,reviewedPlanSha256=interrupted['planSha256']),'RecoveryRequired')
        reject('vci_reconcile',dict(previewRef=interrupted_ref,expectedCallbackSha256=unknown['commitJournal']['sha256']),'RecoveryRequired')
        check('parent remains intact after real Host loss', download(first['recordRef'],path='PLC/Über Motor.s7dcl') == raw)
        native_store = artifacts/'engineering-operations'
        check('no Siemens worker or native operation records created', not missing.exists() and
              (not native_store.exists() or not any(native_store.iterdir())))
        report['completed'] = True
    except Exception as error:
        report['failure'] = str(error)
    finally:
        if client is not None:
            try: stop()
            except Exception as error: report.update(completed=False,shutdownFailure=str(error))
        report['checkCount']=len(report['checks'])
        (output/'verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(('PASS' if report['completed'] else 'FAIL')+' VCI actual stdio: '+str(report['checkCount'])+' checks; '+str(output/'verification.json'))
    if not report['completed']: print(report.get('failure',report.get('shutdownFailure')))
    return 0 if report['completed'] else 1


if __name__ == '__main__': raise SystemExit(main())
