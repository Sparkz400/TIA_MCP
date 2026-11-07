"""Native tag-import regression in a newly owned scratch; never uses a user project."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid
from xml.etree import ElementTree as ET
from smoke_mcp import Client, ROOT


def candidate(address, data_type='Bool'):
    return f'''<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Signals</Name></AttributeList>
<ObjectList><SW.Tags.PlcTag ID="1" CompositionName="Tags"><AttributeList><Name>Added</Name><DataTypeName>{data_type}</DataTypeName>
<LogicalAddress>{address}</LogicalAddress></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    # A packaged bundle passes host/TiaMcp.Host.exe and workers/openness/TiaMcp.Openness.V21.exe.
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    host, worker = args.host.resolve(), args.worker.resolve()
    helper = ROOT / 'scripts/native/TagImportFixture/bin/Debug/net48/TagImportFixture.exe'
    client = Client(host, 180, ['--worker', str(worker), '--artifacts', str(root)])
    report = {'completed': False, 'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(name, flush=True)
        return data

    def refuse(name, arguments, code):
        result = client.request('tools/call', {'name': name, 'arguments': arguments})
        value = result.get('structuredContent') or json.loads(result['content'][0]['text'])
        assert result.get('isError') and value['error']['code'] == code, value
        record(code, value)

    def apply(prepared):
        return dict(changeId=prepared['changeId'], previewHash=prepared['previewHash'], checkpointSave=True,
                    compile=True, save=True, idempotencyKey=uuid.uuid4().hex)

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'tag-scope-regression', 'version': '1'}})
        client.notify('notifications/initialized')
        started = record('owned start', client.tool('tia_start'))
        assert started['ownsPortalProcess'] is True
        record('owned scratch', client.tool('tia_create_scratch'))
        status = client.tool('tia_session_status')
        project = status['projects'][0]
        inventory = client.tool('project_inventory')
        plc = inventory['plcs'][0]['path']
        auth = root / 'authorization.json'
        auth.write_text(json.dumps(dict(ownsPortalProcess=True, creationTool='tia_create_scratch',
                                       processId=started['processId'], projectPath=project['path'])), encoding='utf-8')
        record('seed inconsistent block', subprocess.run([str(helper), 'seed', str(auth)], check=True, text=True, capture_output=True).stdout)
        inv = client.tool('project_inventory')
        assert any(b['name'] == 'TagScopeBroken' and b['isConsistent'] is False for b in inv['blocks'])
        selectors = dict(projectName=project['name'], plcPath=plc)

        def prepare(address, data_type='Bool'):
            native = client.tool('tag_table_read', dict(selectors, tagTableName='Signals'))
            document = ET.fromstring(native['xml'])
            table = document.find('SW.Tags.PlcTagTable')
            objects = table.find('ObjectList')
            added = next((t for t in objects.findall('SW.Tags.PlcTag') if t.findtext('AttributeList/Name') == 'Added'), None)
            if added is None:
                added = ET.fromstring(candidate(address, data_type)).find('SW.Tags.PlcTagTable/ObjectList/SW.Tags.PlcTag')
                objects.append(added)
            else:
                added.find('AttributeList/LogicalAddress').text = address
                added.find('AttributeList/DataTypeName').text = data_type
            for index, element in enumerate(e for e in document.iter() if 'ID' in e.attrib):
                element.set('ID', format(index, 'X'))
            return client.tool('change_prepare', dict(selectors, kind='tag_table_import_xml',
                payload={'xml': ET.tostring(document, encoding='unicode'), 'expectedTableNames': ['Signals']}))

        refuse('change_prepare', dict(selectors, kind='tag_table_import_xml', payload={'xml': candidate('%M50.1'), 'expectedTableNames': ['Signals']}), 'TagScopeMismatch')
        prepared = record('prepare despite inconsistent block', prepare('%M50.1'))
        assert prepared['fingerprintCoverage']['scopePolicy'] == 'tag-table-content-and-project-metadata-v1'
        applied = record('apply with existing diagnostic', client.tool('change_apply', apply(prepared)))
        assert applied['state'] == 'applied' and applied['compile']['compilerSucceeded'] is False
        assert applied['tagTableEvidence']['baselineCompile']['errorCount'] == applied['compile']['errorCount'] > 0
        table = record('native table readback', client.tool('tag_table_read', dict(selectors, tagTableName='Signals')))
        assert '<Name>Added</Name>' in table['xml'] and '<Name>Existing</Name>' in table['xml']
        refuse('change_restore', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash']}, 'ScopedRecoveryRequired')
        stale = record('prepare stale candidate', prepare('%M50.2'))
        record('external scratch tag edit', subprocess.run([str(helper), 'drift', str(auth)], check=True, text=True, capture_output=True).stdout)
        refuse('change_apply', apply(stale), 'StaleRevision')
        # An unused tag with an out-of-range address compiles; the regression needs a reader.
        record('native consumer diagnostic', subprocess.run([str(helper), 'consume', str(auth)], check=True, text=True, capture_output=True).stdout)
        bad = record('prepare consumed type change', prepare('%MW60', 'Int'))
        refuse('change_apply', apply(bad), 'CompileFailed')
        outcome = record('retain regression failure', client.tool('change_status', {'changeId': bad['changeId'], 'includeDetails': True}))
        assert outcome['state'] == 'recovery_required' and Path(outcome['recoveryArchive']).is_file()
        assert 'TagScopeConsumer' in json.dumps(outcome), 'regression must be the consumer diagnostic'
        report['completed'] = True
    finally:
        record('owned worker shutdown', client.close())
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
