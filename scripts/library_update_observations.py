"""Check recorded update analyses without resolving native objects or files."""
from collections import deque
from library_operation_observations import _version


def require(condition, field):
    if not condition:
        raise ValueError('Inconsistent update analysis: ' + field)


def indexed(rows, key, field):
    result = {}
    for row in rows:
        identity = key(row)
        require(identity not in result, 'duplicate ' + field)
        result[identity] = row
    return result


def path(value):
    require(len(value.encode('utf-16-le')) // 2 <= 4096
            and value.startswith('/') and '\\' not in value
            and not any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in value)
            and all(part not in ('', '.', '..') for part in value[1:].split('/')), 'canonical exact path')
    return value


def check_update(plan, same):
    require(plan['kind'] == 'library_update_project', 'operation kind')
    impact, baseline, guard = (plan[k] for k in ('updateImpact', 'updateBaseline', 'updateCleanupGuard'))
    plc = path(impact['plcPath'])
    same(baseline['plcPath'], plc, 'baseline PLC')
    target = plan['payload'].get('target', {})
    if 'plcPath' in target:
        same(target['plcPath'], plc, 'requested PLC')
    require(isinstance(plan.get('projectPath'), str) and bool(plan['projectPath'].strip()), 'project path')
    known = set(baseline['objectPaths'])
    for value in known:
        path(value)
        require(value.startswith(plc + '/'), 'object outside selected PLC')
    uses = indexed(baseline['uses'], lambda r: path(r['objectPath']), 'baseline use')
    require(set(uses) <= known, 'baseline use outside object inventory')
    affected = indexed(impact['affectedUses'], lambda r: path(r['objectPath']), 'affected use')
    types = indexed(impact['types'], lambda r: r['requested']['typeGuid'].lower(), 'requested type')
    indexed(plan['dependencies'], lambda r: r['rootVersionGuid'].lower(), 'dependency root')
    pins = {}
    for closure in plan['dependencies']:
        require('versions' in closure and 'masterCopy' not in closure, 'unavailable or master dependency closure')
        rows = indexed(closure['versions'], lambda r: r['versionGuid'].lower(), 'dependency version')
        root = closure['rootVersionGuid'].lower()
        require(root in rows, 'dependency root')
        seen, pending = set(), [root]
        while pending:
            current = pending.pop()
            if current in seen:
                continue
            require(current in rows, 'missing dependency version')
            seen.add(current)
            pending.extend(g.lower() for g in rows[current]['requiredVersionGuids'])
        require(seen == set(rows), 'extraneous dependency version')
        for row in rows.values():
            clean = {k: v for k, v in row.items() if k != 'requiredVersionGuids'}
            key = row['typeGuid'].lower()
            if key in pins:
                same(pins[key], clean, 'conflicting dependency type')
            pins[key] = clean
    require(set(pins) == set(types), 'dependency type coverage')
    indexed(pins.values(), lambda r: r['versionGuid'].lower(), 'requested version identity')
    selected = plan['selection']
    require(all('masterCopyPath' not in row for row in selected), 'master copy update')
    require({row['versionGuid'].lower() for row in selected}
            == {row['rootVersionGuid'].lower() for row in plan['dependencies']}, 'selected dependency roots')
    roots = {row['typeGuid'].lower() for row in selected}
    for row in selected:
        require(row['typeGuid'].lower() in pins, 'selected type')
        same(row['versionGuid'].lower(), pins[row['typeGuid'].lower()]['versionGuid'].lower(), 'selected version')
    force = plan['payload'].get('forceDefault') is True
    if 'forceDefault' in impact:
        same(impact['forceDefault'], force, 'impact forceDefault')
    for row in baseline.get('peerPlcUses', []):
        require(path(row['plcPath']) != plc, 'peer PLC is the selected PLC')
    indexed(baseline.get('peerPlcUses', []), lambda r: (r['plcPath'], r['versionGuid'].lower()), 'peer PLC use')
    complete = True
    for type_id, item in types.items():
        requested = item['requested']
        same(requested, pins[type_id], 'requested dependency identity')
        require(requested['state'] == 'Committed' and requested['isDefault'], 'committed source default')
        # forceDefault applies to selected roots only; it waives "highest release" and "newer project default".
        forced = item.get('forceDefault') is True
        require(not forced or force and type_id in roots, 'forced type outside the forced roots')
        if 'defaultEffect' in item:
            before = item['projectDefaultBefore']
            newer = before is not None and _version(before['version']) > _version(requested['version'])
            require(forced or not newer, 'newer project default')
            same(item['defaultEffect'], 'preserveDefault' if before is not None and before['versionGuid'].lower() == requested['versionGuid'].lower()
                 else 'promoteIfHigher' if not forced else 'forcedDowngrade' if newer else 'forcedDefault', 'default effect')
        releases = indexed(item['libraryVersionsEligibleForCopy'], lambda r: r['versionGuid'].lower(), 'eligible release')
        version_id = requested['versionGuid'].lower()
        require(version_id in releases, 'missing requested release')
        same(releases[version_id], requested, 'requested release')
        require(all(r['typeGuid'].lower() == type_id for r in releases.values()), 'eligible release type')
        require([key for key, row in releases.items() if row['isDefault']] == [version_id], 'eligible default coverage')
        require(forced or all(_version(r['version']) <= _version(requested['version'])
                              for r in releases.values() if r['state'] == 'Committed'), 'newer committed release')
        local = [r for r in affected.values() if r['typeGuid'].lower() == type_id]
        same(item['affectedUseCount'], len(local), 'affected use count')
        same(item['scope'], 'allNativeUsesInSelectedPlc' if local else 'librarySynchronizationOnly_NoNewEngineeringInstance', 'type scope')
        require(local or type_id not in roots, 'selected root without uses')
        before_versions = {}
        for row in local:
            same(row['requestedVersionGuid'].lower(), version_id, 'requested use version')
            same(row['requestedVersion'], requested['version'], 'requested version number')
            before = row['beforeVersionGuid'].lower()
            if before in before_versions:
                same(before_versions[before], row['beforeVersion'], 'before version identity')
            before_versions[before] = row['beforeVersion']
            _version(row['beforeVersion'])
            if before in releases:
                same(row['beforeVersion'], releases[before]['version'], 'known prior release number')
            if before == version_id:
                same(row['beforeVersion'], requested['version'], 'unchanged version number')
            same(row['action'], 'keepExactVersion' if before == version_id else 'update', 'use action')
        comparisons = indexed(item['nativeComparisons'], lambda r: r['beforeVersionGuid'].lower(), 'version comparison')
        require(set(comparisons) == set(before_versions) - {version_id}, 'comparison coverage')
        for comparison in comparisons.values():
            if 'properties' in comparison:
                same(comparison['requestedVersionGuid'].lower(), version_id, 'comparison requested version')
            else:
                complete = False
    require(all(row['typeGuid'].lower() in types for row in affected.values()), 'unknown affected type')
    expected = {p for p, row in uses.items() if row['typeGuid'].lower() in types}
    require(set(affected) == expected, 'affected baseline coverage')
    for p, row in affected.items():
        same(row['typeGuid'].lower(), uses[p]['typeGuid'].lower(), 'baseline type')
        same(row['beforeVersionGuid'].lower(), uses[p]['versionGuid'].lower(), 'baseline version')
    same(impact['nativeComparisonsComplete'], complete, 'comparison completeness')
    same(impact['noOp'], bool(affected) and all(r['action'] == 'keepExactVersion' for r in affected.values())
         and all(t.get('defaultEffect') == 'preserveDefault' for t in types.values() if t.get('forceDefault')), 'noOp')
    for field in ('requiredLibraryUses', 'obRoots', 'reachablePaths', 'objectsWithoutCrossReferenceService'):
        for value in guard[field]:
            path(value)
        require(set(guard[field]) <= known, field + ' outside inventory')
    require(set(guard['requiredLibraryUses']) == set(uses), 'all baseline uses required')
    changed = {p for p, row in affected.items() if row['action'] == 'update'}
    edges = indexed(guard['proofEdges'], lambda r: (path(r['callerPath']), path(r['calleePath'])), 'reference edge')
    outgoing = {}
    for caller, callee in edges:
        require(caller in known and callee in known, 'unresolved reference endpoint')
        require(caller != callee and caller not in changed, 'self or mutable caller edge')
        outgoing.setdefault(caller, []).append(callee)
    reachable = set(guard['obRoots'])
    queue = deque(reachable)
    while queue:
        for callee in outgoing.get(queue.popleft(), []):
            if callee not in reachable:
                reachable.add(callee)
                queue.append(callee)
    require(reachable == set(guard['reachablePaths']), 'reported reachable paths')
    require(set(uses) <= reachable, 'baseline use lacks OB-rooted reference proof')
    return {'updateAnalysisValidated': True, 'updateComparisonsComplete': complete,
            'recordedCleanupReachabilityValidated': True, 'nativeReferenceGraphVerified': False,
            'engineeringContentPreservationVerified': False}
