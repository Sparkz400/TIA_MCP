"""Bounded software-inventory and native-export preservation for encoder fixtures.

This does not cover hardware settings, unexposed services or an atomic snapshot.
"""
import copy
from xml.dom import Node, minidom
from urllib.parse import quote

from engineering_preservation import require, native_payload_hash, decode_native_artifact
from encoder_endpoints import same

POLICY = 'encoder-software-peers-v1'
FAMILIES = (('blocks', 'block_read', 'blockPath', 'block'),
            ('types', 'type_read', 'typePath', 'type'),
            ('tagTables', 'tag_table_read', 'tagTablePath', 'tagTable'),
            ('technologyObjects', 'technology_read', 'technologyPath', 'technologyObject'))


INTERFACE = 'http://www.siemens.com/automation/Openness/SW/Interface/v5'
LOCATOR = ('Document/SW.TechnologicalObjects.TechnologicalInstanceDB/AttributeList/Interface/'
           'Sections/Section[@Name="Static"]/Member[@Name="Sensor"]/Sections/'
           'Section[@Name="None"]/Member[@Name="InverseDirection"]')


def expected_encoder_edit(xml):
    """Insert the one native true start value; no defaults or schemas are guessed."""
    native_payload_hash(xml)  # Reject DTD/entity declarations before parsing.
    document = minidom.parseString(xml)
    node = document.documentElement
    require(node.localName == 'Document' and not node.namespaceURI, "Native evidence requirement failed: node.localName == 'Document' and not node.namespaceURI")
    for namespace, tag, attributes in [
        ('', 'SW.TechnologicalObjects.TechnologicalInstanceDB', {}),
        ('', 'AttributeList', {}), ('', 'Interface', {}),
        (INTERFACE, 'Sections', {}), (INTERFACE, 'Section', {'Name': 'Static'}),
        (INTERFACE, 'Member', {'Name': 'Sensor'}), (INTERFACE, 'Sections', {}),
        (INTERFACE, 'Section', {'Name': 'None'}),
        (INTERFACE, 'Member', {'Name': 'InverseDirection', 'Datatype': 'Bool'}),
    ]:
        matches = [child for child in node.childNodes if child.nodeType == Node.ELEMENT_NODE
                   and (child.namespaceURI or '') == namespace and child.localName == tag
                   and all(child.getAttribute(name) == value for name, value in attributes.items())]
        require(len(matches) == 1, 'Native encoder locator is missing or ambiguous: ' + tag)
        node = matches[0]
    require(not node.childNodes, 'This fixture requires an empty native InverseDirection member before edit')
    start = document.createElementNS(INTERFACE, 'StartValue')
    start.appendChild(document.createTextNode('true'))
    node.appendChild(start)
    return document.toxml()



def owner(value, native, plc=True):
    require(value.get('project', {}).get('path') == native['project']['path'] and
            value.get('project', {}).get('name') == native['project']['name'] and
            type(value.get('processId')) is int and value['processId'] == native['processId'],
            'Software preservation read belongs to another project or process')
    if plc:
        require(value.get('plcPath') == native['plcPath'], 'Software preservation read belongs to another PLC')


def inventory_view(inventory, native):
    owner(inventory, native)
    require(inventory.get('warnings') == [] and inventory.get('completeness', {}).get('scopeComplete') is True and
            inventory['completeness'].get('truncated') is False, 'Complete unfiltered software inventory is required')
    root = '/projects/' + quote(native['project']['name'], safe='')
    plc = native['plcPath']
    require(plc.startswith(root + '/'), 'PLC path is outside its named project')

    def path(value):
        require(isinstance(value, str) and (value == plc or value.startswith(plc + '/')),
                'Software object path is outside its selected PLC')
        return value[len(root):]

    def normalize(value):
        if isinstance(value, list): return [normalize(item) for item in value]
        if not isinstance(value, dict): return value
        result = {}
        for key, item in value.items():
            if key == 'identity':
                require(isinstance(item, dict) and item.get('projectPath') == native['project']['path'] and
                        item.get('plcPath') == plc and item.get('objectPath') == value.get('path') and
                        item.get('scopePath') == value.get('scopePath', value.get('path')) and
                        item.get('name') == value.get('name'), 'Software identity contradicts its inventory row')
                result[key] = normalize({k: v for k, v in item.items() if k != 'projectPath'})
            elif key in ('path', 'ownerPath', 'scopePath', 'objectPath', 'plcPath', 'technologyPath'):
                result[key] = None if item is None and key in ('ownerPath', 'technologyPath') else path(item)
            elif key == 'groups':
                require(isinstance(item, list) and len(item) <= 50000 and len(item) == len(set(item)),
                        'Software group paths are missing, duplicated or unbounded')
                result[key] = sorted(path(group) for group in item)
            else: result[key] = normalize(item)
        return result

    view = {}
    for family in ('softwareScopes', *(entry[0] for entry in FAMILIES)):
        rows = inventory.get(family)
        require(isinstance(rows, list) and len(rows) <= 50000 and all(isinstance(row, dict) for row in rows),
                'Software inventory family is missing or unbounded: ' + family)
        paths = [row.get('path') for row in rows]
        require(all(isinstance(item, str) and item for item in paths) and len(paths) == len(set(paths)),
                'Software inventory paths are missing or ambiguous: ' + family)
        fields = {'softwareScopes': ('name', 'kind', 'identity', 'groups', 'namespacePreset'),
                  'blocks': ('name', 'number', 'kind', 'programmingLanguage', 'isConsistent', 'isKnowHowProtected', 'identity', 'plcPath', 'scopePath', 'technologyPath'),
                  'types': ('name', 'scopePath', 'isConsistent', 'isKnowHowProtected'),
                  'tagTables': ('name', 'scopePath', 'tags', 'constants'),
                  'technologyObjects': ('name', 'ownerPath', 'composition', 'systemLibraryType', 'systemLibraryVersion')}
        require(all(all(key in row for key in fields[family]) for row in rows), 'Software inventory metadata is missing: ' + family)
        if family == 'softwareScopes':
            require(rows and plc in paths and all('groups' in row and 'namespacePreset' in row for row in rows),
                    'Software scope and empty-group coverage is missing')
        view[family] = {path(row['path']): normalize(row) for row in rows}
    scopes = {row['path'] for row in inventory['softwareScopes']}
    for family in ('blocks', 'types', 'tagTables'):
        require(all(row.get('scopePath') in scopes for row in inventory[family]), 'Software object has no inventoried scope')
    technology = {row['path'] for row in inventory['technologyObjects']}
    selected = native['technologyObject']['path']
    require(selected in technology and same(next(row for row in inventory['technologyObjects'] if row['path'] == selected),
                                         native['technologyObject']), 'Selected encoder differs from software inventory')
    mapped = []
    for row in inventory['blocks']:
        require('technologyPath' in row and (row['technologyPath'] is None or row['technologyPath'] in technology),
                'Block-to-technology membership is missing or unresolved')
        if row['technologyPath'] is not None: mapped.append(row['technologyPath'])
    require(len(mapped) == len(set(mapped)), 'Multiple blocks alias one technology object')
    coverage = inventory.get('technologyBlockMembership', {})
    require(coverage.get('basis') == 'nativeObjectEquality' and coverage.get('scopeComplete') is True and
            coverage.get('atomicSnapshot') is False and
            all(type(coverage.get(key)) is int and coverage[key] == expected for key, expected in (
                ('technologyObjects', len(technology)), ('blockObjects', len(inventory['blocks'])), ('mappedBlocks', len(mapped)))),
            'Native technology/block membership coverage differs')
    return view


def capture(call, native, selector):
    require(selector == {'projectName': native['project']['name'], 'plcPath': native['plcPath']},
            'Software preservation selector differs from selected encoder')
    inventory = call('software_inspect', {**selector, 'maxItems': 2000})
    inventory_view(inventory, native)
    exports = []
    selected = native['technologyObject']['path']
    for family, tool, argument, field in FAMILIES:
        for row in sorted(inventory[family], key=lambda item: item['path']):
            if family == 'technologyObjects' and row['path'] == selected:
                continue  # The original selected export is already bound independently.
            arguments = {**selector, argument: row['path']}
            if tool in ('technology_read', 'type_read'): arguments['maxItems'] = 2000
            if tool == 'technology_read': arguments['exportXml'] = True
            read = call(tool, arguments)
            owner(read, native, plc=tool != 'block_read')
            require(isinstance(read.get(field), dict) and read[field].get('path') == row['path'] and
                    read[field].get('name') == row['name'], 'Software export selected another object')
            if tool == 'block_read': require(read[field].get('plcPath') == native['plcPath'], 'Block export belongs to another PLC')
            if tool == 'technology_read':
                require(same(read[field], row), 'Technology export metadata differs from inventory')
            xml = decode_native_artifact(read, call('artifact_read', {'path': read['artifact']['path']}))
            digest = native_payload_hash(xml)
            if family == 'blocks' and row['technologyPath'] is not None:
                # Do not discard generated-looking blocks or guess aliases by name.
                if row['technologyPath'] == selected:
                    require(digest == native_payload_hash(native['xml']), 'Selected technology/block exports disagree')
            exports.append({'family': family, 'path': row['path'], 'read': read, 'xml': xml, 'payloadSha256': digest})
    evidence = {'policy': POLICY, 'inventory': inventory, 'exports': exports,
                'atomicSnapshot': False, 'fullProjectPreservationVerified': False}
    native['softwarePeers'] = evidence
    return evidence


def compare(before, after):
    first, second = before.get('softwarePeers', {}), after.get('softwarePeers', {})
    require(first.get('policy') == second.get('policy') == POLICY, 'Software peer evidence is required')
    require(same(inventory_view(first['inventory'], before), inventory_view(second['inventory'], after)),
            'Software inventory, metadata or empty groups changed')

    def peers(native, evidence):
        root = '/projects/' + quote(native['project']['name'], safe='')
        mapped = {row['path'] for row in evidence['inventory']['blocks']
                  if row['technologyPath'] == native['technologyObject']['path']}
        rows = {}
        expected = {(family, row['path']) for family, *_ in FAMILIES for row in evidence['inventory'][family]
                    if not (family == 'technologyObjects' and row['path'] == native['technologyObject']['path'])}
        for row in evidence['exports']:
            key = (row['family'], row['path'])
            require(key in expected, 'Software export is duplicate or outside its inventory')
            expected.remove(key)
            digest = native_payload_hash(row['xml'])
            require(digest == row['payloadSha256'], 'Software export payload summary differs')
            if row['family'] == 'blocks' and row['path'] in mapped:
                require(digest == native_payload_hash(native['xml']), 'Selected block alias differs from encoder')
            else: rows[(row['family'], row['path'][len(root):])] = digest
        require(not expected, 'Software peer exports are missing')
        return rows
    require(peers(before, first) == peers(after, second), 'Unrelated native software XML changed')


def require_calls(native, calls):
    def raw(value):
        return {key: item for key, item in value.items() if key not in ('xml', 'endpointEvidence', 'softwarePeers', 'hardwarePeers')}
    matches = [i for i, row in enumerate(calls) if row['tool'] == 'technology_read' and
               row.get('status') == 'passed' and same(raw(row['result']), raw(native))]
    require(len(matches) == 1, 'Software peers lack one original selected encoder read')
    last_index = matches[0]
    remaining = iter(enumerate(calls[last_index + 1:], last_index + 1))

    def next_call():
        nonlocal last_index
        last_index, row = next(remaining, (last_index, None))
        while row is not None and row['tool'] == 'tia_session_status':
            require(row.get('status') == 'passed', 'Failed session observation interrupts software evidence')
            last_index, row = next(remaining, (last_index, None))
        require(row is not None and row.get('status') == 'passed', 'Software evidence is missing or interrupted')
        return row

    # The selected export and two bound endpoint observations immediately precede peers.
    exported = next_call()
    require(exported['tool'] == 'artifact_read' and exported['arguments'] == {'path': native['artifact']['path']} and
            decode_native_artifact(native, exported['result']) == native['xml'], 'Selected XML differs from its original artifact read')
    for tool in ('hardware_inspect', 'hardware_inspect'):
        require(next_call()['tool'] == tool, 'Software capture is separated from its selected encoder read')

    def replay(tool, arguments):
        row = next_call()
        require(row['tool'] == tool and same(row['arguments'], arguments), 'Software export request changed, was filtered or reordered')
        return copy.deepcopy(row['result'])
    rebuilt = capture(replay, copy.deepcopy(native), {'projectName': native['project']['name'], 'plcPath': native['plcPath']})
    require(same(rebuilt, native.get('softwarePeers')), 'Software evidence differs from original calls and exported bytes')
    compare(native, native)
    return last_index + 1


def verify_exports(native):
    from audit_engineering_preservation import verified_artifact
    for row in native['softwarePeers']['exports']:
        require(verified_artifact(row['read']['artifact']).decode('utf-8-sig') == row['xml'],
                'Retained software export differs from its original bytes')
