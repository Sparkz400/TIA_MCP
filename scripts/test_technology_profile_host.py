"""Actual supplied technology profile review, schemas, exact identities, retention and restart."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError
from smoke_mcp import Client, ROOT


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
    review_bytes = (Path(fixture['artifactRoot']) / 'project-reviews' / (fixture['reviewId'] + '.json')).read_bytes()
    packet_bytes = (json.dumps(packet, ensure_ascii=False) + '\n').encode()
    schema = json.loads((ROOT / 'contracts/technology-profile-review.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    validate(packet, 'request')
    check(True, 'Synthetic input satisfies strict request schema')
    for change in ('extra', 'coverage', 'domain', 'access', 'value'):
        mutated = copy.deepcopy(packet)
        if change == 'extra': mutated['nativeQualified'] = True
        elif change == 'coverage': mutated['before']['parametersComplete'] = 'true'
        elif change == 'domain': mutated['before']['valueDomain'] = 'current'
        elif change == 'access': mutated['before']['parameters'][0]['access'] = 'nativeApproved'
        else: mutated['before']['parameters'][0]['value'] = None
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
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'technology-review-offline', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def upload(client, data, name='supplied.json'):
        status = client.tool('artifact_upload_begin', {'fileName': name, 'expectedBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
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
        def retained():
            return {str(p.relative_to(artifacts)): hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in (artifacts / 'technology-profile-reviews').glob('*') if p.is_file()}
        before_refusal = retained()
        response = client.request('tools/call', {'name': tool, 'arguments': arguments})
        error = response['structuredContent']['error']
        check(response.get('isError') and error['code'] == code and error['nativeAccessAttempted'] is False, 'Structured offline refusal ' + code)
        check(retained() == before_refusal, 'Refusal preserves every retained workflow file')
    try:
        client = start()
        try:
            check({'technology_profile_review', 'technology_profile_report'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both tools registered')
            saved = upload(client, review_bytes)
            source = client.tool('project_snapshot_import', {'uploadRef': saved['artifactRef']})
            check(source['reviewId'] == fixture['reviewId'], 'Saved review imports exact original bytes')
            incoming = upload(client, packet_bytes)
            arguments = {'reviewId': source['reviewId'], 'uploadRef': incoming['artifactRef']}
            receipt = client.tool('technology_profile_review', arguments)
            validate(receipt, 'receipt')
            check(receipt == client.tool('technology_profile_review', arguments), 'Duplicate review is idempotent')
            check(read(client, receipt['input']) == packet_bytes, 'Original Unicode and final newline preserved')
            record = json.loads(read(client, receipt['reportArtifact']))
            validate(record, 'record')
            result = record['report']
            check(result['summary']['changedCount'] == 3 and result['summary']['readOnlyConflictCount'] == 1, 'Exact changes retain original read-only contradiction')
            check(result['summary']['unknownAccessChangeCount'] == 1, 'Unknown access is not promoted')
            check(result['summary']['declaredCycleRecordCount'] == 4, 'Supplied cycles remain explicit without native invalidity claim')
            check(result['summary']['unresolvedEndpointCount'] == 2, 'Unknown endpoints never resolve by name')
            check(result['relationships'][3]['memberResolution'] == 'notVerified', 'DB member resolution remains unverified')
            for section in ('observations', 'relationships', 'changes', 'context'):
                rows, offset = [], 0
                while True:
                    page = client.tool('technology_profile_report', {'recordId': receipt['recordId'], 'section': section, 'offset': offset, 'count': 1})
                    validate(page, 'page')
                    rows.extend(row['value'] for row in page['rows'])
                    if page['nextOffset'] is None: break
                    offset = page['nextOffset']
                check(rows == ([result[section]] if section == 'context' else result[section]), 'Paged ' + section + ' matches immutable report')
            adversary = copy.deepcopy(record)
            adversary['report']['nativeQualified'] = True
            try: validate(adversary, 'record')
            except ValidationError: check(True, 'Output schema cannot certify native qualification')
            else: raise AssertionError('Native claim accepted')
            refuse(client, 'technology_profile_report', {'recordId': receipt['recordId'], 'section': 'changes', 'offset': 100}, 'InvalidRequest')
            bad = copy.deepcopy(packet)
            bad['reviewId'] = '0' * 64
            invalid = upload(client, json.dumps(bad).encode())
            refuse(client, 'technology_profile_review', {**arguments, 'uploadRef': invalid['artifactRef']}, 'InvalidTechnologyProfile')
            duplicate = upload(client, b'{"x":1,"x":2}')
            refuse(client, 'technology_profile_review', {**arguments, 'uploadRef': duplicate['artifactRef']}, 'InvalidTechnologyProfile')
            mapping = copy.deepcopy(packet)
            mapping['after']['identity']['firmware'] = 'another'
            bad_mapping = upload(client, json.dumps(mapping).encode())
            refuse(client, 'technology_profile_review', {**arguments, 'uploadRef': bad_mapping['artifactRef']}, 'InvalidTechnologyProfile')
            proposal = copy.deepcopy(packet)
            proposal['before']['valueDomain'] = 'online'
            bad_proposal = upload(client, json.dumps(proposal).encode())
            refuse(client, 'technology_profile_review', {**arguments, 'uploadRef': bad_proposal['artifactRef']}, 'InvalidTechnologyProfile')
            observation = copy.deepcopy(packet)
            observation['afterKind'] = 'observation'
            observation['after']['valueDomain'] = 'online'
            observed = upload(client, json.dumps(observation).encode())
            observed_receipt = client.tool('technology_profile_review', {**arguments, 'uploadRef': observed['artifactRef']})
            validate(observed_receipt, 'receipt')
            observed_record = json.loads(read(client, observed_receipt['reportArtifact']))
            validate(observed_record, 'record')
            check(observed_record['report']['beforeValueDomain'] == 'offline' and observed_record['report']['afterValueDomain'] == 'online', 'Different value domains stay explicit')
            source_bytes = ['<supplied>日本語\r\nopaque</supplied>'.encode('utf-16'), b'\0\xff\x80\r\n\0']
            source_names = ['supplied日本.xml', 'supplied.bin']
            source_uploads = [upload(client, data, name) for data, name in zip(source_bytes, source_names)]
            source_packet = copy.deepcopy(packet)
            source_packet['schemaVersion'] = 'technology-profile-review-request.v2'
            source_packet['sources'] = [dict(label=label, purpose='supplied baseline only', phase='before', fileName=name,
                uploadRef=uploaded['artifactRef'], sha256=hashlib.sha256(data).hexdigest(), bytes=len(data))
                for label, name, uploaded, data in zip(['é', 'e\u0301'], source_names, source_uploads, source_bytes)]
            role = {**source_packet['sources'][0], 'label': 'same bytes, another role', 'purpose': 'declared comparison source', 'phase': 'after'}
            source_packet['sources'].append(role)
            validate(source_packet, 'request')
            source_input = upload(client, json.dumps(source_packet, ensure_ascii=False).encode())
            source_receipt = client.tool('technology_profile_review', {'reviewId': source['reviewId'], 'uploadRef': source_input['artifactRef']})
            validate(source_receipt, 'receipt')
            source_record = json.loads(read(client, source_receipt['reportArtifact']))
            validate(source_record, 'record')
            source_rows = source_record['report']['sources']
            check(source_record['schemaVersion'] == 'technology-profile-review-record.v2', 'Source bundle uses explicit v2 envelope')
            check(source_rows[0]['artifact'] == source_rows[2]['artifact'] and source_rows[0]['purpose'] != source_rows[2]['purpose'], 'Same bytes keep distinct declared roles')
            check([read(client, row['artifact']) for row in source_rows[:2]] == source_bytes, 'Independent UTF16 and binary source bytes preserved')
            rows = []
            for offset in range(3):
                page = client.tool('technology_profile_report', {'recordId': source_receipt['recordId'], 'section': 'sources', 'offset': offset, 'count': 1})
                validate(page, 'page')
                rows.extend(row['value'] for row in page['rows'])
            check(rows == source_rows, 'Source manifest pages match complete retained record')
            check(client.tool('technology_profile_report', {'recordId': receipt['recordId']}) == receipt, 'v1 receipt unchanged after v2 intake')
            check(client.tool('technology_profile_report', {'recordId': receipt['recordId'], 'section': 'sources'})['total'] == 0, 'v1 gets no invented sources')
            for defect in ('name', 'digest', 'bytes', 'duplicate', 'count', 'total', 'authority'):
                bad = copy.deepcopy(source_packet)
                if defect == 'name': bad['sources'][0]['fileName'] = 'wrong.xml'
                elif defect == 'digest': bad['sources'][0]['sha256'] = '0' * 64
                elif defect == 'bytes': bad['sources'][0]['bytes'] += 1
                elif defect == 'duplicate': bad['sources'].append(copy.deepcopy(bad['sources'][0]))
                elif defect == 'count': bad['sources'] = [{**role, 'label': str(i)} for i in range(17)]
                elif defect == 'total': bad['sources'] = [{**role, 'label': str(i), 'bytes': 16*1024*1024} for i in range(5)]
                else: bad['sources'][0]['nativeQualified'] = True
                invalid_source = upload(client, json.dumps(bad).encode())
                refuse(client, 'technology_profile_review', {'reviewId': source['reviewId'], 'uploadRef': invalid_source['artifactRef']}, 'InvalidTechnologyProfile')
                client.tool('artifact_upload_release', {'uploadId': invalid_source['uploadId']})
            consumer_packet = copy.deepcopy(fixture['consumerPacket'])
            consumer_review_bytes = (Path(fixture['artifactRoot']) / 'project-reviews' / (fixture['consumerReviewId'] + '.json')).read_bytes()
            consumer_saved = upload(client, consumer_review_bytes)
            check(client.tool('project_snapshot_import', {'uploadRef': consumer_saved['artifactRef']})['reviewId'] == fixture['consumerReviewId'], 'Consumer review imports exact saved graph')
            validate(consumer_packet, 'request')
            consumer_input = upload(client, json.dumps(consumer_packet, ensure_ascii=False).encode())
            consumer_receipt = client.tool('technology_profile_review', {'reviewId': fixture['consumerReviewId'], 'uploadRef': consumer_input['artifactRef']})
            validate(consumer_receipt, 'receipt')
            consumer_record = json.loads(read(client, consumer_receipt['reportArtifact']))
            validate(consumer_record, 'record')
            check(consumer_record['schemaVersion'] == 'technology-profile-review-record.v3', 'Consumer extension has explicit v3 record')
            check(len(consumer_record['report']['consumers']) == 4 and consumer_record['report']['impactPaths'], 'Both consumer phases and transitive saved context retained')
            check(all(not row['nativeLinkVerified'] and row['memberResolution'] == 'notVerified' for row in consumer_record['report']['consumers']), 'Captured identities do not verify native consumer links or members')
            for section in ('consumers', 'consumerChanges', 'impactPaths', 'impactTraversal'):
                rows, offset = [], 0
                while True:
                    page = client.tool('technology_profile_report', {'recordId': consumer_receipt['recordId'], 'section': section, 'offset': offset, 'count': 1})
                    validate(page, 'page')
                    rows.extend(row['value'] for row in page['rows'])
                    if page['nextOffset'] is None: break
                    offset = page['nextOffset']
                check(rows == ([consumer_record['report'][section]] if section == 'impactTraversal' else consumer_record['report'][section]), 'v3 paged ' + section + ' matches retained report')
                for old_receipt in (receipt, source_receipt):
                    old_page = client.tool('technology_profile_report', {'recordId': old_receipt['recordId'], 'section': section})
                    validate(old_page, 'page')
                    check(old_page['total'] == 0, 'Legacy records invent no ' + section)
            # Exercise v3 source retention together with consumer/impact review.
            combined = copy.deepcopy(consumer_packet)
            combined['sources'] = copy.deepcopy(source_packet['sources'])
            combined_input = upload(client, json.dumps(combined, ensure_ascii=False).encode())
            combined_receipt = client.tool('technology_profile_review', {'reviewId': fixture['consumerReviewId'], 'uploadRef': combined_input['artifactRef']})
            combined_record = json.loads(read(client, combined_receipt['reportArtifact']))
            validate(combined_record, 'record')
            check(combined_record['report']['sources'] == source_rows and combined_record['report']['consumers'] == consumer_record['report']['consumers'], 'v3 composes exact source bundle and consumer evidence')
            for defect in ('duplicate', 'authority', 'halfIdentity', 'unknown', 'limit', 'oldVersion'):
                bad = copy.deepcopy(consumer_packet)
                row = bad['consumers']['before'][0]
                if defect == 'duplicate': bad['consumers']['before'].append(copy.deepcopy(row))
                elif defect == 'authority': row['nativeLinkVerified'] = True
                elif defect == 'halfIdentity': row['nodeId'] = None
                elif defect == 'unknown': row['state'] = 'unknown'
                elif defect == 'limit': bad['impactLimits']['maxImpactEdgeVisits'] = 20001
                else: bad['schemaVersion'] = 'technology-profile-review-request.v2'
                invalid_consumer = upload(client, json.dumps(bad).encode())
                refuse(client, 'technology_profile_review', {'reviewId': fixture['consumerReviewId'], 'uploadRef': invalid_consumer['artifactRef']}, 'InvalidTechnologyProfile')
                client.tool('artifact_upload_release', {'uploadId': invalid_consumer['uploadId']})
            for defect in ('consumer', 'paths', 'coverage', 'version'):
                forged = copy.deepcopy(consumer_record)
                if defect == 'consumer': forged['report']['consumers'][0]['objectResolution'] = 'missing'
                elif defect == 'paths': forged['report']['impactPaths'] = []
                elif defect == 'coverage': forged['report']['impactTraversal']['complete'] = True
                else: forged['schemaVersion'] = 'technology-profile-review-record.v2'
                data = json.dumps(forged).encode()
                digest = hashlib.sha256(data).hexdigest()
                (artifacts / 'technology-profile-reviews' / (digest + '.json')).write_bytes(data)
                refuse(client, 'technology_profile_report', {'recordId': digest}, 'InvalidTechnologyProfile' if defect == 'version' else 'ArtifactChanged')
            for item in (consumer_input, consumer_saved, combined_input):
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
            from test_technology_encoder_contracts import verify as verify_encoders, loader as encoder_loader
            encoder_fixture = json.loads((args.fixture.parent / 'encoders-v4/mcp-fixture.json').read_text(encoding='utf-8'))
            encoder_root = Path(encoder_fixture['artifactRoot'])
            encoder_review_bytes = (encoder_root / 'project-reviews' / (encoder_fixture['reviewId'] + '.json')).read_bytes()
            encoder_saved = upload(client, encoder_review_bytes)
            check(client.tool('project_snapshot_import', {'uploadRef': encoder_saved['artifactRef']})['reviewId'] == encoder_fixture['reviewId'], 'Encoder exact source review imports')
            encoder_drive_bytes = (encoder_root / encoder_fixture['driveReceipt']['input']['path']).read_bytes()
            encoder_drive_input = upload(client, encoder_drive_bytes)
            check(client.tool('drive_profile_review', {'reviewId': encoder_fixture['reviewId'], 'uploadRef': encoder_drive_input['artifactRef']}) == encoder_fixture['driveReceipt'], 'Encoder original drive dependency has identical retained receipt')
            encoder_bytes = (encoder_root / encoder_fixture['receipt']['input']['path']).read_bytes()
            encoder_input = upload(client, encoder_bytes)
            encoder_arguments = dict(reviewId=encoder_fixture['reviewId'], uploadRef=encoder_input['artifactRef'])
            encoder_receipt = client.tool('technology_profile_review', encoder_arguments); validate(encoder_receipt, 'receipt')
            check(encoder_receipt == encoder_fixture['receipt'], 'Actual MCP and managed v4 receipts are identical')
            check(client.tool('technology_profile_review', encoder_arguments) == encoder_receipt, 'Typed encoder intake is idempotent')
            encoder_record = json.loads(read(client, encoder_receipt['reportArtifact'])); validate(encoder_record, 'record')
            check(verify_encoders(encoder_fixture['packet'], encoder_record['report'], json.loads(encoder_review_bytes), encoder_loader(artifacts))['retainedDriveInputsChecked'], 'Independent original encoder and drive inputs reproduce report')
            for section in ('encoderConnections', 'encoderChanges', 'encoderCoverage'):
                rows, offset = [], 0
                while True:
                    page = client.tool('technology_profile_report', dict(recordId=encoder_receipt['recordId'], section=section, offset=offset, count=2)); validate(page, 'page')
                    for row in page['rows']: check(row['index'] == len(rows), 'Exact encoder page index'); rows.append(row['value'])
                    if page['nextOffset'] is None: break
                    offset = page['nextOffset']
                check(rows == ([encoder_record['report'][section]] if section == 'encoderCoverage' else encoder_record['report'][section]), 'All encoder section rows retained: ' + section)
            for old in (receipt, source_receipt, consumer_receipt):
                refuse(client, 'technology_profile_report', dict(recordId=old['recordId'], section='encoderConnections'), 'InvalidRequest')
            for defect in ('unit', 'phase', 'unknown', 'schema'):
                bad = copy.deepcopy(encoder_fixture['packet']); rows = bad['encoders']['before']['observations']
                if defect == 'unit': rows[3]['target']['input']['unit'] = 'word'
                elif defect == 'phase': rows[5]['target']['phase'] = 'after'
                elif defect == 'unknown': rows[0]['sensorIndex'] = None
                else: bad['schemaVersion'] = False
                invalid_encoder = upload(client, json.dumps(bad).encode())
                refuse(client, 'technology_profile_review', dict(reviewId=encoder_fixture['reviewId'], uploadRef=invalid_encoder['artifactRef']), 'InvalidTechnologyProfile')
                client.tool('artifact_upload_release', {'uploadId': invalid_encoder['uploadId']})
            encoder_combined = copy.deepcopy(encoder_fixture['packet']); encoder_combined['sources'] = copy.deepcopy(source_packet['sources'])
            encoder_combined_input = upload(client, json.dumps(encoder_combined).encode())
            encoder_combined_receipt = client.tool('technology_profile_review', dict(reviewId=encoder_fixture['reviewId'], uploadRef=encoder_combined_input['artifactRef']))
            encoder_combined_record = json.loads(read(client, encoder_combined_receipt['reportArtifact'])); validate(encoder_combined_record, 'record')
            check(encoder_combined_record['report']['sources'] == source_rows and encoder_combined_record['report']['encoderConnections'] == encoder_record['report']['encoderConnections'], 'V4 composes original source retention and encoder evidence without downgrade')
            oversized_drive_input = upload(client, (encoder_root / encoder_fixture['oversizedDriveReceipt']['input']['path']).read_bytes())
            check(client.tool('drive_profile_review', dict(reviewId=encoder_fixture['reviewId'], uploadRef=oversized_drive_input['artifactRef'])) == encoder_fixture['oversizedDriveReceipt'], 'Large original telegram dependency retains exact receipt')
            oversized_bytes = (encoder_root / encoder_fixture['oversizedReceipt']['input']['path']).read_bytes()
            oversized_input = upload(client, oversized_bytes)
            oversized_receipt = client.tool('technology_profile_review', dict(reviewId=encoder_fixture['reviewId'], uploadRef=oversized_input['artifactRef']))
            page = client.tool('technology_profile_report', dict(recordId=oversized_receipt['recordId'], section='encoderConnections', count=1)); validate(page, 'page')
            check(page['rows'][0]['omitted'] is True, 'Oversized encoder page row is explicitly omitted')
            oversized_record = json.loads(read(client, oversized_receipt['reportArtifact'])); validate(oversized_record, 'record')
            check(verify_encoders(json.loads(oversized_bytes), oversized_record['report'], json.loads(encoder_review_bytes), encoder_loader(artifacts))['originalRowsChecked'], 'Full oversized encoder download independently matches originals')
            for item in (oversized_drive_input, oversized_input):
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
            for item in (encoder_saved, encoder_drive_input, encoder_input, encoder_combined_input):
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
            for item in [*source_uploads, source_input]:
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
            for item in (incoming, saved, invalid, duplicate, bad_mapping, bad_proposal, observed):
                client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
        finally: close(client)
        client = start()
        try:
            check(client.tool('technology_profile_report', {'recordId': receipt['recordId']}) == receipt, 'Restart and upload release preserve receipt')
            check(read(client, receipt['input']) == packet_bytes, 'Original bytes survive restart')
            for current in (encoder_receipt, encoder_combined_receipt, oversized_receipt):
                check(client.tool('technology_profile_report', {'recordId': current['recordId']}) == current, 'V4 survives restart and release of all uploads')
            check(read(client, encoder_receipt['input']) == encoder_bytes, 'Encoder original bytes survive restart')
            dependent = artifacts / encoder_fixture['driveReceipt']['input']['path']; original_dependency = dependent.read_bytes()
            try:
                dependent.write_bytes(b'corrupted original drive input')
                for current in (encoder_receipt, encoder_combined_receipt):
                    refuse(client, 'technology_profile_report', {'recordId': current['recordId']}, 'ArtifactChanged')
            finally: dependent.write_bytes(original_dependency)
            check(client.tool('technology_profile_report', {'recordId': encoder_receipt['recordId']}) == encoder_receipt, 'Restored exact dependency reopens without mutation replay')
            for current in (consumer_receipt, combined_receipt):
                check(client.tool('technology_profile_report', {'recordId': current['recordId']}) == current, 'v3 record reopens after restart and release of every upload')
                reopened = json.loads(read(client, current['reportArtifact']))
                validate(reopened, 'record')
                check(reopened['report']['consumers'] == consumer_record['report']['consumers'], 'v3 association bytes survive restart')

            check(client.tool('technology_profile_report', {'recordId': source_receipt['recordId']}) == source_receipt, 'v2 reopens after all source uploads released and Host restarted')
            check([read(client, row['artifact']) for row in source_rows[:2]] == source_bytes, 'Owned source bytes survive release and restart')
            for row in source_rows[:2]:
                path = (artifacts / row['artifact']['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'technology_profile_report', {'recordId': source_receipt['recordId']}, 'ArtifactChanged')
                    refuse(client, 'technology_profile_report', {'recordId': combined_receipt['recordId']}, 'ArtifactChanged')
                    refuse(client, 'technology_profile_report', {'recordId': encoder_combined_receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            check(client.tool('technology_profile_report', {'recordId': source_receipt['recordId']}) == source_receipt, 'Restored exact sources reopen without upload lookup')
            for key in ('input', 'reportArtifact'):
                path = (artifacts / receipt[key]['path']).resolve()
                path.relative_to(artifacts.resolve())
                original = path.read_bytes()
                try:
                    path.write_bytes(b'corrupted')
                    refuse(client, 'technology_profile_report', {'recordId': receipt['recordId']}, 'ArtifactChanged')
                finally: path.write_bytes(original)
            check(client.tool('technology_profile_report', {'recordId': receipt['recordId']}) == receipt, 'Restored exact bytes reopen without mutation replay')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS technology profile MCP: {report['count']} checks; two clean Host lifetimes, no native execution")


if __name__ == '__main__':
    main()
