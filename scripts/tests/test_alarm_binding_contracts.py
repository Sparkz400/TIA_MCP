"""Independent alarm selection bounds and supplied-evidence qualification limits."""
import copy
import unittest

from jsonschema import ValidationError
from qualify_trace_diagnostics_offline import watch_contracts


class AlarmBindingContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.validators = watch_contracts()

    def observation(self):
        return dict(schemaVersion='alarm-operand.v1', role='condition', blockPath='/FB',
                    instance=dict(dbPath='/DB', memberPath=['Förderer']),
                    memberPath=['Enabled'], datatype='Bool')

    def report(self):
        return dict(schemaVersion='diagnostic-alarm-bindings.v1', reviewId='a'*64,
                    alarmSelector=dict(reviewId='a'*64, nodeId='alarm', objectPath='/alarm'),
                    selectors=[dict(id='condition', signalId='enabled',
                                    operand=dict(nodeId='member', objectPath='/member'), edgeIndices=[1, 3])],
                    bindings=[dict(id='condition', signalId='enabled', operand={'metadata': self.observation()},
                                   relationships=[dict(index=1, edge={}), dict(index=3, edge={})],
                                   signal={}, status='observationUnavailable', declaration=None, storage=None)],
                    allSelectedMatched=False, sourceAuthenticityVerified=False, nativeQualified=False,
                    runtimeAlarmAssociationVerified=False, coverageComplete=False, maxBytes=524288,
                    policy='Supplied observations, no live association.')

    def test_supported_observation_roles_and_instance_depth(self):
        validator = self.validators['alarmOperand']
        for role in ['condition', 'delay', 'parameter', 'status']:
            for depth in [0, 8]:
                value = self.observation()
                value['role'] = role
                value['instance']['memberPath'] = ['Förderer'] * depth
                validator.validate(value)

    def test_observation_rejects_unknown_fields_types_and_depth(self):
        mutations = [lambda v: v.update(role='unknown'), lambda v: v.update(nativeQualified=True),
                     lambda v: v.update(schemaVersion='vendor-xml'), lambda v: v.update(datatype=True),
                     lambda v: v.update(memberPath=[]), lambda v: v.update(memberPath=['x']*17),
                     lambda v: v['instance'].update(memberPath=['x']*9),
                     lambda v: v['instance'].update(dbPath=''),
                     lambda v: v.update(memberPath=['a\x01b']),
                     lambda v: v.update(memberPath=['a\x85b']),
                     lambda v: v.update(memberPath=['x'*129])]
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                value = self.observation(); mutate(value)
                with self.assertRaises(ValidationError): self.validators['alarmOperand'].validate(value)

    def test_selector_bounds_and_exact_endpoint_shape(self):
        validator = self.validators['alarmBindings']
        validator.validate(self.report())
        for key, values in {'id': ['', True, 'x'*65, 'a\x00b'],
                            'signalId': [False, 'a\x7fb'],
                            'edgeIndices': [[], [1, 1], [-1], [50000], [1.5], [True], list(range(9))],
                            'operand': [dict(nodeId='member'), dict(nodeId='member', objectPath='/member', guessed=True)]}.items():
            for value in values:
                with self.subTest(key=key, value=value):
                    report = self.report(); report['selectors'][0][key] = value
                    with self.assertRaises(ValidationError): validator.validate(report)
        for count in [0, 17]:
            report = self.report(); report['selectors'] *= count
            with self.assertRaises(ValidationError): validator.validate(report)

    def test_report_cannot_upgrade_supplied_evidence(self):
        validator = self.validators['alarmBindings']
        validator.validate(self.report())
        for field in ['sourceAuthenticityVerified', 'nativeQualified', 'runtimeAlarmAssociationVerified', 'coverageComplete']:
            with self.subTest(field=field):
                report = self.report(); report[field] = True
                with self.assertRaises(ValidationError): validator.validate(report)
        report = self.report(); report['bindings'][0]['status'] = 'nativeConfirmed'
        with self.assertRaises(ValidationError): validator.validate(report)

    def test_storage_budgets_and_runtime_claims_remain_fixed(self):
        report = self.report()
        storage = dict(candidates=[], gaps=[], truncated=True, maxCandidates=64, maxCallSites=64,
                       maxParameterBindings=256, maxResolutionVisits=1000, runtimeCallContextVerified=False)
        report['bindings'][0]['storage'] = storage
        validator = self.validators['alarmBindings']; validator.validate(report)
        for field, value in [('maxCandidates', 65), ('maxCallSites', 65), ('maxParameterBindings', 257),
                             ('maxResolutionVisits', 1001), ('runtimeCallContextVerified', True),
                             ('gaps', [{}]*101)]:
            with self.subTest(field=field):
                changed = copy.deepcopy(report); changed['bindings'][0]['storage'][field] = value
                with self.assertRaises(ValidationError): validator.validate(changed)


if __name__ == '__main__':
    unittest.main()
