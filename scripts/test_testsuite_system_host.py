"""Check actual qualification-Host TS-Q03 input refusals with both native workers absent."""
import argparse
from pathlib import Path
import tempfile

from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    args = parser.parse_args()
    checks = 0
    with tempfile.TemporaryDirectory(prefix='tsq03-host-') as folder:
        root = Path(folder)
        missing = root / 'absent.exe'
        for enabled in (False, True):
            client = Client(args.host.resolve(), 30, ['--worker', str(missing), '--sim-worker', str(missing), '--artifacts', str(root),
                *(['--testsuite-qualification'] if enabled else [])])
            try:
                client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'q03-input-check', 'version': '1'}})
                client.notify('notifications/initialized')
                tools = {t['name']: t for t in client.request('tools/list')['tools']}
                assert ('qualification_testsuite_prepare' in tools) == enabled
                checks += 1
                if not enabled:
                    continue
                assert 'serverAddress' in tools['qualification_testsuite_prepare']['inputSchema']['properties']
                checks += 1
                valid = {'projectPath': str(root / 'scratch' / 'S.ap21'), 'fixture': 'systemPass', 'idempotencyKey': 'never-dispatch',
                         'simulatorTicket': 'opaque', 'serverAddress': 'opc.tcp://192.168.0.1:4840/'}
                for changed in ({'simulatorTicket': ''}, {'serverAddress': None}, {'serverAddress': 'opc.tcp://localhost:4840/'},
                                {'serverAddress': 'opc.tcp://192.168.0.1:4840/other'}, {'fixture': 'clean'}, {'fixture': 'applicationPass'}, {'fixture': 'UserCase'}):
                    result = client.request('tools/call', {'name': 'qualification_testsuite_prepare', 'arguments': {**valid, **changed}})
                    assert result.get('isError') and result['structuredContent']['error']['code'] == 'InvalidRequest', result
                    checks += 1
            finally:
                receipt = client.close()
                assert receipt['hostExitCode'] == 0 and not receipt['workerExits'] and not receipt['forcedHostStop'], receipt
                checks += 1
    print('PASS TS-Q03 actual Host input/registration boundary: %d checks; no native workers.' % checks)


if __name__ == '__main__':
    main()
