"""Validate retained TS-03 review/job contracts without native code or network resolution."""
import argparse
import copy
import json
from pathlib import Path
from jsonschema import Draft202012Validator, ValidationError
from referencing import Registry, Resource


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    assert not args.report.exists(), 'Use a fresh report path'
    root = Path(__file__).resolve().parents[1]
    registry = Registry()
    for path in (root / 'contracts').glob('*.json'):
        schema = json.loads(path.read_text(encoding='utf-8'))
        if isinstance(schema, dict) and '$schema' in schema and '$id' in schema:
            resource = Resource.from_contents(schema)
            registry = registry.with_resource(schema['$id'], resource).with_resource('https://tia-mcp.local/contracts/' + path.name, resource)
    def validator(name):
        schema = json.loads((root / 'contracts' / name).read_text())
        Draft202012Validator.check_schema(schema)
        return Draft202012Validator(schema, registry=registry)
    fixture = json.loads(args.fixture.read_text(encoding='utf-8'))
    artifacts = Path(fixture['artifactRoot'])
    review = json.loads((artifacts / fixture['review']['relativePath']).read_text(encoding='utf-8'))
    job = json.loads((artifacts / 'testsuite-jobs' / (fixture['jobId'] + '.json')).read_text(encoding='utf-8'))
    review_validator = validator('testsuite-style-review.v1.schema.json')
    job_validator = validator('testsuite-style-job.v1.schema.json')
    review_validator.validate(review)
    job_validator.validate(job)
    cases = []
    def reject(v, document, label):
        try:
            v.validate(document)
        except ValidationError:
            cases.append(label)
            return
        raise AssertionError(label)
    changed = copy.deepcopy(review); changed['runs'] = []; reject(review_validator, changed, 'empty queue')
    changed = copy.deepcopy(review); changed['runs'] *= 33; reject(review_validator, changed, 'queue limit')
    changed = copy.deepcopy(review); changed['runs'][0]['run']['nativeExecutionAuthorized'] = True; reject(review_validator, changed, 'native authorization claim')
    changed = copy.deepcopy(review); changed['runs'][0]['run']['executionMode'] = 'endpoint'; reject(review_validator, changed, 'endpoint run')
    changed = copy.deepcopy(review); changed['runs'][0]['operation']['operation'] = 'testSuite.system.run'; reject(review_validator, changed, 'nonstyle operation')
    changed = copy.deepcopy(review); changed['runs'][0]['operation']['inputs'].pop(); reject(review_validator, changed, 'missing definition bundle')
    changed = copy.deepcopy(review); changed['stopSemantics'] = 'cancel'; reject(review_validator, changed, 'invented cancellation')
    changed = copy.deepcopy(job); changed['operationIds'] *= 2; reject(job_validator, changed, 'duplicate operation')
    changed = copy.deepcopy(job); changed['nativeExecutionAuthorized'] = True; reject(job_validator, changed, 'extra authorization field')
    names = {row['featureId'] for row in json.loads((root / 'contracts/feature-catalog.v1.json').read_text(encoding='utf-8'))['features'] if row['domain'] == 'test_suite'}
    mappings = {row['featureId']: row['obligations'] for row in json.loads((root / 'contracts/api-coverage/registry.v1.json').read_text(encoding='utf-8'))['featureObligations']}
    assert len([name for name in names if name.startswith('testsuite_style_')]) == 10
    assert all(mappings[name] == ['D06'] for name in names if name.startswith('testsuite_style_'))
    report = {'validDocuments': 2, 'negativeChecks': len(cases), 'negativeCases': cases, 'registeredTs03Tools': 10, 'nativeExecuted': False, 'nativeQualified': False}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('PASS TS-03 contracts: 2 valid documents, 9 rejected adversaries, 10 catalog/D06 mappings')


if __name__ == '__main__':
    main()
