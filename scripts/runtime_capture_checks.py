"""Capture checks against the real Host and the session harness's loopback TLS endpoint."""
import base64
import csv
import hashlib
import io
import json
import threading
import time
import uuid
from functools import lru_cache
from pathlib import Path
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

@lru_cache(maxsize=3)
def validator(name='runtime-capture'):
    registry=Registry();root=Path(__file__).resolve().parents[1]
    for dependency in ["physical-runtime","diagnostic-case","diagnostic-signal-manifest","diagnostic-alarm-bindings","runtime-capture","runtime-capture-analysis","diagnostic-report-export"]:
        schema=json.loads((root/"contracts"/(dependency+".v1.schema.json")).read_text())
        registry=registry.with_resource(schema["$id"],Resource.from_contents(schema))
    schema=json.loads((root/"contracts"/(name+".v1.schema.json")).read_text())
    return Draft202012Validator(schema,registry=registry)


def capture_call(client, name, arguments, check, code=None):
    result = client.request('tools/call', {'name': 'runtime_capture_'+name, 'arguments': arguments})
    value = result['structuredContent']
    if code:
        check(result.get('isError') and value['error']['code'] == code, 'Capture refuses '+code)
    else:
        check(not result.get('isError'), 'Capture '+name+' succeeds: '+str(value)[:200])
    if value.get('schemaVersion','').startswith('runtime-capture-'):
        validator('runtime-capture-analysis' if name=='analyze' else 'runtime-capture').validate(value)
    return value


def last_read(rows):
    row = next((row for row in reversed(rows) if row['kind'] == 'read'), None)
    assert row is not None, 'Capture contains no read observation'
    return row


def wait_capture(client, capture_id, check):
    deadline = time.monotonic()+15
    while time.monotonic() < deadline:
        status = capture_call(client, 'status', dict(captureId=capture_id), check)
        if status['state'] != 'running': return status
        time.sleep(.1)
    raise AssertionError('Capture did not terminate')


def run_capture_checks(client, attempt, selection, state, output, check):
    from runtime_association_checks import run_association_checks
    from runtime_alarm_capture_checks import run_alarm_capture_checks
    def call(name, **args): return capture_call(client, name, args, check)
    def wait(capture_id): return wait_capture(client, capture_id, check)
    request = dict(attemptId=attempt, selectionIds=[selection], intervalMilliseconds=100,
                   durationSeconds=5, maximumSamples=3, maximumBytes=1048576)
    retained = []
    retained.append(run_association_checks(client,attempt,state,output,check))
    retained.append(run_alarm_capture_checks(client,attempt,state,output,check))
    # A supplied review is a declaration only; deliberately use an unrelated engineering project.
    review_root = output/'store/project-reviews'
    review_root.mkdir(parents=True, exist_ok=True)
    review = dict(schemaVersion='project-review.v1',
                  model=json.loads((Path(__file__).resolve().parents[1]/'fixtures/project-explorer/representative.project-model.json').read_text()),
                  documents=[], capture=dict(syntheticFixture=True), wholeProjectContentComplete=False)
    review_bytes = json.dumps(review).encode()
    review_id = hashlib.sha256(review_bytes).hexdigest()
    review_path = review_root/(review_id+'.json')
    review_path.write_bytes(review_bytes)
    for supplied, code in [('bad', 'RuntimeCaptureReferenceInvalid'), ('0'*64, 'ArtifactUnavailable')]:
        refused_id = uuid.uuid4().hex; before = len(state['calls'])
        capture_call(client, 'start', dict(captureId=refused_id, request={**request, 'reviewId':supplied}), check, code)
        check(len(state['calls']) == before and not (output/'store/runtime-captures'/refused_id).exists(), 'Invalid review refuses before capture intent or target access')
    review_path.write_bytes(review_bytes+b' ')
    before = len(state['calls']); refused_id = uuid.uuid4().hex
    capture_call(client, 'start', dict(captureId=refused_id, request={**request, 'reviewId':review_id}), check, 'ArtifactChanged')
    check(len(state['calls']) == before and not (output/'store/runtime-captures'/refused_id).exists(), 'Changed review refuses before polling or durable intent')
    review_path.write_bytes(review_bytes)
    associated_id = uuid.uuid4().hex
    associated_request = {**request, 'reviewId':review_id, 'maximumSamples':1}
    call('start', captureId=associated_id, request=associated_request)
    associated = wait(associated_id)
    check(associated['plan']['request']['reviewId'] == review_id and not associated['programMatchVerified'], 'Unrelated saved review remains only a declared association, never a verified program match')
    review_path.unlink()
    before = len(state['calls'])
    repeated = call('start', captureId=associated_id, request=associated_request)
    associated_export = call('export', snapshotRef=associated['snapshotRef'])
    exported = json.loads(Path(associated_export['artifacts'][0]['path']).read_text())
    check(repeated['snapshotRef'] == associated['snapshotRef'] and exported['capture']['plan']['request']['reviewId'] == review_id and len(state['calls']) == before, 'Removing source review does not rewrite or restart a retained declaration')
    retained.append(dict(captureId=associated_id, snapshotRef=associated['snapshotRef'], state='ended', reviewId=review_id))
    # The failure is after a valid prefix, while a real polling worker owns the capture.
    blocked_id = uuid.uuid4().hex
    blocked_request = {**request, 'maximumSamples':100, 'durationSeconds':10}
    call('start', captureId=blocked_id, request=blocked_request)
    deadline = time.monotonic()+10
    while time.monotonic() < deadline:
        prefix = call('status', captureId=blocked_id)
        if prefix['recordedProgress']['rows'] > 0: break
        time.sleep(.05)
    check(prefix['state'] == 'running' and prefix['recordedProgress']['rows'] > 0, 'Storage fault starts with a committed live prefix')
    next_segment = int(prefix['snapshotRef'].split(':')[2])+1
    collision = output/'store/runtime-captures'/blocked_id/f'{next_segment:06d}.segment'
    with collision.open('xb') as stream: stream.write(b'occupied-by-storage-fault-test')
    blocked = wait(blocked_id)
    check(blocked['state'] == 'interrupted' and blocked['snapshotRef'] == prefix['snapshotRef'] and blocked['localFailure'] == 'RuntimeCaptureStorageFailure' and blocked['uncommittedTailPresent'] and blocked['uncommittedDataUnknown'], 'Worker publication failure preserves the committed prefix and exposes unknown loss')
    before = len(state['calls'])
    call('start', captureId=blocked_id, request=blocked_request)
    blocked_page = call('page', snapshotRef=blocked['snapshotRef'])
    call('export', snapshotRef=blocked['snapshotRef'])
    check(blocked_page['totalRows'] == prefix['recordedProgress']['rows'] and len(state['calls']) == before, 'Storage failure observation and export do not replay acquisition')
    retained.append(dict(captureId=blocked_id, snapshotRef=blocked['snapshotRef'], state='interrupted', reviewId=None))
    capture_id = uuid.uuid4().hex
    call('start', captureId=capture_id, request=request)
    status = wait(capture_id)
    check(status['recordedProgress']['terminalReason'] == 'sampleLimit' and status['recordedProgress']['readAttempts'] == 3, 'Exact sample bound terminates polling')
    before = len(state['calls'])
    check(call('start', captureId=capture_id, request=request)['snapshotRef'] == status['snapshotRef'], 'Repeated capture ID observes retained intent')
    capture_call(client, 'start', dict(captureId=capture_id, request={**request, 'maximumSamples': 4}), check, 'RuntimeCaptureRequestChanged')
    page = call('page', snapshotRef=status['snapshotRef'], offset=0, count=2)
    check(page['nextOffset'] == 2 and len(page['rows']) == 2, 'Capture page is bounded')
    check(json.loads(page['rows'][0]['readResultJson'])['value'] == 2**64-1, 'UInt64 capture is exact through MCP JSON string')
    export = call('export', snapshotRef=status['snapshotRef'], format='csv')
    for item in export['artifacts']:
        data = bytearray()
        while len(data) < item['bytes']:
            result = client.request('tools/call', {'name': 'artifact_read', 'arguments': {'path': item['path'], 'offset': len(data), 'count': 65536}})
            check(not result.get('isError'), 'Capture artifact download')
            data.extend(base64.b64decode(result['structuredContent']['data']))
        check(hashlib.sha256(data).hexdigest() == item['sha256'], 'Capture export download hash')
        if item['path'].endswith('.json'):
            (output/'capture-export.json').write_bytes(data)
            document = json.loads(data)
            check(not document['capture']['nativeQualified'] and document['capture']['plcChangesBetweenPollsUnknown'], 'Export retains polling limitations')
        else:
            rows = list(csv.DictReader(io.StringIO(data.decode())))
            check(json.loads(rows[0]['read_result_json'])['value'] == 2**64-1, 'CSV retains exact nested JSON value')
    check(len(state['calls']) == before, 'Reopen, repeat, page and export dispatch no network calls')
    try:
        state['readDelay'] = .35
        final_slow_id = uuid.uuid4().hex
        call('start', captureId=final_slow_id, request={**request, 'maximumSamples': 1})
        final_slow = wait(final_slow_id)
        check(final_slow['recordedProgress']['skippedPolls'] >= 3 and final_slow['recordedProgress']['terminalReason'] == 'sampleLimit', 'Slow final sample records missed slots before sample-limit termination')
    finally:
        state['readDelay'] = 0
    state['mode'] = 'providerDenied'
    failed_id = uuid.uuid4().hex
    call('start', captureId=failed_id, request={**request, 'maximumSamples': 1})
    failed = wait(failed_id)
    failed_row = call('page', snapshotRef=failed['snapshotRef'])['rows'][0]
    value = json.loads(failed_row['readResultJson'])
    check(value['status'] == 'permissionDenied' and value['value'] is None, 'Provider error does not become zero or stale sample')
    state['mode'] = 'normal'
    raw_id = uuid.uuid4().hex
    call('start', captureId=raw_id, request={**request, 'maximumSamples': 1, 'representation': 'raw'})
    raw = wait(raw_id)
    check(json.loads(call('page', snapshotRef=raw['snapshotRef'])['rows'][0]['readResultJson'])['value'] == [0,255,17], 'Capture raw byte representation')
    (output/'capture-retention-cases.json').write_text(json.dumps(retained,indent=2)+'\n')
    return retained


def run_capture_cancellation_check(client, attempt, selection, state, output, check, scenario):
    """One deliberate cancellation on a caller-owned fresh session; never reconnect."""
    def call(name, **args): return capture_call(client, name, args, check)
    def wait(identity): return wait_capture(client, identity, check)
    request = dict(attemptId=attempt, selectionIds=[selection], intervalMilliseconds=100,
                   durationSeconds=5, maximumSamples=3, maximumBytes=1048576)
    identity = uuid.uuid4().hex
    extra = {}
    try:
        if scenario == 'durationLimit':
            request.update(maximumSamples=10, durationSeconds=1)
            state['readDelay'] = .35
            call('start', captureId=identity, request=request)
            status = wait(identity)
            check(status['recordedProgress']['skippedPolls'] >= 2 and status['recordedProgress']['terminalReason'] == 'durationLimit', 'Slow endpoint records skipped polls without catch-up burst')
        elif scenario == 'verificationCancellation':
            request.update(maximumSamples=10, durationSeconds=1)
            entered, released = threading.Event(), threading.Event()
            state['sessionInfoGate'] = (entered, released)
            before = len(state['calls'])
            try:
                call('start', captureId=identity, request=request)
                check(entered.wait(5), 'Identity verification entered the explicit cancellation gate')
                status = wait(identity)
                observed = client.tool('runtime_web_status', dict(attemptId=attempt))
                check(observed['state'] == 'remoteSessionUnknown' and observed['errorCode'] == 'RuntimeCancelled'
                      and observed['readAvailability'] == 'permissionUnknown', 'Cancelled identity verification keeps the old session unusable')
                check(state['calls'][before:] == ['Api.GetSessionInfo'], 'Cancelled identity verification dispatches no value read or automatic login')
                extra['invalidatedSession'] = observed
                refused_id = uuid.uuid4().hex
                refused_before = len(state['calls'])
                extra['oldAttemptRefusal'] = capture_call(client, 'start', dict(captureId=refused_id, request=request), check, 'RuntimeSessionUnavailable')
                check(len(state['calls']) == refused_before and not (output/'store/runtime-captures'/refused_id).exists(),
                      'Unusable old attempt refuses before network dispatch or new capture intent')
                extra['refusedCaptureId'] = refused_id
            finally:
                released.set()
                state.pop('sessionInfoGate', None)
            rows = call('page', snapshotRef=status['snapshotRef'])['rows']
            check(status['recordedProgress']['terminalReason'] == 'durationLimit'
                  and len([r for r in rows if r['kind'] == 'read']) == 1
                  and last_read(rows)['errorCode'] == 'RuntimeCancelled', 'Verification cancellation remains an original cancelled read and duration limit')
        elif scenario == 'queuedDeadline':
            request.update(intervalMilliseconds=500, durationSeconds=1, maximumSamples=10)
            before_read = state['calls'].count('PlcProgram.Read')
            call('start', captureId=identity, request=request)
            deadline = time.monotonic()+3
            while state['calls'].count('PlcProgram.Read') == before_read and time.monotonic()<deadline: time.sleep(.01)
            time.sleep(.05)
            state['readDelay'] = 2
            competing = client.request('tools/call', {'name':'runtime_web_read','arguments':dict(attemptId=attempt,selectionId=selection)})
            check(not competing.get('isError'), 'Concurrent caller holds the shared session gate during capture deadline')
            status = wait(identity)
            rows = call('page', snapshotRef=status['snapshotRef'])['rows']
            check(status['recordedProgress']['terminalReason']=='durationLimit' and any(r['errorCode']=='RuntimeCancelled' for r in rows), 'Cancellation while queued for the session remains duration cancellation, not session failure')
        elif scenario == 'explicitStop':
            request.update(maximumSamples=100, durationSeconds=10)
            call('start', captureId=identity, request=request)
            capture_call(client, 'start', dict(captureId=uuid.uuid4().hex, request=request), check, 'RuntimeCaptureSessionBusy')
            logout = state['logoutCount']
            status = call('stop', captureId=identity)
            check(status['state'] == 'ended' and status['recordedProgress']['terminalReason'] == 'stopped' and state['logoutCount'] == logout, 'Explicit capture stop retains evidence without logout')
            check(call('stop', captureId=identity)['snapshotRef'] == status['snapshotRef'], 'Repeated stop is observation')
        else:
            raise AssertionError('Unknown planned cancellation scenario')
    finally:
        state['readDelay'] = 0
    before = len(state['calls'])
    page = call('page', snapshotRef=status['snapshotRef'])
    exported = call('export', snapshotRef=status['snapshotRef'])
    check(len(state['calls']) == before, 'Terminal cancellation evidence reads and exports never refresh a session: ' + scenario)
    return dict(scenario=scenario, request=request, status=status, page=page, export=exported, **extra)


def check_retained_captures(client, retained, state, check):
    before = len(state['calls'])
    for case in retained:
        status = capture_call(client, 'status', dict(captureId=case['captureId']), check)
        check(status['snapshotRef'] == case['snapshotRef'] and status['state'] == case['state'] and status['plan']['request']['reviewId'] == case['reviewId'], 'Restart preserves review declaration and storage-failure prefix')
        check(status['localFailure'] is None and not status['programMatchVerified'], 'Restart does not invent process-local failure details or program qualification')
        if 'association' in case:check(status['plan']['association']==case['association'],'Restart retains the complete exact source mapping after case and review removal')
        if 'handoff' in case:check(status['plan']['handoff']==case['handoff'],'Restart retains the exact historical handoff without adopting a session or refreshing evidence')
        if case['state'] == 'interrupted':
            check(status['uncommittedTailPresent']==case.get('uncommittedTailPresent',True) and status['uncommittedDataUnknown'], 'Restart retains interrupted tail state and unknown loss')
        capture_call(client, 'page', dict(snapshotRef=case['snapshotRef']), check)
        capture_call(client, 'export', dict(snapshotRef=case['snapshotRef']), check)
        if 'reportId' in case:
            exported=client.tool('diagnostic_report_export',dict(reportId=case['reportId'],format='html'))
            check(exported['sourceReportId']==case['reportId'] and not exported['analysisRecomputed'],'Restart exports retained analysis without source case, review or network')
    check(len(state['calls']) == before, 'Retained association and storage-failure reopening have no network access')


def start_interrupted_capture(client, attempt, selection, check):
    capture_id = uuid.uuid4().hex
    capture_call(client, 'start', dict(captureId=capture_id, request=dict(attemptId=attempt,
        selectionIds=[selection], intervalMilliseconds=100, durationSeconds=60)), check)
    deadline = time.monotonic()+10
    while time.monotonic() < deadline:
        status = capture_call(client, 'status', dict(captureId=capture_id), check)
        if status['recordedProgress']['readAttempts'] > 0: return capture_id, status['snapshotRef']
        time.sleep(.15)
    raise AssertionError('No committed capture prefix before restart')
