"""Qualify #220 natively: confirmed machine intent on change_prepare is recorded in the hash-bound preview and change status,
recognized when an earlier applied change confirmed it, flagged when it conflicts, and never evaluated. Owned scratch only."""
import argparse
import json
from pathlib import Path
import uuid

from smoke_mcp import Client, ROOT

DECISION = {"id": "door-reset", "source": "maintainer, qualification run", "target": "Servos", "symbols": ["Reset", "DoorOpen"],
            "statement": "Reset is only allowed while the door is closed.", "meaning": "The reset expression is \"Reset\" AND NOT \"DoorOpen\"."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--host', type=Path, default=ROOT / 'src/TiaMcp.Host/bin/Debug/net10.0-windows/TiaMcp.Host.exe')
    parser.add_argument('--worker', type=Path, default=ROOT / 'src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe')
    args = parser.parse_args()
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    client = Client(args.host.resolve(), 180, ['--worker', str(args.worker.resolve()), '--artifacts', str(root)])
    report = {'completed': False, 'steps': []}

    def record(name, data):
        report['steps'].append({'name': name, 'result': data})
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(name, flush=True)
        return data

    def prepare(name, address, intent):
        return client.tool('change_prepare', {'kind': 'tag_create', 'plcPath': plc, 'intent': intent, 'payload': {
            'tagTableName': 'Default tag table', 'name': name, 'dataTypeName': 'Bool', 'logicalAddress': address}})

    def apply(prepared):
        applied = client.tool('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'], 'idempotencyKey': uuid.uuid4().hex,
                                               'checkpointSave': True, 'compile': True, 'save': True})
        assert applied['state'] == 'applied', applied['state']
        return applied

    try:
        client.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'confirmed-intent', 'version': '1'}})
        client.notify('notifications/initialized')
        assert client.tool('tia_start')['ownsPortalProcess'] is True
        record('owned scratch', client.tool('tia_create_scratch'))
        plc = client.tool('project_inventory')['plcs'][0]['path']
        intent = {'decisions': [DECISION], 'observedFacts': ['The scratch PLC has no door switch; DoorOpen is a memory bit.'],
                  'assumptions': ['The reset button is wired to Reset.']}
        first = prepare('Reset', '%M100.0', intent)
        recorded = first['preview']['intent']
        assert recorded['decisions'][0]['statement'] == DECISION['statement'] and recorded['previouslyConfirmed'] == [] and recorded['conflicts'] == []
        assert recorded['verificationScope']['machineSafetyBehavior'].startswith('Not verified'), recorded['verificationScope']
        applied = apply(first)
        status = client.tool('change_status', {'changeId': first['changeId']})
        assert status['preview']['intent'] == recorded, 'intent did not survive into change_status'
        record('intent recorded in the applied change and its status', {'intent': recorded, 'state': applied['state']})

        same = prepare('DoorOpen', '%M100.1', {'decisions': [DECISION]})
        confirmed = same['preview']['intent']
        assert [c['earlierChangeId'] for c in confirmed['previouslyConfirmed']] == [first['changeId']] and confirmed['conflicts'] == [], confirmed
        record('the same decision is recognized as previously confirmed', confirmed['previouslyConfirmed'])
        apply(same)

        changed = prepare('SS1', '%M100.2', {'decisions': [{**DECISION, 'statement': 'Reset is allowed at any time.'}]})
        conflict = changed['preview']['intent']['conflicts']
        assert len(conflict) == 1 and conflict[0]['earlierStatement'] == DECISION['statement'], conflict
        assert any('conflicts with a decision' in w for w in changed['preview']['warnings']), changed['preview']['warnings']
        record('a changed decision is flagged as a conflict', {'conflicts': conflict, 'warnings': changed['preview']['warnings']})

        result = client.request('tools/call', {'name': 'change_prepare', 'arguments': {'kind': 'tag_create', 'plcPath': plc, 'intent': {'guess': 'door switch is X1'},
                                'payload': {'tagTableName': 'Default tag table', 'name': 'Guess', 'dataTypeName': 'Bool', 'logicalAddress': '%M100.3'}}})
        error = (result.get('structuredContent') or {}).get('error') or {}
        assert result.get('isError') and error.get('code') == 'InvalidRequest', error
        record('an unstructured guess is refused', error)
        report['completed'] = True
    finally:
        record('owned worker shutdown', client.close())
        (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
