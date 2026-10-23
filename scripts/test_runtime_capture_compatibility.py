"""Reopen exact capture bytes produced before optional security handoff binding."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path
from smoke_mcp import Client, ROOT
from runtime_capture_checks import validator


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args();output=args.output.resolve()
    assert not output.exists(),'Fresh evidence directory required'
    fixture=ROOT/'fixtures/physical-runtime/retained-v1'
    manifest=json.loads((fixture/'fixture.json').read_text())
    digest=lambda data:hashlib.sha256(data).hexdigest()
    for row in manifest['files']:
        raw=(fixture/'store'/row['path']).read_bytes()
        assert len(raw)==row['bytes'] and digest(raw)==row['sha256']
    shutil.copytree(fixture/'store',output/'store')
    report=dict(completed=False,nativeExecuted=False,producer=manifest['producer'],checks=[],shutdowns=[])
    def check(ok,label):
        assert ok,label
        report['checks'].append(label)
    try:
        for lifetime in range(2):
            client=Client(args.host.resolve(),60,['--no-native-workers','--artifacts',str(output/'store')])
            try:
                client.request('initialize',dict(protocolVersion='2025-11-25',capabilities={},clientInfo=dict(name='prior-captures',version='1')))
                client.notify('notifications/initialized')
                for record in manifest['records']:
                    status=client.tool('runtime_capture_status',dict(captureId=record['captureId']))
                    check(status==record['status'],'Exact prior capture status: '+str(lifetime))
                    check('handoff' not in status['plan'] and 'handoffRef' not in status['plan']['request'],'No retroactive handoff in prior capture')
                    page=client.tool('runtime_capture_page',dict(snapshotRef=record['snapshotRef'],count=100))
                    check(page==record['page'],'Exact prior capture rows')
                    exported=client.tool('runtime_capture_export',dict(snapshotRef=record['snapshotRef']))
                    raw=Path(exported['artifacts'][0]['path']).read_bytes();validator().validate(json.loads(raw))
                    check(digest(raw)==record['exportSha256'],'Exact prior capture export bytes')
                for row in manifest['files']:
                    check(digest((output/'store'/row['path']).read_bytes())==row['sha256'],'No rewrite: '+row['path'])
            finally:
                closed=client.close();report['shutdowns'].append(closed)
                check(closed['hostExitCode']==0 and not closed['forcedHostStop'] and not closed['workerExits'],'Clean offline Host lifetime')
        report['completed']=True
    finally:
        report['count']=len(report['checks'])
        report['binaryHashes']={name:digest((args.host.resolve().parent/name).read_bytes()) for name in ['TiaMcp.Host.dll','TiaMcp.Core.dll']}
        (output/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print('PASS actual prior capture compatibility:',report['count'],'checks')


if __name__=='__main__':main()
