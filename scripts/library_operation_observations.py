"""Cross-field checks for original library operation observations; no file access."""
import ntpath
import re


def _require(condition, field):
    if not condition:
        raise ValueError('Inconsistent operation observation: ' + field)


def _text(value, bound, field):
    _require(isinstance(value, str) and bool(value.strip()) and len(value.encode('utf-16-le')) // 2 <= bound, field)


def _version(value):
    parts = value.split('.')
    _require(2 <= len(parts) <= 4, 'versionNumber')
    values = []
    for part in parts:
        _require(re.fullmatch(r'[+]?[0-9]+', part.strip()) is not None, 'versionNumber')
        number = int(part)
        _require(number <= 2147483647, 'versionNumber')
        values.append(number)
    return tuple(values)


def _pin(value):
    return value['typeGuid'].lower(), value['versionGuid'].lower()


def _link(value, selected):
    status = value['linkStatus']
    if status == 'linked':
        _require(all(isinstance(value[key], str) and value[key] for key in ('typeGuid', 'versionGuid', 'version')), 'linked identity')
        _require('linkReadError' not in value, 'linked error')
    else:
        _require(all(value[key] is None for key in ('typeGuid', 'versionGuid', 'version')), 'unavailable identity')
        _require(value['nativeLinkVerified'] is False, 'unavailable link verification')
        _require(('linkReadError' in value) == (status == 'unavailable'), 'linkReadError')
    if selected:
        _require(status == 'linked' and value['nativeLinkVerified'] is True and _pin(value) == _pin(selected), 'selected HMI link')
    else:
        _require(value['nativeLinkVerified'] is False, 'unselected HMI link verification')


def check_operation(plan, result, has_result, same):
    """Check serialized relationships, without resolving native objects or paths."""
    kind, payload = plan['kind'], plan['payload']
    selected = payload.get('selection', [])
    same(plan['selection'], selected, 'payload.selection')
    resources = plan['resources']
    _require(len({row['libraryId'] for row in resources}) == len(resources), 'duplicate resource')
    by_id = {row['libraryId']: row for row in resources}
    source = payload.get('source')
    if source is not None:
        _require(source['libraryId'] in by_id, 'source resource')
        same(source['revision'], by_id[source['libraryId']]['revision'], 'source.revision')
    if selected:
        _require(source is not None, 'selected source')
    destination = payload.get('destinationLibraryId')
    if destination is not None:
        _require(destination in by_id, 'destination resource')
    if kind in ('library_copy_elements', 'library_publish'):
        _require(source['libraryId'] != destination, 'distinct destination')
    versions = [row for row in selected if 'masterCopyPath' not in row]
    masters = [row for row in selected if 'masterCopyPath' in row]
    _require(len({row['typeGuid'].lower() for row in versions}) == len(versions), 'one selected version per type')
    _require(len({row['versionGuid'].lower() for row in versions}) == len(versions), 'duplicate selected version')
    _require(len({row['masterCopyPath'] for row in masters}) == len(masters), 'duplicate master copy')
    for row in masters: _text(row['masterCopyPath'], 4096, 'masterCopyPath')
    limits = {'library_create': [('name', 128)], 'library_upgrade': [('path', 4096)],
              'library_create_type': [('documentDirectory', 4096), ('documentName', 256)],
              'library_release_version': [('author', 128), ('comment', 4096), ('versionNumber', 32)],
              'library_edit_version': [('testInstanceName', 128)],
              'library_organize': [('folderPath', 4096), ('name', 128)]}
    for field, bound in limits.get(kind, []):
        if field in payload: _text(payload[field], bound, field)
    family = payload.get('target', {}).get('family') if kind in ('library_instantiate', 'library_integrate') else None
    if kind == 'library_upgrade':
        path = payload['path']
        absolute = re.match(r'^[A-Za-z]:[\\/]', path) is not None or (
            path.startswith('\\\\') and not path.startswith(('\\\\?\\', '\\\\.\\'))
            and len([part for part in re.split(r'[\\/]', path[2:]) if part]) >= 3)
        _require(absolute, 'absolute upgrade path')
        _require(re.fullmatch(r'\.(?:zal|al)(?:1[4-9]|20)', ntpath.splitext(payload['path'])[1], re.I) is not None, 'upgrade version')
        files = payload.get('expectedFiles', [])
        paths = set()
        for row in files:
            path = row['relativePath'].replace('\\', '/')
            _text(path, 4096, 'expectedFiles path')
            _require(not path.startswith('/') and not ntpath.isabs(path) and ':' not in path and '..' not in path.split('/'), 'expectedFiles path')
            _require(path.lower() not in paths, 'duplicate companion path')
            paths.add(path.lower())
    if kind == 'library_release_version': _version(payload['versionNumber'])
    if kind == 'library_integrate' and family not in ('classic', 'unified', 'hmi'):
        _require(plan.get('applySupported') is False and 'integrationPlan' in plan, 'plan-only integration')
        target = payload['target']
        if 'scopePath' in target: same(target['scopePath'], target['plcPath'], 'standard PLC scope')
        pins = payload['integration']['dependencyPins']
        all_pins = selected + pins
        _require(len({_pin(row)[0] for row in all_pins}) == len(all_pins), 'duplicate dependency type')
        _require(len({_pin(row)[1] for row in all_pins}) == len(all_pins), 'duplicate dependency version')
        parameters = [row['parameter'] for row in payload['integration']['mappings']]
        _require(len(set(parameters)) == len(parameters), 'duplicate parameter mapping')
        _require(not has_result, 'PLC combined result')
    if family in ('classic', 'unified'):
        target = payload['target']
        for field, bound in (('hmiPath', 4096), ('tagTable', 256), ('screen', 256), ('container', 256)):
            if field in target: _text(target[field], bound, 'target.' + field)
        bindings = payload.get('bindings', []) if family == 'unified' else []
        if family == 'unified' and payload['hmiAction'] == 'tag_create':
            _text(payload['name'], 256, 'name')
            _text(payload['dataType'], 256, 'dataType')
        properties = [row['property'] for row in bindings]
        _require(len(set(properties)) == len(properties), 'duplicate interface binding')
        for row in bindings:
            for field in ('property', 'tag', 'dataType'): _text(row[field], 256, field)
        if has_result:
            action = payload.get('hmiAction', 'instantiate')
            if family == 'unified' and action == 'tag_create':
                same(result['name'], payload['name'], 'tag name')
                same(result['dataType'], payload['dataType'], 'tag dataType')
                if 'defaultConnection' in result:
                    same(result['connection'], result['defaultConnection'], 'internal tag default connection')
                    _require(result['plcTag'] in ('', None) and result['nativeConnectionResolved'] is False, 'internal tag binding')
                else:
                    # Legacy observations have no before/readback evidence for
                    # a localized sentinel; only their empty connection is known.
                    _require(result['connection'] in ('', None), 'internal tag connection')
            else:
                _link(result, versions[0] if versions else None)
                if family == 'unified':
                    same(result['name'], target['container'], 'faceplate name')
                    _require(not result['interfaceTruncated'] or len(result['interface']) == 200, 'interfaceTruncated')
                    if action == 'faceplate_create':
                        for field, value in payload['placement'].items():
                            if field in ('left', 'top', 'width', 'height'): same(result[field], value, 'placement.' + field)
                    else:
                        observed = {row['property']: row['value'] for row in result['interface']}
                        _require(len(observed) == len(result['interface']), 'duplicate observed interface')
                        for row in payload['bindings']:
                            if row['property'] in observed: same(observed[row['property']], row['tag'], 'binding readback')
                            else: _require(result['interfaceTruncated'], 'missing binding readback')
        return
    if not has_result:
        return
    rows = result.get('objects', [])
    observed_versions = {}
    for closure in plan['dependencies']:
        for row in closure.get('versions', []):
            key = _pin(row)
            if key in observed_versions: same(observed_versions[key], row['version'], 'repeated dependency version')
            observed_versions[key] = row['version']
    if kind in ('library_instantiate', 'library_copy_elements', 'library_publish', 'library_update_project', 'library_set_default', 'library_type_policy'):
        for row in rows:
            version = row['version'] if isinstance(row.get('version'), dict) else row
            if 'versionGuid' in version and _pin(version) in observed_versions:
                same(version['version'], observed_versions[_pin(version)], 'observed version number')
    if kind in ('library_create', 'library_upgrade'):
        same(result['library']['sessionGeneration'], plan['sessionGeneration'], 'created library generation')
        if kind == 'library_create': same(result['library']['name'], payload['name'], 'created library name')
    elif kind == 'library_instantiate':
        linked = [row for row in rows if 'version' in row]
        _require(len(rows) == len(selected) and len(linked) == len(versions), 'instantiation count')
        _require({_pin(row['version']) for row in linked} == {_pin(row) for row in versions}, 'instantiation version pins')
    elif kind in ('library_copy_elements', 'library_publish', 'library_update_project'):
        if kind == 'library_update_project' and 'updateImpact' in plan:
            impact = plan['updateImpact']
            _require(isinstance(impact.get('noOp'), bool), 'update noOp observation')
            same(result.get('noOp', False), impact['noOp'], 'update noOp result')
        if result.get('noOp') is not True:
            returned = [row for row in rows if 'versionGuid' in row]
            _require(len(rows) == len(selected) and len(returned) == len(versions), 'returned selection count')
            _require({_pin(row) for row in returned} == {_pin(row) for row in versions}, 'returned version pins')
        if kind in ('library_copy_elements', 'library_publish'):
            sync = result['typeSynchronization']
            _require(len({row['typeGuid'].lower() for row in sync}) == len(sync), 'duplicate synchronization type')
            for item in sync:
                versions_seen = item['versions']
                _require(all(row['typeGuid'].lower() == item['typeGuid'].lower() and row['state'] == 'Committed' for row in versions_seen), 'synchronized version scope')
                _require(len({_pin(row) for row in versions_seen}) == len(versions_seen), 'duplicate synchronized version')
                defaults = [row for row in versions_seen if row['isDefault']]
                _require(len(defaults) == 1, 'synchronized default count')
                same(defaults[0], item['defaultVersion'], 'synchronized default')
    elif kind in ('library_edit_version', 'library_release_version', 'library_set_default', 'library_type_policy'):
        row = rows[0]
        _require(row['typeGuid'].lower() == versions[0]['typeGuid'].lower(), 'returned type')
        # Siemens may rotate the version GUID when an InWork version is released.
        if kind not in ('library_edit_version', 'library_release_version'):
            _require(_pin(row) == _pin(versions[0]), 'returned version')
        if kind == 'library_release_version':
            _require(row['state'] == 'Committed' and _version(row['version']) == _version(payload['versionNumber']), 'released version')
        if kind == 'library_set_default': _require(row['isDefault'] is True, 'default version')
        if kind == 'library_type_policy':
            for field in ('doNotUse', 'setForUpdate'):
                if field in payload: same(row[field], payload[field], field)
    elif kind == 'library_discard_version':
        _require(result['discardedVersionGuid'].lower() == versions[0]['versionGuid'].lower(), 'discarded version')
        if result['remainingType'] is not None: _require(_pin(result['remainingType']) == _pin(versions[0]), 'remaining version')
