import copy
import json
import unittest

from jsonschema import Draft202012Validator, ValidationError
from hmi_text_list_generation_checks import expected_intent, validate
from hmi_text_list_author_checks import ROOT, expected_documents


class BoundTextListContractTests(unittest.TestCase):
    def setUp(self):
        self.request = dict(schemaVersion='unified-text-list-author-request.v2', profile='unified-bool-binding-text-list.v1',
                            componentReviewId='a' * 64, outputId='states', bindingId='run', previousRecordId=None, retainOutputIds=['manual'],
                            template=dict(fileBase='States', textLibrary='StateLibrary', languages=['en-US', 'de-DE'], defaultLanguage='en-US',
                                          falseText={'en-US': 'Off', 'de-DE': 'Aus'}, trueText={'en-US': 'On', 'de-DE': 'Ein'}))
        self.review = dict(outputs=[dict(id='states', name='States')])

    def test_schema_self_contained_and_strict(self):
        schema = json.loads((ROOT / 'contracts/unified-text-list-author.v2.schema.json').read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema)
        validate(self.request, 'request')
        for edit in (lambda p: p.update(nativeQualified=True), lambda p: p['template'].update(systemList=True),
                     lambda p: p.update(profile='invented'), lambda p: p.update(previousRecordId='invalid'),
                     lambda p: p.update(retainOutputIds=['same', 'same']),
                     lambda p: p['template']['trueText'].update({'en-US': 1})):
            value = copy.deepcopy(self.request); edit(value)
            with self.assertRaises(ValidationError): validate(value, 'request')

    def test_expected_output_uses_original_selected_name_and_fixed_bool_values(self):
        intent = expected_intent(self.request, self.review)
        self.assertEqual(intent['lists'][0]['name'], 'States')
        self.assertEqual(intent['lists'][0]['rangeType'], 'Bool')
        self.assertEqual([e['fromValue'] for e in intent['lists'][0]['entries']], [0, 1])
        self.assertEqual([e['isDefault'] for e in intent['lists'][0]['entries']], [False, False])
        self.review['outputs'][0]['name'] = 'Exact selected name'
        self.assertEqual(expected_intent(self.request, self.review)['lists'][0]['name'], 'Exact selected name')

    def test_language_order_and_scalar_text_are_explicit(self):
        self.request['template']['languages'].reverse()
        self.request['template']['defaultLanguage'] = 'de-DE'
        self.request['template']['trueText']['en-US'] = '日本語 😀\n"false"'
        self.request['template']['trueText']['de-DE'] = ''
        document = expected_documents(expected_intent(self.request, self.review))[1]['TextLibraries']['StateLibrary']
        self.assertEqual(document['Languages'], ['de-DE', 'en-US'])
        self.assertEqual(document['DefaultLanguage'], 'de-DE')
        self.assertEqual(document['Entries']['TrueText']['Text'], ['', '日本語 😀\n"false"'])

    def test_expectation_does_not_alias_template(self):
        intent = expected_intent(self.request, self.review)
        intent['texts'][0]['translations']['en-US'] = 'changed'
        self.assertEqual(self.request['template']['falseText']['en-US'], 'Off')


if __name__ == '__main__':
    unittest.main()
