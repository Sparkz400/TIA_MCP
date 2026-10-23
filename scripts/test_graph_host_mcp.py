"""Exercise the actual offline GRAPH MCP workflow with nonexistent Siemens workers."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator
from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists(): raise ValueError('Select a fresh evidence directory.')
    output.mkdir(parents=True)
    artifacts = output / 'host-artifacts'
    missing = output / 'absent-native-worker.exe'
    schema_path = ROOT / 'contracts/graph-workflow.v1.schema.json'
    schema = json.loads(schema_path.read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    tools = set(schema['x-tool-contracts'])
    allowed = tools | {'artifact_upload_begin', 'artifact_upload_chunk', 'artifact_upload_commit', 'artifact_upload_release'}
    digest = lambda data: hashlib.sha256(data).hexdigest()
    report = dict(completed=False, nativeExecuted=False, checks=[], hostExits=[],
                  hostSha256=digest(args.host.read_bytes()), contractSha256=digest(schema_path.read_bytes()),
                  harnessSha256=digest(Path(__file__).read_bytes()),
                  hostFiles=[dict(name=p.name, bytes=p.stat().st_size, sha256=digest(p.read_bytes()))
                             for p in sorted(args.host.parent.iterdir()) if p.is_file() and p.suffix in ('.exe', '.dll', '.json')])
    client = None

    def check(name, condition):
        if not condition: raise AssertionError(name)
        report['checks'].append(name)

    def start():
        connection = Client(args.host.resolve(), 90, ['--worker', str(missing), '--sim-worker', str(missing), '--artifacts', str(artifacts)])
        connection.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                          'clientInfo': {'name': 'graph-offline-contract', 'version': '1'}})
        connection.notify('notifications/initialized')
        return connection

    def stop():
        receipt = client.close(); report['hostExits'].append(receipt)
        check('Host exits normally without starting native workers', receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'] and
              all(row.get('started') is not True for row in receipt['workerExits']))

    def call(name, arguments=None):
        if name not in allowed: raise AssertionError('Non-offline tool attempted')
        if name in tools:
            Draft202012Validator(dict(schema, oneOf=[schema['x-tool-contracts'][name]['input']])).validate(arguments or {})
        result = client.tool(name, arguments or {})
        if name in tools:
            validator.validate(result)
            Draft202012Validator(dict(schema, oneOf=[schema['x-tool-contracts'][name]['output']])).validate(result)
        return result

    def reject(name, arguments, code):
        result = client.request('tools/call', {'name': name, 'arguments': arguments})
        value = result.get('structuredContent', {})
        validator.validate(value)
        check(name + ' refuses ' + code, result.get('isError') and value.get('error', {}).get('code') == code)

    def upload(data):
        status = call('artifact_upload_begin', dict(fileName='sequence.xml', expectedBytes=len(data), sha256=digest(data)))
        for offset in range(0, len(data), status['maxChunkBytes']):
            call('artifact_upload_chunk', dict(uploadId=status['uploadId'], offset=offset,
                                              data=base64.b64encode(data[offset:offset+status['maxChunkBytes']]).decode()))
        return call('artifact_upload_commit', dict(uploadId=status['uploadId']))

    def download(reference, name):
        data = bytearray()
        while True:
            chunk = call('graph_artifact_read', dict(recordRef=reference, name=name, offset=len(data), count=8192))
            check('chunk offsets and lengths are contiguous', chunk['offset'] == len(data) and chunk['nextOffset'] == len(data)+chunk['count'])
            data.extend(base64.b64decode(chunk['data'], validate=True))
            if chunk['complete']:
                check('complete transfer independently matches bytes and SHA-256', len(data) == chunk['bytes'] and digest(data) == chunk['sha256'])
                return bytes(data)

    try:
        client = start()
        check('six GRAPH tools registered in normal Host', tools <= {t['name'] for t in client.request('tools/list')['tools']})
        raw = (ROOT / 'tests/TiaMcp.Graph.Tests/fixtures/sequence-v1.xml').read_bytes()
        uploaded = upload(raw)
        first = call('graph_inspect', dict(xmlUploadRef=uploaded['artifactRef'])); before = first['recordRef']
        check('native, behavior and full schema qualification remain false',
              all(first[k] is False for k in ['nativeQualified', 'behavioralEquivalenceCertified', 'fullSchemaValidated']))
        call('artifact_upload_release', dict(uploadId=uploaded['uploadId']))
        stop(); client = start()
        check('restart reopens immutable record after source upload release', call('graph_reopen', dict(recordRef=before)) == first)
        check('download retains exact source bytes', download(before, 'original.xml') == raw)
        report_bytes = download(before, 'report.json'); inspection = json.loads(report_bytes)
        (output / 'inspection.json').write_bytes(report_bytes)
        check('report retains exact original source', base64.b64decode(inspection['originalSourceBase64'], validate=True) == raw)
        check('receipt digest pins the handle', digest(download(before, 'receipt.json')) == before.split(':')[-1])
        html = download(before, 'viewer.html'); (output / 'viewer.html').write_bytes(html)
        check('static viewer has no script and includes CSP', b'<script' not in html.lower() and b'Content-Security-Policy' in html)
        steps = []; offset = 0
        while offset is not None:
            page = call('graph_report_page', dict(recordRef=before, section='/graphs/0/sequences/0/steps', offset=offset, count=1))
            check('page hash binds the downloaded report', page['reportSha256'] == digest(report_bytes))
            steps.extend(r['value']['name'] for r in page['rows']); offset = page['nextOffset']
        check('all step rows appear once in source order', steps == ['Idle', 'Run A', 'Run B'])
        second = call('graph_inspect', dict(xmlUploadRef=upload((ROOT / 'tests/TiaMcp.Graph.Tests/fixtures/sequence-v2.xml').read_bytes())['artifactRef']))
        comparison = call('graph_compare', dict(beforeRef=before, afterRef=second['recordRef']))
        check('comparison retains exact inputs and explicit changes', comparison['inputs'] == [before, second['recordRef']] and comparison['sourceBytesChanged'] and comparison['counts']['changes'] > 0)
        check('comparison pages contain retained changes', bool(call('graph_report_page', dict(recordRef=comparison['recordRef'], section='/changes'))['rows']))
        reject('graph_compare', dict(beforeRef=comparison['recordRef'], afterRef=before), 'GraphInvalidReference')
        reject('graph_report_page', dict(recordRef=before, section='/originalSourceBase64'), 'GraphInvalidSection')
        reject('graph_report_page', dict(recordRef=before, section='/graphs', count=201), 'GraphPageLimit')
        reject('graph_artifact_read', dict(recordRef=before, name='../intent.json'), 'GraphArtifactMissing')
        reject('graph_artifact_read', dict(recordRef=before, name='original.xml', count=65537), 'InvalidRequest')
        reject('graph_inspect', dict(xmlUploadRef=upload(b'<!DOCTYPE Graph [<!ENTITY e SYSTEM "file:///denied">]><Graph>&e;</Graph>')['artifactRef']), 'InvalidXml')
        retained = artifacts / 'graph-workflow' / before.split(':')[1] / 'original.xml'
        retained.write_bytes(raw+b' ')
        reject('graph_reopen', dict(recordRef=comparison['recordRef']), 'GraphIntegrityFailure')
        reject('graph_artifact_read', dict(recordRef=comparison['recordRef'], name='report.json'), 'GraphIntegrityFailure')
        stop(); client = start()
        check('tampered records remain discoverable after restart', any(r['state'] == 'integrity-failed' for r in call('graph_list')['rows']))
        retained.write_bytes(raw)
        check('restored exact source revalidates input chain', call('graph_reopen', dict(recordRef=comparison['recordRef'])) == comparison)
        (artifacts / 'graph-workflow' / ('f'*32)).mkdir()
        check('incomplete publication remains discoverable', any(r['state'] == 'incomplete' for r in call('graph_list')['rows']))
        native_store = artifacts / 'engineering-operations'
        check('offline workflow creates no native engineering operation',
              (not native_store.exists() or not any(native_store.iterdir())) and not missing.exists())
        report['completed'] = True
    except Exception as error:
        report['failure'] = str(error)
    finally:
        if client is not None:
            try: stop()
            except Exception as error: report.update(completed=False, shutdownFailure=str(error))
        report['checkCount'] = len(report['checks'])
        (output / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(('PASS' if report['completed'] else 'FAIL')+' GRAPH actual stdio: '+str(report['checkCount'])+' checks; '+str(output/'verification.json'))
    if not report['completed']: print(report.get('failure', report.get('shutdownFailure')))
    return 0 if report['completed'] else 1


if __name__ == '__main__': raise SystemExit(main())
