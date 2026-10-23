"""Explicit saved-source mapping through diagnostic planning, Web capture and offline export."""
import hashlib
import json
import time
import uuid
from pathlib import Path
from runtime_capture_checks import capture_call, validator


def run_association_checks(client, attempt, state, output, check):
    fixture=Path(__file__).resolve().parents[1]/'fixtures/physical-runtime'
    raw=(fixture/'association-review.json').read_bytes();review_id=hashlib.sha256(raw).hexdigest()
    review_path=output/'store/project-reviews'/(review_id+'.json');review_path.parent.mkdir(parents=True,exist_ok=True);review_path.write_bytes(raw)
    request=json.loads((fixture/'association-request.json').read_text());check(request['reviewId']==review_id,'Diagnostic fixture hash matches exact saved review')
    request['hypotheses'].append(dict(id='captured',statement='Captured sequential pattern <script> remains unqualified.',prediction=dict(
        when=[dict(signalId='enable',op='eq',value=True)],expect=[dict(signalId='step',op='eq',value=65535,unit='count')])))
    case=client.tool('diagnostic_plan',dict(request=request));case_id=case['id']
    symbols={'"DB_Line".Conv2.Enable':dict(name='Enable',datatype='bool',value=True),
             '"DB_Line".Conv2.Step':dict(name='Step',datatype='uint',value=65535)}
    state['symbols']=symbols
    ids=[client.tool('runtime_web_browse',dict(attemptId=attempt,symbol=s,children=False))['selectionId'] for s in symbols]
    mapping=dict(caseId=case_id,mappings=[dict(selectionId=s,signalId=n,reason='Explicit synthetic conveyor '+n) for s,n in zip(ids,['enable','step'])],assertedProgramIdentity='Caller assertion only; synthetic loopback')
    capture_request=dict(attemptId=attempt,selectionIds=ids,intervalMilliseconds=100,durationSeconds=5,maximumSamples=4,maximumBytes=1048576,reviewId=review_id,association=mapping)
    before=len(state['calls'])
    bad={**mapping,'mappings':[dict(selectionId=ids[0],signalId='step',reason='Wrong type'),dict(selectionId=ids[1],signalId='enable',reason='Wrong type')]}
    refused=uuid.uuid4().hex
    capture_call(client,'start',dict(captureId=refused,request={**capture_request,'association':bad}),check,'RuntimeCaptureAssociationMismatch')
    check(len(state['calls'])==before and not (output/'store/runtime-captures'/refused).exists(),'Mismatched source/observed datatype refuses before intent or polling')
    capture_id=uuid.uuid4().hex
    capture_call(client,'start',dict(captureId=capture_id,request=capture_request),check)
    deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        status=capture_call(client,'status',dict(captureId=capture_id),check)
        if status['state']!='running':break
        time.sleep(.1)
    check(status['state']=='ended' and status['recordedProgress']['readAttempts']==4,'Mapped mixed Bool/UInt capture completes exact sample budget')
    association=status['plan']['association']
    check(association['selectedInstance']['memberPath']==['Conv2'] and all(not association[k] for k in ['addressMatchVerified','programMatchVerified','sourceAuthenticityVerified','nativeQualified']),'Exact source instance retained without promoting caller mapping')
    page=capture_call(client,'page',dict(snapshotRef=status['snapshotRef']),check)
    check([r['selectionId'] for r in page['rows'] if r['kind']=='read']==ids*2,'Sequential mixed-signal capture preserves explicit selection order')
    check([json.loads(r['readResultJson'])['value'] for r in page['rows'] if r['kind']=='read']==[True,65535,True,65535],'Mapped Bool and UInt observations retain their independent values')
    before=len(state['calls']);units=dict(enable='1',step='count')
    capture_call(client,'analyze',dict(snapshotRef=status['snapshotRef'],units={**units,'step':'s'}),check,'RuntimeCaptureAnalysisMismatch')
    analysis=capture_call(client,'analyze',dict(snapshotRef=status['snapshotRef'],units=units),check)
    report=analysis['report'];finding=next(h for h in report['hypotheses'] if h['id']=='captured')
    check(finding['matchedCycles']==2 and all(h['verdict']=='unresolved' for h in report['hypotheses']),'Observed cycle matches never promote sequential polling to passing assertions')
    check(report['qualityAssessment']['cyclesObserved']==2 and report['qualityAssessment']['missingValues']==18 and len(report['qualityAssessment']['uncapturedSignalIds'])==9,'Uncaptured case signals stay missing in each cycle')
    reads=finding['examples'][0]['reads'];check(reads['enable']['sequence']<reads['step']['sequence'] and 'requestStartedUtc' in reads['enable'],'Each cycle keeps separate per-read order and timing')
    review_path.unlink()
    check(capture_call(client,'analyze',dict(snapshotRef=status['snapshotRef'],units=units),check)['id']==analysis['id'],'Analysis reuses exact retained case without original review or new clock input')
    (output/'store/trace-diagnostics'/(case_id+'.json')).unlink()
    capture_call(client,'analyze',dict(snapshotRef=status['snapshotRef'],units=units),check,'ArtifactUnavailable')
    formats={}
    for format in ['json','markdown','html']:
        receipt=client.tool('diagnostic_report_export',dict(reportId=analysis['id'],format=format));validator('diagnostic-report-export').validate(receipt)
        data=Path(receipt['artifact']['path']).read_bytes();check(hashlib.sha256(data).hexdigest()==receipt['artifact']['sha256'],'Exported analysis '+format+' retains exact receipt hash after source removal')
        formats[format]=receipt
    (output/'capture-analysis-report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    (output/'capture-analysis-exports.json').write_text(json.dumps(dict(status='passed',reportExports=dict(capture=formats)),indent=2)+'\n',encoding='utf-8')
    check(len(state['calls'])==before,'Analysis, refusal and retained report export make no network calls')
    before=len(state['calls'])
    repeated=capture_call(client,'start',dict(captureId=capture_id,request=capture_request),check)
    exported=capture_call(client,'export',dict(snapshotRef=status['snapshotRef']),check)
    data=Path(exported['artifacts'][0]['path']).read_bytes();document=json.loads(data);validator().validate(document)
    (output/'associated-capture-export.json').write_bytes(data)
    check(repeated['plan']['association']==association and document['capture']['plan']['association']==association and len(state['calls'])==before,'Associated capture reopens/exports after source removal with no reread or network')
    del state['symbols']
    return dict(captureId=capture_id,snapshotRef=status['snapshotRef'],state='ended',reviewId=review_id,association=association,reportId=analysis['id'])
