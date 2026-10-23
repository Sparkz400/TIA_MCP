"""Consistency checks for retained library HMI query responses; no native access."""
from library_operation_observations import _link

ASSEMBLIES = {'classic': 'Siemens.Engineering.WinCC', 'unified': 'Siemens.Engineering.WinCCUnified'}
OPERATIONS = {
    ('classic', 'inventory'): 'typed', ('classic', 'screen_type'): 'typed',
    ('classic', 'master_copy'): 'typed', ('classic', 'faceplate_binding'): 'unsupported',
    ('unified', 'inventory'): 'typed', ('unified', 'faceplate_create'): 'typed',
    ('unified', 'faceplate_bind'): 'typed', ('unified', 'tag_create'): 'typed',
    ('unified', 'master_copy'): 'unsupported',
}


def require(condition, field):
    if not condition:
        raise ValueError('Inconsistent HMI query observation: ' + field)


def unique(rows, field):
    require(len({row[field] for row in rows}) == len(rows), 'duplicate ' + field)


def target(value, same):
    if value['family'] == 'classic':
        same(value['compileScopeKind'], 'hmiSoftware', 'Classic compiler scope kind')
        same(value['compileScope'], value['hmiPath'], 'Classic compiler scope')
    else:
        same(value['compileScopeKind'], 'owningDeviceHardwareAndSoftware', 'Unified compiler scope kind')
        owner, separator, _ = value['hmiPath'].partition('/items/')
        require(bool(owner and separator), 'Unified owning device path')
        same(value['compileScope'], owner, 'Unified compiler scope')


def assembly_identity(value, family, same):
    same(value['family'], family, 'assembly family')
    same(value['requiredAssemblyName'], ASSEMBLIES[family], 'required assembly')
    parts = [int(part) for part in value['assemblyVersion'].split('.')]
    require(all(part <= 65535 for part in parts), 'assembly version bounds')
    same(parts[0], value['observedMajorVersion'], 'observed assembly version')
    code = None
    if value['observedMajorVersion'] != 21:
        code = 'HmiVersionMismatch'
    elif (not value['requiredPublicKeyToken']
          or value['observedAssemblyName'] != ASSEMBLIES[family]
          or value['requiredPublicKeyToken'] != value['observedPublicKeyToken']
          or value['observedCulture'] not in ('', 'neutral')):
        code = 'HmiAssemblyIdentityMismatch'
    same(value['code'], code, 'assembly identity rejection')
    same(value['accepted'], code is None, 'assembly identity acceptance')


def check_hmi_query(value, kind, same):
    if kind == 'hmi-request':
        if value['action'] == 'hmi_inventory':
            selected = value['target'] if isinstance(value.get('target'), dict) else value
            path = selected['hmiPath']
            require(bool(path.strip()) and len(path.encode('utf-16-le')) // 2 <= 4096, 'requested HMI path')
        return
    if kind == 'hmi-capabilities':
        require({row['family'] for row in value['modules']} == set(ASSEMBLIES), 'module family coverage')
        for row in value['modules']:
            same(row['assembly'], ASSEMBLIES[row['family']] + '.dll', 'module filename')
            if not row['installedApi']:
                require(not any(key in row for key in ('assemblyIdentity', 'assemblyVersion', 'diagnostic')), 'absent module metadata')
                same(row['apiAvailability'], 'unavailable', 'absent module availability')
            elif 'assemblyIdentity' in row:
                identity = row['assemblyIdentity']
                assembly_identity(identity, row['family'], same)
                require('assemblyVersion' in row, 'module assemblyVersion')
                same(row['assemblyVersion'], identity['assemblyVersion'], 'module assemblyVersion')
                same(row['apiAvailability'], 'available' if identity['accepted'] else 'unavailable', 'module availability')
                if identity['accepted']:
                    require('diagnostic' not in row, 'accepted module diagnostic')
                else:
                    require('diagnostic' in row, 'rejected module diagnostic')
                    same(row['diagnostic'], identity, 'module rejection diagnostic')
            else:
                require('assemblyVersion' not in row and 'diagnostic' in row, 'unreadable module metadata')
                require('nativeException' in row['diagnostic'], 'unreadable module diagnostic')
                same(row['apiAvailability'], 'unavailable', 'unreadable module availability')
        observed = {(row['family'], row['operation']): row['implementation'] for row in value['operations']}
        require(observed == OPERATIONS, 'operation coverage and implementation')
        return
    if kind == 'hmi-targets':
        unique(value['targets'], 'hmiPath')
        for row in value['targets']:
            target(row, same)
        return
    target(value['target'], same)
    unique(value['connections'], 'name')
    if value['target']['family'] == 'classic':
        unique(value['items'], 'path')
        require(not value['truncated'] or bool(value['items']), 'empty truncated Classic inventory')
        require(not value['connectionsTruncated'] or len(value['connections']) == 200, 'connection truncation')
        for row in value['items']:
            prefix = '/screens/' if row['kind'] == 'screen' else '/tagTables/'
            require(row['path'].startswith(prefix), 'Classic item path')
            if row['kind'] == 'tagTable':
                unique(row['tags'], 'name')
                require(not row['tagsTruncated'] or len(row['tags']) == 200, 'tag-table truncation')
    else:
        unique(value['screens'], 'name')
        unique(value['tags'], 'name')
        require(not value['truncated'] or any(value[key] for key in ('screens', 'tags', 'connections')), 'empty truncated Unified inventory')
        for screen in value['screens']:
            unique(screen['faceplates'], 'name')
            # screenItemsTruncated counts ALL screen items. It can be true with
            # zero faceplates and does not imply top-level inventory truncation.
            for faceplate in screen['faceplates']:
                _link(faceplate, None)
                unique(faceplate['interface'], 'property')
                require(not faceplate['interfaceTruncated'] or len(faceplate['interface']) == 200, 'interface truncation')
