"""Offline fixture/orchestrator regressions; never launches a native process."""
import copy
import json
import tempfile
import unittest
import uuid
from unittest import mock
from pathlib import Path
from xml.etree import ElementTree as ET
from qualify_plc_component import (ComponentRun, FIXTURES, ROOT, drive_component, first_available_db_number, pin, review_step, terminal_network,
    verify_component_call, verify_dependency_graph, verify_preserved_installation, verify_standalone_caller, verify_explicit_source_attributes, verify_pinned_source_oracle, recovery_inputs)


class ComponentFixtureTests(unittest.TestCase):
    def setUp(self):
        self.spec = json.loads((FIXTURES / "manifest.json").read_text())

    def native_call(self):
        # Retained native FBD call serialization supplies syntax; fixture semantics
        # (names/global tags) are independently authored, not planner-produced.
        doc = ET.fromstring((ROOT / "fixtures/typed-graphical/native/Tgp_FB_global_FBD.xml").read_text(encoding="utf-8-sig"))
        for node in doc.iter():
            local = node.tag.rsplit("}", 1)[-1]
            if local == "CallInfo": node.set("Name", self.spec["root"])
            if local == "Access" and node.get("Scope") == "LocalVariable": node.set("Scope", "GlobalVariable")
            if local == "Component":
                node.set("Name", {"TiaMcp_EnableGate_DB": self.spec["instance"], "Enable": "ComponentEnable", "Active": "ComponentActive"}.get(node.get("Name"), node.get("Name")))
        envelope = ET.fromstring((ROOT / "fixtures/typed-graphical/native/Tgp_TemplateFC_FBD.xml").read_text(encoding="utf-8-sig"))
        block = envelope.find("SW.Blocks.FC")
        block.find("AttributeList/Name").text = self.spec["caller"]
        objects = block.find("ObjectList")
        objects.remove(objects.find("SW.Blocks.CompileUnit"))
        objects.insert(0, copy.deepcopy(doc.find(".//SW.Blocks.CompileUnit")))
        return ET.tostring(envelope, encoding="unicode")

    def test_real_native_call_shape_passes_independent_oracle(self):
        verify_component_call(self.native_call(), self.spec)

    def test_wrong_instance_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "instance identity"):
            verify_component_call(self.native_call().replace(self.spec["instance"], "Other"), self.spec)

    def test_swapped_mapping_wire_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "wiring differs"):
            verify_component_call(self.native_call().replace('UId="22" />', 'UId="23" />'), self.spec)

    def test_missing_enable_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "wiring differs"):
            verify_component_call(self.native_call().replace('Name="en"', 'Name="eno"'), self.spec)

    def test_false_constant_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "enable operand"):
            verify_component_call(self.native_call().replace(">TRUE<", ">FALSE<"), self.spec)

    def test_extra_call_parameter_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "interface differs"):
            verify_component_call(self.native_call().replace('Section="Input"', 'Section="InOut"'), self.spec)

    def test_following_title_is_retained_as_insertion_blocker(self):
        raw = (ROOT / "fixtures/typed-graphical/native/Tgp_TemplateFC_FBD.xml").read_text(encoding="utf-8-sig")
        _, following = terminal_network(raw, "FBD")
        self.assertTrue(any(row["kind"] == "MultilingualText" for row in following))
        self.assertIn('CompositionName="Title"', raw)

    def versions(self):
        return [{"typeGuid": "aaaaaaaa-0000-0000-0000-000000000001", "versionGuid": "bbbbbbbb-0000-0000-0000-000000000001",
                 "typeName": self.spec["root"], "state": "Committed", "version": "1.0.0", "doNotUse": False,
                 "requiredVersionGuids": ["bbbbbbbb-0000-0000-0000-000000000002"]},
                {"typeGuid": "aaaaaaaa-0000-0000-0000-000000000002", "versionGuid": "bbbbbbbb-0000-0000-0000-000000000002",
                 "typeName": self.spec["dependency"], "state": "Committed", "version": "1.0.0", "doNotUse": False, "requiredVersionGuids": []}]

    def test_real_dependency_edge_required(self):
        versions = self.versions()
        verify_dependency_graph(versions, self.spec)
        versions[0]["requiredVersionGuids"] = []
        with self.assertRaisesRegex(RuntimeError, "dependency edges"):
            verify_dependency_graph(versions, self.spec)

    def test_dependency_type_and_version_identities_must_be_distinct(self):
        for field in ("typeGuid", "versionGuid"):
            with self.subTest(field=field):
                versions = self.versions()
                versions[1][field] = versions[0][field].upper()
                with self.assertRaisesRegex(RuntimeError, "duplicate " + field):
                    verify_dependency_graph(versions, self.spec)

    def test_dependency_edge_identity_comparison_is_case_insensitive(self):
        versions = self.versions()
        versions[0]["requiredVersionGuids"][0] = versions[1]["versionGuid"].upper()
        verify_dependency_graph(versions, self.spec)

    def test_dependency_edges_reject_unresolved_and_duplicate_identities(self):
        for edges in ([str(uuid.uuid4())], [self.versions()[1]["versionGuid"]] * 2):
            with self.subTest(edges=edges):
                versions = self.versions()
                versions[0]["requiredVersionGuids"] = edges
                with self.assertRaisesRegex(RuntimeError, "duplicate or unresolved"):
                    verify_dependency_graph(versions, self.spec)

    def test_missing_dependency_edges_are_not_an_empty_closure(self):
        versions = self.versions()
        del versions[1]["requiredVersionGuids"]
        with self.assertRaisesRegex(RuntimeError, "complete list"):
            verify_dependency_graph(versions, self.spec)

    def test_library_review_uses_actual_native_nested_kind_response(self):
        versions = self.versions(); selection = [pin(versions[0])]
        exact = {"changeId": "lib-native", "previewHash": "hash", "state": "prepared",
                 "preview": {"kind": "library_instantiate", "selection": selection, "dependencies": [{"versions": versions}]}}
        value = {"state": "library_prepared", "previewHash": "hash", "review": exact}
        self.assertIs(exact, review_step(value, selection, self.spec, lambda _: self.fail("No typed artifact during import review")))

    def test_typed_review_uses_actual_native_nested_kind_response(self):
        value = self.retained_typed_review()
        self.assertEqual(8, len(value['review']['preview']['effects']))
        self.assertIs(value['review'], self.review_retained(value))

    def test_independent_instance_number_includes_native_global_and_instance_dbs(self):
        blocks = [{'kind': 'InstanceDB', 'number': 1}, {'kind': 'GlobalDB', 'number': 2},
                  {'kind': 'DB', 'number': 3}, {'kind': 'FC', 'number': 4}, {'kind': 'FB', 'number': 4}]
        self.assertEqual(4, first_available_db_number(blocks))

    def retained_typed_review(self):
        return json.loads((ROOT / 'fixtures/libraries/component-review/native05-typed-preview.json').read_text())

    def review_retained(self, value, expected_number=2):
        def read(artifact):
            filename = 'native05-requested-fbd.xml' if artifact['path'].endswith('requested.xml') else 'native05-requested-instance.xml'
            return (ROOT / 'fixtures/libraries/component-review' / filename).read_bytes()
        return review_step(value, [], self.spec, read, expected_number)

    def test_typed_review_rejects_unknown_missing_and_duplicate_mutations(self):
        for mode in ('unknown', 'missing', 'duplicate', 'extraField'):
            with self.subTest(mode=mode):
                value = self.retained_typed_review(); effects = value['review']['preview']['effects']
                if mode == 'unknown': effects.append({'action': 'delete', 'path': '/unrelated'})
                elif mode == 'missing': effects.pop(0)
                elif mode == 'duplicate': effects.append(copy.deepcopy(effects[1]))
                else: effects[1]['overwriteExisting'] = True
                with self.assertRaisesRegex(RuntimeError, 'mutation'):
                    self.review_retained(value)

    def test_typed_review_rejects_unbound_instance_mutation_fields(self):
        for field, replacement in [('calleePath', '/wrong'), ('name', 'Wrong'), ('number', 3), ('autoNumber', True),
                                    ('number', True), ('startValueImpact', 'overwrite existing starts')]:
            with self.subTest(field=field, replacement=replacement):
                value = self.retained_typed_review(); value['review']['preview']['effects'][1][field] = replacement
                with self.assertRaisesRegex(RuntimeError, 'mutation identity'):
                    self.review_retained(value)
        with self.assertRaisesRegex(RuntimeError, 'first available number'):
            self.review_retained(self.retained_typed_review(), expected_number=3)

    def test_typed_review_rejects_unbound_instance_artifact(self):
        for field in ('bytes', 'sha256'):
            with self.subTest(field=field):
                value = self.retained_typed_review(); artifact = value['review']['preview']['effects'][1]['artifact']
                artifact[field] = 0 if field == 'bytes' else '0' * 64
                with self.assertRaisesRegex(RuntimeError, 'artifact differs'):
                    self.review_retained(value)

    def test_typed_review_requires_exact_complete_dependency_effects(self):
        for mode in ('unknown', 'missing', 'duplicate', 'hash', 'identity', 'extraField'):
            with self.subTest(mode=mode):
                value = self.retained_typed_review(); effects = value['review']['preview']['effects']
                if mode == 'unknown': effects[2]['kind'] = 'deleteNetwork'
                elif mode == 'missing': effects.pop()
                elif mode == 'duplicate': effects.append(copy.deepcopy(effects[-1]))
                elif mode == 'hash': effects[-1]['sourceSha256'] = '0' * 64
                elif mode == 'identity': effects[-1]['identity'] = '/unrelated'
                else: effects[-1]['mayMutate'] = True
                with self.assertRaisesRegex(RuntimeError, 'metadata effects'):
                    self.review_retained(value)

    def test_typed_review_rejects_consistently_changed_operand_identity(self):
        value = self.retained_typed_review(); preview = value['review']['preview']
        operand = preview['typed']['resolvedOperands'][0]
        effect = next(row for row in preview['effects'] if row.get('identity') == operand['identity'])
        effect['identity'] = operand['identity'] = '/otherTag'
        with self.assertRaisesRegex(RuntimeError, 'independent signal mappings'):
            self.review_retained(value)

    def test_standalone_oracle_checks_new_fc_identity(self):
        verify_standalone_caller(self.native_call(), self.spec)
        with self.assertRaisesRegex(RuntimeError, "Standalone caller identity"):
            verify_standalone_caller(self.native_call().replace(self.spec["caller"], "OtherCaller"), self.spec)

    def test_standalone_preserves_every_existing_object_and_ob(self):
        before = {"objects": {"block:/Main": {"path": "/Main", "payloadSha256": "unchanged-ob"},
                              "block:/Peer": {"path": "/Peer", "payloadSha256": "unchanged-peer"}}}
        after = copy.deepcopy(before)
        for name in ("Caller", "Root", "Instance"):
            after["objects"]["block:/" + name] = {"path": "/" + name}
        after["objects"]["type:/Dependency"] = {"path": "/Dependency"}
        added = ["block:/Caller", "block:/Root", "block:/Instance", "type:/Dependency"]
        verify_preserved_installation(before, after, "/Caller", added)
        after["objects"]["block:/Main"]["payloadSha256"] = "implicit-ob-call"
        with self.assertRaisesRegex(RuntimeError, "Complete native XML changed"):
            verify_preserved_installation(before, after, "/Caller", added)

    def test_standalone_preservation_rejects_unexpected_additions(self):
        with self.assertRaisesRegex(RuntimeError, "inventory differs"):
            verify_preserved_installation({"objects": {}}, {"objects": {"block:/Caller": {}, "block:/Unexpected": {}}},
                                          "/Caller", ["block:/Caller"])

    def test_explicit_native_source_preserves_retained_fb_and_declares_settings(self):
        import hashlib
        provenance = json.loads((FIXTURES / "native/provenance.json").read_text())
        fixture = FIXTURES / self.spec["rootDocument"]
        captured = FIXTURES / "native" / provenance["installedVersionExportFixture"]
        self.assertEqual(provenance["fixtureSha256"], hashlib.sha256(fixture.read_bytes()).hexdigest())
        self.assertEqual(provenance["installedVersionExportSha256"], hashlib.sha256(captured.read_bytes()).hexdigest())
        verify_explicit_source_attributes(fixture.read_text(encoding="utf-8-sig"), self.spec)
        original = ET.fromstring(captured.read_text(encoding="utf-8-sig")).find(".//SW.Blocks.FB")
        actual = ET.fromstring(fixture.read_text(encoding="utf-8-sig")).find("SW.Blocks.FB")
        original.attrib.pop("CompositionName")
        # Whitespace indentation is the only serialization adaptation; every
        # element, attribute, ID, body token, interface and comment is compared.
        def tree(node):
            value = node.text or ""
            return node.tag, sorted(node.attrib.items()), value if value.strip() else "", [tree(child) for child in node]
        self.assertEqual(tree(original), tree(actual))

    def test_original_omitted_source_attributes_remain_rejected(self):
        source = (FIXTURES / "native/captured-source-version.xml").read_text(encoding="utf-8-sig")
        with self.assertRaisesRegex(RuntimeError, "explicitly declare exact AutoNumber"):
            verify_explicit_source_attributes(source, self.spec)

    def source_oracle_inputs(self):
        import hashlib
        guid = "43b5c338-b589-4ab5-8c82-cae3e08e9d06"
        base = (FIXTURES / ("oracle-xp01/source-" + guid + "-options1.xml")).read_bytes()
        oracle = (FIXTURES / ("oracle-xp01/source-" + guid + "-options3.xml")).read_bytes()
        base_hash, oracle_hash = (hashlib.sha256(value).hexdigest() for value in (base, oracle))
        version = {"typeName": self.spec["root"], "typeGuid": "146cafdb-e1d5-4111-9b23-1078b6557d53", "versionGuid": guid,
                   "contentSha256": base_hash, "artifact": {"sha256": base_hash}, "contentPolicy": {
                       "policyId": self.spec["contentPolicyId"], "sourceLibraryName": self.spec["sourceLibraryName"],
                       "sourceOracleSha256": oracle_hash, "sourceOracleArtifact": {"sha256": oracle_hash},
                       "properties": self.spec["explicitBlockAttributes"], "sourceBaseExportOptions": "WithDefaults",
                       "sourceOracleExportOptions": "WithDefaults, WithReadOnly", "documentInfoOptions": "None"}}
        return base, oracle, version

    def test_source_oracle_native_shape_and_object_inventory_reject_honestly_rehashed_changes(self):
        import hashlib
        def duplicate_id(base, oracle):
            for document in (base, oracle):
                nodes = [node for node in document.iter() if "ID" in node.attrib]
                nodes[1].set("ID", nodes[0].get("ID"))
        def empty_id(base, oracle):
            for document in (base, oracle):
                document.find("SW.Blocks.CodeBlockLibraryTypeVersion").set("ID", " ")
        def wrong_composition(base, oracle):
            for document in (base, oracle):
                document.find(".//SW.Blocks.FB").set("CompositionName", "Other")
        def duplicate_attributes(base, oracle):
            for document in (base, oracle):
                block = document.find(".//SW.Blocks.FB")
                block.append(copy.deepcopy(block.find("AttributeList")))
        cases = (
            ("base root", lambda b, o: setattr(b, "tag", "OtherDocument")),
            ("oracle root", lambda b, o: setattr(o, "tag", "OtherDocument")),
            ("extra base version", lambda b, o: ET.SubElement(b, "SW.Blocks.CodeBlockLibraryTypeVersion", ID="extra")),
            ("hidden oracle version", lambda b, o: ET.SubElement(o.find("SW.Blocks.CodeBlockLibraryTypeVersion/ObjectList"), "SW.Blocks.CodeBlockLibraryTypeVersion", ID="extra")),
            ("version ID mismatch", lambda b, o: o.find("SW.Blocks.CodeBlockLibraryTypeVersion").set("ID", "other")),
            ("extra base FB", lambda b, o: ET.SubElement(b, "SW.Blocks.FB", ID="extra")),
            ("duplicate IDs", duplicate_id), ("empty IDs", empty_id),
            ("wrong content composition", wrong_composition), ("duplicate attribute scope", duplicate_attributes))
        for label, mutate in cases:
            with self.subTest(label=label):
                base, oracle, version = self.source_oracle_inputs()
                base_document, oracle_document = ET.fromstring(base), ET.fromstring(oracle)
                mutate(base_document, oracle_document)
                base, oracle = ET.tostring(base_document), ET.tostring(oracle_document)
                base_hash, oracle_hash = (hashlib.sha256(raw).hexdigest() for raw in (base, oracle))
                version.update(contentSha256=base_hash, artifact={"sha256": base_hash})
                version["contentPolicy"].update(sourceOracleSha256=oracle_hash, sourceOracleArtifact={"sha256": oracle_hash})
                with self.assertRaises(RuntimeError):
                    verify_pinned_source_oracle(base, oracle, version, self.spec)

    def test_full_source_oracle_proves_exact_values_missing_from_base(self):
        verify_pinned_source_oracle(*self.source_oracle_inputs(), self.spec)

    def test_source_oracle_hash_and_guid_are_independently_bound(self):
        for field in ("hash", "versionGuid", "typeGuid", "global"):
            with self.subTest(field=field):
                base, oracle, version = self.source_oracle_inputs()
                if field == "hash": oracle += b" "
                elif field == "global": version["contentPolicy"]["sourceLibraryName"] = "Other"
                else: version[field] = str(uuid.uuid4())
                with self.assertRaises(RuntimeError):
                    verify_pinned_source_oracle(base, oracle, version, self.spec)

    def test_source_oracle_missing_duplicate_wrong_and_readonly_fields_reject(self):
        import hashlib
        for field in self.spec["explicitBlockAttributes"]:
            for kind in ("missing", "duplicate", "wrong", "readOnly"):
                with self.subTest(field=field, kind=kind):
                    base, oracle, version = self.source_oracle_inputs()
                    doc = ET.fromstring(oracle); attrs = doc.find(".//SW.Blocks.FB/AttributeList"); entry = attrs.find(field)
                    if kind == "missing": attrs.remove(entry)
                    elif kind == "duplicate": attrs.append(copy.deepcopy(entry))
                    elif kind == "wrong": entry.text = "wrong"
                    else: entry.attrib.clear()
                    oracle = ET.tostring(doc)
                    value = hashlib.sha256(oracle).hexdigest()
                    version["contentPolicy"].update(sourceOracleSha256=value, sourceOracleArtifact={"sha256": value})
                    with self.assertRaises(RuntimeError):
                        verify_pinned_source_oracle(base, oracle, version, self.spec)

    def test_each_explicit_source_attribute_is_exact_and_required(self):
        text = (FIXTURES / self.spec["rootDocument"]).read_text(encoding="utf-8-sig")
        for name in self.spec["explicitBlockAttributes"]:
            for mutation in ("missing", "wrong", "duplicate"):
                with self.subTest(name=name, mutation=mutation):
                    document = ET.fromstring(text); attributes = document.find("SW.Blocks.FB/AttributeList")
                    entry = attributes.find(name)
                    if mutation == "missing": attributes.remove(entry)
                    elif mutation == "wrong": entry.text = "wrong"
                    else: attributes.append(copy.deepcopy(entry))
                    with self.assertRaisesRegex(RuntimeError, "explicitly declare exact " + name):
                        verify_explicit_source_attributes(ET.tostring(document, encoding="unicode"), self.spec)

    def test_different_release_rejected(self):
        versions = self.versions(); versions[1]["version"] = "1.1.0"
        with self.assertRaisesRegex(RuntimeError, "exact committed"):
            verify_dependency_graph(versions, self.spec)

    def test_setup_sources_encode_independent_semantics(self):
        fb = (FIXTURES / (self.spec["root"] + ".scl")).read_text()
        self.assertIn('State : "' + self.spec["dependency"] + '";', fb)
        self.assertIn('#State.Active := #Enable;', fb)
        self.assertIn('#Active := #State.Active;', fb)

    def test_existing_caller_preserves_metadata_namespaces_and_pinned_snapshot_bytes(self):
        import hashlib
        original = ('<?xml version="1.0" encoding="utf-8"?><?owner old?><!--outside-->'
                    '<Document xmlns:p="urn:old" xmlns:prov="urn:extra"><!--old-->'
                    '<prov:DocumentInfo>retained</prov:DocumentInfo>'
                    '<SW.Blocks.FC ID="0"><AttributeList><Name>Caller</Name><Qualified>p:T</Qualified>'
                    '<Spacing xml:space="preserve"><Child/> </Spacing></AttributeList>'
                    '<ObjectList><SW.Blocks.CompileUnit ID="1"/></ObjectList></SW.Blocks.FC></Document>')
        inserted = '<SW.Blocks.CompileUnit ID="2"/>'
        changed = original.replace('</ObjectList>', inserted + '</ObjectList>')
        cases = (
            ('comment', changed.replace('<!--old-->', '<!--changed-->')),
            ('outside comment', changed.replace('<!--outside-->', '<!--different-->')),
            ('instruction', changed.replace('<?owner old?>', '<?owner changed?>')),
            ('QName binding', changed.replace('urn:old', 'urn:changed')),
            ('preserved whitespace', changed.replace('<Child/> ', '<Child/>  ')),
            ('namespaced provenance', changed.replace('>retained<', '>changed<')),
            ('declaration', changed.replace('encoding="utf-8"', 'encoding="utf-16"')),
            ('duplicate object ID', changed.replace(inserted, '<SW.Blocks.CompileUnit ID="2"><Unknown ID="0"/></SW.Blocks.CompileUnit>')),
            ('wrong network scope', changed.replace(inserted, '').replace('</Document>', inserted + '</Document>')),
            ('hash', changed), ('count', changed), ('missing hash', changed))
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            def snapshot(name, text):
                raw = text.encode('utf-8'); path = directory / (name + '.xml'); path.write_bytes(raw)
                return {'objects': {'block:/caller': {'path': '/caller', 'file': str(path), 'bytes': len(raw),
                    'sha256': hashlib.sha256(raw).hexdigest(), 'payloadSha256': name}}}
            before = snapshot('before', original)
            verify_preserved_installation(before, snapshot('control', changed), '/caller', [])
            for index, (label, text) in enumerate(cases):
                with self.subTest(label=label):
                    after = snapshot(str(index), text); row = after['objects']['block:/caller']
                    if label == 'hash': row['sha256'] = 'a' * 64
                    elif label == 'count': row['bytes'] += 1
                    elif label == 'missing hash': row.pop('sha256')
                    with self.assertRaises(RuntimeError):
                        verify_preserved_installation(before, after, '/caller', [])

    def test_existing_caller_retains_native_templates_except_one_network_and_export_provenance(self):
        import hashlib
        from xml.dom import minidom
        prefixes = ('Tgp_TemplateFC', 'Tgp_TemplateFB', 'Tgp_MultiTemplateFB',
                    'TgpUnit_TemplateFC', 'TgpUnit_TemplateFB', 'TgpUnit_MultiTemplateFB',
                    'TgpNamespace_TemplateFC', 'TgpNamespace_TemplateFB')
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            def snapshot(name, raw):
                path = directory / (name + '.xml'); path.write_bytes(raw)
                return {'objects': {'block:/caller': {'path': '/caller', 'file': str(path), 'bytes': len(raw),
                    'sha256': hashlib.sha256(raw).hexdigest(), 'payloadSha256': name}}}
            for prefix in prefixes:
                for language in ('FBD', 'LAD'):
                    with self.subTest(prefix=prefix, language=language):
                        source = ROOT / 'fixtures/typed-graphical/native' / (prefix + '_' + language + '.xml')
                        raw = source.read_bytes(); document = minidom.parseString(raw)
                        unit = document.getElementsByTagName('SW.Blocks.CompileUnit')[0]
                        clone = unit.cloneNode(deep=True)
                        used = {node.getAttribute('ID') for node in document.getElementsByTagName('*') if node.hasAttribute('ID')}
                        for node in [clone, *clone.getElementsByTagName('*')]:
                            if node.hasAttribute('ID'):
                                number = 0
                                while str(number) in used: number += 1
                                node.setAttribute('ID', str(number)); used.add(str(number))
                        unit.parentNode.appendChild(clone)
                        for node in document.documentElement.childNodes:
                            if node.nodeType == node.ELEMENT_NODE and node.tagName == 'DocumentInfo' and not node.namespaceURI:
                                node.appendChild(document.createComment('Synthetic changed export provenance'))
                        after = document.toxml(encoding=document.encoding or 'utf-8', standalone=document.standalone)
                        verify_preserved_installation(snapshot('before', raw), snapshot('after', after), '/caller', [])
                        self.assertEqual(raw, source.read_bytes())

    def test_full_caller_preservation_rejects_unrelated_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)
            (path / "before.xml").write_text('<Document><SW.Blocks.FC ID="0"><AttributeList><Name>Caller</Name></AttributeList><ObjectList><SW.Blocks.CompileUnit ID="1"/></ObjectList></SW.Blocks.FC></Document>')
            new = (path / "before.xml").read_text().replace('</ObjectList>', '<SW.Blocks.CompileUnit ID="2"/></ObjectList>')
            (path / "after.xml").write_text(new)
            before = {"objects": {"block:/caller": {"path": "/caller", "file": str(path / "before.xml"), "payloadSha256": "before"},
                "block:/other": {"path": "/other", "payloadSha256": "same"}}}
            after = copy.deepcopy(before); after["objects"]["block:/caller"].update(file=str(path / "after.xml"), payloadSha256="after")
            after["objects"]["block:/new"] = {"path": "/new"}
            import hashlib
            for snapshot in (before, after):
                row = snapshot["objects"]["block:/caller"]
                raw = Path(row["file"]).read_bytes()
                row.update(bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest())
            verify_preserved_installation(before, after, "/caller", ["block:/new"])
            after["objects"]["block:/other"]["payloadSha256"] = "changed"
            with self.assertRaisesRegex(RuntimeError, "Complete native XML changed"):
                verify_preserved_installation(before, after, "/caller", ["block:/new"])


class ComponentDriverTests(unittest.TestCase):
    def value(self, state):
        return {"changeId": "lib-component-fixture", "state": state, "previewHash": state + "-hash"}

    def test_every_step_review_precedes_durable_intent_and_single_dispatch(self):
        states = iter(["library_applied", "import_verified", "graphical_prepared", "graphical_applied", "installation_verified"])
        events = []
        def call(tool, args):
            events.append((tool, copy.deepcopy(args)))
            if tool == "change_apply":
                self.assertEqual("dispatching", events[-2][0])
                self.assertEqual("review", events[-3][0])
            return self.value(next(states))
        result = drive_component(call, self.value("library_prepared"), lambda v: events.append(("review", v)), lambda k, v: events.append((k, v)))
        self.assertEqual("installation_verified", result["state"])
        applies = [value for kind, value in events if kind == "change_apply"]
        self.assertEqual(["library_prepared-hash", "graphical_prepared-hash"], [r["previewHash"] for r in applies])
        self.assertTrue(all(r["checkpointSave"] and r["compile"] and r["save"] for r in applies))

    def test_lost_response_never_replays_or_restores(self):
        calls = []
        def call(tool, args):
            calls.append(tool)
            raise TimeoutError("lost mutation response")
        with self.assertRaises(TimeoutError):
            drive_component(call, self.value("library_prepared"), lambda _: None, lambda *_: None)
        self.assertEqual(["change_apply"], calls)

    def test_lost_response_at_every_component_boundary_stops_without_replay(self):
        states = ["library_applied", "import_verified", "graphical_prepared", "graphical_applied", "installation_verified"]
        tools = ["change_apply", "change_prepare", "change_prepare", "change_apply", "change_prepare"]
        for failed_boundary in range(len(states)):
            with self.subTest(failed_boundary=failed_boundary):
                calls, retained = [], []
                def call(tool, args):
                    index = len(calls)
                    calls.append((tool, copy.deepcopy(args)))
                    if index == failed_boundary:
                        raise TimeoutError("lost boundary response")
                    return self.value(states[index])
                with self.assertRaisesRegex(TimeoutError, "lost boundary response"):
                    drive_component(call, self.value("library_prepared"), lambda _: None,
                                    lambda kind, value: retained.append((kind, copy.deepcopy(value))))
                self.assertEqual(tools[:failed_boundary + 1], [tool for tool, _ in calls])
                applies = [args for tool, args in calls if tool == "change_apply"]
                self.assertEqual(len(applies), len({args["previewHash"] for args in applies}))
                self.assertEqual(len(applies), sum(kind == "dispatching" for kind, _ in retained))
                self.assertNotIn("installation_verified", [value.get("state") for _, value in retained])

    def test_durable_dispatch_receipt_failure_prevents_each_apply(self):
        states = ["library_applied", "import_verified", "graphical_prepared"]
        for failed_apply in (0, 1):
            with self.subTest(failed_apply=failed_apply):
                calls, dispatches = [], []
                def call(tool, args):
                    calls.append(tool)
                    return self.value(states[len(calls) - 1])
                def retain(kind, value):
                    if kind == "dispatching":
                        dispatches.append(value)
                        if len(dispatches) == failed_apply + 1:
                            raise OSError("dispatch receipt write failed")
                with self.assertRaisesRegex(OSError, "dispatch receipt write failed"):
                    drive_component(call, self.value("library_prepared"), lambda _: None, retain)
                self.assertEqual(failed_apply, calls.count("change_apply"))

    def test_foreign_completed_receipt_never_continues(self):
        calls = []
        def call(tool, args):
            calls.append(tool)
            result = self.value("library_applied"); result["changeId"] = "foreign"; return result
        with self.assertRaisesRegex(RuntimeError, "identity"):
            drive_component(call, self.value("library_prepared"), lambda _: None, lambda *_: None)
        self.assertEqual(["change_apply"], calls)

    def test_unknown_initial_state_is_status_only(self):
        calls = []
        with self.assertRaisesRegex(RuntimeError, "never replay"):
            drive_component(lambda *args: calls.append(args), self.value("outcome_unknown"), lambda _: None, lambda *_: None)
        self.assertEqual([], calls)

    def test_terminal_initial_receipt_is_not_accepted_as_installation(self):
        calls = []
        with self.assertRaisesRegex(RuntimeError, "Unexpected initial"):
            drive_component(lambda *args: calls.append(args), self.value("installation_verified"), lambda _: None, lambda *_: None)
        self.assertEqual([], calls)

    def test_same_prepared_state_cannot_trigger_second_apply(self):
        calls = []
        def call(tool, args): calls.append(tool); return self.value("library_prepared")
        with self.assertRaisesRegex(RuntimeError, "Unexpected component response state"):
            drive_component(call, self.value("library_prepared"), lambda _: None, lambda *_: None)
        self.assertEqual(["change_apply"], calls)

    def test_early_same_identity_completed_receipt_never_continues(self):
        calls = []
        def call(tool, args): calls.append(tool); return self.value("installation_verified")
        with self.assertRaisesRegex(RuntimeError, "Unexpected component response state"):
            drive_component(call, self.value("library_prepared"), lambda _: None, lambda *_: None)
        self.assertEqual(["change_apply"], calls)

    def test_review_failure_prevents_dispatch(self):
        calls = []
        def review(_): raise RuntimeError("wrong pins")
        with self.assertRaisesRegex(RuntimeError, "wrong pins"):
            drive_component(lambda *args: calls.append(args), self.value("library_prepared"), review, lambda *_: None)
        self.assertEqual([], calls)


class ComponentSeparateRecoveryTests(unittest.TestCase):
    def test_rejected_recovery_inputs_persist_terminal_failure_before_native_session(self):
        import qualify_plc_component as module
        with tempfile.TemporaryDirectory() as folder:
            run = ComponentRun.__new__(ComponentRun)
            run.output = Path(folder)
            run.report = {"status": "running", "runtimeVerified": False, "firstCheckpointRecoveryVerified": False}
            failure = RuntimeError("Original first checkpoint hash differs.")
            with mock.patch.object(module, "parent_recovery_inputs", side_effect=failure), mock.patch.object(module, "OwnedScratch") as session:
                with self.assertRaises(RuntimeError) as raised:
                    run.run_recovery(Path("failed-result.json"))
            self.assertIs(failure, raised.exception)
            session.assert_not_called()
            retained = json.loads((run.output / "result.json").read_text())
            self.assertEqual("failed", retained["status"])
            self.assertEqual("first_checkpoint_recovery_only", retained["qualificationScope"])
            self.assertEqual(str(failure), retained["failure"])
            self.assertTrue(retained["completedAtUtc"] and retained["traceback"])
            self.assertTrue(retained["recoveryPreflightFailed"])
            self.assertFalse(retained["nativeRecoveryDispatched"])
            self.assertFalse(retained["firstCheckpointRecoveryVerified"])
            self.assertNotIn("sessions", retained)

    def inputs(self, folder):
        import hashlib
        from qualification_owned_scratch import native_hash
        folder = Path(folder); (folder / "before-import").mkdir()
        raw = b'<Document><SW.Blocks.OB ID="0"><AttributeList><Name>Main</Name></AttributeList></SW.Blocks.OB></Document>'
        xml = folder / "before-import/0000.xml"; xml.write_bytes(raw)
        archive = folder / "first.zap21"; archive.write_bytes(b"original-first-checkpoint")
        checkpoint = {"changeId": "lib-first", "previewHash": "hash", "archivePath": str(archive),
                      "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "originalPath": str(folder / "original.ap21")}
        baseline = {"inventory": {"project": {"path": checkpoint["originalPath"]}}, "objects": {"block:/Main": {
            "file": str(xml), "sha256": hashlib.sha256(raw).hexdigest(), "payloadSha256": native_hash(raw.decode())}}}
        (folder / "before-import/manifest.json").write_text(json.dumps(baseline))
        journal = {"state": "applied"}; journal_path = folder / "journal.json"; journal_path.write_text(json.dumps(journal))
        report = {"status": "failed", "failureStatus": {"record": {"initialCheckpoint": checkpoint,
            "library": {"prepared": {"changeId": "lib-first", "previewHash": "hash"}, "result": {"journalPath": str(journal_path), "journal": journal}}}}}
        path = folder / "result.json"; path.write_text(json.dumps(report))
        return path, xml, journal_path

    def test_recovery_inputs_bind_first_checkpoint_and_original_files_read_only(self):
        with tempfile.TemporaryDirectory() as folder:
            path, xml, journal = self.inputs(folder)
            before = {p: p.read_bytes() for p in Path(folder).rglob("*") if p.is_file()}
            _, checkpoint, _, inputs = recovery_inputs(path)
            self.assertEqual("lib-first", checkpoint["changeId"]); self.assertEqual(5, len(inputs))
            self.assertEqual(before, {p: p.read_bytes() for p in before})

    def test_changed_baseline_or_journal_blocks_retrieval(self):
        for target in ("baseline", "journal"):
            with self.subTest(target=target), tempfile.TemporaryDirectory() as folder:
                path, xml, journal = self.inputs(folder)
                if target == "baseline": xml.write_text("<Document/>")
                else: journal.write_text('{"state":"restored_to_new_locations"}')
                with self.assertRaisesRegex(RuntimeError, "no longer matches|journal changed"):
                    recovery_inputs(path)

    def test_foreign_first_checkpoint_blocks_retrieval(self):
        with tempfile.TemporaryDirectory() as folder:
            path, _, _ = self.inputs(folder)
            report = json.loads(path.read_text()); report["failureStatus"]["record"]["initialCheckpoint"]["changeId"] = "lib-later"
            path.write_text(json.dumps(report))
            with self.assertRaisesRegex(RuntimeError, "exact first import"):
                recovery_inputs(path)

    def test_lost_retrieval_response_never_retries_or_dispatches_installation(self):
        from types import SimpleNamespace
        import qualify_plc_component as module
        run = ComponentRun.__new__(ComponentRun)
        run.args = SimpleNamespace(host=Path("host"), worker=Path("worker"), timeout=600)
        run.native = Path("new-owned-recovery"); run.output = Path("new-report"); run.report = {}
        run.persist = lambda: None
        with mock.patch.object(module, "parent_recovery_inputs"), mock.patch.object(module, "run_parent_recovery", side_effect=TimeoutError("lost retrieval")) as recovery:
            with self.assertRaisesRegex(TimeoutError, "lost retrieval"):
                run.run_recovery(Path("failed-report"))
            recovery.assert_called_once()
        self.assertEqual("failed", run.report["status"]); self.assertFalse(run.report["installationVerified"])


class ComponentReportPersistenceTests(unittest.TestCase):
    def new_run(self, directory):
        run = ComponentRun.__new__(ComponentRun)
        run.output = Path(directory)
        run.report = {"fixtureChanges": [], "events": []}
        return run

    def native_fixture(self, run):
        calls = []
        def call(tool, arguments):
            calls.append(tool)
            if tool == "change_prepare":
                return {"changeId": "lib-one", "previewHash": "reviewed"}
            self.assertEqual("change_apply", tool)
            return {"changeId": "lib-one", "state": "applied"}
        run.call = call
        return calls

    def test_transient_post_response_replace_retries_same_report_not_native_dispatch(self):
        for code in (5, 32, 33):
            with self.subTest(winerror=code), tempfile.TemporaryDirectory() as directory:
                run = self.new_run(directory)
                calls = self.native_fixture(run)
                original = Path.replace
                attempts = []
                def replace(pending, target):
                    if calls.count("change_apply"):
                        attempts.append((pending, target, pending.read_bytes()))
                        if len(attempts) == 1:
                            error = OSError("transient report collision"); error.winerror = code; raise error
                    return original(pending, target)
                with mock.patch.object(Path, "replace", autospec=True, side_effect=replace):
                    result = run.apply("library_instantiate", {})
                self.assertEqual("applied", result["state"])
                self.assertEqual(["change_prepare", "change_apply"], calls)
                self.assertEqual(2, len(attempts))
                self.assertEqual(attempts[0], attempts[1])
                self.assertEqual("result.json.pending", attempts[0][0].name)
                retained = json.loads((run.output / "result.json").read_text(encoding="utf-8"))
                self.assertEqual(result, retained["fixtureChanges"][0]["result"])

    def test_nonretryable_post_response_write_keeps_pending_and_never_redispatches(self):
        with tempfile.TemporaryDirectory() as directory:
            run = self.new_run(directory)
            calls = self.native_fixture(run)
            original = Path.replace
            attempts = []
            failure = OSError("nonretryable report failure"); failure.winerror = 112
            def replace(pending, target):
                if calls.count("change_apply"):
                    attempts.append((pending, target)); raise failure
                return original(pending, target)
            with mock.patch.object(Path, "replace", autospec=True, side_effect=replace):
                with self.assertRaises(OSError) as raised:
                    run.apply("library_instantiate", {})
            self.assertIs(failure, raised.exception)
            self.assertEqual(["change_prepare", "change_apply"], calls)
            self.assertEqual(1, len(attempts))
            previous = json.loads((run.output / "result.json").read_text(encoding="utf-8"))
            pending = json.loads((run.output / "result.json.pending").read_text(encoding="utf-8"))
            self.assertTrue(previous["fixtureChanges"][0]["dispatched"])
            self.assertNotIn("result", previous["fixtureChanges"][0])
            self.assertEqual("applied", pending["fixtureChanges"][0]["result"]["state"])

    def test_nonretryable_component_response_report_stops_advance_and_recovery(self):
        with tempfile.TemporaryDirectory() as directory:
            run = self.new_run(directory)
            calls = []
            original = Path.replace
            attempts = []
            def replace(pending, target):
                if run.report["events"][-1]["event"] == "response":
                    attempts.append((pending, target))
                    error = OSError("permanent report error"); error.winerror = 112; raise error
                return original(pending, target)
            def call(tool, arguments):
                calls.append(tool)
                return {"changeId": "lib-component-one", "state": "library_applied"}
            initial = {"changeId": "lib-component-one", "state": "library_prepared", "previewHash": "reviewed"}
            with mock.patch.object(Path, "replace", autospec=True, side_effect=replace):
                with self.assertRaisesRegex(OSError, "permanent report error"):
                    drive_component(call, initial, lambda _: None, run.retain)
            self.assertEqual(["change_apply"], calls)
            self.assertEqual(1, len(attempts))
            retained = json.loads((run.output / "result.json.pending").read_text(encoding="utf-8"))
            self.assertEqual("library_applied", retained["events"][-1]["value"]["state"])


if __name__ == "__main__": unittest.main()
