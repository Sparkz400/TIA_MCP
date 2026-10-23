"""Reopen actual pre-v4 technology records and exact sources through two current Host lifetimes."""
import argparse
import base64
import hashlib
import json
import shutil
from pathlib import Path
from smoke_mcp import Client, ROOT


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--host',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    output=args.output.resolve();assert not output.exists();output.mkdir(parents=True)
    source=ROOT/'fixtures/technology-profiles/retained-v1-v3';fixture=json.loads((source/'fixture.json').read_text(encoding='utf-8'));digest=lambda b:hashlib.sha256(b).hexdigest()
    for row in fixture['files']:
        raw=(source/'store'/row['path']).read_bytes();assert len(raw)==row['bytes'] and digest(raw)==row['sha256']
    assert {p.relative_to(source/'store').as_posix() for p in (source/'store').rglob('*') if p.is_file()}=={r['path'] for r in fixture['files']}
    artifacts=output/'store';shutil.copytree(source/'store',artifacts)
    report=dict(completed=False,nativeExecuted=False,priorProducer=fixture['producer'],checks=[],shutdowns=[])
    def check(ok,label):
        assert ok,label;report['checks'].append(label)
    try:
        for lifetime in range(2):
            client=Client(args.host.resolve(),90,['--artifacts',str(artifacts),'--worker',str(output/'absent.exe'),'--sim-worker',str(output/'absent.exe')])
            try:
                client.request('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'prior-technology-records','version':'1'}});client.notify('notifications/initialized')
                for record in fixture['records']:
                    key=record['recordId'];check(client.tool('technology_profile_report',{'recordId':key})==record['summary'],'Actual prior producer receipt unchanged: '+key)
                    for section,expected in record['sections'].items():
                        rows=[];offset=0
                        while True:
                            page=client.tool('technology_profile_report',dict(recordId=key,section=section,offset=offset,count=2));rows.extend(page['rows'])
                            if page['nextOffset'] is None:break
                            assert page['nextOffset']>offset;offset=page['nextOffset']
                        check(rows==expected,'Exact prior section unchanged: '+section)
                    refused=client.request('tools/call',{'name':'technology_profile_report','arguments':dict(recordId=key,section='encoderConnections')})
                    check(refused.get('isError') and refused['structuredContent']['error']['code']=='InvalidRequest','Legacy record cannot acquire encoder semantics')
                for row in fixture['files']:
                    data=bytearray()
                    while True:
                        part=client.tool('artifact_read',dict(path=row['path'],offset=len(data),count=65536));raw=base64.b64decode(part['data'],validate=True)
                        assert part['offset']==len(data) and len(raw)==part['count'];data.extend(raw)
                        if part['complete']:break
                        assert raw and part['nextOffset']==len(data)
                    check(len(data)==row['bytes'] and digest(data)==row['sha256'],'Exact prior artifact bytes: '+row['path'])
            finally:
                closed=client.close();report['shutdowns'].append(closed);check(closed['hostExitCode']==0 and not closed['forcedHostStop'] and not closed['workerExits'],'Clean Host exit without native workers')
        check(all(digest((artifacts/r['path']).read_bytes())==r['sha256'] for r in fixture['files']),'Original records remain unchanged')
        report['completed']=True
    finally:
        report.update(count=len(report['checks']),fixtureManifestSha256=digest((source/'fixture.json').read_bytes()),harnessSha256=digest(Path(__file__).read_bytes()),managedBinaryHashes={name:digest((args.host.parent/name).read_bytes()) for name in ('TiaMcp.Host.dll','TiaMcp.Core.dll')})
        (output/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(f"PASS actual prior technology v1-v3 producer: {report['count']} checks; exact retained records, no native execution.")


if __name__=='__main__':main()
