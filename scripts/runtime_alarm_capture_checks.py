"""Saved alarm bindings through the actual Host; only disposable loopback reads."""
import copy
import hashlib
import json
import time
import uuid
from pathlib import Path
from runtime_capture_checks import capture_call, validator


def run_alarm_capture_checks(client, attempt, state, output, check):
    fixture=Path(__file__).resolve().parents[1]/'fixtures/physical-runtime'
    raw=(fixture/'alarm-review.json').read_bytes(); review_id=hashlib.sha256(raw).hexdigest()
    review_path=output/'store/project-reviews'/(review_id+'.json');review_path.parent.mkdir(parents=True,exist_ok=True);review_path.write_bytes(raw)
    request=json.loads((fixture/'alarm-request.json').read_text(encoding='utf-8'))
    check(request['reviewId']==review_id,'Alarm request names exact independently authored saved source')
    case=client.tool('diagnostic_plan',dict(request=request));case_id=case['id'];alarm=case['plan']['alarmBindings']
    check([r['signalId'] for r in alarm['bindings']]==['enable','enableSource','final'],'Case resolves three independent alarm signal selections')
    symbol='"DB_Line".Conv2.Enable';state['symbols']={symbol:dict(name='Enable',datatype='bool',value=True)}
    selection=client.tool('runtime_web_browse',dict(attemptId=attempt,symbol=symbol,children=False))['selectionId']
    mapping=dict(caseId=case_id,mappings=[dict(selectionId=selection,signalId='enable',reason='Select only the saved condition operand')])
    capture_request=dict(attemptId=attempt,selectionIds=[selection],intervalMilliseconds=100,durationSeconds=5,maximumSamples=2,maximumBytes=1048576,reviewId=review_id,association=mapping)
    before=len(state['calls'])
    for field in ['nativeQualified','sourceAuthenticityVerified','runtimeAlarmAssociationVerified','coverageComplete']:
        forged=copy.deepcopy(case['plan']);forged['alarmBindings'][field]=True
        data=json.dumps(forged,ensure_ascii=False,separators=(',',':')).encode('utf-8');bad_id=hashlib.sha256(data).hexdigest()
        (output/'store/trace-diagnostics'/(bad_id+'.json')).write_bytes(data)
        refused=uuid.uuid4().hex
        capture_call(client,'start',dict(captureId=refused,request={**capture_request,'association':{**mapping,'caseId':bad_id}}),check,'RuntimeCaptureAssociationMismatch')
        check(not (output/'store/runtime-captures'/refused).exists(),'Altered '+field+' refuses without creating capture intent')
    check(len(state['calls'])==before,'Rehashed alarm source refusals perform no reads')
    capture_id=uuid.uuid4().hex
    capture_call(client,'start',dict(captureId=capture_id,request=capture_request),check)
    deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        status=capture_call(client,'status',dict(captureId=capture_id),check)
        if status['state']!='running':break
        time.sleep(.1)
    check(status['state']=='ended' and status['recordedProgress']['readAttempts']==2,'Single selected alarm operand completes its exact read budget')
    association=status['plan']['association']
    check(association['alarmBindings']==alarm and len(association['signals'])==1,'Capture retains all three original bindings while selecting one signal')
    before=len(state['calls'])
    analyzed=capture_call(client,'analyze',dict(snapshotRef=status['snapshotRef'],units=dict(enable='1')),check)
    report=analyzed['report']
    check(report['association']['alarmBindings']==alarm,'Analysis retains exact original alarm evidence')
    check({'enableSource','final'}.issubset(report['qualityAssessment']['uncapturedSignalIds']),'Unselected alarm signals remain uncaptured')
    check(all(h['verdict']=='unresolved' for h in report['hypotheses']),'Alarm case observations cannot qualify runtime assertions')
    review_path.unlink()
    capture_call(client,'analyze',dict(snapshotRef=status['snapshotRef'],units=dict(enable='1')),check,'ArtifactUnavailable')
    (output/'store/trace-diagnostics'/(case_id+'.json')).unlink()
    for format in ['json','markdown','html']:
        exported=client.tool('diagnostic_report_export',dict(reportId=analyzed['id'],format=format))
        validator('diagnostic-report-export').validate(exported)
        data=Path(exported['artifact']['path']).read_bytes()
        check(hashlib.sha256(data).hexdigest()==exported['artifact']['sha256'],'Alarm report '+format+' exports verified bytes after source removal')
        if format!='json':check(b'Selected for capture' in data and b'Not selected in this capture' in data,'Alarm report distinguishes selected and unselected bindings')
    repeated=capture_call(client,'start',dict(captureId=capture_id,request=capture_request),check)
    exported=capture_call(client,'export',dict(snapshotRef=status['snapshotRef']),check)
    data=Path(exported['artifacts'][0]['path']).read_bytes();document=json.loads(data);validator().validate(document)
    (output/'alarm-capture-export.json').write_bytes(data)
    (output/'alarm-capture-analysis.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    check(repeated['plan']['association']==association and document['capture']['plan']['association']==association,'Capture reopens and exports exact alarm associations after source removal')
    check(len(state['calls'])==before,'Analysis, source-removal refusals, report export and idempotent start never reacquire values')
    del state['symbols']
    return dict(captureId=capture_id,snapshotRef=status['snapshotRef'],state='ended',reviewId=review_id,association=association,reportId=analyzed['id'])
