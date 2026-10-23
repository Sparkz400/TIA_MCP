"""Exercise the production Host's opt-in pipe and unavailable enrollment gate; no test authority is injected."""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import time

from smoke_mcp import Client
from test_addin_receiver import validate


def exchange(discovery, expected_pid, request):
    import msvcrt
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
    kernel.WaitNamedPipeW.restype = wintypes.BOOL
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.GetNamedPipeServerProcessId.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.ULONG)]
    kernel.GetNamedPipeServerProcessId.restype = wintypes.BOOL
    kernel.PeekNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
    kernel.PeekNamedPipe.restype = wintypes.BOOL
    assert discovery['host']['processId'] == expected_pid
    assert discovery['pipeName'] == 'TiaMcp-AddIn-' + discovery['receiverInstanceId']
    name = '\\\\.\\pipe\\' + discovery['pipeName']
    assert kernel.WaitNamedPipeW(name, 5000), ctypes.get_last_error()
    handle = kernel.CreateFileW(name, 0xC0000000, 0, None, 3, 0, None)
    assert handle != ctypes.c_void_p(-1).value, ctypes.get_last_error()
    try:
        pid = wintypes.ULONG()
        assert kernel.GetNamedPipeServerProcessId(handle, ctypes.byref(pid)), ctypes.get_last_error()
        assert pid.value == expected_pid, 'Actual pipe server differs from the owned Host'
        fd = msvcrt.open_osfhandle(handle, os.O_BINARY | os.O_RDWR)
    except BaseException:
        kernel.CloseHandle(handle)
        raise
    with os.fdopen(fd, 'r+b', buffering=0) as stream:
        data = (json.dumps(request) + '\n').encode()
        assert len(data) < 4096 and stream.write(data) == len(data)
        deadline = time.monotonic() + 25
        response = bytearray()
        while b'\n' not in response:
            assert time.monotonic() < deadline, 'Pipe response deadline exceeded'
            available = wintypes.DWORD()
            assert kernel.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None), ctypes.get_last_error()
            if available.value:
                response += stream.read(min(available.value, 8192))
                assert len(response) <= 2 * 1024 * 1024
            else:
                time.sleep(0.01)
        value = json.loads(response.split(b'\n', 1)[0]); validate(value, 'response'); return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    assert os.name == 'nt', 'Actual Windows named pipes are required'
    output = args.output.resolve(); assert not output.exists(); output.mkdir(parents=True)
    artifacts = output / 'host-artifacts'
    discovery_path = artifacts / 'addin-handoffs/receiver.json'
    report = dict(completed=False, nativeExecuted=False, productionEnrollmentAvailable=False, checks=[], shutdowns=[])
    def check(value, label):
        assert value, label
        report['checks'].append(label)
    def start(enabled=True):
        return Client(args.host.resolve(), 30, ['--no-native-workers', '--artifacts', str(artifacts), *(['--addin-handoff'] if enabled else [])])
    def initialize(client):
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'addin-receiver-test', 'version': '1'}})
        client.notify('notifications/initialized')
    def discover(client):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if discovery_path.is_file():
                value = json.loads(discovery_path.read_bytes())
                if value['state'] == 'listening' and value['host']['processId'] == client.process.pid:
                    return value
            assert client.process.poll() is None, client.errors
            time.sleep(0.02)
        raise AssertionError('No discovery for the current owned Host')
    def close(client, expected=0):
        result = client.close(); report['shutdowns'].append(result)
        (output / ('shutdown-' + str(len(report['shutdowns'])) + '.json')).write_text(json.dumps(dict(result=result, stderr=client.errors), indent=2) + '\n', encoding='utf-8')
        check(result['hostExitCode'] == expected and not result['forcedHostStop'] and not result['workerExits'], 'Exact Host exit; no native worker launch')
    client = start(False)
    try:
        initialize(client); check(not discovery_path.exists(), 'Default Host has no receiver or discovery')
    finally:
        close(client)
    previous = None
    for attempt in range(2):
        client = start()
        try:
            initialize(client); discovery = discover(client)
            validate(discovery, 'discovery')
            (output / f'discovery-{attempt}.json').write_text(json.dumps(discovery, indent=2) + '\n', encoding='utf-8')
            check(not discovery['authorityConfigured'] and not discovery['producerAuthenticated'] and not discovery['nativeQualified'], 'Production discovery cannot claim enrollment or native authority')
            check(discovery['maximumConnections'] == 4 and discovery['connectionLifetimeMilliseconds'] == 20000, 'Production bounds are explicit')
            if previous is not None:
                check(discovery['receiverInstanceId'] != previous['receiverInstanceId'] and discovery['pipeName'] != previous['pipeName'], 'Host restart replaces endpoint and receiver instance')
            response = exchange(discovery, client.process.pid, dict(protocolVersion=1, action='status'))
            check(response['success'] and response['result'] == discovery, 'Actual OS-verified production pipe status matches discovery')
            for action in ['begin', 'complete', 'cancel']:
                request = dict(protocolVersion=1, action=action, requestId='a' * 32)
                request.update(dict(reviewId='b' * 64, selectionBase64='e30=') if action == 'begin' else dict(challenge='c' * 64))
                response = exchange(discovery, client.process.pid, request)
                check(not response['success'] and response['error'] == 'AddInEnrollmentUnavailable', action + ' cannot bypass production enrollment')
            check(not any(p.is_dir() for p in discovery_path.parent.iterdir()), 'Refusal does not reserve requests or publish selections')
            listed = client.request('tools/list')['tools']
            check(any(t['name'] == 'addin_selection_review' for t in listed) and not any(t['name'] == 'addin_handoff' for t in listed), 'Existing stdio tools remain available without a new handoff MCP tool')
            check(client.tool('feature_catalog')['schemaVersion'] is not None, 'Normal stdio remains responsive alongside the pipe')
            if attempt == 0:
                competitor = start()
                try:
                    competitor.process.wait(timeout=15)
                finally:
                    close(competitor, 1)
                check(json.loads(discovery_path.read_bytes()) == discovery, 'Competing Host cannot replace current discovery')
                check(exchange(discovery, client.process.pid, dict(protocolVersion=1, action='status'))['success'], 'Original receiver survives competing Host')
            previous = discovery
        finally:
            close(client)
        check(json.loads(discovery_path.read_bytes())['state'] == 'stopped', 'Graceful Host shutdown marks discovery stopped')
    report['completed'] = True
    (output / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f'PASS production Add-In receiver: {len(report["checks"])} checks; actual pipes and Host lifetimes, no enrollment or native execution.')


if __name__ == '__main__':
    main()
