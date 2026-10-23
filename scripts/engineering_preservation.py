"""Native payload equality and saved-project recovery checks for owned fixtures."""
import base64
import hashlib
import json
import pathlib
import re
from xml.dom import Node, minidom

from smoke_mcp import ROOT

PAYLOAD_HASH_POLICY = 'native-payload.v3-document-comments-pi-qnames'


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def decode_native_artifact(exported, artifact):
    require(artifact["complete"] is True, "Complete native XML bytes are required")
    content = base64.b64decode(artifact["data"], validate=True)
    digest = exported["artifact"]["sha256"].lower()
    require(hashlib.sha256(content).hexdigest().lower() == digest, "Native XML byte SHA-256 differs")
    require(artifact["sha256"].lower() == digest, "Artifact API SHA-256 differs")
    return content.decode("utf-8-sig")


def native_payload(xml):
    """Ignore export provenance/formatting; retain comments, IDs and QName bindings."""
    if '<!DOCTYPE' in xml.upper() or '<!ENTITY' in xml.upper():
        raise ValueError('DTD/entity declarations are not allowed in fixture XML.')
    document = minidom.parseString(xml)

    def visit(node, namespaces, preserve=False):
        if node.nodeType == Node.ELEMENT_NODE:
            namespaces = dict(namespaces)
            for attr in node.attributes.values():
                if attr.name == 'xmlns' or attr.prefix == 'xmlns':
                    namespaces['' if attr.name == 'xmlns' else attr.localName] = attr.value
            space = node.getAttributeNS('http://www.w3.org/XML/1998/namespace', 'space')
            preserve = preserve if not space else space == 'preserve'

            def value(text):
                # A namespace alias appearing in a native attribute/text value is
                # semantically relevant even when no element uses that namespace.
                bindings = tuple((prefix, namespaces.get(prefix)) for prefix in sorted(set(
                    re.findall(r'(?<![\w.-])([A-Za-z_][\w.-]*):[A-Za-z_][\w.-]*', text))))
                return text, bindings

            attrs = tuple(sorted((attr.namespaceURI or '', attr.localName or attr.name, value(attr.value))
                                 for attr in node.attributes.values()
                                 if attr.name != 'xmlns' and attr.prefix != 'xmlns'))
            has_elements = any(child.nodeType == Node.ELEMENT_NODE for child in node.childNodes)
            children = []
            for child in node.childNodes:
                if node is document.documentElement and child.nodeType == Node.ELEMENT_NODE and not child.namespaceURI and child.localName == 'DocumentInfo':
                    continue
                if child.nodeType in (Node.TEXT_NODE, Node.CDATA_SECTION_NODE):
                    if child.data.strip() or preserve or not has_elements:
                        children.append(('text', value(child.data)))
                else:
                    children.append(visit(child, namespaces, preserve))
            return ('element', node.namespaceURI or '', node.localName or node.nodeName, attrs, tuple(children))
        if node.nodeType == Node.COMMENT_NODE:
            return ('comment', node.data)
        if node.nodeType == Node.PROCESSING_INSTRUCTION_NODE:
            return ('instruction', node.target, node.data)
        raise ValueError('Unsupported XML node: ' + str(node.nodeType))

    return ('document', tuple(visit(node, {'xml': 'http://www.w3.org/XML/1998/namespace'})
                              for node in document.childNodes))


def native_payload_hash(xml):
    return hashlib.sha256(json.dumps(native_payload(xml), ensure_ascii=False, separators=(',', ':')).encode('utf-8')).hexdigest()


def check_native_blocks(call, expected, evidence, persist):
    """Record each actual export before asserting full native payload equality."""
    evidence['blocks'] = {}
    for name, xml in expected.items():
        actual = call('block_read', {'blockName': name})
        row = {'expectedPayloadSha256': native_payload_hash(xml),
               'actualPayloadSha256': native_payload_hash(actual['xml']),
               'nativeExport': actual.get('artifact'), 'block': actual.get('block')}
        row['passed'] = row['actualPayloadSha256'] == row['expectedPayloadSha256']
        evidence['blocks'][name] = row
        persist()
        require(row['passed'], 'Native payload differs for ' + name)
    evidence['nativeEqualityPassed'] = True
    persist()


def reopen_owned_project(call, path):
    project = pathlib.Path(path).resolve()
    require(project.is_relative_to((ROOT / 'artifacts').resolve()), 'Only this workspace fixture may be reopened')
    require(project.name.startswith('TiaMcpScratch_') and project.suffix.lower() == '.ap21', project)
    detached = call('tia_detach')
    require(detached['disposedOwnedPortalConnection'] is True, detached)
    started = call('tia_start')
    require(started['ownsPortalProcess'] is True and started['projects'] == [], started)
    opened = call('project_open', {'path': str(project)})
    require(opened['opened'] is True and pathlib.Path(opened['project']['path']).resolve() == project, opened)
    return {'processId': started['processId'], 'project': opened['project'], 'opened': True,
            'saveSource': 'Native apply compile/save or checkpoint archive; project_open does not save'}


def recover_and_reopen(call, prepared, baseline, changed, evidence, persist, read_inspection=None,
                       before_inspection=None, after_inspection=None):
    """Restore in the original session, then independently reopen both saved copies.

    change_restore deliberately cannot cross a TIA process boundary. It first
    retains the applied project and retrieves the checkpoint to a new directory;
    both paths can then be opened in fresh owned instances without replaying a
    mutation or bypassing that guard. Leave the recovered copy open for the next
    fixture. Compilation evidence belongs to apply, not these read-only reopens.
    """
    restored = call('change_restore', {'changeId': prepared['changeId'], 'previewHash': prepared['previewHash']})
    evidence['recovery'] = restored
    persist()
    require(restored['state'] == 'restored', restored)
    original_path = prepared['preview']['project']['path']
    recovered_path = restored['recoveredProject']['path']
    require(pathlib.Path(original_path).resolve() != pathlib.Path(recovered_path).resolve(), 'Native evidence requirement failed: pathlib.Path(original_path).resolve() != pathlib.Path(recovered_path).resolve()')
    evidence['recoveredReadback'] = {}
    check_native_blocks(call, baseline, evidence['recoveredReadback'], persist)
    if read_inspection:
        inspected, native_xml = read_inspection()
        evidence['recoveredInspectionXml'] = native_xml
        evidence['recoveredCall'] = inspected
        persist()
        require(inspected == before_inspection, 'Recovered call/instance metadata differs')
    evidence['recoveredNativeEqualityPassed'] = True
    persist()
    try:
        evidence['appliedReopen'] = reopen_owned_project(call, original_path)
        persist()
        require(evidence['appliedReopen']['processId'] != evidence['processIdAtApply'], 'Applied reopen must use a fresh TIA process')
        check_native_blocks(call, changed, evidence['appliedReopen'], persist)
        if read_inspection:
            inspected, native_xml = read_inspection()
            evidence['appliedReopen'].update(call=inspected, inspectionXml=native_xml)
            persist()
            require(inspected == after_inspection, 'Saved/reopened call/instance metadata differs')
        evidence['savedAppliedEqualityPassed'] = True
        persist()
    except Exception as error:
        evidence['appliedReopenFailure'] = type(error).__name__ + ': ' + str(error)
        persist()
        raise
    finally:
        # A completed open permits controlled cleanup/reopen after a readback
        # failure. Do not initiate another startup after an uncertain failed open.
        if 'appliedReopen' in evidence:
            try:
                evidence['recoveredReopen'] = reopen_owned_project(call, recovered_path)
                persist()
                previous_processes = {evidence['processIdAtApply'], evidence['appliedReopen']['processId']}
                require(evidence['recoveredReopen']['processId'] not in previous_processes, 'Recovered reopen must use a fresh TIA process')
                check_native_blocks(call, baseline, evidence['recoveredReopen'], persist)
                if read_inspection:
                    inspected, native_xml = read_inspection()
                    evidence['recoveredReopen'].update(call=inspected, inspectionXml=native_xml)
                    persist()
                    require(inspected == before_inspection, 'Saved/reopened recovered call metadata differs')
                evidence['savedRecoveredEqualityPassed'] = True
                persist()
            except Exception as error:
                evidence['recoveredReopenFailure'] = type(error).__name__ + ': ' + str(error)
                persist()
                raise
