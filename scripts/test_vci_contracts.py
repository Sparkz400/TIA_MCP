"""Independent input/response contract negatives for local Git; no qualification claims."""
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator, ValidationError

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = json.loads((ROOT/'contracts/vci-workspace.v1.schema.json').read_text(encoding='utf-8'))


class VciContracts(unittest.TestCase):
    def validate(self, definition, value):
        Draft202012Validator(dict(SCHEMA, oneOf=[{'$ref':'#/$defs/'+definition}])).validate(value)

    def test_catalog_and_registry_cover_exact_tool_set_without_native_promotion(self):
        Draft202012Validator.check_schema(SCHEMA)
        expected = {'vci_prepare','vci_commit','vci_reconcile','vci_reopen','vci_history','vci_materialize','vci_list','vci_artifact_read','vci_file_read',
                    'vci_package_capture','vci_package_compare','vci_branches'}
        self.assertEqual(expected, set(SCHEMA['x-tool-contracts']))
        catalog = json.loads((ROOT/'contracts/feature-catalog.v1.json').read_text(encoding='utf-8'))
        registry = json.loads((ROOT/'contracts/api-coverage/registry.v1.json').read_text(encoding='utf-8'))
        for name in expected:
            row = next(row for row in catalog['features'] if row['featureId']==name)
            self.assertEqual('unverified',row['qualification']['status'])
            self.assertEqual([],row['qualification']['evidenceIds'])
            self.assertIn('D01',next(row for row in registry['featureObligations'] if row['featureId']==name)['obligations'])

    def test_file_input_distinguishes_empty_upload_and_deletion(self):
        for value in ({'relativePath':'empty.txt'}, {'relativePath':'remove.txt','delete':True},
                      {'relativePath':'raw.bin','uploadRef':'upload:'+'a'*32}):
            self.validate('fileInput',value)
        for value in ({'relativePath':'x','delete':True,'uploadRef':'upload:'+'a'*32},
                      {'relativePath':'x','delete':'true'}, {'relativePath':'x','shell':'anything'}):
            with self.assertRaises(ValidationError): self.validate('fileInput',value)

    def test_commit_requires_preview_and_exact_review_digest(self):
        valid={'previewRef':'vci:preview:'+'a'*32+':'+'b'*64,'reviewedPlanSha256':'c'*64}
        self.validate('vci_commitInput',valid)
        for change in ({'previewRef':valid['previewRef'].replace(':preview:',':revision:')},
                       {'reviewedPlanSha256':'c'*63}, {'unreviewed':True}):
            with self.assertRaises(ValidationError): self.validate('vci_commitInput',dict(valid,**change))

    def test_chunk_cannot_exceed_bounds_or_promote_native_qualification(self):
        value=dict(sha256='a'*64,bytes=1,offset=0,count=1,data='YQ==',encoding='base64',nextOffset=1,complete=True,nativeQualified=False)
        self.validate('chunk',value)
        for change in ({'nativeQualified':True},{'count':65537},{'offset':-1},{'complete':'true'}):
            with self.assertRaises(ValidationError): self.validate('chunk',dict(value,**change))

    def test_package_compare_requires_manifest_handles_and_all_review_hashes(self):
        valid = dict(baselineRef='vci:manifest:'+'a'*32+':'+'b'*64,projectRef='vci:manifest:'+'c'*32+':'+'d'*64,
                     workspaceRef='vci:manifest:'+'e'*32+':'+'f'*64,expectedHashes=dict(baseline='a'*64,project='b'*64,workspace='c'*64))
        self.validate('vci_package_compareInput',valid)
        for change in ({'expectedHashes':{'baseline':'a'*64}}, {'baselineRef':valid['baselineRef'].replace('manifest','revision')},
                       {'applyResolution':True}):
            with self.assertRaises(ValidationError): self.validate('vci_package_compareInput',dict(valid,**change))

    def test_branch_pages_require_exact_revision_and_pinned_continuations(self):
        first = dict(revisionRef='vci:revision:'+'a'*32+':'+'b'*64, count=1)
        self.validate('vci_branchesInput', first)
        self.validate('vci_branchesInput', dict(first, offset=1, expectedInventorySha256='c'*64))
        for change in ({'offset':1}, {'offset':1,'expectedInventorySha256':None}, {'count':51},
                       {'revisionRef':first['revisionRef'].replace(':revision:',':preview:')}, {'expectedInventorySha256':'bad'}):
            with self.assertRaises(ValidationError): self.validate('vci_branchesInput', dict(first, **change))
        result = dict(revisionRef=first['revisionRef'], inventorySha256='c'*64, offset=0, rows=[], total=0, nextOffset=None,
                      nativeQualified=False, scope='Fresh local refs in this exact revision repository; pin inventorySha256 across pages.')
        self.validate('branches', result)
        with self.assertRaises(ValidationError): self.validate('branches', dict(result, nativeQualified=True))

    def test_mapping_evidence_schema_is_explicit_and_cannot_claim_qualification(self):
        schema=json.loads((ROOT/'contracts/vci-mapping-evidence.v1.schema.json').read_text(encoding='utf-8'))
        Draft202012Validator.check_schema(schema); validator=Draft202012Validator(schema)
        value=dict(schemaVersion='vci-mapping-evidence.v1',workspaceId='fixture',snapshot=dict(observationId='authored',sourceSha256='a'*64,tiaVersion=21),
                   enumerationComplete=False,packages=[])
        validator.validate(value)
        for change in ({'nativeQualified':True},{'snapshot':dict(observationId='authored',sourceSha256='a'*64)}, {'packages':[None]}):
            with self.assertRaises(ValidationError): validator.validate(dict(value,**change))


if __name__=='__main__': unittest.main()
