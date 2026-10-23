"""Check serialized component plans without reading their referenced artifacts."""
import ntpath
import re
from urllib.parse import quote

from library_operation_observations import _pin, _version


def require(condition, field):
    if not condition:
        raise ValueError('Inconsistent component preview: ' + field)


def descriptor(value):
    require(ntpath.isabs(value['path']), 'absolute artifact path')
    require(len(value['path'].encode('utf-16-le')) // 2 <= 4096, 'artifact path bound')


def wire(source, source_port, destination, destination_port):
    return {'from': {'node': source, 'port': source_port}, 'to': {'node': destination, 'port': destination_port}}


def name_key(value):
    # Python full Unicode casing can merge names that .NET OrdinalIgnoreCase
    # keeps distinct (for example sharp-s and SS). Check ASCII casing and exact
    # non-ASCII text here; complete native name resolution remains unverified.
    return value.translate(str.maketrans('abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'))


def check_component_preview(preview, same):
    require(preview['kind'] == 'library_integrate', 'operation kind')
    payload = preview['payload']
    require(payload.get('action') == 'plc_component_plan' and isinstance(payload.get('integration'), dict), 'explicit PLC component recipe')
    recipe = payload['integration']; plan = preview['integrationPlan']
    same(preview['applyError'], plan['applyError'], 'component apply refusal')
    plc = payload['target']['plcPath']
    same(preview.get('targetScope'), plc, 'component target scope')
    same(preview.get('compileTarget'), plc, 'component compile target')
    require(isinstance(preview.get('projectPath'), str) and bool(preview['projectPath'].strip()), 'project path')
    pins = payload['selection'] + recipe['dependencyPins']
    versions = plan['versions']; by_version = {row['versionGuid'].lower(): row for row in versions}
    require(len(by_version) == len(versions), 'duplicate version')
    require(len({r['typeGuid'].lower() for r in versions}) == len(versions), 'duplicate native type')
    require({_pin(row) for row in versions} == {_pin(row) for row in pins} and len(versions) == len(pins), 'exact pinned closure')
    root = by_version[payload['selection'][0]['versionGuid'].lower()]
    require(root['engineeringType'] == 'Siemens.Engineering.SW.Blocks.CodeBlockLibraryTypeVersion', 'root block version')
    require(re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]{0,127}', root['typeName']) is not None, 'root native name')
    for row in versions:
        require(all(g.replace('-', '') != '0' * 32 for g in _pin(row)), 'nonzero version pins')
        _version(row['version'])
        descriptor(row['artifact']); same(row['artifact']['sha256'], row['contentSha256'], 'version artifact pin')
        if 'contentPolicy' in row:
            require(row['engineeringType'] == 'Siemens.Engineering.SW.Blocks.CodeBlockLibraryTypeVersion', 'FB content policy')
            policy = row['contentPolicy']; descriptor(policy['sourceOracleArtifact'])
            same(policy['sourceOracleArtifact']['sha256'], policy['sourceOracleSha256'], 'oracle artifact pin')
    pending = [root['versionGuid'].lower()]; seen = set()
    while pending:
        key = pending.pop()
        if key in seen:
            continue
        require(key in by_version, 'unresolved dependency edge'); seen.add(key)
        pending.extend(g.lower() for g in by_version[key]['requiredVersionGuids'])
    require(seen == set(by_version), 'unreachable dependency version')
    closure = preview['dependencies']
    require(len(closure) == 1 and 'versions' in closure[0] and 'masterCopy' not in closure[0], 'one complete dependency capture')
    same(closure[0]['rootVersionGuid'].lower(), root['versionGuid'].lower(), 'captured dependency root')
    captured = {r['versionGuid'].lower(): r for r in closure[0]['versions']}
    require(len(captured) == len(closure[0]['versions']) and set(captured) == seen, 'matching captured dependency set')
    for key, row in by_version.items():
        metadata = {k: v for k, v in row.items() if k not in ('contentSha256', 'artifact', 'contentPolicy')}
        same(metadata, captured[key], 'captured version metadata')

    interface = plan['interface']; mappings = plan['mappings']
    names = [row['name'].lower() for row in interface]
    require(len(set(names)) == len(names), 'unique interface parameters')
    requested = {row['parameter']: row for row in recipe['mappings']}
    require(len(requested) == len(recipe['mappings']) == len(interface) == len(mappings), 'complete parameter mapping')
    operands = []; connections = []; writers = set(); tags = {}; tag_names = {}
    for index, (parameter, mapping) in enumerate(zip(interface, mappings)):
        name = parameter['name']; require(name in requested, 'requested interface member')
        same({k: v for k, v in mapping.items() if k != 'nativeTag'}, requested[name], 'requested mapping')
        same({'name': mapping['parameter'], 'direction': mapping['direction'], 'dataType': mapping['dataType']}, parameter, 'ordered interface mapping')
        tag = mapping['nativeTag']; same(tag['path'], mapping['tagPath'], 'native tag path')
        same(tag['dataType'], parameter['dataType'], 'native tag data type')
        require(tag['tablePath'].startswith(plc + '/tags/'), 'selected PLC tag table')
        same(tag['path'], tag['tablePath'] + '/tags/' + quote(tag['name'], safe=''), 'native tag ownership')
        if tag['path'] in tags:
            same(tags[tag['path']], tag, 'repeated native tag observation')
        tags[tag['path']] = tag
        key_name = name_key(tag['name'])
        if key_name in tag_names:
            same(tag_names[key_name], tag['path'], 'captured tag name ambiguity')
        tag_names[key_name] = tag['path']
        direction = mapping['direction']
        if direction != 'Input':
            require(not re.match(r'^%?[IE]', tag['logicalAddress'] or '', re.I), 'write to physical input')
            require(name_key(tag['path']) not in writers, 'writable tag alias'); writers.add(name_key(tag['path']))
        key = 'p' + str(index)
        operands.append(dict(key=key, scope='tag', ownerPath=tag['tablePath'], components=[tag['name']], dataType=tag['dataType']))
        connections.append(wire('call', name, '$' + key, 'value') if direction == 'Output' else wire('$' + key, 'value', 'call', name))

    caller = plan['caller']; requested_caller = recipe['caller']
    standalone = requested_caller.get('mode') == 'createStandaloneFc'
    caller_path = plc + '/blocks/' + quote(requested_caller['name'], safe='') if standalone else requested_caller['blockPath']
    require(caller_path.startswith(plc + '/blocks/'), 'selected PLC caller')
    same(caller['blockPath'], caller_path, 'caller path')
    same(caller['mode'], 'createStandaloneFc' if standalone else 'existing', 'caller mode')
    same(caller['initiallyAbsent'], standalone, 'caller absence')
    require(not standalone or not plan['blockers'], 'standalone insertion blockers')
    if standalone:
        same(caller['language'], requested_caller['language'], 'caller language')
        require('callerArtifact' not in plan, 'absent caller artifact')
    elif 'callerArtifact' in plan:
        descriptor(plan['callerArtifact'])
    group = caller_path.rsplit('/', 1)[0]; db = plan['instanceDb']
    same(db['name'], recipe['instanceDb']['name'], 'instance name')
    same(db['path'], group + '/' + quote(db['name'], safe=''), 'instance path')
    native_names = [name_key(r['typeName']) for r in versions] + [name_key(db['name'])]
    if standalone:
        native_names.append(name_key(requested_caller['name']))
    require(len(native_names) == len(set(native_names)), 'planned object name collision')
    language = caller['language']; nodes = []
    if language == 'LAD':
        nodes.append(dict(key='rail', kind='powerrail')); connections.insert(0, wire('rail', 'out', 'call', 'en'))
    else:
        operands.append(dict(key='enable', scope='constant', dataType='Bool', value='true'))
        connections.append(wire('$enable', 'value', 'call', 'en'))
    nodes.append(dict(key='call', kind='call', calleePath=plc + '/blocks/' + quote(root['typeName'], safe=''), instance='instance'))
    model = dict(schemaVersion=1, language=language, nodes=nodes, operands=operands, connections=connections)
    graphical = dict(scopePath=plc, groupPath=group, namespace='', model=model,
                     instances=[dict(key='instance', kind='createGlobal', name=db['name'])])
    if standalone:
        model.update(operation='createBlock', creation=dict(name=requested_caller['name'], blockType='FC', interface=[]))
    else:
        graphical['blockPath'] = caller_path
        model.update(operation='insertNetwork', insertAfterNetworkId=requested_caller['insertAfterNetworkId'])
    first, second = plan['steps']
    same(first, dict(order=1, kind='library_instantiate', requiresFreshPreparation=True,
        prepareArguments=dict(kind='library_instantiate', payload=dict(schemaVersion=1, source=payload['source'], selection=payload['selection'], target=payload['target']))), 'import step')
    same(second['order'], 2, 'typed step order'); same(second['kind'], 'graphical_typed_edit', 'typed step kind')
    require('mustWaitFor' in second, 'typed preparation boundary')
    same(second['prepareArguments'], dict(kind='graphical_typed_edit', projectName=payload['target']['projectName'], plcPath=plc, payload=graphical), 'typed call graph')
    effects = [row for row in preview['effects'] if row.get('action') == 'planPlcComponent']
    require(len(effects) == 1, 'component planning effect')
    same(effects[0], dict(action='planPlcComponent', nativeMutation=False, proposedSteps=plan['steps']), 'component effect steps')
    return dict(componentPreviewValidated=True, componentStepGraphBound=True,
        componentContentPinsBound=True, componentArtifactBytesVerified=False,
        componentCallerArtifactRetained='callerArtifact' in plan,
        componentInventoryVerified=False, componentInterfaceExportVerified=False,
        componentNativeSelectorsVerified=False, componentPreservationVerified=False,
        inventoryFingerprintRecomputed=False)
