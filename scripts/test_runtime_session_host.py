"""Real MCP + current-user DPAPI + disposable loopback TLS; no Siemens or OS trust installation."""
import argparse
import copy
import hashlib
import http.server
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import threading
import time
import uuid
from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from smoke_mcp import Client, ROOT
from runtime_capture_checks import run_capture_checks, run_capture_cancellation_check, start_interrupted_capture, capture_call, check_retained_captures, last_read
from runtime_capture_sequence_checks import SCENARIOS, verify as verify_capture_sequence, negative_controls as capture_sequence_controls
from runtime_handoff_capture_checks import run_handoff_capture_checks, prepare_handoff, retain_input
from test_runtime_handoff_workflow import record as verify_handoff_record


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host',required=True,type=Path)
    parser.add_argument('--tls',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args();host=args.host.resolve();tls=args.tls.resolve();out=args.output.resolve()
    assert not out.exists();out.mkdir(parents=True)
    pins=json.loads((tls/'pins.json').read_text());vault=uuid.uuid4().hex
    password='synthetic-password-'+uuid.uuid4().hex;token='synthetic-token-'+uuid.uuid4().hex
    state={'mode':'normal','rights':True,'drift':False,'calls':[],'requests':[],'loginCorrect':[],'connectionClose':[],'connectionIds':[],'logoutCount':0,'timeout':'PT90S'}
    report={'completed':False,'nativeExecuted':False,'actualTls':True,'actualDpapi':True,'checks':[],'shutdowns':[]}
    report['harnessSources']={name:hashlib.sha256((ROOT/'scripts'/name).read_bytes()).hexdigest() for name in
        ['test_runtime_session_host.py','runtime_handoff_capture_checks.py','runtime_capture_checks.py','runtime_capture_sequence_checks.py','runtime_association_checks.py','runtime_alarm_capture_checks.py','test_runtime_handoff_contracts.py','test_runtime_handoff_workflow.py','smoke_mcp.py']}
    clients=[];server=None
    registry=Registry()
    for name in ['runtime-web-session','physical-runtime','protected-credential']:
        schema=json.loads((ROOT/'contracts'/(name+'.v1.schema.json')).read_text(encoding='utf-8'))
        registry=registry.with_resource(schema['$id'],Resource.from_contents(schema))
    session_schema=json.loads((ROOT/'contracts/runtime-web-session.v2.schema.json').read_text())
    Draft202012Validator.check_schema(session_schema)
    session_validator=Draft202012Validator(session_schema,registry=registry)
    def check(ok,label):
        assert ok,label
        report['checks'].append(label)
    class Handler(http.server.BaseHTTPRequestHandler):
        def setup(self):
            super().setup();self.connection_id=uuid.uuid4().hex
        def log_message(self,*_):pass
        def handle(self):
            try:super().handle()
            except (ConnectionError,ssl.SSLError):pass  # Expected adversarial TLS rejection.
        def do_POST(self):
            try:
                raw=self.rfile.read(int(self.headers.get('Content-Length','0')));body=json.loads(raw)
                method=body['method'];state['calls'].append(method);state['connectionIds'].append(self.connection_id)
                state['requests'].append(dict(method=method,id=body['id'],tlsSessionReused=self.connection.session_reused))
                state['connectionClose'].append(self.headers.get('Connection','').lower()=='close')
                assert self.path=='/api/jsonrpc'
                error=None
                if method=='Api.Browse':
                    if state['mode']=='redirect':
                        self.send_response(302);self.send_header('Location','https://127.0.0.1:1/forbidden');self.end_headers();return
                    value=[{'name':n} for n in ['Api.Login','Api.Logout','Api.GetPermissions','PlcProgram.Browse','PlcProgram.Read']]
                    if state.get('profiles',True):value += [{'name':n} for n in ['Api.Version','Api.GetQuantityStructures','Api.GetAuthenticationMode','Api.GetSessionInfo']]
                    if state['mode']=='largeMethods':value += [{'name':str(i)+'x'*120} for i in range(600)]
                elif method=='Api.Version':value=state.get('version',3.28)
                elif method=='Api.GetQuantityStructures':
                    value=state.get('limits',{'webapi_max_http_request_body_size':131072,'webapi_max_parallel_requests':4,'webapi_max_parallel_user_sessions':200})
                elif method=='Api.GetAuthenticationMode':value=state.get('authentication',{'authentication_modes':['local','umc']})
                elif method=='Api.Login':
                    state['loginCorrect'].append(body['params']=={'user':state.get('user','reader'),'mode':state.get('loginMode','local'),'password':password} and self.headers.get('X-Auth-Token') is None)
                    value={'token':token,'runtime_timeout':state['timeout'],**state.get('loginExtra',{})}
                    if state['mode']=='loginDenied':error={'code':100,'message':password,'data':token}
                elif self.headers.get('X-Auth-Token')!=token:
                    error={'code':2,'message':'bad token'};value=None
                elif method=='Api.GetSessionInfo':
                    if state.get('sessionInfoGate'):
                        entered,released=state['sessionInfoGate'];entered.set()
                        if not released.wait(15):return
                    value=state.get('sessionInfo',{'username':'reader','authentication_mode':'local','runtime_timeout':state['timeout']})
                    if state['mode']=='sessionDenied':error={'code':6,'message':password,'data':token}
                elif method=='Api.GetPermissions':value=[{'name':'read_value'}] if state['rights'] else []
                elif method=='PlcProgram.Browse':
                    symbol=state.get('symbols',{}).get(body['params'].get('var'))
                    value=[{k:symbol[k] for k in ['name','datatype']}] if symbol else [{'name':'Counter','datatype':'lint' if state['drift'] else 'ulint'}]
                elif method=='PlcProgram.Read':
                    if state.get('readGate'):
                        entered,released=state['readGate'];entered.set()
                        if not released.wait(15):return
                    if state['mode']=='lostRead':
                        self.send_response(200);self.send_header('Content-Length','100');self.end_headers();self.wfile.write(b'{');self.wfile.flush();self.close_connection=True;return
                    if state['mode']=='slowRead':time.sleep(11)
                    if state.get('readDelay'):time.sleep(state['readDelay'])
                    value=[0,255,17] if body['params']['mode']=='raw' else state.get('symbols',{}).get(body['params']['var'],{}).get('value',18446744073709551615)
                    if state['mode']=='providerDenied':error={'code':2,'message':password,'data':token}
                    if state['mode']=='echoToken':value=token
                elif method=='Api.Logout':state['logoutCount']+=1;value=True
                else:raise AssertionError('Unexpected RPC '+method)
                if (method=='Api.Login' and state['mode']=='lostLogin') or (method=='Api.Logout' and state['mode']=='lostLogout'):
                    self.send_response(200);self.send_header('Content-Length','100');self.end_headers();self.wfile.write(b'{');self.wfile.flush();self.close_connection=True;return
                result={'jsonrpc':'2.0','id':body['id'],**({'error':error} if error else {'result':value})}
                if method=='PlcProgram.Read' and state['mode']=='wrongId':result['id']='f'*32
                data=json.dumps(result).encode()
                if method=='PlcProgram.Read' and state['mode']=='oversize':data=b' '*300000
                if method=='Api.Version' and state['mode']=='longVersion':data=data.replace(b'3.28',b'3.'+b'0'*180000+b'1')
                self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
            except (ConnectionError,ssl.SSLError):pass
    class KeepAliveHandler(Handler):
        protocol_version='HTTP/1.1'
        def parse_request(self):
            accepted=super().parse_request()
            if accepted:self.close_connection=False  # Deliberately ignore the client's close request.
            return accepted
    def start_server(name='server',keep_alive=False):
        srv=http.server.ThreadingHTTPServer(('127.0.0.1',0),KeepAliveHandler if keep_alive else Handler)
        context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);context.load_cert_chain(str(tls/(name+'.crt')),str(tls/(name+'.key')))
        srv.socket=context.wrap_socket(srv.socket,server_side=True)
        threading.Thread(target=srv.serve_forever,daemon=True).start();return srv
    def client(artifact_root=None):
        c=Client(host,35,['--no-native-workers','--artifacts',str(artifact_root or out/'store')]);clients.append(c)
        c.request('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'runtime-local-tls','version':'1'}});c.notify('notifications/initialized');return c
    def call(c,name,arguments,code=None):
        r=c.request('tools/call',{'name':'runtime_web_'+name,'arguments':arguments})
        text=json.dumps(r);check(password not in text and token not in text,'MCP response redacted: '+name)
        if code:
            check(r.get('isError') and r['structuredContent']['error']['code']==code,'Refuses '+code);return r['structuredContent']
        check(not r.get('isError'),'Successful tool '+name+': '+str(r)[:160])
        value=r['structuredContent']
        if value.get('schemaVersion') in ['runtime-web-session.v1','runtime-web-session.v2']:report.setdefault('sessionObservations',[]).append(dict(tool=name,status=value))
        if value.get('schemaVersion')=='runtime-web-session.v2':session_validator.validate(value)
        elif value.get('schemaVersion')=='runtime-web-session.v1':
            Draft202012Validator(json.loads((ROOT/'contracts/runtime-web-session.v1.schema.json').read_text()),registry=registry).validate(value)
        return value
    def credential(c,endpoint,pin,user='reader',mode='local'):
        binding=call(c,'credential_binding',dict(endpoint=endpoint,peerCertificateSha256=pin,user=user,mode=mode))
        path=out/(uuid.uuid4().hex+'-binding.json');path.write_text(json.dumps(binding))
        p=subprocess.run([str(host),'--credential-store','create','--vault-name',vault,'--binding',str(path),'--stdin'],input=json.dumps(password).encode(),capture_output=True,timeout=30)
        check(p.returncode==0 and password.encode() not in p.stdout+p.stderr,'Protected credential creation')
        ref=json.loads(p.stdout)['reference'];refpath=path.with_name(path.stem+'-reference.json');refpath.write_text(json.dumps(ref))
        return dict(endpoint=endpoint,peerCertificateSha256=pin,user=user,mode=mode,vaultName=vault,credential=ref),path,refpath
    def connect(c,login):
        attempt=uuid.uuid4().hex;return attempt,call(c,'connect',dict(attemptId=attempt,login=login))
    def abandon(c,attempt):return call(c,'disconnect',dict(attemptId=attempt,acknowledgeUnknown=True))
    try:
        server=start_server();endpoint='https://127.0.0.1:'+str(server.server_port);c=client()
        tools=c.request('tools/list')['tools'];check(len([t for t in tools if t['name'].startswith('runtime_web_')])==6,'Six runtime tools registered')
        alias_root=out/'owner-alias';(alias_root/'runtime-web').mkdir(parents=True)
        marker=out/'owner-target.txt';marker.write_text('retained-owner-marker')
        os.link(marker,alias_root/'runtime-web/owner.lock');other=client(alias_root)
        rejected=other.request('tools/call',{'name':'runtime_web_credential_binding','arguments':dict(endpoint=endpoint,peerCertificateSha256=pins['server'],user='reader')})
        check(rejected.get('isError') and marker.read_text()=='retained-owner-marker','Aliased owner lock is refused without changing its target')
        login,binding_path,reference_path=credential(c,endpoint,pins['server'])
        attempt,connected=connect(c,login);check(connected['state']=='connected' and connected['peerVerified'],'Actual pinned TLS login')
        check(connected['profile']['apiVersion']==3.28 and connected['profile']['quantityProfile']=='siemens-v21-table-max' and connected['profile']['sessionUser']=='reader','Observed profile has numeric API version, exact limit profile and verified session user')
        check(not connected['deviceIdentityVerified'] and not connected['programMatchVerified'] and not connected['nativeQualified'],'No manufactured physical qualification')
        for field in ['deviceIdentityVerified','programMatchVerified','nativeQualified','secretReturned']:
            bad=copy.deepcopy(connected);bad[field]=True;check(not session_validator.is_valid(bad),'Session schema refuses '+field)
        count=len(state['calls']);check(call(c,'connect',dict(attemptId=attempt,login=login))==connected and len(state['calls'])==count,'Duplicate attempt never logs in again')
        altered=copy.deepcopy(login);altered['user']='other'
        call(c,'connect',dict(attemptId=attempt,login=altered),code='RuntimeCredentialContextMismatch')
        call(c,'browse',dict(attemptId=attempt))
        selected=call(c,'browse',dict(attemptId=attempt,symbol='"MyDB".Counter',children=False));sid=selected['selectionId']
        readargs=dict(attemptId=attempt,selectionId=sid)
        value=call(c,'read',readargs);check(value['value']==2**64-1 and value['sourceTimestamp'] is None,'Exact UInt64 with truthful clock scope')
        check(call(c,'read',{**readargs,'representation':'raw'})['value']==[0,255,17],'Raw byte read')
        retained_captures=run_capture_checks(c,attempt,sid,state,out,check)
        # Every deliberate cancellation gets one explicitly requested fresh session.
        # Cancellation may invalidate identity verification; no scenario resumes it.
        sequence={'nativeQualified':False,'scenarios':[]}
        sequence['ordinarySessionClosed']=call(c,'disconnect',dict(attemptId=attempt))
        check(sequence['ordinarySessionClosed']['state']=='closed','Close ordinary capture session before deliberate cancellation scenarios')
        sequence['ordinaryCloseRpcEnd']=len(state['calls'])
        report['captureCancellationSequence']=sequence
        for scenario in SCENARIOS:
            bounds=[len(state['calls'])]
            attempt,connected=connect(c,login);bounds.append(len(state['calls']))
            check(connected['state']=='connected','Explicit fresh session for cancellation scenario: '+scenario)
            selected=call(c,'browse',dict(attemptId=attempt,symbol='"MyDB".Counter',children=False));bounds.append(len(state['calls']))
            captured=run_capture_cancellation_check(c,attempt,selected['selectionId'],state,out,check,scenario)
            observed=call(c,'status',dict(attemptId=attempt));bounds.append(len(state['calls']))
            closed=call(c,'disconnect',dict(attemptId=attempt));bounds.append(len(state['calls']))
            check(closed['state']=='closed','Explicit close after cancellation scenario: '+scenario)
            sequence['scenarios'].append(dict(scenario=scenario,connected=connected,selection=selected,observed=observed,
                                              closed=closed,rpcBoundaries=bounds,capture=captured))
            retained_captures.append(dict(captureId=captured['status']['captureId'],snapshotRef=captured['status']['snapshotRef'],
                                         state=captured['status']['state'],reviewId=None))
            (out/'capture-cancellation-scenarios.json').write_text(json.dumps(sequence,indent=2)+'\n')
        verify_capture_sequence(sequence,state['calls'])
        sequence['negativeControls']=capture_sequence_controls(sequence,state['calls'])
        check(len(sequence['negativeControls'])==7,'Independent original-export oracle refuses seven session-sequencing mutations')
        (out/'capture-cancellation-scenarios.json').write_text(json.dumps(sequence,indent=2)+'\n')
        (out/'capture-retention-cases.json').write_text(json.dumps(retained_captures,indent=2)+'\n')
        attempt,_=connect(c,login)
        retained_captures.extend(run_handoff_capture_checks(c,attempt,state,out,check))
        call(c,'disconnect',dict(attemptId=attempt))
        attempt,_=connect(c,login)
        # Bound acquisition must retain uncertainty across transport loss, owner exit and migration.
        lifecycle={'completed':False,'nativeExecuted':False,'captures':[],'migrations':[]}
        def bound_request(selected_attempt):
            _,packet,handoff,_=prepare_handoff(c,selected_attempt,out,check)
            selection=call(c,'browse',dict(attemptId=selected_attempt,symbol='"MyDB".Counter',children=False))
            return dict(attemptId=selected_attempt,selectionIds=[selection['selectionId']],intervalMilliseconds=2000,
                durationSeconds=30,maximumSamples=10,maximumBytes=1048576,reviewId=packet['reviewId'],handoffRef=handoff['recordRef']),packet
        def capture_status(identity,minimum=None):
            deadline=time.monotonic()+15
            while time.monotonic()<deadline:
                observed=capture_call(c,'status',dict(captureId=identity),check)
                if (minimum is None and observed['state']!='running') or (minimum is not None and observed['recordedProgress']['readAttempts']>=minimum):return observed
                time.sleep(.05)
            raise AssertionError('Bound lifecycle capture did not reach its required state')
        def begin_bound(request):
            identity=uuid.uuid4().hex;capture_call(c,'start',dict(captureId=identity,request=request),check)
            return identity
        def retain_bound(label,request,status):
            exported=capture_call(c,'export',dict(snapshotRef=status['snapshotRef']),check)
            original=json.loads(Path(exported['artifacts'][0]['path']).read_bytes())
            check(original['capture']['plan']['handoff']==status['plan']['handoff'],'Lifecycle export retains exact handoff: '+label)
            before=len(state['calls'])
            repeated=capture_call(c,'start',dict(captureId=status['captureId'],request=request),check)
            check(repeated['snapshotRef']==status['snapshotRef'] and len(state['calls'])==before,'Lifecycle repeated start does not replay acquisition: '+label)
            retained=dict(captureId=status['captureId'],snapshotRef=status['snapshotRef'],state=status['state'],reviewId=request['reviewId'],handoff=status['plan']['handoff'])
            if status['state']=='interrupted':retained['uncommittedTailPresent']=status['uncommittedTailPresent']
            retained_captures.append(retained)
            lifecycle['captures'].append(dict(scenario=label,request=request,status=status,export=exported['artifacts'][0]))
        def migration(label,old_request,old_packet,new_request,new_packet):
            changed=copy.deepcopy(old_packet);changed['current']=copy.deepcopy(new_packet['current'])
            review=retain_input(c,'runtime_handoff_review',changed)
            verify_handoff_record(out/'store',review['recordRef'])
            check(review['summary']['state']=='requiresFreshEvidence' and review['operationAdmitted'] is False,'Migration retains old/new evidence and requires fresh context: '+label)
            before=len(state['calls']);identity=uuid.uuid4().hex
            capture_call(c,'start',dict(captureId=identity,request={**new_request,'handoffRef':old_request['handoffRef']}),check,'RuntimeCaptureHandoffMismatch')
            check(len(state['calls'])==before and not (out/'store/runtime-captures'/identity).exists(),'Old handoff refuses before intent or network after migration: '+label)
            short={**new_request,'maximumSamples':1};identity=begin_bound(short);status=capture_status(identity)
            page=capture_call(c,'page',dict(snapshotRef=status['snapshotRef']),check)
            check(page['rows'][0]['errorCode'] is None and not status['plan']['handoff']['programMatchVerified'],'Fresh migration context permits only an unqualified read: '+label)
            retain_bound(label,short,status)
            lifecycle['migrations'].append(dict(kind=label,oldHandoffRef=old_request['handoffRef'],newHandoffRef=new_request['handoffRef'],comparisonRef=review['recordRef']))
        lost_request,_=bound_request(attempt);lost_request['maximumSamples']=1
        state['mode']='lostRead';before=len(state['requests']);identity=begin_bound(lost_request);lost=capture_status(identity)
        page=capture_call(c,'page',dict(snapshotRef=lost['snapshotRef']),check)
        reads=[r for r in state['requests'][before:] if r['method']=='PlcProgram.Read']
        check(len(reads)==1 and page['rows'][0]['readResultJson'] is None and page['rows'][0]['errorCode']=='RuntimeTransportFailure','Lost bound read response records uncertainty without replay or a stale value')
        lifecycle['lostReadRpcRange']=[before,len(state['requests'])]
        state['mode']='normal';retain_bound('lostReadResponse',lost_request,lost)
        call(c,'disconnect',dict(attemptId=attempt));attempt,_=connect(c,login)
        crash_request,_=bound_request(attempt);identity=begin_bound(crash_request);prefix=capture_status(identity,2)
        entered,released=threading.Event(),threading.Event();state['readGate']=(entered,released)
        try:
            check(entered.wait(10),'Bound read is dispatched and held before Host crash')
            before=len(state['calls']);old_pid=c.process.pid
            c.process.kill();c.process.wait(timeout=10);exit_receipt=c.close();report['shutdowns'].append(exit_receipt);clients.remove(c);c=client()
            interrupted=capture_status(identity)
            check(interrupted['state']=='interrupted' and interrupted['uncommittedDataUnknown'] and interrupted['snapshotRef']==prefix['snapshotRef'],'In-flight Host crash preserves exact committed bound prefix and unknown loss')
            retain_bound('inflightHostCrash',crash_request,interrupted)
            call(c,'connect',dict(attemptId=attempt,login=login))
            fresh=uuid.uuid4().hex
            capture_call(c,'start',dict(captureId=fresh,request=crash_request),check,'RuntimeSessionUnavailable')
            check(len(state['calls'])==before and not (out/'store/runtime-captures'/fresh).exists(),'New Host neither replays login nor adopts the interrupted bound acquisition')
            lifecycle['crash']=dict(processId=old_pid,exit=exit_receipt,inFlightRequest=state['requests'][-1],committedSnapshotRef=prefix['snapshotRef'])
        finally:released.set();state.pop('readGate',None)
        abandon(c,attempt)
        rotated_login,rotation_binding,rotation_reference=credential(c,endpoint,pins['server']);attempt,_=connect(c,rotated_login)
        old_request,old_packet=bound_request(attempt);identity=begin_bound(old_request);capture_status(identity,2)
        before=len(state['calls'])
        retired=subprocess.run([str(host),'--credential-store','retire','--vault-name',vault,'--binding',str(rotation_binding),'--reference',str(rotation_reference)],capture_output=True,timeout=30)
        check(retired.returncode==0,'Retire the credential of an active bound capture')
        stopped=capture_status(identity);page=capture_call(c,'page',dict(snapshotRef=stopped['snapshotRef']),check)
        check(last_read(page['rows'])['errorCode']=='CredentialRetired' and len(state['calls'])==before,'Retired bound credential stops acquisition before any further RPC')
        lifecycle['retiredCredentialRpcRange']=[before,len(state['requests'])]
        retain_bound('credentialRetired',old_request,stopped);call(c,'disconnect',dict(attemptId=attempt))
        renewed,_,_=credential(c,endpoint,pins['server']);attempt,_=connect(c,renewed)
        new_request,new_packet=bound_request(attempt);migration('credentialReplacement',old_request,old_packet,new_request,new_packet)
        old_request,old_packet=new_request,new_packet;identity=begin_bound(old_request);capture_status(identity,2)
        before=len(state['calls'])
        server.socket.context.load_cert_chain(str(tls/'rotated.crt'),str(tls/'rotated.key'))
        try:
            probe_context=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT);probe_context.check_hostname=False;probe_context.verify_mode=ssl.CERT_NONE
            with socket.create_connection(('127.0.0.1',server.server_port),timeout=5) as plain:
                with probe_context.wrap_socket(plain,server_hostname='localhost') as peer:
                    observed_pin=hashlib.sha256(peer.getpeercert(binary_form=True)).hexdigest()
                    check(observed_pin==pins['rotated'] and not peer.session_reused,'Independent full TLS handshake observes the rotated certificate at the same endpoint')
                    lifecycle['certificateProbe']=dict(peerCertificateSha256=observed_pin,tlsVersion=peer.version(),sessionReused=peer.session_reused)
                    (out/'certificate-rotation-probe.json').write_text(json.dumps(lifecycle['certificateProbe'],indent=2)+'\n')
            stopped=capture_status(identity)
            check(stopped['recordedProgress']['terminalReason']=='sessionUnavailable' and len(state['calls'])==before,'Same-endpoint certificate rotation stops bound acquisition before HTTP dispatch')
            lifecycle['rotatedCertificateRpcRange']=[before,len(state['requests'])]
            retain_bound('certificateRotated',old_request,stopped)
            call(c,'disconnect',dict(attemptId=attempt));abandon(c,attempt)
            pinned,_,_=credential(c,endpoint,pins['rotated']);attempt,_=connect(c,pinned)
            new_request,new_packet=bound_request(attempt);migration('certificateReplacement',old_request,old_packet,new_request,new_packet)
            call(c,'disconnect',dict(attemptId=attempt))
        finally:server.socket.context.load_cert_chain(str(tls/'server.crt'),str(tls/'server.key'))
        before=len(state['calls']);capture_call(c,'start',dict(captureId=identity,request=old_request),check)
        check(len(state['calls'])==before,'Restoring the old certificate does not resume its ended capture')
        state['user']='other-reader';state['sessionInfo']={'username':'other-reader','authentication_mode':'local'}
        try:
            account,_,_=credential(c,endpoint,pins['server'],user='other-reader');attempt,_=connect(c,account)
            account_request,account_packet=bound_request(attempt);migration('accountReplacement',old_request,old_packet,account_request,account_packet)
            call(c,'disconnect',dict(attemptId=attempt))
        finally:del state['user'];del state['sessionInfo']
        lifecycle['completed']=True
        (out/'handoff-lifecycle.json').write_text(json.dumps(lifecycle,indent=2)+'\n',encoding='utf-8')
        attempt,_=connect(c,login)
        selected=call(c,'browse',dict(attemptId=attempt,symbol='"MyDB".Counter',children=False));sid=selected['selectionId']
        readargs=dict(attemptId=attempt,selectionId=sid)
        bound=next(case for case in retained_captures if 'handoff' in case)
        refused=uuid.uuid4().hex;before=len(state['calls'])
        capture_call(c,'start',dict(captureId=refused,request=dict(attemptId=attempt,selectionIds=[sid],intervalMilliseconds=100,durationSeconds=1,
            reviewId=bound['reviewId'],handoffRef=bound['handoff']['recordRef'])),check,'RuntimeCaptureHandoffMismatch')
        check(len(state['calls'])==before and not (out/'store/runtime-captures'/refused).exists(),'Reconnected session cannot reuse an earlier handoff generation or create capture intent')
        retained_path=out/'store/runtime-web'/(attempt+'.json');retained_bytes=retained_path.read_bytes()
        changed=json.loads(retained_bytes);changed['state']='disconnecting';retained_path.write_text(json.dumps(changed))
        count=len(state['calls']);call(c,'read',readargs,code='RuntimeRecordChanged');check(len(state['calls'])==count,'Changed local ownership record refuses before network')
        retained_path.write_bytes(retained_bytes)
        call(c,'read',{**readargs,'selectionId':'0'*32},code='RuntimeSelectionUnavailable')
        state['rights']=False;before=state['calls'].count('PlcProgram.Read');call(c,'read',readargs,code='RuntimePermissionDenied');check(state['calls'].count('PlcProgram.Read')==before,'Denied permissions never dispatch read');state['rights']=True
        state['drift']=True;call(c,'read',readargs,code='RuntimeSymbolMetadataChanged');state['drift']=False
        state['mode']='providerDenied';failed=call(c,'read',readargs);check(failed['status']=='permissionDenied' and failed['value'] is None,'Remote failure has no stale good value or remote text')
        state['mode']='wrongId';call(c,'read',readargs,code='RuntimeResponseInvalid')
        state['mode']='oversize';call(c,'read',readargs,code='RuntimeResponseLimit')
        state['mode']='echoToken';call(c,'read',readargs,code='RuntimeSecretEchoRejected')
        state['mode']='slowRead';started=time.monotonic();call(c,'read',readargs,code='RuntimeTimeout');check(time.monotonic()-started<20,'Ten-second request deadline bounds a stalled endpoint')
        state['mode']='normal';check(call(c,'disconnect',dict(attemptId=attempt))['state']=='closed','Owned logout verified')
        count=len(state['calls']);call(c,'disconnect',dict(attemptId=attempt));check(len(state['calls'])==count,'Repeated disconnect does not repeat logout')
        call(c,'read',readargs,code='RuntimeSessionUnavailable')
        persistent=start_server(keep_alive=True)
        try:
            persistent_login,_,_=credential(c,'https://127.0.0.1:'+str(persistent.server_port),pins['server'])
            first=len(state['connectionIds']);persistent_attempt,persistent_status=connect(c,persistent_login)
            check(persistent_status['state']=='connected','Persistent peer connection fixture connects')
            persistent_selection=call(c,'browse',dict(attemptId=persistent_attempt,symbol='"MyDB".Counter',children=False))
            call(c,'read',dict(attemptId=persistent_attempt,selectionId=persistent_selection['selectionId']))
            call(c,'disconnect',dict(attemptId=persistent_attempt))
            observed=state['connectionIds'][first:]
            check(len(observed)>=10 and len(observed)==len(set(observed)),'Every RPC uses a fresh connection even when the peer ignores Connection close')
        finally:persistent.shutdown();persistent.server_close()

        for mode in ['redirect','largeMethods','longVersion','loginDenied','lostLogin','lostLogout']:
            state['mode']=mode;before_login=state['calls'].count('Api.Login');attempt,status=connect(c,login)
            if mode=='lostLogout':status=call(c,'disconnect',dict(attemptId=attempt))
            expected='notConnected' if mode in ['redirect','largeMethods','longVersion','loginDenied'] else 'remoteSessionUnknown'
            check(status['state'] in [expected,'hostEndedRemoteUnknown'],mode+' remains explicit')
            if mode=='longVersion':check(status['errorCode']=='RuntimeProfileInvalid' and state['calls'].count('Api.Login')==before_login,'Oversized numeric version refuses before secret dispatch')
            count=len(state['calls']);call(c,'connect',dict(attemptId=attempt,login=login));check(len(state['calls'])==count,mode+' retry is observation only')
            if mode not in ['redirect','largeMethods','longVersion','loginDenied']:
                call(c,'connect',dict(attemptId=uuid.uuid4().hex,login=login),code='RuntimeEndpointSessionUnresolved')
                check(abandon(c,attempt)['state']=='abandonedRemoteUnknown','Explicit release preserves unknown remote outcome')
        state['mode']='normal'
        # Independent method fixtures exercise pre-secret refusal and post-login ownership separately.
        prelogin=[
            ('version','3.28','RuntimeProfileInvalid'),('version',-1,'RuntimeProfileInvalid'),
            ('limits',{'webapi_http_request_body_size':131072,'webapi_parallel_requests':4,'webapi_parallel_user_sessions':200},'RuntimeQuantityProfileUnsupported'),
            ('limits',{'webapi_max_http_request_body_size':0,'webapi_max_parallel_requests':4,'webapi_max_parallel_user_sessions':200},'RuntimeQuantityProfileUnsupported'),
            ('limits',{'webapi_max_http_request_body_size':16,'webapi_max_parallel_requests':4,'webapi_max_parallel_user_sessions':200},'RuntimeRequestLimit'),
            ('authentication',{'authentication_modes':['umc']},'RuntimeAuthenticationModeUnsupported'),
            ('authentication',{'authentication_modes':['local','local']},'RuntimeProfileInvalid'),
            ('authentication',{'authentication_mode':['local']},'RuntimeProfileInvalid')]
        for key,value,code in prelogin:
            state[key]=value;before=state['calls'].count('Api.Login');_,status=connect(c,login)
            check(status['state']=='notConnected' and status['errorCode']==code,'Profile refuses '+key+': '+code)
            check(state['calls'].count('Api.Login')==before,'Invalid discovery never dispatches password')
            del state[key]
        original_password=password;password='synthetic-long-'+('x'*600)
        constrained,_,_=credential(c,endpoint,pins['server'])
        state['limits']={'webapi_max_http_request_body_size':512,'webapi_max_parallel_requests':1,'webapi_max_parallel_user_sessions':1}
        before=state['calls'].count('Api.Login');_,status=connect(c,constrained)
        check(status['state']=='notConnected' and status['errorCode']=='RuntimeRequestLimit' and state['calls'].count('Api.Login')==before,'Discovered request limit rejects oversized password before login dispatch')
        password=original_password;del state['limits']
        invalid_sessions=[{'username':'other','authentication_mode':'local'}, {'username':'reader','authentication_mode':'umc'}, {'username':'reader'}]
        for info in invalid_sessions:
            state['sessionInfo']=info;attempt,status=connect(c,login)
            check(status['state']=='remoteSessionUnknown' and status['errorCode']=='RuntimeSessionIdentityMismatch','Session identity mismatch retains owned token for logout')
            check(call(c,'disconnect',dict(attemptId=attempt))['state']=='closed','Mismatch cleanup uses owned logout')
        del state['sessionInfo']
        for extra in [{'runtime_timeout':'invalid'}, {'has_no_permissions':'true'}, {'password_expiration':{'timestamp':'2026-10-17T12:00:00+02:00','warning':True}}]:
            state['loginExtra']=extra;attempt,status=connect(c,login)
            check(status['state']=='remoteSessionUnknown' and status['errorCode']=='RuntimeProfileInvalid','Invalid login policy retains unknown outcome')
            check(call(c,'disconnect',dict(attemptId=attempt))['state']=='closed','Malformed login policy still permits owned cleanup')
        state['loginExtra']={'has_no_permissions':True,'password_expiration':{'timestamp':'2030-01-01T12:00:00Z','warning':True}}
        attempt,status=connect(c,login)
        check(status['readAvailability']=='permissionDenied' and status['profile']['passwordExpirationWarning'] is True,'Login policy is visible and does not claim read permission')
        selection=call(c,'browse',dict(attemptId=attempt,symbol='"MyDB".Counter',children=False))
        before=state['calls'].count('PlcProgram.Read');call(c,'read',dict(attemptId=attempt,selectionId=selection['selectionId']),code='RuntimePermissionDenied')
        check(state['calls'].count('PlcProgram.Read')==before,'Login no-permissions flag overrides contradictory permission list')
        call(c,'disconnect',dict(attemptId=attempt));del state['loginExtra']
        state['authentication']={'authentication_modes':['umc']};state['user']='Anonymous';state['sessionInfo']={'username':'Anonymous'}
        anonymous,_,_=credential(c,endpoint,pins['server'],user='Anonymous');attempt,status=connect(c,anonymous)
        check(status['state']=='connected' and status['profile']['sessionAuthenticationMode'] is None,'Documented Anonymous exception preserves explicit local login')
        call(c,'disconnect',dict(attemptId=attempt))
        for key in ['authentication','user','sessionInfo']:del state[key]
        for mode,user in [('static','Everybody'),('disabled','Anonymous'),('umc','reader')]:
            state['authentication']={'authentication_modes':[mode]};state['user']=user;state['loginMode']=mode
            state['sessionInfo']={'username':user,**({'authentication_mode':mode} if user=='reader' else {})}
            selected_login,_,_=credential(c,endpoint,pins['server'],user=user,mode=mode);attempt,status=connect(c,selected_login)
            check(status['state']=='connected' and status['profile']['sessionUser']==user,'Explicit '+mode+' mode retains selected identity')
            call(c,'disconnect',dict(attemptId=attempt))
        for key in ['authentication','user','loginMode','sessionInfo']:del state[key]
        state['profiles']=False;attempt,status=connect(c,login)
        check(status['state']=='connected' and status['profile']['apiVersion'] is None and not status['profile']['sessionInfoObserved'],'Older method surface stays explicitly unobserved')
        call(c,'disconnect',dict(attemptId=attempt));state['profiles']=True
        for mode in ['mismatch','sessionDenied','echo']:
            attempt,status=connect(c,login);selection=call(c,'browse',dict(attemptId=attempt,symbol='"MyDB".Counter',children=False))
            state['sessionInfo']={'username':token if mode=='echo' else 'other','authentication_mode':'local'};state['mode']=mode
            before=state['calls'].count('PlcProgram.Read')
            code={'mismatch':'RuntimeSessionIdentityMismatch','sessionDenied':'RuntimeProviderRejected','echo':'RuntimeSecretEchoRejected'}[mode]
            call(c,'read',dict(attemptId=attempt,selectionId=selection['selectionId']),code=code)
            check(state['calls'].count('PlcProgram.Read')==before,'Changed session refuses before value dispatch')
            check(call(c,'status',dict(attemptId=attempt))['state']=='remoteSessionUnknown','Failed identity recheck is retained')
            state['mode']='normal';del state['sessionInfo'];count=len(state['calls'])
            call(c,'browse',dict(attemptId=attempt),code='RuntimeSessionUnavailable');check(len(state['calls'])==count,'Invalidated session cannot silently resume')
            call(c,'disconnect',dict(attemptId=attempt))
        attempt,status=connect(c,login);check(status['state']=='connected','Explicit fresh attempt after release')
        legacy_id=uuid.uuid4().hex;legacy=copy.deepcopy(status);legacy.pop('profile');legacy['schemaVersion']='runtime-web-session.v1';legacy['attemptId']=legacy_id;legacy['context']['connectionId']=legacy_id
        (out/'store/runtime-web'/(legacy_id+'.json')).write_text(json.dumps(legacy))
        crash_selection=call(c,'browse',dict(attemptId=attempt,symbol='"MyDB".Counter',children=False))
        crash_id,crash_ref=start_interrupted_capture(c,attempt,crash_selection['selectionId'],check)
        c.process.kill();c.process.wait(timeout=10);report['shutdowns'].append(c.close());clients.remove(c);c=client()
        count=len(state['calls'])
        crash_status=capture_call(c,'status',dict(captureId=crash_id),check)
        check(crash_status['state']=='interrupted' and crash_status['uncommittedDataUnknown'],'Killed Host reopens committed capture with unknown loss')
        capture_call(c,'page',dict(snapshotRef=crash_ref),check)
        capture_call(c,'export',dict(snapshotRef=crash_ref),check)
        check(len(state['calls'])==count,'Capture restart does not reconnect or resume')
        check_retained_captures(c,retained_captures,state,check)
        count=len(state['calls']);old=call(c,'status',dict(attemptId=legacy_id));check(old['schemaVersion']=='runtime-web-session.v1' and 'profile' not in old and old['state']=='hostEndedRemoteUnknown' and len(state['calls'])==count,'V1 retained record reopens without network or invented profile')
        abandon(c,legacy_id)
        original=json.loads((ROOT/'fixtures/physical-runtime/session-v1-closed.json').read_text());original_path=out/'store/runtime-web'/(original['attemptId']+'.json')
        original_path.write_bytes((ROOT/'fixtures/physical-runtime/session-v1-closed.json').read_bytes())
        count=len(state['calls']);check(call(c,'status',dict(attemptId=original['attemptId']))==original and len(state['calls'])==count,'Actual previous-build v1 record reopens unchanged without network')
        count=len(state['calls']);check(call(c,'status',dict(attemptId=attempt))['state']=='hostEndedRemoteUnknown','Actual Host restart preserves unknown remote session')
        call(c,'connect',dict(attemptId=attempt,login=login));check(len(state['calls'])==count,'Restart never replays login')
        abandon(c,attempt)
        state['timeout']='PT1S';attempt,status=connect(c,login);check(status['state']=='connected','Short idle session connected')
        deadline=time.monotonic()+10
        while time.monotonic()<deadline:
            status=call(c,'status',dict(attemptId=attempt))
            if status['state']=='closed':break
            time.sleep(.25)
        check(status['state']=='closed','Idle timer owns bounded logout');state['timeout']='PT90S'
        attempt,status=connect(c,login)
        selected=call(c,'browse',dict(attemptId=attempt,symbol='"MyDB".Counter',children=False))
        p=subprocess.run([str(host),'--credential-store','retire','--vault-name',vault,'--binding',str(binding_path),'--reference',str(reference_path)],capture_output=True,timeout=30)
        check(p.returncode==0,'Retire connected credential')
        call(c,'read',dict(attemptId=attempt,selectionId=selected['selectionId']),code='CredentialRetired')
        check(call(c,'disconnect',dict(attemptId=attempt))['state']=='closed','Retirement still permits cleanup of our session')
        for name in ['wrong-name','expired','server']:
            extra=start_server(name)
            try:
                badendpoint='https://127.0.0.1:'+str(extra.server_port);badpin='0'*64 if name=='server' else pins[name]
                badlogin,_,_=credential(c,badendpoint,badpin);count=len(state['calls']);_,status=connect(c,badlogin)
                check(status['state']=='notConnected' and len(state['calls'])==count,'TLS refuses '+name+' before HTTP or secret dispatch')
            finally:extra.shutdown();extra.server_close()
        check(all(state['loginCorrect']),'Every login uses exact protected password with no borrowed token')
        check(all(state['connectionClose']),'Every request avoids reuse of a stale pooled connection')
        check(all(not request['tlsSessionReused'] for request in state['requests']),'Every RPC uses a full TLS handshake rather than a resumed peer identity')
        check(set(state['calls'])<={'Api.Browse','Api.Login','Api.Logout','Api.GetPermissions','PlcProgram.Browse','PlcProgram.Read','Api.Version','Api.GetQuantityStructures','Api.GetAuthenticationMode','Api.GetSessionInfo'},'No write, arbitrary RPC or inventory-token route')
        for path in (out/'store').rglob('*'):
            if path.name=='owner.lock':
                check(path.stat().st_size==0,'Owner lock contains no data');continue
            if path.is_file():check(password.encode() not in path.read_bytes() and token.encode() not in path.read_bytes(),'No runtime journal secrets')
        final_login,_,_=credential(c,endpoint,pins['server']);shutdown_attempt,status=connect(c,final_login)
        check(status['state']=='connected','Owned session retained for graceful shutdown check')
        report['completed']=True
    finally:
        for c in clients:
            receipt=c.close();report['shutdowns'].append(receipt)
            report.setdefault('hostLogs',[]).extend(c.errors)
            check(receipt['hostExitCode']==0 and not receipt['forcedHostStop'],'Clean normal Host shutdown')
            check(password not in '\n'.join(c.errors) and token not in '\n'.join(c.errors),'Host stderr contains no secrets')
        if report['completed']:
            retained=json.loads((out/'store/runtime-web'/(shutdown_attempt+'.json')).read_text())
            check(retained['state']=='closed','Graceful shutdown performs owned logout and saves terminal state')
        if server:server.shutdown();server.server_close()
        report['rpcMethods']=state['calls'];report['rpcRequests']=state['requests'];report['connectionIds']=state['connectionIds'];report['checkCount']=len(report['checks'])
        report['binaryHashes']={n:hashlib.sha256((host.parent/n).read_bytes()).hexdigest() for n in ['TiaMcp.Host.dll','TiaMcp.Core.dll']}
        (out/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print('PASS runtime Web MCP: '+str(report['checkCount'])+' checks; actual loopback TLS, DPAPI and Host restart; no physical PLC.')


if __name__=='__main__':main()
