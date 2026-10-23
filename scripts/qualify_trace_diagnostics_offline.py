"""Exercise saved-review upload and diagnostic MCP workflows with absent native workers.

Input samples come from TiaMcp.Diagnostics.Tests' optional JSON output argument.
No discovery, attachment, Siemens worker, PLC transport or native acceptance runs.
"""
import argparse
import base64
import hashlib
import json
import csv
import io
import copy
from pathlib import Path

from smoke_mcp import Client, ROOT
from jsonschema import Draft202012Validator, ValidationError
from referencing import Registry, Resource
from alarm_binding_checks import check_alarm_bindings


def alarm_type_review(data_type='Bool'):
    """Independently authored partial evidence; no Siemens format/relationship claim."""
    root = '/projects/SuppliedAlarm'
    paths = {name: root + '/' + name for name in ('hmi', 'alarm', 'supervision', 'member')}
    paths['type'] = root + '/plc/types/Status'
    edges = [dict(kind=kind, source=dict(nodeId=source, objectPath=paths[source]),
                  target=dict(nodeId=target, objectPath=paths[target]),
                  evidence=dict(origin='parsedSupported', resolution='resolved', location='independent synthetic fixture'))
             for source, target, kind in [('hmi','alarm','hmiBinding'), ('alarm','supervision','dependsOn'),
                                          ('supervision','member','reads'), ('member','type','usesType')]]
    interface = '<Interface><Member Name="Ready" Datatype="' + data_type + '" /></Interface>'
    xml = '<Document><Type>' + interface + '<Supervisions><Opaque>Überlast</Opaque></Supervisions></Type></Document>'
    digest = lambda text: hashlib.sha256(text.encode('utf-8')).hexdigest()
    return dict(schemaVersion='project-review.v1', createdAtUtc='2026-10-17T00:00:00Z',
                wholeProjectContentComplete=False, capture=dict(syntheticFixture=True), consistency='Authored partial fixture only',
                model=dict(schemaVersion='project-model.v1', project=dict(host='synthetic', projectPath='supplied.ap21'),
                           nodes=[dict(nodeId='root', kind='project', objectPath=root)] +
                                 [dict(nodeId=name, kind=name, objectPath=path) for name, path in paths.items()],
                           edges=edges, coverage=[]),
                documents=[dict(objectPath=paths['type'], status='exported', xml=xml, utf8Sha256=digest(xml),
                                signatures=dict(document=digest(xml), interface=digest(interface), logic=digest('')))])


def watch_contracts():
    schemas = [s for p in (ROOT / 'contracts').glob('*.json')
               if isinstance(s := json.loads(p.read_text(encoding='utf-8')), dict) and '$schema' in s and '$id' in s]
    registry = Registry().with_resources((s['$id'], Resource.from_contents(s)) for s in schemas)
    for schema in schemas: Draft202012Validator.check_schema(schema)
    selected = {'envelope': 'diagnostic-watch-plan.v1', 'receipt': 'diagnostic-watch-plan.v1#/$defs/receipt',
                'input': 'diagnostic-watch-plan.v1#/$defs/input', 'case': 'diagnostic-case.v1',
                'caseRequest': 'diagnostic-case.v1#/$defs/request', 'replanRequest': 'diagnostic-replan-request.v1',
                'comparisonOptions': 'trace-comparison.v1#/$defs/options', 'comparison': 'trace-comparison.v1', 'analysis': 'diagnostic-report.v1',
                'reportExport': 'diagnostic-report-export.v1', 'alarmBindings': 'diagnostic-alarm-bindings.v1',
                'alarmOperand': 'diagnostic-alarm-bindings.v1#/$defs/observation'}
    return {part: Draft202012Validator({'$ref': 'https://tia-mcp.local/contracts/' + target.replace('.v1', '.v1.schema.json')}, registry=registry)
            for part, target in selected.items()}


def run(host, output, samples_path):
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    samples = json.loads(samples_path.read_text(encoding="utf-8"))
    raw = base64.b64decode(samples["reviewArtifactBase64"], validate=True)
    review_id = hashlib.sha256(raw).hexdigest()
    assert review_id == samples["diagnosticRequest"]["reviewId"]
    report = {"schemaVersion": 1, "status": "running", "nativeQualification": False,
              "hostSha256": hashlib.sha256(host.read_bytes()).hexdigest(),
              "hostExits": [], "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "managedBinaryHashes": {name: hashlib.sha256((host.parent / name).read_bytes()).hexdigest()
                                      for name in ("TiaMcp.Host.dll", "TiaMcp.Core.dll")}, "steps": []}
    schema = json.loads((ROOT / 'contracts/project-review-import.v1.schema.json').read_text(encoding='utf-8'))
    validator = Draft202012Validator(schema)
    watch_validators = watch_contracts()
    client = Client(host.resolve(), 15, ["--artifacts", str(output),
                    "--worker", str(output / "intentionally-absent-worker.exe"),
                    "--sim-worker", str(output / "intentionally-absent-simulator.exe")])
    def upload(data, name='input.json'):
        begun = client.tool('artifact_upload_begin', dict(fileName=name, expectedBytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
        for offset in range(0, len(data), begun['maxChunkBytes']):
            client.tool('artifact_upload_chunk', dict(uploadId=begun['uploadId'], offset=offset,
                        data=base64.b64encode(data[offset:offset+begun['maxChunkBytes']]).decode()))
        return client.tool('artifact_upload_commit', dict(uploadId=begun['uploadId']))
    def import_review(data):
        committed = upload(data)
        result = client.tool('project_snapshot_import', dict(uploadRef=committed['artifactRef']))
        validator.validate(result)
        assert result['reviewId'] == hashlib.sha256(data).hexdigest()
        assert not result['nativeQualified'] and not result['sourceAuthenticityVerified']
        client.tool('artifact_upload_release', dict(uploadId=committed['uploadId']))
        return result
    def close():
        receipt = client.close(); report['hostExits'].append(receipt)
        assert receipt['hostExitCode'] == 0 and not receipt['forcedHostStop']
        assert all(row.get('started') is not True for row in receipt['workerExits'])
        return receipt
    def restart():
        nonlocal client
        close()
        client = Client(host.resolve(), 30, ['--artifacts', str(output), '--worker', str(output/'intentionally-absent-worker.exe'),
                        '--sim-worker', str(output/'intentionally-absent-simulator.exe')])
        client.request('initialize', {'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'offline-trace-restart','version':'1'}})
        client.notify('notifications/initialized')
    def reject(name, arguments, code):
        result = client.request('tools/call', {'name':name,'arguments':arguments})
        error = result.get('structuredContent') or json.loads('\n'.join(c['text'] for c in result['content'] if c['type']=='text'))
        assert result.get('isError') and error['error']['code'] == code, error
        if name == 'project_snapshot_import': validator.validate(error)
        report['steps'].append(name+' rejects '+code)
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                      "clientInfo": {"name": "offline-trace-diagnostics", "version": "1"}})
        client.notify("notifications/initialized")
        listed = {t["name"]: t for t in client.request("tools/list")["tools"]}
        for name in ("diagnostic_plan", "diagnostic_watch_plan", "trace_capabilities", "trace_prepare", "trace_read", "trace_analyze", "trace_compare", "diagnostic_report_export", "diagnostic_replan", "trace_check_readback"):
            assert listed[name]["annotations"]["readOnlyHint"] is True
        assert listed['project_snapshot_import']['annotations']['readOnlyHint'] is False
        retained_review = import_review(raw)
        type_before = alarm_type_review()
        type_after = alarm_type_review('Int')
        type_bytes = json.dumps(type_before, ensure_ascii=False).encode('utf-8')
        type_receipt = import_review(type_bytes)
        before_type_id = type_receipt['reviewId']
        after_type_id = import_review(json.dumps(type_after, ensure_ascii=False).encode('utf-8'))['reviewId']
        alarm_raw = base64.b64decode(samples['alarmReviewArtifactBase64'], validate=True)
        alarm_review = import_review(alarm_raw)
        restart()
        alarm_request = samples['alarmRequest']
        assert alarm_request['reviewId'] == alarm_review['reviewId']
        watch_validators['caseRequest'].validate(alarm_request)
        alarm_case = client.tool('diagnostic_plan', dict(request=alarm_request))
        watch_validators['case'].validate(alarm_case['plan'])
        alarm_evidence = alarm_case['plan']['alarmEvidence']
        assert alarm_evidence['selector'] == alarm_request['alarmContext']
        assert alarm_evidence['object']['metadata']['alarmId'] == 42
        assert alarm_evidence['ancestryStatus'] == 'capturedToProject' and len(alarm_evidence['ancestry']) == 3
        assert len(alarm_evidence['relationships']) == 2 and not alarm_evidence['storageSelected']
        assert not alarm_evidence['sourceAuthenticityVerified'] and not alarm_evidence['coverageComplete']
        for row in alarm_evidence['relationships']:
            assert row['edge'] == json.loads(alarm_raw)['model']['edges'][row['index']]
            assert row['status'] == 'exactCapturedEndpoints'
        alarm_watch_args = dict(planId=alarm_case['id'], request=samples['watchRequest'])
        alarm_watch = client.tool('diagnostic_watch_plan', alarm_watch_args)
        alarm_envelope = json.loads(Path(alarm_watch['artifact']['path']).read_text(encoding='utf-8'))
        watch_validators['envelope'].validate(alarm_envelope)
        assert alarm_envelope['alarmEvidence'] == alarm_evidence
        unresolved_alarm = copy.deepcopy(alarm_request)
        for field in ['blockPath', 'instance', 'signals']: unresolved_alarm.pop(field)
        unresolved_case = client.tool('diagnostic_plan', dict(request=unresolved_alarm))
        assert unresolved_case['plan']['selectedInstance'] is None and unresolved_case['plan']['signals'] == []
        reject('diagnostic_watch_plan', dict(planId=unresolved_case['id'], request=samples['watchRequest']), 'InstanceResolutionIncomplete')
        for field, value in [('reviewId', review_id), ('nodeId', 'peer-42'), ('objectPath', '/projects/Fixture/other/42')]:
            stale_alarm = copy.deepcopy(alarm_request); stale_alarm['alarmContext'][field] = value
            reject('diagnostic_plan', dict(request=stale_alarm), 'AlarmContextUnresolved')
        spoofed_alarm = copy.deepcopy(alarm_request); spoofed_alarm['alarmContext']['nativeQualified'] = True
        try: watch_validators['caseRequest'].validate(spoofed_alarm)
        except ValidationError: pass
        else: raise AssertionError('alarm selector schema accepted a qualification claim')
        reject('diagnostic_plan', dict(request=spoofed_alarm), 'InvalidRequest')
        alarm_replan = dict(reviewId=alarm_request['reviewId'], blockPath=alarm_request['blockPath'], instance=alarm_request['instance'],
                            reason='Explicit unchanged declaration mappings; alarm evidence must be selected again.',
                            signalMappings=[dict(id=s['id'], storagePath=s['storagePath'], memberPath=s['memberPath'], reason='Explicit exact mapping')
                                            for s in alarm_case['plan']['signals']])
        watch_validators['replanRequest'].validate(alarm_replan)
        alarm_next = client.tool('diagnostic_replan', dict(planId=alarm_case['id'], request=alarm_replan))
        assert 'alarmEvidence' not in alarm_next['plan'] and alarm_next['plan']['lineage']['previousPlanId'] == alarm_case['id']
        alarm_replan['alarmContext'] = alarm_request['alarmContext']
        watch_validators['replanRequest'].validate(alarm_replan)
        alarm_reselected = client.tool('diagnostic_replan', dict(planId=alarm_case['id'], request=alarm_replan))
        assert alarm_reselected['plan']['alarmEvidence'] == alarm_evidence
        # Honest new hashes must not promote modified derived fields to source evidence.
        mutations = [
            (['signals', 0, 'symbolComponents'], ['DifferentDB', 'DifferentMember']),
            (['signals', 0, 'datatype'], 'LReal'),
            (['signals', 0, 'evidence', 'xmlLine'], 999999),
            (['signals', 0, 'evidence', 'liveVerified'], True),
            (['signals', 0, 'onlineAddress'], '%M0.0'),
            (['signals', 0, 'memberPath'], ['MissingMember']),
            (['selectedInstance', 'callSites', 0, 'callUid'], 'forged'),
            (['selectedInstance', 'callSites', 0, 'parameterBindings'], [{'parameter': 'forged'}]),
            (['selectedInstance', 'rootCallSites', 0, 'calleePath'], alarm_request['blockPath']),
            (['sourceRevision', 'documents', 0, 'utf8Sha256'], '0' * 64),
            (['sourceRevision', 'reviewId'], '0' * 64),
            (['sourceRevision', 'onlineVerified'], True),
            (['nativeExecutable'], True),
            (['alarmEvidence', 'object', 'metadata', 'text'], 'forged'),
        ]
        source_controls = []
        for path, value in mutations:
            changed = copy.deepcopy(alarm_case['plan']); target = changed
            for key in path[:-1]: target = target[key]
            target[path[-1]] = value
            data = json.dumps(changed, ensure_ascii=False).encode('utf-8')
            case_id = hashlib.sha256(data).hexdigest()
            retained_path = output / 'trace-diagnostics' / (case_id + '.json')
            with retained_path.open('xb') as stream: stream.write(data)
            source_controls.append(dict(caseId=case_id, field=path))
        restart()
        prepare = {**samples['prepareRequest'], 'reviewId': alarm_request['reviewId']}
        for control in source_controls:
            before_files = set((output / 'trace-diagnostics').iterdir())
            for name, request in [('trace_prepare', prepare), ('diagnostic_watch_plan', samples['watchRequest']), ('diagnostic_replan', alarm_replan)]:
                reject(name, dict(planId=control['caseId'], request=request), 'CaptureMismatch')
            if set((output / 'trace-diagnostics').iterdir()) != before_files:
                raise AssertionError('A rejected source control published a diagnostic artifact')
        report['sourceRevalidationControls'] = source_controls
        report['sourceRevalidationRefusals'] = len(source_controls) * 3
        check_alarm_bindings(lambda name, args: client.tool(name, args), import_review, restart, reject, samples, watch_validators, output, report)
        report['steps'].append('exact supplied alarm context: original ancestry/edges, duplicate scoped IDs, watch envelope, unresolved storage, stale selector refusal and explicit replan lineage')
        type_args = dict(beforeId=before_type_id, afterId=after_type_id, impactDepth=4, maxItems=1)
        type_delta = client.tool('project_snapshot_compare', type_args)
        assert type_delta['contentComparedTypes'] == 1 and type_delta['contentComparedBlocks'] == 0
        assert type_delta['contentUnknownTypes'] == 0 and type_delta['totalChanges'] == 1
        assert type_delta['changes'][0]['changedFields'] == ['interfaceChanged', 'documentChanged']
        assert not type_delta['sourceAuthenticityVerified'] and not type_delta['impactCoverageComplete']
        assert not type_delta['impactTraversal']['truncated'] and not type_delta['impactTraversal']['complete']
        paths = type_delta['observedImpactPaths']
        assert len(paths) == 8
        full_paths = [p for p in paths if p['distance'] == 4]
        assert len(full_paths) == 2
        for path in full_paths:
            assert path['edges'] == type_before['model']['edges']
            assert path['reviewId'] == (before_type_id if path['reviewSide'] == 'before' else after_type_id)
            assert [p.rsplit('/', 1)[-1] for p in path['objectPaths']] == ['hmi', 'alarm', 'supervision', 'member', 'Status']
        next_page = client.tool('project_snapshot_compare', {**type_args, 'offset': 1})
        assert next_page['changes'] == [] and next_page['observedImpactPaths'] == paths
        assert client.tool('project_snapshot_compare', {**type_args, 'impactDepth': 3})['impactTraversal']['truncated']
        assert client.tool('project_snapshot_search', dict(reviewId=before_type_id, query='überlast'))['totalObservedHits'] == 1
        assert client.tool('project_health_report', dict(reviewId=before_type_id))['freshness'] == 'savedSnapshotOnly'
        downloaded = client.tool('artifact_read', dict(path=type_receipt['artifact']['path']))
        assert base64.b64decode(downloaded['data'], validate=True) == type_bytes
        report['steps'].append('supplied UDT XML exact-byte retention, search, health and before/after four-edge alarm impact after Host restart')
        missing_type = copy.deepcopy(type_before)
        missing_type['documents'] = []
        missing_id = import_review(json.dumps(missing_type).encode('utf-8'))['reviewId']
        missing_delta = client.tool('project_snapshot_compare', dict(beforeId=before_type_id, afterId=missing_id))
        assert missing_delta['contentUnknownTypes'] == 1 and missing_delta['contentComparedTypes'] == 0
        assert missing_delta['contentUnknownBlocks'] == 0 and not missing_delta['wholeProjectEquivalent']
        for mutation in ('virtual', 'wrongKind', 'duplicate', 'foreign', 'signature', 'wrongEndpoint'):
            bad_type = copy.deepcopy(type_before)
            type_node = next(n for n in bad_type['model']['nodes'] if n['nodeId'] == 'type')
            if mutation == 'virtual': type_node['origin'] = 'virtual'
            if mutation == 'wrongKind': type_node['kind'] = 'tag'
            if mutation == 'duplicate': bad_type['documents'].append(copy.deepcopy(bad_type['documents'][0]))
            if mutation == 'foreign': bad_type['documents'][0]['objectPath'] += '/foreign'
            if mutation == 'signature': bad_type['documents'][0]['signatures']['document'] = '0'*64
            if mutation == 'wrongEndpoint': bad_type['model']['edges'][-1]['target']['nodeId'] = 'member'
            committed_type = upload(json.dumps(bad_type).encode('utf-8'))
            reject('project_snapshot_import', dict(uploadRef=committed_type['artifactRef']),
                   'ArtifactChanged' if mutation == 'signature' else 'InvalidSnapshot')
            client.tool('artifact_upload_release', dict(uploadId=committed_type['uploadId']))
        report['steps'].append('type content unknown stays explicit; invalid document selectors, hashes and dependency identities refuse')
        (output / 'supplied-type-comparison.json').write_text(json.dumps(type_delta, ensure_ascii=False, indent=2)+'\n', encoding='utf-8', newline='\n')
        searched = client.tool('project_snapshot_search', dict(reviewId=review_id,query='Conveyor'))
        assert searched['totalObservedHits'] > 0 and not searched['sourceAuthenticityVerified']
        assert client.tool('project_snapshot_compare', dict(beforeId=review_id,afterId=review_id))['totalChanges'] == 0
        assert client.tool('project_health_report', dict(reviewId=review_id))['freshness'] == 'savedSnapshotOnly'
        transferred = bytearray()
        while len(transferred) < retained_review['artifact']['bytes']:
            chunk = client.tool('artifact_read', dict(path=retained_review['artifact']['path'],offset=len(transferred)))
            transferred.extend(base64.b64decode(chunk['data'],validate=True))
        assert bytes(transferred) == raw
        report['steps'].append('review upload, release, exact-byte download and offline tools after actual Host restart')
        capabilities = client.tool("trace_capabilities")
        assert capabilities["targetProbed"] is False and capabilities["nativeQualification"] is False
        ax = next(route for route in capabilities["routes"] if route["backend"] == "simaticAxCli")
        assert ax["documentedRoute"] == "documentedExternalCli" and ax["implementation"] == "unavailable"
        assert "excludes save triggers on TIA-engineered PLCs" in ax["limitations"]
        report["steps"].append("discovery and offline route capabilities")
        result_upload = upload(json.dumps(samples['testSuitePacket'], ensure_ascii=False).encode('utf-8'))
        supplied_results = client.tool('testsuite_result_import', dict(reviewId=review_id, uploadRef=result_upload['artifactRef']))
        client.tool('artifact_upload_release', dict(uploadId=result_upload['uploadId']))
        finding_request = copy.deepcopy(samples['diagnosticRequest'])
        finding_request.pop('blockPath')
        health = client.tool('project_health_report', dict(reviewId=review_id, testSuiteResults=[supplied_results['receipt']]))
        assert health['testSuite']['findingCount'] == 1 and not health['testSuite']['wholeProjectHealthComplete']
        finding_request['testSuiteFinding'] = {**health['testSuite']['reports'][0]['receipt'], 'index': 0}
        report['steps'].append('selected Test Suite health evidence provides exact retained finding navigation into a diagnostic case')
        watch_validators['caseRequest'].validate(finding_request)
        template_replans = []
        for template_id in ('competingWriters', 'timer', None):
            template_request = copy.deepcopy(samples['diagnosticRequest'])
            if template_id is None: template_request.pop('templateId', None)
            else: template_request['templateId'] = template_id
            template_request['observations'] = ['Prior machine observation']
            template_request['context'] = {'timing': 'Prior machine timing'}
            template_case = client.tool('diagnostic_plan', dict(request=template_request))
            mappings = []
            for signal in template_case['plan']['signals']:
                members = copy.deepcopy(signal['memberPath'])
                if members[0] == 'Conv2': members[0] = 'Conv1'
                mappings.append(dict(id=signal['id'], storagePath=signal['storagePath'], memberPath=members,
                                     reason='Explicit new instance; shared external signals deliberately retained.'))
            replacement = dict(reviewId=template_request['reviewId'], blockPath=template_request['blockPath'],
                               instance=dict(dbPath=template_request['instance']['dbPath'], memberPath=['Conv1']),
                               reason='Same diagnostic question on a separately resolved machine instance.', signalMappings=mappings)
            watch_validators['replanRequest'].validate(replacement)
            replanned = client.tool('diagnostic_replan', dict(planId=template_case['id'], request=replacement))
            watch_validators['case'].validate(replanned['plan'])
            assert replanned['plan']['template'] == template_case['plan']['template']
            assert replanned['plan']['selectedInstance']['memberPath'] == ['Conv1']
            assert replanned['plan']['lineage']['previousPlanId'] == template_case['id']
            assert 'Prior machine' not in json.dumps(replanned['plan']) and not replanned['plan']['nativeExecutable']
            template_replans.append(dict(planId=template_case['id'], request=replacement, resultId=replanned['id']))
        report['templateReplans'] = template_replans
        report['steps'].append('Instance replan retains selected role templates and legacy absence while re-resolving storage and excluding old observations')
        plan = client.tool("diagnostic_plan", {"request": finding_request})
        watch_validators['case'].validate(plan['plan'])
        evidence = plan['plan']['testSuiteEvidence']
        assert evidence['finding']['finding']['warningCount'] == 2 and evidence['finding']['finding']['state'] == 'Success'
        assert evidence['input'] == supplied_results['input'] and not evidence['summary']['sourceAuthenticityVerified']
        assert not evidence['summary']['nativeQualified'] and not plan['plan']['nativeExecutable']
        for mutation in ('binding', 'nativeQualified', 'counter'):
            changed = copy.deepcopy(plan['plan'])
            if mutation == 'binding': changed['testSuiteEvidence']['finding']['binding'] = 'differentSourceObservation'
            elif mutation == 'nativeQualified': changed['testSuiteEvidence']['summary']['nativeQualified'] = True
            else: changed['testSuiteEvidence']['finding']['finding']['warningCount'] = -1
            try: watch_validators['case'].validate(changed)
            except ValidationError: report['steps'].append('diagnostic case schema rejects altered Test Suite ' + mutation)
            else: raise AssertionError('Diagnostic case schema accepted altered ' + mutation)
        conflict = copy.deepcopy(finding_request); conflict['blockPath'] = samples['diagnosticRequest']['instance']['dbPath']
        reject('diagnostic_plan', dict(request=conflict), 'TestSuiteFindingUnresolved')
        report['steps'].append('retained supplied Test Suite finding selects exact block and preserves original counters and unverified provenance')
        restart()
        assert client.tool('diagnostic_plan', dict(request=finding_request)) == plan
        report['steps'].append('finding-linked diagnostic case survives released upload and actual Host restart')
        assert plan["plan"]["selectedInstance"]["memberPath"] == ["Conv2"]
        assert len(plan["plan"]["signals"]) == 11
        for numeric in (False, True):
            unavailable = copy.deepcopy(samples['diagnosticRequest'])
            signal = next(s for s in unavailable['signals'] if s['id'] == 'enable')
            signal['memberPath'] = ['Conv2', 'Missing']
            condition = next(c for c in unavailable['hypotheses'][0]['prediction']['expect'] if c['signalId'] == 'enable')
            if numeric: condition.update(value=1,unit='1')
            reject('diagnostic_plan', dict(request=unavailable), 'SignalUnobservable')
        report['steps'].append('Boolean and numeric predictions cannot assume an unavailable declaration type')
        watch_args = dict(planId=plan['id'], request=samples['watchRequest'])
        watch_validators['input'].validate(watch_args)
        watch = client.tool('diagnostic_watch_plan', watch_args)
        watch_validators['receipt'].validate(watch)
        assert watch['rowCount'] == watch['candidateCount'] == 11 and watch['unresolvedCount'] == 0
        reject('diagnostic_watch_plan', dict(planId=plan['id'],request={k:v for k,v in samples['watchRequest'].items() if k != 'externalStorage'}), 'ExternalStorageNotSelected')
        limited = client.tool('diagnostic_watch_plan', dict(planId=plan['id'],request={**samples['watchRequest'],'maxRows':2}))
        watch_validators['receipt'].validate(limited)
        assert limited['rowCount'] == 2 and limited['unresolvedCount'] == 9 and limited['truncated']
        report['steps'].append('offline watch planning, explicit external allowance and retained row omissions')
        trace = client.tool("trace_prepare", {"planId": plan["id"], "request": samples["prepareRequest"]})
        assert trace["specification"]["fulfillment"] == "partialManualHandoff"
        assert trace["specification"]["effectiveSettings"] is None
        report["steps"].append("exact captured storage and manual specification")
        rejected = client.request("tools/call", {"name": "trace_prepare", "arguments": {"planId": plan["id"],
            "request": {**samples["prepareRequest"], "backend": "nativeApi"}}})
        assert rejected["isError"] is True
        error = json.loads("\n".join(c["text"] for c in rejected["content"] if c["type"] == "text"))["error"]
        assert error["code"] == "UnsupportedCapability" and error["details"]["nativeAccessAttempted"] is False
        report["steps"].append("unsupported native route fails without dispatch")
        measurement = samples["trace-measurement.v1"]
        measurement["tracePlanId"] = trace["id"]
        data = json.dumps(measurement).encode("utf-8")
        committed = upload(data)
        imported = client.tool("trace_read", {"tracePlanId": trace["id"], "uploadRef": committed['artifactRef']})
        client.tool('artifact_upload_release', dict(uploadId=committed['uploadId']))
        restart()
        assert client.tool('diagnostic_watch_plan', watch_args)['id'] == watch['id']
        assert client.tool('diagnostic_watch_plan', alarm_watch_args)['id'] == alarm_watch['id']
        alarm_bytes = client.tool('artifact_read', dict(path=alarm_case['artifact']['path']))
        retained_alarm = json.loads(base64.b64decode(alarm_bytes['data'], validate=True))
        assert retained_alarm['alarmEvidence'] == alarm_evidence
        report['steps'].append('selected alarm case and watch envelope survive actual Host restart with exact evidence')
        watch_bytes = bytearray()
        while len(watch_bytes) < watch['artifact']['bytes']:
            chunk = client.tool('artifact_read', dict(path=watch['artifact']['path'], offset=len(watch_bytes)))
            watch_bytes.extend(base64.b64decode(chunk['data'], validate=True))
        assert hashlib.sha256(watch_bytes).hexdigest() == watch['id']
        envelope = json.loads(watch_bytes); watch_validators['envelope'].validate(envelope)
        assert envelope['caseId'] == plan['id'] and envelope['reviewId'] == review_id
        assert envelope['watchPlan']['source']['snapshot']['sourceSha256'] == review_id
        assert envelope['watchPlan']['planSha256'] == watch['planSha256']
        assert [row['candidateId'] for row in envelope['watchPlan']['rows']] == [s['id'] for s in plan['plan']['signals']]
        assert not envelope['nativeDocumentGenerated'] and not envelope['sourceAuthenticityVerified']
        report['steps'].append('watch plan exact-byte download and idempotent regeneration after Host restart')
        imported_document = json.loads(Path(imported['artifact']['path']).read_text(encoding='utf-8'))
        assert Path(imported_document['inputArtifact']['path']).read_bytes() == data
        assert any('authenticity' in gap for gap in plan['plan']['coverageGaps'])
        analyzed = client.tool("trace_analyze", {"measurementId": imported["id"]})
        finding = analyzed["report"]["hypotheses"][0]
        assert finding["verdict"] == "supportedPrediction" and finding["matchedSamples"] == 3
        assert analyzed["report"]["transitionCount"] == 4
        assert analyzed["report"]["nativeQualification"] is False
        assert analyzed['report']['acquisitionContext'] == {**{field: measurement[field] for field in ('mode', 'clock', 'timeUnit', 'programIdentity')}, 'identityVerified': False}
        assert analyzed['report']['clockSynchronizationVerified'] is False
        watch_validators['analysis'].validate(analyzed['report'])
        report["steps"].append("normalized synthetic import and independent event assertions")
        def artifact(name, data):
            path = output / name
            path.write_bytes(data)
            return {"path": str(path), "sha256": hashlib.sha256(data).hexdigest()}
        # Separately saved source with explicit SCL dependencies and parameter actuals.
        automatic_raw = base64.b64decode(samples["automaticReviewArtifactBase64"], validate=True)
        automatic_review_id = hashlib.sha256(automatic_raw).hexdigest()
        assert automatic_review_id == samples["automaticRequest"]["reviewId"]
        import_review(automatic_raw)
        automatic = client.tool("diagnostic_plan", {"request": samples["automaticRequest"]})
        resolved = {tuple(s["memberPath"]) for s in automatic["plan"]["signals"]}
        assert {("Conv2", "RunCmd"), ("Conv2", "Enable"), ("Enable2",), ("Final2",)} <= resolved
        assert ("Conv1", "RunCmd") not in resolved
        report["steps"].append("automatic dependency and actual binding selection with exact instance")
        readback = copy.deepcopy(samples["trace-readback.v1"])
        readback["tracePlanId"] = trace["id"]
        readback["evidenceArtifacts"] = [artifact("synthetic-readback-proof.txt", b"Authored fixture only; no native evidence")]
        checked = client.tool("trace_check_readback", {"tracePlanId": trace["id"], "artifact": artifact("synthetic-readback.json", json.dumps(readback).encode())})
        assert checked["readbackCheck"]["matchesSuppliedReadback"] is True
        assert checked["readbackCheck"]["nativeQualification"] is False
        readback["limits"]["freeSlotsBefore"] = 0
        mismatch = client.tool("trace_check_readback", {"tracePlanId": trace["id"], "artifact": artifact("synthetic-exhausted-readback.json", json.dumps(readback).encode())})
        assert mismatch["readbackCheck"]["matchesSuppliedReadback"] is False
        report["steps"].append("supplied readback comparison and exhausted-slot mismatch without native probe")
        ids = list(measurement["units"])
        buffer = io.StringIO(newline="")
        writer = csv.writer(buffer, delimiter=";")
        writer.writerow(["time"] + ids)
        for row in measurement["samples"]:
            writer.writerow([row["time"]] + [int(row["values"][key]) if isinstance(row["values"][key], bool) else row["values"][key] for key in ids])
        csv_options = {key: copy.deepcopy(measurement[key]) for key in ("timeUnit", "mode", "clock", "programIdentity", "quality")}
        csv_options.update(delimiter=";", decimalSeparator=".", headerRecord=0, timeColumn="time",
                           columns=[{"signalId": key, "column": key, "unit": measurement["units"][key]} for key in ids])
        csv_bytes = buffer.getvalue().encode(); committed_csv = upload(csv_bytes, 'measurement.csv')
        csv_import = client.tool("trace_read", {"tracePlanId": trace["id"], "uploadRef": committed_csv['artifactRef'], "csvOptions": csv_options})
        client.tool('artifact_upload_release', dict(uploadId=committed_csv['uploadId']))
        comparison = client.tool("trace_compare", {"beforeMeasurementId": imported["id"], "afterMeasurementId": csv_import["id"], "options": samples["comparisonOptions"]})
        assert comparison["comparison"]["bugFixProven"] is False
        for row in comparison["comparison"]["signals"]:
            assert row["before"] == row["after"]
        report["steps"].append("explicit CSV mapping and exact capture comparison without resampling")
        changed = copy.deepcopy(measurement)
        changed['programIdentity'] = 'Another supplied program, same saved source review'
        changed['clock'] = 'Separate unsynchronized host clock'
        changed['mode'] = 'polling'
        changed['quality']['atomicAcrossSignals'] = False
        changed['quality']['scanSynchronized'] = False
        committed = upload(json.dumps(changed).encode(), 'changed-program.json')
        changed_import = client.tool('trace_read', dict(tracePlanId=trace['id'], uploadRef=committed['artifactRef']))
        client.tool('artifact_upload_release', dict(uploadId=committed['uploadId']))
        compare_args = dict(beforeMeasurementId=imported['id'], afterMeasurementId=changed_import['id'], options=samples['comparisonOptions'])
        reject('trace_compare', compare_args, 'ProgramRevisionMismatch')
        reject('trace_compare', {**compare_args, 'options': {**samples['comparisonOptions'], 'allowChangedSource': False}}, 'ProgramRevisionMismatch')
        compare_args['options'] = {**samples['comparisonOptions'], 'allowChangedSource': True}
        changed_comparison = client.tool('trace_compare', compare_args)
        context_report = changed_comparison['comparison']
        watch_validators['comparison'].validate(context_report)
        assert context_report['sourceChanged'] is False and context_report['programIdentityChanged'] is True
        assert context_report['clockSynchronizationVerified'] is False and context_report['bugFixProven'] is False
        for key, original in [('beforeAcquisition', measurement), ('afterAcquisition', changed)]:
            assert context_report[key] == {**{field: original[field] for field in ('mode', 'clock', 'timeUnit', 'programIdentity')}, 'identityVerified': False}
        for key in ('beforeAcquisition', 'afterAcquisition'):
            invalid = copy.deepcopy(context_report); invalid[key]['identityVerified'] = True
            try: watch_validators['comparison'].validate(invalid)
            except ValidationError: pass
            else: raise AssertionError('Comparison contract accepted authenticated supplied identity')
        legacy = copy.deepcopy(context_report)
        for key in ('beforeAcquisition', 'afterAcquisition', 'programIdentityChanged', 'clockSynchronizationVerified'): legacy.pop(key)
        watch_validators['comparison'].validate(legacy)
        incomplete = copy.deepcopy(context_report); incomplete.pop('afterAcquisition')
        try: watch_validators['comparison'].validate(incomplete)
        except ValidationError: pass
        else: raise AssertionError('Comparison contract accepted one-sided acquisition context')
        hostile_request = copy.deepcopy(samples['diagnosticRequest'])
        hostile_request['report'] += '\nÜber conveyor | <img src="https://invalid.example/x" onerror="alert(1)"> **bold** [link](https://invalid.example) www.invalid.example test@invalid.example Ω'
        hostile_case = client.tool('diagnostic_plan', dict(request=hostile_request))
        hostile_trace = client.tool('trace_prepare', dict(planId=hostile_case['id'], request=samples['prepareRequest']))
        hostile_measurement = copy.deepcopy(measurement); hostile_measurement['tracePlanId'] = hostile_trace['id']
        hostile_import = client.tool('trace_read', dict(tracePlanId=hostile_trace['id'], artifact=artifact('hostile-report-measurement.json', json.dumps(hostile_measurement).encode())))
        hostile_analysis = client.tool('trace_analyze', dict(measurementId=hostile_import['id']))
        exports = {}
        for name, source in [('analysis', analyzed), ('comparison', changed_comparison), ('hostile', hostile_analysis)]:
            exports[name] = {}
            for format in ('json', 'markdown', 'html'):
                receipt = client.tool('diagnostic_report_export', dict(reportId=source['id'], format=format))
                watch_validators['reportExport'].validate(receipt)
                assert receipt['sourceReportId'] == source['id'] == receipt['sourceArtifact']['sha256']
                assert receipt['sourceArtifact'] == source['artifact']
                assert not receipt['nativeQualification'] and not receipt['sourceAuthenticityVerified'] and not receipt['analysisRecomputed']
                data = bytearray()
                while len(data) < receipt['artifact']['bytes']:
                    chunk = client.tool('artifact_read', dict(path=receipt['artifact']['path'], offset=len(data)))
                    data.extend(base64.b64decode(chunk['data'], validate=True))
                assert hashlib.sha256(data).hexdigest() == receipt['id']
                if format == 'json': assert receipt['id'] == source['id'] and bytes(data) == Path(source['artifact']['path']).read_bytes()
                else: assert b'Complete source report' in data and b'does not prove causality' in data
                exports[name][format] = receipt
        report['reportExports'] = exports
        for field, value in [('nativeQualification', True), ('sourceAuthenticityVerified', True), ('analysisRecomputed', True), ('mediaType', 'application/json')]:
            malformed = copy.deepcopy(exports['analysis']['html']); malformed[field] = value
            try: watch_validators['reportExport'].validate(malformed)
            except ValidationError: pass
            else: raise AssertionError('Report export contract accepted invalid ' + field)
        reject('diagnostic_report_export', dict(reportId=analyzed['id'], format='pdf'), 'InvalidRequest')
        reject('diagnostic_report_export', dict(reportId='../outside', format='html'), 'InvalidRequest')
        source_path = Path(analyzed['artifact']['path']); original = source_path.read_bytes(); source_path.write_bytes(original + b' ')
        reject('diagnostic_report_export', dict(reportId=analyzed['id'], format='html'), 'ArtifactChanged')
        source_path.write_bytes(original)
        restart()
        for formats in exports.values():
            for format, receipt in formats.items():
                assert client.tool('diagnostic_report_export', dict(reportId=receipt['sourceReportId'], format=format)) == receipt
        for replan in template_replans:
            assert client.tool('diagnostic_replan', dict(planId=replan['planId'], request=replan['request']))['id'] == replan['resultId']
        report['steps'].append('JSON/Markdown/HTML diagnostic exports preserve exact source, escaped content, download hashes and stable receipts across actual Host restart')
        assert client.tool('trace_compare', compare_args)['id'] == changed_comparison['id']
        downloaded = bytearray()
        while len(downloaded) < changed_comparison['artifact']['bytes']:
            chunk = client.tool('artifact_read', dict(path=changed_comparison['artifact']['path'], offset=len(downloaded)))
            downloaded.extend(base64.b64decode(chunk['data'], validate=True))
        assert hashlib.sha256(downloaded).hexdigest() == changed_comparison['id']
        assert json.loads(downloaded) == context_report
        analysis_bytes = client.tool('artifact_read', dict(path=analyzed['artifact']['path']))
        retained_analysis = json.loads(base64.b64decode(analysis_bytes['data'], validate=True))
        assert retained_analysis['acquisitionContext'] == context_report['beforeAcquisition']
        assert retained_analysis['clockSynchronizationVerified'] is False
        report['steps'].append('changed declared program admission and exact acquisition contexts survive upload release, Host restart and report download')
        nanoseconds = copy.deepcopy(measurement)
        nanoseconds["timeUnit"] = "ns"
        nanoseconds["quality"]["expectedInterval"] *= 1000000
        for row in nanoseconds["samples"]:
            row["time"] = row["time"] * 1000000 + 7
        nano_import = client.tool("trace_read", {"tracePlanId": trace["id"], "artifact": artifact("synthetic-nanoseconds.json", json.dumps(nanoseconds).encode())})
        nano_options = {**samples["comparisonOptions"], "afterAnchorSeconds": 0.020000007}
        nano_comparison = client.tool("trace_compare", {"beforeMeasurementId": imported["id"], "afterMeasurementId": nano_import["id"], "options": nano_options})
        for row in nano_comparison["comparison"]["signals"]:
            assert row["before"] == row["after"]
        nano_report = client.tool("trace_analyze", {"measurementId": nano_import["id"]})
        assert nano_report["report"]["timeUnit"] == "ns" and nano_report["report"]["hypotheses"][0]["matchedSamples"] == 3
        report["steps"].append("nanosecond import/analysis and exact seven-nanosecond comparison offset")
        tiny = copy.deepcopy(measurement)
        tiny['timeUnit'] = 's'; tiny['quality']['expectedInterval'] = 1e-28
        tiny['samples'] = [copy.deepcopy(measurement['samples'][0]), copy.deepcopy(measurement['samples'][2])]
        tiny['samples'][0]['time'] = 0; tiny['samples'][1]['time'] = 2e-28
        tiny_import = client.tool('trace_read', dict(tracePlanId=trace['id'], artifact=artifact('exact-gap.json', json.dumps(tiny).encode())))
        assert tiny_import['qualityAssessment']['detectedIntervalGaps'] == 1
        tiny_report = client.tool('trace_analyze', dict(measurementId=tiny_import['id']))['report']
        assert tiny_report['hypotheses'][0]['matchedSamples'] == 1
        assert tiny_report['hypotheses'][0]['verdict'] == 'unresolved'
        report['steps'].append('exact tiny interval gap prevents a supported prediction despite matching samples')
        large = copy.deepcopy(measurement)
        large['timeUnit'] = 's'; large['quality']['expectedInterval'] = 1
        for index, row in enumerate(large['samples']): row['time'] = 999999999999999995 + index
        large_import = client.tool('trace_read', dict(tracePlanId=trace['id'], artifact=artifact('exact-large-time.json', json.dumps(large).encode())))
        aligned_options = dict(reason='Exact alignment of supplied large timestamps', beforeAnchorSeconds=10**18,
                               afterAnchorSeconds=10**18, windowStartSeconds=-5, windowEndSeconds=0)
        aligned = client.tool('trace_compare', dict(beforeMeasurementId=large_import['id'], afterMeasurementId=large_import['id'], options=aligned_options))
        for row in aligned['comparison']['signals']:
            assert row['before']['samplesInWindow'] == 6 and row['before'] == row['after']
        for side in ('beforeAnchorSeconds', 'afterAnchorSeconds'):
            reject('trace_compare', dict(beforeMeasurementId=large_import['id'], afterMeasurementId=large_import['id'],
                                        options={**aligned_options, side: 1e-28}), 'MeasurementFormatUnsupported')
        report['steps'].append('large timestamp alignment retains six samples and refuses rounding on either capture')
        watch_validators['comparison'].validate(aligned['comparison'])
        watch_validators['comparisonOptions'].validate(aligned_options)
        for field, value in [('signalMap', None), ('signalMap', {}), ('allowChangedSource', None),
                             ('allowChangedSource', 'true'), ('signalMaps', []),
                             ('signalMap', [{'before': 'final', 'after': 'run', 'ignored': True}])]:
            bad_options = {**aligned_options, field: value}
            try: watch_validators['comparisonOptions'].validate(bad_options)
            except ValidationError: pass
            else: raise AssertionError('Comparison schema accepted invalid selection: ' + field)
            reject('trace_compare', dict(beforeMeasurementId=large_import['id'], afterMeasurementId=large_import['id'], options=bad_options), 'InvalidRequest')
        selected = client.tool('trace_compare', dict(beforeMeasurementId=large_import['id'], afterMeasurementId=large_import['id'],
                              options={**aligned_options, 'signalMap': [{'before': 'final', 'after': 'run'}]}))['comparison']
        watch_validators['comparison'].validate(selected)
        assert len(selected['signals']) == 1 and selected['signals'][0]['afterSignalId'] == 'run'
        assert selected['signals'][0]['sameStorage'] is False
        report['steps'].append('comparison schema and MCP refuse malformed selection while explicit cross-storage mapping remains available')
        mappings = []
        for signal in plan["plan"]["signals"]:
            member = list(signal["memberPath"])
            if member[0] == "Conv2": member[0] = "Conv1"
            mappings.append({"id": signal["id"], "storagePath": signal["storagePath"], "memberPath": member, "reason": "Explicit synthetic fixture instance mapping"})
        replan_request = {"reviewId": review_id, "blockPath": samples["diagnosticRequest"]["blockPath"],
                          "instance": {**samples["diagnosticRequest"]["instance"], "memberPath": ["Conv1"]},
                          "reason": "Revalidate all fixture declarations", "signalMappings": mappings}
        reused = client.tool("diagnostic_replan", {"planId": plan["id"], "request": replan_request})
        assert reused["plan"]["selectedInstance"]["memberPath"] == ["Conv1"]
        assert reused["plan"]["lineage"]["previousPlanId"] == plan["id"]
        assert 'testSuiteEvidence' not in reused['plan']
        replan_request['testSuiteFinding'] = finding_request['testSuiteFinding']
        watch_validators['replanRequest'].validate(replan_request)
        rebound = client.tool('diagnostic_replan', dict(planId=plan['id'], request=replan_request))
        assert rebound['plan']['testSuiteEvidence'] == evidence
        report['steps'].append('replan keeps old finding in previous-case lineage unless explicitly selected and revalidated')
        report["steps"].append("explicit machine-instance reuse with declaration revalidation")
        (output / "additional-contract-samples.json").write_text(json.dumps({"trace-csv-options.v1": csv_options, "diagnostic-replan-request.v1": replan_request}, indent=2) + "\n", encoding="utf-8")
        reject('trace_read', dict(tracePlanId=trace['id']), 'InvalidRequest')
        reject('trace_read', dict(tracePlanId=trace['id'], artifact={'path':'unused','sha256':'0'*64},uploadRef='upload:'+'0'*32), 'InvalidRequest')
        bad = json.loads(raw); bad['documents'][0]['signatures']['document'] = '0'*64
        reject('project_snapshot_import', dict(uploadRef=upload(json.dumps(bad).encode())['artifactRef']), 'ArtifactChanged')
        reject('project_snapshot_import', dict(uploadRef=upload(b'{"schemaVersion":"one","schemaVersion":"two"}')['artifactRef']), 'InvalidSnapshot')
        pending = client.tool('artifact_upload_begin', dict(fileName='incomplete.json',expectedBytes=1,sha256=hashlib.sha256(b'x').hexdigest()))
        reject('project_snapshot_import', dict(uploadRef='upload:'+pending['uploadId']), 'UploadIncomplete')
        client.tool('artifact_upload_abort',dict(uploadId=pending['uploadId']))
        source_path = Path(retained_review['artifact']['path']); source_path.write_bytes(raw+b' ')
        reject('diagnostic_plan', dict(request=samples['diagnosticRequest']), 'ArtifactChanged')
        reject('diagnostic_watch_plan', watch_args, 'ArtifactChanged')
        source_path.write_bytes(raw)
        assert client.tool('project_snapshot_search',dict(reviewId=review_id,query='Conveyor'))['totalObservedHits'] > 0
        report['steps'].append('tampered source refused; exact restoration restores offline review')
        report["artifactIds"] = {"case": plan["id"], "watchPlan": watch['id'], "tracePlan": trace["id"], "import": imported["id"], "report": analyzed["id"]}
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
        raise
    finally:
        try:
            report["shutdown"] = close()
        except Exception as error:
            report["status"] = "failed"
            report["shutdownError"] = str(error) or type(error).__name__
        finally:
            (output / "result.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    assert report["status"] == "passed", report
    print("PASS Offline MCP diagnostic workflow; native workers deliberately absent.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--samples", type=Path, required=True)
    args = parser.parse_args()
    run(args.host, args.output, args.samples)
