"""Independent offline regression checks for source-bound coverage; never loads native code."""
import copy, json, os, subprocess, sys, tempfile, unittest
from pathlib import Path
import api_coverage_bindings as b

class BindingTests(unittest.TestCase):
    def setUp(self):
        parent=b.ROOT/'artifacts/expansion-implementation-20261015/coverage'; parent.mkdir(parents=True,exist_ok=True)
        self.temp=tempfile.TemporaryDirectory(dir=parent); self.root=Path(self.temp.name)
        self.put('src/TiaMcp.Host/Tools.cs','''
// [McpServerTool(Name = "fake_comment")] public object Fake() => Call("decoy");
class Tools {
 string decoy = "[McpServerTool(Name = fake_string)] { }";
 [McpServerTool(Name = "foo")] public object Foo() { var s = "} ; case fake:"; return Call("real"); }
 private object Call(string op) => bridge.CallAsync(op);
}''')
        self.put('src/TiaMcp.Openness.V21/OpennessSession.cs','''
class OpennessSession {
 internal object Execute(string operation) { switch(operation) { case "real": return Run(); default: throw new Exception(); } }
 private object Run() { return Helper(); }
 private object PrepareEngineeringMutation(object change) { switch(change.Kind) {case "change_a": return PrepareA();} }
 private void ApplyEngineeringMutation(object change) { switch(change.Kind) {case "change_a": ApplyA(); break;} }
 private object PrepareA() => "sample.v1";
 private void ApplyA() { }
}''')
        self.put('src/TiaMcp.Openness.V21/Other.cs','class Other { private object Helper() => 1; }')
        self.put('contracts/sample.v1.schema.json','{"type":"object"}')
        self.catalog={'features':[{'featureId':'foo','mcpTools':['foo'],'changeKind':None,'qualification':{'evidenceIds':['ev']}},
            {'featureId':'change.change_a','mcpTools':['foo'],'changeKind':'change_a','qualification':{'evidenceIds':[]}}],
            'evidence':[{'id':'ev','profile':{'tiaVersion':21},'artifacts':['artifacts/proof.json']}]}
        self.save_catalog()
    def tearDown(self): self.temp.cleanup()
    def put(self,p,text):
        path=self.root/p; path.parent.mkdir(parents=True,exist_ok=True); path.write_text(text,encoding='utf-8',newline='\n')
    def save_catalog(self): self.put(b.CATALOG,json.dumps(self.catalog))
    def collect(self): return b.collect(self.root)
    def test_exact_route_and_decoys(self):
        x=self.collect(); self.assertEqual({'tools':1,'mutations':1,'features':2},x['counts'])
        tool=x['tools'][0]; self.assertEqual('Foo',tool['host']['name']); self.assertEqual(['real'],tool['bridgeOperations'])
        self.assertEqual(['Run'],[h['name'] for h in tool['workerRoutes'][0]['handlers']]); b.validate(x,self.root)
    def test_nested_expression_method(self):
        ms,_=b.methods('class A { public object F() => Wrap(() => new { X = "}" }); public void G() { if(true) { Do(); } } }','a.cs')
        self.assertEqual(['F','G'],[m['name'] for m in ms])
    def test_string_forms(self):
        text='class A { public string F() => $"hello {Fn("inner")} world"; public string G() => @"a "" quote"; public string H() => """ { [McpServerTool(Name = "raw")] } """; }'
        ms,_=b.methods(text,'a.cs'); self.assertEqual(['F','G','H'],[m['name'] for m in ms])
        self.assertNotIn('Fn',b.calls(ms[0]['_tokens']))
    def test_unclosed_literals_fail(self):
        for text in ['/* unclosed','"unclosed','{ bad']:
            with self.assertRaises(ValueError): b.methods(text,'x')
    def test_duplicate_tool(self):
        self.put('src/TiaMcp.Host/Extra.cs','class Extra { [McpServerTool(Name = "foo")] public object X() => 1; }')
        with self.assertRaises(ValueError): self.collect()
    def test_missing_tool(self):
        self.catalog['features'][0]['mcpTools']=['missing']; self.save_catalog()
        with self.assertRaises(ValueError): self.collect()
    def test_changed_mutation_population(self):
        x=self.collect(); y=copy.deepcopy(x); y['mutations'].clear()
        with self.assertRaises(ValueError): b.validate(y)
    def test_source_change_stales(self):
        x=self.collect(); self.put('src/TiaMcp.Openness.V21/Other.cs','class Other { private object Helper() => 2; }')
        with self.assertRaisesRegex(ValueError,'Stale'): b.validate(x,self.root)
    def test_package_version_bump_does_not_stale_but_other_project_edits_do(self):
        project='src/TiaMcp.Host/TiaMcp.Host.csproj'
        self.put(project,'<Project><ItemGroup><PackageReference Include="A" Version="1.0.0" /></ItemGroup></Project>')
        x=self.collect()
        self.put(project,'<Project><ItemGroup><PackageReference Include="A" Version="1.10.2-rc.1" /></ItemGroup></Project>')
        b.validate(x,self.root)
        for changed in ('<Project><ItemGroup><PackageReference Include="B" Version="1.0.0" /></ItemGroup></Project>',
                        '<Project><ItemGroup><PackageReference Include="A" Version="1.0.0" /><PackageReference Include="C" Version="1.0.0" /></ItemGroup></Project>',
                        '<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup><ItemGroup><PackageReference Include="A" Version="1.0.0" /></ItemGroup></Project>'):
            self.put(project,changed)
            with self.assertRaisesRegex(ValueError,'Stale'): b.validate(x,self.root)
    def test_crlf_source_refused_before_binding_but_evidence_bytes_preserved(self):
        evidence=self.root/'artifacts/proof.json'; evidence.parent.mkdir(parents=True,exist_ok=True)
        raw=b'{\r\n"outcome":1\r\n}\r\n'; evidence.write_bytes(raw)
        original=self.collect()
        path=self.root/'contracts/sample.v1.schema.json'
        path.write_bytes(b'{\r\n"type":"object"\r\n}\r\n')
        with self.assertRaisesRegex(ValueError,'checkout-stable LF.*contracts/sample.v1.schema.json'): self.collect()
        path.write_bytes(b'{\n"type":"object"\n}\n')
        fixed=self.collect(); b.validate(fixed,self.root)
        self.assertEqual(raw,evidence.read_bytes())
        self.assertEqual(original['evidenceInputs'],fixed['evidenceInputs'])
    def test_source_anchor_tampering(self):
        x=self.collect(); x['tools'][0]['host']['line']=99
        with self.assertRaises(ValueError): b.validate(x,self.root)
    def test_unknown_schema_reference(self):
        x=self.collect(); x['features'][0]['schemaBindings'].append({'path':'contracts/no.schema.json','source':'src/no.cs'})
        with self.assertRaises(ValueError): b.validate(x)
    def test_unknown_profile(self):
        x=self.collect(); x['features'][1]['evidenceRefs'][0]['id']='unknown'
        with self.assertRaises(ValueError): b.validate(x)
    def test_unknown_catalog_evidence(self):
        self.catalog['features'][0]['qualification']['evidenceIds']=['unknown']; self.save_catalog()
        with self.assertRaises(ValueError): self.collect()
    def test_evidence_specific_impact(self):
        r=b.impact(self.collect(),evidence_ids=['ev']); self.assertEqual(['foo'],r['affectedFeatureIds']); self.assertFalse(r['conservativeFallback'])
    def test_unknown_profile_impact_falls_back(self):
        self.assertEqual(2,len(b.impact(self.collect(),evidence_ids=['unknown'])['affectedFeatureIds']))
    def test_transitive_source_falls_back(self):
        r=b.impact(self.collect(),['src/TiaMcp.Openness.V21/Other.cs']); self.assertTrue(r['conservativeFallback']); self.assertEqual(2,len(r['affectedFeatureIds']))
    def test_schema_change_falls_back(self):
        r=b.impact(self.collect(),['contracts/sample.v1.schema.json']); self.assertEqual(2,len(r['affectedFeatureIds']))
    def test_removed_feature_retained_in_impact(self):
        x=self.collect(); self.catalog['features'].pop(); self.save_catalog(); y=self.collect()
        r=b.diff(x,y); self.assertEqual(['change.change_a'],r['removedFeatures']); self.assertIn('change.change_a',r['affectedFeatureIds'])
    def test_no_promotion(self):
        x=self.collect(); self.assertTrue(all(f['reviewState']=='partial' for f in x['features']))
        x['features'][0]['qualificationPromoted']=True
        with self.assertRaises(ValueError): b.validate(x)
    def test_library_and_direct_early_branches(self):
        path='src/TiaMcp.Openness.V21/OpennessSession.cs'; content=(self.root/path).read_text()
        content=content.replace('switch(change.Kind) {case "change_a": return PrepareA();}', 'if(change.Kind == "change_a") return PrepareA();')
        content=content.replace('switch(change.Kind) {case "change_a": ApplyA(); break;}', 'if(change.Kind == "change_a") { ApplyA(); return; }')
        self.put(path,content); x=self.collect()
        self.assertEqual(['direct_early_route']*2,[v['routeKind'] for v in x['mutations'][0]['branches']])
        self.catalog['features'][1]['changeKind']='library_a'; self.save_catalog()
        content=content.replace('PrepareEngineeringMutation','PrepareLibraryAction').replace('ApplyEngineeringMutation','ApplyLibraryAction').replace('change_a','library_a')
        self.put(path,content); x=self.collect(); self.assertEqual('library_delegation',x['mutations'][0]['dispatcherFamily'])
    def test_empty_schema_mapping_is_unresolved(self):
        self.put('contracts/sample.v1.schema.json','{}'); (self.root/'contracts/sample.v1.schema.json').unlink()
        self.assertTrue(all(f['schemaReview']=='unresolved' for f in self.collect()['features']))
    def test_grouped_cases_retain_handler(self):
        ms,_=b.methods('class A { public void F() { switch(x) { case "a": case "b": Run(); break; case "c": Other(); break; } } }','x')
        rows=dict(b.case_rows(ms[0])); self.assertIn('Run',b.calls(rows['a'])); self.assertNotIn('Other',b.calls(rows['a']))
    def test_worker_families_do_not_overwrite(self):
        self.put('src/TiaMcp.Simulator.V8/SimulatorSession.cs','class SimulatorSession { internal object Execute(string operation) { switch(operation) { case "real": return Sim(); } } private object Sim() => 1; }')
        self.assertEqual('Run',self.collect()['tools'][0]['workerRoutes'][0]['handlers'][0]['name'])
    def test_unique_host_delegation(self):
        self.put('src/TiaMcp.Host/Tools.cs','class Tools { [McpServerTool(Name = "foo")] public object Foo() => HostCore(); private object HostCore() => bridge.CallAsync("real"); }')
        self.assertEqual(['real'],self.collect()['tools'][0]['bridgeOperations'])
    def test_artifact_change_stales_and_specific_impact(self):
        self.put('artifacts/proof.json','{"outcome":1}'); x=self.collect()
        self.put('artifacts/proof.json','{"outcome":2}'); y=self.collect()
        with self.assertRaises(ValueError): b.validate(x,self.root)
        result=b.diff(x,y); self.assertEqual(['foo'],result['affectedFeatureIds']); self.assertFalse(result['conservativeFallback'])
    def test_receiver_calls_do_not_invent_bridge_routes(self):
        self.put('src/TiaMcp.Host/Tools.cs','class Tools { [McpServerTool(Name = "foo")] public object Foo() => service.Select(); } class Other { private object Select() => Call("sim_select"); }')
        self.assertEqual([],self.collect()['tools'][0]['bridgeOperations'])
    def test_clean_source_explicitly_reports_absent_evidence(self):
        self.put('artifacts/proof.json','{"outcome":1}'); x=self.collect()
        (self.root/'artifacts/proof.json').unlink()
        with self.assertRaises(ValueError): b.validate(x,self.root)
        result=b.validate(x,self.root,allow_missing_evidence=True)
        self.assertEqual(['artifacts/proof.json'],result['unavailableEvidence'])
        self.assertFalse(result['releaseAccepted'])
    def test_clean_source_does_not_allow_changed_available_evidence(self):
        self.put('artifacts/proof.json','{"outcome":1}'); x=self.collect()
        self.put('artifacts/proof.json','{"outcome":2}')
        with self.assertRaises(ValueError): b.validate(x,self.root,allow_missing_evidence=True)
    def test_clean_source_still_validates_exact_source_and_anchors(self):
        x=self.collect(); x['tools'][0]['host']['sha256']='0'*64
        with self.assertRaises(ValueError): b.validate(x,self.root,allow_missing_evidence=True)
    def test_cross_process_deterministic_host_delegation(self):
        self.put('src/TiaMcp.Host/Tools.cs','class Tools { [McpServerTool(Name = "foo")] public object Foo() { A(); B(); return C(); } private void A() {} private void B() {} private object C() => Call("real"); }')
        program='import sys; from pathlib import Path; sys.path.insert(0,sys.argv[1]); import api_coverage_bindings as b; print(b.digest(b.inventory.canonical(b.collect(Path(sys.argv[2])))))'
        results=[]
        for seed in ['1','2']:
            results.append(subprocess.check_output([sys.executable,'-B','-c',program,str(b.ROOT/'scripts'),str(self.root)],env={**os.environ,'PYTHONHASHSEED':seed},text=True))
        self.assertEqual(results[0],results[1])

if __name__=='__main__': unittest.main(verbosity=2)
