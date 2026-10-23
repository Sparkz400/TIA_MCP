"""Preserve all exposed configured hardware in the owned encoder fixture.

Complete readable observations are required; physical behavior and unexposed
settings remain outside this proof. Each phase is a bounded, non-atomic capture.
"""
import copy
from urllib.parse import quote

from engineering_preservation import require
from compilation_evidence import fixture_plc_selection
from qualify_technology_families import parameter_rows, same

POLICY = 'encoder-configured-hardware-peers-v1'


def rows(value, label):
    require(isinstance(value, list) and len(value) <= 50000 and all(isinstance(row, dict) for row in value),
            'Missing or unbounded hardware collection: ' + label)
    return value


def text(value):
    return isinstance(value, str) and bool(value.strip())


def integer(value, minimum=0):
    return type(value) is int and minimum <= value <= 2147483647


def attributes(value):
    parameter_rows(rows(value, 'attributes'))


def addresses(value, captured):
    result = []
    for row in rows(value, 'addresses'):
        require(text(row.get('ioType')) and integer(row.get('startAddress'), -1) and integer(row.get('length')) and
                row.get('lengthUnit') == 'bits' and integer(row.get('byteSpan')) and row['byteSpan'] == (row['length'] + 7) // 8,
                'Hardware address range or bit/byte unit is incomplete')
        if captured: attributes(row.get('attributes'))
        result.append({key: row[key] for key in ('ioType', 'startAddress', 'length', 'lengthUnit', 'byteSpan')})
    keys = [(row['ioType'], row['startAddress'], row['length']) for row in result]
    require(len(keys) == len(set(keys)), 'Hardware address ranges are ambiguous')
    return result


def inventory_view(inventory, native):
    selector = fixture_plc_selection(inventory, native['project']['path'], native['processId'])
    require(selector == {'projectName': native['project']['name'], 'plcPath': native['plcPath']},
            'Hardware inventory selected another project or PLC')
    root = '/projects/' + quote(native['project']['name'], safe='')
    result = {}
    paths = {row['path'] for row in inventory['hardware']}
    for row in inventory['hardware']:
        path = row['path']
        require(text(row.get('name')) and path.startswith(root + '/devices/') and
                path.endswith('/items/' + quote(row['name'], safe='')) and
                (path.count('/items/') == 1 or path.rsplit('/items/', 1)[0] in paths),
                'Hardware item has no canonical name or inventoried parent')
        require(integer(row.get('positionNumber'), -1) and text(row.get('classification')) and 'typeIdentifier' in row and
                (row['typeIdentifier'] is None or isinstance(row['typeIdentifier'], str)), 'Hardware inventory metadata is incomplete')
        addresses(row.get('addresses'), captured=False)
        result[path[len(root):]] = {**copy.deepcopy(row), 'path': path[len(root):]}
    return result


def network_view(value, owner, hardware, root):
    require(isinstance(value, dict) and value.get('scopeComplete') is True and
            value.get('origin') == 'nativeConfiguredOffline' and value.get('physicalTopologyVerified') is False and
            text(value.get('interfaceType')) and text(value.get('operatingMode')) and text(value.get('associationBasis')),
            'Configured network observation is incomplete')
    result = copy.deepcopy(value)

    def reference(item):
        require(isinstance(item, dict) and item.get('resolution') == 'resolved' and item.get('ownerHardwarePath') in hardware and
                'unavailable' not in item, 'Network association owner is unresolved or foreign')
        return {**item, 'ownerHardwarePath': item['ownerHardwarePath'][len(root):]}

    def subnet(item):
        require(isinstance(item, dict), 'Network subnet observation is missing')
        if item.get('state') == 'notConnected':
            require(item == {'state': 'notConnected'}, 'Disconnected subnet contains contradictory evidence')
            return item
        require(item.get('state') == 'connected' and item.get('resolution') == 'resolved' and text(item.get('name')) and
                item.get('path') == root + '/subnets/' + quote(item['name'], safe='') and item.get('reason') is None and
                'unavailable' not in item, 'Network subnet identity is unresolved or foreign')
        return {**item, 'path': item['path'][len(root):]}

    def system(item):
        require(isinstance(item, dict), 'I/O-system observation is missing')
        if item.get('state') == 'notAssigned':
            require(item == {'state': 'notAssigned'}, 'Unassigned I/O system contains contradictory evidence')
            return item
        require(item.get('state') == 'assigned' and text(item.get('name')) and integer(item.get('number')) and
                'unavailable' not in item, 'I/O-system identity is incomplete')
        return {**item, 'controllerOwner': reference(item.get('controllerOwner')), 'subnet': subnet(item.get('subnet'))}

    nodes = rows(result.get('nodes'), 'network nodes')
    names = []
    for row in nodes:
        require(text(row.get('name')) and text(row.get('type')) and 'unavailable' not in row, 'Network node identity is incomplete')
        names.append(row['name'])
        attributes(row.get('attributes')); row['subnet'] = subnet(row.get('subnet'))
    require(len(names) == len(set(names)), 'Network node identities are ambiguous')
    for collection, index_name, association in (('ioControllers', 'controllerIndex', 'ioSystem'),
                                               ('ioConnectors', 'connectorIndex', 'connectedToIoSystem')):
        entries = rows(result.get(collection), collection)
        for index, row in enumerate(entries):
            require(type(row.get(index_name)) is int and row[index_name] == index and row.get('ownerHardwarePath') == owner and
                    'unavailable' not in row, 'Network service index or hardware owner differs')
            attributes(row.get('attributes'))
            row['ownerHardwarePath'] = owner[len(root):]
            row[association] = system(row.get(association))
            if collection == 'ioControllers':
                current = row[association]
                require(current['state'] != 'assigned' or current['controllerOwner']['ownerHardwarePath'] == owner[len(root):],
                        'I/O system belongs to another controller owner')
                row['assignedConnectors'] = [reference(item) for item in rows(row.get('assignedConnectors'), 'assigned connectors')]
                require(current['state'] == 'assigned' or not row['assignedConnectors'], 'Unassigned controller has assigned connectors')
    return result


def read_view(read, native, hardware, root):
    require(read.get('project', {}).get('path') == native['project']['path'] and
            read.get('project', {}).get('name') == native['project']['name'] and type(read.get('processId')) is int and
            read['processId'] == native['processId'] and read.get('warnings') == [] and
            read.get('completeness', {}).get('scopeComplete') is True and read['completeness'].get('truncated') is False,
            'Hardware read is incomplete or belongs to another owner')
    items = rows(read.get('items'), 'selected hardware')
    require(len(items) == 1 and items[0].get('path') in hardware, 'Hardware read selected another or ambiguous item')
    row = items[0]; path = row['path']; relative = path[len(root):]
    expected = hardware[path]
    require(all(key in row and same(row[key], expected[key]) for key in ('name', 'positionNumber', 'classification', 'typeIdentifier')) and
            type(row.get('isBuiltIn')) is bool and type(row.get('isPlugged')) is bool, 'Hardware read metadata differs from inventory')
    require(same(addresses(row.get('addresses'), captured=True), addresses(expected['addresses'], captured=False)),
            'Hardware address ranges differ from their inventory')
    attributes(row.get('attributes'))
    channels = rows(row.get('channels'), 'channels'); keys = []
    for channel in channels:
        require(integer(channel.get('number')) and text(channel.get('type')) and text(channel.get('ioType')),
                'Hardware channel identity is incomplete')
        keys.append((channel['number'], channel['type'], channel['ioType']))
        attributes(channel.get('attributes'))
    require(len(keys) == len(set(keys)), 'Hardware channel identities are ambiguous')
    result = {**copy.deepcopy(row), 'path': relative}
    if row.get('networkObservation') == 'observed':
        result['network'] = network_view(row.get('network'), path, hardware, root)
    else:
        require(row.get('networkObservation') == 'notExposed' and 'network' not in row,
                'Network service absence was not observed explicitly')
    return relative, result


def view(native, evidence):
    require(isinstance(evidence, dict) and evidence.get('policy') == POLICY and evidence.get('atomicSnapshot') is False and
            evidence.get('fullProjectPreservationVerified') is False, 'Current bounded hardware evidence is required')
    before = inventory_view(evidence.get('inventoryBefore'), native)
    require(same(before, inventory_view(evidence.get('inventoryAfter'), native)) and
            same(before, inventory_view(native['endpointEvidence']['inventory'], native)),
            'Hardware inventory changed during the selected capture')
    root = '/projects/' + quote(native['project']['name'], safe='')
    hardware = {row['path']: row for row in evidence['inventoryBefore']['hardware']}
    captured = rows(evidence.get('reads'), 'hardware reads')
    require(len(captured) == len(hardware), 'Every inventoried hardware item requires one exact read')
    result = {}
    for read in captured:
        relative, value = read_view(read, native, hardware, root)
        require(relative not in result, 'Duplicate hardware read cannot replace a missing peer')
        result[relative] = value
    # The earlier endpoint reads must agree with the corresponding full reads.
    for read in native['endpointEvidence']['hardware']:
        row = read['items'][0]
        require(read.get('project', {}).get('path') == native['project']['path'] and type(read.get('processId')) is int and
                read['processId'] == native['processId'] and len(read['items']) == 1 and
                any(same(row, candidate['items'][0]) for candidate in captured),
                'Encoder endpoint differs from the complete hardware read')
    return {'inventory': before, 'hardware': result}


def capture(call, native):
    before = call('project_inventory', {'maxItems': 2000})
    require(same(inventory_view(before, native), inventory_view(native['endpointEvidence']['inventory'], native)),
            'Hardware inventory changed before complete capture')
    root = '/projects/' + quote(native['project']['name'], safe='')
    hardware = {row['path']: row for row in before['hardware']}
    reads = []
    for row in sorted(before['hardware'], key=lambda row: row['path']):
        read = call('hardware_inspect', {'projectName': native['project']['name'], 'hardwarePath': row['path'], 'maxItems': 2000})
        relative, _ = read_view(read, native, hardware, root)
        require(relative == row['path'][len(root):], 'Hardware response differs from its exact requested selector')
        reads.append(read)
    after = call('project_inventory', {'maxItems': 2000})
    evidence = {'policy': POLICY, 'inventoryBefore': before, 'reads': reads, 'inventoryAfter': after,
                'atomicSnapshot': False, 'fullProjectPreservationVerified': False}
    view(native, evidence)
    native['hardwarePeers'] = evidence
    return evidence


def compare(before, after):
    require(same(view(before, before.get('hardwarePeers')), view(after, after.get('hardwarePeers'))),
            'Configured hardware peers changed')


def require_calls(native, calls, start):
    require(type(start) is int and 0 <= start < len(calls), 'Hardware capture lacks its preceding software proof')
    remaining = iter(calls[start:])
    def replay(tool, arguments):
        row = next(remaining, None)
        while row is not None and row['tool'] == 'tia_session_status':
            require(row.get('status') == 'passed', 'Failed session observation interrupts hardware evidence')
            row = next(remaining, None)
        require(row is not None and row.get('status') == 'passed' and row['tool'] == tool and same(row['arguments'], arguments),
                'Hardware request changed, was filtered, omitted or interrupted')
        return copy.deepcopy(row['result'])
    rebuilt = capture(replay, copy.deepcopy(native))
    require(same(rebuilt, native.get('hardwarePeers')), 'Hardware evidence differs from original ordered calls')
