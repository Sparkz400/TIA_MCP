"""Exercise the actual offline AutomationML MCP workflow with nonexistent Siemens workers."""
import argparse
import base64
import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path
from jsonschema import Draft202012Validator
from smoke_mcp import Client, ROOT


def verify_fixture_structure(raw, report):
    """Independent traversal of trusted local fixtures; ignore extension payloads."""
    root = ET.fromstring(raw)
    ns = root.tag.partition('}')[0] + '}' if root.tag.startswith('{') else ''
    kinds = {'InstanceHierarchy', 'InternalElement', 'ExternalInterface', 'SystemUnitClassLib', 'SystemUnitClass',
             'RoleClassLib', 'RoleClass', 'InterfaceClassLib', 'InterfaceClass', 'AttributeTypeLib', 'AttributeType'}
    objects, links = [], []

    def walk(element):
        if not element.tag.startswith(ns): return
        kind = element.tag[len(ns):]
        if kind in kinds: objects.append((kind, element.get('ID'), element.get('Name')))
        if kind == 'InternalLink': links.append((element.get('Name'), element.get('RefPartnerSideA'), element.get('RefPartnerSideB')))
        if kind not in kinds | {'CAEXFile', 'Attribute', 'RoleRequirements', 'SupportedRoleClass'}: return
        for child in element: walk(child)

    walk(root)
    if objects != [(o['kind'], o.get('id'), o.get('name')) for o in report['objects']]: raise AssertionError('Independent object traversal differs')
    if links != [(o.get('name'), o.get('sideA'), o.get('sideB')) for o in report['links']]: raise AssertionError('Independent link traversal differs')
    if hashlib.sha256(raw).hexdigest() != report['sourceSha256']: raise AssertionError('Original digest differs')


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
    schema_path = ROOT / 'contracts/automationml-workflow.v1.schema.json'
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
        nonlocal client
        connection = Client(args.host.resolve(), 90, ['--worker', str(missing), '--sim-worker', str(missing), '--artifacts', str(artifacts)])
        client = connection
        connection.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                          'clientInfo': {'name': 'automationml-offline-contract', 'version': '1'}})
        connection.notify('notifications/initialized')
        return connection

    def stop():
        nonlocal client
        receipt = client.close(); client = None; report['hostExits'].append(receipt)
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
            chunk = call('automationml_artifact_read', dict(recordRef=reference, name=name, offset=len(data), count=8192))
            check('chunk offsets and lengths are contiguous', chunk['offset'] == len(data) and chunk['nextOffset'] == len(data)+chunk['count'])
            data.extend(base64.b64decode(chunk['data'], validate=True))
            if chunk['complete']:
                check('complete transfer independently matches bytes and SHA-256', len(data) == chunk['bytes'] and digest(data) == chunk['sha256'])
                return bytes(data)

    try:
        client = start()
        check('six AutomationML tools registered in normal Host', tools <= {t['name'] for t in client.request('tools/list')['tools']})
        raw = (ROOT / 'tests/TiaMcp.AutomationMl.Tests/fixtures/upstream/9_ExtInt_IntLink.aml').read_bytes()
        uploaded = upload(raw)
        first = call('automationml_inspect', dict(xmlUploadRef=uploaded['artifactRef'])); before = first['recordRef']
        check('native, importability and complete impact qualification remain false',
              all(first[k] is False for k in ['nativeQualified', 'importabilityCertified', 'impactScopeComplete']))
        call('artifact_upload_release', dict(uploadId=uploaded['uploadId']))
        stop(); client = start()
        check('restart reopens immutable record after source upload release', call('automationml_reopen', dict(recordRef=before)) == first)
        check('download retains exact source bytes', download(before, 'original.aml') == raw)
        report_bytes = download(before, 'report.json'); inspection = json.loads(report_bytes)
        (output / 'inspection.json').write_bytes(report_bytes)
        verify_fixture_structure(raw, inspection)
        check('independent Python traversal matches 2.15 objects and links', True)
        check('report source digest and public schema validation bind original', inspection['sourceSha256'] == digest(raw) and inspection['schemaValid'])
        check('receipt digest pins the handle', digest(download(before, 'receipt.json')) == before.split(':')[-1])
        steps = []; offset = 0
        while offset is not None:
            page = call('automationml_report_page', dict(recordRef=before, section='/objects', offset=offset, count=1))
            check('page hash binds the downloaded report', page['reportSha256'] == digest(report_bytes))
            steps.extend(r['value']['path'] for r in page['rows']); offset = page['nextOffset']
        check('all object rows appear once in exact report order', steps == [row['path'] for row in inspection['objects']])
        second = call('automationml_inspect', dict(xmlUploadRef=upload(raw.replace(b'firstScrewdriver', b'firstScrewdriverRevised'))['artifactRef']))
        comparison = call('automationml_compare', dict(beforeRef=before, afterRef=second['recordRef']))
        check('comparison retains exact inputs and explicit changes', comparison['inputs'] == [before, second['recordRef']] and comparison['sourceBytesChanged'] and comparison['counts']['changes'] > 0)
        check('comparison pages contain retained changes', bool(call('automationml_report_page', dict(recordRef=comparison['recordRef'], section='/changes'))['rows']))
        raw3 = (ROOT / 'tests/TiaMcp.AutomationMl.Tests/fixtures/upstream/medium-valid-robot-cell.aml').read_bytes()
        third = call('automationml_inspect', dict(xmlUploadRef=upload(raw3)['artifactRef']))
        report3 = json.loads(download(third['recordRef'], 'report.json'))
        verify_fixture_structure(raw3, report3)
        check('independent 3.0 traversal, schema and resolved endpoints', third['schemaValid'] and report3['profile'] == 'CAEX 3.0' and
              all(link['targetA'] and link['targetB'] for link in report3['links']))
        cross = call('automationml_compare', dict(beforeRef=before, afterRef=third['recordRef']))
        check('cross-profile comparison never claims reliable matching', cross['matchingReliable'] is False)
        opaque_raw = raw.replace(b'<Attribute Name="New Attribute" />', b'<Attribute Name="New Attribute"><Value><vendor>12</vendor></Value></Attribute>')
        check('opaque regression fixture changes the exact expected element', opaque_raw != raw)
        opaque = call('automationml_inspect', dict(xmlUploadRef=upload(opaque_raw)['artifactRef']))
        opaque_report = json.loads(download(opaque['recordRef'], 'report.json'))
        check('schema-valid vendor Value stays unknown through actual MCP', opaque['schemaValid'] and
              next(a for a in opaque_report['attributes'] if a['name'] == 'New Attribute')['values'] == [None] and
              any(d['code'] == 'OpaqueAttributeValue' for d in opaque_report['diagnostics']))
        check('vendor Value original remains exact', download(opaque['recordRef'], 'original.aml') == opaque_raw)
        colon_raw = raw.replace(b'EnergySupply', b'Energy:Supply')
        colon = call('automationml_inspect', dict(xmlUploadRef=upload(colon_raw)['artifactRef']))
        colon_report = json.loads(download(colon['recordRef'], 'report.json'))
        check('full interface name including colon resolves through actual MCP', colon['schemaValid'] and
              all(link['targetA'] and link['targetB'] for link in colon_report['links']))
        reject('automationml_compare', dict(beforeRef=comparison['recordRef'], afterRef=before), 'AutomationMlInvalidReference')
        reject('automationml_report_page', dict(recordRef=before, section='/sourceSha256'), 'AutomationMlInvalidSection')
        reject('automationml_report_page', dict(recordRef=before, section='/objects', count=201), 'AutomationMlPageLimit')
        reject('automationml_artifact_read', dict(recordRef=before, name='../intent.json'), 'AutomationMlArtifactMissing')
        reject('automationml_artifact_read', dict(recordRef=before, name='original.aml', count=65537), 'InvalidRequest')
        reject('automationml_inspect', dict(xmlUploadRef=upload(b'<!DOCTYPE AutomationMl [<!ENTITY e SYSTEM "file:///denied">]><AutomationMl>&e;</AutomationMl>')['artifactRef']), 'AutomationMlInvalidXml')
        retained = artifacts / 'automationml-workflow' / before.split(':')[1] / 'original.aml'
        retained.write_bytes(raw+b' ')
        reject('automationml_reopen', dict(recordRef=comparison['recordRef']), 'AutomationMlIntegrityFailure')
        reject('automationml_artifact_read', dict(recordRef=comparison['recordRef'], name='report.json'), 'AutomationMlIntegrityFailure')
        stop(); client = start()
        check('tampered records remain discoverable after restart', any(r['state'] == 'integrity-failed' for r in call('automationml_list')['rows']))
        retained.write_bytes(raw)
        check('restored exact source revalidates input chain', call('automationml_reopen', dict(recordRef=comparison['recordRef'])) == comparison)
        (artifacts / 'automationml-workflow' / ('f'*32)).mkdir()
        check('incomplete publication remains discoverable', any(r['state'] == 'incomplete' for r in call('automationml_list')['rows']))
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
    print(('PASS' if report['completed'] else 'FAIL')+' AutomationML actual stdio: '+str(report['checkCount'])+' checks; '+str(output/'verification.json'))
    if not report['completed']: print(report.get('failure', report.get('shutdownFailure')))
    return 0 if report['completed'] else 1


if __name__ == '__main__': raise SystemExit(main())
