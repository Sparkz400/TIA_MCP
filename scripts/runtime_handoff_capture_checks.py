"""Join actual Host session/capture lifecycle with retained synthetic C03 declarations."""
import base64
import copy
import hashlib
import json
import time
import uuid
from pathlib import Path
from runtime_capture_checks import capture_call, validator, last_read
from test_runtime_handoff_contracts import verify as verify_handoff


def retain_input(client, name, packet, **args):
    def upload(value):
        raw=json.dumps(value,ensure_ascii=False,separators=(',',':')).encode()
        incoming=client.tool('artifact_upload_begin',dict(fileName='public.json',expectedBytes=len(raw),sha256=hashlib.sha256(raw).hexdigest()))
        for offset in range(0,len(raw),incoming['maxChunkBytes']):
            client.tool('artifact_upload_chunk',dict(uploadId=incoming['uploadId'],offset=offset,data=base64.b64encode(raw[offset:offset+incoming['maxChunkBytes']]).decode()))
        return client.tool('artifact_upload_commit',dict(uploadId=incoming['uploadId']))
    incoming=upload(packet)
    result=client.tool(name,dict(uploadRef=incoming['artifactRef'],**args))
    client.tool('artifact_upload_release',dict(uploadId=incoming['uploadId']))
    return result


def prepare_handoff(client, attempt, output, check):
    def tool(name, args): return client.tool(name, args)
    session=tool('runtime_web_status',dict(attemptId=attempt))
    fixtures=Path(__file__).resolve().parents[1]/'fixtures/physical-runtime'
    review=json.loads((fixtures/'association-review.json').read_text(encoding="utf-8"))
    owner=dict(nodeId='handoff-cpu',objectPath='/projects/Fixture/devices/CPU')
    review['model']['nodes'].append(dict(**owner,kind='hardware',name='Synthetic CPU',parentId='project'))
    saved=retain_input(client, 'project_snapshot_import',review);review_id=saved['reviewId']
    identity=dict(domain='cpuRuntime',sourceId='synthetic-cpu',version='synthetic',securityMode=session['login']['mode'],unknownReason=None)
    right=dict(id='ReadTag',scope='synthetic-cpu')
    snapshot=dict(identity=identity,entitiesComplete=True,entities=[dict(id=session['login']['user'],kind='user',enabled=True,rightsComplete=True,assignmentsComplete=True,rights=[right],inherits=[])])
    access_packet=dict(schemaVersion='access-review-request.v1',reviewId=review_id,owner=owner,identity=identity,subjectId=session['login']['user'],afterKind='observation',requiredRights=[right],allowedRights=[right],before=snapshot,after=snapshot)
    access=retain_input(client, 'access_review',access_packet,reviewId=review_id)
    expected=dict(hostId=session['hostId'],context=session['context'],subjectId=session['login']['user'],securityMode=session['login']['mode'],securityDomain='cpuRuntime',clientId='synthetic-client',credential=session['login']['credential'],targetIdentity=dict(scheme='synthetic',value='cpu'),programIdentity=dict(scheme='synthetic',value='program'))
    originals={}
    references={}
    for role in ('target','program','session'):
        row=dict(schemaVersion='runtime-handoff-'+role+'.v1',sourceKind='synthetic',hostId=session['hostId'],context=session['context'],sequence=1,freshness='current')
        if role=='session':
            row.update({k:expected[k] for k in ('subjectId','securityMode','securityDomain','clientId','credential')})
            row.update(peerVerified=True,authenticated=True,state='connected',permissionsComplete=True,permissions=[dict(**right,outcome='allowed'),dict(id='ModifyTag',scope='synthetic-cpu',outcome='denied')])
        else: row['identity']=expected[role+'Identity']
        originals[role]=row
        references[role]=retain_input(client, 'runtime_handoff_observation',row,role=role)['artifact']
    packet=dict(schemaVersion='runtime-handoff-request.v1',reviewId=review_id,accessRecordId=access['recordId'],accessPhase='before',owner=owner,expected=expected,operations=[dict(**right,requiredOutcome='allowed'),dict(id='ModifyTag',scope='synthetic-cpu',requiredOutcome='denied')],baseline=copy.deepcopy(references),current=copy.deepcopy(references))
    handoff=retain_input(client, 'runtime_handoff_review',packet)
    handoff_path=output/'store/runtime-handoff/records'/handoff['recordRef'].split(':')[1]
    verify_handoff(output/'store',json.loads((handoff_path/'request.json').read_text(encoding="utf-8")),json.loads((handoff_path/'report.json').read_text(encoding="utf-8")))
    check(True,'Independent original-input evaluator verifies bound handoff, access and source model')
    return session, packet, handoff, originals


def run_handoff_capture_checks(client, attempt, state, output, check):
    def tool(name, args): return client.tool(name, args)
    session, packet, handoff, originals = prepare_handoff(client, attempt, output, check)
    review_id=packet["reviewId"]
    fixtures=Path(__file__).resolve().parents[1]/"fixtures/physical-runtime"
    case_request=json.loads((fixtures/'association-request.json').read_text(encoding="utf-8"));case_request['reviewId']=review_id
    case_request['hypotheses'].append(dict(id='bound',statement='Observed pattern stays unqualified.',prediction=dict(when=[dict(signalId='enable',op='eq',value=True)],expect=[dict(signalId='step',op='eq',value=65535,unit='count')])))
    case=tool('diagnostic_plan',dict(request=case_request))
    state['symbols']={'"DB_Line".Conv2.Enable':dict(name='Enable',datatype='bool',value=True),'"DB_Line".Conv2.Step':dict(name='Step',datatype='uint',value=65535)}
    selections=[tool('runtime_web_browse',dict(attemptId=attempt,symbol=s,children=False))['selectionId'] for s in state['symbols']]
    mapping=dict(caseId=case['id'],mappings=[dict(selectionId=s,signalId=n,reason='Explicit synthetic binding') for s,n in zip(selections,['enable','step'])])
    request=dict(attemptId=attempt,selectionIds=selections,intervalMilliseconds=100,durationSeconds=5,maximumSamples=4,maximumBytes=1048576,reviewId=review_id,association=mapping,handoffRef=handoff['recordRef'])
    def call(name,**args):return capture_call(client,name,args,check)
    def wait(identity):
        deadline=time.monotonic()+15
        while time.monotonic()<deadline:
            status=call('status',captureId=identity)
            if status['state']!='running':return status
            time.sleep(.05)
        raise AssertionError('Bound capture did not finish')
    retained=[]
    for mode in ('normal','providerDenied','deniedRights','gapped'):
        state['mode']='providerDenied' if mode=='providerDenied' else 'normal';state['rights']=mode!='deniedRights'
        state['readDelay']=.2 if mode=='gapped' else 0
        identity=uuid.uuid4().hex;before=state['calls'].count('PlcProgram.Read')
        call('start',captureId=identity,request=request);status=wait(identity)
        binding=status['plan']['handoff']
        check(binding['session']['hostId']==session['hostId'] and status['plan']['hostId']!=session['hostId'],'Handoff keeps exact session owner separate from capture owner: '+mode)
        check(binding['recordRef']==handoff['recordRef'] and all(binding[k] is False for k in ('deviceIdentityVerified','programMatchVerified','effectiveRightsVerified','sourceAuthenticityVerified','operationAdmitted','assertionsAdmitted')),'Bound capture does not grant native identity or authority: '+mode)
        analysis=call('analyze',snapshotRef=status['snapshotRef'],units=dict(enable='1',step='count'))
        check(analysis['report']['handoff']==binding and all(h['verdict']=='unresolved' for h in analysis['report']['hypotheses']),'Bound analysis retains context independently of unresolved verdicts: '+mode)
        quality=analysis['report']['qualityAssessment']
        if mode=='providerDenied':check(quality['providerFailures']==4 and next(h for h in analysis['report']['hypotheses'] if h['id']=='bound')['unknownCycles']==2,'Denied provider samples remain unknown despite matching handoff')
        if mode=='deniedRights':check(quality['hostReadErrors']==1 and state['calls'].count('PlcProgram.Read')==before,'Changed effective read permission dispatches no value read')
        if mode=='gapped':check(quality['skippedPolls']>=1 and analysis['report']['observedChangeCount']==0,'Bound capture retains missed polls without inventing intermediate transitions')
        exported=call('export',snapshotRef=status['snapshotRef']);document=json.loads(Path(exported['artifacts'][0]['path']).read_text(encoding="utf-8"));validator().validate(document)
        check(document['capture']['plan']['handoff']==binding,'Export retains exact handoff: '+mode)
        retained.append(dict(captureId=identity,snapshotRef=status['snapshotRef'],state='ended',reviewId=review_id,handoff=binding,reportId=analysis['id']))
    state['mode']='normal';state['rights']=True;state['readDelay']=0
    altered=copy.deepcopy(packet);program=copy.deepcopy(originals['program']);program['identity']['value']='changed-program';program['sequence']=2
    altered['current']['program']=retain_input(client,'runtime_handoff_observation',program,role='program')['artifact']
    stale=retain_input(client,'runtime_handoff_review',altered)
    before=len(state['calls']);identity=uuid.uuid4().hex
    capture_call(client,'start',dict(captureId=identity,request={**request,'handoffRef':stale['recordRef']}),check,'RuntimeCaptureHandoffMismatch')
    check(len(state['calls'])==before and not (output/'store/runtime-captures'/identity).exists(),'Changed program declarations refuse before capture intent/network')
    identity=uuid.uuid4().hex
    tamper_request={**request,'selectionIds':selections[:1],'association':None,'intervalMilliseconds':2000,'durationSeconds':10,'maximumSamples':10}
    call('start',captureId=identity,request=tamper_request)
    deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        prefix=call('status',captureId=identity)
        if prefix['recordedProgress']['readAttempts']>=2:break
        time.sleep(.02)
    check(prefix['state']=='running' and prefix['recordedProgress']['readAttempts']==2,'Bound capture has committed two single-signal cycles before dependency change')
    original_path=output/'store/runtime-handoff/records'/handoff['recordRef'].split(':')[1]/'request.json';raw=original_path.read_bytes();original_path.write_bytes(raw+b' ')
    before=state['calls'].count('PlcProgram.Read')
    try:
        status=wait(identity);page=call('page',snapshotRef=status['snapshotRef'])
        check(status['recordedProgress']['terminalReason']=='sessionUnavailable' and last_read(page['rows'])['errorCode']=='RuntimeCaptureHandoffUnavailable' and state['calls'].count('PlcProgram.Read')==before,'Changed bound evidence stops polling before another value read')
        capture_call(client,'analyze',dict(snapshotRef=status['snapshotRef'],units=dict(enable='1',step='count')),check,'RuntimeCaptureHandoffUnavailable')
        call('export',snapshotRef=status['snapshotRef'])
        repeated=call('start',captureId=identity,request=tamper_request)
        check(repeated['snapshotRef']==status['snapshotRef'] and state['calls'].count('PlcProgram.Read')==before,'Repeated bound start after dependency failure observes prior intent without replay')
    finally:original_path.write_bytes(raw)
    retained.append(dict(captureId=identity,snapshotRef=status['snapshotRef'],state='ended',reviewId=review_id,handoff=status['plan']['handoff']))
    del state['symbols']
    (output/'handoff-captures.json').write_text(json.dumps(dict(completed=True,nativeExecuted=False,reviewId=review_id,recordRef=handoff['recordRef'],captures=retained),indent=2)+'\n',encoding='utf-8')
    return retained
