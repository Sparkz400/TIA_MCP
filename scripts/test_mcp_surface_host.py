"""Actual stdio Host MCP surface: server identity, tool titles, output schemas, prompts, progress and --toolsets."""
import argparse
import json
import re
import subprocess
import time
from pathlib import Path
from jsonschema import Draft202012Validator
from smoke_mcp import Client, ROOT

OUTPUT_CONTRACTS = ['automationml-workflow.v1', 'graph-workflow.v1', 'opc-ua-workflow.v1', 'project-review-import.v1', 'vci-workspace.v1']
PROMPT_ARGUMENTS = {'safe_change': {'goal': 'rename tag Start to StartButton'}, 'project_health': {},
                    'block_test': {'behavior': 'Motor_On follows Start'}, 'diagnose_fault': {'symptom': 'conveyor stops'}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    assert not output.exists(), 'Fresh evidence directory required'
    output.mkdir(parents=True)
    host = args.host.resolve()
    report = {'completed': False, 'nativeExecuted': False, 'checks': [], 'shutdowns': []}

    def check(value, label):
        assert value, label
        report['checks'].append(label)

    def start(*extra):
        client = Client(host, 30, ['--no-native-workers', '--artifacts', str(output / 'host-artifacts'), *extra])
        initialized = client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'mcp-surface', 'version': '1'}})
        client.notify('notifications/initialized')
        return client, initialized

    def close(client):
        closed = client.close()
        report['shutdowns'].append(closed)
        check(closed['hostExitCode'] == 0 and not closed['forcedHostStop'] and not closed['workerExits'], 'Clean Host exit without native workers')

    def raw_call(client, name, arguments, meta=None):
        """Returns the response and every notification that arrived before it."""
        client.sequence += 1
        request = {'jsonrpc': '2.0', 'id': client.sequence, 'method': 'tools/call', 'params': {'name': name, 'arguments': arguments}}
        if meta: request['params']['_meta'] = meta
        client.process.stdin.write(json.dumps(request) + '\n')
        client.process.stdin.flush()
        notes, deadline = [], time.monotonic() + 60
        while True:
            message = json.loads(client.lines.get(timeout=max(0.01, deadline - time.monotonic())))
            if message.get('id') == client.sequence: return message, notes
            notes.append(message)

    catalog = json.loads((ROOT / 'contracts/feature-catalog.v1.json').read_text(encoding='utf-8'))
    change_kinds = {feature['changeKind'] for feature in catalog['features'] if feature['changeKind']}
    contracts = {name: json.loads((ROOT / 'contracts' / (name + '.schema.json')).read_text(encoding='utf-8')) for name in OUTPUT_CONTRACTS}
    expected_schemas = {tool for contract in contracts.values() for tool in contract['x-tool-contracts']}

    client, initialized = start()
    try:
        info = initialized['serverInfo']
        check(info['version'] == catalog['catalogVersion'] and info['title'] == 'TIA MCP', 'Server reports the bundled catalog version, never an unearned 1.0')
        tools = {tool['name']: tool for tool in client.request('tools/list')['tools']}
        default_count = len(tools)
        titles = [tool.get('title') for tool in tools.values()]
        check(all(title and len(title) <= 60 for title in titles) and len(set(titles)) == len(titles), 'Every tool has a short, distinct title')
        with_schema = {name for name, tool in tools.items() if 'outputSchema' in tool}
        check(with_schema == expected_schemas, 'Output schemas exactly for the contract-validated workflow tools')
        validators = {}
        for name in with_schema:
            schema = tools[name]['outputSchema']
            Draft202012Validator.check_schema(schema)
            check(schema['type'] == 'object' and '$schema' not in schema, name + ' output schema is a plain object schema')
            validators[name] = Draft202012Validator(schema)
        for name, arguments in (('opcua_list', {}), ('automationml_list', {}), ('graph_list', {}),
                                ('opcua_reopen', {'recordRef': 'nodeset:' + '0' * 32 + ':' + '0' * 64}),
                                ('automationml_reopen', {'recordRef': 'automationml:' + '0' * 32 + ':' + '0' * 64})):
            response, _ = raw_call(client, name, arguments)
            result = response['result']
            validators[name].validate(result['structuredContent'])
            check(True, name + (' error' if result.get('isError') else ' result') + ' conforms to its advertised output schema')

        prompts = {prompt['name']: prompt for prompt in client.request('prompts/list')['prompts']}
        check(set(prompts) == set(PROMPT_ARGUMENTS) and all(prompt.get('title') for prompt in prompts.values()), 'All workflow prompts listed with titles')
        for name, arguments in PROMPT_ARGUMENTS.items():
            text = '\n'.join(message['content']['text'] for message in client.request('prompts/get', {'name': name, 'arguments': arguments})['messages'])
            named = set(re.findall(r'`([a-z][a-z0-9_]+)`', text))
            check(named and all(item in tools or item in change_kinds for item in named), name + ' names only registered tools and change kinds')
            check(all(value in text for value in arguments.values()), name + ' carries its arguments')

        response, notes = raw_call(client, 'feature_catalog', {'featureId': 'feature_catalog'}, {'progressToken': 'surface-1'})
        progress = [note['params'] for note in notes if note.get('method') == 'notifications/progress']
        check(progress and all(item['progressToken'] == 'surface-1' for item in progress) and progress[0]['progress'] == 0,
              'Progress-token call announces itself before its result')
        check(not response['result'].get('isError'), 'Progress filter leaves the tool result unchanged')
        response, notes = raw_call(client, 'feature_catalog', {'featureId': 'feature_catalog'})
        check(not any(note.get('method') == 'notifications/progress' for note in notes), 'No progress after completion and none without a token')
    finally:
        close(client)

    failed = subprocess.run([str(host), '--no-native-workers', '--toolsets', 'nope'], capture_output=True, text=True, timeout=60)
    groups = re.search(r'Valid groups: all, ([a-z, ]+)\.', failed.stderr)
    check(failed.returncode != 0 and groups, 'Unknown toolset refuses startup and names the valid groups')
    groups = groups.group(1).split(', ')

    client, initialized = start('--toolsets', ','.join(groups))
    try:
        check(len(client.request('tools/list')['tools']) == default_count, 'Naming every group advertises exactly the default surface, so every tool has a group')
    finally:
        close(client)

    client, initialized = start('--toolsets', 'opcua')
    try:
        names = {tool['name'] for tool in client.request('tools/list')['tools']}
        check(names and all(name == 'feature_catalog' or name.startswith(('artifact_', 'opcua_')) for name in names)
              and 'artifact_upload_begin' in names and 'opcua_inspect' in names, 'Selected toolset plus base tools only')
        check('toolsets opcua' in initialized['instructions'], 'Instructions name the restricted toolsets')
        check(client.request('prompts/list')['prompts'] == [], 'Prompts whose tools are hidden are not listed')
        response, _ = raw_call(client, 'tia_discover', {})
        check('error' in response or response['result'].get('isError'), 'Hidden tool cannot be called')
    finally:
        close(client)

    client, initialized = start('--toolsets', 'core,simulation')
    try:
        check({prompt['name'] for prompt in client.request('prompts/list')['prompts']} == {'safe_change', 'block_test'}, 'Prompts follow their required toolsets')
    finally:
        close(client)

    report['completed'] = True
    (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"MCP surface: {len(report['checks'])} checks passed")


if __name__ == '__main__':
    main()
