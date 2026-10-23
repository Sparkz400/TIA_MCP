"""Portable runtime checks for native-evidence guards; no Siemens process starts."""
import hashlib
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import audit_encoder_preservation as encoder_audit
import audit_engineering_preservation as engineering_audit
import verify_encoder_preservation as verifier
import resume_typed_graphical_qualification as resume
from engineering_preservation import native_payload_hash
from smoke_mcp import ROOT
from test_engineering_preservation import ENCODER
from test_compilation_evidence import clean_compile


class NativeEvidenceGuardTests(unittest.TestCase):
    def setUp(self):
        (ROOT / 'artifacts').mkdir(exist_ok=True)
        self.folder = tempfile.TemporaryDirectory(prefix='encoder-guards-', dir=ROOT / 'artifacts')
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)

    def artifact(self, name, content):
        data = content.encode('utf-8')
        path = self.root / name
        path.write_bytes(data)
        return {'path': str(path), 'sha256': hashlib.sha256(data).hexdigest()}

    def source(self):
        changed = ENCODER.replace('<Member Name="InverseDirection" Datatype="Bool"/>',
            '<Member Name="InverseDirection" Datatype="Bool"><StartValue>true</StartValue></Member>')
        before = {'xml': ENCODER, 'artifact': self.artifact('before.xml', ENCODER), 'hardwareConnections': []}
        after = {'xml': changed, 'artifact': self.artifact('after.xml', changed), 'hardwareConnections': []}
        old = {'value': False, 'type': 'System.Boolean'}
        archive = self.artifact('checkpoint.zap21', 'synthetic archive bytes')
        return {'completed': False, 'nativeBefore': before,
            'parameterDifferences': {'Sensor.InverseDirection': {'before': old, 'after': {**old, 'value': True}}},
            'profile': {'parameterEdit': {'Sensor.InverseDirection': True}},
            'edit': {'nativeAfter': after, 'applied': {'state': 'applied', 'compile': clean_compile(), 'stages': ['changed_project_saved']}},
            'recovery': {'nativeRecovered': before, 'compile': clean_compile(),
                'restored': {'state': 'restored', 'recoveryArchive': archive['path'], 'recoveryArchiveSha256': archive['sha256']}}}

    def test_retained_artifact_checks_hash_and_resolved_boundary(self):
        value = self.artifact('data.bin', 'retained')
        self.assertEqual(b'retained', engineering_audit.verified_artifact(value))
        with self.assertRaisesRegex(AssertionError, 'hash mismatch'):
            engineering_audit.verified_artifact({**value, 'sha256': '0' * 64})
        # The existing source file is outside artifacts; it must be refused before reading.
        with self.assertRaisesRegex(AssertionError, 'escaped'):
            engineering_audit.verified_artifact({'path': str(Path(__file__).resolve()), 'sha256': '0' * 64})

    def test_report_xml_cannot_substitute_for_retained_bytes(self):
        value = {'artifact': self.artifact('native.xml', ENCODER), 'xml': ENCODER + ' '}
        with self.assertRaisesRegex(AssertionError, 'Report XML differs'):
            encoder_audit.native_export(value)

    def test_exact_synthetic_edit_audits_without_claiming_saved_copies(self):
        result = {}
        encoder_audit.audit(self.source(), result)
        self.assertTrue(result['exactEdit']['passed'])
        self.assertTrue(result['recovery']['passed'])
        self.assertTrue(result['compilationEvidenceVerified'])
        self.assertEqual([], result['savedReopens'])
        self.assertEqual('not available in this source report', result['saveReopenCoverage'])

    def test_independent_audit_refuses_bad_edit_compile_recovery_and_hash(self):
        for defect in ('peer', 'profile', 'compile', 'saved', 'connection', 'restore', 'archive', 'recoveryCompile'):
            source = self.source()
            if defect == 'peer':
                source['parameterDifferences']['Other'] = {}
            elif defect == 'profile': source['profile']['parameterEdit'] = {'Sensor.InverseDirection': False}
            elif defect == 'compile': source['edit']['applied']['compile']['errorCount'] = 1
            elif defect == 'saved': source['edit']['applied']['stages'] = []
            elif defect == 'connection': source['edit']['nativeAfter']['hardwareConnections'] = ['changed']
            elif defect == 'restore': source['recovery']['restored']['state'] = 'recovery_required'
            elif defect == 'archive': source['recovery']['restored']['recoveryArchiveSha256'] = '0' * 64
            else: source['recovery']['compile']['errorCount'] = 1
            with self.subTest(defect=defect), self.assertRaises(RuntimeError if defect in ('compile', 'recoveryCompile') else AssertionError):
                encoder_audit.audit(source, {})

    def test_matching_digest_does_not_hide_collateral_native_xml_change(self):
        source = self.source()
        row = source['edit']['nativeAfter']
        row['xml'] = row['xml'].replace('<StartValue>7', '<StartValue>8')
        row['artifact'] = self.artifact('collateral.xml', row['xml'])
        with self.assertRaisesRegex(AssertionError, 'beyond the selected'):
            encoder_audit.audit(source, {})

    def test_document_annotations_are_checked_in_edit_recovery_and_saved_copies(self):
        for phase in ('edit', 'recovery', 'savedEdit', 'savedRecovery'):
            source = self.source()
            for name, row in (('before', source['nativeBefore']), ('after', source['edit']['nativeAfter'])):
                row['xml'] = '<?native keep?><!--before-->' + row['xml'] + '<!--after-->'
                row['artifact'] = self.artifact(name + '.xml', row['xml'])
            source['recovery']['nativeRecovered'] = copy.deepcopy(source['nativeBefore'])
            source['recovery']['reopen'] = {'nativeReopened': copy.deepcopy(source['nativeBefore'])}
            source['saveReopen'] = {'nativeReopened': copy.deepcopy(source['edit']['nativeAfter'])}
            result = {}
            encoder_audit.audit(source, result)
            self.assertEqual('both copies verified', result['saveReopenCoverage'])
            row = {'edit': source['edit']['nativeAfter'], 'recovery': source['recovery']['nativeRecovered'],
                   'savedEdit': source['saveReopen']['nativeReopened'],
                   'savedRecovery': source['recovery']['reopen']['nativeReopened']}[phase]
            row['xml'] = row['xml'].replace('<!--after-->', '<!--lost-->')
            row['artifact'] = self.artifact('changed-' + phase + '.xml', row['xml'])
            originals = {p: p.read_bytes() for p in self.root.iterdir()}
            with self.subTest(phase=phase), self.assertRaisesRegex(AssertionError, 'beyond the selected|payload differs|changed on reopen'):
                encoder_audit.audit(source, {})
            self.assertEqual(originals, {p: p.read_bytes() for p in self.root.iterdir()})

    def test_resume_recomputes_complete_document_from_original_strings(self):
        xml = '<?native keep?><!--before-->' + ENCODER + '<!--after-->'
        def read(value):
            return {'tool': 'block_read', 'result': {'block': {'name': 'Encoder'}, 'xml': value, 'payloadSha256': 'legacy'}}
        report = {'prepared': {'changeId': 'one'}, 'calls': [read(xml),
            {'tool': 'change_prepare', 'result': {'changeId': 'one'}},
            {'tool': 'change_apply', 'arguments': {'changeId': 'one'}}, read(xml)]}
        original = copy.deepcopy(report)
        expected, _ = resume.original_snapshots(report)
        self.assertEqual(native_payload_hash(xml), expected['Encoder']['payloadSha256'])
        for changed in (xml.replace('<!--after-->', ''), xml.replace('native keep', 'native lost'),
                        xml.replace('<!--keep-->', ''), xml.replace('ID="3"', 'ID="4"')):
            current = resume.snapshot(lambda *args: {'xml': changed}, ['Encoder'], self.root / ('capture-' + str(len(list(self.root.iterdir())))))
            with self.assertRaisesRegex(AssertionError, 'Complete native payload changed'):
                resume.assert_snapshot(expected, current)
        with self.assertRaisesRegex(AssertionError, 'Export coverage differs'):
            resume.assert_snapshot(expected, {})
        self.assertEqual(original, report)

    def test_failed_or_wrong_source_companion_cannot_qualify(self):
        for companion in ({'completed': False, 'readOnly': True},
                          {'completed': True, 'readOnly': True, 'sourceReport': {'sha256': 'wrong'}}):
            with self.assertRaises(AssertionError):
                encoder_audit.audit_companion({}, companion, {'sourceReportSha256': 'expected'})

    def test_empty_or_failed_general_audit_is_not_success(self):
        for completed in (False, True):
            path = self.root / 'source.json'
            path.write_text(json.dumps({'completed': completed, 'steps': []}), encoding='utf-8')
            result = {}
            with self.assertRaises(AssertionError): engineering_audit.audit_report(path, result)
            self.assertFalse(result['passed'])

    def test_failed_retained_compile_stops_before_verifier_host_start(self):
        path = self.root / 'failed.json'
        path.write_text(json.dumps({'profile': {}, 'seederConnect': {'completed': True, 'compile': {'errorCount': 1}}}), encoding='utf-8')
        with patch.object(verifier, 'new_session') as factory, patch('sys.argv', ['verify', '--evidence', str(path),
                '--evidence-sha256', hashlib.sha256(path.read_bytes()).hexdigest(),
                '--host', 'unavailable.exe', '--worker', 'unavailable.exe', '--report', str(self.root / 'result.json')]):
            with self.assertRaisesRegex(AssertionError, 'Exact cleanup evidence'): verifier.main()
            factory.assert_not_called()


if __name__ == '__main__':
    unittest.main()
