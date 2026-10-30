"""Inspect retained compiler diagnostics and scope without dispatching compilation."""
SUCCESS_STATES = frozenset(('Success', 'Information', 'Warning'))


def require(condition, field):
    if not condition:
        raise ValueError('Inconsistent compiler observation: ' + field)


def distinct(values, field):
    require(len(values) == len(set(values)), 'duplicate ' + field)


def check_compilation(value, same):
    targets = value['targets']
    modern = 'targetIdentityPolicy' in value
    identities = [row['targetPath'] if modern else row['target'] for row in targets]
    distinct(identities, 'target')
    # Devices without a compiler service (a Startdrive drive) cover their required target only as notCompiled.
    skipped = [row['targetPath'] for row in value.get('notCompiled', [])]
    if modern:
        distinct(value['requiredTargets'], 'required target')
        distinct(skipped, 'not compiled target')
        require(not set(identities) & set(skipped), 'target both compiled and not compiled')
        require(set(identities) | set(skipped) == set(value['requiredTargets']), 'required target coverage')
    same(value['errorCount'], sum(row['errorCount'] for row in targets), 'aggregate errorCount')
    same(value['warningCount'], sum(row['warningCount'] for row in targets), 'aggregate warningCount')
    complete = True
    successful = value['errorCount'] == 0
    for row in targets:
        previous = -1
        for message in row['messages']:
            require(message['depth'] <= previous + 1, 'flattened message depth')
            previous = message['depth']
        # A truncated capture can end at the message budget OR at the depth
        # bound. An exact 500-message capture need not be truncated.
        if row['messagesTruncated']:
            require(len(row['messages']) == 500 or previous == 64, 'truncation boundary')
        if any(message['depth'] == 64 for message in row['messages']):
            require(row['messagesTruncated'] and previous == 64, 'depth-bound truncation')
        complete = complete and not row['messagesTruncated']
        successful = (successful and row['errorCount'] == 0 and not row['messagesTruncated']
                      and row['state'] in SUCCESS_STATES
                      and all(message['state'] in SUCCESS_STATES for message in row['messages']))
    return {'compilerReportsSuccess': bool(successful), 'compilerMessagesComplete': bool(complete),
            'compilerTargetIdentity': 'device-path-v1' if modern else 'target-labels', 'compilerNotCompiled': skipped}


def bind_scope(compilation, context, same, is_lock=False):
    selected = context.get('target') if is_lock else context['payload'].get('target')
    selected = selected if isinstance(selected, dict) else {}
    family = selected.get('family')
    if family in ('classic', 'unified', 'hmi'):
        require(family in ('classic', 'unified'), 'HMI family')
        path = selected.get('hmiPath')
        require(isinstance(path, str) and bool(path.strip()), 'HMI path')
        if family == 'unified':
            path, separator, _ = path.partition('/items/')
            require(bool(path and separator), 'Unified owning-device path')
        require('runtimeVerified' in compilation and len(compilation['targets']) == 1, 'HMI compiler route')
        same(compilation['targets'][0]['target'], path, 'HMI compile target')
        return 'hmi-target'
    require('runtimeVerified' not in compilation, 'project compiler route')
    if not is_lock and 'compileTargetPaths' in context:
        paths, names = context['compileTargetPaths'], context['compileScope']
        distinct(paths, 'prepared target path')
        require(bool(paths) and 'targetIdentityPolicy' in compilation, 'prepared device-path policy')
        expected = dict(zip(paths, names))
        observed = {row['targetPath']: row['target'] for row in compilation['targets'] + compilation.get('notCompiled', [])}
        same(observed, expected, 'prepared device path/name coverage')
        return 'prepared-device-paths'
    if not is_lock:
        names = context.get('compileScope')
        require(isinstance(names, list) and bool(names), 'prepared compiler names')
        distinct(names, 'prepared target name')
        require('targetIdentityPolicy' not in compilation, 'legacy prepared target identity')
        require(set(names) == {row['target'] for row in compilation['targets']}, 'prepared target names')
        return 'prepared-target-labels'
    # A lock does not retain the original compilation inventory. Its declared
    # device coverage can be checked, but cannot be called independently bound.
    return 'lock-declared-targets'


def check_compiler_context(value, kind, validate_compilation, same):
    is_lock = kind == 'compiler-lock'
    journal = None if is_lock else value['journal']
    context = value if is_lock else journal['preview']
    project = context.get('projectPath') is not None
    observed = context.get('compilation') if is_lock else journal.get('compile')
    final = is_lock or journal['state'] == 'applied'
    if not is_lock:
        if 'engineeringVerified' in journal:
            require(type(journal['engineeringVerified']) is bool, 'engineeringVerified type')
        if final:
            require('engineeringVerified' in journal, 'missing applied engineering verification observation')
            same(journal['engineeringVerified'], project, 'applied engineering verification scope')
        if 'compileScope' in context or 'compileTargetPaths' in context or context['kind'] in (
                'library_instantiate', 'library_integrate', 'library_update_project', 'library_create_master_copy'):
            require(project, 'prepared project scope without project path')
        require('compile' not in journal or observed is not None, 'null journal compiler result')
    if project:
        require(isinstance(context['projectPath'], str) and bool(context['projectPath'].strip()), 'project path')
    if observed is None:
        require(not (final and project), 'missing compiler result for applied project operation')
        if journal is not None:
            require(journal.get('engineeringVerified') is not True, 'engineering verification without compiler result')
        return {'compilerObservationValidated': False, 'compilerReportsSuccess': None,
                'compilerMessagesComplete': None, 'compilerScopeBinding': 'not-present'}
    require(project, 'compiler result without project observation')
    report = validate_compilation(observed)
    binding = bind_scope(observed, context, same, is_lock)
    if final or (journal is not None and journal.get('engineeringVerified') is True):
        require(report['compilerReportsSuccess'], 'applied/verified operation with failed or incomplete diagnostics')
    return {**report, 'compilerObservationValidated': True, 'compilerScopeBinding': binding}
