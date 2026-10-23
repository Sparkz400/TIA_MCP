"""Qualify expanded edits in a fresh worker-owned offline scratch; never attaches to a user project."""
import argparse
import json
import pathlib
import uuid
import hashlib
import xml.etree.ElementTree as ET

from smoke_mcp import Client, ROOT
from run_conformance import safe_xml
from engineering_preservation import check_native_blocks, native_payload_hash, recover_and_reopen


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=pathlib.Path, default=ROOT / 'src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=pathlib.Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--report', type=pathlib.Path, default=ROOT / 'artifacts/engineering-layers.json')
    parser.add_argument('--artifacts', type=pathlib.Path, default=ROOT / 'artifacts', help='Keep native scratch/recovery paths under workspace artifacts.')
    parser.add_argument('--section', choices=['all', 'objects', 'graphical', 'calls'], default='all')
    parser.add_argument('--timeout', type=int, default=300, help='Per-tool timeout, including each fresh TIA startup.')
    args = parser.parse_args()
    report = {'schemaVersion': 2, 'completed': False, 'steps': [], 'failures': [], 'qualifiedSections': [],
              'scope': 'Fresh worker-owned offline scratch only',
              'preservationScope': 'Whole native XML except top-level DocumentInfo and serializer indentation; comments, IDs, significant whitespace and QName bindings retained',
              'compilationScope': 'Native compile/save at apply; reopens independently inspect both saved copies without an extra mutation or compile'}
    report['binaries'] = {name: {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()} for name, path in [('host', args.host), ('worker', args.worker)]}
    report['harness'] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in (pathlib.Path(__file__).resolve(), pathlib.Path(__file__).with_name('engineering_preservation.py').resolve())}
    assert args.artifacts.resolve().is_relative_to((ROOT / 'artifacts').resolve()), 'Qualification artifacts must remain in this workspace'
    client = Client(args.host, args.timeout, ['--worker', str(args.worker.resolve()), '--artifacts', str(args.artifacts.resolve())])

    def persist():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')

    def call(name, arguments=None, expect=None):
        raw = client.request('tools/call', {'name': name, 'arguments': arguments or {}})
        value = raw.get('structuredContent') or json.loads(raw['content'][0]['text'])
        report['steps'].append({'tool': name, 'arguments': arguments or {}, 'isError': bool(raw.get('isError')), 'result': value})
        persist()
        if expect:
            assert raw.get('isError') and value.get('error', {}).get('code') == expect, value
        else:
            assert not raw.get('isError'), value
        if name == 'tia_start' and not expect:
            owned_ids = report.setdefault('ownedProcessIds', [])
            assert value['processId'] not in owned_ids, 'Fresh start must return a new TIA process'
            owned_ids.append(value['processId'])
            persist()
        print('PASS ' + name + (' rejects ' + expect if expect else ''), flush=True)
        return value

    def apply(kind, payload):
        prepared = call('change_prepare', {'kind': kind, 'payload': payload})
        try:
            result = call('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
            assert result['state'] == 'applied' and result['compile']['errorCount'] == 0
            assert 'changed_project_saved' in result['stages'], 'Save must complete before preservation qualification'
            assert result['preview'] == prepared['preview'], 'Apply/readback must not mutate the hashed preview'
            assert result['previewHash'] == prepared['previewHash']
            return prepared, result
        except Exception:
            status = call('change_status', {'changeId': prepared['changeId']})
            if status.get('recoveryArchive'):
                call('change_restore', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash']})
            raise

    def native_block(name):
        return call('block_read', {'blockName': name})['xml']

    def native_tree(xml):
        native_payload_hash(xml)  # Reject DTD/entity declarations before parsing.
        return ET.fromstring(xml, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True, insert_pis=True)))

    def qualify_graphical(language):
        name = 'Layer' + language
        evidence = {'blockName': name, 'language': language, 'status': 'running',
                    'operation': 'graphical_network_patch', 'compilePhase': 'apply-before-save'}
        report.setdefault('graphicalFixtures', []).append(evidence)
        persist()
        try:
            fixture = call('fixture_sources', {'language': language, 'blockName': name})
            apply(fixture['kind'], fixture['payload'])
            before = native_block(name)
            xml = native_tree(before)
            unit = xml.find('.//SW.Blocks.CompileUnit')
            access = next(node for node in unit.iter() if isinstance(node.tag, str) and node.tag.rsplit('}', 1)[-1] == 'Access'
                          and any(child.get('Name') == 'Enable' for child in node.iter()))
            next(child for child in access.iter() if child.get('Name') == 'Enable').set('Name', 'EnableAlternate')
            expected = ET.tostring(xml, encoding='unicode')
            patch = ET.tostring(access, encoding='unicode')
            prepared, applied = apply('graphical_network_patch', {'blockName': name, 'networkId': unit.get('ID'), 'upsertXml': [patch]})
            evidence.update(changeId=prepared['changeId'], previewHash=prepared['previewHash'], compile=applied['compile'],
                            processIdAtApply=report['ownedProcessIds'][-1],
                            beforeNativeXml=before, expectedNativeXml=expected,
                            baselinePayloadSha256=native_payload_hash(before), expectedPayloadSha256=native_payload_hash(expected))
            persist()
            try:
                evidence['appliedReadback'] = {}
                check_native_blocks(call, {name: expected}, evidence['appliedReadback'], persist)
                evidence['nativePreservationPassed'] = True
                persist()
            finally:
                recover_and_reopen(call, prepared, {name: before}, {name: expected}, evidence, persist)
            evidence['status'] = 'passed'
            persist()
        except Exception as error:
            evidence.update(status='failed', failure=type(error).__name__ + ': ' + str(error))
            persist()
            raise

    def qualify_call(caller, block_type, instance_kind, instance_scope, instance_components, instance_resolution, preserved_blocks):
        """Exercise one native SCL argument edit without changing the instance or other XML."""
        evidence = {'caller': caller, 'blockType': block_type, 'expectedInstanceKind': instance_kind,
                    'expectedInstanceComponents': instance_components, 'expectedInstanceResolution': instance_resolution, 'status': 'running',
                    'comparisonScope': 'Whole native document except export DocumentInfo; only Enable operand A to Alternate may change',
                    'operation': 'call_parameter_set', 'compilePhase': 'apply-before-save'}
        report.setdefault('callFixtures', []).append(evidence)
        persist()

        def read_selected():
            inspected = call('engineering_read', {'selectors': {'scope': 'calls', 'blockName': caller}})
            assert inspected['truncated'] is False and len(inspected['calls']) == 1, inspected
            selected = inspected['calls'][0]
            assert selected['language'] == 'SCL' and selected['blockType'] == block_type, selected
            assert selected['instanceKind'] == instance_kind, selected
            assert selected['instanceScope'] == instance_scope, selected
            assert selected['instanceComponents'] == instance_components, selected
            # These expected states come from the captured native fixtures. The
            # global FB Component carries HasQuotes metadata, which the conservative
            # parser leaves unresolved. Do not generalize that to missing metadata.
            assert selected['instanceResolution'] == instance_resolution, selected
            if instance_resolution == 'unresolved':
                instance = safe_xml(selected['rawXml']).find('./{*}Instance')
                assert instance is not None and len(instance) == 1, selected
                component = instance[0]
                assert component.tag.rsplit('}', 1)[-1] == 'Component' and len(component) == 1, selected
                metadata = component[0]
                assert metadata.tag.rsplit('}', 1)[-1] == 'BooleanAttribute' and metadata.get('Name') == 'HasQuotes' and metadata.text == 'true', selected
            parameters = {parameter['name']: parameter for parameter in selected['parameters']}
            assert set(parameters) == {'Enable', 'Active'}, parameters
            assert parameters['Enable']['resolution'] == 'symbolic' and parameters['Enable']['scope'] == 'LocalVariable', parameters
            assert parameters['Active']['resolution'] == 'symbolic' and parameters['Active']['components'] == ['B'], parameters
            assert parameters['Active']['direction'] == 'write', parameters
            return selected, inspected['nativeXml']

        try:
            selected, evidence['beforeInspectionXml'] = read_selected()
            assert next(parameter for parameter in selected['parameters'] if parameter['name'] == 'Enable')['components'] == ['A'], selected
            if block_type == 'FC':
                assert selected['calledTargetComponents'] == ['LayerCallee'] and selected['calledTargetResolution'] == 'symbolicOnly', selected
            evidence['beforeCall'] = selected
            evidence['observedInstanceResolution'] = selected['instanceResolution']
            baseline = native_block(caller)
            protected = {name: native_block(name) for name in preserved_blocks}
            expected = native_tree(baseline)
            ns = {'st': 'http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4'}
            units = [unit for unit in expected.iter('SW.Blocks.CompileUnit') if unit.get('ID') == selected['networkId']]
            assert len(units) == 1
            infos = [info for info in units[0].findall('.//st:CallInfo', ns) if info.get('UId') == selected['callUid']]
            assert len(infos) == 1
            parameters = [parameter for parameter in infos[0].findall('st:Parameter', ns) if parameter.get('Name') == 'Enable']
            assert len(parameters) == 1
            components = parameters[0].findall('st:Access/st:Symbol/st:Component', ns)
            assert len(components) == 1 and components[0].get('Name') == 'A'
            components[0].set('Name', 'Alternate')
            expected_xml = ET.tostring(expected, encoding='unicode')
            prepared, applied = apply('call_parameter_set', {'blockName': caller, 'networkId': selected['networkId'],
                'callUid': selected['callUid'], 'parameterName': 'Enable', 'components': ['Alternate']})
            evidence.update(changeId=prepared['changeId'], previewHash=prepared['previewHash'], compile=applied['compile'],
                            processIdAtApply=report['ownedProcessIds'][-1],
                            beforeNativeXml=baseline, expectedNativeXml=expected_xml,
                            baselinePayloadSha256=native_payload_hash(baseline), expectedPayloadSha256=native_payload_hash(expected_xml))
            persist()
            after = None
            try:
                after, evidence['afterInspectionXml'] = read_selected()
                evidence['afterCall'] = after
                assert next(parameter for parameter in after['parameters'] if parameter['name'] == 'Enable')['components'] == ['Alternate'], after
                # Preserve all instance/target identities, UIds and every other argument.
                assert {key: value for key, value in after.items() if key not in ('parameters', 'rawXml')} == {
                    key: value for key, value in selected.items() if key not in ('parameters', 'rawXml')}
                assert [parameter for parameter in after['parameters'] if parameter['name'] != 'Enable'] == [
                    parameter for parameter in selected['parameters'] if parameter['name'] != 'Enable']
                evidence['appliedReadback'] = {}
                check_native_blocks(call, {caller: expected_xml, **protected}, evidence['appliedReadback'], persist)
                evidence['nativePreservationPassed'] = True
                evidence['supportingBlocksPreserved'] = list(protected)
                persist()
            finally:
                # A successful apply is always restored, including if a preservation assertion fails.
                recover_and_reopen(call, prepared, {caller: baseline, **protected}, {caller: expected_xml, **protected},
                                   evidence, persist, read_selected if after is not None else None, selected, after)
            evidence['status'] = 'passed'
            persist()
        except Exception as error:
            evidence.update(status='failed', failure=type(error).__name__ + ': ' + str(error))
            persist()
            raise

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'expanded-engineering', 'version': '1'}})
        client.notify('notifications/initialized')
        started = call('tia_start')
        assert started['ownsPortalProcess'] is True and started['projects'] == [], started
        scratch = call('tia_create_scratch', {'cpuTypeIdentifier': 'OrderNumber:6ES7 511-1AL03-0AB0/V4.0'})
        report['project'] = scratch['project']['path']
        report['target'] = {'cpuTypeIdentifier': 'OrderNumber:6ES7 511-1AL03-0AB0/V4.0',
                            'tiaVersion': 'V21', 'scope': 'PLC root', 'scratchCreation': scratch}
        inventory = call('software_inspect', {'maxItems': 2000})
        scope = inventory['plcPath']

        if args.section in ('all', 'objects'):
            for kind in ('blocks', 'types', 'tags'):
                path = scope + '/' + kind
                apply('group_create', {'groupKind': kind, 'groupPath': path, 'name': 'LayerFolder'})
                call('change_prepare', {'kind': 'group_create', 'payload': {'groupKind': kind, 'groupPath': path, 'name': 'LayerFolder'}}, 'ObjectExists')
                apply('group_rename', {'groupKind': kind, 'groupPath': path + '/groups/LayerFolder', 'name': 'LayerFolder2'})
                apply('group_delete', {'groupKind': kind, 'groupPath': path + '/groups/LayerFolder2', 'dependencyPolicy': 'rejectReferenced'})

            apply('type_source_upsert', {'source': 'TYPE "LayerType"\nVERSION : 0.1\nSTRUCT\nValue : Int;\nEND_STRUCT;\nEND_TYPE', 'expectedTypeNames': ['LayerType']})
            before_type = call('type_read', {'typeName': 'LayerType'})
            assert any(member['name'] == 'Value' for section in before_type['sections'] for member in section['members'])
            apply('type_rename', {'typeName': 'LayerType', 'newName': 'LayerTypeRenamed'})
            call('type_read', {'typeName': 'LayerTypeRenamed'})
            apply('source_upsert', {'language': 'SCL', 'source': 'DATA_BLOCK "LayerDb"\n{ S7_Optimized_Access := \'TRUE\' }\nVERSION : 0.1\nVAR\nItem : "LayerTypeRenamed";\nValues : Array[1..3] of Int := [4, 5, 6];\nEND_VAR\nBEGIN\nEND_DATA_BLOCK', 'expectedBlockNames': ['LayerDb']})
            call('change_prepare', {'kind': 'type_delete', 'payload': {'typeName': 'LayerTypeRenamed', 'dependencyPolicy': 'rejectReferenced'}}, 'DependencyExists')
            interface = call('block_interface', {'blockName': 'LayerDb'})
            array = next(member for section in interface['sections'] for member in section['members'] if member['name'] == 'Values')
            assert [value['startValue'] for value in array['elementValues']] == ['4', '5', '6'], array
            apply('block_delete', {'blockName': 'LayerDb'})
            apply('type_delete', {'typeName': 'LayerTypeRenamed', 'dependencyPolicy': 'rejectReferenced'})

            tags = '<Document><Engineering version="V21"/><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>LayerTags</Name></AttributeList><ObjectList><SW.Tags.PlcTag ID="1" CompositionName="Tags"><AttributeList><Name>LayerTag</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%M100.0</LogicalAddress></AttributeList></SW.Tags.PlcTag><SW.Tags.PlcUserConstant ID="2" CompositionName="UserConstants"><AttributeList><Name>LayerConstant</Name><DataTypeName>Int</DataTypeName><Value>1</Value></AttributeList></SW.Tags.PlcUserConstant></ObjectList></SW.Tags.PlcTagTable></Document>'
            apply('tag_table_import_xml', {'xml': tags, 'expectedTableNames': ['LayerTags']})
            apply('tag_set', {'tagTableName': 'LayerTags', 'memberName': 'LayerTag', 'attributes': {'LogicalAddress': '%M100.1'}})
            apply('constant_set', {'tagTableName': 'LayerTags', 'memberName': 'LayerConstant', 'attributes': {'Value': '2'}})
            table = call('tag_table_read', {'tagTableName': 'LayerTags'})
            assert '%M100.1' in table['xml'] and '<Value>2</Value>' in table['xml']
            apply('tag_delete', {'tagTableName': 'LayerTags', 'memberName': 'LayerTag', 'dependencyPolicy': 'rejectReferenced'})
            apply('constant_delete', {'tagTableName': 'LayerTags', 'memberName': 'LayerConstant', 'dependencyPolicy': 'rejectReferenced'})
            report['qualifiedSections'].append('objects-and-data')
            persist()

        if args.section in ('all', 'graphical'):
            for language in ('LAD', 'FBD'):
                qualify_graphical(language)
            report['qualifiedSections'].append('lad-fbd-native-patch-preservation-recovery-and-save-reopen')
            persist()

        if args.section in ('all', 'calls'):
            fixture = call('fixture_sources', {'language': 'SCL', 'blockName': 'LayerCallee'})
            apply(fixture['kind'], fixture['payload'])
            source = '''FUNCTION "LayerCaller" : Void
VERSION : 0.1
VAR_INPUT
    A : Bool;
    Alternate : Bool;
END_VAR
VAR_OUTPUT
    B : Bool;
END_VAR
BEGIN
    // Preserve this statement and comment while changing only the call operand.
    #B := FALSE;
    "LayerCallee"(Enable := #A, Active => #B);
END_FUNCTION'''
            apply('source_upsert', {'language': 'SCL', 'source': source, 'expectedBlockNames': ['LayerCaller']})
            qualify_call('LayerCaller', 'FC', 'notApplicable', 'GlobalVariable', ['LayerCallee'], 'notApplicable', ['LayerCallee'])

            # Same native declarations as the scratch FB/DB source and catalog multi-instance fixture.
            fb_source = '''FUNCTION_BLOCK "LayerFbCallee"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
VAR_INPUT
    Enable : Bool;
END_VAR
VAR_OUTPUT
    Active : Bool;
END_VAR
BEGIN
    #Active := #Enable;
END_FUNCTION_BLOCK

DATA_BLOCK "LayerFbInstance"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
"LayerFbCallee"
BEGIN
END_DATA_BLOCK

FUNCTION "LayerInstanceCaller" : Void
VERSION : 0.1
VAR_INPUT
    A : Bool;
    Alternate : Bool;
END_VAR
VAR_OUTPUT
    B : Bool;
END_VAR
BEGIN
    // The global instance and output connection must remain unchanged.
    #B := FALSE;
    "LayerFbInstance"(Enable := #A, Active => #B);
END_FUNCTION

FUNCTION_BLOCK "LayerMultiCaller"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
VAR_INPUT
    A : Bool;
    Alternate : Bool;
END_VAR
VAR_OUTPUT
    B : Bool;
END_VAR
VAR
    Nested : "LayerFbCallee";
END_VAR
BEGIN
    // Preserve the static multi-instance declaration and all other source.
    #B := FALSE;
    #Nested(Enable := #A, Active => #B);
END_FUNCTION_BLOCK'''
            apply('source_upsert', {'language': 'SCL', 'source': fb_source,
                'expectedBlockNames': ['LayerFbCallee', 'LayerFbInstance', 'LayerInstanceCaller', 'LayerMultiCaller']})
            qualify_call('LayerInstanceCaller', 'FB', 'instance', 'GlobalVariable', ['LayerFbInstance'], 'unresolved', ['LayerFbCallee', 'LayerFbInstance'])
            qualify_call('LayerMultiCaller', 'FB', 'multiInstance', 'LocalVariable', ['Nested'], 'symbolicOnly', ['LayerFbCallee', 'LayerFbInstance'])
            report['qualifiedSections'].append('scl-fc-fb-instance-multi-instance-arguments-preservation-recovery-and-save-reopen')
            persist()
        report['completed'] = True
    except Exception as error:
        report['failures'].append(type(error).__name__ + ': ' + str(error))
        raise
    finally:
        try:
            call('tia_detach')
        finally:
            client.close()
            report['hostExitCode'] = client.process.returncode
            report['hostLogs'] = client.errors
            persist()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
