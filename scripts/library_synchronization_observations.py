"""Check recorded synchronization plans/results without native or file access."""
from library_operation_observations import _version


def require(condition, field):
    if not condition:
        raise ValueError('Inconsistent synchronization observation: ' + field)


def indexed(rows, key, field):
    result = {}
    for row in rows:
        identity = key(row)
        require(identity not in result, 'duplicate ' + field)
        result[identity] = row
    return result


def clean(row):
    return {key: value for key, value in row.items() if key != 'requiredVersionGuids'}


def closure_rows(closure):
    require('versions' in closure, 'unavailable source dependencies')
    rows = indexed(closure['versions'], lambda r: r['versionGuid'].lower(), 'dependency version')
    pending, seen = [closure['rootVersionGuid'].lower()], set()
    while pending:
        current = pending.pop()
        if current in seen:
            continue
        require(current in rows, 'missing dependency version')
        seen.add(current)
        pending.extend(g.lower() for g in rows[current]['requiredVersionGuids'])
    require(seen == set(rows), 'extraneous dependency version')
    require(all(r['state'] == 'Committed' and not r['doNotUse'] for r in rows.values()), 'committed usable source')
    return rows


def check_synchronization(plan, result, same):
    require(plan['kind'] in ('library_copy_elements', 'library_publish'), 'operation kind')
    selected = [r for r in plan['selection'] if 'masterCopyPath' not in r]
    masters = [r for r in plan['selection'] if 'masterCopyPath' in r]
    source, roots = {}, set()
    for closure in plan['dependencies']:
        rows = closure_rows(closure)
        if 'masterCopy' in closure:
            # Embedded master-copy dependencies are observations from a separate
            # scan; they do not populate CopyTypes or its synchronization plan.
            require(bool(masters), 'master dependencies without selected master')
            continue
        root = closure['rootVersionGuid'].lower()
        require(root not in roots, 'duplicate release root')
        roots.add(root)
        for guid, row in rows.items():
            if guid in source:
                same(source[guid], row, 'repeated source release')
            source[guid] = row
    require(roots == set(source) and len(roots) <= 1000, 'every source release is a dependency root')
    by_type = {}
    for guid, row in source.items():
        by_type.setdefault(row['typeGuid'].lower(), {})[guid] = row
    sync = indexed(plan['typeSynchronization'], lambda r: r['typeGuid'].lower(), 'synchronization type')
    require(set(sync) == set(by_type), 'source type coverage')
    selected_by_type = indexed(selected, lambda r: r['typeGuid'].lower(), 'selected type')
    require(set(selected_by_type) <= set(sync), 'selected type missing')
    # Traverse type dependencies through every release, not only the selected
    # default: older releases can require additional copied types.
    seen, pending = set(), list(selected_by_type)
    while pending:
        current = pending.pop()
        if current in seen:
            continue
        seen.add(current)
        for release in by_type[current].values():
            pending.extend(source[g.lower()]['typeGuid'].lower() for g in release['requiredVersionGuids'])
    require(seen == set(sync), 'unselected or missing transitive type')
    union_count = minimum_capture_count = 0
    expected_by_type = {}
    for type_id, item in sync.items():
        releases = by_type[type_id]
        defaults = [r for r in releases.values() if r['isDefault']]
        require(len(defaults) == 1, 'one source default')
        default = clean(defaults[0]); source_id = default['versionGuid'].lower()
        same(item['sourceDefault'], default, 'source default identity')
        same(item['typeName'], default['typeName'], 'source type name')
        require(all(r['typeName'] == item['typeName'] for r in releases.values()), 'source type names')
        require(all(_version(r['version']) <= _version(default['version']) for r in releases.values()), 'source default is highest release')
        if type_id in selected_by_type:
            same(selected_by_type[type_id]['versionGuid'].lower(), source_id, 'selected source default')
        expected = indexed(item['expectedVersions'], lambda r: r['versionGuid'].lower(), 'expected release')
        indexed(expected.values(), lambda r: _version(r['versionNumber']), 'expected version number')
        expected_by_type[type_id] = expected
        require(set(releases) <= set(expected), 'source release missing from expected union')
        for guid, row in releases.items():
            same(expected[guid]['versionNumber'], row['version'], 'source release number')
        before = item['beforeDefault']
        if before is None:
            require(set(expected) == set(releases), 'new type contains unobserved destination releases')
            chosen, effect, changed = default, 'initializeDefault', True
        else:
            before_id = before['versionGuid'].lower()
            require(before['typeGuid'].lower() == type_id and before_id in expected, 'prior default identity')
            same(expected[before_id]['versionNumber'], before['version'], 'prior default version number')
            chosen = default if _version(default['version']) > _version(before['version']) else before
            changed = before_id != chosen['versionGuid'].lower()
            effect = 'promoteNewerSourceDefault' if changed else 'preserveDefault'
        same(item['expectedDefault'], chosen, 'policy-selected default')
        same(item['defaultEffect'], effect, 'default effect')
        same(item['defaultChange'], changed, 'default change')
        union_count += len(expected)
        # The before inventory is not retained. Count only its known minimum:
        # union-only releases plus the original default if also in the source.
        minimum_capture_count += len(expected) + int(before is not None and before['versionGuid'].lower() in releases)
    require(union_count <= 2000 and minimum_capture_count <= 2000, 'capture budget')
    indexed((pin for pins in expected_by_type.values() for pin in pins.values()),
            lambda r: r['versionGuid'].lower(), 'cross-type expected version identity')
    copies = [e for e in plan['effects'] if e['action'] == 'copyTypeAllVersions']
    effects = indexed(copies, lambda e: e['versions'][0]['typeGuid'].lower(), 'copy effect')
    require(set(effects) == set(selected_by_type), 'selected copy effects')
    for type_id, effect in effects.items():
        observed = indexed(effect['versions'], lambda r: r['versionGuid'].lower(), 'effect release')
        same(observed, {guid: clean(row) for guid, row in by_type[type_id].items()}, 'all source versions effect')
    tail = [e for e in plan['effects'] if e['action'] != 'copyTypeAllVersions']
    require(len(tail) == 1, 'operation effect count')
    same(tail[0], {'action':plan['kind'],'targetLibraryId':plan['payload']['destinationLibraryId'],
                   'projectPath':plan.get('projectPath')}, 'destination operation effect')
    if result is not None:
        returned = indexed(result['typeSynchronization'], lambda r: r['typeGuid'].lower(), 'returned type')
        require(set(returned) == set(sync), 'returned type coverage')
        for type_id, item in returned.items():
            actual = {r['versionGuid'].lower(): r['version'] for r in item['versions']}
            same(actual, {guid:r['versionNumber'] for guid,r in expected_by_type[type_id].items()}, 'returned exact release set')
            default = item['defaultVersion']; planned = sync[type_id]['expectedDefault']
            same(default['versionGuid'].lower(), planned['versionGuid'].lower(), 'returned planned default')
            same(default['version'], planned['version'], 'returned planned default number')
    return {'synchronizationPlanValidated':True, 'synchronizationResultBound':result is not None,
            'destinationBeforeInventoryVerified':False, 'nativeSynchronizationVerified':False,
            'masterCopyContentVerified':False}
