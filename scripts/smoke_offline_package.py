"""Validate a portable bundle, its external configuration and native refusal over real MCP."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import zipfile

from smoke_mcp import Client
from smoke_package import verify_catalog


def digest(data):
    return hashlib.sha256(data).hexdigest()


def verify_explorer_assets(bundle):
    directory=bundle/'ui/project-explorer'
    manifest=json.loads((directory/'index.html.manifest.json').read_bytes())
    names=['index.template.html','style.css','model.js','client.js','explorer.js']
    assert [row['path'] for row in manifest['assets']]==names, 'Explorer source manifest is incomplete'
    for row in manifest['assets']:
        raw=(directory/row['path']).read_bytes()
        assert len(raw)==row['bytes'] and digest(raw)==row['sha256'], 'Stale Explorer asset: '+row['path']
    html=(directory/'index.html').read_bytes()
    assert len(html)==manifest['bytes'] and digest(html)==manifest['sha256'], 'Explorer entry point changed'
    assert manifest['entryPoint']=='index.html' and manifest['snapshotId'] is None and manifest['networkRequired'] is False


def verify_files(bundle, archive):
    verify_explorer_assets(bundle)
    archive_hash = digest(archive.read_bytes())
    assert archive.with_suffix('.zip.sha256').read_text(encoding='utf-8-sig').strip() == archive_hash + ' *' + archive.name
    manifest = json.loads((bundle / 'manifest.json').read_text(encoding='utf-8-sig'))
    assert manifest['bundleKind'] == 'offline' and manifest['frameworkDependent'] is True
    names = {entry['path'] for entry in manifest['files']}
    assert len(names) == len(manifest['files'])
    assert {p.relative_to(bundle).as_posix() for p in bundle.rglob('*') if p.is_file()} == names | {'manifest.json'}
    with zipfile.ZipFile(archive) as zipped:
        archive_names = [entry.filename for entry in zipped.infolist() if not entry.is_dir()]
        assert len(archive_names) == len(set(archive_names))
        assert set(archive_names) == names | {'manifest.json'}
        assert zipped.read('manifest.json') == (bundle / 'manifest.json').read_bytes()
        for entry in manifest['files']:
            path = (bundle / entry['path']).resolve()
            assert path.is_relative_to(bundle)
            data = path.read_bytes()
            assert len(data) == entry['bytes'] and digest(data) == entry['sha256']
            assert zipped.read(entry['path']) == data
    dependencies = json.loads((bundle / 'host/TiaMcp.Host.deps.json').read_text(encoding='utf-8'))
    assert not any(name.lower().startswith('opcfoundation.netstandard.opc.ua.server/')
                   for name in dependencies['libraries']), 'Test server leaked into Host dependencies'
    assert not any(name.lower() == 'host/opc.ua.server.dll' for name in names), 'Test server binary leaked into Host'
    packages = json.loads((bundle / 'third-party/host-packages.json').read_text(encoding='utf-8-sig'))['packages']
    assert {row['id'] + '/' + row['version'] for row in packages} == {
        name for name, value in dependencies['libraries'].items() if value['type'] == 'package'}
    for package in packages:
        assert package['license'] and len(package['files']) >= 2
        for notice in package['files']:
            assert notice['path'] in names and digest((bundle / notice['path']).read_bytes()) == notice['sha256']
    return dict(manifestSha256=digest((bundle / 'manifest.json').read_bytes()),
                archiveSha256=archive_hash, fileCount=len(names), dependencyPackages=len(packages))


def run(bundle, configuration, archive, output, git_executable):
    output.mkdir(parents=True, exist_ok=False)
    report = dict(completed=False, nativeExecuted=False, steps=[], hostExits=[],
                  harnessSha256=digest(Path(__file__).read_bytes()))
    client = None

    def stop():
        nonlocal client
        connection, client = client, None
        if connection is None:
            return
        receipt = connection.close()
        report['hostExits'].append(receipt)
        assert receipt['hostExitCode'] == 0 and not receipt['forcedHostStop'] and receipt['stderrEofObserved'], receipt
        assert not receipt['workerExits'], receipt

    def exercise(command):
        nonlocal client
        client = Client(None, 60, command=command)
        initialized = client.request('initialize', dict(protocolVersion='2025-11-25',
            capabilities={'extensions': {'io.modelcontextprotocol/ui': {'mimeTypes': ['text/html;profile=mcp-app']}}},
            clientInfo=dict(name='offline-bundle', version='1')))
        assert 'native workers disabled' in initialized['instructions']
        manifest = json.loads((bundle / 'manifest.json').read_text(encoding='utf-8-sig'))
        assert initialized['serverInfo']['version'] == manifest['productVersion'], 'Packaged Host and manifest name different product versions'
        client.notify('notifications/initialized')
        names = {row['name'] for row in client.request('tools/list')['tools']}
        assert not any(name.startswith('qualification_') for name in names)
        catalog = json.loads((bundle / 'contracts/feature-catalog.v1.json').read_text(encoding='utf-8'))
        report['steps'].append(verify_catalog(client, catalog, names))
        resource = client.request('resources/read', {'uri': 'ui://tia-project-explorer/view.html'})
        assert len(resource['contents'][0]['text']) > 1000
        for name in ('tia_discover', 'sim_status', 'tia_discover', 'sim_status'):
            result = client.request('tools/call', dict(name=name, arguments={}))
            assert result.get('isError') and result['structuredContent']['error']['code'] == 'NativeWorkersDisabled', result
        assert client.tool('trace_capabilities')['targetProbed'] is False
        report['steps'].append('catalog, Explorer resource and repeated engineering/simulator refusal; session still usable')

    try:
        report['integrity'] = verify_files(bundle, archive)
        configuration_bytes = configuration.read_bytes()
        config = json.loads(configuration_bytes.decode('utf-8-sig'))['mcpServers']['tia-v21']
        report['configurationSha256'] = digest(configuration_bytes)
        exercise([config['command'], *config['args']])
        data = b'\xef\xbb\xbfportable\r\n\x00\xff'
        upload = client.tool('artifact_upload_begin', dict(fileName='source.bin', expectedBytes=len(data), sha256=digest(data)))
        client.tool('artifact_upload_chunk', dict(uploadId=upload['uploadId'], offset=0, data=base64.b64encode(data).decode()))
        upload = client.tool('artifact_upload_commit', dict(uploadId=upload['uploadId']))
        preview = client.tool('vci_prepare', dict(files=[dict(relativePath='PLC/Über Motor.s7dcl', uploadRef=upload['artifactRef'])],
            author=dict(name='Portable package test', email='fixture@example.invalid'), message='portable package fixture'))
        revision = client.tool('vci_commit', dict(previewRef=preview['recordRef'], reviewedPlanSha256=preview['planSha256']))
        assert revision['executableSha256'] == digest(git_executable.read_bytes())
        client.tool('artifact_upload_release', dict(uploadId=upload['uploadId']))
        stop()
        exercise([config['command'], *config['args']])
        assert client.tool('vci_reopen', dict(recordRef=revision['recordRef']))['commit'] == revision['commit']
        downloaded = client.tool('vci_file_read', dict(recordRef=revision['recordRef'], relativePath='PLC/Über Motor.s7dcl'))
        assert base64.b64decode(downloaded['data'], validate=True) == data
        report['steps'].append('configured external Git commits exact uploaded bytes and retains them across launcher restart')
        stop()
        # Existing executables make refusal independent of missing native files.
        # No --no-native-workers: the portable binary itself must disable workers.
        host = bundle / 'host/TiaMcp.Host.exe'
        exercise([str(host), '--artifacts', str(output / 'direct-artifacts'),
                  '--worker', sys.executable, '--sim-worker', sys.executable])
        stop()
        for option in ('--diagnose', '--component-qualification', '--testsuite-scope-probe', '--translation-qualification'):
            result = subprocess.run([str(host), option], capture_output=True, text=True, encoding='utf-8', timeout=20)
            assert result.returncode != 0 and 'Native diagnosis and qualification cannot run' in result.stderr, result
        report['steps'].append('direct portable Host disables existing executable paths and refuses native qualification flags')
        assert verify_files(bundle, archive) == report['integrity']
        report['completed'] = True
    except Exception as error:
        report['failure'] = repr(error)
        raise
    finally:
        try:
            stop()
        finally:
            (output / 'verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('PASS portable bundle hashes, ZIP, notices, configuration, native refusal, local Git, resources and restart')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('bundle', 'configuration', 'archive', 'output', 'git-executable'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    run(args.bundle.resolve(), args.configuration.resolve(), args.archive.resolve(), args.output.resolve(), args.git_executable.resolve())
