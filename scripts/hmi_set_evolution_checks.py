"""Independent original-selection, transition and complete YAML oracle for set v2."""
import copy
import hashlib
import json
import xml.etree.ElementTree as ET
import yaml

from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError
from hmi_text_list_author_checks import ROOT, expected_documents, parse_candidate
from hmi_text_list_generation_checks import local_reader, expected_intent as bool_intent
from hmi_numeric_generation_checks import expected_intent as numeric_intent, expected_coverage, expected_changes, retained
from hmi_dependency_impact_checks import verify as verify_dependencies
from hmi_coordinated_generation_checks import verify as verify_v1
from hmi_integer_conversion_checks import verify_conversion, TYPE_CHANGE


def dependencies(report, original, binding):
    roots = set()
    for scope, field in (('beforeComponent', 'beforeMember'), ('afterComponent', 'afterMember')):
        if binding[field]:
            path = binding[field]['objectPath']
            roots.add((scope, path))
            roots.update((scope, node) for p in report['consumerPaths'] if p['scope'] == scope
                         and p['reviewId'] == original[scope]['reviewId'] and path in (p['rootObjectPath'], p['consumerObjectPath'])
                         for node in p['objectPaths'])
    associations = [dict(associationIndex=i, association=row) for i, row in enumerate(report['dependencyAssociations'])
                    if any((row['selection']['scope'], row['selection'][k]) in roots for k in ('objectPath', 'targetObjectPath'))]
    scopes = set(roots)
    for row in associations:
        s = row['association']['selection']
        scopes.update((s['scope'], s[k]) for k in ('objectPath', 'targetObjectPath'))
    covered = [r for r in report['dependencyCoverage'] if (r['scope'], r['objectPath']) in scopes]
    paths = [p for p in report['consumerPaths'] if any(binding[field] and p['scope'] == scope
             and p['reviewId'] == original[scope]['reviewId'] and binding[field]['objectPath'] in (p['rootObjectPath'], p['consumerObjectPath'])
             for scope, field in (('beforeComponent', 'beforeMember'), ('afterComponent', 'afterMember')))]
    return dict(selectedScopes=covered, associations=associations,
        unobservedScopes=[dict(scope=s, objectPath=p) for s, p in sorted(scopes - {(r['scope'], r['objectPath']) for r in covered})],
        contradictionCount=sum(r['association']['status'] in ('different', 'conflicting') for r in associations),
        allObservedSelectedIndicesMatched=bool(covered) and len(covered) == len(scopes) and all(r['allObservedIndicesMatched'] for r in covered),
        dependencyClosureVerified=False, consumerCoverageComplete=False,
        sourceImpactCoverage=report['dependencyImpactCoverage'], sourceConsumerCoverage=report['consumerCoverage']), paths


def member_coverage(original, reviews, selected, actual):
    members, unavailable, truncated = [], [], False
    for scope, field in (('beforeComponent', 'beforeMember'), ('afterComponent', 'afterMember')):
        if not original[scope]:
            continue
        identity = original[scope]['reviewId']
        saved = reviews[identity]
        for path in original[scope]['objectPaths']:
            docs = [d for d in saved['documents'] if d['objectPath'] == path and d['status'] == 'exported']
            owners = []
            if len(docs) == 1:
                tree = ET.fromstring(docs[0]['xml'])
                for element in tree.iter():
                    element.tag = element.tag.rsplit('}', 1)[-1]
                owners = [e for e in tree if e.tag.startswith('SW.Blocks.') or e.tag == 'SW.Types.PlcStruct']
            interfaces = owners[0].findall('./AttributeList/Interface') if len(owners) == 1 else []
            if len(interfaces) != 1:
                unavailable.append(dict(scope=scope, objectPath=path))
                continue
            def walk(parent, prefix):
                nonlocal truncated
                for member in parent.findall('./Sections/Section/Member'):
                    if len(members) >= 4096 or len(prefix) >= 16:
                        truncated = True
                        return
                    parts = prefix + [member.get('Name', '')]
                    ids = [b['id'] for b in original['bindings'] if b['id'] in selected and b[field] and
                           b[field]['objectPath'] == path and b[field]['memberPath'] == parts]
                    members.append(dict(scope=scope, reviewId=identity, objectPath=path, memberPath=parts,
                                        dataType=member.get('Datatype'), bindingIds=ids))
                    walk(member, parts)
            walk(interfaces[0], [])
    assert [{k: v for k, v in m.items() if k != 'evidence'} for m in actual['observations']] == members
    assert actual['unavailableObjects'] == unavailable and actual['truncated'] == truncated
    for member in actual['observations']:
        doc = next(d for d in reviews[member['reviewId']]['documents'] if d['objectPath'] == member['objectPath'])
        assert member['evidence']['utf8Sha256'] == doc['utf8Sha256'] and member['evidence']['liveVerified'] is False


def verify(request, receipt, read, ancestry=True):
    version = 'v3' if request['schemaVersion'].endswith('.v3') else 'v2'
    schema = json.loads((ROOT / ('contracts/unified-text-list-set.' + version + '.schema.json')).read_text(encoding='utf-8'))
    for value, kind in ((request, 'request'), (receipt, 'receipt' if 'recordId' in receipt else 'record')):
        Draft202012Validator({**schema, '$ref': '#/$defs/' + kind}).validate(value)
    assert json.loads(read(receipt['input'])) == request
    previous = retained(read, request['previousRecordId'], '.text-set.json')
    assert receipt['previousRecordArtifact'] == dict(path='hmi-component-reviews/' + request['previousRecordId'] + '.text-set.json',
                                                    sha256=request['previousRecordId'])
    if ancestry:
        previous_request = json.loads(read(previous['input']))
        (verify_v1 if previous['schemaVersion'].endswith('.v1') else verify)(previous_request, previous, read)
    component = retained(read, request['componentReviewId'], '.json')
    original, report = json.loads(read(component['input'])), component['report']
    assert receipt['componentReviewId'] == request['componentReviewId'] and receipt['previousRecordId'] == request['previousRecordId']
    sources = {f['id']: f for f in original['files']}
    raw_sources = {i: read(dict(path='hmi-component-reviews/' + f['sha256'] + '.source', sha256=f['sha256'])) for i, f in sources.items()}
    reviews = {s['reviewId']: json.loads(read(dict(path='project-reviews/' + s['reviewId'] + '.json', sha256=s['reviewId'])))
               for s in (original[k] for k in ('beforeComponent', 'afterComponent', 'destination')) if s}
    verify_dependencies(component, original, raw_sources, reviews)
    originals = {o['id']: o for o in original['outputs']}
    reports = {o['id']: o for o in report['outputs']}
    bindings = {b['id']: b for b in original['bindings']}
    bound = {b['id']: b for b in report['bindings']}
    # Derive selected types from the original saved XML, independently of both
    # the retained binding summary and its member-coverage projection.
    for binding in bindings.values():
        assert bound[binding['id']]['selection'] == binding
        for scope, selection_field, declaration_field in (
                ('beforeComponent', 'beforeMember', 'beforeDeclaration'),
                ('afterComponent', 'afterMember', 'afterDeclaration')):
            selection = binding[selection_field]
            declaration = bound[binding['id']][declaration_field]
            if selection is None:
                assert declaration is None
                continue
            saved = reviews[original[scope]['reviewId']]
            docs = [d for d in saved['documents'] if d['objectPath'] == selection['objectPath'] and d['status'] == 'exported']
            member = None
            if len(docs) == 1:
                tree = ET.fromstring(docs[0]['xml'])
                for node in tree.iter():
                    node.tag = node.tag.rsplit('}', 1)[-1]
                owners = [n for n in tree if n.tag.startswith('SW.Blocks.') or n.tag == 'SW.Types.PlcStruct']
                interfaces = owners[0].findall('./AttributeList/Interface') if len(owners) == 1 else []
                member = interfaces[0] if len(interfaces) == 1 else None
                for name in selection['memberPath']:
                    matches = [m for m in member.findall('./Sections/Section/Member') if m.get('Name') == name] if member is not None else []
                    member = matches[0] if len(matches) == 1 else None
            data_type = member.get('Datatype') if member is not None else None
            assert declaration['dataType'] == data_type
            assert declaration['status'] == ('capturedDeclaration' if data_type and data_type.strip() else 'unavailableOrAmbiguous')
            assert declaration['objectPath'] == selection['objectPath'] and declaration['memberPath'] == selection['memberPath']
            if member is not None:
                assert declaration['evidence']['utf8Sha256'] == docs[0]['utf8Sha256'] and declaration['evidence']['liveVerified'] is False
            else:
                assert declaration['evidence'] is None
    assert all(reports[identity]['selection'] == source for identity, source in originals.items())
    prior = {o['outputId']: o for o in previous['outputs']}
    selected = {o['outputId'] for o in request['outputs']} | set(request['removeOutputIds']) | set(request['retainOutputIds'])
    selected_bindings = {o['bindingId'] for o in request['outputs']} | {
        b['id'] for b in original['bindings'] if b['outputId'] in request['removeOutputIds']}
    accounted = {o['previousOutputId'] for o in request['outputs'] if o['previousOutputId']} | set(request['removeOutputIds'])
    coverage = receipt['coverage']
    for field, expected in dict(
        declaredOutputIds=list(originals), declaredBindingIds=list(bindings),
        uncoveredOutputIds=[o for o in originals if o not in selected],
        uncoveredBindingIds=[b for b in bindings if b not in selected_bindings],
        uncoveredPredecessorOutputIds=[o for o in prior if o not in accounted],
        allDeclaredOutputsCovered=set(originals) <= selected,
        allDeclaredBindingsCovered=set(bindings) <= selected_bindings,
        allPredecessorOutputsCovered=set(prior) <= accounted,
        sourceConsumerCoverage=report['consumerCoverage'], sourceDependencyImpactCoverage=report['dependencyImpactCoverage'],
        consumerCoverageComplete=False, dependencyClosureVerified=False, componentMemberCoverageComplete=False).items():
        assert coverage[field] == expected, field
    member_coverage(original, reviews, selected_bindings, coverage['members'])
    problems = [not coverage[k] for k in ('allDeclaredOutputsCovered', 'allDeclaredBindingsCovered', 'allPredecessorOutputsCovered')]
    problems.append(previous['status'] != 'candidatesReady')
    previous_report = retained(read, previous['componentReviewId'], '.json')['report']
    problems.append(previous_report['afterComponent']['selection'] != original['beforeComponent'] or
                    any(previous_report['destination'][k] != original['destination'][k] for k in ('hmiPath', 'family')))
    def files(ids):
        return sorted([{k: sources[i][k] for k in ('fileName', 'sha256', 'bytes')} for i in ids], key=lambda f: f['fileName'])
    def prior_files(identity):
        return [f for f in previous['files'] if f['outputId'] == identity]
    def same(supplied, wanted):
        return sorted((f['fileName'], f['bytes'], f['sha256']) for f in supplied) == sorted(
            (f['fileName'], f['bytes'], f['artifact']['sha256']) for f in wanted)
    def comparison(source, actual, wanted):
        assert actual['baselineFiles'] == files(source['baseline']) and actual['currentFiles'] == files(source['current'])
        assert actual['suppliedProposedFiles'] == files(source['proposed']) and actual['proposedFiles'] == wanted
        status = 'notSupplied' if not source['proposed'] and not source['proposedComplete'] else (
            'exact' if source['proposedComplete'] and same(files(source['proposed']), wanted) else 'conflict')
        assert actual['suppliedProposedStatus'] == status
        problems.append(status == 'conflict')
    def evidence(identity, binding, actual, conversion=False):
        binding_conflicts = [c for c in bound[binding['id']]['conflicts'] if not conversion or c != TYPE_CHANGE]
        problems.append(bool(reports[identity]['conflicts']) or bool(binding_conflicts))
        wanted, paths = dependencies(report, original, binding)
        assert {k: v for k, v in actual['dependencyCoverage'].items() if k != 'policy'} == wanted
        assert actual['consumerPaths'] == paths
        problems.extend([bool(wanted['contradictionCount']), request['requireObservedDependencies'] and not wanted['allObservedSelectedIndicesMatched']])
    def prior_checks(identity, source, binding, old, previous_id):
        problems.extend([not source['baselineComplete'], not source['currentComplete'], not same(files(source['baseline']), prior_files(previous_id)),
                         not same(files(source['current']), prior_files(previous_id)), len(prior_files(previous_id)) != 2])
        context = old['componentContext']
        problems.extend([context['afterComponent']['selection']['reviewId'] != original['beforeComponent']['reviewId'],
                         context['binding']['selection']['afterMember'] != binding['beforeMember'],
                         any(context['destination'][k] != original['destination'][k] for k in ('hmiPath', 'family')),
                         bound[binding['id']]['beforeDeclaration']['status'] != 'capturedDeclaration',
                         bound[binding['id']]['beforeDeclaration']['dataType'] != context['binding']['afterDeclaration']['dataType'],
                         source['name'] != old['intent']['lists'][0]['name'], reports[identity]['capturedObject'] is None])
    proposed, effects = [], []
    assert len(receipt['outputs']) == len(request['outputs'])
    for row, output in zip(request['outputs'], receipt['outputs']):
        identity, prior_id = row['outputId'], row['previousOutputId']
        source, binding = originals[identity], bindings[row['bindingId']]
        old = prior.get(prior_id)
        numeric = row['profile'] == 'unified-integer-state-text-list.v1'
        # The original review describes the current captured name. The separate
        # explicit proposed name is authoritative only for new candidate intent.
        intent_source = copy.deepcopy(original)
        next(o for o in intent_source['outputs'] if o['id'] == identity)['name'] = row['listName']
        intent = (numeric_intent if numeric else bool_intent)(row, intent_source)
        assert output['intent'] == intent
        for k in ('outputId', 'previousOutputId', 'operation', 'bindingId', 'bindingAction', 'profile'):
            assert output[k] == row[k]
        assert output['componentContext'] == dict(reviewRecordId=request['componentReviewId'], outputId=identity,
            bindingId=row['bindingId'], afterComponent=report['afterComponent'], destination=original['destination'], binding=bound[row['bindingId']])
        conversion = version == 'v3' and row.get('typeConversion') is not None
        evidence(identity, binding, output, conversion)
        if version == 'v3':
            problems.append(verify_conversion(row, old, bound[binding['id']], intent, output['typeConversion']))
        expected_type = row['template']['dataType'] if numeric else 'Bool'
        problems.extend([source['kind'] != 'hmiResource', source['ownership'] != 'generated',
                         source['action'] != ('create' if row['operation'] == 'add' else 'update'),
                         bound[binding['id']]['afterDeclaration']['status'] != 'capturedDeclaration',
                         bound[binding['id']]['afterDeclaration']['dataType'] != expected_type, binding['declaredHmiType'] != expected_type])
        if old:
            prior_checks(identity, source, binding, old, prior_id)
            problems.extend([old['profile'] != row['profile'], not conversion and old['componentContext']['binding']['afterDeclaration']['dataType'] != expected_type])
            same_identity = identity == prior_id and all(old['intent'][k] == intent[k] for k in ('fileBase', 'textLibrary')) and old['intent']['lists'][0]['name'] == row['listName']
            problems.append(same_identity != (row['operation'] == 'update'))
            same_binding = row['bindingId'] == old['bindingId'] and binding['beforeMember'] == binding['afterMember'] and binding['property'] == old['componentContext']['binding']['selection']['property']
            problems.append(same_binding != (row['bindingAction'] == 'preserve'))
        else:
            problems.extend([identity in prior, row['bindingAction'] != 'preserve', binding['beforeMember'] is not None,
                bool(source['baseline']), bool(source['current']), not source['baselineComplete'], not source['currentComplete'], source['name'] != row['listName']])
        assert output['valueCoverage'] == (expected_coverage(row['template']) if numeric else None)
        assert output['stateChanges'] == (expected_changes(old['intent'] if old else None, intent) if numeric else None)
        if numeric:
            problems.append(row['template']['requireFullTypeCoverage'] and not output['valueCoverage']['allTypeValuesListed'])
        pair = output['comparison']['proposedFiles']
        assert len(pair) == 2
        for f, suffix, tree in zip(pair, ('.hmi.yml', '.TextLibrary.hmi.yml'), expected_documents(intent)):
            assert f['outputId'] == identity and f['fileName'] == row['template']['fileBase'] + suffix
            if receipt['status'] == 'candidatesReady':
                raw = read(f['artifact'])
                assert len(raw) == f['bytes']
                assert json.dumps(parse_candidate(raw), sort_keys=True) == json.dumps(tree, sort_keys=True)
        comparison(source, output['comparison'], pair)
        proposed.extend(pair)
        effects.append(dict(operation=row['operation'], previousOutputId=prior_id, outputId=identity, bindingAction=row['bindingAction'],
            currentObjectPath=source['objectPath'], previousListName=old['intent']['lists'][0]['name'] if old else None, proposedListName=row['listName'],
            retireFiles=prior_files(prior_id) if old else [], proposedFiles=pair, destructiveEffectPreviewOnly=old is not None, nativeExecutable=False))
    assert [r['outputId'] for r in receipt['removedOutputs']] == request['removeOutputIds']
    for removed in receipt['removedOutputs']:
        identity = removed['outputId']; source = originals[identity]; old = prior[identity]
        binding = next(b for b in bindings.values() if b['outputId'] == identity)
        assert removed['priorOutput'] == old and removed['binding'] == bound[binding['id']] and removed['bindingId'] == binding['id']
        evidence(identity, binding, removed); prior_checks(identity, source, binding, old, identity)
        problems.extend([binding['id'] != old['bindingId'], binding['property'] != old['componentContext']['binding']['selection']['property']])
        problems.extend([source['action'] != 'remove', source['kind'] != 'hmiResource', source['ownership'] != 'generated',
                         binding['afterMember'] is not None, bool(source['proposed']), not source['proposedComplete']])
        comparison(source, removed['comparison'], [])
        effects.append(dict(operation='remove', previousOutputId=identity, outputId=None, currentObjectPath=source['objectPath'],
                            retireFiles=prior_files(identity), proposedFiles=[], destructiveEffectPreviewOnly=True, nativeExecutable=False))
    preserved = []
    for identity in request['retainOutputIds']:
        source = originals[identity]
        problems.extend([source['action'] != 'retain', not source['currentComplete'], not source['current'], bool(reports[identity]['conflicts'])])
        for f in files(source['current']):
            preserved.append(dict(outputId=identity, fileName=f['fileName'], bytes=f['bytes'],
                artifact=dict(path='hmi-component-reviews/' + f['sha256'] + '.source', sha256=f['sha256'])))
    assert receipt['preservedFiles'] == preserved
    problems.append(preserved != previous['preservedFiles'])
    for values in ([f['fileName'].casefold() for f in proposed + preserved],
                   [o['intent']['textLibrary'].casefold() for o in receipt['outputs']],
                   [o['intent']['lists'][0]['name'].casefold() for o in receipt['outputs']]):
        problems.append(len(values) != len(set(values)))
    for output in receipt['outputs'][1:]:
        problems.append(any(output['intent'][k] != receipt['outputs'][0]['intent'][k] for k in ('languages', 'defaultLanguage')))
    vacated = {originals[o['outputId']]['objectPath'] for o in request['outputs']} | {originals[o]['objectPath'] for o in request['removeOutputIds']}
    destination = reviews[original['destination']['reviewId']]['model']['nodes']
    for row in request['outputs']:
        parent = next(n for n in destination if n['objectPath'] == originals[row['outputId']]['parentPath'])
        problems.append(any(n['parentId'] == parent['nodeId'] and n['objectPath'] not in vacated and n['name'].casefold() == row['listName'].casefold() for n in destination))
    if version == 'v3':
        for effect in effects:
            selected_row = next((r for r in request['outputs'] if r['outputId'] == effect['outputId']), {})
            effect.update(typeConversion=selected_row.get('typeConversion'), plcValueConversionPerformed=False)
    assert receipt['effectsPreview'] == effects
    transfers = [dict(fileName=f['fileName'], fromOutputId=old['outputId'], toOutputId=f['outputId'], previewOnly=True)
                 for f in proposed for old in previous['files'] if old['fileName'].casefold() == f['fileName'].casefold()
                 and old['outputId'] != next(o['previousOutputId'] for o in request['outputs'] if o['outputId'] == f['outputId'])]
    assert receipt['filenameTransfers'] == transfers
    ready = not any(problems)
    assert receipt['status'] == ('candidatesReady' if ready else 'conflict'), ('independent eligibility', problems)
    assert bool(receipt['conflicts']) == (not ready)
    assert receipt['files'] == (proposed if ready else [])
    assert all(receipt[k] is False for k in ('nativeExecutable', 'nativeQualified', 'ownershipVerified', 'preservationVerified'))


def negative_controls(request, receipt, read):
    controls = []
    for label, mutate in [
        ('previous lineage omitted', lambda r: r.update(previousRecordId='0'*64)),
        ('prior coverage promoted', lambda r: r['coverage'].update(uncoveredPredecessorOutputIds=['invented'])),
        ('manual baseline invented', lambda r: r['outputs'][0]['comparison'].update(baselineFiles=[])),
        ('proposed file ownership swapped', lambda r: r['outputs'][0]['comparison']['proposedFiles'][0].update(outputId='wrong')),
        ('wrong explicit old member', lambda r: r['outputs'][0]['componentContext']['binding']['selection']['beforeMember'].update(memberPath=['invented'])),
        ('false rebind label', lambda r: r['outputs'][0].update(bindingAction='rebind')),
        ('wrong translation', lambda r: r['outputs'][0]['intent']['texts'][0]['translations'].update({'en-US':'invented'})),
        ('language order reversed', lambda r: r['outputs'][0]['intent']['languages'].reverse()),
        ('effect preview omitted', lambda r: r['effectsPreview'].pop()),
        ('native applicability promoted', lambda r: r.update(nativeExecutable=True)),
        ('member declaration omitted', lambda r: r['coverage']['members']['observations'].pop()),
    ]:
        changed = copy.deepcopy(receipt); mutate(changed)
        try:
            verify(request, changed, read)
        except (AssertionError, ValidationError):
            controls.append(label)
        else:
            raise AssertionError('Oracle accepted ' + label)
    # Keep all candidate descriptors internally consistent with newly encoded,
    # honestly hashed YAML. Only independent original intent can reject these.
    for label, output_index, file_index in (
            ('honest-hash changed translation', 0, 1), ('honest-hash language order', 0, 1),
            ('honest-hash numeric range', 1, 0), ('honest-hash text reference', 0, 0)):
        changed = copy.deepcopy(receipt)
        output = changed['outputs'][output_index]
        candidate = output['comparison']['proposedFiles'][file_index]
        original_reference = copy.deepcopy(candidate['artifact'])
        tree = parse_candidate(read(original_reference))
        if file_index == 1:
            library = tree['TextLibraries'][output['intent']['textLibrary']]
            if 'translation' in label:
                next(iter(library['Entries'].values()))['Text'][0] = 'forged supplied meaning'
            else:
                library['Languages'].reverse()
        else:
            entry = next(iter(next(iter(tree['TextListContainers']['TextList']['ResourceLists'].values()))['Entries'].values()))
            if 'range' in label:
                entry.update(Value=999, FromValue=999, ToValue=999)
            else:
                entry['Text'] = 'Invented.WrongText'
        raw = b'#Version: 2.0\n' + yaml.safe_dump(tree, allow_unicode=True, sort_keys=False).encode('utf-8')
        identity = hashlib.sha256(raw).hexdigest()
        reference = dict(path='hmi-component-reviews/' + identity + '.candidate', sha256=identity)
        groups = [changed['files'], *[o['comparison']['proposedFiles'] for o in changed['outputs']],
                  *[e['proposedFiles'] for e in changed['effectsPreview']]]
        for group in groups:
            for file in group:
                if file['artifact'] == original_reference:
                    file.update(artifact=copy.deepcopy(reference), bytes=len(raw))
        def altered_read(selected):
            return raw if selected == reference else read(selected)
        try:
            verify(request, changed, altered_read)
        except (AssertionError, ValidationError):
            controls.append(label)
        else:
            raise AssertionError('Independent YAML oracle accepted ' + label)
    return controls
