"""Native diamond-call dependency review, preservation, reopen and recovery proof.

Only a new owned offline scratch is mutated. Every result is bound to the exact
binary and authored source bytes; no simulator or existing project is touched.
"""
import argparse
import hashlib
import json
import itertools
import pathlib
import uuid
from xml.dom import Node, minidom

from block_test_runtime import utc_now
from qualify_project_index import initialize
from qualification_owned_scratch import assert_snapshot, native_hash, require, snapshot
from component_exact_lifetime import ComponentOwnedScratch
from compilation_evidence import require_plc_compile
from run_block_tests import save_json
from smoke_mcp import Client


NAMES = ("ReviewLeaf", "ReviewLeft", "ReviewRight", "ReviewRoot")


def fixture_source(changed=False, leaf_only=False):
    leaf = ('FUNCTION "ReviewLeaf" : ' + ('DInt' if changed else 'Int') + '\n'
            'VERSION : 0.1\nBEGIN\n// ReviewImpactMarker' + ('2' if changed else '1') + '\n'
            '#ReviewLeaf := ' + ('2' if changed else '1') + ';\nEND_FUNCTION\n')
    if leaf_only:
        return leaf
    return leaf + ''.join('FUNCTION "' + name + '" : DInt\nVERSION : 0.1\nBEGIN\n#' + name
                         + ' := "ReviewLeaf"();\nEND_FUNCTION\n' for name in NAMES[1:3]) + (
        'FUNCTION "ReviewRoot" : DInt\nVERSION : 0.1\nBEGIN\n'
        '#ReviewRoot := "ReviewLeft"() + "ReviewRight"();\nEND_FUNCTION\n')


def read_review(result):
    artifact = result['artifact']
    raw = pathlib.Path(artifact['path']).read_bytes()
    require(hashlib.sha256(raw).hexdigest() == artifact['sha256'] == result['reviewId'],
            'Persistent review bytes do not match the exact native capture.')
    return json.loads(raw)


def validate_leaf_change(before_xml, after_xml):
    """The authored change alters only return type, literal and marker text."""
    if isinstance(before_xml, bytes):
        before_xml = before_xml.decode('utf-8-sig')
    if isinstance(after_xml, bytes):
        after_xml = after_xml.decode('utf-8-sig')
    # Derive the expected edit without dropping in-scope namespace bindings or
    # opaque comments/PIs. ElementTree reserialization loses QName-only bindings.
    native_hash(before_xml)
    expected = minidom.parseString(before_xml)
    blocks = [node for node in expected.documentElement.childNodes
              if node.nodeType == Node.ELEMENT_NODE and node.tagName == 'SW.Blocks.FC']
    require(len(blocks) == 1, 'Wrong leaf identity.')
    block = blocks[0]
    elements = list(block.getElementsByTagName('*'))

    def text_node(element):
        require(len(element.childNodes) == 1 and element.firstChild.nodeType in (Node.TEXT_NODE, Node.CDATA_SECTION_NODE),
                'Expected a single native text value.')
        return element.firstChild

    attributes = [node for node in block.childNodes if node.nodeType == Node.ELEMENT_NODE and node.tagName == 'AttributeList']
    names = [node for attribute in attributes for node in attribute.childNodes
             if node.nodeType == Node.ELEMENT_NODE and node.tagName == 'Name']
    require(len(names) == 1 and text_node(names[0]).data == 'ReviewLeaf', 'Wrong leaf identity.')
    members = [node for node in elements if node.localName == 'Member']
    require(len(members) == 1 and members[0].getAttribute('Name') == 'Ret_Val' and members[0].getAttribute('Datatype') == 'Int',
            'Unexpected baseline leaf interface.')
    members[0].setAttribute('Datatype', 'DInt')
    literals = [node for node in elements if node.localName == 'ConstantValue']
    require(len(literals) == 1 and text_node(literals[0]).data == '1', 'Unexpected baseline leaf literal.')
    text_node(literals[0]).data = '2'
    markers = [node for node in elements if node.localName == 'Text' and len(node.childNodes) == 1
               and node.firstChild.nodeType in (Node.TEXT_NODE, Node.CDATA_SECTION_NODE)
               and node.firstChild.data.strip() == 'ReviewImpactMarker1']
    require(len(markers) == 1, 'Unexpected baseline leaf marker.')
    text_node(markers[0]).data = text_node(markers[0]).data.replace('ReviewImpactMarker1', 'ReviewImpactMarker2')
    require(native_hash(expected.toxml()) == native_hash(after_xml),
            'Native leaf changed outside the exact authored type/literal/marker contract.')


def validate_impacts(compared, captures, plc):
    leaf = plc + '/blocks/ReviewLeaf'
    expected = {
        (plc + '/blocks/ReviewLeft', leaf),
        (plc + '/blocks/ReviewRight', leaf),
        (plc + '/blocks/ReviewRoot', plc + '/blocks/ReviewLeft', leaf),
        (plc + '/blocks/ReviewRoot', plc + '/blocks/ReviewRight', leaf),
    }
    change = next(row for row in compared['changes'] if row.get('afterObjectPath') == leaf)
    require({'interfaceChanged', 'logicChanged', 'documentChanged'} <= set(change['changedFields']),
            'Authored return type and logic changes were not both detected.')
    require(compared['impactCoverageComplete'] is False and compared['wholeProjectEquivalent'] is False
            and compared['impactTraversal']['complete'] is False, 'Partial observations claimed full coverage.')
    require(compared['impactTraversal']['truncated'] is False, 'Unbounded fixture unexpectedly truncated.')
    counts = {}
    for side in ('before', 'after'):
        capture, review = captures[side]
        rows = [row for row in compared['observedImpactPaths']
                if row['reviewSide'] == side and row['changedObjectPath'] == leaf]
        expected_typed = {(path, kinds) for path in expected
                          for kinds in itertools.product(('calls', 'dependsOn'), repeat=len(path) - 1)}
        require(len(rows) == len(expected_typed) and
                {(tuple(row['objectPaths']), tuple(edge['kind'] for edge in row['edges'])) for row in rows} == expected_typed,
                'Native diamond impact paths differ from the independently authored program: ' + side)
        edges = {edge['edgeId']: edge for edge in review['model']['edges']}
        nodes = {node.get('objectPath'): node['nodeId'] for node in review['model']['nodes']}
        for row in rows:
            require(row['reviewId'] == capture['reviewId'] and row['distance'] == len(row['objectPaths']) - 1
                    and row['affectedObjectPath'] == row['objectPaths'][0], 'Path identity/length differs.')
            require(len(row['edges']) == row['distance'], 'Path omitted original edge evidence.')
            for index, edge in enumerate(row['edges']):
                require(edge == edges.get(edge['edgeId']) and edge['kind'] in ('calls', 'dependsOn'), 'Edge evidence was changed.')
                require(edge['source']['objectPath'] == row['objectPaths'][index]
                        and edge['target']['objectPath'] == row['objectPaths'][index + 1], 'Edge direction differs.')
                require(all(endpoint['nodeId'] == nodes[endpoint['objectPath']]
                            for endpoint in (edge['source'], edge['target'])), 'Exact endpoint identity differs.')
                evidence = edge['evidence']
                require(evidence['resolution'] == 'resolved' and evidence['origin'] in ('parsedSupported', 'nativeConfirmed')
                        and evidence.get('sourcePath') and evidence.get('location'), 'Native source evidence missing.')
        counts[side] = len(rows)
    return {'pathCounts': counts, 'exactCapturedEdgesVerified': True, 'runtimeVerified': False}


def qualify(args):
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    report_path = root / 'result.json'
    report = {'schemaVersion': 1, 'startedAtUtc': utc_now(), 'completed': False,
              'sourceSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              'authoredSources': {name: fixture_source(changed, leaf_only) for name, changed, leaf_only in
                                  [('baseline', False, False), ('changedLeaf', True, True)]}}
    persist = lambda: save_json(report_path, report)
    extra = ('project_inventory', 'project_snapshot_capture', 'project_snapshot_compare',
             'project_snapshot_search', 'project_health_report')
    owned = ComponentOwnedScratch(args.host, args.worker, root, report, persist,
        extra_tools=extra, lifecycle_directory=root / 'lifecycle')
    offline = None
    try:
        owned.start()
        scratch = owned.call('tia_create_scratch')
        project = scratch['project']['path']
        plc = owned.call('project_inventory', {'maxItems': 2000})['plcs'][0]['path']
        report.update(projectPath=project, plcPath=plc)

        def apply(source, names):
            prepared = owned.call('change_prepare', {'kind': 'source_upsert', 'plcPath': plc,
                'payload': {'language': 'SCL', 'source': source, 'expectedBlockNames': list(names)}})
            result = owned.call('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
            require(result['state'] == 'applied', 'Fixture mutation failed.')
            require_plc_compile(result['compile'], plc)
            return prepared

        def capture(side):
            result = owned.call('project_snapshot_capture', {'request': {'plcPath': plc, 'maxBlocks': 4,
                'blockPaths': [plc + '/blocks/' + name for name in NAMES], 'maxRelationshipRoots': 4}})
            require(result['exportedBlocks'] == 4, 'Complete selected native source capture is absent.')
            report.setdefault('captures', {})[side] = result
            persist()
            return result, read_review(result)

        apply(fixture_source(), NAMES)
        baseline = snapshot(owned.call, root / 'before')
        before = capture('before')
        change = apply(fixture_source(True, True), ['ReviewLeaf'])
        after_snapshot = snapshot(owned.call, root / 'after')
        assert_snapshot(baseline, after_snapshot, except_paths=[plc + '/blocks/ReviewLeaf'])
        leaf_key = 'block:' + plc + '/blocks/ReviewLeaf'
        validate_leaf_change(pathlib.Path(baseline['objects'][leaf_key]['file']).read_bytes(),
                             pathlib.Path(after_snapshot['objects'][leaf_key]['file']).read_bytes())
        after = capture('after')
        compare_args = {'beforeId': before[0]['reviewId'], 'afterId': after[0]['reviewId']}
        compared = owned.call('project_snapshot_compare', compare_args)
        report['impactAudit'] = validate_impacts(compared, {'before': before, 'after': after}, plc)
        for bound, value, reason in (('impactDepth', 1, 'impactDepth'), ('maxImpactPaths', 1, 'maxImpactPaths'),
                                     ('maxImpactEdgeVisits', 1, 'maxImpactEdgeVisits')):
            bounded = owned.call('project_snapshot_compare', {**compare_args, bound: value})
            require(bounded['impactTraversal']['truncated'] is True
                    and reason in bounded['impactTraversal']['truncationReasons'], 'Missing native limit evidence: ' + bound)
        restored = owned.call('change_restore', {'changeId': change['changeId'], 'previewHash': change['previewHash']})
        recovered = restored['recoveredProject']['path']
        recovered_snapshot = snapshot(owned.call, root / 'recovered')
        assert_snapshot(baseline, recovered_snapshot)
        owned.reopen(project)
        assert_snapshot(after_snapshot, snapshot(owned.call, root / 'changed-reopen'))
        owned.reopen(recovered)
        assert_snapshot(baseline, snapshot(owned.call, root / 'recovered-reopen'))
        owned.close()
        offline = Client(args.host.resolve(), 300, ['--worker', str(args.worker.resolve()), '--artifacts', str(root)])
        initialize(offline)
        repeated = offline.tool('project_snapshot_compare', compare_args)
        require(repeated == compared, 'Host restart changed persisted comparison or exact edge evidence.')
        search = offline.tool('project_snapshot_search', {'reviewId': after[0]['reviewId'], 'query': 'ReviewImpactMarker2'})
        require(search['totalObservedHits'] >= 1, 'Persisted source search failed after host restart.')
        offline.tool('project_health_report', {'reviewId': after[0]['reviewId']})
        shutdown = offline.close()
        offline = None
        require(shutdown['hostExitCode'] == 0 and shutdown['forcedHostStop'] is False
                and not shutdown['workerExits'], 'Offline restart launched native worker or failed cleanup.')
        report['offlineRestart'] = shutdown
        report['preservation'] = {'allPeerXml': True, 'changedFreshReopen': True, 'checkpointRecovery': True,
                                  'recoveredFreshReopen': True}
        report['completed'] = True
    except Exception as error:
        report['failure'] = {'type': type(error).__name__, 'message': str(error)}
    finally:
        if offline is not None:
            try:
                report['offlineCleanup'] = offline.close()
            except Exception as error:
                report['offlineCleanupFailure'] = str(error)
                report['completed'] = False
        try:
            owned.close()
        except Exception as error:
            report['cleanupFailure'] = str(error)
            report['completed'] = False
        report['completedAtUtc'] = utc_now()
        persist()
    print(json.dumps({'completed': report['completed'], 'report': str(report_path), 'failure': report.get('failure')}, indent=2))
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=pathlib.Path, required=True)
    parser.add_argument('--worker', type=pathlib.Path, required=True)
    parser.add_argument('--artifacts', type=pathlib.Path, required=True)
    raise SystemExit(qualify(parser.parse_args()))
