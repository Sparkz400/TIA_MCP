"""Independent intake contract checks; no source observations become native proof."""
import base64
import hashlib
import tempfile
from unittest.mock import patch
import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator, ValidationError

ROOT = Path(__file__).resolve().parents[1]


class ReviewImportContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema = json.loads((ROOT / 'contracts/project-review-import.v1.schema.json').read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(cls.schema)

    def test_exact_committed_upload_input(self):
        validator = Draft202012Validator(dict(self.schema, oneOf=[{'$ref': '#/$defs/input'}]))
        validator.validate({'uploadRef': 'upload:' + 'a' * 32})
        for value in [{}, {'uploadRef': '../review.json'}, {'uploadRef': 'upload:'+'a'*32, 'nativeQualified': True}]:
            with self.assertRaises(ValidationError): validator.validate(value)

    def test_intake_never_certifies_source_or_native_qualification(self):
        receipt = dict(reviewId='a'*64, artifact=dict(path='managed/review.json', sha256='a'*64, bytes=100),
                       nodeCount=2, documentCount=1, nativeAccessAttempted=False, sourceAuthenticityVerified=False,
                       nativeQualified=False, wholeProjectContentComplete=False, sourceEvidencePolicy='Supplied observations only.')
        validator = Draft202012Validator(self.schema)
        validator.validate(receipt)
        for field in ['nativeAccessAttempted', 'sourceAuthenticityVerified', 'nativeQualified', 'wholeProjectContentComplete']:
            changed = copy.deepcopy(receipt); changed[field] = True
            with self.assertRaises(ValidationError): validator.validate(changed)
        for field, value in [('nodeCount', 50001), ('documentCount', 201), ('reviewId', '../outside')]:
            changed = copy.deepcopy(receipt); changed[field] = value
            with self.assertRaises(ValidationError): validator.validate(changed)

    def test_failed_shutdown_still_retains_original_failure_receipt(self):
        import qualify_trace_diagnostics_offline as harness
        class FailedHost:
            def request(self, *args): raise RuntimeError('initialization failed')
            def close(self): return dict(hostExitCode=5, forcedHostStop=True, workerExits=[])
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); host=root/'TiaMcp.Host.exe'; host.write_bytes(b'fixture')
            for name in ['TiaMcp.Host.dll','TiaMcp.Core.dll']: (root/name).write_bytes(b'fixture')
            raw=b'{}'; samples=root/'samples.json'
            samples.write_text(json.dumps(dict(reviewArtifactBase64=base64.b64encode(raw).decode(),
                              diagnosticRequest=dict(reviewId=hashlib.sha256(raw).hexdigest()))),encoding='utf-8')
            with patch.object(harness, 'Client', return_value=FailedHost()):
                with self.assertRaisesRegex(RuntimeError, 'initialization failed'):
                    harness.run(host, root/'result', samples)
            receipt=json.loads((root/'result/result.json').read_text(encoding='utf-8'))
            self.assertEqual('failed',receipt['status'])
            self.assertEqual('initialization failed',receipt['error'])
            self.assertEqual(5,receipt['hostExits'][0]['hostExitCode'])
            self.assertTrue(receipt['shutdownError'])

    def test_invalid_schema_does_not_start_an_unmanaged_host(self):
        import qualify_trace_diagnostics_offline as harness
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); host=root/'TiaMcp.Host.exe'; host.write_bytes(b'fixture')
            for name in ['TiaMcp.Host.dll','TiaMcp.Core.dll']: (root/name).write_bytes(b'fixture')
            raw=b'{}'; samples=root/'samples.json'
            samples.write_text(json.dumps(dict(reviewArtifactBase64=base64.b64encode(raw).decode(),
                              diagnosticRequest=dict(reviewId=hashlib.sha256(raw).hexdigest()))),encoding='utf-8')
            with patch.object(harness, 'watch_contracts', side_effect=ValueError('invalid schema')), patch.object(harness, 'Client') as client:
                with self.assertRaisesRegex(ValueError, 'invalid schema'):
                    harness.run(host, root/'result', samples)
                client.assert_not_called()


if __name__ == '__main__': unittest.main()
