"""Actual stdio Host MCP surface: server identity, tool titles, output schemas, prompts, progress, resources,
completions, logging and --toolsets."""
import argparse
import base64
import hashlib
import json
import os
import re
import subprocess
import time
from pathlib import Path
from jsonschema import Draft202012Validator
from smoke_mcp import Client, ROOT
from test_product_version import product_version

OUTPUT_CONTRACTS = ['automationml-workflow.v1', 'graph-workflow.v1', 'opc-ua-workflow.v1', 'project-review-import.v1', 'vci-workspace.v1',
                    'opcua-runtime-subscription.v1', 'diagnostic-watch-plan.v1']
REVIEW_TOOLS = {'access_review', 'access_review_page', 'addin_selection_review', 'addin_selection_page', 'connection_review',
                'connection_review_page', 'db_snapshot_review', 'db_snapshot_review_page', 'hmi_component_review',
                'hmi_component_review_page', 'multiuser_observation_review', 'multiuser_observation_page', 'public_certificate_review',
                'public_certificate_review_page', 'safety_evidence_review', 'safety_evidence_report', 'software_unit_review',
                'software_unit_review_page', 'teamcenter_observation_review', 'teamcenter_observation_page', 'technology_profile_review',
                'technology_profile_report', 'web_application_review', 'web_application_review_page'}
TYPED_TOOLS = {'artifact_read', 'artifact_upload_begin', 'artifact_upload_chunk', 'artifact_upload_status', 'artifact_upload_commit',
               'artifact_upload_abort', 'runtime_ua_connect', 'runtime_ua_status', 'runtime_ua_disconnect', 'runtime_ua_read',
               'runtime_ua_browse', 'runtime_ua_credential_binding', 'runtime_web_connect', 'runtime_web_status', 'runtime_web_disconnect',
               'runtime_web_read', 'runtime_web_diagnostics', 'runtime_web_credential_binding', 'testsuite_style_plan_review',
               'testsuite_style_job_status', 'testsuite_style_job_stop_request', 'testsuite_style_job_mark_interrupted'}
REFUSAL = {'error': {'code': 'Refused', 'message': 'refused'}}
ARTIFACT_TEMPLATE, FEATURE_TEMPLATE, REVIEW_TEMPLATE = 'tia://artifacts/{+path}', 'tia://feature-catalog/{featureId}', 'tia://project-reviews/{reviewId}'
CANARY = 'surface-canary-7f3a'
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

    def raw(client, method, params):
        """Returns the response and every notification that arrived before it."""
        client.sequence += 1
        client.process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': client.sequence, 'method': method, 'params': params}) + '\n')
        client.process.stdin.flush()
        notes, deadline = [], time.monotonic() + 60
        while True:
            message = json.loads(client.lines.get(timeout=max(0.01, deadline - time.monotonic())))
            if message.get('id') == client.sequence: return message, notes
            notes.append(message)

    def raw_call(client, name, arguments, meta=None):
        return raw(client, 'tools/call', {'name': name, 'arguments': arguments, **({'_meta': meta} if meta else {})})

    catalog = json.loads((ROOT / 'contracts/feature-catalog.v1.json').read_text(encoding='utf-8'))
    change_kinds = {feature['changeKind'] for feature in catalog['features'] if feature['changeKind']}
    contracts = {name: json.loads((ROOT / 'contracts' / (name + '.schema.json')).read_text(encoding='utf-8')) for name in OUTPUT_CONTRACTS}
    expected_schemas = {tool for contract in contracts.values() for tool in contract['x-tool-contracts']} | REVIEW_TOOLS | TYPED_TOOLS

    client, initialized = start()
    try:
        info = initialized['serverInfo']
        check(info['version'] == product_version() and info['title'] == 'TIA MCP', 'Server reports the product version from Directory.Build.props')
        check(initialized['capabilities']['experimental']['tiaMcp/featureCatalog'] == {'version': catalog['catalogVersion']},
              'Server advertises the bundled catalog version as the data contract version')
        tools = {tool['name']: tool for tool in client.request('tools/list')['tools']}
        default_count = len(tools)
        titles = [tool.get('title') for tool in tools.values()]
        check(all(title and len(title) <= 60 for title in titles) and len(set(titles)) == len(titles), 'Every tool has a short, distinct title')
        with_schema = {name for name, tool in tools.items() if 'outputSchema' in tool}
        check(with_schema == expected_schemas, 'Output schemas exactly for the contract-validated and typed-result tools')
        read_only = {name for name, tool in tools.items() if tool.get('annotations', {}).get('readOnlyHint')}
        report['outputSchemas'] = {'tools': len(with_schema), 'readOnly': len(with_schema & read_only), 'readOnlyTools': len(read_only)}
        validators = {}
        for name in with_schema:
            schema = tools[name]['outputSchema']
            Draft202012Validator.check_schema(schema)
            text = json.dumps(schema)
            check(schema['type'] == 'object' and '$schema' not in schema and 'prefixItems' not in text
                  and all(ref.startswith('#/$defs/') for ref in re.findall(r'"\$ref": "([^"]*)"', text)),
                  name + ' output schema is a self-contained plain object schema without draft-07-incompatible keywords')
            validators[name] = Draft202012Validator(schema)
            validators[name].validate(REFUSAL)
        check(True, 'Every output schema admits the Host refusal envelope')
        for name, arguments in (('opcua_list', {}), ('automationml_list', {}), ('graph_list', {}),
                                ('opcua_reopen', {'recordRef': 'nodeset:' + '0' * 32 + ':' + '0' * 64}),
                                ('automationml_reopen', {'recordRef': 'automationml:' + '0' * 32 + ':' + '0' * 64})):
            response, notes = raw_call(client, name, arguments)
            result = response['result']
            validators[name].validate(result['structuredContent'])
            check(True, name + (' error' if result.get('isError') else ' result') + ' conforms to its advertised output schema')
            check(not any(note.get('method') == 'notifications/message' for note in notes), 'No log notifications before the client sets a level')

        # client.request validates every structured result against the advertised schema (smoke_mcp.Client.check_output).
        selection = {'endpoint': 'opc.tcp://192.0.2.10:4840', 'applicationUri': 'urn:surface:server', 'certificateSha256': 'a' * 64,
                     'authentication': 'username', 'user': 'operator', 'vaultName': 'default'}
        for name, arguments, refused in (
                ('runtime_ua_credential_binding', {'selection': selection}, False),
                ('runtime_web_credential_binding', {'endpoint': 'https://192.0.2.10', 'peerCertificateSha256': 'b' * 64, 'user': 'operator'}, False),
                ('runtime_ua_status', {'attemptId': '0' * 32}, True), ('runtime_web_status', {'attemptId': '0' * 32}, True),
                ('runtime_ua_subscription_status', {'captureId': '0' * 32}, True), ('testsuite_style_job_status', {'jobId': '0' * 32}, True),
                ('access_review_page', {'recordId': '0' * 64}, True), ('diagnostic_watch_plan', {'planId': '0' * 32, 'request': {}}, True),
                ('artifact_read', {'path': '../outside.txt'}, True)):
            result = client.request('tools/call', {'name': name, 'arguments': arguments})
            check(name in with_schema and bool(result.get('isError')) == refused and 'structuredContent' in result,
                  name + (' refusal' if refused else ' result') + ' conforms to its advertised output schema')
        data = json.dumps({'surface': CANARY}).encode()
        upload = client.request('tools/call', {'name': 'artifact_upload_begin', 'arguments': {'fileName': 'surface.json', 'expectedBytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}})['structuredContent']
        client.request('tools/call', {'name': 'artifact_upload_chunk', 'arguments': {'uploadId': upload['uploadId'], 'offset': 0, 'data': base64.b64encode(data).decode()}})
        client.request('tools/call', {'name': 'artifact_upload_status', 'arguments': {'uploadId': upload['uploadId']}})
        committed = client.request('tools/call', {'name': 'artifact_upload_commit', 'arguments': {'uploadId': upload['uploadId']}})['structuredContent']
        stored = os.path.join(committed['hostPath'], 'data.bin')
        chunk = client.request('tools/call', {'name': 'artifact_read', 'arguments': {'path': stored}})['structuredContent']
        check(base64.b64decode(chunk['data']) == data, 'Upload and artifact_read results conform to their typed output schemas')

        templates = {template['uriTemplate'] for template in client.request('resources/templates/list')['resourceTemplates']}
        check(templates == {ARTIFACT_TEMPLATE, FEATURE_TEMPLATE, REVIEW_TEMPLATE}, 'Artifact, saved-review and feature resource templates listed')
        check('tia://feature-catalog' in {resource['uri'] for resource in client.request('resources/list')['resources']}, 'Feature catalog summary listed as a resource')
        summary = json.loads(client.request('resources/read', {'uri': 'tia://feature-catalog'})['contents'][0]['text'])
        check(summary['catalogVersion'] == catalog['catalogVersion'] and len(summary['features']) == len(catalog['features']), 'Catalog summary lists every feature')
        feature = json.loads(client.request('resources/read', {'uri': 'tia://feature-catalog/feature_catalog'})['contents'][0]['text'])
        check([f['featureId'] for f in feature['features']] == ['feature_catalog'], 'Feature template returns one exact feature')
        relative = os.path.relpath(stored, output / 'host-artifacts').replace(os.sep, '/')
        content = client.request('resources/read', {'uri': 'tia://artifacts/' + relative})['contents'][0]
        check(base64.b64decode(content['blob']) == data and content['uri'] == 'tia://artifacts/' + relative, 'Artifact template returns the exact retained bytes')
        for uri in ('tia://artifacts/../outside.txt', 'tia://artifacts/' + str(ROOT / 'README.md').replace(os.sep, '/'), 'tia://project-reviews/' + '0' * 64,
                    'tia://project-reviews/not-a-hash', 'tia://feature-catalog/no_such_feature'):
            response, _ = raw(client, 'resources/read', {'uri': uri})
            check('error' in response and 'result' not in response, 'Resource read refused: ' + uri)
        check(raw(client, 'resources/read', {'uri': 'tia://project-reviews/' + '0' * 64})[0]['error']['code'] == -32002, 'Missing saved review is ResourceNotFound')

        def complete(reference, argument, value):
            return client.request('completion/complete', {'ref': reference, 'argument': {'name': argument, 'value': value}})['completion']['values']
        check(initialized['capabilities'].get('completions') is not None, 'Completions capability advertised')
        check(relative in complete({'type': 'ref/resource', 'uri': ARTIFACT_TEMPLATE}, 'path', relative[:3]), 'Artifact path completes from retained files')
        check(complete({'type': 'ref/resource', 'uri': FEATURE_TEMPLATE}, 'featureId', 'feature_cat') == ['feature_catalog'], 'Feature ID completes from the catalog')
        check(complete({'type': 'ref/resource', 'uri': REVIEW_TEMPLATE}, 'reviewId', '') == [], 'No saved reviews, no review completions')
        check(complete({'type': 'ref/prompt', 'name': 'safe_change'}, 'project', '') == [], 'Free-text prompt arguments get no invented completions')

        check(initialized['capabilities'].get('logging') is not None, 'Logging capability advertised')
        client.request('logging/setLevel', {'level': 'info'})
        response, notes = raw_call(client, 'runtime_ua_status', {'attemptId': CANARY})
        logs = [note['params'] for note in notes if note.get('method') == 'notifications/message']
        time.sleep(0.5)
        while not client.lines.empty():  # Records sent after the result still belong to this call.
            note = json.loads(client.lines.get())
            if note.get('method') == 'notifications/message': logs.append(note['params'])
        check(logs and all(log['level'] in ('info', 'notice', 'warning', 'error') and log.get('logger') for log in logs),
              'Host log records reach the client as notifications/message after logging/setLevel')
        check(not any(CANARY in json.dumps(log) for log in logs), 'Log notifications never echo tool arguments')
        client.request('logging/setLevel', {'level': 'error'})
        time.sleep(0.5)
        while not client.lines.empty(): client.lines.get()  # Records of the level change itself.
        response, notes = raw_call(client, 'feature_catalog', {'featureId': 'feature_catalog'})
        time.sleep(0.5)
        check(not any(note.get('method') == 'notifications/message' for note in notes) and client.lines.empty(), 'Raised level suppresses info records')

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
