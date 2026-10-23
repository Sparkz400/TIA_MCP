"""Independent checks of supplied alarm paths, executable declarations and MCP handoff."""
import base64
import copy
import hashlib
import json
import xml.etree.ElementTree as ET


def check_alarm_bindings(tool, import_review, restart, reject, samples, validators, output, report):
    checks = 0

    def require(value, message):
        nonlocal checks
        if not value:
            raise AssertionError(message)
        checks += 1

    raw = base64.b64decode(samples['alarmBindingReviewArtifactBase64'], validate=True)
    changed_raw = base64.b64decode(samples['alarmBindingChangedReviewArtifactBase64'], validate=True)
    original, changed_source = json.loads(raw), json.loads(changed_raw)
    before, after = import_review(raw), import_review(changed_raw)
    plc = '/projects/Fixture/plc/CPU'
    fb, db, io = (plc + '/blocks/' + name for name in ['FB_Conveyor', 'DB_Line', 'DB_IO'])

    def exported_type(review, path):
        document = next(d for d in review['documents'] if d['objectPath'] == path)
        require(hashlib.sha256(document['xml'].encode('utf-8')).hexdigest() == document['utf8Sha256'], 'original XML digest')
        xml = ET.fromstring(document['xml'])
        return [m.attrib['Datatype'] for m in xml.iter() if m.tag.split('}')[-1] == 'Member' and m.attrib.get('Name') == 'Enable']

    require(exported_type(original, fb) == ['Bool'] and exported_type(changed_source, fb) == ['Int'], 'independent FB breaking-member oracle')
    require(exported_type(original, db) == ['Bool', 'Bool'] and exported_type(changed_source, db) == ['Int', 'Int'], 'independent embedded DB oracle')
    request = samples['alarmBindingRequest']
    validators['caseRequest'].validate(request)
    case = tool('diagnostic_plan', dict(request=request))
    validators['case'].validate(case['plan'])
    bindings = case['plan']['alarmBindings']
    validators['alarmBindings'].validate(bindings)
    require(bindings['reviewId'] == before['reviewId'] and bindings['allSelectedMatched'], 'exact source and matched declarations')
    for field in ['nativeQualified', 'sourceAuthenticityVerified', 'runtimeAlarmAssociationVerified', 'coverageComplete']:
        require(bindings[field] is False, 'unsupported qualification: ' + field)
    oracle = [('condition', 'enable', 'Enable', db, ['Conv2', 'Enable'], 'selectedMemberOrAlias'),
              ('condition-actual', 'enableSource', 'Enable', io, ['Enable2'], 'actualParameter'),
              ('parameter', 'final', 'RunCmd', io, ['Final2'], 'actualParameter')]
    require(len(bindings['bindings']) == len(oracle), 'all explicit bindings retained')
    for row, (identity, signal, member, storage, path, origin) in zip(bindings['bindings'], oracle):
        require(row['id'] == identity and row['signalId'] == signal and row['status'] == 'capturedDeclarationMatch', 'binding identity/status')
        require(row['signal']['storagePath'] == storage and row['signal']['memberPath'] == path and row['signal']['datatype'] == 'Bool', 'independent storage oracle')
        require(row['declaration']['objectPath'] == fb and row['declaration']['datatype'] == 'Bool' and row['declaration']['xmlLine'] > 0, 'formal source declaration')
        node = next(n for n in original['model']['nodes'] if n['objectPath'] == row['operand']['objectPath'])
        require(row['operand'] == node, 'unaltered normalized original observation')
        validators['alarmOperand'].validate(node['metadata']['alarmOperand'])
        require(node['metadata']['alarmOperand']['memberPath'] == [member], 'formal member selection')
        for edge in row['relationships']:
            require(edge['edge'] == original['model']['edges'][edge['index']], 'original directed relationship')
        candidates = [c for c in row['storage']['candidates'] if c['storagePath'] == storage and c['memberPath'] == path and c['origin'] == origin]
        require(len(candidates) == 1 and not row['storage']['gaps'] and not row['storage']['truncated'], 'one exact candidate in this fixture')
        if origin == 'actualParameter':
            require(candidates[0]['parameterBinding']['parameter'] == member and candidates[0]['callSite']['objectPath'] == plc + '/blocks/FB_Line', 'captured caller and formal parameter')
    restart()
    watch = tool('diagnostic_watch_plan', dict(planId=case['id'], request=samples['watchRequest']))
    envelope = json.loads((output / 'trace-diagnostics' / (watch['id'] + '.json')).read_text(encoding='utf-8'))
    validators['envelope'].validate(envelope)
    require(envelope['alarmBindings'] == bindings, 'watch retains exact source association after restart')
    prepare = {**samples['prepareRequest'], 'reviewId': before['reviewId']}
    trace = tool('trace_prepare', dict(planId=case['id'], request=prepare))
    require(trace['specification']['alarmBindings'] == bindings and trace['specification']['nativeExecutable'] is False, 'trace handoff retains association without native eligibility')
    changed_request = samples['alarmBindingChangedRequest']
    changed = tool('diagnostic_plan', dict(request=changed_request))
    validators['case'].validate(changed['plan'])
    require(changed['plan']['alarmBindings']['bindings'][0]['status'] == 'datatypeMismatch' and not changed['plan']['alarmBindings']['allSelectedMatched'], 'breaking source member detected')
    require(changed['plan']['reviewId'] == after['reviewId'] != before['reviewId'], 'before and after source identities stay separate')
    reject('diagnostic_watch_plan', dict(planId=changed['id'], request=samples['watchRequest']), 'AlarmBindingUnresolved')
    reject('trace_prepare', dict(planId=changed['id'], request={**prepare, 'reviewId': after['reviewId']}), 'AlarmBindingUnresolved')
    require(tool('trace_prepare', dict(planId=case['id'], request=prepare))['id'] == trace['id'], 'old exact source stays independently usable')
    replan = dict(reviewId=before['reviewId'], blockPath=fb, instance=request['instance'], reason='Explicit retained source recheck.',
                  signalMappings=[dict(id=s['id'], storagePath=s['storagePath'], memberPath=s['memberPath'], reason='Explicit unchanged storage.') for s in case['plan']['signals']])
    next_case = tool('diagnostic_replan', dict(planId=case['id'], request=replan))
    require('alarmBindings' not in next_case['plan'] and 'alarmEvidence' not in next_case['plan'], 'old alarm associations stay only in predecessor lineage')
    replan.update(alarmContext=request['alarmContext'], alarmBindings=request['alarmBindings'])
    validators['replanRequest'].validate(replan)
    require(tool('diagnostic_replan', dict(planId=case['id'], request=replan))['plan']['alarmBindings']['allSelectedMatched'], 'explicit fresh reselection')
    controls = []
    for path, value in [(['alarmBindings', 'allSelectedMatched'], False),
                        (['alarmBindings', 'nativeQualified'], True),
                        (['alarmBindings', 'bindings', 0, 'declaration', 'xmlLine'], 999),
                        (['alarmBindings', 'bindings', 0, 'operand', 'metadata', 'rawLabel'], 'forged')]:
        modified = copy.deepcopy(case['plan']); target = modified
        for key in path[:-1]: target = target[key]
        target[path[-1]] = value
        data = json.dumps(modified, ensure_ascii=False).encode('utf-8'); identity = hashlib.sha256(data).hexdigest()
        with (output / 'trace-diagnostics' / (identity + '.json')).open('xb') as stream: stream.write(data)
        controls.append(dict(id=identity, field=path))
    restart()
    for control in controls:
        files = set((output / 'trace-diagnostics').iterdir())
        for name, args in [('diagnostic_watch_plan', samples['watchRequest']), ('trace_prepare', prepare), ('diagnostic_replan', replan)]:
            reject(name, dict(planId=control['id'], request=args), 'CaptureMismatch')
        require(set((output / 'trace-diagnostics').iterdir()) == files, 'rejected altered associations publish no artifact')
    report['alarmBindingWorkflow'] = dict(checks=checks, beforeReviewId=before['reviewId'], afterReviewId=after['reviewId'],
                                        caseId=case['id'], watchId=watch['id'], traceId=trace['id'], changedCaseId=changed['id'],
                                        mutationControls=controls, mutationRefusals=12, nativeQualified=False)
    report['steps'].append('normalized alarm operands: independent original paths/declarations/call arguments, watch/trace/replan, breaking-member gap, restart and rehashed mutation refusal')
