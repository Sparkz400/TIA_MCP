"""Verify original bound capture chains, migration inputs and actual TLS/process observations."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import ValidationError
from runtime_capture_checks import validator
from test_runtime_handoff_workflow import record


def verify(root, proof, runtime):
    assert proof['completed'] and runtime['completed'] and not proof['nativeExecuted'] and not runtime['nativeExecuted']
    assert runtime['actualTls'] and runtime['actualDpapi']
    requests=runtime['rpcRequests']
    assert requests and all(not r['tlsSessionReused'] for r in requests)
    assert len({r['id'] for r in requests})==len(requests)
    assert [r['method'] for r in requests]==runtime['rpcMethods']
    assert set(runtime['rpcMethods']) <= {'Api.Browse','Api.Login','Api.Logout','Api.GetPermissions','PlcProgram.Browse','PlcProgram.Read','Api.Version','Api.GetQuantityStructures','Api.GetAuthenticationMode','Api.GetSessionInfo'}
    expected={'lostReadResponse','inflightHostCrash','credentialRetired','credentialReplacement','certificateRotated','certificateReplacement','accountReplacement'}
    captures={case['scenario']:case for case in proof['captures']}
    assert set(captures)==expected and len(proof['captures'])==len(expected)
    store=root/'store';rows_by_case={}
    def original(reference):
        record(store,reference)
        folder=store/'runtime-handoff/records'/reference.split(':')[1]
        return json.loads((folder/'request.json').read_bytes()),json.loads((folder/'report.json').read_bytes())
    for label,case in captures.items():
        status=case['status'];validator().validate(status)
        identity=status['captureId'];folder=store/'runtime-captures'/identity
        raw=(folder/'plan.json').read_bytes();plan=json.loads(raw)
        assert plan==status['plan'] and plan['request']=={'representation':'simple',**case['request']}
        request,report=original(plan['handoff']['recordRef'])
        assert report['summary']['state']=='consistentDeclarations'
        assert request['expected']['context']==plan['handoff']['session']['context']
        assert request['reviewId']==plan['request']['reviewId']==plan['handoff']['reviewId']
        reference=status['snapshotRef'];kind,selected,count,digest=reference.split(':')
        assert kind=='capture' and selected==identity and json.loads((folder/'head.json').read_bytes())==reference
        segments=[]
        for index in range(int(count),0,-1):
            content=(folder/f'{index:06d}.segment').read_bytes();assert hashlib.sha256(content).hexdigest()==digest
            segment=json.loads(content);assert segment['index']==index
            digest=segment['previousSha256'];segments.append(segment)
        assert hashlib.sha256(raw).hexdigest()==digest
        rows=[row for segment in reversed(segments) for row in segment['rows']]
        assert [r['sequence'] for r in rows]==list(range(len(rows)))
        assert segments[0]['progress']==status['recordedProgress'] and len(rows)==status['recordedProgress']['rows']
        artifact=case['export'];export_path=Path(artifact['path'])
        assert export_path.parts[-3:-1]==('runtime-captures',identity)
        exported=(folder/export_path.name).read_bytes()
        assert len(exported)==artifact['bytes'] and hashlib.sha256(exported).hexdigest()==artifact['sha256']
        document=json.loads(exported);validator().validate(document)
        assert document['capture']['plan']==plan and document['rows']==rows and document['capture']['snapshotRef']==reference
        assert status['state']==('interrupted' if label=='inflightHostCrash' else 'ended')
        rows_by_case[label]=[r for r in rows if r['kind']=='read']
    lost=rows_by_case['lostReadResponse']
    assert len(lost)==1 and lost[0]['errorCode']=='RuntimeTransportFailure' and lost[0]['readResultJson'] is None
    first,last=proof['lostReadRpcRange'];assert 0<=first<last<=len(requests)
    assert sum(r['method']=='PlcProgram.Read' for r in requests[first:last])==1
    for key in ['retiredCredentialRpcRange','rotatedCertificateRpcRange']:
        first,last=proof[key];assert 0<=first==last<=len(requests)
    assert rows_by_case['credentialRetired'][-1]['errorCode']=='CredentialRetired'
    assert rows_by_case['certificateRotated'][-1]['errorCode']=='RuntimeSessionUnavailable'
    crash=proof['crash'];status=captures['inflightHostCrash']['status']
    assert crash['processId']>0 and crash['exit']['hostExitCode']!=0 and crash['exit']['stderrEofObserved']
    assert crash['exit']['workerExits']==[] and crash['inFlightRequest'] in requests and crash['inFlightRequest']['method']=='PlcProgram.Read'
    assert crash['committedSnapshotRef']==status['snapshotRef'] and status['uncommittedDataUnknown']
    migrations={m['kind']:m for m in proof['migrations']}
    assert set(migrations)=={'credentialReplacement','certificateReplacement','accountReplacement'} and len(proof['migrations'])==3
    for label,migration in migrations.items():
        old,_=original(migration['oldHandoffRef']);new,_=original(migration['newHandoffRef']);comparison,report=original(migration['comparisonRef'])
        assert migration['oldHandoffRef']!=migration['newHandoffRef'] and comparison['baseline']==old['baseline'] and comparison['current']==new['current']
        assert report['summary']['state']=='requiresFreshEvidence' and not report['operationAdmitted']
        assert captures[label]['request']['handoffRef']==migration['newHandoffRef']
        assert len(rows_by_case[label])==1 and rows_by_case[label][0]['errorCode'] is None
        if label=='credentialReplacement':assert old['expected']['credential']!=new['expected']['credential']
        if label=='certificateReplacement':
            assert old['expected']['context']['endpoint']==new['expected']['context']['endpoint']
            assert old['expected']['context']['peerCertificateSha256']!=new['expected']['context']['peerCertificateSha256']==proof['certificateProbe']['peerCertificateSha256']
        if label=='accountReplacement':assert old['expected']['subjectId']!=new['expected']['subjectId']
    assert not proof['certificateProbe']['sessionReused']


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--fixture',type=Path,required=True);args=parser.parse_args()
    root=args.fixture.resolve();proof=json.loads((root/'handoff-lifecycle.json').read_bytes());runtime=json.loads((root/'verification.json').read_bytes())
    verify(root,proof,runtime)
    controls={
        'missing capture':lambda p,r:p['captures'].pop(),
        'invented crash request':lambda p,r:p['crash']['inFlightRequest'].update(id='0'*32),
        'wrong export hash':lambda p,r:p['captures'][0]['export'].update(sha256='0'*64),
        'promoted authority':lambda p,r:p['captures'][0]['status']['plan']['handoff'].update(programMatchVerified=True),
        'wrong migration':lambda p,r:p['migrations'][0].update(newHandoffRef=p['migrations'][0]['oldHandoffRef']),
        'wrong certificate':lambda p,r:p['certificateProbe'].update(peerCertificateSha256='0'*64),
        'resumed TLS':lambda p,r:r['rpcRequests'][0].update(tlsSessionReused=True),
    }
    for name,mutate in controls.items():
        changed,observed=copy.deepcopy(proof),copy.deepcopy(runtime);mutate(changed,observed)
        try:verify(root,changed,observed)
        except (AssertionError,ValueError,ValidationError):pass
        else:raise AssertionError('Verifier accepted '+name)
    result=dict(completed=True,nativeExecuted=False,captures=7,migrations=3,mutationControls=list(controls),rpcCount=len(runtime['rpcRequests']),originalCaptureChains=True,originalHandoffInputs=True)
    (root/'handoff-lifecycle-independent.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('PASS independent bound lifecycle: seven capture chains, three original migration comparisons and seven mutation controls')


if __name__=='__main__':main()
