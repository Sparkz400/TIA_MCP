"""Exactly 1,000 native functions, with one fresh owned TIA lifetime per batch.

Fresh lifetimes prevent accumulating Openness object handles across generation
batches. A failed or uncertain batch is never replayed. A stop file is honored
only after the current batch has saved and all exact owned processes have exited.
"""
import argparse
import hashlib
import json
import pathlib
import statistics
import time
import uuid
import xml.etree.ElementTree as ET

from block_test_runtime import utc_now
from qualification_owned_scratch import OwnedScratch, complete_inventory, native_hash, require, snapshot
from qualify_project_explorer import exact_reference, transfer, validate_fixture_relationships
from qualify_project_index import generated_functions, source_for
from run_block_tests import save_json
from typed_graphical_transport import artifact_bytes


PREFIX = 'TiaMcp_Index_Explorer'


def validate_generated(inventory, names, plc):
    coverage = inventory.get('completeness', {})
    require(coverage.get('scopeComplete') is True and coverage.get('truncated') is False,
            'Complete native inventory is required to admit or verify a batch.')
    rows = [row for row in inventory['blocks'] if row['name'].startswith(PREFIX + '_')]
    require(len(rows) == len(names) and {row['name'] for row in rows} == set(names),
            'Native generated function inventory differs; never replay a batch.')
    require(all(row['path'] == plc + '/blocks/' + row['name'] for row in rows),
            'Native generated function scope differs.')
    require(all(row.get('kind') == 'FC' and row.get('programmingLanguage') == 'SCL'
                and row.get('isConsistent') is True and row.get('isKnowHowProtected') is False for row in rows),
            'Generated objects are not consistent unprotected SCL functions.')


def validate_sample(data, name):
    document = ET.fromstring(data)
    block = document.find('SW.Blocks.FC')
    require(block is not None and block.findtext('AttributeList/Name') == name
            and block.findtext('AttributeList/ProgrammingLanguage') == 'SCL', 'Sample source block identity differs.')
    sections = [element for element in block.iter() if element.tag.rsplit('}', 1)[-1] == 'Section']
    for section_name, member_name in (('Input', 'Enable'), ('Return', 'Ret_Val')):
        selected = [section for section in sections if section.get('Name') == section_name]
        require(len(selected) == 1 and len(selected[0]) == 1 and selected[0][0].get('Name') == member_name
                and selected[0][0].get('Datatype') == 'Bool', 'Sample source declaration differs.')
    source = block.find('ObjectList/SW.Blocks.CompileUnit/AttributeList/NetworkSource')
    require(source is not None, 'Sample source has no native logic.')
    tokens = [(element.tag.rsplit('}', 1)[-1], element.get('Name') or element.get('Text'))
              for element in source.iter() if element.tag.rsplit('}', 1)[-1] in ('Component', 'Token')]
    require(tokens == [('Component', name), ('Token', ':='), ('Component', 'Enable'), ('Token', ';')],
            'Sample native logic differs from the independently authored assignment.')


def validate_nodes(nodes, names, plc):
    rows = [row for row in nodes if row['name'].startswith(PREFIX + '_')]
    require(len(rows) == len(names) and {row['name'] for row in rows} == set(names), 'Explorer omitted/duplicated a generated function.')
    require(all(row['kind'] == 'block' and row['origin'] == 'native'
                and row['objectPath'] == plc + '/blocks/' + row['name'] for row in rows), 'Explorer function identities differ.')
    require(len({row['nodeId'] for row in nodes}) == len(nodes), 'Explorer duplicated a node identity.')
    by_id = {row['nodeId']: row for row in nodes}
    require(all(row.get('parentId') is None or row['parentId'] in by_id for row in nodes), 'Explorer omitted a captured parent.')


def qualify(args):
    require(1 <= args.batch_size <= 100, 'Batch size must retain the existing 100-declaration limit.')
    root = args.artifacts.resolve()
    root.mkdir(parents=True, exist_ok=False)
    names = generated_functions(1000, PREFIX)
    report = {'schemaVersion': 1, 'startedAtUtc': utc_now(), 'completed': False, 'generatedFunctionCount': 0,
              'expectedFunctionCount': 1000, 'batches': [], 'scriptSha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
              'scope': 'Offline function-heavy metadata workload; sampled source and complete original peer XML. No PLC runtime proof.'}
    persist = lambda: save_json(root / 'result.json', report)
    extra = ('project_inventory', 'project_model_capture', 'project_model_search', 'project_model_children',
             'project_model_details', 'project_model_relationships', 'project_model_verify', 'project_model_export',
             'project_model_release', 'project_index')
    owned = OwnedScratch(args.host, args.worker, root, report, persist, timeout=600, extra_tools=extra)
    try:
        if args.stop_file and args.stop_file.exists():
            report['stopped'] = True
            report['remainingNames'] = names
            return 2
        owned.start()
        scratch = owned.call('tia_create_scratch')
        project = scratch['project']['path']
        plc = owned.call('project_inventory', {'maxItems': 2000})['plcs'][0]['path']
        report.update(projectPath=project, plcPath=plc)
        baseline = snapshot(owned.call, root / 'baseline')
        owned.close()

        def verify_peers(label):
            directory = root / label
            directory.mkdir()
            rows = []
            for index, expected in enumerate(baseline['objects'].values()):
                tool, selector = {'block': ('block_read', 'blockPath'), 'type': ('type_read', 'typePath'),
                                  'tagTable': ('tag_table_read', 'tagTablePath')}[expected['kind']]
                exported = owned.call(tool, {selector: expected['path']})
                data = artifact_bytes(owned.call, exported['artifact'])
                target = directory / (str(index).zfill(4) + '.xml')
                target.write_bytes(data)
                require(native_hash(data.decode('utf-8-sig')) == expected['payloadSha256'], 'Original native peer content changed.')
                rows.append({'path': expected['path'], 'file': str(target), 'sha256': hashlib.sha256(data).hexdigest()})
            report.setdefault('peerChecks', []).append({'phase': label, 'objects': rows})
            persist()

        for offset in range(0, len(names), args.batch_size):
            if args.stop_file and args.stop_file.exists():
                report['stopped'] = True
                report['remainingNames'] = names[offset:]
                break
            owned.start()
            owned.call('project_open', {'path': project})
            validate_generated(complete_inventory(owned.call), names[:offset], plc)
            batch = names[offset:offset + args.batch_size]
            source = source_for(batch)
            prepared = owned.call('change_prepare', {'kind': 'source_upsert', 'plcPath': plc,
                'payload': {'language': 'SCL', 'source': source, 'expectedBlockNames': batch}})
            record = {'names': batch, 'source': source, 'sourceSha256': hashlib.sha256(source.encode()).hexdigest(),
                      'changeId': prepared['changeId'], 'applyAttempted': True}
            report['batches'].append(record)
            persist()
            started = time.perf_counter()
            applied = owned.call('change_apply', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash'],
                'idempotencyKey': uuid.uuid4().hex, 'checkpointSave': True, 'compile': True, 'save': True})
            record['applyCompileSaveMilliseconds'] = (time.perf_counter() - started) * 1000
            require(applied['state'] == 'applied' and applied['compile']['errorCount'] == 0, 'Function batch import/compile failed.')
            record['knownAppliedFunctionCount'] = offset + len(batch)
            validate_generated(complete_inventory(owned.call), names[:offset + len(batch)], plc)
            verify_peers('peers-' + str(offset).zfill(4))
            record['sampledSources'] = []
            for name in sorted({batch[0], batch[len(batch) // 2], batch[-1]}):
                exported = owned.call('block_read', {'blockPath': plc + '/blocks/' + name})
                data = artifact_bytes(owned.call, exported['artifact'])
                validate_sample(data, name)
                target = root / (name + '.xml')
                target.write_bytes(data)
                record['sampledSources'].append({'name': name, 'file': str(target), 'sha256': hashlib.sha256(data).hexdigest()})
            owned.close()
            record['completed'] = True
            report['generatedFunctionCount'] = offset + len(batch)
            persist()
            print('PASS saved/reopened batch ' + str(report['generatedFunctionCount']) + '/1000', flush=True)
        if report.get('stopped'):
            return 2
        owned.start()
        owned.call('project_open', {'path': project})
        validate_generated(complete_inventory(owned.call), names, plc)
        verify_peers('peers-final-reopen')

        def model(action, **kwargs):
            return owned.call('project_model_' + action, {'request': kwargs})

        def pages(action, **kwargs):
            result, cursor = [], None
            while True:
                page = model(action, **kwargs, maxItems=200, **({'cursor': cursor} if cursor else {}))
                result.extend(page['nodes'])
                cursor = page.get('nextCursor')
                if cursor is None:
                    return result
                require(page['nodes'], 'Empty page retained a cursor.')

        legacy = owned.call('project_index', {'request': {'action': 'capture', 'lifetimeSeconds': 900}})
        header = model('capture', lifetimeSeconds=900)
        nodes = pages('search', snapshotId=header['snapshotId'])
        validate_nodes(nodes, names, plc)
        report.update(nativeCapture=header['usage'], nodeCount=len(nodes))
        for parent in {None, *(row.get('parentId') for row in nodes)}:
            children = pages('children', snapshotId=header['snapshotId'], parentId=parent)
            expected = {row['nodeId'] for row in nodes if row.get('parentId') == parent}
            require(len(children) == len(expected) and {row['nodeId'] for row in children} == expected,
                    'Explorer child paging differs from independent tree partition.')
        block = next(row for row in nodes if row.get('objectPath') == plc + '/blocks/TiaMcp_EnableGate')
        reference = exact_reference(header, block)
        graph = model('relationships', reference=reference, depth=1, maxNodes=100, maxEdges=200)
        validate_fixture_relationships(graph, plc)
        report['populatedRelationships'] = {'edges': len(graph['edges']), 'authoredDirectionsVerified': True}
        for direction, endpoint in (('in', 'target'), ('out', 'source')):
            directed = model('relationships', reference=reference, kinds=['calls'], direction=direction)
            require(directed['edges'] and all(edge[endpoint]['objectPath'] == reference['objectPath']
                                            for edge in directed['edges']), 'Direction filter disagrees with native calls.')
        last = next(row for row in nodes if row.get('objectPath') == plc + '/blocks/' + names[-1])
        last_ref = exact_reference(header, last)
        details = model('details', reference=last_ref)
        require(details['status'] == 'available' and last['objectPath'] in json.dumps(details), 'Exact last function details missing.')
        independent = owned.call('block_read', {'blockPath': last['objectPath']})
        transfer(owned.client, independent['artifact'], root / 'last-function.xml')
        verified = model('verify', snapshotId=header['snapshotId'], reference=last_ref)
        require(verified['matchesCurrentMetadata'] and verified['contentVerified'] is False, 'Metadata verification boundary changed.')
        require(owned.call('project_index', {'request': {'action': 'query', 'indexId': legacy['indexId'], 'maxItems': 200}})['valid'],
                'Explorer reads invalidated the legacy index.')
        timings = []
        for _ in range(25):
            started = time.perf_counter()
            page = owned.client.tool('project_model_search', {'request': {
                'snapshotId': header['snapshotId'], 'text': names[-1], 'maxItems': 200}})
            timings.append((time.perf_counter() - started) * 1000)
            require([node['objectPath'] for node in page['nodes']] == [last['objectPath']], 'Timed exact search result differs.')
        report['cachedQueryMilliseconds'] = {'count': 25, 'samples': timings,
            'median': statistics.median(timings), 'p95': sorted(timings)[23],
            'includesMcpTransport': True, 'includesReportPersistence': False}
        for format in ('json', 'html'):
            exported = model('export', snapshotId=header['snapshotId'], format=format)
            target = root / ('snapshot.json' if format == 'json' else 'viewer.html')
            transfer(owned.client, exported['artifact'], target)
            report.setdefault('exports', {})[format] = {'path': str(target), 'sha256': hashlib.sha256(target.read_bytes()).hexdigest()}
        model('release', snapshotId=header['snapshotId'])
        owned.close()
        report['completed'] = True
    except Exception as error:
        report['failure'] = {'type': type(error).__name__, 'message': str(error)}
    finally:
        try:
            owned.close()
        except Exception as error:
            report['cleanupFailure'] = str(error)
            report['completed'] = False
        report['completedAtUtc'] = utc_now()
        persist()
    print(json.dumps({'completed': report['completed'], 'report': str(root / 'result.json'), 'failure': report.get('failure')}, indent=2))
    return 0 if report['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=pathlib.Path, required=True)
    parser.add_argument('--worker', type=pathlib.Path, required=True)
    parser.add_argument('--artifacts', type=pathlib.Path, required=True)
    parser.add_argument('--batch-size', type=int, default=100)
    parser.add_argument('--stop-file', type=pathlib.Path)
    raise SystemExit(qualify(parser.parse_args()))
