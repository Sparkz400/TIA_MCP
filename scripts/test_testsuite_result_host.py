"""Supplied Test Suite result intake through real offline MCP Hosts and schema checks."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

from jsonschema import Draft202012Validator, ValidationError
from referencing import Registry, Resource
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
    review_bytes = (Path(fixture['artifactRoot']) / 'project-reviews' / (fixture['reviewId'] + '.json')).read_bytes()
    packet = fixture['packet']
    packet['results'][0]['messages'][0]['description'] += ' 日本語 😀'
    packet_bytes = json.dumps(packet, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    registry = Registry()
    for path in (ROOT / 'contracts').glob('*.json'):
        schema = json.loads(path.read_text(encoding='utf-8'))
        if isinstance(schema, dict) and '$schema' in schema and '$id' in schema:
            resource = Resource.from_contents(schema)
            registry = registry.with_resource(schema['$id'], resource).with_resource('https://tia-mcp.local/contracts/' + path.name, resource)
    def validator(name):
        schema = json.loads((ROOT / 'contracts' / name).read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema)
        return Draft202012Validator(schema, registry=registry)
    incoming = validator('testsuite-supplied-results.v1.schema.json')
    outgoing = validator('testsuite-result-report.v1.schema.json')
    health_schema = validator('project-health-testsuite.v1.schema.json')
    report = {'completed': False, 'nativeExecuted': False, 'nativeQualified': False, 'checks': [], 'shutdowns': []}
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    incoming.validate(packet)
    check(True, 'Supplied fixture satisfies strict input schema')
    for mutation in ('extra', 'scope', 'plan', 'counter', 'state'):
        changed = copy.deepcopy(packet)
        if mutation == 'extra': changed['nativeQualified'] = True
        elif mutation == 'scope': changed['scope'] = None
        elif mutation == 'plan': changed['plan']['nativeExecutionAuthorized'] = True
        elif mutation == 'counter': changed['results'][0]['warningCount'] = -1
        else: changed['results'][0]['state'] = 'Invented'
        try: incoming.validate(changed)
        except ValidationError: check(True, 'Schema refuses ' + mutation)
        else: raise AssertionError('Schema accepted ' + mutation)
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-native-worker.exe'
    def start():
        client = Client(args.host.resolve(), 30, ['--artifacts', str(artifacts), '--worker', str(missing), '--sim-worker', str(missing)])
        try:
            client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'supplied-results-offline', 'version': '1'}})
            client.notify('notifications/initialized')
        except BaseException:
            close(client)
            raise
        return client
    def close(client):
        receipt = client.close(); report['shutdowns'].append(receipt)
        check(receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'] and not receipt['workerExits'], 'Clean Host exit with no native workers')
    def upload(client, name, data):
        status = client.tool('artifact_upload_begin', {'fileName': name, 'expectedBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        for offset in range(0, len(data), status['maxChunkBytes']):
            client.tool('artifact_upload_chunk', {'uploadId': status['uploadId'], 'offset': offset, 'data': base64.b64encode(data[offset:offset + status['maxChunkBytes']]).decode()})
        return client.tool('artifact_upload_commit', {'uploadId': status['uploadId']})
    def read(client, reference):
        parts, offset = [], 0
        while True:
            data = client.tool('artifact_read', {'path': reference['relativePath'], 'offset': offset, 'count': 1024})
            check(data['sha256'] == reference['sha256'] and data['offset'] == offset, 'Exact artifact chunk identity and offset')
            decoded = base64.b64decode(data['data'], validate=True)
            assert len(decoded) == data['count'] and data['nextOffset'] == offset + len(decoded)
            parts.append(decoded); offset = data['nextOffset']
            if data['complete']: break
        raw = b''.join(parts)
        check(hashlib.sha256(raw).hexdigest() == reference['sha256'], 'Complete downloaded artifact digest')
        return raw
    def refuse(client, tool, arguments, code):
        result = client.request('tools/call', {'name': tool, 'arguments': arguments})
        check(result.get('isError') and result['structuredContent']['error']['code'] == code, 'Host refusal: ' + code)
    try:
        client = start()
        try:
            check({'testsuite_result_import', 'testsuite_result_review'} <= {t['name'] for t in client.request('tools/list')['tools']}, 'Both result tools registered')
            review_upload = upload(client, 'review.json', review_bytes)
            review = client.tool('project_snapshot_import', {'uploadRef': review_upload['artifactRef']})
            check(review['reviewId'] == fixture['reviewId'], 'Saved review imported with its exact original byte identity')
            result_upload = upload(client, 'results.json', packet_bytes)
            imported = client.tool('testsuite_result_import', {'reviewId': review['reviewId'], 'uploadRef': result_upload['artifactRef']})
            duplicate = client.tool('testsuite_result_import', {'reviewId': review['reviewId'], 'uploadRef': result_upload['artifactRef']})
            check(imported == duplicate, 'Identical supplied import returns the same immutable artifacts')
            check(read(client, imported['input']) == packet_bytes, 'Original packet retained byte-for-byte')
            derived = json.loads(read(client, imported['report']))
            outgoing.validate(derived)
            check(derived['findings'][0]['binding'] == 'exactCapturedSource' and derived['findings'][0]['finding']['warningCount'] == 1, 'Exact saved-source warning finding is retained')
            check(derived['summary']['savedReviewScopeBound'] and not derived['summary']['nativeQualified'] and not imported['sourceAuthenticityVerified'], 'Structural binding does not authenticate supplied evidence')
            check('testSuite' not in client.tool('project_health_report', {'reviewId': review['reviewId']}), 'Health does not implicitly select stored test results')
            health_args = {'reviewId': review['reviewId'], 'testSuiteResults': [imported['receipt']]}
            health = client.tool('project_health_report', health_args)
            health_schema.validate(health['testSuite'])
            check(health['testSuite']['reports'][0]['outcomeCounts']['inconclusive'] == 1 and health['testSuite']['findingCount'] == 1 and not health['testSuite']['allSelectedSuppliedEvidencePassed'], 'Health retains warning outcome and per-packet finding count')
            check(health['testSuite']['reports'][0]['receipt'] == imported['receipt'] and not health['testSuite']['wholeProjectHealthComplete'], 'Health provides exact finding navigation without whole-project acceptance')
            empty_health = client.tool('project_health_report', {'reviewId': review['reviewId'], 'testSuiteResults': []})['testSuite']
            health_schema.validate(empty_health)
            check(empty_health['selectionCount'] == 0 and not empty_health['allSelectedSuppliedEvidencePassed'], 'Empty selected evidence cannot pass health coverage')
            for mutation in ('emptyPass', 'qualified', 'negativeCount'):
                adversary = copy.deepcopy(empty_health)
                if mutation == 'emptyPass': adversary['allSelectedSuppliedEvidencePassed'] = True
                elif mutation == 'qualified': adversary['wholeProjectHealthComplete'] = True
                else: adversary['findingCount'] = -1
                try: health_schema.validate(adversary)
                except ValidationError: check(True, 'Health schema refuses ' + mutation)
                else: raise AssertionError('Health schema accepted ' + mutation)
            refuse(client, 'project_health_report', {**health_args, 'testSuiteResults': [imported['receipt'], imported['receipt']]}, 'InvalidRequest')
            refuse(client, 'project_health_report', {**health_args, 'testSuiteResults': [{**imported['receipt'], 'nativeQualified': True}]}, 'InvalidTestSuiteArtifact')
            other_review = json.loads(review_bytes); other_review['createdAtUtc'] = '2020-01-01T00:00:00Z'
            other_upload = upload(client, 'other-review.json', json.dumps(other_review).encode())
            other = client.tool('project_snapshot_import', {'uploadRef': other_upload['artifactRef']})
            refuse(client, 'project_health_report', {**health_args, 'reviewId': other['reviewId']}, 'TestSuiteReviewMismatch')
            page = client.tool('testsuite_result_review', {**imported['receipt'], 'section': 'findings', 'count': 1})
            check(page['total'] == 1 and page['nextOffset'] is None and page['rows'][0]['value'] == derived['findings'][0], 'Paged report matches exact downloadable report')
            refuse(client, 'testsuite_result_review', {**imported['receipt'], 'section': 'findings', 'offset': 2}, 'InvalidRequest')
            changed = copy.deepcopy(packet); changed['unexpected'] = True
            invalid = upload(client, 'invalid.json', json.dumps(changed).encode())
            refuse(client, 'testsuite_result_import', {'reviewId': review['reviewId'], 'uploadRef': invalid['artifactRef']}, 'InvalidTestSuiteArtifact')
            changed = copy.deepcopy(packet); changed['results'][0]['messages'] = []; changed['scope'] = []
            incomplete_upload = upload(client, 'incomplete.json', json.dumps(changed).encode())
            incomplete = client.tool('testsuite_result_import', {'reviewId': review['reviewId'], 'uploadRef': incomplete_upload['artifactRef']})
            junit = ET.fromstring(read(client, incomplete['junit']))
            check(junit.attrib['errors'] == '1' and junit.find("testcase/error[@type='incompleteSuppliedEvidence']") is not None, 'Incomplete supplied scope fails the explicit offline JUnit gate')
            check(not client.tool('project_health_report', {**health_args, 'testSuiteResults': [incomplete['receipt']]})['testSuite']['allSelectedSuppliedEvidencePassed'], 'Health refuses a passing classification for missing independent scope')
            clean_packet = copy.deepcopy(packet); clean_packet['results'][0]['messages'] = []
            clean_upload = upload(client, 'clean.json', json.dumps(clean_packet).encode())
            clean = client.tool('testsuite_result_import', {'reviewId': review['reviewId'], 'uploadRef': clean_upload['artifactRef']})
            clean_health = client.tool('project_health_report', {**health_args, 'testSuiteResults': [clean['receipt']]})['testSuite']
            check(clean_health['allSelectedSuppliedEvidencePassed'] and not clean_health['wholeProjectHealthComplete'] and not clean_health['nativeQualified'], 'Passing selected evidence remains unqualified and incomplete for the project')
            mixed = client.tool('project_health_report', {**health_args, 'testSuiteResults': [clean['receipt'], imported['receipt']]})['testSuite']
            check(mixed['selectionCount'] == 2 and mixed['resultCount'] == 2 and not mixed['allSelectedSuppliedEvidencePassed'], 'Distinct conflicting attempts are retained separately')
            clean_packet['results'][0]['errorCount'] = 2
            root_upload = upload(client, 'root-error.json', json.dumps(clean_packet).encode())
            root_error = client.tool('testsuite_result_import', {'reviewId': review['reviewId'], 'uploadRef': root_upload['artifactRef']})
            root_health = client.tool('project_health_report', {**health_args, 'testSuiteResults': [root_error['receipt']]})['testSuite']
            check(root_health['findingCount'] == 0 and root_health['reports'][0]['outcomeCounts']['executionError'] == 1 and not root_health['allSelectedSuppliedEvidencePassed'], 'Root errors remain visible even when there are no message findings')
            for item in (review_upload, result_upload): client.tool('artifact_upload_release', {'uploadId': item['uploadId']})
        finally: close(client)
        client = start()
        try:
            reopened = client.tool('testsuite_result_review', imported['receipt'])
            check(reopened == imported and read(client, reopened['input']) == packet_bytes, 'Actual restart and upload release preserve exact review and original input')
            check(client.tool('project_health_report', health_args) == health, 'Selected health evidence is stable after actual Host restart and upload release')
            path = (artifacts / imported['report']['relativePath']).resolve(); path.relative_to(artifacts.resolve())
            original = path.read_bytes()
            try:
                path.write_bytes(b'tampered')
                refuse(client, 'testsuite_result_review', imported['receipt'], 'ArtifactChanged')
                refuse(client, 'project_health_report', health_args, 'ArtifactChanged')
            finally: path.write_bytes(original)
            check(client.tool('testsuite_result_review', imported['receipt']) == imported, 'Restored exact artifact reopens without replay')
        finally: close(client)
        report['completed'] = True
    finally:
        report['count'] = len(report['checks'])
        report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
        (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS supplied Test Suite result MCP: {report['count']} checks, two clean Host lifetimes; no native execution")


if __name__ == '__main__':
    main()
