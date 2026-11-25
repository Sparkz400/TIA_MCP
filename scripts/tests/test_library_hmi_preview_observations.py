import copy, hashlib, json, os, shutil, subprocess, sys, tempfile, unittest
from pathlib import Path
import validate_library_observations as validation
import test_library_operation_observations as fixtures
root=validation.ROOT

class LibraryHmiPreviewObservations(unittest.TestCase):
    def setUp(self):
        self.operation=fixtures.LibraryOperationObservations(); self.operation.setUp()

    def plan(self,action='faceplate_create',family='unified'):
        p=self.operation.hmi(action,family); p['projectPath']='D:/Synthetic/Project.ap21'
        before=json.loads((root/('fixtures/libraries/observation-contracts/hmi-inventory-'+family+'.json')).read_bytes())
        target=copy.deepcopy(before['target']); selected=p['payload']['target']; selected['hmiPath']=target['hmiPath']
        version=p['dependencies'][0]['versions'][0]
        version['engineeringType']='Siemens.Engineering.Hmi.Screen.ScreenLibraryTypeVersion' if family=='classic' else 'Synthetic.UnifiedFaceplateVersion'
        if family=='classic':
            effect=dict(composition='screens',name=version['typeName'],sourceNativeType=version['engineeringType'],
                        nativeVersionGuid=version['versionGuid'],preservationCoverage='Native copy; compile and independent screen/resource preservation qualification remain required.')
        elif action=='tag_create':
            p['dependencies']=[]
            p['payload']['name']='NewTag'
            effect=dict(name='NewTag',dataType='Bool',tagTable='Tags',scope='internal')
        elif action=='faceplate_create':
            selected.update(screen='Synthetic',container='NewContainer')
            effect=dict(screen='Synthetic',container='NewContainer',versionGuid=version['versionGuid'],
                containedType='V'+version['version']+'\\'+version['typeName'],bindings=[],placement=copy.deepcopy(p['payload']['placement']),
                bindingTypeCoverage='Existing HMI tag type checked; native interface compatibility and PLC communication are additionally validated by compile/runtime.')
        else:
            before=self.operation.faceplate()
            before['observedTags']=[dict(name='Tag',dataType='Bool',hmiDataType='',connection='',plcTag='')]
            effect=dict(screen=selected['screen'],container=selected['container'],versionGuid=version['versionGuid'],
                bindings=copy.deepcopy(p['payload']['bindings']),
                bindingTypeCoverage='Native interface and HMI tag data types matched; PLC communication requires runtime qualification.')
        p['targetPreview']=dict(target=target,hmiAction=action,before=before,effect=effect,targetFingerprint='a'*64,
            compileRequired=True,runtimeVerified=False,visualVerified=False)
        return p

    def validate(self,p):
        return validation.validate_bytes(json.dumps(p).encode(),'hmi-plan')

    def validate_result(self,p,result):
        response=self.operation.response(p,result)
        return validation.validate_bytes(json.dumps(response).encode(),'hmi-change')

    def result(self,p):
        result=self.operation.faceplate()
        action=p['payload']['hmiAction']
        if action=='faceplate_create':
            result.update(name=p['payload']['target']['container'],containedType=p['targetPreview']['effect']['containedType'])
        return result

    def rejects(self,p,mutate):
        p=copy.deepcopy(p); mutate(p)
        with self.assertRaises(ValueError):self.validate(p)

    def test_four_routes_are_observations(self):
        for action,family in [('instantiate','classic'),('tag_create','unified'),('faceplate_create','unified'),('faceplate_bind','unified')]:
            with self.subTest(action=action):
                r=self.validate(self.plan(action,family)); self.assertTrue(r['hmiPreviewValidated'])
                for key in ('nativeSelectorVerified','nativeInterfaceTypesVerified','fullPreservationVerified','targetFingerprintRecomputed'):
                    self.assertFalse(r[key])

    def test_owner_target_and_action_binding(self):
        p=self.plan()
        for mutate in (lambda p:p['targetPreview']['target'].update(hmiPath='/other/items/HMI'),
                       lambda p:p['targetPreview']['target'].update(compileScope='/other'),
                       lambda p:p['targetPreview'].update(hmiAction='tag_create'),
                       lambda p:p.update(projectPath=None)):
            self.rejects(p,mutate)

    def test_all_truncation_levels_refuse(self):
        p=self.plan()
        for mutate in (lambda p:p['targetPreview']['before'].update(truncated=True),
                       lambda p:p['targetPreview']['before']['screens'][0].update(screenItemsTruncated=True),
                       lambda p:p['targetPreview']['before']['screens'][0]['faceplates'][0].update(interfaceTruncated=True)):
            self.rejects(p,mutate)

    def test_placement_must_fit_observed_screen(self):
        p=self.plan()
        self.rejects(p,lambda p:p['targetPreview']['before']['screens'][0].update(width=100))
        self.rejects(p,lambda p:p['targetPreview']['before']['screens'][0].update(height=0))
        self.rejects(p,lambda p:p['targetPreview']['before']['screens'].clear())
        self.rejects(p,lambda p:p['targetPreview']['effect']['placement'].update(left=2))

    def test_existing_container_and_tag_refuse(self):
        self.rejects(self.plan(),lambda p:p['targetPreview']['before']['screens'][0]['faceplates'][0].update(name='NewContainer'))
        self.rejects(self.plan('tag_create'),lambda p:p['targetPreview']['before']['tags'][0].update(name='NewTag'))

    def test_contained_type_and_selected_metadata(self):
        p=self.plan()
        self.rejects(p,lambda p:p['targetPreview']['effect'].update(containedType='Type'))
        self.rejects(p,lambda p:p['dependencies'].clear())
        self.rejects(p,lambda p:p['dependencies'][0]['versions'][0].update(engineeringType='Siemens.Engineering.SW.FB'))

    def test_binding_requires_exact_selected_link(self):
        p=self.plan('faceplate_bind')
        self.rejects(p,lambda p:p['targetPreview']['before'].update(nativeLinkVerified=False))
        self.rejects(p,lambda p:p['targetPreview']['before'].update(versionGuid='00000000-0000-0000-0000-000000000001'))
        self.rejects(p,lambda p:p['targetPreview']['before'].update(name='Other'))

    def test_binding_covers_all_interface_properties(self):
        p=self.plan('faceplate_bind')
        self.rejects(p,lambda p:p['targetPreview']['before']['interface'].append({'property':'Missing','value':None}))
        self.rejects(p,lambda p:p['targetPreview']['before']['interface'].append(copy.deepcopy(p['targetPreview']['before']['interface'][0])))

    def test_binding_tags_bind_order_name_and_type(self):
        p=self.plan('faceplate_bind')
        for mutate in (lambda p:p['targetPreview']['before']['observedTags'].clear(),
                       lambda p:p['targetPreview']['before']['observedTags'][0].update(name='Other'),
                       lambda p:p['targetPreview']['before']['observedTags'][0].update(dataType='Int')):
            self.rejects(p,mutate)

    def test_one_tag_may_bind_multiple_interface_properties(self):
        p=self.plan('faceplate_bind')
        p['payload']['bindings'].append({'property':'Second','tag':'Tag','dataType':'Bool'})
        p['targetPreview']['effect']['bindings']=copy.deepcopy(p['payload']['bindings'])
        p['targetPreview']['before']['interface'].append({'property':'Second','value':None})
        p['targetPreview']['before']['observedTags']*=2
        self.validate(p)

    def test_classic_source_name_type_and_collision(self):
        p=self.plan('instantiate','classic')
        self.rejects(p,lambda p:p['targetPreview']['effect'].update(name='Other'))
        self.rejects(p,lambda p:p['targetPreview']['effect'].update(sourceNativeType='Synthetic'))
        n=p['targetPreview']['effect']['name']
        self.rejects(p,lambda p:p['targetPreview']['before']['items'].append({'kind':'screen','path':'/screens/'+n,'name':n}))

    def test_classic_master_has_no_native_version_link(self):
        p=self.plan('instantiate','classic')
        p['selection']=p['payload']['selection']=[{'masterCopyPath':'/masterCopies/Screen'}]
        p['dependencies']=[]
        p['targetPreview']['effect'].update(nativeVersionGuid=None,sourceNativeType='Siemens.Engineering.Hmi.Screen.Screen')
        self.validate(p)
        self.rejects(p,lambda p:p['targetPreview']['effect'].update(nativeVersionGuid='00000000-0000-0000-0000-000000000001'))

    def test_native_names_use_utf16_bounds(self):
        p=self.plan('tag_create'); value='\U0001f600'*129
        p['payload']['name']=p['targetPreview']['effect']['name']=value
        with self.assertRaises(ValueError):self.validate(p)
        p=self.plan('faceplate_bind')
        p['payload']['bindings'][0]['property']=p['targetPreview']['effect']['bindings'][0]['property']='A/B'
        p['targetPreview']['before']['interface'][0]['property']='A/B'
        with self.assertRaises(ValueError):self.validate(p)

    def test_tag_table_only_has_hmitext_constraints(self):
        p=self.plan('tag_create')
        p['payload']['target']['tagTable']=p['targetPreview']['effect']['tagTable']='Folder/Table'
        self.validate(p)

    def test_classic_tag_master_requires_recorded_root_table(self):
        p=self.plan('instantiate','classic')
        p['selection']=p['payload']['selection']=[{'masterCopyPath':'/masterCopies/Tag'}]; p['dependencies']=[]
        p['payload']['target'].update(composition='tags',tagTable='Synthetic')
        p['targetPreview']['effect'].update(composition='tags',name='NewTag',nativeVersionGuid=None,
            sourceNativeType='Siemens.Engineering.Hmi.Tag.Tag')
        self.validate(p)
        self.rejects(p,lambda p:p['targetPreview']['before']['items'][1]['tags'].append({'name':'NewTag'}))
        self.rejects(p,lambda p:p['targetPreview']['before']['items'][1].update(path='/tagTables/folders/Nested/Synthetic'))

    def test_capture_dependencies_match_selection_and_closed_graph(self):
        p=self.plan()
        self.rejects(p,lambda p:p['dependencies'].append(copy.deepcopy(p['dependencies'][0])))
        self.rejects(p,lambda p:p['dependencies'][0]['versions'][0]['requiredVersionGuids'].append('00000000-0000-0000-0000-000000000001'))
        self.rejects(p,lambda p:p['dependencies'][0]['versions'][0].update(doNotUse=True))
        self.rejects(p,lambda p:p['dependencies'][0].update(masterCopy='Unselected'))
        tag=self.plan('tag_create'); tag['dependencies']=copy.deepcopy(p['dependencies'])
        with self.assertRaises(ValueError):self.validate(tag)

    def test_transitive_version_is_accepted_and_pinned(self):
        p=self.plan(); child=copy.deepcopy(p['dependencies'][0]['versions'][0])
        child.update(typeGuid='00000000-0000-0000-0000-000000000001',versionGuid='00000000-0000-0000-0000-000000000002')
        p['dependencies'][0]['versions'][0]['requiredVersionGuids']=[child['versionGuid']]
        p['dependencies'][0]['versions'].append(child)
        self.validate(p)
        self.rejects(p,lambda p:p['dependencies'][0]['versions'][0]['requiredVersionGuids'].clear())

    def test_created_contained_type_binds_the_result(self):
        p=self.plan(); result=self.result(p)
        self.assertEqual('selected-version-contained-type-and-placement',self.validate_result(p,result)['hmiResultBindingScope'])
        result['containedType']='Other'
        with self.assertRaises(ValueError):self.validate_result(p,result)

    def test_binding_readback_reports_geometry_without_claiming_preservation(self):
        p=self.plan('faceplate_bind'); result=self.result(p)
        report=self.validate_result(p,result)
        self.assertTrue(report['hmiBindingReadbackComplete']); self.assertTrue(report['hmiRetainedLayoutMatches'])
        result['width']+=1
        report=self.validate_result(p,result)
        self.assertFalse(report['hmiRetainedLayoutMatches']); self.assertFalse(report['fullPreservationVerified'])

    def test_truncated_binding_result_is_not_complete(self):
        p=self.plan('faceplate_bind'); result=self.result(p)
        result['interface']=[{'property':f'Other{i}','value':None} for i in range(200)]
        result['interfaceTruncated']=True
        self.assertFalse(self.validate_result(p,result)['hmiBindingReadbackComplete'])

    def test_result_version_number_cannot_disagree_with_selected_metadata(self):
        p=self.plan('faceplate_bind'); result=self.result(p); result['version']='99.0'
        with self.assertRaises(ValueError):self.validate_result(p,result)

    def test_no_result_does_not_imply_binding_or_preservation(self):
        report=self.validate(self.plan('faceplate_bind'))
        self.assertIsNone(report['hmiResultBindingScope']); self.assertIsNone(report['hmiRetainedLayoutMatches'])
        self.assertIsNone(report['hmiBindingReadbackComplete'])

    def test_authored_binding_fixture_retains_exact_report_scope(self):
        raw=(root/'fixtures/libraries/observation-contracts/hmi-binding-change.json').read_bytes()
        report=validation.validate_bytes(raw,'hmi-change')
        self.assertTrue(report['hmiBindingReadbackComplete'])
        self.assertTrue(report['hmiRetainedLayoutMatches'])
        self.assertFalse(report['nativeInterfaceTypesVerified'])
        self.assertFalse(report['fullPreservationVerified'])

    def test_unavailable_or_duplicate_dependency_evidence_refuses(self):
        p=self.plan(); root_id=p['dependencies'][0]['rootVersionGuid']
        self.rejects(p,lambda p:p.update(dependencies=[{'rootVersionGuid':root_id,'scopeComplete':False,'reason':'Unavailable'}]))
        self.rejects(p,lambda p:p['dependencies'][0]['versions'].append(copy.deepcopy(p['dependencies'][0]['versions'][0])))

    def test_all_classic_master_compositions(self):
        classes={'screens':'Screen.Screen','screenTemplates':'Screen.ScreenTemplate','screenPopups':'Screen.ScreenPopup',
                 'tagTables':'Tag.TagTable','tags':'Tag.Tag','scripts':'RuntimeScripting.VBScript'}
        for composition,kind in classes.items():
            with self.subTest(composition=composition):
                p=self.plan('instantiate','classic')
                p['selection']=p['payload']['selection']=[{'masterCopyPath':'/masterCopies/Example'}]; p['dependencies']=[]
                p['payload']['target'].update(composition=composition,tagTable='Synthetic')
                p['targetPreview']['effect'].update(composition=composition,nativeVersionGuid=None,sourceNativeType='Siemens.Engineering.Hmi.'+kind)
                result=dict(nativeObjectType='Siemens.Engineering.Hmi.'+kind,typeGuid=None,versionGuid=None,version=None,
                            nativeLinkVerified=False,runtimeVerified=False,linkStatus='noService')
                self.assertEqual('master-link-metadata-only',self.validate_result(p,result)['hmiResultBindingScope'])

    def test_classic_script_version_and_result_binding(self):
        p=self.plan('instantiate','classic'); native='Siemens.Engineering.Hmi.RuntimeScripting.VBScriptLibraryTypeVersion'
        p['payload']['target']['composition']='scripts'
        p['targetPreview']['effect'].update(composition='scripts',sourceNativeType=native)
        p['dependencies'][0]['versions'][0]['engineeringType']=native
        result={k:v for k,v in self.operation.faceplate().items() if k in ('nativeObjectType','typeGuid','versionGuid','version','nativeLinkVerified','runtimeVerified','linkStatus')}
        self.assertEqual('selected-version-link',self.validate_result(p,result)['hmiResultBindingScope'])
        result['version']='9.9'
        with self.assertRaises(ValueError):self.validate_result(p,result)

    def test_internal_tag_result_scope_is_explicit(self):
        p=self.plan('tag_create')
        result=dict(name='NewTag',dataType='Bool',connection='<Interne Variable>',defaultConnection='<Interne Variable>',
                    plcTag='',nativeConnectionResolved=False,runtimeVerified=False)
        self.assertEqual('internal-tag-name-and-type',self.validate_result(p,result)['hmiResultBindingScope'])

    def test_plain_plc_target_refuses_cleanly(self):
        p=self.plan()
        self.rejects(p,lambda p:p['payload'].pop('target'))
        self.rejects(p,lambda p:p['payload'].update(target={'plcPath':'/PLC'}))

    def test_compilation_and_visual_flags_cannot_promote(self):
        p=self.plan()
        for flag,value in (('compileRequired',False),('runtimeVerified',True),('visualVerified',True)):
            self.rejects(p,lambda p:p['targetPreview'].update({flag:value}))

    def test_relocated_cli_original_bytes_and_invalid_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            target=Path(temporary); (target/'scripts').mkdir(); (target/'contracts').mkdir()
            files=[root/'scripts/validate_library_observations.py',*list((root/'scripts').glob('library_*observations.py'))]
            for p in files:shutil.copyfile(p,target/'scripts'/p.name)
            for p in (root/'contracts').glob('*.schema.json'):shutil.copyfile(p,target/'contracts'/p.name)
            observation=target/'plan.json'; raw=json.dumps(self.plan()).encode(); observation.write_bytes(raw)
            digest=hashlib.sha256(raw).hexdigest(); environment={k:v for k,v in os.environ.items() if k.upper()!='PYTHONPATH'}
            args=[sys.executable,'-B',str(target/'scripts/validate_library_observations.py'),'--kind','hmi-plan','--path',str(observation)]
            run=subprocess.run(args+['--expected-sha256',digest],cwd=target,env=environment,capture_output=True,text=True)
            self.assertEqual(0,run.returncode,run.stdout+run.stderr); result=json.loads(run.stdout)
            self.assertEqual(digest,result['sha256']); self.assertTrue(result['hmiPreviewValidated'])
            self.assertFalse(result['targetFingerprintRecomputed']); self.assertEqual(raw,observation.read_bytes())
            value=json.loads(raw); value['payload'].pop('target'); observation.write_bytes(json.dumps(value).encode())
            run=subprocess.run(args,cwd=target,env=environment,capture_output=True,text=True)
            self.assertEqual(1,run.returncode); self.assertFalse(json.loads(run.stdout)['valid']); self.assertEqual('',run.stderr)


if __name__=='__main__':unittest.main()
