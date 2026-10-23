"""Engineering-only exact tracked session for the isolated candidate worker."""
import functools
import hashlib
from pathlib import Path
from block_test_process import OwnedTiaProcessWatcher
from exact_qualification_client import ExactClient
from qualification_owned_scratch import require
from typed_graphical_owned_session import TypedOwnedSession

class ExactTypedOwnedSession(TypedOwnedSession):
    def __init__(self,*args,**kwargs):
        require(kwargs.get('sim_worker') is None,'This exact session is engineering-only; runtime has a separate exclusive window')
        require('client_factory' not in kwargs and 'watcher_factory' not in kwargs,'Native ownership cannot be replaced by caller factories')
        kwargs['client_factory']=ExactClient
        kwargs['watcher_factory']=functools.partial(OwnedTiaProcessWatcher,query_exit_code=True)
        self.exact_sources=[{'path':str(Path(__file__).with_name(name)),'sha256':hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()}
            for name in ('typed_graphical_exact_session.py','exact_qualification_client.py','retained_native_lifecycle.py','block_test_process.py','typed_graphical_owned_session.py','qualification_owned_scratch.py','smoke_mcp.py')]
        super().__init__(*args,**kwargs)
        self.report['exactEngineeringSessionSources']=self.exact_sources
        self.report['runtimeAllowed']=False
    def start(self):
        require(all(hashlib.sha256(Path(r['path']).read_bytes()).hexdigest()==r['sha256'] for r in self.exact_sources),'Exact session sources changed before native start')
        value=super().start()
        tree=self.client.exact_tree
        self.session['exactStartupTreeDirectory']=str(tree.directory)
        require(tree.pending_start is None and tree.one('native',value['processId'])['watcher'].evidence.get('birthAndImageVerified') is True,'Native startup lacks retained exact ancestry/birth/image')
        self.persist()
        return value
    def close(self):
        if self.client is None:return
        super().close()
        close=self.session['cleanup']['launcherShutdown'];tree=close.get('tree',{})
        try:
            require(tree.get('completed') is True and tree.get('startupOutcomeUnknown') is False and not tree.get('errors') and not tree.get('unknownReasons') and close.get('closeInterrupted') is None,'Exact startup/terminal ownership did not complete normally')
            require(sorted(row['role'] for row in tree['exits'])==['host','native','worker'],'One exact Host/worker/native lifetime required per typed session')
            require(all(row['proof'].get('birthAndImageVerified') is True and row['proof'].get('sameOsHandleRetained') is True and row['proof'].get('completed') is True and row['proof'].get('exitCode')==0 and row['proof'].get('deadlineOrCancellationObserved') is False for row in tree['exits']),'Exact process proof is incomplete or late')
            self.session['cleanup']['exactStartupAndTerminalProofPassed']=True
        except BaseException:
            self.poisoned=True;self.session['cleanup']['completed']=False;raise
        finally:self.persist()
