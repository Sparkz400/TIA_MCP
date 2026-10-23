"""Exact stdio client for isolated engineering qualification; no automatic termination."""
import json
import threading
import time
from pathlib import Path
from smoke_mcp import Client
from retained_native_lifecycle import RetainedNativeTree, close_host_without_kill

class ExactClient(Client):
    def __new__(cls, *args, **kwargs):
        value=super().__new__(cls)
        value._close_mutex=threading.RLock()
        value._close_result=None
        value._close_errors=[]
        value._closing=False
        value._request_failure=None
        return value
    def __init__(self, executable, timeout, arguments):
        root=Path(arguments[arguments.index('--artifacts')+1])
        worker=Path(arguments[arguments.index('--worker')+1])
        root.mkdir(parents=True,exist_ok=True)
        creation_error=None
        try:
            super().__init__(executable,timeout,arguments)
        except BaseException as error:
            creation_error=error
        self.exact_tree=RetainedNativeTree.__new__(RetainedNativeTree)
        try:
            if not getattr(self,'process',None):
                raise RuntimeError('Host creation outcome has no retained Popen identity')
            self.exact_tree.__init__(self.process,executable,worker,root/('exact-host-'+str(self.process.pid)))
        except BaseException as error:
            self._hold_startup(root,error)
        if creation_error is not None:self._hold_startup(root,creation_error)
    def _hold_startup(self,root,error):
        # The partially constructed self stays on this stack, retaining Popen
        # and all captured tree handles even when receipt writing fails.
        try:
            (root/'uncaptured-host-startup.json').write_text(json.dumps({'pid':self.process.pid,'error':str(error),'automaticTermination':False,'dependentDispatchProhibited':True}),encoding='utf-8')
        except BaseException:pass
        while True:
            try:time.sleep(.2)
            except BaseException:pass
    def request(self,method,params):
        with self._close_mutex:
            if self._closing or self._request_failure is not None:
                raise RuntimeError('Exact client is closed or failed; automatic replay is prohibited')
            try:return self._request_once(method,params)
            except BaseException as error:
                self._request_failure=error
                raise
    def _request_once(self,method,params):
        starting=method=='tools/call' and params.get('name')=='tia_start'
        detaching=method=='tools/call' and params.get('name')=='tia_detach'
        if detaching:self.exact_tree.await_capture_complete()
        if starting:self.exact_tree.begin_start()
        result=super().request(method,params)
        if starting and not result.get('isError'):
            packet=result.get('structuredContent') or json.loads('\n'.join(c['text'] for c in result['content'] if c['type']=='text'))
            self.exact_tree.accept_start(packet['processId'])
        if detaching and not result.get('isError'):
            for row in self.exact_tree.snapshot():
                if row['role']=='native' and row['proof'] is None:
                    proof=self.exact_tree.close_record(row)
                    if not (proof.get('completed') is True and proof.get('exitCode')==0 and proof.get('deadlineOrCancellationObserved') is False):
                        raise RuntimeError('Native detach lacks an on-time exact normal terminal proof')
        return result
    def close(self):
        # A cancellation while acquiring the close lock, writing EOF, or
        # collecting terminal receipts cannot unwind the owning client. Only
        # cleanup is retried; no request/start/recovery can be sent again.
        while True:
            try:
                with self._close_mutex:
                    self._closing=True
                    if self._close_result is None:
                        self._close_result=close_host_without_kill(self,self.exact_tree)
                    if self._close_errors:
                        self._close_result['closeInterrupted']=list(self._close_errors)
                        if 'tree' in self._close_result:self._close_result['tree']['completed']=False
                    return self._close_result
            except BaseException as error:
                self._closing=True
                message=type(error).__name__+': '+str(error)
                if message not in self._close_errors:self._close_errors.append(message)
                try:time.sleep(.2)
                except BaseException as interrupted:
                    message=type(interrupted).__name__+': '+str(interrupted)
                    if message not in self._close_errors:self._close_errors.append(message)
