"""Actual offline component/HMI MCP intake, strict schemas, retention and restart checks."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError
from smoke_mcp import Client, ROOT
from hmi_derived_effect_checks import verify as verify_derived, negative_controls
from hmi_library_association_checks import verify as verify_library, negative_controls as library_controls
from hmi_dependency_association_checks import verify as verify_dependencies, negative_controls as dependency_controls
from hmi_dependency_impact_checks import verify as verify_impacts, negative_controls as impact_controls
from hmi_text_list_author_checks import run as author_text_lists, reopen as reopen_text_lists


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--fixture', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    packet = fixture['packet']
    schema = json.loads((ROOT / 'contracts/hmi-component-review.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    validate(packet, 'request')
    validate(fixture['install'], 'request')
    validate(fixture['consumerPacket'], 'request')
    validate(fixture['consumerInstall'], 'request')
    validate(fixture['effectPacket'], 'request')
    check(True, 'Both install/update requests satisfy strict schema')
    for change in ('extra', 'ownership', 'complete', 'limit', 'install-prior'):
        mutated = copy.deepcopy(packet)
        if change == 'extra': mutated['nativeQualified'] = True
        elif change == 'ownership': mutated['outputs'][0]['ownership'] = 'automatic'
        elif change == 'complete': mutated['outputs'][0]['currentComplete'] = 'true'
        elif change == 'limit': mutated['outputs'] *= 257
        else: mutated['operation'] = 'install'
        try: validate(mutated, 'request')
        except ValidationError: check(True, 'Schema refuses ' + change)
        else: raise AssertionError('Schema accepted ' + change)
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-worker.exe'
    def close(client):
        closed = client.close()
        report['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native workers')
    def start():
        client = Client(args.host.resolve(), 30, ['--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        try:
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'hmi_component-review-offline', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def upload(client, data):
        status = client.tool('artifact_upload_begin', {'fileName': 'supplied.json', 'expectedBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', {'uploadId': status['uploadId'], 'offset': offset, 'data': base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()})
        return client.tool('artifact_upload_commit', {'uploadId': status['uploadId']})
    def read(client, reference):
        chunks, offset = [], 0
        while True:
            part = client.tool('artifact_read', {'path': reference['path'], 'offset': offset, 'count': 65536})
            raw = base64.b64decode(part['data'], validate=True)
            assert part['sha256'] == reference['sha256'] and part['offset'] == offset and len(raw) == part['count']
            chunks.append(raw)
            offset = part['nextOffset']
            if part['complete']: break
        raw = b''.join(chunks)
        check(hashlib.sha256(raw).hexdigest() == reference['sha256'], 'Downloaded artifact exact digest')
        return raw
    def refuse(client, tool, arguments, code):
        response = client.request('tools/call', {'name': tool, 'arguments': arguments})
        error = response['structuredContent']['error']
        check(response.get('isError') and error['code'] == code and error['nativeAccessAttempted'] is False, 'Structured offline refusal ' + code)
    incoming = []
    def retain_upload(client, data):
        item = upload(client, data)
        incoming.append(item)
        return item['artifactRef']
    def review(client, request):
        data = (json.dumps(request, ensure_ascii=False) + '\n').encode('utf-8')
        receipt = client.tool('hmi_component_review', {'uploadRef': retain_upload(client, data)})
        validate(receipt, 'receipt')
        check(read(client, receipt['input']) == data, 'Original request bytes preserved')
        record = json.loads(read(client, receipt['reportArtifact']))
        validate(record, 'record')
        if request['schemaVersion'] in ('hmi-component-review-request.v4','hmi-component-review-request.v5','hmi-component-review-request.v6','hmi-component-review-request.v7'):
            originals={f['id']:read(client,f['artifact']) for f in record['report']['files']}
            verify_derived(record,request,originals)
            check(True,'Independent DOM oracle verifies original expected-effect inputs')
        return receipt, record
    def pages(client, receipt, record):
        sections = ['components', 'outputs', 'bindings', 'files']
        if record['report']['schemaVersion'] in ('hmi-component-review.v2', 'hmi-component-review.v3', 'hmi-component-review.v4', 'hmi-component-review.v5', 'hmi-component-review.v6','hmi-component-review.v7'): sections += ['consumerPaths', 'consumerCoverage']
        if record['report']['schemaVersion'] in ('hmi-component-review.v3', 'hmi-component-review.v4', 'hmi-component-review.v5', 'hmi-component-review.v6','hmi-component-review.v7'): sections += ['effectChecks']
        if record['report']['schemaVersion'] in ('hmi-component-review.v4', 'hmi-component-review.v5', 'hmi-component-review.v6','hmi-component-review.v7'): sections += ['derivedEffects','expectationCoverage']
        if record['report']['schemaVersion'] in ('hmi-component-review.v5','hmi-component-review.v6','hmi-component-review.v7'): sections += ['libraryAssociations','libraryCoverage']
        if record['report']['schemaVersion'] in ('hmi-component-review.v6','hmi-component-review.v7'): sections += ['dependencyAssociations','dependencyCoverage','dependencyIdentities']
        if record['report']['schemaVersion'] == 'hmi-component-review.v7': sections += ['dependencyImpacts','dependencyImpactCoverage']
        for section in sections:
            rows, offset = [], 0
            while True:
                page = client.tool('hmi_component_review_page', {'recordId': receipt['recordId'], 'section': section, 'offset': offset, 'count': 1})
                validate(page, 'page')
                rows.extend(row['value'] for row in page['rows'])
                if page['nextOffset'] is None: break
                offset = page['nextOffset']
            check(rows == record['report'][section], 'Paged ' + section + ' matches retained report')
    try:
        client = start()
        try:
            tools = {t['name']: t for t in client.request('tools/list')['tools']}
            check(tools['hmi_component_review']['annotations']['readOnlyHint'] is False and tools['hmi_component_review_page']['annotations']['readOnlyHint'] is True, 'Both tools expose correct read-only annotations')
            for review_id in dict.fromkeys(fixture['reviewIds'] + fixture['consumerReviewIds']):
                data = (Path(fixture['artifactRoot']) / 'project-reviews' / (review_id + '.json')).read_bytes()
                source = client.tool('project_snapshot_import', {'uploadRef': retain_upload(client, data)})
                check(source['reviewId'] == review_id, 'Exact saved component/destination review imports')
            for file in packet['files']:
                data = base64.b64decode(fixture['files'][file['id']], validate=True)
                file['uploadRef'] = retain_upload(client, data)
            receipt, record = review(client, packet)
            check(receipt == client.tool('hmi_component_review', {'uploadRef': incoming[-1]['artifactRef']}), 'Duplicate exact request is idempotent')
            check(record['report']['summary']['conflictCount'] == 0 and record['report']['summary']['componentComparisonCount'] == 2, 'Explicit multi-object component update compared')
            check(all(record['report'][flag] is False for flag in ('nativeQualified', 'nativeExecutable', 'wholeProjectPreservationVerified', 'componentMemberCoverageComplete', 'consumerCoverageComplete')), 'No compatibility, coverage or preservation promotion')
            pages(client, receipt, record)
            install = copy.deepcopy(fixture['install'])
            install['files'] = copy.deepcopy(packet['files'])
            install_receipt, install_record = review(client, install)
            check(install_record['report']['summary']['gapCount'] >= 2 and install_record['report']['operation'] == 'install', 'Installation records missing native identity and port evidence')
            changed = copy.deepcopy(packet)
            changed['afterComponent']['reviewId'] = fixture['changedReviewId']
            changed['outputs'][0]['ownership'] = 'manual'
            changed['outputs'][0]['current'] = ['proposed-xml', 'current-script']
            changed['bindings'][0]['afterMember'] = None
            conflict_receipt, conflict_record = review(client, changed)
            check(conflict_record['report']['summary']['conflictCount'] == 3 and conflict_record['report']['summary']['changedInterfaceCount'] == 2, 'Both changed interfaces, removed member, independent byte drift and manual ownership survive public workflow')
            for file in record['report']['files']:
                check(read(client, file['artifact']) == base64.b64decode(fixture['files'][file['id']]), 'Original independent file exact: ' + file['id'])
            adversary = copy.deepcopy(record)
            adversary['report']['nativeQualified'] = True
            try: validate(adversary, 'record')
            except ValidationError: check(True, 'Output schema refuses native qualification')
            else: raise AssertionError('Native qualification accepted')
            refuse(client, 'hmi_component_review_page', {'recordId': receipt['recordId'], 'section': 'files', 'offset': 100}, 'InvalidHmiComponentReview')
            for data in (b'{"x":1,"x":2}', b'\xff', b'{"schemaVersion":"unknown"}'):
                ref = retain_upload(client, data)
                response = client.request('tools/call', {'name': 'hmi_component_review', 'arguments': {'uploadRef': ref}})
                check(response.get('isError') and response['structuredContent']['error']['nativeAccessAttempted'] is False, 'Malformed request refuses without native access')
            bad = copy.deepcopy(packet)
            bad['files'][0]['sha256'] = '0' * 64
            refuse(client, 'hmi_component_review', {'uploadRef': retain_upload(client, json.dumps(bad).encode())}, 'ArtifactChanged')
            consumer_packet = copy.deepcopy(fixture['consumerPacket'])
            consumer_packet['files'] = copy.deepcopy(packet['files'])
            consumer_receipt, consumer_record = review(client, consumer_packet)
            paths = consumer_record['report']['consumerPaths']
            check({p['scope'] for p in paths} == {'beforeComponent', 'afterComponent', 'destination'}, 'All three independent saved scopes supply observed consumer context')
            check(sum(p['scope'] == 'beforeComponent' and p['consumerObjectPath'].endswith('/screens/Status') and p['rootObjectPath'].endswith('/blocks/Component') for p in paths) == 2, 'Independent diamond edges survive public artifact retention')
            check(not any(p['scope'] == 'afterComponent' and p['rootObjectPath'].endswith('/blocks/Component') for p in paths), 'No before/after edge joining through same object paths')
            check(all(len(set(p['objectPaths'])) == len(p['objectPaths']) for p in paths), 'Cycles cannot repeat consumer identities')
            pages(client, consumer_receipt, consumer_record)
            bounded = copy.deepcopy(consumer_packet)
            bounded['impactLimits']['maxImpactPaths'] = 1
            bounded_receipt, bounded_record = review(client, bounded)
            check(bounded_record['report']['summary']['truncatedConsumerScopes'] > 0, 'Explicit path budget exposes missing consumer coverage')
            consumer_install = copy.deepcopy(fixture['consumerInstall'])
            consumer_install['files'] = copy.deepcopy(packet['files'])
            new_receipt, new_record = review(client, consumer_install)
            check(new_record['report']['summary']['consumerScopeCount'] == 2 and new_record['report']['consumerCoverage'][1]['excludedOutputIds'] == ['run'], 'Install supplies no prior component or invented created identity')
            refuse(client, 'hmi_component_review_page', {'recordId': receipt['recordId'], 'section': 'consumerPaths'}, 'InvalidHmiComponentReview')
            for mode in ('complete', 'envelope-version'):
                bad_record = copy.deepcopy(consumer_record)
                if mode == 'complete': bad_record['report']['consumerCoverage'][0]['traversal']['complete'] = True
                else: bad_record['schemaVersion'] = 'hmi-component-review-record.v1'
                try: validate(bad_record, 'record')
                except ValidationError: check(True, 'Schema refuses invented consumer claim: ' + mode)
                else: raise AssertionError('Invented consumer claim accepted')
            invalid_limits = copy.deepcopy(consumer_packet)
            invalid_limits['impactLimits']['maxImpactEdgeVisits'] = 20001
            try: validate(invalid_limits, 'request')
            except ValidationError: check(True, 'Schema refuses oversized consumer traversal')
            else: raise AssertionError('Traversal limit accepted')
            refuse(client, 'hmi_component_review', {'uploadRef': retain_upload(client, json.dumps(invalid_limits).encode())}, 'InvalidHmiComponentReview')
            effect_packet = copy.deepcopy(fixture['effectPacket'])
            for file in effect_packet['files']:
                file['uploadRef'] = retain_upload(client, base64.b64decode(fixture['files'][file['id']], validate=True))
            effect_receipt, effect_record = review(client, effect_packet)
            check(effect_record['report']['summary']['effectMatchCount'] == 1, 'Independent supplied post-operation files match reviewed proposal')
            check(effect_record['report']['effectChecks'][0]['nativeEffectVerified'] is False, 'Observed byte match grants no native qualification')
            pages(client, effect_receipt, effect_record)
            effect_bad = copy.deepcopy(effect_packet)
            effect_bad['observations'][0]['files'] = ['current-xml']
            effect_bad_receipt, effect_bad_record = review(client, effect_bad)
            check({f['status'] for f in effect_bad_record['report']['effectChecks'][0]['files']} == {'different', 'missing'}, 'Changed and missing post-operation files independently detected')
            effect_missing = copy.deepcopy(effect_packet)
            effect_missing['observations'] = []
            effect_missing_receipt, effect_missing_record = review(client, effect_missing)
            check(effect_missing_record['report']['summary']['effectIncompleteCount'] == 1, 'Missing observation remains incomplete')
            for old in (receipt, consumer_receipt):
                refuse(client, 'hmi_component_review_page', {'recordId': old['recordId'], 'section': 'effectChecks'}, 'InvalidHmiComponentReview')
            for defect in ('complete', 'extra', 'duplicate', 'missing'):
                invalid = copy.deepcopy(effect_packet)
                if defect == 'complete': invalid['observations'][0]['complete'] = 'true'
                elif defect == 'extra': invalid['observations'][0]['nativeEffectVerified'] = True
                elif defect == 'duplicate': invalid['observations'][0]['files'] *= 2
                else: del invalid['observations']
                try: validate(invalid, 'request')
                except ValidationError: check(True, 'Strict observation schema refuses ' + defect)
                else: raise AssertionError('Invalid observation passed schema')
                refuse(client, 'hmi_component_review', {'uploadRef': retain_upload(client, json.dumps(invalid).encode())}, 'InvalidHmiComponentReview')
            derived_packet=copy.deepcopy(fixture['derivedPacket'])
            for file in derived_packet['files']:
                file['uploadRef']=retain_upload(client,base64.b64decode(fixture['files'][file['id']],validate=True))
            validate(derived_packet,'request')
            derived_receipt,derived_record=review(client,derived_packet)
            check(derived_record['report']['derivedEffects'][0]['proposal']['status']=='match','Proposal matches independently derived screen expectation')
            originals={f['id']:read(client,f['artifact']) for f in derived_record['report']['files']}
            report['derivedMutationControls']=negative_controls(derived_record,derived_packet,originals)
            derived_records=[(derived_receipt,derived_record)]
            for label,data in [('changed',originals['screen-proposed'].replace(b'<Name>Overview_A</Name>',b'<Name>Other</Name>')),('invalid',b'<Document>')]:
                changed=copy.deepcopy(derived_packet)
                changed['files'].append(dict(id='independent-change',fileName='screen.xml',sha256=hashlib.sha256(data).hexdigest(),bytes=len(data),uploadRef=retain_upload(client,data)))
                changed['outputs'][0]['proposed']=['independent-change','proposed-script']
                changed['observations'][0]['files']=['independent-change','observed-script']
                result,full=review(client,changed);derived_records.append((result,full))
                check(full['report']['effectChecks'][0]['status']=='match','Equal supplied files alone cannot establish intended effects: '+label)
                check(full['report']['derivedEffects'][0]['proposal']['status']==('different' if label=='changed' else 'invalidDocument'),'Independent expected document detects '+label)
            missing_derived=copy.deepcopy(derived_packet);missing_derived['observations']=[]
            derived_records.append(review(client,missing_derived))
            for old in (receipt,consumer_receipt,effect_receipt):
                refuse(client,'hmi_component_review_page',dict(recordId=old['recordId'],section='derivedEffects'),'InvalidHmiComponentReview')
            for defect in ['profile','extra','empty','source']:
                invalid=copy.deepcopy(derived_packet)
                if defect=='profile':invalid['expectations'][0]['profile']='all-hmi'
                elif defect=='extra':invalid['expectations'][0]['nativeQualified']=True
                elif defect=='empty':invalid['expectations']=[]
                else:invalid['expectations'][0]['sourceFileId']='screen-proposed'
                if defect!='source':
                    try:validate(invalid,'request')
                    except ValidationError:check(True,'Schema refuses expected-effect '+defect)
                    else:raise AssertionError('Expected-effect schema accepted '+defect)
                refuse(client,'hmi_component_review',dict(uploadRef=retain_upload(client,json.dumps(invalid).encode())),'InvalidHmiComponentReview')
            report['derivedRecords']=[r['recordId'] for r,_ in derived_records]
            library_fixture=fixture['libraryFixture']; library_reviews={}
            for review_id in library_fixture['reviewIds']:
                data=(Path(library_fixture['artifactRoot'])/'project-reviews'/(review_id+'.json')).read_bytes()
                imported=client.tool('project_snapshot_import',dict(uploadRef=retain_upload(client,data)))
                check(imported['reviewId']==review_id,'Exact library observation review imports')
                library_reviews[review_id]=json.loads(data)
            library_records=[]
            for candidate in library_fixture['packets']:
                candidate=copy.deepcopy(candidate)
                for file in candidate['files']:file['uploadRef']=retain_upload(client,base64.b64decode(library_fixture['files'][file['id']],validate=True))
                validate(candidate,'request')
                result,full=review(client,candidate);library_records.append((result,full))
                originals={f['id']:read(client,f['artifact']) for f in full['report']['files']}
                verify_library(full,candidate,originals,library_reviews)
                check(True,'Independent original export and saved-object comparison')
            library_request=json.loads(read(client,library_records[0][0]['input']))
            library_full=library_records[0][1]
            report['libraryMutationControls']=library_controls(library_full,library_request,{f['id']:read(client,f['artifact']) for f in library_full['report']['files']},library_reviews)
            combined=copy.deepcopy(derived_packet);combined['schemaVersion']='hmi-component-review-request.v5'
            combined['afterComponent']=copy.deepcopy(library_request['afterComponent']);combined['componentMatches']=[];combined['bindings']=[]
            combined['libraryAssociations']=copy.deepcopy(library_request['libraryAssociations']);combined['files']+=copy.deepcopy(library_request['files'])
            result,full=review(client,combined);library_records.append((result,full))
            verify_library(full,combined,{f['id']:read(client,f['artifact']) for f in full['report']['files']},library_reviews)
            check(full['report']['derivedEffects'][0]['proposal']['status']=='match','Existing expected effects compose with explicit library associations')
            for old in (receipt,consumer_receipt,effect_receipt,derived_receipt):
                refuse(client,'hmi_component_review_page',dict(recordId=old['recordId'],section='libraryAssociations'),'InvalidHmiComponentReview')
            for defect in ['profile','extra','empty','scope','path','file']:
                invalid=copy.deepcopy(library_request)
                if defect=='profile':invalid['libraryAssociations'][0]['profile']='generic'
                elif defect=='extra':invalid['libraryAssociations'][0]['nativeQualified']=True
                elif defect=='empty':invalid['libraryAssociations']=[]
                elif defect=='scope':invalid['libraryAssociations'][0]['scope']='destination'
                elif defect=='path':invalid['libraryAssociations'][0]['objectPath']='/wrong'
                else:invalid['libraryAssociations'][0]['fileId']='wrong'
                if defect not in ['path','file']:
                    try:validate(invalid,'request')
                    except ValidationError:check(True,'Schema refuses library association '+defect)
                    else:raise AssertionError('Library schema accepted '+defect)
                refuse(client,'hmi_component_review',dict(uploadRef=retain_upload(client,json.dumps(invalid).encode())),'InvalidHmiComponentReview')
            report['libraryRecords']=[r['recordId'] for r,_ in library_records]
            # Earlier reviews own their retained inputs; release transient uploads before the next batch.
            for item in incoming:
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
            incoming.clear()
            dependency_fixture=fixture['dependencyFixture'];dependency_reviews={}
            for review_id in dependency_fixture['reviewIds']:
                data=(Path(dependency_fixture['artifactRoot'])/'project-reviews'/(review_id+'.json')).read_bytes()
                imported=client.tool('project_snapshot_import',dict(uploadRef=retain_upload(client,data)))
                check(imported['reviewId']==review_id,'Exact dependency observation review imports')
                dependency_reviews[review_id]=json.loads(data)
            dependency_records=[]
            for candidate in dependency_fixture['packets']:
                candidate=copy.deepcopy(candidate)
                for file in candidate['files']:file['uploadRef']=retain_upload(client,base64.b64decode(dependency_fixture['files'][file['id']],validate=True))
                validate(candidate,'request')
                result,full=review(client,candidate);dependency_records.append((result,full))
                verify_dependencies(full,candidate,{f['id']:read(client,f['artifact']) for f in full['report']['files']},dependency_reviews)
                check(True,'Independent dependency indices, original exports, exact objects and coverage agree')
            dependency_request=json.loads(read(client,dependency_records[0][0]['input']))
            dependency_full=dependency_records[0][1]
            report['dependencyMutationControls']=dependency_controls(dependency_full,dependency_request,{f['id']:read(client,f['artifact']) for f in dependency_full['report']['files']},dependency_reviews)
            for old in (receipt,consumer_receipt,effect_receipt,derived_receipt,library_records[0][0]):
                refuse(client,'hmi_component_review_page',dict(recordId=old['recordId'],section='dependencyAssociations'),'InvalidHmiComponentReview')
            for defect in ['extra','scope','target','index','duplicate','oldVersion']:
                invalid=copy.deepcopy(dependency_request);row=invalid['dependencyAssociations'][0]
                if defect=='extra':row['nativeQualified']=True
                elif defect=='scope':row['scope']='destination'
                elif defect=='target':row['targetObjectPath']='RenamedInstance'
                elif defect=='index':row['dependencyIndex']=1
                elif defect=='duplicate':invalid['dependencyAssociations'].append(copy.deepcopy(row))
                else:invalid['schemaVersion']='hmi-component-review-request.v5'
                refuse(client,'hmi_component_review',dict(uploadRef=retain_upload(client,json.dumps(invalid).encode())),'InvalidHmiComponentReview')
            report['dependencyRecords']=[r['recordId'] for r,_ in dependency_records]
            for item in incoming: client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
            incoming.clear()
            impact_fixture=fixture['dependencyImpactFixture'];impact_reviews=dict(dependency_reviews)
            for review_id in impact_fixture['reviewIds']:
                data=(Path(impact_fixture['artifactRoot'])/'project-reviews'/(review_id+'.json')).read_bytes()
                imported=client.tool('project_snapshot_import',dict(uploadRef=retain_upload(client,data)))
                check(imported['reviewId']==review_id,'Exact impact consumer review imports')
                impact_reviews[review_id]=json.loads(data)
            impact_records=[]
            for candidate in impact_fixture['packets']:
                candidate=copy.deepcopy(candidate)
                for file in candidate['files']:file['uploadRef']=retain_upload(client,base64.b64decode(impact_fixture['files'][file['id']],validate=True))
                validate(candidate,'request')
                result,full=review(client,candidate);impact_records.append((result,full))
                originals={f['id']:read(client,f['artifact']) for f in full['report']['files']}
                verify_impacts(full,candidate,originals,impact_reviews)
                pages(client,result,full)
                check(True,'Independent dependency-to-output references and raw output effects agree')
            report['impactMutationControls']=impact_controls(impact_records[0][1],json.loads(read(client,impact_records[0][0]['input'])),
                {f['id']:read(client,f['artifact']) for f in impact_records[0][1]['report']['files']},impact_reviews)
            for old in (receipt,consumer_receipt,effect_receipt,derived_receipt,library_records[0][0],dependency_records[0][0]):
                for section in ['dependencyImpacts','dependencyImpactCoverage']:
                    refuse(client,'hmi_component_review_page',dict(recordId=old['recordId'],section=section),'InvalidHmiComponentReview')
            report['impactRecords']=[r['recordId'] for r,_ in impact_records]
            authored_records = author_text_lists(client, retain_upload, read, check, refuse, output)
            report['authoredTextListRecords'] = [receipt['recordId'] for _, receipt, _ in authored_records]
            authored_request, authored_receipt, authored_files = authored_records[0]
            authored_proposal = copy.deepcopy(fixture['effectPacket'])
            authored_proposal.update(operation='install', beforeComponent=None, componentMatches=[], bindings=[], files=[])
            authored_proposal['destination'].update(reviewId=fixture['unifiedReviewId'], family='unified')
            authored_ids = []
            for index, (descriptor, data) in enumerate(zip(authored_receipt['files'], authored_files)):
                identifier = 'authored-' + str(index); authored_ids.append(identifier)
                authored_proposal['files'].append(dict(id=identifier, fileName=descriptor['fileName'], uploadRef=retain_upload(client, data), sha256=hashlib.sha256(data).hexdigest(), bytes=len(data)))
            authored_output = copy.deepcopy(packet['outputs'][0])
            authored_output.update(id='text-lists', objectPath=None, parentPath=authored_proposal['destination']['hmiPath'], name=authored_request['lists'][0]['name'], kind='hmiResource', action='create', ownership='generated', baseline=[], current=[], proposed=authored_ids, baselineComplete=False, currentComplete=False, proposedComplete=True)
            authored_proposal['outputs'] = [authored_output]
            authored_proposal['observations'] = []
            authored_component_receipt, authored_component_record = review(client, authored_proposal)
            check(authored_component_record['report']['summary']['conflictCount'] == 0, 'Authored candidates enter exact Unified destination component proposal review')
            check(authored_component_receipt['nativeExecutable'] is False and authored_component_receipt['wholeProjectPreservationVerified'] is False, 'Authoring proposal remains unqualified and unexecuted')
            for item in incoming:
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
        finally: close(client)
        client = start()
        try:
            reopen_text_lists(client, authored_records, read, check, refuse, artifacts)
            check(client.tool('hmi_component_review_page', dict(recordId=authored_component_receipt['recordId'])) == authored_component_receipt, 'Authored component proposal survives upload release and Host restart')
            for expected,full in impact_records:
                check(client.tool('hmi_component_review_page',dict(recordId=expected['recordId']))==expected,'Dependency impact receipt survives fresh Host and upload release')
                pages(client,expected,full)
                verify_impacts(full,json.loads(read(client,expected['input'])),{f['id']:read(client,f['artifact']) for f in full['report']['files']},impact_reviews)
            forged_impact=copy.deepcopy(impact_records[0][1]);forged_impact['report']['dependencyImpacts'][0]['outputs'][0]['effectStatus']='match'
            raw=json.dumps(forged_impact).encode();identity=hashlib.sha256(raw).hexdigest()
            (artifacts/'hmi-component-reviews'/(identity+'.json')).write_bytes(raw)
            refuse(client,'hmi_component_review_page',dict(recordId=identity),'ArtifactChanged')
            forged_impact['report']['dependencyImpacts'][0]['nativeImpactVerified']=True
            try:validate(forged_impact,'record')
            except ValidationError:check(True,'Schema refuses native impact promotion')
            else:raise AssertionError('Native impact promotion accepted')
            for expected,full in dependency_records:
                check(client.tool('hmi_component_review_page',dict(recordId=expected['recordId']))==expected,'Dependency associations survive restart and upload release')
                pages(client,expected,full)
                request=json.loads(read(client,expected['input']))
                verify_dependencies(full,request,{f['id']:read(client,f['artifact']) for f in full['report']['files']},dependency_reviews)
            forged_dependency=copy.deepcopy(dependency_full);forged_dependency['report']['dependencyAssociations'][0]['status']='different'
            raw=json.dumps(forged_dependency).encode();identity=hashlib.sha256(raw).hexdigest()
            (artifacts/'hmi-component-reviews'/(identity+'.json')).write_bytes(raw)
            refuse(client,'hmi_component_review_page',dict(recordId=identity),'ArtifactChanged')
            source=dependency_full['report']['files'][0]['artifact'];original=(artifacts/source['path']).read_bytes()
            try:
                (artifacts/source['path']).write_bytes(b'tampered')
                refuse(client,'hmi_component_review_page',dict(recordId=dependency_records[0][0]['recordId']),'ArtifactChanged')
            finally:(artifacts/source['path']).write_bytes(original)
            forged_dependency['report']['dependencyAssociations'][0]['nativeIdentityVerified']=True
            try:validate(forged_dependency,'record')
            except ValidationError:check(True,'Schema refuses native dependency identity promotion')
            else:raise AssertionError('Dependency identity promotion accepted')
            for expected,full in library_records:
                check(client.tool('hmi_component_review_page',dict(recordId=expected['recordId']))==expected,'Library associations survive restart and upload release')
                pages(client,expected,full)
                request=json.loads(read(client,expected['input']))
                verify_library(full,request,{f['id']:read(client,f['artifact']) for f in full['report']['files']},library_reviews)
            forged_library=copy.deepcopy(library_full);forged_library['report']['libraryAssociations'][0]['identifierAgreement']='different'
            raw=json.dumps(forged_library).encode();identity=hashlib.sha256(raw).hexdigest()
            (artifacts/'hmi-component-reviews'/(identity+'.json')).write_bytes(raw)
            refuse(client,'hmi_component_review_page',dict(recordId=identity),'ArtifactChanged')
            source=library_full['report']['files'][0]['artifact'];original=(artifacts/source['path']).read_bytes()
            try:
                (artifacts/source['path']).write_bytes(b'tampered')
                refuse(client,'hmi_component_review_page',dict(recordId=library_records[0][0]['recordId']),'ArtifactChanged')
            finally:(artifacts/source['path']).write_bytes(original)
            forged_library['report']['libraryAssociations'][0]['libraryIdentityVerified']=True
            try:validate(forged_library,'record')
            except ValidationError:check(True,'Schema refuses library identity promotion')
            else:raise AssertionError('Library identity promotion accepted')
            for expected,full in derived_records:
                check(client.tool('hmi_component_review_page',dict(recordId=expected['recordId']))==expected,'Derived expectations survive restart and upload release')
                pages(client,expected,full)
                request=json.loads(read(client,expected['input']))
                verify_derived(full,request,{f['id']:read(client,f['artifact']) for f in full['report']['files']})
            forged_derived=copy.deepcopy(derived_record);forged_derived['report']['derivedEffects'][0]['observation']['status']='different'
            raw=json.dumps(forged_derived).encode();identity=hashlib.sha256(raw).hexdigest()
            (artifacts/'hmi-component-reviews'/(identity+'.json')).write_bytes(raw)
            refuse(client,'hmi_component_review_page',dict(recordId=identity),'ArtifactChanged')
            source=next(f['artifact'] for f in derived_record['report']['files'] if f['id']=='screen-current')
            original=(artifacts/source['path']).read_bytes()
            try:
                (artifacts/source['path']).write_bytes(b'tampered')
                refuse(client,'hmi_component_review_page',dict(recordId=derived_receipt['recordId']),'ArtifactChanged')
            finally:(artifacts/source['path']).write_bytes(original)
            for expected, full in ((receipt, record), (install_receipt, install_record), (conflict_receipt, conflict_record), (consumer_receipt, consumer_record), (bounded_receipt, bounded_record), (new_receipt, new_record), (effect_receipt, effect_record), (effect_bad_receipt, effect_bad_record), (effect_missing_receipt, effect_missing_record)):
                check(client.tool('hmi_component_review_page', {'recordId': expected['recordId']}) == expected, 'Restart and release preserve receipt')
                pages(client, expected, full)
            for file in record['report']['files']:
                check(read(client, file['artifact']) == base64.b64decode(fixture['files'][file['id']]), 'Released original file survives restart: ' + file['id'])
            for reference in (receipt['input'], receipt['reportArtifact'], record['report']['files'][0]['artifact']):
                path = (artifacts / reference['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'hmi_component_review_page', {'recordId': receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            forged = copy.deepcopy(record)
            forged['report']['outputs'][0]['preservationVerified'] = True
            raw = json.dumps(forged).encode()
            forged_id = hashlib.sha256(raw).hexdigest()
            (artifacts / 'hmi-component-reviews' / (forged_id + '.json')).write_bytes(raw)
            refuse(client, 'hmi_component_review_page', {'recordId': forged_id}, 'ArtifactChanged')
            forged_context = copy.deepcopy(consumer_record)
            forged_context['report']['consumerPaths'][0]['reviewId'] = fixture['consumerReviewIds'][1]
            raw = json.dumps(forged_context).encode()
            forged_id = hashlib.sha256(raw).hexdigest()
            (artifacts / 'hmi-component-reviews' / (forged_id + '.json')).write_bytes(raw)
            refuse(client, 'hmi_component_review_page', {'recordId': forged_id}, 'ArtifactChanged')
            check(client.tool('hmi_component_review_page', {'recordId': receipt['recordId']}) == receipt, 'Exact restored evidence reopens unchanged')
            for file in effect_record['report']['files']:
                check(read(client, file['artifact']) == base64.b64decode(fixture['files'][file['id']]), 'Independent observation/source survives release and restart: ' + file['id'])
            forged_effect = copy.deepcopy(effect_record)
            forged_effect['report']['effectChecks'][0]['status'] = 'mismatch'
            raw = json.dumps(forged_effect).encode()
            forged_id = hashlib.sha256(raw).hexdigest()
            (artifacts / 'hmi-component-reviews' / (forged_id + '.json')).write_bytes(raw)
            refuse(client, 'hmi_component_review_page', {'recordId': forged_id}, 'ArtifactChanged')
            forged_effect['report']['effectChecks'][0]['nativeEffectVerified'] = True
            try: validate(forged_effect, 'record')
            except ValidationError: check(True, 'Schema refuses native effect promotion')
            else: raise AssertionError('Native effect promotion accepted')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS hmi_component MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__':
    main()
