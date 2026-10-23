"""Independent encoder source, identity, unit, dependency and schema checks."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator
from test_drive_telegram_contracts import verify as verify_drive

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT / 'contracts/technology-profile-review.v1.schema.json').read_text(encoding='utf-8'))


def validate(value, kind):
    Draft202012Validator({**SCHEMA, '$ref': '#/$defs/' + kind}).validate(value)


def declaration():
    return dict(id='encoder', provider='supplied', interfaceName='SensorInterface', state='connected', route='addresses',
                sensorIndex=1, connectOption='Default', unknownReason=None,
                target=dict(plc=dict(nodeId='plc', objectPath='/plc'), input=dict(value=256, unit='byte'), output=dict(value=0, unit='bit')))


class EncoderContracts(unittest.TestCase):
    def test_schema_and_routes(self):
        Draft202012Validator.check_schema(SCHEMA); validate(declaration(), 'encoderDeclaration')
        for route, target in [('module',dict(nodeId='h',objectPath='/h')), ('splitModules',dict(inputModule=dict(nodeId='h',objectPath='/h'),outputModule=dict(nodeId='h',objectPath='/h'))),
                              ('channel',dict(module=dict(nodeId='h',objectPath='/h'),number=0,type='Technology',direction='Complex')),
                              ('dbMember',dict(block=dict(nodeId='b',objectPath='/b'),member='Data.Encoder')),
                              ('startdriveTelegram',dict(recordId='a'*64,phase='before',telegramId='t'))]:
            row=declaration(); row.update(route=route,target=target); validate(row,'encoderDeclaration')

    def test_unknown_is_not_disconnected(self):
        row=declaration();row.update(state='unknown',target=None)
        self.assertFalse(Draft202012Validator({**SCHEMA,'$ref':'#/$defs/encoderDeclaration'}).is_valid(row))
        row['unknownReason']='Not captured';validate(row,'encoderDeclaration')
        row.update(state='disconnected',sensorIndex=0,unknownReason=None);validate(row,'encoderDeclaration')
        row['target']=declaration()['target']
        self.assertFalse(Draft202012Validator({**SCHEMA,'$ref':'#/$defs/encoderDeclaration'}).is_valid(row))

    def test_unknown_units_require_reason(self):
        for field,value in [('unit','unknown'),('value',None)]:
            row=declaration();row['target']['input'][field]=value
            self.assertFalse(Draft202012Validator({**SCHEMA,'$ref':'#/$defs/encoderDeclaration'}).is_valid(row))
            row['unknownReason']='Unknown readback';validate(row,'encoderDeclaration')

    def test_bounds_route_shapes_and_types(self):
        bad=[]
        for key,value in [('sensorIndex',True),('sensorIndex',3),('state','qualified'),('route','guessed'),('nativeConnectionVerified',True)]:
            row=declaration();row[key]=value;bad.append(row)
        for key,value in [('value',2147483648),('value',-1),('value',True),('unit','word')]:
            row=declaration();row['target']['input'][key]=value;bad.append(row)
        row=declaration();row['route']='module';bad.append(row)
        for row in bad:self.assertFalse(Draft202012Validator({**SCHEMA,'$ref':'#/$defs/encoderDeclaration'}).is_valid(row),row)

    def test_old_versions_do_not_gain_encoder_fields(self):
        for version in (1,2,3):
            self.assertNotIn('encoders',SCHEMA['$defs']['requestV'+str(version)]['properties'])
            self.assertEqual({'$ref':'#/$defs/summaryV1'},SCHEMA['$defs']['reportV'+str(version)]['properties']['summary'])
        self.assertEqual({'const':False},SCHEMA['$defs']['encoderObservation']['properties']['nativeConnectionVerified'])
        self.assertEqual({'const':False},SCHEMA['$defs']['encoderChange']['properties']['identityEqualityVerified'])


def verify(packet, report, review, load_drive):
    validate(packet,'request');validate(report,'report')
    nodes=review['model']['nodes'];plc=packet['before']['identity']['plcPath']
    def resolve(selector,kind):
        paths=[n for n in nodes if n.get('objectPath')==selector['objectPath']];ids=[n for n in nodes if n.get('nodeId')==selector['nodeId']]
        if len(paths)>1 or len(ids)>1:return 'ambiguous'
        if not paths and not ids:return 'missing'
        if len(paths)!=1 or len(ids)!=1 or paths[0] is not ids[0]:return 'identityMismatch'
        n=paths[0]
        if n.get('origin')=='virtual':return 'virtualObject'
        if n.get('kind')!=kind:return 'wrongKind'
        if kind=='plc':return 'capturedObject' if selector['objectPath']==plc else 'differentPlc'
        if n.get('plcPath') is None:return 'scopeUnknown'
        return 'capturedObject' if n['plcPath']==plc else 'differentPlc'
    owner=next(n for n in nodes if n['objectPath']==packet['technologyPath'] and n.get('origin')!='virtual')
    assert report['encoderCoverage']['ownerResolution']==resolve({k:owner[k] for k in ('nodeId','objectPath')},'technologyObject')
    observations=[];states=[];drives={}
    for phase in ('before','after'):
        rows=packet['encoders'][phase]['observations'];states.append({r['id']:r for r in rows});assert len(states[-1])==len(rows)
        assert report['encoderCoverage'][phase+'Complete']==packet['encoders'][phase]['complete']
        assert report['summary'][phase+'EncoderCount']==len(rows)
        for index,row in enumerate(rows):
            target=row['target'];evidence={};route=row['route'];issues=[]
            if row['state']=='connected' and row['sensorIndex']==0:issues.append('connectedWithZeroSensorIndex')
            if row['state']=='disconnected' and row['sensorIndex'] not in (0,None):issues.append('disconnectedWithSensorIndex')
            if target is None:evidence['state']='declaredDisconnected' if row['state']=='disconnected' else 'notCaptured'
            else:
                evidence['state']='suppliedTarget'
                if route=='module':evidence['moduleResolution']=resolve(target,'hardware')
                elif route=='splitModules':
                    for direction in ('input','output'):evidence[direction+'ModuleResolution']=resolve(target[direction+'Module'],'hardware')
                elif route=='channel':evidence.update(moduleResolution=resolve(target['module'],'hardware'),channelResolution='notVerified')
                elif route=='dbMember':evidence.update(blockResolution=resolve(target['block'],'block'),memberResolution='notVerified')
                elif route=='addresses':
                    evidence['plcResolution']=resolve(target['plc'],'plc')
                    for direction in ('input','output'):
                        address=target[direction];evidence[direction+'BitAddress']=None if address['value'] is None or address['unit']=='unknown' else address['value']*{'bit':1,'byte':8}[address['unit']]
                elif route=='startdriveTelegram':
                    assert target['phase']==phase
                    if target['recordId'] not in drives:
                        original,drive_report,drive_review=load_drive(target['recordId']);assert drive_report['reviewId']==packet['reviewId']
                        if 'telegrams' in drive_report:verify_drive(original,drive_report,drive_review)
                        drives[target['recordId']]=drive_report
                    drive=drives[target['recordId']];selected=next((r for r in drive.get('telegrams',[]) if r['phase']==phase and r['telegram']['id']==target['telegramId']),None)
                    hardware=next(n for n in nodes if n['objectPath']==drive['hardwarePath'] and n.get('origin')!='virtual')
                    number=selected['telegram']['number'] if selected else None
                    evidence.update(telegramResolution='unsupportedVersion' if 'telegrams' not in drive else 'missing' if selected is None else 'retainedDeclaration',
                        driveHardwarePath=drive['hardwarePath'],driveHardwareResolution=resolve({k:hardware[k] for k in ('nodeId','objectPath')},'hardware'),driveIdentity=drive['identity'],driveValueDomain=drive[phase+'ValueDomain'],telegramEvidence=selected,
                        telegramChange=next((r for r in drive.get('telegramChanges',[]) if r['id']==target['telegramId']),None),
                        valueDomainComparison='equalDeclarations' if packet[phase]['valueDomain']==drive[phase+'ValueDomain'] else 'differentDeclarations',
                        externalEncoderNumberCheck='outsideDocumentedProfile' if packet[phase]['identity']['technologyType']!='TO_ExternalEncoder' else 'unknown' if number is None else 'documentedNumber' if number in (81,83) else 'differentNumber')
            observations.append(dict(phase=phase,sourceIndex=index,encoder=row,targetEvidence=evidence,contradictions=issues,nativeConnectionVerified=False,targetCompatibilityVerified=False))
    assert observations==report['encoderConnections']
    changes=[]
    for key in sorted(states[0].keys()|states[1].keys(),key=lambda k:k.encode('utf-16-be')):
        before,after=states[0].get(key),states[1].get(key);fields=[]
        if before and after:
            b,a=copy.deepcopy(before),copy.deepcopy(after)
            for row in (b,a):
                if row['route']=='startdriveTelegram' and row['target']:row['target'].pop('phase')
            fields=[k for k in ('provider','interfaceName','state','route','sensorIndex','connectOption','target','unknownReason') if b[k]!=a[k]]
        changes.append(dict(id=key,status='onlyAfter' if before is None else 'onlyBefore' if after is None else 'changed' if fields else 'unchanged',changedFields=fields,identityEqualityVerified=False))
    assert changes==report['encoderChanges'];assert report['summary']['changedEncoderCount']==sum(r['status']!='unchanged' for r in changes)
    return dict(completed=True,nativeExecuted=False,originalRowsChecked=True,independentUnitArithmetic=True,retainedDriveInputsChecked=True)


def loader(root):
    def load(record_id):
        raw=(root/'drive-profile-reviews'/f'{record_id}.json').read_bytes();assert hashlib.sha256(raw).hexdigest()==record_id;record=json.loads(raw)
        assert record['input']['path']=='drive-profile-reviews/'+record['input']['sha256']+'.input'
        raw=(root/record['input']['path']).read_bytes();assert hashlib.sha256(raw).hexdigest()==record['input']['sha256']
        return json.loads(raw),record['report'],json.loads((root/'project-reviews'/f"{record['reviewId']}.json").read_bytes())
    return load


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--fixture',type=Path,required=True);args=parser.parse_args()
    fixture=json.loads((args.fixture/'mcp-fixture.json').read_text(encoding='utf-8'));root=Path(fixture['artifactRoot'])
    report=json.loads((args.fixture/'report.json').read_text(encoding='utf-8'));review=json.loads((root/'project-reviews'/f"{fixture['reviewId']}.json").read_bytes())
    result=verify(fixture['packet'],report,review,loader(root));refused=[]
    for defect in ('units','identity','telegram','change'):
        altered=copy.deepcopy(report)
        if defect=='units':altered['encoderConnections'][3]['targetEvidence']['inputBitAddress']=1
        elif defect=='identity':altered['encoderConnections'][0]['targetEvidence']['moduleResolution']='differentPlc'
        elif defect=='telegram':altered['encoderConnections'][5]['targetEvidence']['telegramEvidence']['telegram']['number']=999
        else:altered['encoderChanges'][0]['status']='changed'
        try:verify(fixture['packet'],altered,review,loader(root))
        except AssertionError:refused.append(defect)
        else:raise AssertionError('Independent verifier accepted altered '+defect)
    result['tamperedResultsRefused']=refused
    (args.fixture/'independent-verification.json').write_text(json.dumps(result,indent=2)+'\n')
    print('PASS independently verified encoder original routes, exact selectors, unit arithmetic, retained telegram inputs and schema.')
