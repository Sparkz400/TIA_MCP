"""Actual supplied Teamcenter observation intake/reopen checks; no native server is simulated."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError
from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    packet_bytes = (ROOT / "fixtures/teamcenter/observations.json").read_bytes()
    packet = json.loads(packet_bytes)
    manifest_bytes = (ROOT / "fixtures/teamcenter/local-manifest.json").read_bytes()
    encode = lambda value: (json.dumps(value, ensure_ascii=False) + "\n").encode()
    schema = json.loads((ROOT / "contracts/teamcenter-observations.v1.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = dict(completed=False, nativeExecuted=False, checks=[], shutdowns=[])
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    validate(packet, 'request')
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-worker.exe'
    def close(client):
        closed = client.close()
        report['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native worker')
    def start():
        client = Client(args.host.resolve(), 30, ['--no-native-workers', '--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        try:
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'teamcenter-review-check', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def upload(client, data):
        status = client.tool('artifact_upload_begin', dict(fileName='observations.json', expectedBytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=status['uploadId'], offset=offset, data=base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()))
        return client.tool('artifact_upload_commit', dict(uploadId=status['uploadId']))
    def read(client, reference):
        chunks, offset = [], 0
        while True:
            part = client.tool('artifact_read', dict(path=reference['path'], offset=offset, count=65536))
            raw = base64.b64decode(part['data'], validate=True)
            assert part['sha256'] == reference['sha256'] and part['offset'] == offset and len(raw) == part['count']
            chunks.append(raw)
            if part['complete']: break
            offset = part['nextOffset']
        raw = b''.join(chunks)
        check(hashlib.sha256(raw).hexdigest() == reference['sha256'], 'Downloaded artifact hash matches exact bytes')
        return raw
    def refuse(client, tool, arguments, code):
        result = client.request('tools/call', dict(name=tool, arguments=arguments))
        error = result['structuredContent']['error']
        check(result.get('isError') and error['code'] == code and error['nativeAccessAttempted'] is False, 'Structured refusal: ' + code)
    def bind_manifest(value, reference, data):
        for phase in ('before','after'):
            value[phase]['localCopies']['rows'][0]['manifest'] = dict(uploadRef=reference, sha256=hashlib.sha256(data).hexdigest())
    try:
        client = start()
        try:
            check({'teamcenter_observation_review','teamcenter_observation_page'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both Teamcenter tools registered')
            validate(json.loads(manifest_bytes), 'manifest')
            manifest_upload = upload(client, manifest_bytes)
            bind_manifest(packet, manifest_upload['artifactRef'], manifest_bytes)
            packet_bytes = encode(packet); incoming = upload(client, packet_bytes)
            receipt = client.tool('teamcenter_observation_review', dict(uploadRef=incoming['artifactRef']))
            validate(receipt, 'receipt')
            check(receipt == client.tool('teamcenter_observation_review', dict(uploadRef=incoming['artifactRef'])), 'Repeated intake is byte-idempotent')
            check(read(client, receipt['input']) == packet_bytes, 'Exact original request bytes retained')
            check(read(client, receipt['manifestArtifacts'][manifest_upload['artifactRef']]) == manifest_bytes, 'Exact separate manifest bytes retained')
            record = json.loads(read(client, receipt['reportArtifact'])); validate(record, 'record'); result = record['report']
            check(result['summary']['manifestCount'] == 1, 'Shared manifest is captured once')
            check(result['manifests'][0]['manifestBytesVerified'] and not result['manifests'][0]['fileBytesVerified'], 'Verified manifest bytes do not verify declared file contents')
            check(all(result[k] is False for k in ('nativeExecutable','sourceAuthenticityVerified','checkoutOwnershipVerified','nativeCacheVerified','liveFreshnessVerified')), 'No native authority or cache claim')
            for code in ('reportedCheckout','unknownLock','reportedVersionMismatch','incompleteManifest'):
                check(any(r['code'] == code for r in result['issues']), 'Issue preserved: '+code)
            for section in ('observations','changes','issues','manifests'):
                offset, rows = 0, []
                while True:
                    page = client.tool('teamcenter_observation_page', dict(recordId=receipt['recordId'],section=section,offset=offset,count=2)); validate(page,'page')
                    check(len(encode(page)) < 128*1024, 'Bounded '+section+' page')
                    rows.extend(r['value'] for r in page['rows'])
                    if page['nextOffset'] is None: break
                    check(page['nextOffset'] > offset, 'Page makes progress'); offset=page['nextOffset']
                check(rows == result[section], 'Complete '+section+' pages match retained report')
            for label in ('extra','credentialField','credentialProperty','duplicate','authority','instance','credentialsInUrl','failedRows','wrongType','unknownOwner','sameCapture','manifestHash','manifestRoot'):
                bad=copy.deepcopy(packet)
                if label=='extra':bad['nativeQualified']=True
                elif label=='credentialField':bad['after']['connection']['sessionToken']='must-not-retain'
                elif label=='credentialProperty':bad['after']['items']['rows'][0]['properties'][0]['name']='SessionToken'
                elif label=='duplicate':bad['after']['datasets']['rows'].append(copy.deepcopy(bad['after']['datasets']['rows'][0]))
                elif label=='authority':bad['after']['connection']['serverUrl']='https://another.invalid/'
                elif label=='instance':bad['after']['connection']['instance']='other'
                elif label=='credentialsInUrl':bad['after']['connection']['serverUrl']='https://user:secret@teamcenter.invalid/'
                elif label=='failedRows':bad['after']['items']['coverage']='failed';bad['after']['items']['diagnostic']='public failure'
                elif label=='wrongType':bad['after']['datasets']['rows'][0]['datasetType']='T4TiaLibraryDataset'
                elif label=='unknownOwner':bad['after']['datasets']['rows'][0]['lock']['owner']='invented'
                elif label=='sameCapture':bad['after']['captureId']=bad['before']['captureId']
                elif label=='manifestHash':bad['after']['localCopies']['rows'][0]['manifest']['sha256']='0'*64
                else:bad['after']['localCopies']['rows'][0]['path']='other-root'
                uploaded=upload(client,encode(bad));refuse(client,'teamcenter_observation_review',dict(uploadRef=uploaded['artifactRef']),'InvalidTeamcenterObservation')
                if label in ('extra','credentialField','credentialsInUrl','failedRows','wrongType','unknownOwner'):
                    try:validate(bad,'request')
                    except ValidationError:check(True,'Independent schema refuses '+label)
                    else:raise AssertionError('Schema accepted '+label)
            for label in ('traversal','alias','ancestor','byteCount','missingDiagnostic','extra'):
                bad_manifest=json.loads(manifest_bytes)
                if label=='traversal':bad_manifest['files'][0]['path']='../outside'
                elif label=='alias':bad_manifest['files'][1]['path']='project.AP21'
                elif label=='ancestor':bad_manifest['files'][1]['path']='Project.ap21/child'
                elif label=='byteCount':bad_manifest['files'][0]['bytes']=9007199254740992
                elif label=='missingDiagnostic':bad_manifest['diagnostic']=None
                else:bad_manifest['sessionToken']='secret-field'
                raw=encode(bad_manifest);uploaded_manifest=upload(client,raw);bad=copy.deepcopy(packet);bind_manifest(bad,uploaded_manifest['artifactRef'],raw)
                uploaded=upload(client,encode(bad));refuse(client,'teamcenter_observation_review',dict(uploadRef=uploaded['artifactRef']),'InvalidTeamcenterObservation')
            duplicate=packet_bytes.replace(b'"producer":',b'"producer":"duplicate","producer":',1)
            check(duplicate != packet_bytes,'Duplicate-key negative control constructed')
            uploaded=upload(client,duplicate);refuse(client,'teamcenter_observation_review',dict(uploadRef=uploaded['artifactRef']),'InvalidTeamcenterObservation')
            adversary=copy.deepcopy(record);adversary['report']['fileBytesVerified']=True
            try:validate(adversary,'record')
            except ValidationError:check(True,'Schema refuses invented file-content proof')
            else:raise AssertionError('Schema admitted file proof')
            # Large custom metadata remains exact in the artifact; pages explicitly omit oversize rows.
            large=copy.deepcopy(packet);large['after']['items']['rows'][0]['properties']=[dict(name='public'+str(i),value='x'*4096) for i in range(64)]
            uploaded=upload(client,encode(large));large_receipt=client.tool('teamcenter_observation_review',dict(uploadRef=uploaded['artifactRef']))
            page=client.tool('teamcenter_observation_page',dict(recordId=large_receipt['recordId'],section='observations',count=200));validate(page,'page')
            check(any(r.get('omitted') for r in page['rows']) and len(encode(page)) < 128*1024,'Oversized metadata row is explicitly omitted within page budget')
            refuse(client,'teamcenter_observation_page',dict(recordId=receipt['recordId'],section='execute'),'InvalidRequest')
            refuse(client,'teamcenter_observation_page',dict(recordId=receipt['recordId'],section='manifests',offset=999),'InvalidRequest')
            client.tool('artifact_upload_release',dict(uploadId=incoming['uploadId']))
            client.tool('artifact_upload_release',dict(uploadId=manifest_upload['uploadId']))
        finally:close(client)
        client=start()
        try:
            check(client.tool('teamcenter_observation_page',dict(recordId=receipt['recordId'])) == receipt,'Restart reopens after request and manifest upload release')
            for reference in (receipt['input'],receipt['reportArtifact'],*receipt['manifestArtifacts'].values()):
                path=artifacts/reference['path'];original=path.read_bytes()
                try:
                    path.write_bytes(original+b' ');refuse(client,'teamcenter_observation_page',dict(recordId=receipt['recordId']),'ArtifactChanged')
                finally:path.write_bytes(original)
            for label in ('authority','unusedManifest','missingManifest'):
                forged=copy.deepcopy(record)
                if label=='authority':forged['report']['checkoutOwnershipVerified']=True
                elif label=='unusedManifest':forged['manifests']['upload:'+'f'*32]=next(iter(forged['manifests'].values()))
                else:forged['manifests']={}
                raw=encode(forged);identifier=hashlib.sha256(raw).hexdigest();(artifacts/'teamcenter-reviews'/(identifier+'.json')).write_bytes(raw)
                refuse(client,'teamcenter_observation_page',dict(recordId=identifier),'ArtifactChanged')
            check(client.tool('teamcenter_observation_page',dict(recordId=receipt['recordId'])) == receipt,'Rejected forgery leaves original evidence usable')
        finally:close(client)
        report['completed']=True
    finally:
        report['checkCount']=len(report['checks']);(output/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(f"PASS Teamcenter review MCP: {len(report['checks'])} checks; two Host lifetimes, supplied manifests only.")


if __name__ == '__main__':main()
