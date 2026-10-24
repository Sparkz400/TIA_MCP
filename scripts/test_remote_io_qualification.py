"""Offline remote-I/O fixture placement checks; no native process is started."""
import copy
import unittest

from qualify_remote_io import channel_gate, qualification_complete, server_relocation, verify_engineering_compile


class ServerRelocationTests(unittest.TestCase):
    def setUp(self):
        rack = {'path': '/remote/rack', 'containerPath': None, 'positionNumber': 0}
        server = {'path': '/remote/server', 'containerPath': rack['path'], 'positionNumber': 1,
                  'name': 'Servermodul_1', 'typeIdentifier': 'OrderNumber:6ES7 193-6PA00-0AA0/V1.2',
                  'attributes': {'Name': 'Servermodul_1'}, 'addresses': [], 'channels': []}
        child = {'path': '/remote/server/diagnosis', 'containerPath': server['path'], 'positionNumber': 1,
                 'typeIdentifier': None, 'addresses': [{'ioType': 'Diagnosis', 'lengthBits': 0, 'startAddress': -1, 'owners': []}]}
        self.expected = [server, child]
        self.rows = copy.deepcopy([rack, server, child])

    def test_exact_server_moved_to_vacant_four_without_modifying_input(self):
        before = copy.deepcopy((self.rows, self.expected))
        self.assertEqual({'hardwarePath': '/remote/server', 'positionNumber': 4}, server_relocation(self.rows, self.expected))
        self.assertEqual(before, (self.rows, self.expected))

    def test_unknown_generated_metadata_never_becomes_an_expectation(self):
        self.rows[1]['unknown'] = 'collateral'
        with self.assertRaisesRegex(AssertionError, 'independent pre-assignment'):
            server_relocation(self.rows, self.expected)

    def test_diagnostic_child_drift_rejected(self):
        self.rows[2]['addresses'][0]['startAddress'] = 0
        with self.assertRaisesRegex(AssertionError, 'independent pre-assignment'):
            server_relocation(self.rows, self.expected)

    def test_target_and_future_di_slots_must_be_empty(self):
        for slot in (1, 3, 4):
            with self.subTest(slot=slot):
                rows = [*self.rows, {'path': '/remote/other', 'containerPath': '/remote/rack', 'positionNumber': slot}]
                with self.assertRaises(AssertionError):
                    server_relocation(rows, self.expected)

    def test_english_generated_server_name_accepted(self):
        for row in (self.rows[1], self.expected[0]):
            row.update(name='Server module_1', attributes={'Name': 'Server module_1'})
        self.assertEqual('/remote/server', server_relocation(self.rows, self.expected)['hardwarePath'])

    def test_duplicate_hardware_identity_rejected(self):
        with self.assertRaisesRegex(AssertionError, 'duplicate hardware'):
            server_relocation([*self.rows, copy.deepcopy(self.rows[1])], self.expected)

    def test_missing_rack_rejected(self):
        with self.assertRaisesRegex(AssertionError, 'rack is absent'):
            server_relocation(self.rows[1:], self.expected)

    def test_uncaptured_server_profile_rejected(self):
        for field, value in [('positionNumber', 2), ('typeIdentifier', 'other'), ('name', 'other')]:
            with self.subTest(field=field):
                expected = copy.deepcopy(self.expected)
                expected[0][field] = value
                with self.assertRaisesRegex(AssertionError, 'captured initial'):
                    server_relocation(self.rows, expected)


class ScopedLifecycleTests(unittest.TestCase):
    def observations(self):
        rows = [{'path': '/module/input', 'channels': [{'number': 0, 'type': 'DI', 'attributes': [
            {'name': 'InputDelay', 'accessMode': 'Read', 'value': 6, 'nativeEnumValues': [{'value': 0}, {'value': 6}]},
            {'name': 'ParameterSettings', 'accessMode': 'ReadWrite', 'value': 1, 'nativeEnumValues': [{'value': 2}]}]}]}]
        for row in rows[0]['channels'][0]['attributes']:
            row.update(readAccessKnown=True, readable=True, valueCaptured=True, valueComplete=True, valueStatus='captured')
        return rows

    def test_incomplete_capture_cannot_offer_writes_or_prove_unsupported(self):
        for field, value in [('valueComplete', False), ('valueCaptured', False), ('readAccessKnown', False),
                             ('readable', False), ('valueStatus', 'displayOnly'), ('metadataUnavailable', True)]:
            rows = self.observations(); attribute = rows[0]['channels'][0]['attributes'][0]
            attribute['accessMode'] = 'ReadWrite'; attribute[field] = value
            with self.subTest(field=field):
                self.assertIsNone(channel_gate(rows, 'full')[0])
                with self.assertRaisesRegex(AssertionError, 'incomplete'):
                    channel_gate(rows, 'configured-module-lifecycle')

    def test_readonly_reviewed_and_writable_unreviewed_attributes_never_enable_channel(self):
        rows = self.observations()
        for scope in ('full', 'configured-module-lifecycle'):
            choice, evidence = channel_gate(rows, scope)
            self.assertIsNone(choice)
            self.assertFalse(evidence['qualified'])
            self.assertEqual('unsupported_for_reviewed_channel_route', evidence['status'])
            self.assertEqual(rows[0]['channels'], evidence['nativeObservations'][0]['channels'])
        rows[0]['channels'][0]['attributes'][0]['value'] = 'changed later'
        self.assertEqual(6, evidence['nativeObservations'][0]['channels'][0]['attributes'][0]['value'])

    def test_missing_channel_observations_cannot_qualify_limited_scope(self):
        with self.assertRaisesRegex(AssertionError, 'absent evidence'):
            channel_gate([{'path': '/module'}], 'configured-module-lifecycle')

    def test_channel_metadata_errors_are_incomplete_not_unsupported(self):
        for mode in ('channelError', 'metadataUnavailable', 'attributeError', 'missingValue', 'missingAccess', 'emptyAttributes'):
            with self.subTest(mode=mode):
                rows = self.observations(); channel = rows[0]['channels'][0]
                if mode == 'channelError': channel['error'] = 'read failed'
                elif mode == 'metadataUnavailable': channel['metadataUnavailable'] = True
                elif mode == 'attributeError': channel['attributes'][0]['error'] = 'read failed'
                elif mode == 'missingValue': del channel['attributes'][0]['value']
                elif mode == 'missingAccess': del channel['attributes'][0]['accessMode']
                else: channel['attributes'] = []
                with self.assertRaisesRegex(AssertionError, 'incomplete'):
                    channel_gate(rows, 'configured-module-lifecycle')

    def test_eligible_reviewed_channel_requires_full_scope_and_distinct_value(self):
        rows = self.observations(); rows[0]['channels'][0]['attributes'][0]['accessMode'] = 'ReadWrite'
        choice, evidence = channel_gate(rows, 'full')
        self.assertEqual(0, choice[3]); self.assertFalse(evidence['qualified'])
        with self.assertRaisesRegex(AssertionError, 'use full scope'):
            channel_gate(rows, 'configured-module-lifecycle')
        rows[0]['channels'][0]['attributes'][0]['nativeEnumValues'] = [{'value': 6}]
        self.assertIsNone(channel_gate(rows, 'configured-module-lifecycle')[0])

    def test_lifecycle_requires_all_persistence_and_recovery_gates_without_full_promotion(self):
        gates = dict.fromkeys(('controllerAssignment', 'fixtureServerRelocation', 'moduleCreation', 'slotMove', 'addressChange',
                              'freshProcessPersistence', 'archiveRecovery', 'disconnectOutcomeAndRecovery', 'recoveredFreshProcessPersistence'), True)
        self.assertTrue(qualification_complete('configured-module-lifecycle', gates))
        self.assertFalse(qualification_complete('full', gates))
        for key in gates:
            with self.subTest(missing=key):
                self.assertFalse(qualification_complete('configured-module-lifecycle', {k: v for k, v in gates.items() if k != key}))
        self.assertFalse(qualification_complete('configured-module-lifecycle', {**gates, 'channelChange': True}))
        self.assertTrue(qualification_complete('full', {**gates, 'channelChange': True}))


class NativeCompileDiagnosticTests(unittest.TestCase):
    def compilation(self):
        return {'errorCount': 0, 'targets': [
            {'target': name, 'errorCount': 0, 'messagesTruncated': False, 'state': 'Warning',
             'messages': [{'state': 'Warning', 'description': 'Native warning'}]}
            for name in ('ScratchStation', 'TiaMcpRemoteIo')]}

    def test_complete_two_device_warning_compile_is_accepted(self):
        verify_engineering_compile(self.compilation())

    def test_applied_state_cannot_hide_error_or_incomplete_diagnostics(self):
        for mode in ('errorMessage', 'errorState', 'truncated', 'missingMessages', 'missingTarget', 'wrongTarget', 'wrongTotal', 'unknownState'):
            with self.subTest(mode=mode):
                value = self.compilation(); target = value['targets'][0]
                if mode == 'errorMessage': target['messages'][0]['state'] = 'Error'
                elif mode == 'errorState': target['state'] = 'Error'
                elif mode == 'truncated': target['messagesTruncated'] = True
                elif mode == 'missingMessages': target['messages'] = []
                elif mode == 'missingTarget': value['targets'].pop()
                elif mode == 'wrongTarget': target['target'] = 'Other'
                elif mode == 'wrongTotal': value['errorCount'] = 1
                else: target['messages'][0]['state'] = 'Unknown'
                with self.assertRaises(AssertionError): verify_engineering_compile(value)

    def test_negative_compile_requires_real_errors_and_complete_diagnostics(self):
        value = self.compilation()
        with self.assertRaisesRegex(AssertionError, 'lacks actual'):
            verify_engineering_compile(value, expect_failure=True)
        value['errorCount'] = value['targets'][0]['errorCount'] = 1
        value['targets'][0]['state'] = value['targets'][0]['messages'][0]['state'] = 'Error'
        verify_engineering_compile(value, expect_failure=True)
        value['targets'][1]['messagesTruncated'] = True
        with self.assertRaisesRegex(AssertionError, 'incomplete'):
            verify_engineering_compile(value, expect_failure=True)


if __name__ == '__main__':
    unittest.main()
