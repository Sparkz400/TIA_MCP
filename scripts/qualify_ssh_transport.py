"""Real SSH disconnect/reconnect qualification without a native project or mutation."""
import argparse
import json
from pathlib import Path
from qualify_deployment import McpClient, initialize, load_configuration, require


def run(args):
    config = load_configuration(args.config, args.server)
    require(config['transport'] == 'ssh', 'Actual strict SSH configuration is required.')
    report = dict(completed=False, transport='ssh', configurationSha256=config['configurationSha256'], steps=[], mutationsDispatched=0,
                  limitations=['Disconnect occurs on an idle MCP connection; no interrupted Siemens write or recovery is qualified.'])
    clients = []
    def note(name, result):
        report['steps'].append(dict(name=name, passed=True, result=result))
        args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
        print('PASS ' + name, flush=True)
    def connect(label):
        client = McpClient(config, 120)
        clients.append(client)
        note(label + ' initialize', initialize(client))
        return client
    try:
        first = connect('first')
        observer = connect('independent')
        note('first catalog', first.tool('feature_catalog', {'maxItems': 1}))
        first.process.kill()  # Exact local SSH process, never a remote process tree.
        first.process.wait(timeout=10)
        first.terminal = True
        note('owned SSH connection terminated', {'pid': first.process.pid, 'exitCode': first.process.returncode})
        note('independent connection remains usable', observer.tool('feature_catalog', {'maxItems': 1}))
        fresh = connect('fresh')
        state = fresh.tool('tia_session_status')
        require(state.get('attached') is False and state.get('projects') == [], 'Fresh host unexpectedly inherited a TIA attachment.')
        note('fresh connection is detached', state)
        report['completed'] = True
    except Exception as error:
        report['failure'] = str(error)
    finally:
        for client in clients:
            interrupted = client.terminal
            try:
                closed = client.close()
                report.setdefault('shutdown', []).append(closed)
                if not interrupted and (closed['exitCode'] != 0 or closed['forcedLauncherStop']):
                    report['completed'] = False
            except Exception as error:
                report['completed'] = False
                report.setdefault('cleanupErrors', []).append(str(error))
        args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--server', default='tia-v21')
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    raise SystemExit(run(args))
