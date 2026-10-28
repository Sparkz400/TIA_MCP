"""Independent file-only verifier for the narrow real bilingual import packet."""
from pathlib import Path
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile
from io import BytesIO

from qualify_translation_category_probe import scan_store, require, WindowsFiles
from verify_watch_workflow import canonical_sha

S = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
# ExportProjectTexts labels view paths in the TIA UI language; German and English were observed natively.
MAIN_COMMENT_VIEWS = (['Programmbausteine', 'Main', 'Bausteinkommentar'], ['Program blocks', 'Main', 'Block comment'])


def workbook(data):
    with ZipFile(BytesIO(data)) as archive:
        require(len(archive.infolist()) == 9 and sum(x.file_size for x in archive.infolist()) <= 32 * 1024 * 1024, 'Unrecognized workbook package')
        parts = {x.filename: archive.read(x) for x in archive.infolist()}
    require(all(b'<!DOCTYPE' not in p and b'<!ENTITY' not in p for p in parts.values()), 'DTD refused')
    shared = [x.find(S + 't').text or '' for x in ET.fromstring(parts['xl/sharedStrings.xml'])]
    result = {}
    for i in (1, 2):
        part = f'xl/worksheets/sheet{i}.xml'
        rows = ET.fromstring(parts[part]).find(S + 'sheetData')
        for ordinal, row in enumerate(rows):
            values = {}
            for cell in row:
                require(cell.find(S + 'f') is None, 'Formula refused')
                if cell.attrib.get('t') == 's': text = shared[int(cell.find(S + 'v').text)]
                elif cell.attrib.get('t') == 'inlineStr':
                    node = cell.find(S + 'is')
                    require(len(node) == 1 and node[0].tag == S + 't', 'Rich inline text refused')
                    text = node[0].text or ''
                else: raise ValueError('Unknown native cell encoding')
                require(cell.attrib['r'] not in values, 'Duplicate cell')
                values[cell.attrib['r']] = text
            number = row.attrib['r']
            if ordinal == 0:
                require([values[chr(65+j)+'1'] for j in range(22)] == ['Category','ViewPath','Internal ID','Substitutions','de-DE*','en-GB'] + [f'ref="{j}"' for j in range(16)], 'Changed native headers')
                continue
            require(set(values) == {c + number for c in 'ABCDEF'}, 'Incomplete data row')
            record = tuple(values[c + number] for c in 'ABCDEF')
            require(record[2] and record[2] not in result, 'Duplicate native key')
            result[record[2]] = (part, *record)
    return result


def peer(data, profile, old, new):
    if profile == 'exact': return data
    require(profile == 'tia21-simaticml-translation-v1' and b'<!DOCTYPE' not in data, 'Unknown peer profile')
    doc = ET.fromstring(data)
    require(doc.tag == 'Document', 'Native XML root differs')
    created = doc.findall('./DocumentInfo/Created'); require(len(created) == 1, 'Creation metadata differs')
    created[0].text = ''
    blocks = [b for b in doc.findall('SW.Blocks.OB') if b.findtext('AttributeList/Name') == 'Main']
    if old != new and blocks:
        require(len(blocks) == 1 and blocks[0].findtext('AttributeList/Number') == '1', 'Main OB1 native identity differs')
        nodes = []
        for item in blocks[0].findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem/AttributeList"):
            if item.findtext('Culture') == 'en-GB': nodes.extend(item.findall('Text'))
        require(len(nodes) == 1 and not list(nodes[0]), 'Exact native comment leaf required')
        if nodes[0].text in (old, new): nodes[0].text = '__approved_target__'
    return ET.tostring(doc, encoding='utf-8')


def verify(root):
    root = Path(root).resolve()
    def load(path): return json.loads(path.read_text(encoding='utf-8-sig'))
    receipts = root
    result = load(receipts / 'result.json')
    require(result['completed'] and result['productionEnabled'] is False, 'Incomplete native packet')
    require(result['preexistingPreserved'] and result['automaticReplay'] is False, 'Ownership/replay boundary differs')
    receipt_roots = [receipts]
    for retained in result.get('priorPackets', []):
        path = Path(retained['path'])
        require(hashlib.sha256(path.read_bytes()).hexdigest() == retained['sha256'], 'Prior packet changed')
        if path.name == 'result.json': receipt_roots.append(path.parent)
    root = Path(load(receipts / 'admission.json').get('artifactRoot', str(root))).resolve()
    def artifact(value):
        path = (root / value['relativePath']).resolve()
        require(path.is_relative_to(root), 'Artifact escaped run root')
        data = path.read_bytes(); require(hashlib.sha256(data).hexdigest() == value['sha256'], 'Retained artifact changed')
        return data
    def parsed(value): return json.loads(artifact(value))
    locks = WindowsFiles(); handle = locks.lock(root / 'engineering-operations' / 'store.lock')
    try: stores = scan_store(root, [])
    finally: require(locks.close(handle), 'Verification store lock did not close')
    require(not any(s['pending'] for s in stores), 'Pending native resource remains')
    category = load(receipts / 'category-verification.json')
    require(category['populatedPlcRows'] > 0 and not category['restrictedProfilesEnabled'] and not category['categoryFilteringReliedOn'], 'Category scope differs')
    trials = result['trials']; require([t['name'] for t in trials] == ['noop', 'edit'], 'Exact two-trial packet required')
    imported = [load(p) for p in root.glob('translation-native/import-*/native-return.json')]
    restores = [load(p) for p in root.glob('translation-native/restore-*/restore-return.json')]
    require(len(imported) == len(restores) == 2, 'Exactly two imports and two restorations required')
    edit_oracle = load(receipts / 'edit-oracle.json')['requested']
    checks = []
    for trial in trials:
        revisions = sorted((root / 'engineering-operations' / trial['id']).glob('*.json'))
        final = load(revisions[-1]); applied = load(revisions[trial['appliedRevision']])
        require(final['state'] == 'reconciled' and applied['state'] == 'succeeded', 'Lifecycle/recovery not verified')
        calls = [load(p) for folder in receipt_roots for p in sorted(folder.glob('*-qualification_translation_apply.json'))
                 if load(p)['request']['operationId'] == trial['id']]
        require(len(calls) == 4 and [c['response'].get('isError', False) for c in calls] == [True, True, False, True], 'Missing stale/repeated native request refusals')
        require(calls[0]['request']['planSha256'] != applied['planSha256'] and calls[0]['request']['revision'] == 0 and
                calls[1]['request']['planSha256'] == applied['planSha256'] and calls[1]['request']['revision'] == 1 and
                calls[2]['request']['planSha256'] == applied['planSha256'] and calls[2]['request']['revision'] == 0 and
                calls[3]['request']['revision'] == applied['revision'], 'Refusals did not exercise exact digest/revision/replay boundaries')
        binding = parsed(applied['plan']['inputs'][0]); baseline = binding['baseline']
        before = workbook(artifact(baseline['workbook'])); candidate = workbook(artifact(binding['candidate']))
        require(before.keys() == candidate.keys(), 'Native key set changed')
        changes = [key for key in before if before[key] != candidate[key]]
        require(changes == ([] if trial['name'] == 'noop' else [edit_oracle['nativeKey']]), 'Candidate exceeds reviewed edit')
        if changes:
            key = changes[0]; require(before[key][:-1] == candidate[key][:-1] and candidate[key][-1] == edit_oracle['replacement'], 'Non-target edit')
            view = before[key][2].split('\\')
            require(len(view) == 5 and view[2:] in MAIN_COMMENT_VIEWS and before[key][1] == '<BlockCommentCategoryData>', 'Edit does not bind exact Main comment')
            old, new = before[key][-1], candidate[key][-1]
        else: old = new = ''
        evidence = {e['gate']: e['artifact'] for e in applied['evidence']}
        for gate in ['dispatch.readback', 'dispatch.reopen']:
            observed = parsed(evidence[gate]); require(workbook(artifact(observed['workbook'])) == candidate, 'Native full readback differs')
            require(observed['scope'] == baseline['scope'], 'Scope/languages changed')
            peers = {p['identity']: p for p in observed['peers']}
            require(peers.keys() == {p['identity'] for p in baseline['peers']}, 'Native peer inventory changed')
            for p in baseline['peers']:
                q = peers[p['identity']]
                require(p['comparisonProfile'] == q['comparisonProfile'] and peer(artifact(p['evidence']), p['comparisonProfile'], old, new) == peer(artifact(q['evidence']), q['comparisonProfile'], old, new), 'Native engineering peer changed: '+p['identity'])
        reopened = parsed(evidence['dispatch.reopen'])
        require(reopened['target']['sessionEpoch'] != baseline['target']['sessionEpoch'], 'No fresh reopen')
        recovered_pair = parsed(final['recoveryResolutions'][0]['evidence'])
        recovered = recovered_pair['reopened']
        require(recovered_pair['restored']['target']['sessionEpoch'] != recovered['target']['sessionEpoch'], 'Recovered copy was not freshly reopened')
        for observation in recovered_pair.values():
            require(Path(observation['target']['projectFilePath']).resolve() == Path(trial['recoveryPath']).resolve(), 'Recovery observation escaped reserved destination')
            require(workbook(artifact(observation['workbook'])) == before, 'Restored original texts differ')
            require(observation['scope'] == baseline['scope'], 'Recovered scope/languages differ')
            peers = {p['identity']: p for p in observation['peers']}
            require(peers.keys() == {p['identity'] for p in baseline['peers']}, 'Recovered peer inventory differs')
            for p in baseline['peers']:
                q = peers[p['identity']]
                require(p['comparisonProfile'] == q['comparisonProfile'] and
                        peer(artifact(p['evidence']), p['comparisonProfile'], old, old) == peer(artifact(q['evidence']), q['comparisonProfile'], old, old),
                        'Recovered original engineering peer differs: '+p['identity'])
        native = [r for r in imported if r['operationId'] == trial['id']]
        require(len(native) == 1 and native[0]['state'] == 'Info' and native[0]['updateSourceLanguage'] is False, 'Native import did not return Info once')
        require(native[0]['candidateSha256'] == binding['candidate']['sha256'], 'Imported candidate differs')
        for gate in ['dispatch.compile', 'dispatch.save', 'dispatch.cleanup']:
            require(parsed(evidence[gate])['disposition'] == 'passed', 'Missing completion gate '+gate)
        if trial['name'] == 'edit':
            require(any(load(p)['state'] == 'outcomeUnknown' for p in revisions) and trial['restorationReplyLost'], 'Lost restore reply not exercised')
        checks.append({'trial': trial['name'], 'operationId': trial['id'], 'rows': len(before), 'changedTargets': len(changes),
                       'oneImport': True, 'readback': True, 'preservation': True, 'reopen': True, 'restoration': True})
    owned_exits = 0
    for folder in receipt_roots:
        close = load(folder / 'exact-close.json')
        require(close['hostExitCode'] == 0 and close['closeInterrupted'] is None and close['tree']['completed'], 'Process cleanup incomplete')
        require(close['tree']['workerStartupCaptured'] and close['tree']['nativeStartupInventoryComplete'] and
                not close['tree']['startupOutcomeUnknown'] and not close['tree']['forcedTermination'], 'Incomplete lifetime inventory')
        require(sorted(e['role'] for e in close['tree']['exits']) == ['host', 'native', 'worker'], 'Incomplete owned process roles')
        require(all(e['proof']['completed'] and e['proof']['exitCode'] == 0 and not e['proof']['deadlineOrCancellationObserved'] and
                    all(e['proof'][key] for key in ['processExitVerified', 'birthAndImageVerified', 'sameOsHandleRetained', 'handleClosed'])
                    for e in close['tree']['exits']), 'Non-normal or unverified owned exit')
        owned_exits += len(close['tree']['exits'])
    return {'completed': True, 'nativeExecuted': True, 'productionEnabled': False, 'trials': checks,
            'categoryRestriction': category, 'pendingResources': 0, 'stalePlanRevisionAndReplayRefused': True, 'exactOwnedExits': owned_exits}


if __name__ == '__main__':
    value = verify(Path(sys.argv[1]))
    print(json.dumps(value, indent=2, ensure_ascii=False))
