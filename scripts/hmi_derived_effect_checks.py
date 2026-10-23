"""Independent DOM oracle for selected original C01 expected-effect inputs."""
import copy
import hashlib
import argparse
import json
from pathlib import Path
from xml.dom import Node, minidom
from xml.parsers.expat import ExpatError
from jsonschema import Draft202012Validator


def verify(record, request, originals):
    report=record['report']
    assert report['schemaVersion'] in ('hmi-component-review.v4','hmi-component-review.v5','hmi-component-review.v6','hmi-component-review.v7')
    files={f['id']:f for f in request['files']}
    for identity,row in files.items():
        data=originals[identity]
        assert len(data)==row['bytes'] and hashlib.sha256(data).hexdigest()==row['sha256']
    outputs={o['id']:o for o in request['outputs']}
    observations={o['outputId']:o for o in request['observations']}
    assert len(report['derivedEffects'])==len(request['expectations'])==report['summary']['derivedEffectCount']
    selected=set()
    def parse(data):
        text=data.decode('utf-8-sig')
        if '<!DOCTYPE' in text:raise ValueError('DTD')
        doc=minidom.parseString(text)
        if doc.encoding and doc.encoding.lower()!='utf-8':raise ValueError('encoding')
        return doc
    def shape(node):
        if node.nodeType==Node.DOCUMENT_NODE:
            return (node.nodeType,node.version,node.encoding,node.standalone,tuple(shape(n) for n in node.childNodes))
        if node.nodeType==Node.ELEMENT_NODE:
            return (node.nodeType,node.namespaceURI,node.localName,tuple(sorted((a.name,a.value) for a in node.attributes.values())),tuple(shape(n) for n in node.childNodes))
        return (node.nodeType,node.nodeName,node.nodeValue)
    for rule,result in zip(request['expectations'],report['derivedEffects']):
        assert result['expectation']==rule and rule['profile']=='classic-screen-backcolor.v1'
        assert not any(result[k] for k in ['nativeEffectVerified','contentAssociationVerified','wholeProjectPreservationVerified'])
        source=files[rule['sourceFileId']];output=outputs[rule['outputId']]
        assert rule['sourceFileId'] in output['current'] and output['action']=='update' and output['kind']=='hmiScreen'
        assert request['destination']['family']=='classic'
        assert result['source']=={k:source[k] for k in ['id','fileName','sha256','bytes']}
        assert (rule['outputId'],source['fileName']) not in selected
        selected.add((rule['outputId'],source['fileName']))
        expected=parse(originals[source['id']]);root=expected.documentElement
        assert root.tagName=='Document'
        owners=[n for n in root.childNodes if n.nodeType==Node.ELEMENT_NODE and n.tagName=='Hmi.Screen.Screen']
        assert len(owners)==1 and owners[0].getAttribute('ID')==rule['ownerId']
        attrs=[n for n in owners[0].childNodes if n.nodeType==Node.ELEMENT_NODE and n.tagName=='AttributeList']
        assert len(attrs)==1
        names=[n for a in attrs for n in a.childNodes if n.nodeType==Node.ELEMENT_NODE and n.tagName=='Name']
        scalars=[n for a in attrs for n in a.childNodes if n.nodeType==Node.ELEMENT_NODE and n.tagName=='BackColor']
        assert len(names)==len(scalars)==1 and ''.join(n.nodeValue or '' for n in names[0].childNodes)==output['name']
        scalar=scalars[0];assert not scalar.attributes.length and len(scalar.childNodes)==1
        assert scalar.firstChild.nodeType==Node.TEXT_NODE and scalar.firstChild.data==rule['expectedValue']
        scalar.firstChild.data=rule['replacementValue']
        for phase,ids,complete in [('proposal',output['proposed'],output['proposedComplete']),('observation',observations.get(rule['outputId'],{}).get('files',[]),observations.get(rule['outputId'],{}).get('complete',False))]:
            candidates=[files[i] for i in ids if files[i]['fileName']==source['fileName']];assert len(candidates)<=1
            candidate=candidates[0] if candidates else None
            status='missing' if complete else 'unobserved'
            if candidate:
                try:actual=parse(originals[candidate['id']])
                except (ValueError,UnicodeError,ExpatError):status='invalidDocument'
                else:status='match' if shape(expected)==shape(actual) else 'different'
            assert result[phase]==dict(status=status,file=None if candidate is None else {k:candidate[k] for k in ['id','fileName','sha256','bytes']})
    assert len(report['expectationCoverage'])==len(outputs)
    for row,output in zip(report['expectationCoverage'],outputs.values()):
        identity=output['id']
        names={files[i]['fileName'] for phase in ['baseline','current','proposed'] for i in output[phase]}
        names|={files[i]['fileName'] for i in observations.get(identity,{}).get('files',[])}
        assert row==dict(outputId=identity,derivedFileNames=sorted(n for n in names if (identity,n) in selected),unassessedFileNames=sorted(n for n in names if (identity,n) not in selected),completeOutputEffectsVerified=False)


def negative_controls(record,request,originals):
    controls={
        'invented result':lambda r:r['report']['derivedEffects'][0]['proposal'].update(status='unobserved'),
        'invented source':lambda r:r['report']['derivedEffects'][0]['source'].update(sha256='0'*64),
        'promoted effect':lambda r:r['report']['derivedEffects'][0].update(nativeEffectVerified=True),
        'missing coverage':lambda r:r['report']['expectationCoverage'].clear(),
    }
    for name,mutate in controls.items():
        changed=copy.deepcopy(record);mutate(changed)
        try:verify(changed,request,originals)
        except AssertionError:pass
        else:raise AssertionError('Independent oracle accepted '+name)
    return list(controls)


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--fixture',type=Path,required=True);args=parser.parse_args()
    root=args.fixture.resolve();proof=json.loads((root/'verification.json').read_bytes())
    assert proof['completed'] and not proof['nativeExecuted']
    store=root/'host-artifacts'
    schema=json.loads((Path(__file__).resolve().parents[1]/'contracts/hmi-component-review.v1.schema.json').read_bytes())
    def read(reference):
        path=(store/reference['path']).resolve();path.relative_to(store)
        raw=path.read_bytes();assert hashlib.sha256(raw).hexdigest()==reference['sha256'];return raw
    count=0
    for identity in proof['derivedRecords']:
        record=json.loads(read(dict(path='hmi-component-reviews/'+identity+'.json',sha256=identity)))
        request=json.loads(read(record['input']))
        for data,kind in [(record,'record'),(request,'request')]:Draft202012Validator({**schema,'$ref':'#/$defs/'+kind}).validate(data)
        originals={f['id']:read(f['artifact']) for f in record['report']['files']}
        verify(record,request,originals);count+=1
        if count==1:assert negative_controls(record,request,originals)==proof['derivedMutationControls']
    assert count==4 and len(proof['derivedMutationControls'])==4
    result=dict(completed=True,nativeExecuted=False,originalRecords=count,mutationControls=proof['derivedMutationControls'])
    (root/'derived-independent.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('PASS independent HMI expected effects: four original records and four mutation controls')


if __name__=='__main__':main()
