"""Pure tests of native equality and saved/recovered reopen qualification."""
import pathlib
import unittest

from engineering_preservation import native_payload_hash, recover_and_reopen, reopen_owned_project
from audit_encoder_preservation import expected_encoder_edit
from smoke_mcp import ROOT


BASELINE = '<Document><DocumentInfo>export 1</DocumentInfo><SW.Blocks.FC ID="3"><AttributeList><Name>Caller</Name><Text> keep </Text></AttributeList><!--keep--><Network UId="4"/></SW.Blocks.FC></Document>'
CHANGED = BASELINE.replace('UId="4"', 'UId="5"')
SUPPORT = '<Document><SW.Blocks.FB ID="8"><Name>Callee</Name></SW.Blocks.FB></Document>'
ENCODER = '<Document><SW.TechnologicalObjects.TechnologicalInstanceDB ID="3"><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static"><Member Name="Sensor"><Sections><Section Name="None"><Member Name="InverseDirection" Datatype="Bool"/><Member Name="Unrelated" Datatype="Int"><StartValue>7</StartValue></Member></Section></Sections></Member></Section></Sections></Interface></AttributeList><!--keep--></SW.TechnologicalObjects.TechnologicalInstanceDB></Document>'


class FixtureSession:
    def __init__(self, corrupt_applied=False, restore_state='restored'):
        self.original = str(ROOT / 'artifacts/preservation-fixture/TiaMcpScratch_Test.ap21')
        self.recovered = str(ROOT / 'artifacts/recovery/TiaMcpScratch_Test.ap21')
        self.path = self.original
        self.calls = []
        self.process = 100
        self.corrupt_applied = corrupt_applied
        self.restore_state = restore_state

    def call(self, name, arguments=None):
        arguments = arguments or {}
        self.calls.append((name, arguments))
        if name == 'change_restore':
            self.path = self.recovered
            return {'state': self.restore_state, 'recoveredProject': {'path': self.recovered},
                    'recoveryArchive': 'before.zap21', 'recoveryArchiveSha256': 'a' * 64}
        if name == 'block_read':
            xml = SUPPORT if arguments['blockName'] == 'Callee' else BASELINE
            if self.path == self.original and arguments['blockName'] == 'Caller':
                xml = BASELINE if self.corrupt_applied else CHANGED
            return {'xml': xml, 'artifact': {'path': 'native.xml', 'sha256': 'b' * 64},
                    'block': {'name': arguments['blockName']}}
        if name == 'tia_detach':
            self.path = None
            return {'disposedOwnedPortalConnection': True}
        if name == 'tia_start':
            self.process += 1
            return {'ownsPortalProcess': True, 'projects': [], 'processId': self.process}
        if name == 'project_open':
            self.path = arguments['path']
            return {'opened': True, 'project': {'path': self.path}}
        raise AssertionError('Unexpected tool: ' + name)

    def inspect(self):
        return {'operand': 'Alternate' if self.path == self.original else 'A'}, '<native/>'

    def run(self, evidence):
        evidence['processIdAtApply'] = self.process
        prepared = {'changeId': 'fixture', 'previewHash': 'preview', 'preview': {'project': {'path': self.original}}}
        recover_and_reopen(self.call, prepared, {'Caller': BASELINE, 'Callee': SUPPORT},
                           {'Caller': CHANGED, 'Callee': SUPPORT}, evidence, lambda: None,
                           self.inspect, {'operand': 'A'}, {'operand': 'Alternate'})


class NativePreservationTests(unittest.TestCase):
    def test_encoder_expected_edit_changes_only_exact_bool_start_value(self):
        expected = ENCODER.replace('<Member Name="InverseDirection" Datatype="Bool"/>',
                                   '<Member Name="InverseDirection" Datatype="Bool"><StartValue>true</StartValue></Member>')
        self.assertEqual(native_payload_hash(expected), native_payload_hash(expected_encoder_edit(ENCODER)))
        self.assertNotEqual(native_payload_hash(expected), native_payload_hash(expected.replace('<StartValue>7', '<StartValue>8')))

    def test_encoder_expected_edit_rejects_missing_ambiguous_or_nonempty_member(self):
        member = '<Member Name="InverseDirection" Datatype="Bool"/>'
        for invalid in (ENCODER.replace(member, ''), ENCODER.replace(member, member + member),
                        ENCODER.replace('Datatype="Bool"', 'Datatype="Int"'),
                        ENCODER.replace(member, member[:-2] + '><StartValue>false</StartValue></Member>')):
            with self.assertRaises(AssertionError):
                expected_encoder_edit(invalid)

    def test_provenance_and_serializer_indentation_do_not_change_payload(self):
        formatted = BASELINE.replace('export 1', 'export 2').replace('><', '>\n  <')
        self.assertEqual(native_payload_hash(BASELINE), native_payload_hash(formatted))

    def test_comments_ids_and_meaningful_whitespace_are_preserved(self):
        for changed in (BASELINE.replace('<!--keep-->', ''), CHANGED,
                        BASELINE.replace(' keep ', 'keep')):
            self.assertNotEqual(native_payload_hash(BASELINE), native_payload_hash(changed))

    def test_extension_document_info_is_native_payload(self):
        original = '<Document xmlns:e="urn:extension"><e:DocumentInfo>keep</e:DocumentInfo><Block/></Document>'
        self.assertNotEqual(native_payload_hash(original), native_payload_hash(original.replace('keep', 'lost')))

    def test_xml_space_and_processing_instructions_are_preserved(self):
        original = '<Document><Space xml:space="preserve"> <Child/> </Space><?native keep?></Document>'
        self.assertNotEqual(native_payload_hash(original), native_payload_hash(original.replace('> <', '><')))
        self.assertNotEqual(native_payload_hash(original), native_payload_hash(original.replace('keep?', 'lost?')))

    def test_document_annotations_preserve_content_position_order_and_count(self):
        original = '<?native keep?><!--before-->' + BASELINE + '<!--after--><?tail keep?>'
        for changed in (original.replace('native keep', 'native lost'), original.replace('native keep', 'other keep'),
                        original.replace('before', 'lost'), original.replace('after', 'lost'),
                        original.replace('tail keep', 'tail lost'), original.replace('<!--before-->', ''),
                        original + '<!--after-->', original.replace('<!--before-->', '') + '<!--before-->',
                        original.replace('<?native keep?><!--before-->', '<!--before--><?native keep?>')):
            with self.subTest(changed=changed):
                self.assertNotEqual(native_payload_hash(original), native_payload_hash(changed))

    def test_document_formatting_and_xml_declaration_do_not_hide_annotations(self):
        original = '<?native keep?><!--before-->' + BASELINE + '<!--after-->'
        formatted = '<?xml version="1.0" encoding="utf-8"?>\n' + original.replace('><', '>\n  <').replace('export 1', 'export 2')
        self.assertEqual(native_payload_hash(original), native_payload_hash(formatted))
        self.assertNotEqual(native_payload_hash(BASELINE), native_payload_hash(original))

    def test_qname_bindings_and_unknown_namespace_payload_are_preserved(self):
        original = '<Document xmlns:x="urn:one"><Data Kind="x:Token"/></Document>'
        self.assertNotEqual(native_payload_hash(original), native_payload_hash(original.replace('urn:one', 'urn:two')))
        original = '<Document xmlns:x="urn:one"><x:Data x:Kind="token"/></Document>'
        self.assertEqual(native_payload_hash(original), native_payload_hash(original.replace('xmlns:x', 'xmlns:y').replace('x:', 'y:')))

    def test_entity_declarations_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'DTD/entity'):
            native_payload_hash('<!DOCTYPE Document [<!ENTITY x "value">]><Document>&x;</Document>')

    def test_recovery_precedes_fresh_reopens_and_every_dependency_is_checked(self):
        session, evidence = FixtureSession(), {}
        session.run(evidence)
        self.assertEqual(session.calls[0][0], 'change_restore')
        self.assertEqual([row[1]['path'] for row in session.calls if row[0] == 'project_open'],
                         [session.original, session.recovered])
        self.assertEqual([row[0] for row in session.calls].count('block_read'), 6)
        self.assertNotEqual(evidence['appliedReopen']['processId'], evidence['recoveredReopen']['processId'])
        self.assertTrue(evidence['savedAppliedEqualityPassed'])
        self.assertTrue(evidence['savedRecoveredEqualityPassed'])
        self.assertEqual(session.path, session.recovered)
        for phase in ('recoveredReadback', 'appliedReopen', 'recoveredReopen'):
            self.assertEqual(set(evidence[phase]['blocks']), {'Caller', 'Callee'})
            self.assertTrue(all(row['passed'] for row in evidence[phase]['blocks'].values()))

    def test_failed_saved_edit_comparison_retains_evidence_and_reopens_recovery(self):
        session, evidence = FixtureSession(corrupt_applied=True), {}
        with self.assertRaisesRegex(AssertionError, 'Native payload differs for Caller'):
            session.run(evidence)
        self.assertFalse(evidence['appliedReopen']['blocks']['Caller']['passed'])
        self.assertNotIn('savedAppliedEqualityPassed', evidence)
        self.assertIn('Native payload differs', evidence['appliedReopenFailure'])
        self.assertTrue(evidence['savedRecoveredEqualityPassed'])
        self.assertEqual(session.path, session.recovered)

    def test_failed_recovery_does_not_reopen_or_claim_equality(self):
        session, evidence = FixtureSession(restore_state='recovery_required'), {}
        with self.assertRaises(AssertionError):
            session.run(evidence)
        self.assertEqual([row[0] for row in session.calls], ['change_restore'])
        self.assertEqual(evidence['recovery']['state'], 'recovery_required')
        self.assertNotIn('recoveredNativeEqualityPassed', evidence)

    def test_unknown_startup_failure_is_not_retried(self):
        session, evidence = FixtureSession(), {}
        original_call = session.call

        def call(name, arguments=None):
            if name == 'tia_start':
                session.calls.append((name, arguments or {}))
                raise RuntimeError('TimeoutOutcomeUnknown')
            return original_call(name, arguments)

        session.call = call
        with self.assertRaisesRegex(RuntimeError, 'TimeoutOutcomeUnknown'):
            session.run(evidence)
        self.assertEqual([row[0] for row in session.calls].count('tia_start'), 1)
        self.assertIn('TimeoutOutcomeUnknown', evidence['appliedReopenFailure'])
        self.assertTrue(evidence['recoveredNativeEqualityPassed'])
        self.assertNotIn('savedRecoveredEqualityPassed', evidence)

    def test_reopen_refuses_paths_outside_owned_fixture_directory(self):
        session = FixtureSession()
        with self.assertRaises(AssertionError):
            reopen_owned_project(session.call, ROOT / 'TiaMcpScratch_Unexpected.ap21')
        self.assertEqual(session.calls, [])


if __name__ == '__main__':
    unittest.main(verbosity=2)
