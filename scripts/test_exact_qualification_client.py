"""File-only tests of request/startup ownership ordering; no native dispatch."""
import unittest
import tempfile
import threading
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import exact_qualification_client as module

class Tree:
    def __init__(self):self.calls=[];self.pending=False
    def begin_start(self):self.calls.append('intent');self.pending=True
    def accept_start(self,pid):self.calls.append(('accepted',pid));self.pending=False
    def await_capture_complete(self):self.calls.append('capture-barrier')
    def snapshot(self):return getattr(self,'rows',[])
    def close_record(self,row):
        self.calls.append(('terminal',row['pid']));row['proof']=getattr(self,'terminal',{'completed':True,'exitCode':0,'deadlineOrCancellationObserved':False});return row['proof']

class Checks(unittest.TestCase):
    def client(self):
        value=module.ExactClient.__new__(module.ExactClient);value.exact_tree=Tree();return value
    def test_start_intent_precedes_native_request(self):
        client=self.client()
        def response(*args):
            self.assertTrue(client.exact_tree.pending);client.exact_tree.calls.append('request')
            return {'structuredContent':{'processId':17}}
        with patch.object(module.Client,'request',side_effect=response):client.request('tools/call',{'name':'tia_start'})
        self.assertEqual(client.exact_tree.calls,['intent','request',('accepted',17)])
    def test_error_reply_cannot_complete_startup(self):
        client=self.client()
        with patch.object(module.Client,'request',return_value={'isError':True}):client.request('tools/call',{'name':'tia_start'})
        self.assertTrue(client.exact_tree.pending);self.assertEqual(client.exact_tree.calls,['intent'])
    def test_transport_timeout_cannot_complete_startup(self):
        client=self.client()
        with patch.object(module.Client,'request',side_effect=TimeoutError):
            with self.assertRaises(TimeoutError):client.request('tools/call',{'name':'tia_start'})
        self.assertTrue(client.exact_tree.pending)
    def test_malformed_reply_cannot_complete_startup(self):
        client=self.client()
        with patch.object(module.Client,'request',return_value={'structuredContent':{'attached':True}}):
            with self.assertRaises(KeyError):client.request('tools/call',{'name':'tia_start'})
        self.assertTrue(client.exact_tree.pending)
    def test_text_reply_keeps_exact_identity(self):
        client=self.client()
        with patch.object(module.Client,'request',return_value={'content':[{'type':'text','text':'{"processId":19}'}]}):client.request('tools/call',{'name':'tia_start'})
        self.assertEqual(client.exact_tree.calls,['intent',('accepted',19)])
    def test_initialization_is_not_native_start_acceptance(self):
        client=self.client()
        with patch.object(module.Client,'request',return_value={}):client.request('initialize',{})
        self.assertEqual(client.exact_tree.calls,[])
    def test_detach_never_precedes_capture_barrier(self):
        client=self.client()
        def response(*args):self.assertEqual(client.exact_tree.calls,['capture-barrier']);return {}
        with patch.object(module.Client,'request',side_effect=response):client.request('tools/call',{'name':'tia_detach'})
    def test_close_uses_exact_tree_without_base_automatic_kill(self):
        client=self.client()
        with patch.object(module,'close_host_without_kill',return_value={'completed':True}) as close,patch.object(module.Client,'close',side_effect=AssertionError('automatic close forbidden')):
            self.assertEqual(client.close(),{'completed':True});close.assert_called_once_with(client,client.exact_tree)
    def test_successful_detach_finalizes_only_unclosed_native_original_handle(self):
        client=self.client();client.exact_tree.rows=[{'role':'host','proof':None,'pid':1},{'role':'native','proof':{},'pid':2},{'role':'native','proof':None,'pid':3}]
        with patch.object(module.Client,'request',return_value={}):client.request('tools/call',{'name':'tia_detach'})
        self.assertEqual(client.exact_tree.calls,['capture-barrier',('terminal',3)])
    def test_failed_detach_retains_native_handle(self):
        client=self.client();client.exact_tree.rows=[{'role':'native','proof':None,'pid':3}]
        with patch.object(module.Client,'request',return_value={'isError':True}):client.request('tools/call',{'name':'tia_detach'})
        self.assertEqual(client.exact_tree.calls,['capture-barrier']);self.assertIsNone(client.exact_tree.rows[0]['proof'])
    def test_late_terminal_proof_prevents_successful_detach(self):
        for proof in ({'completed':True,'exitCode':0,'deadlineOrCancellationObserved':True},{'completed':True,'exitCode':2,'deadlineOrCancellationObserved':False},{'completed':False,'exitCode':0,'deadlineOrCancellationObserved':False}):
            client=self.client();client.exact_tree.rows=[{'role':'native','proof':None,'pid':3}];client.exact_tree.terminal=proof
            with patch.object(module.Client,'request',return_value={}):
                with self.assertRaisesRegex(RuntimeError,'terminal proof'):client.request('tools/call',{'name':'tia_detach'})
    def test_base_constructor_failure_retains_partial_popen_before_hold(self):
        captured=[]
        def failed_init(client,*args):client.process=SimpleNamespace(pid=23);raise KeyboardInterrupt('reader failed')
        class BoundTree:
            def __init__(self,process,*args):captured.append(process)
        class Held(BaseException):pass
        with tempfile.TemporaryDirectory() as temp,patch.object(module.Client,'__init__',failed_init),patch.object(module,'RetainedNativeTree',BoundTree),patch.object(module.ExactClient,'_hold_startup',side_effect=Held) as hold:
            with self.assertRaises(Held):module.ExactClient('host',30,['--artifacts',temp,'--worker','worker'])
            self.assertEqual([p.pid for p in captured],[23]);self.assertIsInstance(hold.call_args.args[1],KeyboardInterrupt)
    def test_capture_failure_preserves_partial_tree_before_hold(self):
        client=module.ExactClient.__new__(module.ExactClient)
        def init(value,*args):value.process=SimpleNamespace(pid=24)
        class BoundTree:
            def __init__(self,*args):self.original_handle='retained';raise OSError('capture interrupted')
        class Held(BaseException):pass
        with tempfile.TemporaryDirectory() as temp,patch.object(module.Client,'__init__',init),patch.object(module,'RetainedNativeTree',BoundTree),patch.object(module.ExactClient,'_hold_startup',side_effect=Held):
            with self.assertRaises(Held):client.__init__('host',30,['--artifacts',temp,'--worker','worker'])
        self.assertEqual(client.process.pid,24);self.assertEqual(client.exact_tree.original_handle,'retained')
    def test_failed_receipt_and_cancellation_cannot_escape_retained_hold(self):
        client=self.client();client.process=SimpleNamespace(pid=25)
        reached=threading.Event();never_release=threading.Event();calls=[]
        def delay(_):
            calls.append(1)
            if len(calls)==1:raise KeyboardInterrupt()
            if len(calls)==2:raise SystemExit()
            reached.set();never_release.wait()
        with patch.object(Path,'write_text',side_effect=OSError('disk unavailable')),patch.object(module,'time',SimpleNamespace(sleep=delay)):
            thread=threading.Thread(target=client._hold_startup,args=(Path('unused'),OSError('startup')),daemon=True);thread.start()
            self.assertTrue(reached.wait(2));self.assertTrue(thread.is_alive());self.assertEqual(client.process.pid,25)

if __name__=='__main__':unittest.main()
