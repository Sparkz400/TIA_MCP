"""Actual local Host credential commands using disposable synthetic secrets and Windows DPAPI."""
import argparse
import base64
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid
from jsonschema import Draft202012Validator, ValidationError
from smoke_mcp import ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh output required'
    output.mkdir(parents=True)
    vault_name = uuid.uuid4().hex
    secret = b'SYNTHETIC-ONLY-' + uuid.uuid4().hex.encode() + b'\x00\xff\n'
    schema = json.loads((ROOT/'contracts/protected-credential.v1.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(schema)
    def validate(value, kind): Draft202012Validator({**schema, '$ref':'#/$defs/'+kind}).validate(value)
    report = {'completed':False, 'nativeExecuted':False, 'realCurrentUserDpapi':True, 'physicalConsoleTested':False, 'crossUserProfileTested':False, 'checks':[], 'processes':[]}
    def check(ok, label):
        assert ok, label
        report['checks'].append(label)
    def call(command, options=(), data=b'', code=None):
        result = subprocess.run([str(args.host.resolve()), '--credential-store', command, '--vault-name', vault_name, *options], input=data, capture_output=True, timeout=30, cwd=ROOT)
        report['processes'].append({'command':command,'exitCode':result.returncode})
        for stream in (result.stdout, result.stderr):
            check(secret not in stream and base64.b64encode(secret) not in stream and b'SYNTHETIC-SURPLUS' not in stream, 'Command output contains no supplied secret')
        if code:
            check(result.returncode == 1 and not result.stdout and json.loads(result.stderr)['error'] == code, 'Sanitized refusal ' + code)
            return None
        check(result.returncode == 0 and not result.stderr, 'Clean local command exit ' + command)
        return json.loads(result.stdout)
    def save(name, value):
        path = output/name
        path.write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8',newline='\n')
        return str(path)
    binding = {'resource':{'provider':'fixture-provider','authority':'service:fixture','ownerId':'CPU/one','resourceKind':'account','resourceId':'monitoring'},'purpose':'runtime-authentication','subjectId':'reader'}
    binding_path = save('binding.json', binding)
    validate(binding, 'binding')
    try:
        call('create', ['--binding',binding_path], data=secret, code='CredentialInputModeRequired')
        call('create', ['--binding',binding_path,'--stdin','--password','SYNTHETIC-SURPLUS'], data=secret, code='CredentialCommandInvalid')
        call('create', ['--binding',binding_path,'--stdin','--stdin'], data=secret, code='CredentialCommandInvalid')
        bad = copy.deepcopy(binding);bad['password']='SYNTHETIC-SURPLUS'
        call('create', ['--binding',save('bad-binding.json',bad),'--stdin'],data=secret,code='CredentialInputInvalid')
        call('create', ['--binding',binding_path,'--stdin'],data=b'',code='InvalidCredentialLength')
        call('create', ['--binding',binding_path,'--stdin'],data=b'x'*65537,code='InvalidCredentialLength')
        for name, raw in [('duplicate',b'{"purpose":"one","purpose":"two"}'), ('invalid-utf8',b'{"purpose":"\xff"}'), ('missing',b'{}')]:
            malformed=output/(name+'.json');malformed.write_bytes(raw)
            call('create',['--binding',str(malformed),'--stdin'],data=secret,code='CredentialInputInvalid')
        Path(binding_path).write_bytes(b'\xef\xbb\xbf'+Path(binding_path).read_bytes())
        first = call('create', ['--binding',binding_path,'--stdin'],data=secret)
        second = call('create', ['--binding',binding_path,'--stdin'],data=secret)
        for status in (first,second): validate(status,'status')
        check(first['reference'] != second['reference'], 'Repeated local input preserves separate immutable generations')
        check(not first['decryptionVerified'], 'Creating a reference does not claim external or decrypted use')
        reference_path = save('reference.json',first['reference'])
        arguments = ['--binding',binding_path,'--reference',reference_path]
        inspected = call('inspect',arguments);validate(inspected,'status')
        check(inspected['decryptionVerified'] and inspected['state']=='active', 'New process verifies actual DPAPI readability')
        listed = call('list');validate(listed,'list')
        check({v['reference']['credentialId'] for v in listed} == {first['reference']['credentialId'],second['reference']['credentialId']}, 'Public list recovers both exact references')
        check(all(not v['decryptionVerified'] for v in listed), 'List makes no readability claim')
        bad = copy.deepcopy(binding);bad['purpose']='different'
        call('inspect',['--binding',save('wrong-purpose.json',bad),'--reference',reference_path],code='CredentialContextMismatch')
        call('export',arguments,code='CredentialCommandInvalid')
        for name, mutation in [('secret',lambda x:x.update(secretReturned=True)),('purpose',lambda x:x['binding'].update(password='forbidden')),('reference',lambda x:x['reference'].update(credentialId='../escape'))]:
            bad=copy.deepcopy(inspected);mutation(bad)
            try: validate(bad,'status')
            except ValidationError:check(True,'Strict public schema refuses '+name)
            else:raise AssertionError(name)
        retired = call('retire',arguments);validate(retired,'status')
        check(retired['state']=='retired' and not retired['decryptionVerified'], 'Retirement disables local use without claiming external revocation')
        check(call('inspect',arguments)==retired and call('retire',arguments)==retired, 'Retirement survives restart and is idempotent')
        second_path=save('second-reference.json',second['reference'])
        check(call('inspect',['--binding',binding_path,'--reference',second_path])['decryptionVerified'], 'Other generation remains available for recovery')
        private_root=Path(os.environ['LOCALAPPDATA'])/'TiaMcpProtectedCredentials'/vault_name
        lifecycle=private_root/(first['reference']['credentialId']+'.state.json')
        held=lifecycle.with_suffix('.held')
        lifecycle.rename(held)
        try: call('inspect',arguments,code='CredentialUnavailable')
        finally: held.rename(lifecycle)
        check(call('inspect',arguments)==retired, 'Missing lifecycle refuses and restored record remains retired')
        files=list(private_root.iterdir())
        check(len([p for p in files if p.name.endswith('.credential.json')])==2, 'Rejected inputs never retain an encrypted generation')
        for path in files:
            raw=path.read_bytes()
            check(secret not in raw and base64.b64encode(secret) not in raw, 'Retained private store contains no plaintext secret')
        report['privateSyntheticVault']=str(private_root)
        report['retainedCiphertextHashes']={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
        report['completed']=True
    finally:
        report['count']=len(report['checks'])
        report['harnessSha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['binaryHashes']={name:hashlib.sha256((args.host.parent/name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll','TiaMcp.Core.dll','System.Security.Cryptography.ProtectedData.dll')}
        (output/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8',newline='\n')
    print(f"PASS local credential CLI: {report['count']} checks; {len(report['processes'])} clean/exact-refusal process outcomes, no native provider")


if __name__=='__main__':main()
