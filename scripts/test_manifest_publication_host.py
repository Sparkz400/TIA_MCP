"""Actual MCP upload state recovery after Windows manifest publication denial."""
import argparse
import base64
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path

from smoke_mcp import Client


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    artifacts = output / 'host-artifacts'
    report = {'completed': False, 'nativeExecuted': False, 'checks': [], 'shutdowns': [],
              'windowsLockChecksExecuted': os.name == 'nt'}

    def check(value, label):
        if not value: raise AssertionError(label)
        report['checks'].append(label)

    client = None
    def start():
        nonlocal client
        client = Client(args.host.resolve(), 30, ['--artifacts', str(artifacts),
                        '--worker', str(output / 'absent.exe'), '--sim-worker', str(output / 'absent.exe')])
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                       'clientInfo': {'name': 'manifest-publication-offline', 'version': '1'}})
        client.notify('notifications/initialized')

    def close():
        nonlocal client
        if client is None: return
        result = client.close(); client = None
        report['shutdowns'].append(result)
        check(result['hostExitCode'] == 0 and not result['forcedHostStop'] and not result['workerExits'], 'Clean Host shutdown without native workers')

    def refuse(tool, arguments, code):
        response = client.request('tools/call', {'name': tool, 'arguments': arguments})
        check(response.get('isError') is True and response['structuredContent']['error']['code'] == code, 'Structured refusal: ' + tool + '/' + code)

    def blocked_manifest(manifest, tool, arguments):
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                      wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]; kernel.CloseHandle.restype = wintypes.BOOL
        # Allow reads/writes, deny delete sharing so the exact manifest rename fails.
        handle = kernel.CreateFileW(str(manifest), 0x80000000, 3, None, 3, 0, None)
        if handle == ctypes.c_void_p(-1).value: raise ctypes.WinError(ctypes.get_last_error())
        original = manifest.read_bytes()
        before = {p.name for p in manifest.parent.glob('*.tmp')}
        try: refuse(tool, arguments, 'PackageInvalid')
        finally:
            if not kernel.CloseHandle(handle): raise ctypes.WinError(ctypes.get_last_error())
        check(manifest.read_bytes() == original, 'Denied publication preserves prior manifest bytes')
        created = [p for p in manifest.parent.glob('*.tmp') if p.name not in before]
        check(len(created) == 1, 'Failure retains one serialized candidate without operation replay')
        return created[0].read_bytes()

    data = b'\x00\xfforiginal supplied bytes\r\n'
    digest = hashlib.sha256(data).hexdigest()
    try:
        start()
        upload = client.tool('artifact_upload_begin', {'fileName': 'evidence.bin', 'expectedBytes': len(data), 'sha256': digest})
        uid = upload['uploadId']
        directory = artifacts / 'library-intake' / 'uploads' / uid
        manifest = directory / 'upload.json'
        chunk = {'uploadId': uid, 'offset': 0, 'data': base64.b64encode(data).decode()}
        if os.name == 'nt':
            intended = blocked_manifest(manifest, 'artifact_upload_chunk', chunk)
            check((directory / 'data.bin').read_bytes() == data, 'Unacknowledged data tail is retained before retry')
            state = client.tool('artifact_upload_status', {'uploadId': uid})
            check(state['state'] == 'receiving' and state['receivedBytes'] == 0, 'Failed chunk does not acknowledge uncommitted bytes')
            refuse('artifact_upload_commit', {'uploadId': uid}, 'UploadIncomplete')
        written = client.tool('artifact_upload_chunk', chunk)
        check(written['receivedBytes'] == len(data) and (directory / 'data.bin').read_bytes() == data, 'Retry reconciles the data tail without duplicate bytes')
        if os.name == 'nt': check(manifest.read_bytes() == intended, 'Successful chunk publishes the exact previously prepared state')
        if os.name == 'nt':
            intended = blocked_manifest(manifest, 'artifact_upload_commit', {'uploadId': uid})
            check(client.tool('artifact_upload_status', {'uploadId': uid})['state'] == 'receiving', 'Failed commit remains uncommitted')
        committed = client.tool('artifact_upload_commit', {'uploadId': uid})
        check(committed['state'] == 'committed', 'Explicit commit retry succeeds after lock release')
        if os.name == 'nt': check(manifest.read_bytes() == intended, 'Commit publishes the exact prepared bytes')
        retained = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.glob('*.tmp')}
        close(); start()
        check(client.tool('artifact_upload_commit', {'uploadId': uid}) == committed, 'Restart reconciles an already committed upload')
        check((directory / 'data.bin').read_bytes() == data, 'Committed original survives restart')
        check({p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.glob('*.tmp')} == retained, 'Restart preserves failed publication evidence')
        other = client.tool('artifact_upload_begin', {'fileName': 'abort.bin', 'expectedBytes': len(data), 'sha256': digest})
        client.tool('artifact_upload_chunk', {**chunk, 'uploadId': other['uploadId']})
        other_dir = artifacts / 'library-intake' / 'uploads' / other['uploadId']
        if os.name == 'nt':
            blocked_manifest(other_dir / 'upload.json', 'artifact_upload_abort', {'uploadId': other['uploadId']})
            check((other_dir / 'data.bin').read_bytes() == data, 'Failed abort cannot delete unacknowledged data')
            check(client.tool('artifact_upload_status', {'uploadId': other['uploadId']})['state'] == 'receiving', 'Failed abort preserves receiving state')
        aborted = client.tool('artifact_upload_abort', {'uploadId': other['uploadId']})
        check(aborted['state'] == 'aborted' and not (other_dir / 'data.bin').exists(), 'Successful explicit abort deletes only its own upload data')
        check((directory / 'data.bin').read_bytes() == data, 'Other committed upload remains unchanged')
        close()
        report['completed'] = True
    finally:
        try: close()
        finally:
            report['count'] = len(report['checks'])
            report['harnessSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
            report['managedBinaryHashes'] = {name: hashlib.sha256((args.host.parent / name).read_bytes()).hexdigest() for name in ('TiaMcp.Host.dll', 'TiaMcp.Core.dll')}
            (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"PASS manifest publication MCP: {report['count']} checks; Windows lock checks={report['windowsLockChecksExecuted']}; no native execution")


if __name__ == '__main__':
    main()
