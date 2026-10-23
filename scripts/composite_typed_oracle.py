"""Check typed composite declarations against original inputs, not generated expectations."""
import hashlib
import json


def original(root, folder, reference):
    raw = (root / folder / (reference + '.json')).read_bytes()
    assert hashlib.sha256(raw).hexdigest() == reference
    envelope = json.loads(raw)
    raw = (root / envelope['input']['path']).read_bytes()
    assert hashlib.sha256(raw).hexdigest() == envelope['input']['sha256']
    return json.loads(raw)


def verify_selection(root, request, selection):
    selector, evidence, profile = (selection[k] for k in ('selector', 'evidence', 'profile'))
    phase = selector['phase']; encoder = selector['kind'] == 'technologyEncoder'
    packet = original(root, 'technology-profile-reviews' if encoder else 'drive-profile-reviews', selector['recordRef'])
    assert packet['reviewId'] == request[phase]['reviewId']
    assert profile == dict(identity=packet[phase]['identity'], valueDomain=packet[phase]['valueDomain'], selectionPath=packet['technologyPath' if encoder else 'hardwarePath'])
    rows = packet['encoders'][phase]['observations'] if encoder else packet[phase]['telegrams']
    row, = [r for r in rows if r['id'] == selector['id']]
    if encoder:
        assert evidence['encoder'] == row and evidence['phase'] == phase
        assert evidence['sourceIndex'] == rows.index(row)
        contradictions = []
        if row['state'] == 'connected' and row['sensorIndex'] == 0: contradictions.append('connectedWithZeroSensorIndex')
        if row['state'] == 'disconnected' and row['sensorIndex'] not in (None, 0): contradictions.append('disconnectedWithSensorIndex')
        assert evidence['contradictions'] == contradictions
        assert evidence['nativeConnectionVerified'] is False and evidence['targetCompatibilityVerified'] is False
        if row['route'] == 'startdriveTelegram' and row['target']:
            target = row['target']; drive = original(root, 'drive-profile-reviews', target['recordId'])
            assert drive['reviewId'] == packet['reviewId'] and target['phase'] == phase
            telegram, = [r for r in drive[phase]['telegrams'] if r['id'] == target['telegramId']]
            actual = evidence['targetEvidence']
            assert actual['telegramEvidence']['telegram'] == telegram and actual['telegramEvidence']['phase'] == phase
            assert actual['driveIdentity'] == drive[phase]['identity'] and actual['driveHardwarePath'] == drive['hardwarePath']
            assert actual['driveValueDomain'] == drive[phase]['valueDomain']
            assert actual['valueDomainComparison'] == ('equalDeclarations' if drive[phase]['valueDomain'] == packet[phase]['valueDomain'] else 'differentDeclarations')
            number = 'outsideDocumentedProfile' if packet[phase]['identity']['technologyType'] != 'TO_ExternalEncoder' else 'unknown' if telegram['number'] is None else 'documentedNumber' if telegram['number'] in (81, 83) else 'differentNumber'
            assert actual['externalEncoderNumberCheck'] == number
        return
    telegram = evidence if selector['kind'] == 'driveTelegram' else evidence['telegram']
    assert telegram['telegram'] == row and telegram['phase'] == phase and telegram['sourceIndex'] == rows.index(row)
    assert telegram['nativeCompatibilityVerified'] is False
    for address in telegram['addresses']:
        supplied = row['addresses'][address['sourceIndex']]
        assert address['address'] == supplied
        start = None if supplied['startAddress'] is None or supplied['addressUnit'] == 'unknown' else supplied['startAddress'] * (8 if supplied['addressUnit'] == 'byte' else 1)
        length = None if supplied['length'] is None or supplied['lengthUnit'] == 'unknown' else supplied['length'] * (8 if supplied['lengthUnit'] == 'byte' else 1)
        assert address['startBit'] == start and address['lengthBits'] == length
        assert address['endBitExclusive'] == (None if start is None or length is None else start + length)
        assert address['nativeAddressVerified'] is False
    if selector['kind'] == 'driveTelegramAddress':
        address, = [a for a in telegram['addresses'] if a['address']['id'] == selector['addressId']]
        assert evidence['address'] == address


def verify_mappings(request, report):
    selections = {json.dumps(s['selector'], sort_keys=True): s for s in report['selections']}
    def selected(s): return selections[json.dumps(s, sort_keys=True)]
    for mapping, result in zip(request['mappings'], report['mappings'], strict=True):
        assert result['mapping'] == mapping and result['nativeVerified'] is False
        source, target = selected(mapping['source']), selected(mapping['target'])
        if mapping['purpose'] == 'encoder':
            actual = target['evidence']['encoder']['target']; declared = mapping['source']
            assert actual == dict(recordId=declared['recordRef'], phase=declared['phase'], telegramId=declared['id'])
            assert source['profile']['valueDomain'] == target['profile']['valueDomain']
            assert result['constraints'] == [dict(kind='nativeEligibility', state='notVerified'), dict(kind='originalTelegramReference', state='exactReference'), dict(kind='valueDomain', state='equalDeclarations'), dict(kind='externalEncoderNumber', state=target['evidence']['targetEvidence']['externalEncoderNumberCheck'])]
        else:
            assert mapping['purpose'] == 'telegram'
            assert result['constraints'] == [dict(kind='nativeEligibility', state='notVerified')]
    # Independently close only admitted mapping edges from each impacted AML owner.
    for impact in report['impacts']:
        phase, owner = impact['phase'], impact['amlImpact']['path']
        starts = {s['key'] for s in report['selections'] if s['selector']['phase'] == phase and s['selector']['kind'].startswith('aml') and s['evidence'].get('owner', s['evidence'].get('path')) == owner}
        edges = {}
        for row in report['mappings']:
            if row['state'] == 'declaredAssociation':
                edges.setdefault(selected(row['mapping']['source'])['key'], set()).add(selected(row['mapping']['target'])['key'])
        depths = {key: 0 for key in starts}; pending = list(starts)
        while pending:
            key = pending.pop(0)
            for target in edges.get(key, ()):
                if target not in depths: depths[target] = depths[key] + 1; pending.append(target)
        expected = {key: depth for key, depth in depths.items() if not json.loads(key)['kind'].startswith('aml')}
        assert set(impact['destinationKeys']) == set(expected) and impact['destinationDepths'] == expected
