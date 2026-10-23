"""Require explicit successful compiler diagnostics; never infer success from counts alone."""
from qualification_owned_scratch import require
from urllib.parse import quote


STATES = frozenset({'Success', 'Information', 'Warning'})
BASELINE_POLICY = 'checkpointed-plc-baseline-v1'


def require_clean_compile(receipt, *, expected_targets=None):
    require(isinstance(receipt, dict) and type(receipt.get('errorCount')) is int and receipt['errorCount'] == 0
            and 'error' not in receipt and isinstance(receipt.get('targets'), list) and receipt['targets'],
            'Complete native compile targets and an explicit zero error count are required.')

    def messages(rows, depth=0):
        require(depth <= 64 and isinstance(rows, list), 'Complete compiler message arrays are required.')
        for row in rows:
            require(isinstance(row, dict) and isinstance(row.get('state'), str) and row['state'] in STATES,
                    'Every compiler message requires a known non-error state.')
            if 'children' in row:
                messages(row['children'], depth + 1)

    for target in receipt['targets']:
        require(isinstance(target, dict) and isinstance(target.get('target'), str) and target['target'].strip()
                and isinstance(target.get('state'), str) and target['state'] in STATES
                and type(target.get('errorCount')) is int and target['errorCount'] == 0
                and 'error' not in target and target.get('messagesTruncated') is False,
                'Named native targets require successful state and complete explicit diagnostics.')
        messages(target.get('messages'))

    device_scope = 'targetIdentityPolicy' in receipt or 'requiredTargets' in receipt or any('targetPath' in row for row in receipt['targets'])
    if device_scope:
        require(receipt.get('targetIdentityPolicy') == 'device-path-v1' and receipt.get('scopeComplete') is True,
                'Complete exact device compiler scope is required.')
        observed = [row.get('targetPath') for row in receipt['targets']]
        require(all(isinstance(path, str) and path.startswith('/projects/') for path in observed), 'Exact device compiler paths are required.')
        required = receipt.get('requiredTargets')
        require(isinstance(required, list) and required and all(isinstance(path, str) for path in required)
                and len(required) == len(set(required)) and set(required) == set(observed),
                'Compiler receipts do not cover the declared complete device scope.')
    else:
        observed = [row['target'] for row in receipt['targets']]
    require(len(observed) == len(set(observed)), 'Duplicate compiler targets cannot establish coverage.')
    if expected_targets is not None:
        require(isinstance(expected_targets, (list, tuple)) and expected_targets
                and all(isinstance(path, str) and path.strip() for path in expected_targets)
                and len(expected_targets) == len(set(expected_targets)), 'Exact distinct expected compiler targets are required.')
        require(set(observed) == set(expected_targets), 'Compiler targets differ from the required selection.')

    # Retain the original route-capture guard over all nested diagnostic metadata.
    def inspect(value, depth=0):
        require(depth <= 200, 'Compiler diagnostic metadata exceeds the inspection depth.')
        if isinstance(value, dict):
            if 'state' in value:
                require(isinstance(value['state'], str) and value['state'] in STATES,
                        'Native compiler reports an Error or unknown state, regardless of aggregate errorCount.')
            require(value.get('messagesTruncated') is not True and 'error' not in value,
                    'Truncated or failed compiler diagnostics cannot establish success.')
            if 'errorCount' in value:
                require(type(value['errorCount']) is int and value['errorCount'] == 0, 'Native compiler reports errors.')
            for child in value.values():
                inspect(child, depth + 1)
        elif isinstance(value, list):
            for child in value:
                inspect(child, depth + 1)
    inspect(receipt)


def require_plc_compile(receipt, plc_path):
    require(isinstance(receipt, dict) and 'targetIdentityPolicy' not in receipt,
            'PLC software compilation cannot claim a device compiler identity policy.')
    require_clean_compile(receipt, expected_targets=[plc_path])


def require_compile_only(prepared, applied, plc_path):
    """Require the original checkpointed compile operation, never a same-value write."""
    require(isinstance(prepared, dict) and isinstance(applied, dict), 'Original compile preparation and outcome are required.')
    preview = prepared.get('preview')
    require(isinstance(preview, dict) and preview.get('kind') == 'plc_compile' and preview.get('payload') == {}
            and isinstance(preview.get('effects'), list) and len(preview['effects']) == 1
            and isinstance(preview['effects'][0], dict) and preview['effects'][0].get('action') == 'compile' and preview['effects'][0].get('plcPath') == plc_path,
            'Compile-only preview must identify the exact PLC without a mutation payload.')
    require(prepared.get('state') == 'prepared' and applied.get('state') == 'applied'
            and isinstance(prepared.get('changeId'), str) and prepared['changeId']
            and applied.get('changeId') == prepared['changeId'] == preview.get('changeId')
            and isinstance(prepared.get('previewHash'), str) and prepared['previewHash']
            and applied.get('previewHash') == prepared['previewHash'] and applied.get('preview') == preview
            and type(prepared.get('processId')) is int and prepared['processId'] > 0
            and type(applied.get('processId')) is int and applied.get('processId') == prepared['processId'], 'Compile-only outcome differs from its preparation.')
    stages = applied.get('stages')
    required = ['prechange_project_saved', 'recovery_archive_created', 'compile_only_requested',
                'compiler_dispatching', 'compile_finished', 'changed_project_saved']
    require(isinstance(stages, list) and 'mutation_applied' not in stages
            and all(stages.count(stage) == 1 for stage in required)
            and [stage for stage in stages if stage in required] == required,
            'Compile-only checkpoint, dispatch, completion and save stages are missing or reordered.')
    require_plc_compile(applied.get('compile'), plc_path)


def require_baseline_compile(source, plc_path):
    """Bind a safety baseline to original calls before exports and import preparation."""
    require(source.get('baselineCompilePolicy') == BASELINE_POLICY, 'A fresh checkpointed baseline compilation is required.')
    session = source['sessions'][0]
    calls = session['calls']
    mutation = next((i for i, row in enumerate(calls) if row['tool'] == 'change_prepare'
                     and row['arguments'].get('kind') == 'safety_import_xml' and row.get('status') == 'passed'), None)
    require(mutation is not None, 'Original safety import preparation is missing.')
    preparations = [(i, row) for i, row in enumerate(calls[:mutation]) if row['tool'] == 'change_prepare'
                    and row['arguments'].get('kind') == 'plc_compile']
    require(len(preparations) == 1, 'One fresh baseline compile preparation is required before the edit.')
    index, preparation = preparations[0]
    prepared = preparation['result']
    outcomes = [(i, row) for i, row in enumerate(calls[index+1:mutation], index+1) if row['tool'] == 'change_apply']
    require(len(outcomes) == 1, 'Baseline compile outcome is missing or intervening changes were applied.')
    applied_index, application = outcomes[0]
    args = application['arguments']; applied = application['result']
    require(preparation.get('status') == application.get('status') == 'passed'
            and preparation['arguments'].get('payload') == {} and preparation['arguments'].get('plcPath') == plc_path
            and args.get('changeId') == prepared.get('changeId') and args.get('previewHash') == prepared.get('previewHash')
            and all(args.get(flag) is True for flag in ('checkpointSave', 'compile', 'save')),
            'Original baseline compiler request differs from its exact preparation.')
    require_compile_only(prepared, applied, plc_path)
    require(type(applied.get('processId')) is int and applied['processId'] == session['processId']
            and prepared['preview'].get('project', {}).get('path') == source['projectPath']
            and source.get('baselineCompile') == {'prepared': prepared, 'applied': applied},
            'Baseline compiler owner or retained summary differs from original calls.')
    for path, snapshot in source['nativePreservation']['before'].items():
        exports = [(i, row) for i, row in enumerate(calls[:mutation]) if row['tool'] == 'block_read'
                   and row['arguments'].get('blockPath') == path and row.get('status') == 'passed'
                   and row['result'].get('artifact') == snapshot.get('artifact')]
        require(exports and all(i > applied_index for i, _ in exports), 'Baseline export predates its fresh compilation.')
    require(not any(row['tool'] in ('change_restore', 'project_open', 'project_retrieve', 'tia_create_scratch')
                    for row in calls[applied_index+1:mutation]), 'Baseline owner changed before mutation preparation.')
    return applied


def device_inventory_targets(pages, project_path):
    """Read the independently captured complete device inventory, including empty devices."""
    require(isinstance(pages, list) and 0 < len(pages) <= 1000, 'Bounded device inventory pages are required.')
    require(all(isinstance(page, dict) for page in pages) and isinstance(pages[0].get('snapshot'), dict),
            'Device inventory pages require snapshot objects.')
    metadata = pages[0]['snapshot']; coverage = metadata.get('coverage')
    require(isinstance(coverage, dict), 'Device inventory coverage is missing.')
    require(metadata.get('projectPath') == project_path and isinstance(metadata.get('snapshotId'), str) and metadata['snapshotId']
            and type(metadata.get('totalRows')) is int and 0 < metadata['totalRows'] <= 50000
            and coverage.get('scopeComplete') is True and coverage.get('truncated') is False
            and coverage.get('warnings') == [] and coverage.get('devicePath') is None,
            'An exact complete unfiltered project device inventory is required.')
    paths, ids, cursors, devices, offset = set(), set(), set(), [], 0
    for index, page in enumerate(pages):
        require(page.get('snapshot') == metadata and type(page.get('offset')) is int and page['offset'] == offset
                and isinstance(page.get('rows'), list) and page['rows'], 'Device inventory pagination changed or omitted rows.')
        require((page.get('nextCursor') is None) == (index == len(pages) - 1), 'Device inventory continuation is incomplete.')
        cursor = page.get('nextCursor')
        if cursor is not None:
            require(isinstance(cursor, str) and cursor and cursor not in cursors, 'Device inventory cursor repeated or invalid.')
            cursors.add(cursor)
        for row in page['rows']:
            require(isinstance(row, dict), 'Device inventory rows must be objects.')
            path, row_id = row.get('path'), row.get('rowId')
            require(isinstance(path, str) and path.startswith('/projects/') and path not in paths
                    and isinstance(row_id, str) and row_id and row_id not in ids and row.get('projectPath') == project_path,
                    'Device inventory row ownership or identity differs.')
            require(row.get('kind') in ('device', 'cpu', 'headModule', 'builtIn', 'module'), 'Unknown device inventory row kind.')
            paths.add(path); ids.add(row_id)
            if row.get('kind') == 'device':
                require(row.get('parentPath') is None, 'Device row has an unexpected parent.')
                devices.append(path)
        offset += len(page['rows'])
        require(offset <= metadata['totalRows'], 'Device inventory exceeds its declared row count.')
    require(offset == metadata['totalRows'] and devices, 'Complete device inventory coverage is missing.')
    return devices


def capture_device_targets(call, project_path):
    pages, seen = [], set()
    page = call('device_inventory', {'pageSize': 200, 'maxVisited': 50000})
    while True:
        require(isinstance(page, dict) and isinstance(page.get('snapshot'), dict), 'Device inventory response requires a snapshot object.')
        pages.append(page)
        require(len(pages) <= 1000, 'Device inventory pagination exceeds its bound.')
        cursor = page.get('nextCursor')
        if cursor is None: break
        require(isinstance(cursor, str) and cursor and cursor not in seen, 'Device inventory cursor repeated or invalid.')
        seen.add(cursor)
        page = call('device_inventory', {'snapshotId': pages[0]['snapshot']['snapshotId'], 'cursor': cursor, 'pageSize': 200})
    return {'pages': pages, 'targets': device_inventory_targets(pages, project_path)}


def fixture_plc_selection(inventory, project_path, process_id):
    require(isinstance(inventory, dict) and isinstance(project_path, str) and project_path
            and type(process_id) is int and process_id > 0, 'Exact fixture inventory owner is required.')
    project, coverage = inventory.get('project'), inventory.get('completeness')
    require(isinstance(project, dict) and project.get('path') == project_path
            and isinstance(project.get('name'), str) and project['name'].strip()
            and type(inventory.get('processId')) is int and inventory['processId'] == process_id,
            'Fixture inventory belongs to another project or native process.')
    require(isinstance(coverage, dict) and coverage.get('scopeComplete') is True
            and coverage.get('truncated') is False and inventory.get('warnings') == [],
            'Complete warning-free fixture inventory is required.')
    plcs, hardware = inventory.get('plcs'), inventory.get('hardware')
    require(isinstance(plcs, list) and len(plcs) == 1 and isinstance(plcs[0], dict)
            and isinstance(hardware, list) and 0 < len(hardware) <= 50000,
            'Exactly one inventoried PLC and bounded hardware are required.')
    plc = plcs[0]
    root = '/projects/' + quote(project['name'], safe='') + '/'
    require(isinstance(plc.get('name'), str) and plc['name'].strip()
            and isinstance(plc.get('hardwarePath'), str) and plc['hardwarePath'].startswith(root)
            and plc.get('path') == plc['hardwarePath'] + '/plc/' + quote(plc['name'], safe=''),
            'PLC identity must belong to its exact inventoried hardware owner.')
    require(all(isinstance(row, dict) and isinstance(row.get('path'), str) for row in hardware),
            'Malformed fixture hardware inventory.')
    paths = [row['path'] for row in hardware]
    require(len(paths) == len(set(paths)) and paths.count(plc['hardwarePath']) == 1,
            'PLC hardware owner is missing or ambiguous.')
    return {'projectName': project['name'], 'plcPath': plc['path']}
