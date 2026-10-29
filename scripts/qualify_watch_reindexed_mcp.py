"""Run only the separately reviewed reorder/delete document-ID profile."""
import argparse
import json
import shutil
import time
from pathlib import Path
import qualify_watch_mcp as workflow
import verify_watch_reindexed_profile as oracle
from exact_qualification_client import ExactClient
from retained_native_lifecycle import require


class Run(workflow.Run):
    def __init__(self,args):
        super().__init__(args)
        admission=json.loads((self.root/'admission.json').read_text(encoding="utf-8"))
        admission['reviewedDocumentIdProfile']=oracle.PROFILE
        admission['nativeOutputNormalization']=False
        admission['productionProfileEnabled']=False
        (self.root/'admission.json').write_text(json.dumps(admission,indent=2),encoding='utf-8')
        sources=[]
        for name in ('qualify_watch_reindexed_mcp.py','verify_watch_reindexed_profile.py','retained_native_lifecycle.py','exact_qualification_client.py'):
            target=self.root/'controller-source'/name
            shutil.copyfile(Path(__file__).with_name(name),target)
            sources.append({'path':str(target),'sha256':workflow.digest(target)})
        self.save('explicit-id-profile-sources',sources)

    def start_host(self,point):
        require(self.client is None and not self.clients, 'This profile permits one fresh Host; automatic replay is prohibited')
        require(point=='none', 'Interruption/recovery dispatch is not admitted by this runner')
        require(self.host_files and all(workflow.digest(p['path'])==p['sha256'] for p in self.host_files),
            'Frozen Host package changed')
        self.client=ExactClient(self.args.host.resolve(),240,['--worker',str(self.args.worker.resolve()),
            '--artifacts',str(self.root),'--component-qualification','--watch-qualification',point])
        # Retain the client before any logging or initialization can fail. Use
        # its bound inventory instead of opening additional PID-based watchers.
        entry={'identity':self.client.exact_tree.anchor['identity'],'point':point,'processes':[]}
        self.clients.append(entry)
        self.save('host-01-start',entry)
        self.client.request('initialize',{'protocolVersion':'2025-11-25','capabilities':{},
            'clientInfo':{'name':'watch-native-matrix','version':'1'}})
        self.client.notify('notifications/initialized')
        return self.client.process.pid

    def start_tia(self):
        result=self.call('tia_start')
        require(result['ownsPortalProcess'] and not result['projects'], 'Expected an owned empty native instance')
        self.native=self.client.exact_tree.one('native',result['processId'])
        self.save('native-'+str(result['processId'])+'-start',self.native['identity'])

    def close_tia(self):
        self.call('tia_detach')
        proof=self.client.exact_tree.close_record(self.native)
        self.lifetimes.append(proof)
        self.save('native-'+str(proof['processId'])+'-exit',proof)
        self.native=None

    def close_host(self,expected_loss=False):
        if self.client is None:return
        client=self.client
        requested=time.time()
        # No receipt write, optional monitor, or partial Run initialization may
        # precede the exact cleanup barrier. The client keeps failed/unknown
        # startup handles indefinitely and never kills or dispatches a replay.
        close=client.close()
        self._closed_client=client
        self.client=None
        exits=[row for row in close['tree']['exits'] if row['role']!='host']
        native=getattr(self,'native',None)
        if native is not None:
            self.lifetimes.append(native['proof']);self.native=None
        self.save('host-01-processes',[row['identity'] for row in exits])
        self.save('host-01-close',{'identity':client.exact_tree.anchor['identity'],'close':close,
            'exits':exits,'logs':client.errors,'expectedLoss':expected_loss,
            'closeRequestedAt':requested,'closedAt':time.time(),'cleanupError':None})
        require(not expected_loss and close['tree']['completed'] is True and close['hostExitCode']==0 and
            close['forcedHostStop'] is False and close['stderrEofObserved'] is True and not close['closeInterrupted'],
            'Exact normal Host/worker/native cleanup failed')
    def call(self,name,arguments=None,label=None):
        if name=='watch_table_prepare':
            assert self.args.mode in ('reorder','delete') and arguments['editKind']==self.args.mode
            arguments={**arguments,'reindexDocumentIds':True}
        return super().call(name,arguments,label)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('host','worker','root','seed'):parser.add_argument('--'+key,type=Path,required=True)
    parser.add_argument('--mode',choices=('reorder','delete'),required=True)
    args=parser.parse_args();args.native_cleanup_helper=None;args.comment_input=None
    # Only this dedicated process selects the separate oracle; the original
    # script and all historical evidence/oracle dependencies remain unchanged.
    workflow.oracle=oracle
    workflow.Client=ExactClient
    run=Run(args)
    failure=None
    try:
        try:
            result=run.execute()
        except BaseException as error:
            failure=error
            raise
        finally:
            if run.client is not None:
                try:run.close_host()
                except BaseException as cleanup:
                    save_failure(run,'runner-cleanup-failure',cleanup)
                    if failure is None:raise
        print(json.dumps({'completed':result['completed'],'mode':args.mode,'root':str(run.root),'profile':oracle.PROFILE}),flush=True)
    except BaseException as error:
        save_failure(run,'runner-failure',error)
        raise


def save_failure(run,name,error):
    # Reporting is best effort; it must neither replace the initiating failure
    # nor prevent the unconditional cleanup in main's finally block.
    try:run.save(name,{'completed':False,'error':str(error),'errorType':type(error).__name__,
        'automaticReplay':False,'qualificationPassed':False})
    except BaseException:pass

if __name__=='__main__':main()
