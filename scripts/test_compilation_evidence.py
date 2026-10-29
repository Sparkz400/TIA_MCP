"""Portable compile-diagnostic admission; synthetic receipts are never native proof."""
import copy
import unittest

from compilation_evidence import require_clean_compile, require_plc_compile, require_compile_only, device_inventory_targets, capture_device_targets, fixture_plc_selection


def clean_compile(target='/synthetic/device'):
    return {'errorCount': 0, 'targets': [{'target': target, 'state': 'Success',
        'errorCount': 0, 'messagesTruncated': False, 'messages': [{'state': 'Information'}]}]}


def compile_only_records(plc, project, pid, identity='compile-only'):
    preview = {'kind': 'plc_compile', 'payload': {}, 'changeId': identity, 'project': {'path': project},
               'effects': [{'action': 'compile', 'plcPath': plc}]}
    prepared = {'state': 'prepared', 'changeId': identity, 'previewHash': 'synthetic-preview-hash',
                'processId': pid, 'preview': preview}
    applied = {**copy.deepcopy(prepared), 'state': 'applied', 'compile': clean_compile(plc),
               'stages': ['prechange_project_saved', 'recovery_archive_created', 'compile_only_requested',
                          'compiler_dispatching', 'compile_finished', 'changed_project_saved']}
    return prepared, applied


def device_compile(paths):
    return {'errorCount': 0, 'targetIdentityPolicy': 'device-path-v1', 'scopeComplete': True, 'requiredTargets': paths,
            'targets': [{**clean_compile('Same name')['targets'][0], 'targetPath': path} for path in paths]}


def device_page(project, paths):
    return {'snapshot': {'snapshotId': 'synthetic-inventory', 'projectPath': project, 'totalRows': len(paths),
                         'coverage': {'scopeComplete': True, 'truncated': False, 'warnings': [], 'devicePath': None}},
            'offset': 0, 'nextCursor': None,
            'rows': [{'rowId': str(index), 'path': path, 'kind': 'device', 'parentPath': None, 'projectPath': project}
                     for index, path in enumerate(paths)]}


class CompilationEvidenceTests(unittest.TestCase):
    def test_compile_only_proof_requires_original_intent_owner_and_ordered_dispatch(self):
        plc = '/projects/P/devices/S/items/CPU/plc/PLC'
        prepared, applied = compile_only_records(plc, 'fixture.ap21', 7)
        require_compile_only(prepared, applied, plc)
        for defect in ('payload', 'kind', 'target', 'effect', 'changedPreview', 'identity', 'hash', 'process', 'boolProcess',
                       'state', 'missingDispatch', 'lateCheckpoint', 'mutation', 'duplicateStage', 'compilerTarget'):
            before, after = copy.deepcopy(prepared), copy.deepcopy(applied)
            if defect == 'payload': before['preview']['payload'] = {'parameters': {}}
            if defect == 'kind': before['preview']['kind'] = 'to_parameters'
            if defect == 'target': before['preview']['effects'][0]['plcPath'] += '-other'
            if defect == 'effect': before['preview']['effects'][0] = None
            if defect == 'changedPreview': after['preview']['payload'] = {'xml': 'data'}
            if defect == 'identity': after['changeId'] += '-other'
            if defect == 'hash': after['previewHash'] += '-other'
            if defect == 'process': after['processId'] = 8
            if defect == 'boolProcess': before['processId'] = 1; after['processId'] = True
            if defect == 'state': after['state'] = 'recovery_required'
            if defect == 'missingDispatch': after['stages'].remove('compiler_dispatching')
            if defect == 'lateCheckpoint': after['stages'][0], after['stages'][3] = after['stages'][3], after['stages'][0]
            if defect == 'mutation': after['stages'].insert(2, 'mutation_applied')
            if defect == 'duplicateStage': after['stages'].append('compile_finished')
            if defect == 'compilerTarget': after['compile']['targets'][0]['target'] += '-other'
            with self.subTest(defect=defect), self.assertRaises(RuntimeError): require_compile_only(before, after, plc)

    def test_plc_compiler_cannot_substitute_device_scope_with_matching_text(self):
        plc = '/projects/P/devices/S/items/CPU/plc/PLC'
        require_plc_compile(clean_compile(plc), plc)
        with self.assertRaisesRegex(RuntimeError, 'device compiler identity'):
            require_plc_compile(device_compile([plc]), plc)

    def test_fixture_plc_selection_requires_exact_owner_and_complete_inventory(self):
        p = '/projects/Fixture%20%CE%A9/devices/Station/items/CPU'
        source = {'project': {'path': 'owned.ap21', 'name': 'Fixture Ω'}, 'processId': 12,
                  'warnings': [], 'completeness': {'scopeComplete': True, 'truncated': False, 'complete': False},
                  'plcs': [{'name': 'PLC/1', 'hardwarePath': p, 'path': p + '/plc/PLC%2F1'}],
                  'hardware': [{'path': p}]}
        original = copy.deepcopy(source)
        expected = {'projectName': 'Fixture Ω', 'plcPath': p + '/plc/PLC%2F1'}
        if fixture_plc_selection(source, 'owned.ap21', 12) != expected or source != original:
            self.fail('Good singleton fixture identity changed or failed')
        defects = {
            'project': lambda x: x['project'].update(path='foreign.ap21'),
            'process': lambda x: x.update(processId=13),
            'boolProcess': lambda x: x.update(processId=True),
            'incomplete': lambda x: x['completeness'].update(scopeComplete=False),
            'truncated': lambda x: x['completeness'].update(truncated=True),
            'missingCoverage': lambda x: x.pop('completeness'),
            'warnings': lambda x: x.update(warnings=['inaccessible']),
            'missingWarnings': lambda x: x.pop('warnings'),
            'noPlc': lambda x: x.update(plcs=[]),
            'otherPlc': lambda x: x['plcs'].append(copy.deepcopy(x['plcs'][0])),
            'badPlc': lambda x: x.update(plcs=[None]),
            'missingName': lambda x: x['plcs'][0].pop('name'),
            'wrongPlcPath': lambda x: x['plcs'][0].update(path='/foreign/plc'),
            'wrongHardware': lambda x: x['plcs'][0].update(hardwarePath='/foreign/items/CPU'),
            'missingOwner': lambda x: x['hardware'][0].update(path=p + 'other'),
            'duplicateOwner': lambda x: x['hardware'].append(copy.deepcopy(x['hardware'][0])),
            'badHardware': lambda x: x.update(hardware=[None]),
            'noHardware': lambda x: x.update(hardware=[]),
            'excessHardware': lambda x: x.update(hardware=[{'path': str(i)} for i in range(50001)]),
        }
        for name, change in defects.items():
            value = copy.deepcopy(source)
            change(value)
            try: fixture_plc_selection(value, 'owned.ap21', 12)
            except RuntimeError: pass
            else: self.fail('Accepted malformed fixture: ' + name)

    def test_device_scope_accepts_equal_labels_only_with_complete_distinct_paths(self):
        paths = ['/projects/P/devices/Same', '/projects/P/ungrouped/Same']
        receipt = device_compile(paths); original = copy.deepcopy(receipt)
        require_clean_compile(receipt, expected_targets=paths); self.assertEqual(original, receipt)
        for defect in ('policy', 'incomplete', 'missing', 'extra', 'duplicate', 'unknownPath', 'legacy', 'wrongExpected'):
            bad = copy.deepcopy(receipt); expected = paths
            if defect == 'policy': bad['targetIdentityPolicy'] = 'other'
            if defect == 'incomplete': bad['scopeComplete'] = False
            if defect == 'missing': bad['targets'].pop()
            if defect == 'extra': bad['requiredTargets'].append('/projects/P/devices/Other')
            if defect == 'duplicate': bad['targets'][1]['targetPath'] = paths[0]
            if defect == 'unknownPath': bad['targets'][0]['targetPath'] = None
            if defect == 'legacy': del bad['targets'][0]['targetPath']
            if defect == 'wrongExpected': expected = ['/projects/Other/devices/Same', paths[1]]
            with self.subTest(defect=defect), self.assertRaises(RuntimeError): require_clean_compile(bad, expected_targets=expected)

    def test_uncompiled_devices_pass_only_when_named_by_the_caller(self):
        paths = ['/projects/P/devices/Station', '/projects/P/devices/Drive']
        receipt = device_compile(paths[:1]); receipt['requiredTargets'] = paths
        receipt['notCompiled'] = [{'target': 'Drive', 'targetPath': paths[1], 'reason': 'no compiler service'}]
        require_clean_compile(receipt, expected_targets=paths, not_compiled=[paths[1]])
        for defect in ('unnamed', 'otherNamed', 'alsoCompiled', 'notRequired', 'pathless', 'legacyScope'):
            bad = copy.deepcopy(receipt); allowed = [paths[1]]
            if defect == 'unnamed': allowed = []
            if defect == 'otherNamed': allowed = ['/projects/P/devices/Other']
            if defect == 'alsoCompiled': bad['targets'].append({**bad['targets'][0], 'targetPath': paths[1]})
            if defect == 'notRequired': bad['requiredTargets'] = paths[:1]
            if defect == 'pathless': bad['notCompiled'][0].pop('targetPath')
            if defect == 'legacyScope':
                bad = clean_compile(); bad['notCompiled'] = receipt['notCompiled']
            with self.subTest(defect=defect), self.assertRaises(RuntimeError): require_clean_compile(bad, expected_targets=paths, not_compiled=allowed)

    def test_inventory_pages_keep_empty_devices_and_exact_complete_scope(self):
        paths = ['/projects/P/devices/A', '/projects/P/ungrouped/B']
        first = device_page('P.ap21', paths); second = copy.deepcopy(first)
        first['rows'] = first['rows'][:1]; first['nextCursor'] = 'next'
        second['rows'] = second['rows'][1:]; second['offset'] = 1
        calls = []; replies = iter([first, second])
        def call(name, args): calls.append((name, args)); return next(replies)
        captured = capture_device_targets(call, 'P.ap21')
        self.assertEqual(paths, captured['targets']); self.assertEqual(paths, device_inventory_targets(captured['pages'], 'P.ap21'))
        self.assertEqual({'snapshotId': 'synthetic-inventory', 'cursor': 'next', 'pageSize': 200}, calls[1][1])
        for defect in ('owner', 'scope', 'warnings', 'filtered', 'offset', 'snapshot', 'rows', 'duplicatePath', 'kind', 'parent', 'count', 'unfinished'):
            pages = copy.deepcopy([first, second]); metadata = pages[0]['snapshot']
            if defect == 'owner': metadata['projectPath'] = 'Other'
            if defect == 'scope': metadata['coverage']['scopeComplete'] = False
            if defect == 'warnings': metadata['coverage']['warnings'] = ['missing device']
            if defect == 'filtered': metadata['coverage']['devicePath'] = paths[0]
            if defect == 'offset': pages[1]['offset'] = 0
            if defect == 'snapshot': pages[1]['snapshot']['snapshotId'] = 'different'
            if defect == 'rows': pages[1]['rows'] = []
            if defect == 'duplicatePath': pages[1]['rows'][0]['path'] = paths[0]
            if defect == 'kind': pages[1]['rows'][0]['kind'] = 'unknown'
            if defect == 'parent': pages[1]['rows'][0]['parentPath'] = paths[0]
            if defect == 'count': metadata['totalRows'] = True
            if defect == 'unfinished': pages[1]['nextCursor'] = 'next-again'
            with self.subTest(defect=defect), self.assertRaises(RuntimeError): device_inventory_targets(pages, 'P.ap21')

        for damaged in ([None], [{}], [{'snapshot': None}], [{**first, 'snapshot': {**first['snapshot'], 'coverage': None}}],
                        [{**device_page('P.ap21', paths), 'rows': [None]}]):
            with self.subTest(malformed=damaged), self.assertRaises(RuntimeError): device_inventory_targets(damaged, 'P.ap21')

    def test_inventory_repeated_cursor_stops_without_another_request(self):
        page = device_page('P.ap21', ['/projects/P/devices/A']); page['nextCursor'] = 'repeat'
        calls = []
        def call(name, args): calls.append(args); return page
        with self.assertRaisesRegex(RuntimeError, 'cursor repeated'): capture_device_targets(call, 'P.ap21')
        self.assertEqual(2, len(calls))

    def test_exact_targets_refuse_missing_extra_wrong_and_duplicate_successes(self):
        receipt = clean_compile('/selected/plc')
        require_clean_compile(receipt, expected_targets=['/selected/plc'])
        for targets in (['/other/plc'], [], ['/selected/plc', '/extra/plc'], ['/selected/plc', '/selected/plc']):
            bad = {'errorCount': 0, 'targets': [clean_compile(path)['targets'][0] for path in targets]}
            with self.subTest(targets=targets), self.assertRaises(RuntimeError):
                require_clean_compile(bad, expected_targets=['/selected/plc'])
        for expected in ([], ['/selected/plc', '/selected/plc'], [None], '/selected/plc'):
            with self.subTest(expected=expected), self.assertRaises(RuntimeError):
                require_clean_compile(receipt, expected_targets=expected)

    def test_flat_scratch_fields_and_named_envelope_both_remain_validated(self):
        flat = clean_compile('/selected/plc')['targets'][0]
        flat.pop('target')
        receipt = {**copy.deepcopy(flat), 'targets': [{**copy.deepcopy(flat), 'target': '/selected/plc'}]}
        require_clean_compile(receipt, expected_targets=['/selected/plc'])
        for location in ('flat', 'target'):
            bad = copy.deepcopy(receipt)
            row = bad if location == 'flat' else bad['targets'][0]
            row['messages'].append({'state': 'Error'})
            with self.subTest(location=location), self.assertRaises(RuntimeError): require_clean_compile(bad)

    def test_complete_success_information_and_warning_do_not_mutate_evidence(self):
        for state in ('Success', 'Information', 'Warning'):
            receipt = clean_compile(); receipt['targets'][0]['state'] = state
            receipt['targets'][0]['messages'][0]['state'] = state
            receipt['targets'][0]['messages'][0]['children'] = [{'state': state}]
            before = copy.deepcopy(receipt); require_clean_compile(receipt)
            self.assertEqual(before, receipt)

    def test_aggregate_and_target_counts_cannot_coerce_to_zero(self):
        for location in ('aggregate', 'target'):
            for value in (None, False, True, '0', 0.0, -1, 1):
                receipt = clean_compile(); row = receipt if location == 'aggregate' else receipt['targets'][0]
                row['errorCount'] = value
                with self.subTest(location=location, value=value), self.assertRaises(RuntimeError): require_clean_compile(receipt)
            del row['errorCount']
            with self.assertRaises(RuntimeError): require_clean_compile(receipt)

    def test_nonempty_named_targets_and_explicit_diagnostic_arrays_required(self):
        for value in (None, {}, [], [1], [{'errorCount': 0}]):
            receipt = clean_compile(); receipt['targets'] = value
            with self.subTest(value=value), self.assertRaises(RuntimeError): require_clean_compile(receipt)
        for value in (None, '', ' ', 0):
            receipt = clean_compile(); receipt['targets'][0]['target'] = value
            with self.subTest(identity=value), self.assertRaises(RuntimeError): require_clean_compile(receipt)
        for key, values in [('messages', (None, {}, [17], [{}])), ('messagesTruncated', (None, True, 'false', 0))]:
            for value in values:
                receipt = clean_compile(); receipt['targets'][0][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(RuntimeError): require_clean_compile(receipt)

    def test_unknown_or_error_state_cannot_hide_at_any_diagnostic_level(self):
        for value in (None, '', 'Error', 'Unknown', 0, []):
            for location in ('target', 'message', 'child'):
                receipt = clean_compile(); target = receipt['targets'][0]
                target['messages'][0]['children'] = [{'state': 'Information'}]
                row = target if location == 'target' else target['messages'][0] if location == 'message' else target['messages'][0]['children'][0]
                row['state'] = value
                with self.subTest(location=location, value=value), self.assertRaises(RuntimeError): require_clean_compile(receipt)

    def test_malformed_or_excessively_deep_children_refuse(self):
        for value in (None, {}, [1], [{}]):
            receipt = clean_compile(); receipt['targets'][0]['messages'][0]['children'] = value
            with self.subTest(value=value), self.assertRaises(RuntimeError): require_clean_compile(receipt)
        receipt = clean_compile(); node = receipt['targets'][0]['messages'][0]
        for _ in range(66):
            node['children'] = [{'state': 'Information'}]; node = node['children'][0]
        with self.assertRaises(RuntimeError): require_clean_compile(receipt)

    def test_every_target_and_all_original_nested_metadata_checks_remain_required(self):
        for defect in ({'error': 'getter failed'}, {'error': None}, {'errorCount': 1}, {'state': 'Error'}, {'messagesTruncated': True}):
            receipt = clean_compile(); receipt['targets'].append(copy.deepcopy(receipt['targets'][0]))
            receipt['targets'][1]['target'] = '/synthetic/other'
            receipt['targets'][1]['extraDiagnostics'] = defect
            with self.subTest(defect=defect), self.assertRaises(RuntimeError): require_clean_compile(receipt)
        for location in ('aggregate', 'target'):
            receipt = clean_compile(); row = receipt if location == 'aggregate' else receipt['targets'][0]
            row['error'] = 'native exception'
            with self.subTest(location=location), self.assertRaises(RuntimeError): require_clean_compile(receipt)


if __name__ == '__main__': unittest.main()
