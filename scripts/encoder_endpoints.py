"""Bind encoder endpoints within each project before comparing recovered copies."""
from urllib.parse import quote

from engineering_preservation import require
from compilation_evidence import fixture_plc_selection
from qualify_technology_families import complete_connection_observations, parameter_rows, same

POLICY = 'encoder-project-endpoints-v1'


def connection_attributes(native, configuration):
    connections = native["hardwareConnections"]
    complete_connection_observations(connections, native.get("hardwareConnectionCoverage"), native.get("technologyObject", {}).get("path"),
                                     native.get("project", {}).get("path"), native.get("plcPath"))
    require(len(connections) == 1 and connections[0]["role"] == "encoder", 'Native evidence requirement failed: len(connections) == 1 and connections[0]["role"] == "encoder"')
    require(connections[0]["isConnected"] is True, "Encoder channel is disconnected")
    values = {entry["name"]: entry.get("value") for entry in connections[0]["attributes"]}
    require(all(type(values.get(key)) is int and 0 <= values[key] <= 2147483647 for key in ('InputAddress', 'OutputAddress')),
            'Encoder addresses must be nonnegative Int32 values')
    channel = values["Channel"]
    require(isinstance(channel, dict) and channel.get("kind") == "channel" and channel.get("valueSemantics") == "engineeringReference", "Verified native channel reference required")
    require(channel["number"] == configuration["channelNumber"] and channel["type"] == "Technology", 'Native evidence requirement failed: channel["number"] == configuration["channelNumber"] and channel["type"] == "Technology"')
    require(channel["ioType"] == configuration["channelIoType"] and values["PathToDBMember"] is None, 'Native evidence requirement failed: channel["ioType"] == configuration["channelIoType"] and values["PathToDBMember"] is None')
    require(channel["parent"]["name"] == configuration["channelItemName"], 'Native evidence requirement failed: channel["parent"]["name"] == configuration["channelItemName"]')
    module = channel["parent"]["container"]
    require(module["name"] == "TiaMcp_EncoderHardware" and module["positionNumber"] == 2, 'Native evidence requirement failed: module["name"] == "TiaMcp_EncoderHardware" and module["positionNumber"] == 2')
    require(module["typeIdentifier"] == configuration["moduleTypeIdentifier"], 'Native evidence requirement failed: module["typeIdentifier"] == configuration["moduleTypeIdentifier"]')
    return values


def endpoint(native, profile):
    values = connection_attributes(native, profile)
    evidence = native.get('endpointEvidence', {})
    require(evidence.get('policy') == POLICY, 'Independent encoder endpoint evidence is required')
    inventory = evidence.get('inventory')
    selector = fixture_plc_selection(inventory, native['project']['path'], native.get('processId'))
    require(native.get('plcPath') == selector['plcPath'], 'Endpoint PLC differs from independent inventory')
    root = '/projects/' + quote(selector['projectName'], safe='') + '/'
    coverage = native['hardwareConnectionCoverage']['referenceCoverage']
    require(coverage.get('hardwareMembershipStatus') == 'complete' and
            type(coverage.get('hardwareItemsVisited')) is int and
            type(coverage.get('hardwareItemsLimit')) is int and
            0 < coverage['hardwareItemsVisited'] == len(inventory['hardware']) <= coverage['hardwareItemsLimit'] <= 50000,
            'Complete bounded native hardware membership is required')
    channel = values['Channel']
    parent, module = channel['parent'], channel['parent']['container']
    hardware = {row['path']: row for row in inventory['hardware']}
    for item in (parent, module):
        path = item.get('hardwarePath')
        require(item.get('hardwarePathBasis') == 'boundedProjectHardwareMembership' and
                isinstance(path, str) and path.startswith(root) and path.endswith('/items/' + quote(item['name'], safe='')) and
                path in hardware, 'Endpoint is not an exact member of its own project hardware inventory')
        require(all(key in hardware[path] and same(hardware[path][key], item.get(key))
                    for key in ('name', 'positionNumber', 'typeIdentifier')), 'Endpoint hardware metadata differs from inventory')
    require(parent['hardwarePath'] == module['hardwarePath'] + '/items/' + quote(parent['name'], safe=''),
            'Encoder channel owner is not an immediate child of its module')
    require(parent['nativeIdentifier'] != module['nativeIdentifier'], 'Channel owner and module cannot alias')
    inspections = evidence.get('hardware')
    require(isinstance(inspections, list) and len(inspections) == 2, 'Exact module and channel-owner inspections are required')
    selected_channel = None
    for reference, read in zip((module, parent), inspections):
        require(isinstance(read, dict) and read.get('project', {}).get('path') == native['project']['path'] and
                type(read.get('processId')) is int and read['processId'] == native['processId'] and read.get('warnings') == [] and
                read.get('completeness', {}).get('scopeComplete') is True and read['completeness'].get('truncated') is False,
                'Hardware inspection is incomplete or belongs to another owner')
        rows = read.get('items')
        require(isinstance(rows, list) and len(rows) == 1 and rows[0].get('path') == reference['hardwarePath'] and
                all(same(rows[0].get(key), reference.get(key)) for key in ('name', 'positionNumber')),
                'Hardware inspection selected another or ambiguous item')
        channels = rows[0].get('channels')
        require(isinstance(channels, list) and len(channels) <= 50000 and all(isinstance(row, dict) for row in channels),
                'Hardware channel collection is missing or unbounded')
        keys = [(row.get('number'), row.get('type'), row.get('ioType')) for row in channels]
        require(all(type(n) is int and n >= 0 and isinstance(t, str) and t and isinstance(io, str) and io for n, t, io in keys)
                and len(keys) == len(set(keys)), 'Hardware channel selectors are malformed or ambiguous')
        if reference is parent:
            matches = [row for row in channels if all(same(row.get(key), channel[key]) for key in ('number', 'type', 'ioType'))]
            require(len(matches) == 1, 'Connected channel is absent from its exact hardware owner')
            selected_channel = parameter_rows(matches[0].get('attributes'))
    require(selected_channel.get('OperatingMode', {}).get('value') == 269 and
            type(selected_channel['OperatingMode']['value']) is int, 'Encoder channel is not in the observed MotionControl mode')
    # Project paths and provider IDs are checked above; copies may assign new ones.
    logical_channel = {key: channel[key] for key in ('number', 'type', 'ioType')}
    logical_channel['hardware'] = [{**{key: item[key] for key in ('name', 'positionNumber', 'typeIdentifier')},
                                    'path': item['hardwarePath'][len(root):]} for item in (module, parent)]
    return {'plcPath': selector['plcPath'][len(root):], 'connectOption': native['hardwareConnections'][0]['connectOption'],
            'attributes': {**values, 'Channel': logical_channel}, 'channelAttributes': selected_channel}


def capture_endpoint(call, native, inventory, profile):
    values = connection_attributes(native, profile)
    parent = values['Channel']['parent']
    native['endpointEvidence'] = {'policy': POLICY, 'inventory': inventory, 'hardware': [
        call('hardware_inspect', {'projectName': inventory['project']['name'], 'hardwarePath': row['hardwarePath'], 'maxItems': 2000})
        for row in (parent['container'], parent)]}
    endpoint(native, profile)


def require_endpoint_calls(native, profile, calls):
    """Bind the summary to one original read and its adjacent read-only evidence."""
    logical = endpoint(native, profile)
    def raw(value):
        return {key: val for key, val in value.items() if key not in ('xml', 'endpointEvidence', 'softwarePeers', 'hardwarePeers')}
    matches = [i for i, row in enumerate(calls) if row['tool'] == 'technology_read' and
               row.get('status') == 'passed' and same(raw(row['result']), raw(native))]
    require(len(matches) == 1, 'Endpoint summary does not identify one original technology read')
    index = matches[0]
    preceding = index - 1
    while preceding >= 0 and calls[preceding]['tool'] == 'tia_session_status': preceding -= 1
    require(preceding >= 0 and calls[preceding]['tool'] == 'project_inventory' and
            calls[preceding].get('status') == 'passed' and calls[preceding]['arguments'] == {'maxItems': 2000} and
            same(calls[preceding]['result'], native['endpointEvidence']['inventory']), 'Endpoint inventory differs from its original preceding call')
    selector = fixture_plc_selection(native['endpointEvidence']['inventory'], native['project']['path'], native['processId'])
    require(all(calls[index]['arguments'].get(key) == value for key, value in selector.items()), 'Endpoint read selected another PLC')
    inspections = []
    for row in calls[index + 1:]:
        if row['tool'] in ('artifact_read', 'tia_session_status'): continue
        if row['tool'] != 'hardware_inspect': break
        inspections.append(row)
        if len(inspections) == 2: break
    require(len(inspections) == 2, 'Original endpoint hardware inspection calls are missing or interrupted')
    for call, read in zip(inspections, native['endpointEvidence']['hardware']):
        require(call.get('status') == 'passed' and same(call['result'], read) and
                call['arguments'] == {'projectName': selector['projectName'], 'hardwarePath': read['items'][0]['path'], 'maxItems': 2000},
                'Endpoint hardware summary differs from its original exact inspection')
    return logical
