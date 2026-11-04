"""Actual Host screen-artifact and diagnostic-progress checks; native workers are absent."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import uuid

from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    args = parser.parse_args()
    checks = 0
    with tempfile.TemporaryDirectory(prefix='hmi-screen-host-') as folder:
        root = Path(folder)
        artifacts = root / 'artifacts'
        artifacts.mkdir()
        candidate = artifacts / 'screen.xml'
        raw = b'<Document><Hmi.Screen.Screen /></Document>'
        candidate.write_bytes(raw)
        reference = dict(path=str(candidate), sha256=hashlib.sha256(raw).hexdigest())
        client = Client(args.host.resolve(), 30, ['--worker', str(root / 'absent.exe'),
            '--sim-worker', str(root / 'absent.exe'), '--artifacts', str(artifacts)])

        def refused(payload, code='HmiCandidateUnavailable'):
            nonlocal checks
            result = client.request('tools/call', dict(name='change_prepare', arguments=dict(kind='hmi_classic_screen_edit', payload=payload)))
            assert result.get('isError') and result['structuredContent']['error']['code'] == code, result
            checks += 1

        try:
            client.request('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='screen-input-check', version='1')))
            client.notify('notifications/initialized')
            advertised = {t['name']: t for t in client.request('tools/list', {})['tools']}
            for name, field in [('change_prepare', 'payload'), ('hmi_inspect', 'request'),
                                ('hmi_classic_screen_capture', 'request'), ('hmi_classic_screen_compose', 'request')]:
                assert advertised[name]['inputSchema']['properties'][field]['type'] == 'object'
                checks += 1
            # Valid bytes reach the absent worker; malformed or changed inputs must fail before that boundary.
            refused(dict(xmlArtifact=reference), 'WorkerNotFound')
            refused(dict(xmlArtifact={**reference, 'bytes': len(raw)}), 'WorkerNotFound')
            for size in (None, '12', -1, len(raw) + 1):
                refused(dict(xmlArtifact={**reference, 'bytes': size}))
            refused(dict(xml=raw.decode()), 'WorkerNotFound')
            refused(dict(xmlArtifact=reference, additionalScreens=[dict(target={}, xmlArtifact=reference)]), 'WorkerNotFound')
            for value in (None, 'path', [], {}, dict(path=str(candidate)), dict(sha256=reference['sha256']),
                          dict(path=12, sha256=reference['sha256']), dict(path=str(candidate), sha256=None),
                          dict(path=str(candidate), sha256='z' * 64), {**reference, 'extra': True}):
                refused(dict(xmlArtifact=value))
            for value in (None, [], [None], ['screen'], [{}] * 8):
                refused(dict(xmlArtifact=reference, additionalScreens=value))
            refused(dict(xml=raw.decode(), xmlArtifact=reference))
            refused(dict(xml=None, xmlArtifact=reference))
            refused(dict(xmlArtifact={**reference, 'path': 'screen.xml'}))
            refused(dict(xmlArtifact={**reference, 'sha256': '0' * 64}))
            candidate.write_bytes(raw + b'changed')
            refused(dict(xmlArtifact=reference))
            candidate.write_bytes(b'\xff')
            refused(dict(xmlArtifact=dict(path=str(candidate), sha256=hashlib.sha256(b'\xff').hexdigest())))
            candidate.write_bytes(raw)
            outside = root / 'outside'
            outside.mkdir()
            (outside / candidate.name).write_bytes(raw)
            refused(dict(xmlArtifact={**reference, 'path': str(outside / candidate.name)}))
            link = artifacts / 'linked'
            if os.name == 'nt':
                subprocess.run('cmd.exe /d /v:off /c mklink /J "%TIA_TEST_LINK%" "%TIA_TEST_TARGET%"',
                    env={**os.environ, 'TIA_TEST_LINK': str(link), 'TIA_TEST_TARGET': str(outside)},
                    check=True, capture_output=True, stdin=subprocess.DEVNULL, timeout=30)
            else:
                link.symlink_to(outside, target_is_directory=True)
            try:
                refused(dict(xmlArtifact={**reference, 'path': str(link / candidate.name)}), 'ArtifactPathDenied')
            finally:
                link.rmdir() if os.name == 'nt' else link.unlink()
            assert (outside / candidate.name).read_bytes() == raw
            with candidate.open('wb') as stream:
                stream.truncate(8 * 1024 * 1024 + 1)
            refused(dict(xmlArtifact=reference))
            change_id = uuid.uuid4().hex
            directory = artifacts / 'changes' / ('change-' + change_id)
            directory.mkdir(parents=True)
            progress = client.tool('change_status', dict(changeId=change_id, progressOnly=True))
            assert not progress['progressAvailable'] and not progress['outcomeVerified']
            checks += 1
            (directory / 'progress.json').write_text(json.dumps(dict(changeId=change_id, state='applying', phases=[],
                outcomeVerified=True, automaticRetryAllowed=True)), encoding='utf-8')
            progress = client.tool('change_status', dict(changeId=change_id, progressOnly=True))
            assert progress['progressAvailable'] and not progress['outcomeVerified'] and not progress['automaticRetryAllowed']
            checks += 1
            for state in ['applied', 'not_applied', 'rolled_back', 'recovery_required']:
                (directory / 'journal.json').write_text(json.dumps(dict(changeId=change_id, state=state)), encoding='utf-8')
                progress = client.tool('change_status', dict(changeId=change_id, progressOnly=True))
                assert progress['state'] == state and progress['operationFinished'] == (state != 'recovery_required') and progress['stateSource'] == 'journal'
                assert not progress['outcomeVerified'] and not progress['automaticRetryAllowed']
                checks += 1
        finally:
            receipt = client.close()
            assert receipt['hostExitCode'] == 0 and not receipt['workerExits'] and not receipt['forcedHostStop'], receipt
            checks += 1
    print('PASS Classic screen actual Host: %d checks; no native workers.' % checks)


if __name__ == '__main__':
    main()
