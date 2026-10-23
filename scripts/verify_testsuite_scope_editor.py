"""Verify the retained native scope-observation packet, not production execution admission.

The screenshots were visually read by an independent operator. This verifier
checks their identity and the native binding; it does not pretend to perform OCR
or turn a caller-authored screenshot description into native authorization.
"""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

from qualify_exact_worker_startup import validate_close, emit, reference
from retained_native_lifecycle import require


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def verify(root):
    root = Path(root).resolve()
    report = load(root / 'report.json')
    before = load(root / '03-qualification_testsuite_scope_probe-result.json')
    after = load(root / '04-qualification_testsuite_scope_probe-result.json')
    visual = load(root / 'ui-scope/observation.json')
    owned = load(root / 'awaiting-scope-observation.json')
    require(report['nativeExecutionInvoked'] is False and report['automaticReplay'] is False,
            'A scope-only packet must not claim execution or automatic replay')
    require(before['processId'] == after['processId'] == visual['processId'] == owned['processId'] == report['nativePid'],
            'Scope observations must bind the original owned process')
    require(before['projectPath'] == after['projectPath'] == owned['projectPath'], 'Project identity changed')
    require(before['sessionEpoch'] == after['sessionEpoch'] and before['workerSha256'] == after['workerSha256'],
            'Owning-worker session changed during the UI observation')
    require(before['servicePresent'] and before['styleExecutorPresent'] and after['servicePresent'] and after['styleExecutorPresent'],
            'Current product services were not observed')
    require(not before['projectModified'] and not after['projectModified'] and
            before['styleExecutionInvoked'] is False and after['styleExecutionInvoked'] is False,
            'Scope observation changed or executed the saved project')
    expected = {'Main': 'OB', 'TiaMcp_EnableGate': 'FB', 'TiaMcp_EnableGate_DB': 'InstanceDB', 'TiaMcp_IsEnabled': 'FC'}
    for observation in (before, after):
        require(len(observation['inventory']) == 4 and
                {r['name']: r['type'].rsplit('.', 1)[-1] for r in observation['inventory']} == expected,
                'The independently captured complete four-block inventory changed')
        require(len(observation['rules']) == 1 and observation['rules'][0]['name'] == 'TiaMcp_CasingAvailability' and
                observation['rules'][0]['scopeRoutes'] == [] and observation['completeEffectiveScope'] is False,
                'Public metadata must retain its actual absence of a scope getter')
    first = Path(before['rules'][0]['definitionPath'])
    last = Path(after['rules'][0]['definitionPath'])
    require(first.read_bytes() == last.read_bytes(), 'Native definition changed around the UI observation')
    document = ET.fromstring(first.read_bytes())
    rule = document.findall('./RuleSet/Rules/Rule')
    require(len(rule) == 1 and rule[0].attrib == {'Name': 'TiaMcp_CamelCase', 'Type': 'CasingRule', 'Enabled': 'True'},
            'The exact enabled casing rule was not captured')
    attributes = {r.attrib['Name']: r.attrib['Value'] for r in rule[0].findall('RuleAttribute')}
    require(attributes['ObjectSelector'] == 'CodeBlock_FB_FC_OB' and attributes['Target'] == 'TargetName' and
            attributes['Casing'] == 'CamelCasing' and attributes['ViolationCondition'] == 'IsNotSet',
            'Native rule applicability differs from the visual interpretation')
    require(sorted(visual['selectedProgramBlocks']) == sorted(expected) and
            sorted(visual['uncheckedBranches']) == sorted(['Software Units', 'PLC-Variablen', 'PLC-Datentypen']) and
            visual['objectSelector'] == 'Codebaustein (FB,FC,OB)' and visual['enabled'] is True and
            visual['checkboxesChangedByAssistant'] is False and visual['executionInvoked'] is False,
            'The retained visual reading differs from the exact observed fixture')
    images = sorted((root / 'ui-scope').glob('scope-*.png'))
    require(len(images) >= 2 and all(p.read_bytes().startswith((b'\x89PNG\r\n\x1a\n', b'\xff\xd8\xff')) for p in images),
            'The original full-window and selector screenshots are missing')
    host_path = Path(report['host']['exe'])
    worker_path = Path(next(r['identity']['exe'] for r in report['close']['tree']['exits'] if r['role'] == 'worker'))
    validate_close(report['close'], report['host'], report['nativePid'], host_path, worker_path)
    initial = load(root / '00-tia_discover-result.json')['processes']
    final = load(root / '06-tia_discover-result.json')['processes']
    require(all(row in final for row in initial), 'A preexisting discovery row was removed or changed')
    arrivals = [row for row in final if row not in initial]
    if arrivals:
        require(report['completed'] is False and report['failure']['message'] == 'Preexisting native inventory changed',
                'The original concurrent-arrival failure must remain visible')
    else:
        require(report['completed'] is True, 'The scope probe did not complete normally')
    return {'schemaVersion': 1, 'kind': 'independently-reviewed-native-ui-scope-observation',
        'projectPath': before['projectPath'], 'processId': before['processId'], 'sessionEpoch': before['sessionEpoch'],
        'snapshotSelectedScopeObserved': True, 'manualVisualReadingRequired': True, 'automatedImageInterpretation': False,
        'screenshotEncoding': 'Original tool-returned JPEG bytes retained under .png filenames; bytes were not transcoded.',
        'selectedScope': sorted(expected), 'applicableScope': sorted(n for n, t in expected.items() if t in ('FB', 'FC', 'OB')),
        'selectedButInapplicable': ['TiaMcp_EnableGate_DB'], 'excludedBranches': visual['uncheckedBranches'],
        'nativeMetadataHasScopeGetter': False, 'beforeAfterDefinitionsEqual': True, 'savedProjectUnmodified': True,
        'ownedNormalExits': [{'role': r['role'], 'processId': r['identity']['pid'], 'exitCode': r['proof']['exitCode']}
                             for r in report['close']['tree']['exits']],
        'originalProbeCompleted': report['completed'], 'preexistingDiscoveryRowsPreserved': True,
        'unrelatedArrivals': arrivals,
        'repeatableOwningWorkerScopeProviderQualified': False, 'currentSessionStillLive': False,
        'nativeExecutionAuthorized': False, 'qualifiedProfiles': [],
        'limits': ['Historical UI observations are not fresh prerequisites for a new session.',
                   'No native executor, preservation, fresh-reopen scope or recovery trial is qualified by this packet.',
                   'The native scope probe failure remains retained; this separate audit verifies owned cleanup and evidence binding.'],
        'artifacts': [reference(p) for p in [root / 'report.json', root / 'admission.json', root / 'ui-scope/observation.json',
            root / '03-qualification_testsuite_scope_probe-result.json', root / '04-qualification_testsuite_scope_probe-result.json', first, last, *images]]}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    value = verify(args.root)
    value['verifier'] = reference(__file__)
    emit(args.out, value)
    print('Verified native scope observation and three normal owned exits; no execution admission.')
