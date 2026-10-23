"""Portable exact-controller refusal and failed-negative cleanup checks."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import qualify_watch_exact_profile as subject
from qualify_watch_mcp import ToolError, Run as BaseRun


class Controller(unittest.TestCase):
    def make(self, root):
        run = subject.Run.__new__(subject.Run)
        run.root = Path(root)
        run.reopen = Mock()
        run.save = Mock()
        run.call = Mock()
        return run

    def test_scope_mismatch_stops_before_shadow_prepare(self):
        with tempfile.TemporaryDirectory() as root:
            run = self.make(root)
            prepared = {'review': {'exactProfileCandidate': {'profile': subject.oracle.PROFILE,
                'scopeMatched': False, 'productionApplyEnabled': False}}}
            with self.assertRaisesRegex(RuntimeError, 'outside the exact'):
                run.after_prepare({'idempotencyKey': 'original'}, prepared)
            run.call.assert_not_called()

    def test_refusal_code_must_be_exact(self):
        with tempfile.TemporaryDirectory() as root:
            run = self.make(root)
            run.call.side_effect = ToolError('tool', {'error': {'code': 'Unexpected'}})
            with self.assertRaises(RuntimeError): run.expect_refusal('tool', {}, 'WatchTableCollision', 'proof')
            run.save.assert_not_called()

    def test_accepted_collision_is_failure(self):
        with tempfile.TemporaryDirectory() as root:
            run = self.make(root)
            run.call.return_value = {'operation': {}}
            with self.assertRaisesRegex(RuntimeError, 'Expected native/Host refusal'):
                run.expect_refusal('tool', {}, 'WatchTableCollision', 'proof')

    def test_failed_native_negative_reconciles_fresh_baseline_without_replay(self):
        with tempfile.TemporaryDirectory() as root:
            run = self.make(root)
            generation = run.root/'generation.json'
            generation.write_text(json.dumps({'capture': {'binding': {'projectPath': 'owned.ap21'}}}))
            directory = run.root/'engineering-admissions'/'owner'
            directory.mkdir(parents=True)
            (directory/'00000001.json').write_text(json.dumps({'id': 'owner', 'state': 'outcomeUnknown',
                'scope': {'operation': 'watch_authoring_source_negative'}}))
            original = ToolError('qualification_watch_authoring_source_negative', {'error': {'code': 'WatchSourceNegativeFailed'}})
            run.call.side_effect = [original, {'admission': {'state': 'reconciled'}}]
            with patch.object(subject.authoring.oracle, 'artifact', return_value=generation):
                with self.assertRaises(ToolError): run.after_generate({'artifact': {}}, 'owned.ap21')
            run.reopen.assert_called_once_with('owned.ap21')
            self.assertEqual([c.args[0] for c in run.call.call_args_list],
                ['qualification_watch_authoring_source_negative', 'qualification_watch_authoring_source_reconcile'])

    def test_plain_mcp_error_is_retained_before_normalized_failure(self):
        run = BaseRun.__new__(BaseRun)
        response = {'isError': True, 'content': [{'type': 'text', 'text': "'Author' is not supported by type 'FB'."}]}
        run.client, run.save = Mock(), Mock()
        run.client.request.return_value = response
        with self.assertRaises(ToolError) as caught:
            run.call('qualification_watch_authoring_source_negative', {})
        self.assertEqual(caught.exception.packet['error']['code'], 'McpToolError')
        self.assertEqual(caught.exception.packet['error']['message'], response['content'][0]['text'])
        self.assertEqual(run.save.call_args.args[1], response)
        run.client.request.assert_called_once()

    def test_malformed_success_is_retained_and_never_labelled_success(self):
        run = BaseRun.__new__(BaseRun)
        response = {'content': [{'type': 'text', 'text': 'truncated native reply'}]}
        run.client, run.save = Mock(), Mock()
        run.client.request.return_value = response
        with self.assertRaises(ToolError) as caught:
            run.call('tool', {}, 'success')
        self.assertEqual(caught.exception.packet['error']['code'], 'InvalidMcpPayload')
        run.save.assert_called_once()
        self.assertEqual(run.save.call_args.args[1], response)

    def test_structured_refusal_preserves_exact_code_and_raw_reply(self):
        run = BaseRun.__new__(BaseRun)
        packet = {'error': {'code': 'StaleSource'}}
        response = {'isError': True, 'structuredContent': packet}
        run.client, run.save = Mock(), Mock()
        run.client.request.return_value = response
        with self.assertRaises(ToolError) as caught:
            run.call('tool')
        self.assertEqual(caught.exception.packet, packet)
        self.assertEqual(run.save.call_args.args[1], response)


if __name__ == '__main__':
    unittest.main()
