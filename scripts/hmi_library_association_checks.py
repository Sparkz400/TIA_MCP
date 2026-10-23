"""Independent original-file and saved-observation oracle for explicit C01 v5 associations."""
import copy
import hashlib
import re
import uuid
from xml.dom import Node, minidom


def verify(record, request, originals, reviews):
    report = record['report']
    assert request['schemaVersion'] in ('hmi-component-review-request.v5','hmi-component-review-request.v6','hmi-component-review-request.v7')
    assert report['schemaVersion'] == request['schemaVersion'].replace('-request','')
    files = {f['id']: f for f in request['files']}
    for identity, file in files.items():
        data = originals[identity]
        assert len(data) == file['bytes'] and hashlib.sha256(data).hexdigest() == file['sha256']
    selections = request['libraryAssociations']
    assert len(selections) == len(report['libraryAssociations'])
    covered = set(); mismatch = unavailable = 0

    def one(parent, name):
        elements = [n for n in parent.childNodes if n.nodeType == Node.ELEMENT_NODE and n.tagName == name]
        assert len(elements) == 1
        return elements[0]

    def scalar(parent, name, readonly=False):
        element = one(parent, name)
        assert len(element.childNodes) == 1 and element.firstChild.nodeType == Node.TEXT_NODE
        assert dict(element.attributes.items()) == ({'ReadOnly': 'true'} if readonly else {})
        return element.firstChild.data

    for selection, row in zip(selections, report['libraryAssociations']):
        assert row['selection'] == selection
        component = request[selection['scope']]
        assert selection['objectPath'] in component['objectPaths'] and row['reviewId'] == component['reviewId']
        node = next(n for n in reviews[component['reviewId']]['model']['nodes'] if n['objectPath'] == selection['objectPath'])
        assert node['kind'] == 'block' and node['metadata']['kind'] == 'FB'
        key = (selection['scope'], selection['objectPath']); assert key not in covered; covered.add(key)
        file = files[selection['fileId']]
        assert row['file'] == {k: file[k] for k in ['id', 'fileName', 'sha256', 'bytes']}
        text = originals[selection['fileId']].decode('utf-8-sig')
        assert '<!DOCTYPE' not in text
        document = minidom.parseString(text); root = document.documentElement
        assert root.tagName == 'Document' and [e.tagName for e in root.childNodes if e.nodeType == Node.ELEMENT_NODE] == ['Engineering', 'SW.Blocks.CodeBlockLibraryTypeVersion']
        assert dict(one(root, 'Engineering').attributes.items()) == {'version': 'V21'}
        version = one(root, 'SW.Blocks.CodeBlockLibraryTypeVersion'); attributes = one(version, 'AttributeList')
        assert len(root.getElementsByTagName('SW.Blocks.CodeBlockLibraryTypeVersion')) == 1
        block = one(one(version, 'ObjectList'), 'SW.Blocks.FB')
        assert len(root.getElementsByTagName('SW.Blocks.FB')) == 1 and block.getAttribute('CompositionName') == 'ContentObject'
        type_id = uuid.UUID(scalar(attributes, 'LibraryTypeGuid', True)); version_id = uuid.UUID(scalar(attributes, 'Guid', True))
        assert type_id.int and version_id.int and scalar(attributes, 'State', True) == 'Committed'
        name = scalar(attributes, 'LibraryTypeName', True); assert name == scalar(one(block, 'AttributeList'), 'Name')
        assert row['exportIdentity'] == dict(profile='v21-full-fb-library-version.v1', typeGuid=str(type_id), versionGuid=str(version_id), typeName=name, versionNumber=scalar(attributes, 'VersionNumber'))
        observation = node['metadata'].get('libraryInstance')
        if not isinstance(observation, dict): observation = None
        assert row['capturedObservation'] == observation
        agreement = 'unavailable'
        if observation and observation.get('schemaVersion') == 'plc-library-instance-observation.v1' and observation.get('source') == 'LibraryTypeInstanceInfo.LibraryTypeVersion' and observation.get('status') == 'linked':
            try:
                assert all(isinstance(observation[key],str) and re.fullmatch(r'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}',observation[key]) for key in ['typeGuid','versionGuid'])
                observed_type = uuid.UUID(observation['typeGuid']); observed_version = uuid.UUID(observation['versionGuid'])
                if observed_type.int and observed_version.int:
                    agreement = 'match' if (observed_type, observed_version) == (type_id, version_id) else 'different'
            except (AssertionError, ValueError, TypeError, KeyError, AttributeError): pass
        assert row['identifierAgreement'] == agreement
        mismatch += agreement == 'different'; unavailable += agreement == 'unavailable'
        for flag in ['sourceAuthenticityVerified', 'libraryIdentityVerified', 'contentAssociationVerified', 'dependencyClosureVerified', 'runtimeInstanceVerified']:
            assert row[flag] is False
    expected_coverage = []
    for scope in ['beforeComponent', 'afterComponent']:
        component = request[scope]
        if component is None: continue
        for path in component['objectPaths']:
            expected_coverage.append(dict(scope=scope, reviewId=component['reviewId'], objectPath=path, associationSelected=(scope, path) in covered, libraryIdentityVerified=False))
    assert report['libraryCoverage'] == expected_coverage
    expected = dict(libraryAssociationCount=len(selections), libraryIdentifierMismatchCount=mismatch, libraryIdentifierUnavailableCount=unavailable,
                    unassociatedComponentCount=sum(not row['associationSelected'] for row in expected_coverage))
    assert all(report['summary'][key] == value for key, value in expected.items())
    for flag in ['nativeExecutable', 'nativeQualified', 'sourceAuthenticityVerified', 'wholeProjectPreservationVerified', 'consumerCoverageComplete']:
        assert report[flag] is False


def negative_controls(record, request, originals, reviews):
    controls = {
        'invented agreement': lambda r: r['report']['libraryAssociations'][0].update(identifierAgreement='different'),
        'invented export identity': lambda r: r['report']['libraryAssociations'][0]['exportIdentity'].update(versionGuid='10000000-0000-0000-0000-000000000001'),
        'invented saved observation': lambda r: r['report']['libraryAssociations'][0]['capturedObservation'].update(source='name'),
        'promoted library identity': lambda r: r['report']['libraryAssociations'][0].update(libraryIdentityVerified=True),
        'omitted coverage': lambda r: r['report']['libraryCoverage'].clear(),
    }
    for label, change in controls.items():
        mutated = copy.deepcopy(record); change(mutated)
        try: verify(mutated, request, originals, reviews)
        except AssertionError: continue
        raise AssertionError('Independent library oracle accepted ' + label)
    return list(controls)
