"""Adversarial creation lost-reply controller/oracle checks; no native launch."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock

import qualify_watch_authoring_loss as controller
import verify_watch_authoring_loss as oracle
from verify_watch_workflow import VerificationError


class Loss(unittest.TestCase):
    def marker(self):
        return {'point':oracle.POINT,'automaticReplay':False,'response':{'project':{'path':'owned.ap21'},'saved':True,'target':{'exists':True}}}

    def records(self):
        return [{'state':s,'checkpoints':[{}] if s!='prepared' else [],'evidence':[]} for s in ('prepared','dispatchIntent','recoveryIntent','reconciled')]

    def test_saved_creation_unknown_reply(self):
        self.assertTrue(oracle.loss_boundary(self.records(),self.marker(),'owned.ap21')['target']['exists'])

    def test_existing_edit_or_unsaved_reply_does_not_qualify_creation(self):
        for field in ('exists','saved'):
            marker=self.marker()
            (marker['response']['target'] if field=='exists' else marker['response'])[field]=False
            with self.assertRaises(VerificationError):oracle.loss_boundary(self.records(),marker,'owned.ap21')

    def test_wrong_project_or_boundary_is_refused(self):
        for change in (lambda m:m.update(point='host.watch_checkpoint'),lambda m:m['response']['project'].update(path='other.ap21')):
            marker=self.marker();change(marker)
            with self.assertRaises(VerificationError):oracle.loss_boundary(self.records(),marker,'owned.ap21')

    def test_missing_checkpoint_and_repeated_dispatch_are_refused(self):
        records=self.records();records[1]['checkpoints']=[]
        with self.assertRaises(VerificationError):oracle.loss_boundary(records,self.marker(),'owned.ap21')
        records=self.records();records.insert(3,copy.deepcopy(records[1]))
        with self.assertRaises(VerificationError):oracle.loss_boundary(records,self.marker(),'owned.ap21')

    def test_adopted_lost_reply_or_no_recovery_are_refused(self):
        records=self.records();records[-1]['evidence']=[{'gate':'dispatch.readback'}]
        with self.assertRaises(VerificationError):oracle.loss_boundary(records,self.marker(),'owned.ap21')
        with self.assertRaises(VerificationError):oracle.loss_boundary(self.records()[:2],self.marker(),'owned.ap21')

    def run_object(self,root):
        run=controller.Run.__new__(controller.Run);run.root=Path(root)
        run.call=Mock(side_effect=RuntimeError('Host reply lost'))
        run.save,run.close_host,run.start_host,run.recover=Mock(),Mock(),Mock(),Mock()
        return run

    def test_failure_without_marker_never_restarts(self):
        with tempfile.TemporaryDirectory() as root:
            run=self.run_object(root)
            with self.assertRaisesRegex(RuntimeError,'without admitted'):run.interrupted_apply({'id':'op','digest':'digest'})
            run.call.assert_called_once();run.start_host.assert_not_called();run.recover.assert_not_called()

    def test_exact_owner_closes_before_restarted_host_and_recovery(self):
        with tempfile.TemporaryDirectory() as root:
            run=self.run_object(root);(Path(root)/'interruption-marker.json').write_text(json.dumps(self.marker()))
            order=Mock();order.attach_mock(run.close_host,'close');order.attach_mock(run.start_host,'start');order.attach_mock(run.recover,'recover')
            operation={'id':'op','digest':'digest'};run.interrupted_apply(operation)
            self.assertEqual([c[0] for c in order.mock_calls],['close','start','recover'])
            run.close_host.assert_called_once_with(expected_loss=True);run.call.assert_called_once()

    def test_unfired_fault_is_failure_without_retry(self):
        with tempfile.TemporaryDirectory() as root:
            run=self.run_object(root);run.call.side_effect=None
            with self.assertRaisesRegex(RuntimeError,'did not fire'):run.interrupted_apply({'id':'op','digest':'digest'})
            run.call.assert_called_once();run.recover.assert_not_called()


if __name__=='__main__':unittest.main()
