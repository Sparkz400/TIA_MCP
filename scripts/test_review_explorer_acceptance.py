"""Independent negative controls for native review/scale acceptance oracles."""
import copy
import itertools
import unittest

from qualify_review_impacts import validate_impacts, validate_leaf_change
from qualify_project_explorer_functions import PREFIX, validate_generated, validate_nodes, validate_sample
from qualification_owned_scratch import native_hash


class OwnedPayloadContract(unittest.TestCase):
    def test_comments_processing_instructions_and_qname_bindings_are_significant(self):
        xml = '<Document xmlns:v="urn:one"><Block ID="9" Value="v:Kind"><!--keep--><?retain instruction?><Text xml:space="preserve">  value  </Text></Block></Document>'
        for changed in (xml.replace('<!--keep-->', ''), xml.replace('<?retain instruction?>', ''),
                        xml.replace('urn:one', 'urn:two'), xml.replace('ID="9"', 'ID="10"'),
                        xml.replace('  value  ', 'value')):
            with self.subTest(changed=changed):
                self.assertNotEqual(native_hash(xml), native_hash(changed))

    def test_only_native_top_level_provenance_is_excluded(self):
        xml = '<Document><DocumentInfo>one</DocumentInfo><Block><DocumentInfo>opaque</DocumentInfo></Block></Document>'
        self.assertEqual(native_hash(xml), native_hash(xml.replace('>one<', '>two<')))
        self.assertNotEqual(native_hash(xml), native_hash(xml.replace('opaque', 'changed')))


class ReviewImpactOracle(unittest.TestCase):
    def test_exact_leaf_edit_rejects_collateral_ids_and_unknown_payload(self):
        before = '<Document xmlns:v="urn:one"><SW.Blocks.FC ID="0"><AttributeList><Name>ReviewLeaf</Name><Member Name="Ret_Val" Datatype="Int"/></AttributeList><Text> ReviewImpactMarker1</Text><ConstantValue>1</ConstantValue><!--keep--><?retain instruction?><Opaque ID="17" Value="untouched" Type="v:Kind" xml:space="preserve">  value  </Opaque></SW.Blocks.FC></Document>'
        after = before.replace('Datatype="Int"', 'Datatype="DInt"').replace('Marker1', 'Marker2').replace('>1</ConstantValue>', '>2</ConstantValue>')
        validate_leaf_change(before, after)
        for changed in (after.replace('ID="17"', 'ID="18"'), after.replace('untouched', 'modified'), after.replace('<!--keep-->', ''), after.replace('>2</ConstantValue>', '>3</ConstantValue>'), after.replace('urn:one', 'urn:two'), after.replace('<?retain instruction?>', ''), after.replace('  value  ', 'value')):
            with self.subTest(changed=changed):
                with self.assertRaises(RuntimeError):
                    validate_leaf_change(before, changed)

    def fixture(self):
        plc = '/projects/Owned/plc/CPU'
        paths = {name: plc + '/blocks/Review' + name for name in ('Leaf', 'Left', 'Right', 'Root')}
        nodes = [{'objectPath': path, 'nodeId': name} for name, path in paths.items()]
        edges = []
        for source, target in [('Left', 'Leaf'), ('Right', 'Leaf'), ('Root', 'Left'), ('Root', 'Right')]:
            for kind in ('calls', 'dependsOn'):
                edges.append({'edgeId': source + target + kind, 'kind': kind,
                    'source': {'objectPath': paths[source], 'nodeId': source},
                    'target': {'objectPath': paths[target], 'nodeId': target},
                    'evidence': {'resolution': 'resolved', 'origin': 'nativeConfirmed', 'sourcePath': paths[source], 'location': 'line 2'}})
        captures = {side: ({'reviewId': side}, {'model': {'nodes': nodes, 'edges': edges}}) for side in ('before', 'after')}
        result = {'changes': [{'afterObjectPath': paths['Leaf'], 'changedFields': ['interfaceChanged', 'logicChanged', 'documentChanged']}],
                  'wholeProjectEquivalent': False, 'impactCoverageComplete': False,
                  'impactTraversal': {'complete': False, 'truncated': False}, 'observedImpactPaths': []}
        for side in captures:
            for sequence in [('Left', 'Leaf'), ('Right', 'Leaf'), ('Root', 'Left', 'Leaf'), ('Root', 'Right', 'Leaf')]:
                for kinds in itertools.product(('calls', 'dependsOn'), repeat=len(sequence) - 1):
                    selected = [next(edge for edge in edges if edge['edgeId'] == source + target + kind)
                                for source, target, kind in zip(sequence, sequence[1:], kinds)]
                    result['observedImpactPaths'].append({'reviewSide': side, 'reviewId': side, 'changedObjectPath': paths['Leaf'],
                        'affectedObjectPath': paths[sequence[0]], 'objectPaths': [paths[name] for name in sequence],
                        'distance': len(sequence) - 1, 'edges': copy.deepcopy(selected)})
        return plc, captures, result

    def test_all_native_edge_kinds_and_distinct_diamond_paths(self):
        plc, captures, result = self.fixture()
        self.assertEqual({'before': 12, 'after': 12}, validate_impacts(result, captures, plc)['pathCounts'])

    def test_changed_edge_evidence_rejected(self):
        plc, captures, result = self.fixture()
        result['observedImpactPaths'][0]['edges'][0]['evidence']['location'] = 'invented'
        with self.assertRaisesRegex(RuntimeError, 'Edge evidence'):
            validate_impacts(result, captures, plc)

    def test_missing_diamond_branch_rejected(self):
        plc, captures, result = self.fixture()
        result['observedImpactPaths'].pop()
        with self.assertRaisesRegex(RuntimeError, 'diamond'):
            validate_impacts(result, captures, plc)

    def test_cross_snapshot_identity_rejected(self):
        plc, captures, result = self.fixture()
        result['observedImpactPaths'][0]['reviewId'] = 'after'
        with self.assertRaisesRegex(RuntimeError, 'identity'):
            validate_impacts(result, captures, plc)

    def test_complete_coverage_claim_rejected(self):
        plc, captures, result = self.fixture()
        result['impactCoverageComplete'] = True
        with self.assertRaisesRegex(RuntimeError, 'full coverage'):
            validate_impacts(result, captures, plc)


class FunctionScaleOracle(unittest.TestCase):
    def test_sample_source_requires_authored_assignment(self):
        name = PREFIX + '_0999'
        xml = '<Document><SW.Blocks.FC><AttributeList><Name>' + name + '</Name><ProgrammingLanguage>SCL</ProgrammingLanguage><Interface><Section Name="Input"><Member Name="Enable" Datatype="Bool"/></Section><Section Name="Return"><Member Name="Ret_Val" Datatype="Bool"/></Section></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit><AttributeList><NetworkSource><Component Name="' + name + '"/><Token Text=":="/><Component Name="Enable"/><Token Text=";"/></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>'
        validate_sample(xml, name)
        with self.assertRaises(RuntimeError):
            validate_sample(xml.replace('<Token Text=";"/>', '<Token Text="NOT"/><Token Text=";"/>'), name)
        with self.assertRaises(RuntimeError):
            validate_sample(xml.replace('Datatype="Bool"', 'Datatype="Int"'), name)

    def setUp(self):
        self.plc = '/projects/Owned/plc/CPU'
        self.names = [PREFIX + '_0000', PREFIX + '_0001']
        self.rows = [{'name': name, 'path': self.plc + '/blocks/' + name, 'kind': 'FC', 'programmingLanguage': 'SCL',
                      'isConsistent': True, 'isKnowHowProtected': False} for name in self.names]
        self.inventory = {'completeness': {'scopeComplete': True, 'truncated': False}, 'blocks': self.rows}
        self.nodes = [{'name': row['name'], 'nodeId': row['name'], 'kind': 'block', 'origin': 'native',
                       'objectPath': row['path'], 'parentId': None} for row in self.rows]

    def test_complete_exact_inventory_and_tree(self):
        validate_generated(self.inventory, self.names, self.plc)
        validate_nodes(self.nodes, self.names, self.plc)

    def test_partial_inventory_rejected(self):
        self.inventory['completeness']['truncated'] = True
        with self.assertRaises(RuntimeError):
            validate_generated(self.inventory, self.names, self.plc)

    def test_missing_generated_function_rejected(self):
        self.rows.pop()
        with self.assertRaises(RuntimeError):
            validate_generated(self.inventory, self.names, self.plc)

    def test_same_name_wrong_scope_rejected(self):
        self.rows[0]['path'] = '/other/blocks/' + self.names[0]
        with self.assertRaises(RuntimeError):
            validate_generated(self.inventory, self.names, self.plc)

    def test_inconsistent_or_wrong_block_kind_rejected(self):
        for key, value in [('kind', 'FB'), ('programmingLanguage', 'LAD'), ('isConsistent', False), ('isKnowHowProtected', True)]:
            with self.subTest(key=key):
                changed = copy.deepcopy(self.inventory)
                changed['blocks'][0][key] = value
                with self.assertRaises(RuntimeError):
                    validate_generated(changed, self.names, self.plc)

    def test_virtual_duplicate_or_missing_parent_rejected(self):
        for key, value in [('origin', 'virtual'), ('parentId', 'absent'), ('nodeId', self.nodes[1]['nodeId'])]:
            with self.subTest(key=key):
                changed = copy.deepcopy(self.nodes)
                changed[0][key] = value
                with self.assertRaises(RuntimeError):
                    validate_nodes(changed, self.names, self.plc)


if __name__ == '__main__':
    unittest.main()
