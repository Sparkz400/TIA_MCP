"""Real same-user Windows pipe relay checks. No tasks or Siemens APIs are started."""
import argparse
import base64
import csv
import hashlib
import json
import subprocess
import uuid
from pathlib import Path
from qualify_deployment import McpClient, initialize


def run(bundle, output):
    executable = bundle / 'host/TiaMcp.Host.exe'
    sid = next(csv.reader([subprocess.check_output(['whoami.exe', '/user', '/fo', 'csv', '/nh'], text=True).strip()]))[-1]
    root = output / ('relay-tests-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    results = []
    for label, mutate in [
        ('foreign SID', lambda s: s.update(userSid='S-1-5-18')),
        ('invalid channel', lambda s: s.update(channel='../foreign')),
        ('receipt escape', lambda s: s.update(receiptPath=str(root / 'escaped.json'))),
        ('recursive relay', lambda s: s.update(hostArguments=['--session-relay-server', 'bad'])),
        ('unbounded shutdown', lambda s: s.update(shutdownTimeoutSeconds=999999)),
    ]:
        folder = root / uuid.uuid4().hex
        folder.mkdir()
        settings = dict(channel=uuid.uuid4().hex, userSid=sid, hostArguments=[], receiptPath=str(folder / 'receipt.json'), shutdownTimeoutSeconds=20)
        mutate(settings)
        path = folder / 'relay.json'
        path.write_text(json.dumps(settings), encoding='utf-8')
        result = subprocess.run([str(executable), '--session-relay-client', str(path)], capture_output=True, timeout=15)
        assert result.returncode != 0 and b'Invalid relay' in result.stderr and not result.stdout, (label, result)
        results.append(label)

    for interrupted in (False, True):
        folder = root / uuid.uuid4().hex
        folder.mkdir()
        payload = bytes(range(256)) * 1024
        artifact = folder / 'Gr\u00f6\u00dfe fixture.bin'
        artifact.write_bytes(payload)
        settings = dict(channel=uuid.uuid4().hex, userSid=sid, hostArguments=['--artifacts', str(folder)], receiptPath=str(folder / 'receipt.json'), shutdownTimeoutSeconds=20)
        path = folder / 'relay.json'
        path.write_text(json.dumps(settings), encoding='utf-8')
        config = dict(command=[str(executable), '--session-relay-client', str(path)], environment={}, cwd=None)
        client = McpClient(config, 30)
        server = subprocess.Popen([str(executable), '--session-relay-server', str(path)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            initialize(client)
            piece = client.tool('artifact_read', dict(path=str(artifact), offset=0, count=65536))
            assert base64.b64decode(piece['data']) == payload[:65536]
            assert piece['sha256'] == hashlib.sha256(payload).hexdigest()
            if interrupted:
                client.process.kill()
                client.process.wait(timeout=10)
            closed = client.close()
            if not interrupted:
                assert closed['exitCode'] == 0 and not closed['forcedLauncherStop'], closed
                assert closed['stderrTail'], 'MCP logging was not forwarded separately from protocol stdout.'
            stdout, stderr = server.communicate(timeout=30)
            assert server.returncode == 0, (stdout, stderr)
            receipt = json.loads((folder / 'receipt.json').read_text(encoding='utf-8'))
            assert receipt['sessionId'] > 0 and receipt['userSid'] == sid and receipt['channel'] == settings['channel']
            assert receipt['status'] == 'exited' and receipt['exitCode'] == 0 and receipt['replayCount'] == 0
            assert not receipt.get('forcedOwnedHostStop'), receipt
            results.append('client termination' if interrupted else 'protocol bytes, Unicode path, stderr and clean EOF')
        finally:
            if client.process.poll() is None:
                client.process.kill()
                client.process.wait(timeout=5)
            if server.poll() is None:
                server.kill()
                server.wait(timeout=5)
    report = dict(passed=True, cases=results, evidenceDirectory=str(root), scope='Local same-user Windows pipes; no task creation, Siemens API, VM or native interrupted-write evidence.')
    (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] / 'artifacts')
    args = parser.parse_args()
    run(args.bundle.resolve(), args.output.resolve())
