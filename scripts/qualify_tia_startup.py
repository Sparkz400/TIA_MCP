"""Record a bounded empty owned TIA startup and clean detach, with exact binaries."""
import argparse
import hashlib
import json
import time
from pathlib import Path

from smoke_mcp import Client, ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Release/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--timeout', type=int, default=180)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    if args.report.exists():
        parser.error('Choose a fresh report path; prior evidence is preserved.')
    report = {'completed': False, 'scope': 'Empty worker-owned TIA startup only; no project opened or changed',
              'binaries': {key: {'path': str(value.resolve()), 'sha256': hashlib.sha256(value.read_bytes()).hexdigest()}
                           for key, value in [('host', args.host), ('worker', args.worker)]}, 'steps': []}
    args.report.parent.mkdir(parents=True, exist_ok=True)

    def save():
        args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')

    client = Client(args.host.resolve(), args.timeout, ['--worker', str(args.worker.resolve())])
    owned = False
    before = None
    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                     'clientInfo': {'name': 'tia-startup-qualification', 'version': '1'}})
        client.notify('notifications/initialized')
        for name in ['tia_discover', 'tia_start', 'tia_session_status', 'tia_detach', 'tia_discover']:
            row = {'name': name, 'startedAtUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
            report['steps'].append(row)
            save()
            started = time.monotonic()
            try:
                row['result'] = client.tool(name)
            finally:
                row['elapsedSeconds'] = time.monotonic() - started
                save()
            print('PASS', name, round(row['elapsedSeconds'], 3), flush=True)
            if name == 'tia_start':
                owned = row['result'].get('ownsPortalProcess') is True
            if name == 'tia_detach':
                owned = False
            if name == 'tia_discover':
                observed = {entry['processId']: entry.get('projectPath') for entry in row['result']['processes']}
                if before is None:
                    before = observed
                else:
                    assert all(observed.get(pid) == project for pid, project in before.items()), 'A pre-existing TIA process/project disappeared or changed.'
                    report['preexistingProcessesPreserved'] = True
            if name == 'tia_session_status':
                assert row['result']['ownsPortalProcess'] is True
                assert row['result']['projects'] == []
        report['completed'] = True
    except Exception as error:
        report['failure'] = str(error)
        raise
    finally:
        if owned:
            try:
                report['cleanup'] = client.tool('tia_detach')
            except Exception as cleanup_error:
                report['cleanupError'] = str(cleanup_error)
        client.close()
        report['hostExitCode'] = client.process.returncode
        report['hostLogs'] = client.errors
        save()


if __name__ == '__main__':
    main()
