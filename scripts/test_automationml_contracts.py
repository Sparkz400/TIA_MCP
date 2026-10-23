"""Portable contract and pinned independent AutomationML fixture checks."""
import hashlib
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator, ValidationError
from test_automationml_host_mcp import verify_fixture_structure

ROOT = Path(__file__).resolve().parents[1]


class AutomationMlContracts(unittest.TestCase):
    def test_pinned_sources_are_exact(self):
        rows = json.loads((ROOT / 'third-party/automationml/sources.json').read_text(encoding='utf-8'))
        self.assertEqual(len(rows), 11)
        for row in rows:
            with self.subTest(path=row['localPath']):
                raw = (ROOT / row['localPath']).read_bytes()
                self.assertEqual(len(raw), row['bytes'])
                self.assertEqual(hashlib.sha256(raw).hexdigest(), row['sha256'])
                self.assertTrue(row['url'].startswith('https://raw.githubusercontent.com/AutomationML/'))
                self.assertRegex(row['url'], r'/[a-f0-9]{40}/')

    def test_contracts_bind_all_six_tools_and_refuse_native_claim(self):
        schema = json.loads((ROOT / 'contracts/automationml-workflow.v1.schema.json').read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema)
        self.assertEqual(set(schema['x-tool-contracts']), {'automationml_'+suffix for suffix in
                         ['inspect', 'compare', 'reopen', 'report_page', 'artifact_read', 'list']})
        validator = Draft202012Validator(schema['$defs']['list'])
        good = dict(rows=[], total=0, nextOffset=None, ordering='record-id; relist after additions', nativeQualified=False)
        validator.validate(good)
        with self.assertRaises(ValidationError): validator.validate(dict(good, nativeQualified=True))

    def test_independent_oracle_detects_dropped_declarations(self):
        raw = b'<CAEXFile><AdditionalInformation><InternalElement ID="opaque"/></AdditionalInformation><InstanceHierarchy Name="P"><InternalElement ID="i" Name="D"/></InstanceHierarchy></CAEXFile>'
        report = dict(sourceSha256=hashlib.sha256(raw).hexdigest(), objects=[dict(kind='InstanceHierarchy',id=None,name='P'),dict(kind='InternalElement',id='i',name='D')], links=[])
        verify_fixture_structure(raw, report)
        report['objects'].pop()
        with self.assertRaises(AssertionError): verify_fixture_structure(raw, report)


if __name__ == '__main__': unittest.main()
