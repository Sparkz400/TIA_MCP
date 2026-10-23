"""Reopen actual prior-producer v1-v5 HMI records without upgrading or rewriting them."""
import argparse
import hashlib
import json
from pathlib import Path
from smoke_mcp import Client, ROOT


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--host',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    output=args.output.resolve();assert not output.exists()
    digest=lambda b:hashlib.sha256(b).hexdigest()
    files={};records=[];producers=[]
    for name in ['retained-v1-v3','retained-v4','retained-v5']:
        fixture=ROOT/'fixtures/hmi-components'/name;manifest=json.loads((fixture/'fixture.json').read_bytes());producers.append(manifest['producer'])
        records+=manifest['records']
        for row in manifest['files']:
            data=(fixture/'store'/row['path']).read_bytes();assert len(data)==row['bytes'] and digest(data)==row['sha256']
            destination=output/'store'/row['path'];assert destination.resolve().is_relative_to((output/'store').resolve())
            if destination.exists():assert destination.read_bytes()==data
            else:destination.parent.mkdir(parents=True,exist_ok=True);destination.write_bytes(data)
            if row['path'] in files:assert files[row['path']]==row
            files[row['path']]=row
    assert len({r['recordId'] for r in records})==len(records)
    proof=dict(completed=False,nativeExecuted=False,producers=producers,checks=[],shutdowns=[])
    def check(ok,label):
        assert ok,label
        proof['checks'].append(label)
    try:
        for lifetime in range(2):
            client=Client(args.host.resolve(),60,['--no-native-workers','--artifacts',str(output/'store')])
            try:
                client.request('initialize',dict(protocolVersion='2025-11-25',capabilities={},clientInfo=dict(name='prior-hmi',version='1')));client.notify('notifications/initialized')
                for row in records:
                    identity=row['recordId'];receipt=client.tool('hmi_component_review_page',dict(recordId=identity))
                    check(receipt==row['receipt'],'Exact prior receipt: '+str(lifetime)+' '+row['schemaVersion'])
                    record=json.loads((output/'store/hmi-component-reviews'/(identity+'.json')).read_bytes())
                    version=record['report']['schemaVersion']
                    has_derived=version in ['hmi-component-review.v4','hmi-component-review.v5']
                    has_libraries=version=='hmi-component-review.v5'
                    check(('derivedEffects' in record['report'])==has_derived,'Historical expected-effect version remains exact')
                    check(('libraryAssociations' in record['report'])==has_libraries,'Historical library-association version remains exact')
                    for section,value in record['report'].items():
                        if not isinstance(value,list):continue
                        offset=0;rows=[]
                        while True:
                            page=client.tool('hmi_component_review_page',dict(recordId=identity,section=section,offset=offset,count=1))
                            rows.extend(r['value'] for r in page['rows'])
                            if page['nextOffset'] is None:break
                            check(page['nextOffset']>offset,'Prior paging makes progress');offset=page['nextOffset']
                        check(rows==value,'Exact prior '+section)
                    for section in ['dependencyImpacts','dependencyImpactCoverage','dependencyAssociations','dependencyCoverage','dependencyIdentities'] + ([] if has_libraries else ['libraryAssociations'] if has_derived else ['derivedEffects','libraryAssociations']):
                        reply=client.request('tools/call',dict(name='hmi_component_review_page',arguments=dict(recordId=identity,section=section)))
                        check(reply.get('isError') and reply['structuredContent']['error']['code']=='InvalidHmiComponentReview','Prior version refuses '+section)
                for row in files.values():check(digest((output/'store'/row['path']).read_bytes())==row['sha256'],'Exact prior bytes: '+row['path'])
            finally:
                closed=client.close();proof['shutdowns'].append(closed)
                check(closed['hostExitCode']==0 and not closed['forcedHostStop'] and not closed['workerExits'],'Clean prior-record Host lifetime')
        proof['completed']=True
    finally:
        proof['count']=len(proof['checks']);proof['records']=len(records)
        proof['binaryHashes']={n:digest((args.host.resolve().parent/n).read_bytes()) for n in ['TiaMcp.Host.dll','TiaMcp.Core.dll']}
        (output/'verification.json').write_text(json.dumps(proof,indent=2)+'\n',encoding='utf-8')
    print('PASS actual prior HMI v1-v5:',proof['records'],'records;',proof['count'],'checks')


if __name__=='__main__':main()
