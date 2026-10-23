"""Package catalog checks must cover all entries and compare full metadata."""
import contextlib
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import smoke_package

ROOT = Path(__file__).resolve().parents[1]


class PackageCatalogTests(unittest.TestCase):
    def test_paged_catalog_rejects_drift_missing_metadata_and_duplicate_ids(self):
        catalog={'catalogVersion':'test','features':[{'featureId':str(i),'title':'Original'} for i in range(201)]}
        class Pages:
            def __init__(self, defect=None): self.defect=defect
            def tool(self, name, args):
                offset=args['offset']; rows=copy.deepcopy(catalog['features'][offset:offset+args['maxItems']]); end=offset+len(rows)
                response={'features':rows,'catalogVersion':'test','targetAvailabilityProbed':False,
                          'pagination':{'total':201,'complete':end==201,'nextOffset':end}}
                if offset and self.defect=='version': response['catalogVersion']='changed'
                if offset and self.defect=='total': response['pagination']['total']=202
                if offset and self.defect=='duplicate': rows[0]['featureId']='0'
                if offset and self.defect=='metadata': rows[0]['title']='Changed'
                if self.defect=='stalled': response['pagination']['nextOffset']=offset
                if self.defect=='target': response['targetAvailabilityProbed']=True
                if self.defect=='boolean-total': response['pagination']['total']=True
                return response
        self.assertEqual(201,smoke_package.verify_catalog(Pages(),catalog,['0'])['featureCount'])
        for defect in ['version','total','duplicate','metadata','stalled','target','boolean-total']:
            with self.subTest(defect=defect),self.assertRaises(ValueError): smoke_package.verify_catalog(Pages(defect),catalog,['0'])
        with self.assertRaises(ValueError): smoke_package.verify_catalog(Pages(),catalog,['unknown-tool'])
        changed=copy.deepcopy(catalog); changed['catalogVersion']='external-change'
        with self.assertRaises(ValueError): smoke_package.verify_catalog(Pages(),changed,['0'])

    def test_current_large_catalog_reaches_native_stage_without_truncation(self):
        catalog=json.loads((ROOT/'contracts/feature-catalog.v1.json').read_text(encoding='utf-8'))
        self.assertGreater(len(catalog['features']),200)
        class Host:
            errors=[]
            process=type('Process',(),{'returncode':0})()
            calls=[]
            def request(self, name, *_):
                if name=='tools/list': return {'tools':[{'name':n} for n in ['project_retrieve','call_graph','change_apply','safety_inspect','simulation_download','sim_test','artifact_read','feature_catalog','object_resolve','target_capabilities']]}
                return {}
            def notify(self,*_): pass
            def close(self): return {'hostExitCode':0,'forcedHostStop':False,'workerExits':[]}
            def tool(self,name,args=None):
                self.calls.append(name)
                if name!='feature_catalog': raise RuntimeError('native intentionally unavailable')
                offset=args.get('offset',0); rows=copy.deepcopy(catalog['features'][offset:offset+args['maxItems']]); end=offset+len(rows)
                return {'features':rows,'catalogVersion':catalog['catalogVersion'],'targetAvailabilityProbed':False,
                        'pagination':{'total':len(catalog['features']),'complete':end==len(catalog['features']),'nextOffset':end}}
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); bundle=root/'bundle'; (bundle/'contracts').mkdir(parents=True)
            (bundle/'contracts/feature-catalog.v1.json').write_text(json.dumps(catalog),encoding='utf-8')
            (bundle/'manifest.json').write_text('{"files":[]}',encoding='utf-8')
            (root/'bundle.mcp.json').write_text('{"mcpServers":{"tia-v21":{"command":"fixture","args":[]}}}',encoding='utf-8')
            host=Host(); report=root/'report.json'
            with patch.object(sys,'argv',['smoke_package','--bundle',str(bundle),'--report',str(report)]), patch.object(smoke_package,'Client',return_value=host),contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1,smoke_package.main())
            result=json.loads(report.read_text(encoding='utf-8'))
            self.assertEqual('native intentionally unavailable',result['failure'])
            self.assertIn('tia_capabilities',host.calls)
            step=next(s for s in result['steps'] if s['name']=='packaged feature catalog matches external contract')
            self.assertEqual(len(catalog['features']),step['result']['featureCount'])


if __name__ == '__main__': unittest.main()
