"""Bind retained library effects and selectors; never resolve native objects/files."""
import ntpath
from urllib.parse import unquote

from library_component_preview_observations import descriptor, name_key
from library_operation_observations import _pin, _version


def require(condition, field):
    if not condition:
        raise ValueError('Inconsistent library preview effects: ' + field)


def windows_path(value):
    require(isinstance(value, str) and bool(value.strip()) and ntpath.isabs(value), 'absolute recorded file path')
    return ntpath.normcase(ntpath.normpath(value))


def object_path_info(path):
    parts = path.split('/'); position = 3; plc = scope = None
    while position + 1 < len(parts) and parts[position] == 'groups': position += 2
    if position + 1 < len(parts) and parts[position] in ('devices','ungrouped'):
        position += 2
        while position + 1 < len(parts) and parts[position] == 'items': position += 2
        if position + 1 < len(parts) and parts[position] == 'plc' and parts[position + 1]:
            plc = '/'.join(parts[:position + 2]); scope = plc; position += 2
            if position + 1 < len(parts) and parts[position] in ('units','safetyUnits'):
                scope = '/'.join(parts[:position + 2]); position += 2
    namespace = ''
    if plc is None:
        kind = 'project' if len(parts) == 3 else 'hardware'
    elif path == plc:
        kind = 'plc'
    elif path == scope:
        kind = 'safetyUnit' if scope.startswith(plc + '/safetyUnits/') else 'softwareUnit'
    else:
        tail = path[len(scope):]
        kind = next((v for k,v in (('/blocks/','block'),('/types/','type'),('/tags/','tagTable'),('/technology/','technology')) if tail.startswith(k)), None)
        if position < len(parts) and parts[position] in ('blocks','types'):
            position += 1
            while position + 1 < len(parts) and parts[position] in ('groups','systemGroups'): position += 2
            if position + 2 == len(parts) - 1 and parts[position] == 'namespaces':
                namespace = unquote(parts[position + 1])
    return plc, scope, kind, namespace


def source_identity(identity, payload, project_path, same):
    same(identity['projectPath'], project_path, 'source object project')
    path = identity['objectPath']; parts = path.split('/')
    require(len(parts) >= 3 and parts[0] == '' and parts[1] == 'projects' and bool(parts[2]), 'source object path')
    target = payload.get('target') if isinstance(payload.get('target'),dict) else {}
    selectors = {**target, **payload['object']}
    for field in ('kind','objectPath','plcPath','scopePath','name','namespace','projectPath','projectName'):
        require(field not in selectors or selectors[field] is None or isinstance(selectors[field],str), 'typed source selector ' + field)
    # The operation kind occupies the outer argument's kind. A successful
    # source-object resolve must override it with a supported native kind.
    require('kind' in selectors, 'source object kind selector override')
    selected_kind = selectors['kind']
    if isinstance(selected_kind, str) and selected_kind.strip():
        same(selected_kind, identity['kind'], 'source object kind selector')
    for field in ('objectPath', 'plcPath', 'scopePath'):
        wanted = selectors.get(field)
        if isinstance(wanted, str) and wanted.strip():
            same(identity[field], wanted, 'source selector ' + field)
    if isinstance(selectors.get('projectPath'), str) and selectors['projectPath'].strip():
        same(windows_path(identity['projectPath']), windows_path(selectors['projectPath']), 'source project selector')
    if isinstance(selectors.get('projectName'), str) and selectors['projectName'].strip():
        same(unquote(parts[2]), selectors['projectName'], 'source project name')
    name_scope = 'not-requested'
    if isinstance(selectors.get('name'), str) and selectors['name'].strip():
        wanted = selectors['name']; actual = identity['name']
        if wanted.isascii() and actual.isascii():
            same(name_key(actual), name_key(wanted), 'source name selector'); name_scope = 'ASCII-case-insensitive'
        elif wanted == actual:
            name_scope = 'exact-non-ASCII'
        else:
            # Native OrdinalIgnoreCase is not Python's full Unicode casing.
            name_scope = 'unverified-non-ASCII-case-comparison'
    if 'namespace' in selectors and selectors['namespace'] is not None and 'namespace' in identity:
        same(identity['namespace'], selectors['namespace'], 'source namespace selector')
    plc, scope, kind, namespace = object_path_info(path)
    same(identity['kind'], kind, 'source kind from canonical path')
    same(identity['plcPath'], plc, 'source PLC from canonical path')
    same(identity['scopePath'], scope, 'source scope from canonical path')
    same(identity['name'], unquote(parts[-1]), 'source name from canonical path')
    if 'namespace' in identity:
        same(identity['namespace'], namespace, 'source namespace from canonical path')
    if selectors.get('namespace') is not None:
        same(namespace, selectors['namespace'], 'selected namespace from canonical path')
    if identity['kind'] in ('project', 'hardware'):
        require(identity['plcPath'] is None and identity['scopePath'] is None, 'non-PLC source owner')
    else:
        plc, scope = identity['plcPath'], identity['scopePath']
        require(isinstance(plc, str) and isinstance(scope, str) and (scope == plc or scope.startswith(plc + '/')), 'source scope owner')
        require(path == scope or path.startswith(scope + '/'), 'source object owner')
    return name_scope


def dependency_observations(plan, same):
    if plan['kind'] in ('library_copy_elements','library_publish'):
        return None  # The synchronization checker validates every release root.
    versions = [r for r in plan['selection'] if 'masterCopyPath' not in r]
    masters = [r for r in plan['selection'] if 'masterCopyPath' in r]
    roots = {}; captured = {}; complete = True
    maintenance = {'library_edit_version','library_release_version','library_discard_version','library_set_default',
                   'library_type_policy','library_organize','library_delete','library_save','library_archive'}
    for closure in plan['dependencies']:
        root = closure['rootVersionGuid'].lower()
        if 'masterCopy' in closure:
            require(bool(masters) and 'versions' in closure, 'selected master dependency observation')
        else:
            require(root not in roots, 'duplicate selected dependency root'); roots[root] = closure
        if 'versions' not in closure:
            require(plan['kind'] in maintenance and closure.get('scopeComplete') is False, 'unavailable dependency scope')
            complete = False; continue
        rows = {r['versionGuid'].lower():r for r in closure['versions']}
        require(len(rows) == len(closure['versions']), 'duplicate dependency version')
        pending = [root]; seen = set()
        while pending:
            key = pending.pop()
            if key in seen: continue
            require(key in rows, 'missing dependency edge target'); seen.add(key)
            row = rows[key]; _version(row['version'])
            if plan['kind'] in ('library_instantiate','library_update_project') or 'masterCopy' in closure:
                require(row['state']=='Committed' and row['doNotUse'] is False, 'committed usable consumption dependency')
            if key in captured: same(captured[key],row,'repeated dependency metadata')
            captured[key] = row
            pending.extend(g.lower() for g in row['requiredVersionGuids'])
        require(seen == set(rows), 'extraneous dependency version')
    same(set(roots),{r['versionGuid'].lower() for r in versions},'selected dependency roots')
    for row in versions:
        key = row['versionGuid'].lower()
        if 'versions' in roots[key]:
            same(captured[key]['typeGuid'].lower(),row['typeGuid'].lower(),'selected dependency type')
            require(plan['kind'] not in ('library_release_version','library_discard_version'), 'InWork authoring dependency observation')
    return complete


def check_preview_effects(plan, same):
    kind, payload = plan['kind'], plan['payload']; effects = plan['effects']
    if kind in ('library_instantiate','library_integrate','library_update_project','library_create_type',
                'library_create_master_copy','library_edit_version','library_release_version','library_discard_version'):
        require(isinstance(plan.get('projectPath'),str) and bool(plan['projectPath'].strip()), 'selected project observation')
    source_value = payload.get('source')
    source = source_value.get('libraryId') if isinstance(source_value,dict) else None
    destination = payload.get('destinationLibraryId')
    expected_resources = {value for value in (source, destination) if value is not None}
    same({r['libraryId'] for r in plan['resources']}, expected_resources, 'exact selected resources')
    dependencies_complete = dependency_observations(plan, same)
    same(effects[-1], dict(action=kind, targetLibraryId=destination or source, projectPath=plan.get('projectPath')), 'terminal operation effect')
    target = payload.get('target') if isinstance(payload.get('target'),dict) else {}
    family = target.get('family')
    specialized = 'synchronization' if kind in ('library_copy_elements','library_publish') else (
        'update' if kind == 'library_update_project' else (
        'hmi' if kind in ('library_instantiate','library_integrate') and family in ('classic','unified') else (
        'component' if kind == 'library_integrate' else None)))
    actions = [row['action'] for row in effects[:-1]]
    if specialized == 'synchronization':
        same(actions, ['copyTypeAllVersions'] * sum('masterCopyPath' not in r for r in plan['selection']), 'copy effect count/order')
    elif specialized == 'update':
        require(len(effects) == 2, 'update effect count')
        require(isinstance(plan.get('updateImpact'), dict) and isinstance(plan['updateImpact'].get('plcPath'), str), 'update target observation')
        same(effects[0], dict(action='updateAllSelectedTypeUses',plcPath=plan['updateImpact']['plcPath'],
                             forceDefault=False,deleteUnusedVersions=False), 'update effect')
    elif specialized == 'component':
        same(actions, ['planPlcComponent'], 'component effect count/order')
    elif kind == 'library_save' and 'existingRecoveryCheckpoint' in plan:
        same(actions, ['persistCurrentPartialNativeState'], 'partial-state persistence effect')
        same(effects[0]['recoveryChangeId'], payload.get('recoveryChangeId'), 'failed operation recovery pin')
        checkpoint = plan['existingRecoveryCheckpoint']
        require(source is not None, 'checkpoint source library')
        recorded = next(r for r in plan['resources'] if r['libraryId'] == source)
        same(windows_path(checkpoint['originalPath']), windows_path(recorded['path']), 'checkpoint original library')
        windows_path(checkpoint['archivePath'])
    else:
        require(not actions, 'unexpected additional operation effect')

    allowed = {
        'outputAbsent': {'library_create','library_upgrade','library_archive'},
        'upgradeSourceHash': {'library_upgrade'}, 'upgradeSourceHashScope': {'library_upgrade'},
        'documentsHash': {'library_create_type'}, 'documentSource': {'library_create_type'},
        'sourceObject': {'library_create_master_copy'}, 'existingRecoveryCheckpoint': {'library_save'},
        'componentContentPins': {'library_instantiate'}, 'typeSynchronization': {'library_copy_elements','library_publish'},
        'targetPreview': {'library_instantiate','library_integrate'},
        'integrationPlan': {'library_integrate'}, 'applySupported': {'library_integrate'}, 'applyError': {'library_integrate'},
        'updateImpact': {'library_update_project'}, 'updateBaseline': {'library_update_project'}, 'updateCleanupGuard': {'library_update_project'}}
    for field, kinds in allowed.items():
        require(field not in plan or kind in kinds, 'unexpected ' + field)
    require(('targetPreview' in plan) == (specialized == 'hmi'), 'HMI preview scope')
    require(('integrationPlan' in plan) == (specialized == 'component'), 'component preview scope')
    if kind in ('library_create','library_upgrade','library_archive'):
        require('outputAbsent' in plan, 'recorded output destination')
        windows_path(plan['outputAbsent'])
        same(ntpath.basename(plan['outputAbsent']), {'library_create':'created','library_upgrade':'upgraded','library_archive':'published.zal21'}[kind], 'output name')
    if kind == 'library_upgrade':
        archive = ntpath.splitext(payload['path'])[1].lower().startswith('.zal')
        require('upgradeSourceHash' in plan and 'upgradeSourceHashScope' in plan, 'upgrade source capture')
        scope = 'Complete original archive bytes' if archive else 'Sorted SHA-256 manifest of every relative companion file; bound to the approved preview hash'
        same(plan['upgradeSourceHashScope'], scope, 'upgrade hash scope')
        if archive:
            same(plan['upgradeSourceHash'], payload['expectedSha256'].lower(), 'original upgrade archive pin')
    if kind == 'library_create_type':
        require('documentSource' in plan and 'documentsHash' in plan, 'authoring snapshot')
        document = plan['documentSource']
        same(windows_path(document['originalDirectory']), windows_path(payload['documentDirectory']), 'original document directory')
        require(windows_path(document['snapshotDirectory']) != windows_path(document['originalDirectory']), 'separate document snapshot')
        same(document['sha256'], plan['documentsHash'], 'document hash pin')
        same(document['hashScope'], 'Complete sorted companion-file paths and SHA-256 digests', 'document hash scope')
    name_scope = None
    if kind == 'library_create_master_copy':
        require('sourceObject' in plan, 'resolved source observation')
        name_scope = source_identity(plan['sourceObject'], payload, plan.get('projectPath'), same)
    if kind == 'library_instantiate' and specialized != 'hmi':
        require('targetScope' in plan and 'compileTarget' in plan, 'PLC destination observation')
        target = payload.get('target', {})
        if isinstance(target.get('plcPath'), str) and target['plcPath'].strip():
            same(plan['compileTarget'], target['plcPath'], 'selected PLC destination')
        if isinstance(target.get('scopePath'), str) and target['scopePath'].strip():
            same(plan['targetScope'], target['scopePath'], 'selected PLC scope')
        require(plan['targetScope'] == plan['compileTarget'] or plan['targetScope'].startswith(plan['compileTarget'] + '/'), 'PLC scope ownership')
    if 'componentContentPins' in plan:
        require(specialized is None, 'PLC component import pins')
        pins = plan['componentContentPins']; captured = {}
        for closure in plan['dependencies']:
            require('versions' in closure and 'masterCopy' not in closure, 'complete component import dependency capture')
            for row in closure['versions']:
                key = _pin(row)
                if key in captured: same(captured[key],row,'repeated component import metadata')
                captured[key] = row
        require(len({_pin(row) for row in pins}) == len(pins), 'duplicate component content pin')
        same({_pin(row) for row in pins}, set(captured), 'component import pin coverage')
        for row in pins:
            _version(row['version']); descriptor(row['artifact'])
            same(row['contentSha256'],row['artifact']['sha256'],'component import artifact pin')
            same({k:v for k,v in row.items() if k not in ('artifact','contentSha256','contentPolicy')},captured[_pin(row)],'component import metadata')
            if 'contentPolicy' in row:
                require(row['engineeringType']=='Siemens.Engineering.SW.Blocks.CodeBlockLibraryTypeVersion','component import content policy family')
                policy=row['contentPolicy']; descriptor(policy['sourceOracleArtifact'])
                same(policy['sourceOracleSha256'],policy['sourceOracleArtifact']['sha256'],'component import oracle pin')
    return dict(previewEffectsValidated=True, detailedPreviewKind=specialized,
        selectedDependencyObservationsComplete=dependencies_complete,
        sourceObjectNameComparison=name_scope, sourceObjectNamespaceRetained='namespace' in plan.get('sourceObject', {}),
        nativeSelectorResolutionVerified=False, nativeAbsenceVerified=False,
        sourceSnapshotBytesVerified=False, recoveryCheckpointBytesVerified=False,
        componentImportContentPinsBound='componentContentPins' in plan)
