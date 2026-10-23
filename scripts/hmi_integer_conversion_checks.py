"""Independent integer-domain evidence oracle; no production conversion code imported."""
import copy
from jsonschema.exceptions import ValidationError

TYPE_CHANGE = 'PLC declaration type text changed; native binding compatibility needs review.'


def domain(name):
    bits, signed = {'SInt': (8, True), 'USInt': (8, False), 'Int': (16, True),
                    'UInt': (16, False), 'DInt': (32, True), 'UDInt': (32, False)}[name]
    return (-(2 ** (bits - 1)), 2 ** (bits - 1) - 1) if signed else (0, 2 ** bits - 1)


def verify_conversion(row, old, binding, intent, actual):
    selection = row.get('typeConversion')
    if selection is None:
        assert actual is None
        return False
    assert selection['profile'] == 'integer-value-preserving.v1'
    low, high = domain(selection['fromType'])
    target_low, target_high = domain(selection['toType'])
    intervals = []
    for state in old['intent']['lists'][0]['entries'] if old else []:
        a, b = state['fromValue'], state['toValue']
        intervals.append(dict(stateId=state['id'], fromValue=a, toValue=b,
                              targetRepresentable=low <= a <= b <= high and target_low <= a <= b <= target_high))
    fit = bool(old) and all(s['targetRepresentable'] for s in intervals)
    declared = binding['selection']['declaredHmiType']
    valid = (selection['fromType'] != selection['toType'] and old is not None
             and old['profile'] == 'unified-integer-state-text-list.v1'
             and old['componentContext']['binding']['afterDeclaration']['dataType'] == selection['fromType']
             and binding['beforeDeclaration']['status'] == 'capturedDeclaration'
             and binding['beforeDeclaration']['dataType'] == selection['fromType']
             and binding['afterDeclaration']['status'] == 'capturedDeclaration'
             and binding['afterDeclaration']['dataType'] == selection['toType']
             and row['template'].get('dataType') == selection['toType'] and declared == selection['toType']
             and old['intent'] == intent and fit)
    expected = dict(selection=selection, sourceMinimum=low, sourceMaximum=high,
                    targetMinimum=target_low, targetMaximum=target_high, listedIntervals=intervals,
                    allListedValuesRepresentable=fit, fullSourceDomainRepresentable=target_low <= low and high <= target_high,
                    status='checkedListedValues' if valid else 'conflict', sourceBindingConflicts=binding['conflicts'],
                    unlistedValuesVerified=False, runtimeConversionVerified=False, nativeCompatibilityVerified=False)
    assert {k: v for k, v in actual.items() if k != 'conflicts'} == expected
    assert bool(actual['conflicts']) == (not valid)
    return not valid or row['operation'] != 'update' or row['bindingAction'] != 'preserve' or row['profile'] != 'unified-integer-state-text-list.v1'


def negative_controls(request, receipt, read, verify):
    """Original-input and semantic evidence mutations must fail the independent oracle."""
    controls = []
    for field, replacement in [('targetMaximum', 1), ('sourceMinimum', 0), ('allListedValuesRepresentable', False),
                               ('fullSourceDomainRepresentable', False), ('runtimeConversionVerified', True),
                               ('sourceBindingConflicts', []), ('listedIntervals', [])]:
        changed = copy.deepcopy(receipt)
        conversion = changed['outputs'][1]['typeConversion']
        if conversion[field] == replacement:
            replacement = True if replacement is False else -999
        conversion[field] = replacement
        try:
            verify(request, changed, read)
        except (AssertionError, ValueError, ValidationError):
            controls.append(field)
        else:
            raise AssertionError('Independent conversion oracle accepted altered ' + field)
    return controls
