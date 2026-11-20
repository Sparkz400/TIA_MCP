"""Native probe of the 20 November changes on owned scratch projects only (no user project is opened or changed).

Openness clients: Host A starts an owned Portal and creates a scratch project in it (a long call); Host B lists that
Portal's attachedSessions with tia_discover while the call runs, tries tia_attach with a short waitForIdleSeconds
(OpennessClientBusy expected if TIA reports the call as active), then attaches with a longer wait after the call and
detaches. Host B only attaches to Host A's owned Portal; it never attaches to another Portal.

UMAC login: the scratch is saved, protected with an administrator user by scripts/native/UmacProjectSeeder (password
on stdin), and its credential (and a wrong one with the same binding) is stored in a dedicated test vault with the
Host's credential-store CLI. project_open is then run without a login, with the right credential (read-write and
read-only), with the wrong one, and on a second, unprotected scratch with a credential bound to it.
The generated password is never printed or written outside the test vault.
"""
import argparse
import json
import secrets
import string
import subprocess
import threading
import time
from pathlib import Path

from smoke_mcp import Client, ROOT

USER = "McpAdmin"


def password():
    # TIA's policy: 8 to 120 characters with a digit, a special character, upper and lower case.
    alphabet = string.ascii_letters + string.digits
    return "Tm#" + "".join(secrets.choice(alphabet) for _ in range(20)) + "a7Q!"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    parser.add_argument('--seeder', type=Path, default=ROOT / 'scripts/native/UmacProjectSeeder/bin/Debug/net48/UmacProjectSeeder.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    host = args.host.resolve()
    vault = secrets.token_hex(16)
    report = {'artifacts': str(root), 'testVault': vault, 'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')
        print(name, json.dumps(data, default=str)[:400], flush=True)
        return data

    def client(label):
        (root / label).mkdir()
        return Client(host, 900, ['--worker', str(args.worker.resolve()), '--artifacts', str(root / label), '--timeout-seconds', '900'])

    def raw(c, name, arguments=None):
        result = c.request('tools/call', {'name': name, 'arguments': arguments or {}})
        return {'isError': bool(result.get('isError')), 'content': result.get('structuredContent')}

    def credential(project, secret):
        binding = {"resource": {"provider": "siemensTiaPortalV21", "authority": project, "ownerId": project,
                                "resourceKind": "projectUmac", "resourceId": USER}, "purpose": "projectUmac", "subjectId": "projectUser"}
        path = root / ('binding-' + secrets.token_hex(4) + '.json')
        path.write_text(json.dumps(binding), encoding='utf-8')
        done = subprocess.run([str(host), '--credential-store', 'create', '--binding', str(path), '--vault-name', vault, '--stdin'],
                              input=secret.encode('utf-8'), capture_output=True, check=True)
        return json.loads(done.stdout)['reference']

    a = client('a')
    b = client('b')
    try:
        # --- Openness clients -------------------------------------------------------------------------------------
        a.tool('tia_start')
        portal = record('host A tia_session_status', a.tool('tia_session_status'))['processId']
        record('host B tia_discover idle', [p for p in b.tool('tia_discover')['processes'] if p['processId'] == portal])
        outcome = {}
        worker = threading.Thread(target=lambda: outcome.update(scratch=a.tool('tia_create_scratch')))
        worker.start()
        time.sleep(8)
        record('host B tia_discover during scratch creation', [p for p in b.tool('tia_discover')['processes'] if p['processId'] == portal])
        record('host B tia_attach waitForIdleSeconds=3 during the call', raw(b, 'tia_attach', {'processId': portal, 'waitForIdleSeconds': 3}))
        busy_attached = not report['steps'][-1]['result']['isError']
        if busy_attached:
            record('host B tia_detach (attached during the call)', b.tool('tia_detach'))
        worker.join()
        record('host A tia_create_scratch', {'completed': 'scratch' in outcome})
        record('host B tia_attach waitForIdleSeconds=30 after the call', raw(b, 'tia_attach', {'processId': portal, 'waitForIdleSeconds': 30}))
        record('host B tia_detach', b.tool('tia_detach'))
        record('host B tia_attach waitForIdleSeconds=61 refused by the Host', raw(b, 'tia_attach', {'processId': portal, 'waitForIdleSeconds': 61}))

        # --- UMAC login -------------------------------------------------------------------------------------------
        project = a.tool('tia_session_status')['projects'][0]['path']
        report['protectedProject'] = project
        record('project_save', a.tool('project_save'))
        record('host A tia_detach', a.tool('tia_detach'))
        right, wrong = password(), password()
        good_ref = credential(project, right)
        bad_ref = credential(project, wrong)
        seeded = subprocess.run([str(args.seeder.resolve()), 'protect', project, USER], input=right.encode('utf-8'), capture_output=True, timeout=900)
        record('UmacProjectSeeder protect', {'exitCode': seeded.returncode, 'output': seeded.stdout.decode('utf-8', 'replace').strip()})
        if seeded.returncode != 0:
            raise RuntimeError('Seeding the protected project failed')

        def login(reference):
            return {'umacUserName': USER, 'umacUserType': 'project', 'umacCredential': reference, 'vaultName': vault}

        a.tool('tia_start')
        record('project_open without login', raw(a, 'project_open', {'path': project}))
        a.tool('tia_detach'); a.tool('tia_start')
        opened = record('project_open with the right credential', raw(a, 'project_open', {'path': project, **login(good_ref)}))
        if not opened['isError']:
            record('project_inventory on the protected project', {'plcs': len(a.tool('project_inventory', {'includeBlocks': False}).get('plcs', []))})
            record('project_close', a.tool('project_close'))
        record('project_open readOnly with the right credential', raw(a, 'project_open', {'path': project, 'readOnly': True, **login(good_ref)}))
        a.tool('tia_detach'); a.tool('tia_start')
        record('project_open with the wrong password', raw(a, 'project_open', {'path': project, **login(bad_ref)}))
        a.tool('tia_detach'); a.tool('tia_start')
        record('project_open with another user name (Host binding check)', raw(a, 'project_open', {'path': project, **login(good_ref), 'umacUserName': 'Someone'}))

        a.tool('tia_create_scratch')
        plain = a.tool('tia_session_status')['projects'][0]['path']
        a.tool('project_save'); a.tool('project_close')
        record('project_open unprotected scratch with a credential', raw(a, 'project_open', {'path': plain, **login(credential(plain, right))}))
        record('host A tia_detach final', a.tool('tia_detach'))
    finally:
        for c in (a, b):
            try:
                c.close()
            except Exception as error:  # noqa: BLE001 - report every cleanup failure, keep closing the rest
                print('close failed', error)
        (root / 'report.json').write_text(json.dumps(report, indent=2, default=str), encoding='utf-8')


if __name__ == '__main__':
    main()
