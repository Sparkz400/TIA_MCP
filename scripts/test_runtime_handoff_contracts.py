"""Check handoff schemas and reports against exact independently read original artifacts."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator, ValidationError

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = ROOT / 'contracts/runtime-handoff.v1.schema.json'
FLAGS = ('nativeExecutable', 'nativeQualified', 'sourceAuthenticityVerified', 'liveFreshnessVerified',
         'deviceIdentityVerified', 'programMatchVerified', 'effectiveRightsVerified', 'operationAdmitted', 'assertionsAdmitted')


def validate(value, kind):
    schema = json.loads(SCHEMA.read_text(encoding='utf-8'))
    Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)


def original(root, reference):
    path = (root / reference['path']).resolve()
    assert path.is_relative_to(root.resolve()), 'Evidence escaped the artifact root'
    raw = path.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == reference['sha256'], 'Original evidence hash mismatch'
    return json.loads(raw)


def verify(root, request, report):
    validate(request, 'request')
    validate(report, 'report')
    assert all(report[k] is False for k in FLAGS)
    assert report['admission'] == 'unavailableWithoutQualifiedNativeEvidence'
    assert report['mutationRecovery'] == 'unknownOutcomeRequiresIndependentReconciliationNoAutomaticReplay'
    for key in ('reviewId', 'accessRecordId', 'accessPhase', 'owner', 'expected'):
        assert report[key] == request[key], key
    access = original(root, {'path': 'access-reviews/' + request['accessRecordId'] + '.json', 'sha256': request['accessRecordId']})
    access_input = original(root, access['input'])
    review = original(root, {'path': 'project-reviews/' + request['reviewId'] + '.json', 'sha256': request['reviewId']})
    assert access_input['reviewId'] == request['reviewId']
    assert access_input['owner'] == request['owner']
    assert sum(n['nodeId'] == request['owner']['nodeId'] for n in review['model']['nodes']) == 1
    assert sum(n.get('objectPath') == request['owner']['objectPath'] for n in review['model']['nodes']) == 1
    evidence = {stage: {role: original(root, request[stage][role]) for role in ('target', 'program', 'session')}
                for stage in ('baseline', 'current')}
    assert report['evidence'] == {stage: request[stage] for stage in evidence}
    assert report['observations'] == evidence, 'Reported observations must equal original bytes parsed as JSON'
    for phase in evidence.values():
        for role, row in phase.items():
            validate(row, role)
    # Evaluate validity from the original input, without consulting reported constraint states.
    expected = request['expected']
    valid = True
    for phase in evidence.values():
        for role, row in phase.items():
            valid &= row['hostId'] == expected['hostId'] and row['context'] == expected['context'] and row['freshness'] == 'current'
            if role != 'session':
                wanted = expected[role + 'Identity']
                valid &= wanted is not None and row['identity'] == wanted
        session = phase['session']
        valid &= all(session[k] == expected[k] and session[k] is not None for k in
                     ('subjectId', 'clientId', 'credential', 'securityDomain', 'securityMode'))
        valid &= session['state'] == 'connected' and all(session[k] for k in ('authenticated', 'peerVerified', 'permissionsComplete'))
    before, after = evidence['baseline'], evidence['current']
    for role in before:
        a, b = before[role], after[role]
        valid &= b['sequence'] >= a['sequence']
        valid &= b['sequence'] != a['sequence'] or a == b
        valid &= a['sourceKind'] == b['sourceKind']
    def permissions(session):
        return {(p['id'], p['scope']): p['outcome'] for p in session['permissions']}
    old, new = permissions(before['session']), permissions(after['session'])
    valid &= old == new
    assert len(report['permissions']) == len(request['operations'])
    entities = {e['id']: e for e in access_input[request['accessPhase']]['entities']}
    pending, seen, grants = [access_input['subjectId']], set(), set()
    while pending:
        entity_id = pending.pop()
        if entity_id in seen or entity_id not in entities:
            continue
        seen.add(entity_id)
        entity = entities[entity_id]
        grants.update((r['id'], r['scope']) for r in entity['rights'])
        pending.extend(entity['inherits'])
    for operation, result in zip(request['operations'], report['permissions']):
        key = operation['id'], operation['scope']
        outcomes = old.get(key, 'unknown'), new.get(key, 'unknown')
        matches = outcomes == (operation['requiredOutcome'],) * 2
        valid &= matches
        assert result['operation'] == operation and result['operationAdmitted'] is False
        assert (result['baselineOutcome'], result['currentOutcome']) == outcomes
        assert result['publicGrantDeclared'] == (key in grants)
        assert result['state'] == ('unknown' if 'unknown' in outcomes else 'matchesDeclaredExpectation' if matches else 'mismatch')
    assert report['summary']['requiresFreshEvidence'] == (not valid)
    assert report['summary']['state'] == ('consistentDeclarations' if valid else 'requiresFreshEvidence')
    constraints = {(c['stage'], c['kind']): c['state'] for c in report['constraints']}
    assert len(constraints) == len(report['constraints']), 'Duplicate constraint'
    assert report['summary']['constraintCount'] == len(constraints)
    assert report['summary']['operationCount'] == len(request['operations'])
    # Every reported constraint must be explained by its original evidence, including failures
    # that do not change an already-invalid aggregate verdict.
    expected_constraints = {}
    def match(stage, name, a, b):
        expected_constraints[stage, name] = 'unknown' if a is None or b is None else 'equalDeclarations' if a == b else 'mismatch'
    for stage, phase in evidence.items():
        for role, row in phase.items():
            match(stage, role + '.host', expected['hostId'], row['hostId'])
            for key in ('endpoint', 'connectionId', 'epoch', 'peerCertificateSha256'):
                match(stage, role + '.' + key, expected['context'][key], row['context'][key])
            expected_constraints[stage, role + '.freshness'] = 'declaredCurrent' if row['freshness'] == 'current' else row['freshness']
        for role in ('target', 'program'):
            match(stage, role + 'Identity', expected[role + 'Identity'], phase[role]['identity'])
        session = phase['session']
        for key in ('subjectId', 'clientId', 'credential', 'securityDomain', 'securityMode'):
            match(stage, key, expected[key], session[key])
        expected_constraints[stage, 'sessionState'] = 'declaredConnected' if session['state'] == 'connected' else 'unavailable'
        for key in ('peerVerified', 'authenticated', 'permissionsComplete'):
            expected_constraints[stage, key] = 'declaredTrue' if session[key] else 'unknown'
    for role in before:
        a, b = before[role], after[role]
        expected_constraints['transition', role + '.sequence'] = 'regressed' if b['sequence'] < a['sequence'] else 'nondecreasingDeclaration'
        for key, suffix in (('sourceKind', 'sourceKind'), ('hostId', 'host'), ('context', 'context')):
            match('transition', role + '.' + suffix, a[key], b[key])
        if role != 'session':
            match('transition', role + '.identity', a['identity'], b['identity'])
        if a['sequence'] == b['sequence']:
            match('transition', role + '.sameSequence', a, b)
    for key in ('subjectId', 'clientId', 'credential', 'securityDomain', 'securityMode', 'permissionsComplete'):
        match('transition', 'session.' + key, before['session'][key], after['session'][key])
    match('transition', 'permissions', old, new)
    assert constraints == expected_constraints, 'Constraint is missing, extra or unsupported by original evidence'
    assert report['summary']['publicAccessPhaseKind'] == ('observation' if request['accessPhase'] == 'before' else access_input['afterKind'])
    assessment = next(a for a in access['report']['assessments'] if a['phase'] == request['accessPhase'])
    assert report['summary']['publicClosureComplete'] == assessment['suppliedClosureComplete']


class HandoffContractTests(unittest.TestCase):
    def test_strict_contracts_and_non_authority(self):
        schema = json.loads(SCHEMA.read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema)
        for name, value in schema['$defs'].items():
            if value.get('type') == 'object':
                self.assertIs(value['additionalProperties'], False)
                if name.endswith('Input'):
                    self.assertLessEqual(set(value['required']), set(value['properties']))
                else:
                    # The owned-PLCSIM binding is optional so retained requests and reports keep their original bytes.
                    optional = {'request': {'plcsimBinding'}, 'report': {'targetBinding'}}.get(name, set())
                    self.assertEqual(set(value['required']), set(value['properties']) - optional)
        for key in FLAGS:
            self.assertEqual(schema['$defs']['report']['properties'][key], {'const': False})
        for role in ('target', 'program', 'session'):
            self.assertEqual(schema['$defs'][role]['properties']['sourceKind']['enum'], ['supplied', 'synthetic'])

    def test_actual_prior_access_bytes(self):
        fixture = ROOT / 'fixtures/access-review/retained-v1'
        manifest = json.loads((fixture / 'fixture.json').read_text(encoding='utf-8'))
        self.assertEqual(len(manifest['records']), 2)
        self.assertIs(manifest['nativeExecuted'], False)
        for row in manifest['files']:
            raw = (fixture / 'store' / row['path']).read_bytes()
            self.assertEqual(len(raw), row['bytes'])
            self.assertEqual(hashlib.sha256(raw).hexdigest(), row['sha256'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture', type=Path, required=True)
    args = parser.parse_args()
    fixture = args.fixture.resolve()
    root = fixture.parent
    cases = json.loads((fixture / 'cases.json').read_text(encoding='utf-8'))
    for case in cases:
        verify(root, case['request'], case['report'])
        assert case['report']['summary']['requiresFreshEvidence'] == case['expectedInvalid']
    request = json.loads((fixture / 'request.json').read_text(encoding='utf-8'))
    report = json.loads((fixture / 'report.json').read_text(encoding='utf-8'))
    verify(root, request, report)
    access_path = root / 'access-reviews' / (request['accessRecordId'] + '.json')
    access = json.loads(access_path.read_bytes())
    referenced_bytes = len((fixture / 'request.json').read_bytes()) + len(access_path.read_bytes())
    referenced_bytes += len((root / access['input']['path']).read_bytes())
    referenced_bytes += len((root / 'project-reviews' / (request['reviewId'] + '.json')).read_bytes())
    referenced_bytes += sum(len((root / reference['path']).read_bytes()) for stage in ('baseline', 'current') for reference in request[stage].values())
    assert report['summary']['referencedBytes'] == referenced_bytes, 'Original byte accounting differs'
    mutations = {
        'invented program': lambda r: r['observations']['current']['program']['identity'].update(value='another'),
        'wrong permission': lambda r: r['permissions'][0].update(currentOutcome='denied'),
        'wrong constraint': lambda r: next(c for c in r['constraints'] if c['kind'] == 'programIdentity').update(state='mismatch'),
        'wrong summary': lambda r: r['summary'].update(requiresFreshEvidence=True),
        'native admission': lambda r: r.update(nativeExecutable=True),
        'operation admission': lambda r: r['permissions'][0].update(operationAdmitted=True),
        'assertion admission': lambda r: r.update(assertionsAdmitted=True),
        'substituted source': lambda r: r['evidence']['current'].update(program=r['evidence']['current']['target']),
    }
    for name, mutate in mutations.items():
        bad = copy.deepcopy(report)
        mutate(bad)
        try:
            verify(root, request, bad)
        except (AssertionError, ValidationError):
            pass
        else:
            raise AssertionError('Oracle accepted ' + name)
    result = {'completed': True, 'nativeExecuted': False, 'caseCount': len(cases), 'mutationControls': list(mutations)}
    (fixture / 'independent-verification.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(f'PASS handoff originals: {len(cases)} cases, {len(mutations)} mutation controls')


if __name__ == '__main__':
    main()
