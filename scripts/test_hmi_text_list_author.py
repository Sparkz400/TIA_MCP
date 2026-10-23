import copy
import hashlib
import json
import unittest
from pathlib import Path

import yaml
from jsonschema import Draft202012Validator, ValidationError
from hmi_text_list_author_checks import expected_documents, parse_candidate, verify

ROOT = Path(__file__).resolve().parents[1]


class TextListOracleTests(unittest.TestCase):
    def setUp(self):
        self.request = json.loads((ROOT / 'fixtures/hmi/unified-text-list/author-request.json').read_text(encoding='utf-8'))
        self.schema = json.loads((ROOT / 'contracts/unified-text-list-author.v1.schema.json').read_text(encoding='utf-8'))

    def test_original_fixture_is_an_independent_format_reference(self):
        for name, expected in zip(('QualificationTextLists.hmi.yml', 'QualificationTextLists.TextLibrary.hmi.yml'), expected_documents(self.request)):
            self.assertEqual(yaml.safe_load((ROOT / 'fixtures/hmi/unified-text-list' / name).read_bytes()), expected)

    def test_schema_closes_fields_and_rejects_qualification(self):
        Draft202012Validator.check_schema(self.schema)
        validator = Draft202012Validator({**self.schema, '$ref': '#/$defs/request'})
        validator.validate(self.request)
        for edit in (lambda p: p.update(nativeQualified=True), lambda p: p['lists'][0]['entries'][0].update(fromValue=0.1),
                     lambda p: p['texts'][0]['translations'].update({'en-US': 5}), lambda p: p.update(languages=[])):
            value = copy.deepcopy(self.request); edit(value)
            with self.assertRaises(ValidationError): validator.validate(value)

    def test_yaml_parser_refuses_hidden_duplicate_alias_tag_and_multidocument_content(self):
        for text in ('key: 1\nkey: 2\n', 'a: &x [1]\nb: *x\n', 'a: !!str value\n', 'a: 1\n---\nb: 2\n'):
            with self.assertRaises((AssertionError, yaml.YAMLError)): parse_candidate(b'#Version: 2.0\n' + text.encode())

    def test_scalar_preservation_includes_empty_quoted_multiline_and_astral_text(self):
        values = ['','false','null','0123','日本語 😀','\0\r\n\t\x85\u2028\u2029','"\\\n---\n!tag &anchor']
        for value in values:
            raw = b'#Version: 2.0\n' + yaml.safe_dump({'text': value}, allow_unicode=True).encode()
            self.assertEqual(parse_candidate(raw), {'text': value})

    def test_oracle_does_not_confuse_boolean_and_integer(self):
        documents = expected_documents(self.request)
        documents[0]['TextListContainers']['TextList']['ResourceLists']['QualificationStates']['Entries']['StateOff']['Value'] = False
        candidates = [b'#Version: 2.0\n' + yaml.safe_dump(doc).encode() for doc in documents]
        record = dict(schemaVersion='unified-text-list-author-record.v1', input=dict(path='input',sha256='0'*64), files=[dict(fileName=self.request['fileBase']+suffix,bytes=len(data),artifact=dict(path='file'+str(i),sha256=hashlib.sha256(data).hexdigest())) for i,(suffix,data) in enumerate(zip(('.hmi.yml','.TextLibrary.hmi.yml'),candidates))],listCount=1,textCount=2,languageCount=2,nativeExecutable=False,nativeQualified=False,preservationVerified=False,consumerCoverageComplete=False,policy='test')
        with self.assertRaises(AssertionError): verify(self.request,record,candidates)


if __name__ == '__main__': unittest.main()
