"""Closed qualification profile for native04's exact retained source archive.

All work here is offline. No project or library is opened and no native action
is replayed. The caller may start its ordinary fresh consumer only after this
preflight and fresh local archive/fixture copy succeed.
"""
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
from xml.parsers import expat

from qualification_owned_scratch import require

PINS = {
    'TiaMcpComponentGate': {'typeGuid':'f566f7e9-d5bc-415e-b552-4828e3b1027c', 'versionGuid':'e129ccee-1eaa-456b-b2f7-1581fcc3b6b9'},
    'TiaMcpComponentState': {'typeGuid':'35c9be87-892c-4ce6-a724-545ab1d41683', 'versionGuid':'eee41dd4-5137-48bb-a42b-d7fc64637eec'}}
POLICY = 'v21-fb-explicit-source-oracle-three-properties-v1'
ARCHIVE_SHA256 = '9d691a31d60863e7384928df09eb37345eb20bd791f18d919cf7960fbfc750cc'
FAILURE = 'No captured instance template covers the exact callee persistent declarations: Structured declarations and custom start values need their own independently observed storage profile.'


def guarded(path, root=None):
    result = Path(os.path.abspath(path))
    for item in (result, *result.parents):
        require(not item.exists() or not (item.is_symlink() or
                getattr(item.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT), 'Source reuse rejects reparse paths.')
    if root is not None:
        parent = guarded(root)
        require(result != parent and result.is_relative_to(parent), 'Source reuse path escapes the explicit artifact root.')
    return result


def file_record(path, root=None):
    path = guarded(path, root)
    require(path.is_file(), 'Required source-reuse input is missing: '+str(path))
    # None of this closed profile's inputs is a credential file. Reject any
    # unexpected sensitive store instead of reading it through a forged record.
    require(not any(any(word in part.lower() for word in ('credential','password','secret','umac','authentication'))
                    or part.lower().endswith(('.umc','.pfx','.p12','.key','.dpapi')) for part in path.parts),
            'Opaque credential material is outside source reuse.')
    with path.open('rb') as stream: digest=hashlib.file_digest(stream,'sha256').hexdigest()
    return {'path':str(path),'bytes':path.stat().st_size,'sha256':digest}


def validate_original_inputs(rows):
    for row in rows:
        require(file_record(row['path']) == row, 'Original source-reuse input changed: '+row['path'])


def exact_native_payload(raw):
    """Remove only the direct native DocumentInfo span; retain every other byte."""
    require(isinstance(raw,bytes), 'Exact storage comparison requires native XML bytes.')
    parser=expat.ParserCreate(namespace_separator='|')
    stack=[]; spans=[]; start=None
    def reject(*_):
        raise RuntimeError('DTD/entity declarations are outside exact storage evidence.')
    def opened(name,attributes):
        nonlocal start
        if not stack:
            require(name=='Document', 'Exact storage evidence requires the native Document root.')
        if stack==['Document'] and name=='DocumentInfo':
            require(start is None and not spans and attributes=={} and raw[parser.CurrentByteIndex:].startswith(b'<DocumentInfo>'),
                    'Require one exact native DocumentInfo envelope.')
            start=parser.CurrentByteIndex
        stack.append(name)
    def closed(name):
        nonlocal start
        require(stack and stack[-1]==name, 'Unbalanced exact storage XML.')
        if stack==['Document','DocumentInfo']:
            end=parser.CurrentByteIndex
            require(start is not None and raw[end:].startswith(b'</DocumentInfo>'), 'Unexpected native DocumentInfo serialization.')
            spans.append((start,end+len(b'</DocumentInfo>'))); start=None
        stack.pop()
    def metadata(*_):
        require(bool(stack), 'Outside-root metadata is outside exact storage evidence.')
    parser.StartElementHandler=opened; parser.EndElementHandler=closed
    parser.CommentHandler=metadata; parser.ProcessingInstructionHandler=metadata
    parser.StartDoctypeDeclHandler=reject; parser.EntityDeclHandler=reject; parser.ExternalEntityRefHandler=reject
    try: parser.Parse(raw,True)
    except expat.ExpatError as error: raise RuntimeError('Invalid exact native storage XML.') from error
    require(len(spans)==1 and not stack, 'Require one exact native DocumentInfo envelope.')
    begin,end=spans[0]
    return raw[:begin]+raw[end:]


def verify_exact_storage_reopen(after,reopened,key):
    """Additional closed-profile proof; generic preservation checks remain active."""
    rows=[]; payloads=[]
    for label,snapshot in (('afterInstall',after),('freshReopen',reopened)):
        require(key in snapshot['objects'], 'Exact captured storage DB missing after installation/reopen.')
        row=snapshot['objects'][key]; raw=Path(row['file']).read_bytes()
        require(hashlib.sha256(raw).hexdigest()==row['sha256'] and len(raw)==row['bytes'],
                'Retained exact storage snapshot bytes changed.')
        payload=exact_native_payload(raw); payloads.append(payload)
        rows.append({'phase':label,'path':row['file'],'sha256':row['sha256'],
                     'exactPayloadSha256':hashlib.sha256(payload).hexdigest()})
    require(payloads[0]==payloads[1], 'Exact captured storage XML changed on fresh reopen.')
    return {'policy':'native-documentinfo-span-only-raw-bytes-v1','object':key,'verified':True,'exports':rows}


def prepare_source_reuse(report_path, report_sha256, fixture_root, fixture, native, artifact_root,
                         verify_dependency_graph, verify_pinned_source_oracle):
    require(isinstance(report_sha256,str) and re.fullmatch('[0-9a-f]{64}',report_sha256) is not None,
            'Exact lowercase SHA256 of the reviewed native04 report is required.')
    report_info=file_record(report_path,artifact_root)
    require(report_info['sha256']==report_sha256, 'Retained source report hash differs.')
    report_bytes=Path(report_info['path']).read_bytes()
    require(hashlib.sha256(report_bytes).hexdigest()==report_sha256 and len(report_bytes)==report_info['bytes'],
            'Retained source report bytes changed before parsing.')
    previous=json.loads(report_bytes.decode('utf-8'))
    record=previous['failureStatus']['record']
    require(previous['status']=='failed' and previous['failure']=='change_prepare: InstanceCreationProfileUnavailable'
            and record['state']=='recovery_required' and record['graphical']=={}
            and record['failure']=={'code':'ComponentStageFailed','message':FAILURE},
            'Source reuse requires the exact failed native04 pre-storage eligibility receipt.')
    require(previous['sourceContentPolicyVerified'] is True and previous['sourceContentEvidenceAttempt']['verified'] is True
            and record['library']['result']['state']=='applied' and previous['productionApplySupported'] is False
            and previous['runtimeVerified'] is False and record['automaticReplayAllowed'] is False,
            'Retained exact source/import contract is not eligible.')
    require([event['stage'] for event in record['events']]==['created','library_prepared','library_dispatching',
        'library_applied','import_readback_captured_unverified','import_readback_verified',
        'pre_typed_readback_captured_unverified','graphical_prepare_failed'], 'Retained stage sequence differs; never resume/replay.')
    require(previous['fixture']==fixture and fixture['contentPolicyId']==POLICY and fixture['callerMode']=='createStandaloneFc'
            and fixture['root']=='TiaMcpComponentGate' and fixture['dependency']=='TiaMcpComponentState'
            and fixture['sourceLibraryName']=='ComponentQualification', 'Current fixture/content contract differs from native04.')
    plan=previous['plan']['preview']['integrationPlan']
    require(record['contract']['plan']==plan and previous['callerInitialState']['exists'] is False
            and previous['callerInitialState']['calledFromOb'] is False, 'Retained plan/caller evidence differs.')
    versions=plan['versions']
    verify_dependency_graph(versions,fixture)
    verify_dependency_graph(previous['authoredDependencyClosure'],fixture)
    require(len(versions)==len(PINS) and {v['typeName'] for v in versions}==set(PINS), 'Exact source closure is required.')
    inputs=[report_info]

    def pin_file(artifact):
        row=file_record(artifact['path'],artifact_root)
        require(row['sha256']==artifact['sha256'] and ('bytes' not in artifact or row['bytes']==artifact['bytes']), 'Pinned retained artifact changed.')
        inputs.append(row); return row

    archive=pin_file(previous['sourceArchive'])
    require(archive['sha256']==ARCHIVE_SHA256 and Path(archive['path']).suffix.lower()=='.zal21', 'Only native04 exact committed source archive is covered.')
    released={}
    for version in versions:
        name=version['typeName']; selected={key:version[key] for key in ('typeGuid','versionGuid')}
        require(selected==PINS[name] and version['state']=='Committed' and version['version']=='1.0.0', 'Exact native04 GUID/version tuple differs.')
        authored=[v for v in previous['authoredDependencyClosure'] if v['typeName']==name]
        require(len(authored)==1 and all(authored[0][key]==value for key,value in selected.items()), 'Authoring/released source identity differs.')
        raw=pin_file(version['artifact'])
        require(raw['sha256']==version['contentSha256'], 'Planned raw source hash differs.')
        observed=[v for v in record['preTypedEvidence']['versions'] if v['typeGuid']==selected['typeGuid'] and v['versionGuid']==selected['versionGuid']]
        require(len(observed)==1 and observed[0]['sourceContentSha256']==raw['sha256'], 'Last verified source observation differs.')
        pin_file(observed[0]['sourceArtifact'])
        if name==fixture['root']:
            require(previous['sourceContentEvidenceAttempt']['pin']==version and version['contentPolicy']['policyId']==POLICY,
                    'Verified source oracle pin differs from plan.')
            oracle=pin_file(version['contentPolicy']['sourceOracleArtifact'])
            require(oracle['sha256']==version['contentPolicy']['sourceOracleSha256']
                    and observed[0]['sourceOracleSha256']==oracle['sha256'], 'Full source oracle hash differs.')
            pin_file(observed[0]['sourceOracleArtifact'])
            require(observed[0]['contentVerification']['baseRemainingTreeMatches'] is True
                    and observed[0]['contentVerification']['oracleRemainingTreeMatches'] is True
                    and observed[0]['contentVerification']['nativeSettingsMatch'] is True,
                    'Original content-oracle verification is incomplete.')
            verify_pinned_source_oracle(Path(raw['path']).read_bytes(),Path(oracle['path']).read_bytes(),version,fixture)
        released[name]=selected
    # Bind original durable receipts without opening any retained project or
    # performing status/apply/restore against the failed operation.
    for path, expected_journal in ((previous['plan']['journalPath'],previous['plan']['journal']),
            (record['library']['prepared']['journalPath'],record['library']['result']['journal'])):
        journal=file_record(path,artifact_root)
        require(json.loads(Path(journal['path']).read_text(encoding='utf-8-sig'))==expected_journal,
                'Original durable plan/import journal differs from the retained receipt.')
        inputs.append(journal)
    native_original=guarded(previous['nativeArtifactRoot'],artifact_root)
    require(re.fullmatch('component-[0-9a-f]{32}',record['operationId']) is not None, 'Invalid retained component operation identity.')
    component_journal=file_record(native_original/'changes'/record['operationId']/'component-journal.json',artifact_root)
    require(json.loads(Path(component_journal['path']).read_text(encoding='utf-8-sig'))['record']==record,
            'Original durable component journal differs from the retained failure receipt.')
    inputs.append(component_journal)
    failed_preparations=[]
    for path in sorted((native_original/'changes').glob('change-*/journal.json')):
        guarded(path,artifact_root)
        journal=json.loads(path.read_text(encoding='utf-8-sig'))
        if journal.get('kind')=='graphical_typed_edit' and journal.get('state')=='prepare_failed' and journal.get('failure')==FAILURE:
            require(journal['projectPath']==previous['consumerScratch']['project']['path'] and journal['stages']==[]
                    and journal['idempotencyKey'] is None and journal['recoveryArchive'] is None,
                    'Failed typed preparation unexpectedly dispatched or belongs to another project.')
            failed_preparations.append(file_record(path,artifact_root))
    require(len(failed_preparations)==1,'Require one original failed typed-preparation journal.')
    inputs.extend(failed_preparations)
    checkpoint=record['initialCheckpoint']
    require(checkpoint['changeId']==record['library']['prepared']['changeId'], 'Original first checkpoint identity differs.')
    checkpoint_file=file_record(checkpoint['archivePath'],artifact_root)
    require(checkpoint_file['sha256']==checkpoint['sha256'], 'Original first checkpoint changed.'); inputs.append(checkpoint_file)
    fixture_root=guarded(fixture_root)
    current=[]
    for path in sorted(fixture_root.rglob('*')):
        guarded(path)
        if path.is_file():
            row=file_record(path); inputs.append(row)
            current.append({'name':path.relative_to(fixture_root).as_posix(),'sha256':row['sha256']})
    require(sorted(current,key=lambda row:row['name'])==sorted(previous['fixtureInputs'],key=lambda row:row['name']),
            'Current complete fixture files differ from the retained native04 contract.')
    inputs=list({row['path']:row for row in inputs}.values())
    validate_original_inputs(inputs)
    native=guarded(native,artifact_root)
    require(native.is_dir(), 'Fresh owned native directory required.')
    directory=guarded(native/'reused-source',native)
    documents=guarded(native/'documents',native)
    require(not directory.exists() and not documents.exists(), 'Source reuse cannot replace existing staged inputs.')
    directory.mkdir()
    destination=directory/'published.zal21'
    with Path(archive['path']).open('rb') as source, destination.open('xb') as target:
        shutil.copyfileobj(source,target)
    copied=file_record(destination,native)
    require(copied['sha256']==archive['sha256'] and copied['bytes']==archive['bytes'], 'Fresh archive copy differs.')
    shutil.copytree(fixture_root,documents,copy_function=shutil.copy2)
    copied_fixture=[{'name':path.relative_to(documents).as_posix(),'sha256':file_record(path,native)['sha256']}
                    for path in sorted(documents.rglob('*')) if path.is_file()]
    require(sorted(copied_fixture,key=lambda row:row['name'])==sorted(current,key=lambda row:row['name']), 'Fresh fixture copy differs.')
    validate_original_inputs(inputs)
    provenance={'mode':'reuse_exact_native04_source_new_consumer','sourceReport':report_info,'sourceAttemptStatus':'failed',
        'originalArchive':archive,'localArchive':copied,'versions':copy.deepcopy(versions),
        'dependencyClosure':copy.deepcopy(previous['authoredDependencyClosure']),'fixtureInputs':copied_fixture,
        'originalInputFiles':inputs,'authoringExecuted':False,'oldConsumerOpened':False,'oldOperationReplayed':False,
        'firstCheckpointRule':'The new consumer pre-import checkpoint is the sole installation recovery anchor; original native04 checkpoint is never retrieved by this mode.'}
    return copied,released,provenance
