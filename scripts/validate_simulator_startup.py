"""Test native PLCSIM startup on one fresh empty owned virtual CPU, without TIA."""
import argparse
import hashlib
import json
from pathlib import Path
import uuid

from smoke_mcp import Client, ROOT
from validate_simulation import artifact_path
from block_test_runtime import NativeAudit, redact, verify_owned_instance


class StartupAudit(NativeAudit):
    ALLOWED_TOOLS = frozenset({'sim_status', 'sim_create', 'sim_state', 'sim_release'})
    OPTIONAL_TOOLS = frozenset()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", default=ROOT / "artifacts/simulator-startup-validation.json", help="JSON report path inside this repository's artifacts directory.")
    parser.add_argument("--host", type=Path, default=ROOT / "src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe")
    parser.add_argument("--sim-worker", type=Path, help="Exact simulator worker to qualify.")
    args = parser.parse_args()
    report_path = artifact_path(args.report, ".json")
    workspace = report_path.with_suffix('')
    if report_path.exists() or workspace.exists():
        raise FileExistsError('Startup validation requires a new report and artifact directory.')
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report = {"schemaVersion": "simulator-startup-validation.v2",
              "scope": "Fresh empty adapter-owned local Softbus CPU; powerOn/powerOff only, no TIA, project download, RUN or trial activation.",
              "steps": [], "completed": False, "cleanupSucceeded": False,
              "creationId": uuid.uuid4().hex, "creationAttempted": False,
              "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "host": str(args.host.resolve()), "simWorker": str(args.sim_worker.resolve()) if args.sim_worker else None}
    client = audit = instance = None
    cycle_verified = shutdown_verified = False

    def persist():
        report_path.write_text(json.dumps(redact(report, audit.secrets if audit else ()), indent=2), encoding='utf-8')

    def verify_state(result, state):
        observed = verify_owned_instance(result.get('instance'))
        if (observed['id'] != instance['id'] or observed['name'] != instance['name']
                or observed.get('operatingState') != state or observed.get('communicationInterface') != 'Softbus'):
            raise RuntimeError('Exact owned simulator did not reach the requested Softbus state.')

    def change_state(action, state):
        result = audit('sim_state', {'instanceId': instance['id'], 'action': action, 'timeoutMs': 60000})
        verify_state(result, state)
        # The managed PowerOff API returns void; only PowerOn supplies a runtime code.
        expected_code = 'OK' if action == 'powerOn' else None
        if result.get('action') != action or 'runtimeCode' not in result or result['runtimeCode'] != expected_code:
            raise RuntimeError('Fresh empty CPU ' + action + ' returned unexpected completion evidence.')

    try:
        persist()
        extra = ['--artifacts', str(workspace)]
        if args.sim_worker:
            extra += ['--sim-worker', str(args.sim_worker.resolve())]
        # The Host refuses a powerOn whose 60 s timeout and failure powerOff cannot end inside its bridge timeout.
        client = Client(args.host, 300, extra)
        audit = StartupAudit(client, report['steps'], persist)
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "tia-simulator-startup-validation", "version": "0.2.0"}})
        client.notify("notifications/initialized")
        status = audit('sim_status')
        if status.get('available') is not True or status.get('networkMode') != 'Softbus' or status.get('instances') != []:
            raise RuntimeError('Startup validation requires available Softbus with zero registered instances.')
        report['creationAttempted'] = True
        created = audit('sim_create', {'cpuType': 'CPU1511', 'creationId': report['creationId']})
        instance = verify_owned_instance(created.get('instance'))
        verify_state(created, 'Off')
        change_state('powerOn', 'Stop')
        change_state('powerOff', 'Off')
        cycle_verified = True
    except Exception as error:
        report['failure'] = str(error)
    finally:
        try:
            if instance is not None:
                released = audit('sim_release', {'instanceId': instance['id'], 'deleteOwned': True})
                if (released.get('unregistered') is not True or type(released.get('instanceId')) is not int
                        or released['instanceId'] != instance['id'] or released.get('instanceName') != instance['name']):
                    raise RuntimeError('Owned unregistration did not confirm the exact identity.')
                status = audit('sim_status')
                if status.get('available') is not True or status.get('networkMode') != 'Softbus' or status.get('instances') != []:
                    raise RuntimeError('Fresh status did not confirm an empty Softbus registry after cleanup.')
                report['cleanupSucceeded'] = True
        except Exception as error:
            report['cleanupFailure'] = str(error)
        finally:
            try:
                if client is not None:
                    shutdown = client.close()
                    report['shutdown'] = shutdown
                    report['hostExitCode'] = client.process.returncode
                    workers = shutdown.get('workerExits', [])
                    shutdown_verified = (type(shutdown.get('hostExitCode')) is int and shutdown['hostExitCode'] == 0
                             and client.process.returncode == 0 and shutdown.get('forcedHostStop') is False
                             and shutdown.get('stderrEofObserved') is True and len(workers) == 1
                             and workers[0].get('processExitVerified') is True
                             and type(workers[0].get('exitCode')) is int and workers[0]['exitCode'] == 0
                             and workers[0].get('forcedTermination') is False)
            except Exception as error:
                report['shutdownFailure'] = str(error)
            report['completed'] = cycle_verified and report['cleanupSucceeded'] and shutdown_verified
            persist()
        print(str(report_path), flush=True)
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
