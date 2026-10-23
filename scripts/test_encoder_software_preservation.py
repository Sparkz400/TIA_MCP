"""Real encoder orchestration with populated synthetic peer inventories/exports."""
import copy
import hashlib
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import encoder_software_preservation as peers


def technology(plc, name):
    return {'path': plc + '/technology/' + name, 'ownerPath': plc + '/technology',
            'composition': 'TechnologicalObjects', 'name': name,
            'systemLibraryType': 'TO_ExternalEncoder', 'systemLibraryVersion': '10.0'}


def inventory(project, pid, plc, selected):
    def identity(path, name, kind):
        return {'projectPath': project['path'], 'plcPath': plc, 'scopePath': plc,
                'objectPath': path, 'kind': kind, 'name': name, 'namespace': None}
    scope = {'path': plc, 'name': 'PLC', 'kind': 'plc', 'namespacePreset': None,
             'identity': identity(plc, 'PLC', 'plc'),
             'groups': [plc + '/blocks', plc + '/blocks/groups/Empty', plc + '/types', plc + '/tags',
                        plc + '/technology', plc + '/technology/groups/Empty']}
    blocks = [{'path': plc + '/blocks/' + name, 'name': selected['name'] if name == 'Peer' else name,
               'scopePath': plc, 'plcPath': plc, 'number': index, 'kind': 'DB', 'programmingLanguage': 'DB',
               'isConsistent': True, 'isKnowHowProtected': False,
               'technologyPath': selected['path'] if name == 'Mapped' else None}
              for index, name in enumerate(('Mapped', 'Peer'), 1)]
    for row in blocks: row['identity'] = identity(row['path'], row['name'], 'block')
    return {'project': copy.deepcopy(project), 'processId': pid, 'plcPath': plc,
            'softwareScopes': [scope], 'blocks': blocks,
            'types': [{'path': plc + '/types/PeerType', 'scopePath': plc, 'name': 'PeerType',
                       'isConsistent': True, 'isKnowHowProtected': False}],
            'tagTables': [{'path': plc + '/tags/PeerTags', 'scopePath': plc, 'name': 'PeerTags', 'tags': [], 'constants': []}],
            'technologyObjects': [copy.deepcopy(selected), technology(plc, 'PeerTechnology')],
            'technologyBlockMembership': {'basis': 'nativeObjectEquality', 'scopeComplete': True, 'atomicSnapshot': False,
                                         'technologyObjects': 2, 'blockObjects': 2, 'mappedBlocks': 1},
            'warnings': [], 'completeness': {'scopeComplete': True, 'truncated': False}}


class SoftwarePeerTests(unittest.TestCase):
    def harness(self):
        import test_encoder_lifecycle
        case = test_encoder_lifecycle.EncoderLifecycleTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        return case

    def test_populated_peers_survive_all_phases_and_independent_copies(self):
        case = self.harness()
        source = case.run_qualifier()
        self.assertTrue(source['completed'])
        original = source['nativeBefore']
        self.assertEqual(5, len(original['softwarePeers']['exports']))
        self.assertNotEqual(original['project']['path'], source['recovery']['nativeRecovered']['project']['path'])
        companion = case.run_companion()
        result = case.audit_companion(companion)
        self.assertTrue(companion['completed'])

    def test_changed_peer_exports_refuse_and_restore(self):
        for family in ('blocks', 'types', 'tagTables', 'technologyObjects'):
            with self.subTest(family=family):
                case = self.harness()
                case.peer_fault = family
                with self.assertRaisesRegex(AssertionError, 'Unrelated native software XML changed'):
                    case.run_qualifier()
                self.assertTrue(any(name == 'change_restore' for _, name, _ in case.calls))

    def test_changed_empty_groups_refuse(self):
        case = self.harness()
        original = inventory
        def changed(*args):
            value = original(*args)
            if case.current['Sensor.InverseDirection']['value']:
                value['softwareScopes'][0]['groups'].append(args[2] + '/blocks/groups/AddedEmpty')
            return value
        with patch(__name__ + '.inventory', side_effect=changed), self.assertRaisesRegex(AssertionError, 'inventory, metadata or empty groups changed'):
            case.run_qualifier()

    def test_missing_foreign_truncated_or_ambiguous_inventory_refuses_before_edit(self):
        for defect in ('foreign', 'truncated', 'missingGroups', 'duplicate', 'nameMapping'):
            with self.subTest(defect=defect):
                case = self.harness()
                original = inventory
                def changed(*args):
                    value = original(*args)
                    if defect == 'foreign': value['processId'] += 1
                    elif defect == 'truncated': value['completeness']['truncated'] = True
                    elif defect == 'missingGroups': del value['softwareScopes'][0]['groups']
                    elif defect == 'duplicate': value['types'].append(copy.deepcopy(value['types'][0]))
                    else: value['technologyBlockMembership']['basis'] = 'name'
                    return value
                with patch(__name__ + '.inventory', side_effect=changed), self.assertRaises(AssertionError):
                    case.run_qualifier()
                self.assertFalse(any(name == 'change_prepare' and args.get('kind') == 'to_parameters' for _, name, args in case.calls))

    def test_original_call_and_summary_tampering_refuses(self):
        case = self.harness()
        source = case.run_qualifier()
        for defect in ('filter', 'summary', 'bytes', 'interrupted', 'missing'):
            with self.subTest(defect=defect):
                value = copy.deepcopy(source['nativeBefore'])
                calls = copy.deepcopy(source['sessions'][0]['calls'])
                index = next(i for i, row in enumerate(calls) if row['tool'] == 'software_inspect')
                if defect == 'filter': calls[index]['arguments']['scopePath'] = value['plcPath']
                elif defect == 'summary': value['softwarePeers']['inventory']['types'][0]['name'] = 'substitute'
                elif defect == 'bytes': calls[index + 2]['result']['data'] = 'Zm9yZWlnbg=='
                elif defect == 'interrupted': calls.insert(index, {'tool': 'change_apply', 'status': 'passed', 'arguments': {}, 'result': {}})
                else: value['softwarePeers']['exports'].pop()
                with self.assertRaises(AssertionError): peers.require_calls(value, calls)

    def test_added_removed_and_renumbered_objects_refuse(self):
        for defect in ('added', 'removed', 'number', 'foreignIdentity'):
            with self.subTest(defect=defect):
                case = self.harness()
                original = inventory
                def changed(*args):
                    value = original(*args)
                    if case.current['Sensor.InverseDirection']['value']:
                        if defect == 'removed': value['types'] = []
                        elif defect == 'number': value['blocks'][1]['number'] = 55
                        elif defect == 'foreignIdentity': value['blocks'][1]['identity']['projectPath'] = 'foreign.ap21'
                        else:
                            row = copy.deepcopy(value['types'][0]); row['name'] = 'Added'; row['path'] += 'Added'
                            value['types'].append(row)
                    return value
                with patch(__name__ + '.inventory', side_effect=changed), self.assertRaises(AssertionError):
                    case.run_qualifier()

    def test_peer_export_byte_drift_blocks_companion_before_startup(self):
        case = self.harness()
        source = case.run_qualifier()
        peer = source['nativeBefore']['softwarePeers']['exports'][1]
        Path(peer['read']['artifact']['path']).write_bytes(b'changed peer export')
        serial = case.serial
        with self.assertRaises(AssertionError): case.run_companion()
        self.assertEqual(serial, case.serial)

    def test_legacy_source_remains_diagnostic_but_cannot_open_new_copies(self):
        import audit_encoder_preservation as auditor
        case = self.harness()
        source = case.run_qualifier()
        del source['softwarePeerPolicy']
        del source['hardwarePeerPolicy']
        case.report_path.write_text(json.dumps(source), encoding='utf-8')
        result = {}; auditor.audit(source, result)
        self.assertFalse(result['softwarePeersVerified'])
        serial = case.serial
        with self.assertRaisesRegex(AssertionError, 'Current software peer evidence'): case.run_companion()
        self.assertEqual(serial, case.serial)

    def test_profile_outside_exact_edit_refuses_before_parameter_dispatch(self):
        case = self.harness()
        case.profile['parameterEdit'] = {'Sensor.Type': 1}
        case.configuration.write_text(json.dumps(case.profile), encoding='utf-8')
        with self.assertRaises(AssertionError): case.run_qualifier()
        self.assertFalse(any(name == 'change_prepare' and args.get('kind') == 'to_parameters' for _, name, args in case.calls))

        import qualify_encoder_hardware as qualifier
        import audit_encoder_preservation as auditor
        valid = self.harness().run_qualifier()
        for invalid in (1, 1.0, 'true'):
            damaged = copy.deepcopy(valid)
            damaged['profile']['parameterEdit']['Sensor.InverseDirection'] = invalid
            with self.subTest(profile=invalid):
                with self.assertRaises(AssertionError): qualifier.require_source_compiles(damaged)
                with self.assertRaises(AssertionError): auditor.audit(damaged, {})

    def test_complete_byte_flag_cannot_be_a_truthy_surrogate(self):
        import base64
        from engineering_preservation import decode_native_artifact
        digest = hashlib.sha256(b'<Document/>').hexdigest()
        for complete in (False, 1, 'true', None):
            with self.subTest(complete=complete), self.assertRaises(AssertionError):
                decode_native_artifact({'artifact': {'sha256': digest}},
                                       {'sha256': digest, 'complete': complete, 'data': base64.b64encode(b'<Document/>').decode()})


if __name__ == '__main__':
    unittest.main()
