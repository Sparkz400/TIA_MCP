"""Synthetic endpoint correspondence; no Siemens installation or runtime claims."""
import copy
import unittest

from encoder_endpoints import capture_endpoint, endpoint, require_endpoint_calls, same
import test_encoder_hardware_qualification as fixtures
from test_encoder_hardware_qualification import connected_native, hardware_inspection


class EndpointTests(unittest.TestCase):
    def setUp(self):
        fixture = fixtures.EncoderEvidenceTests(); fixture.setUp()
        self.profile = fixture.profile

    def capture(self, project='original.ap21', process=42, name='Fixture'):
        root = '/projects/' + name + '/devices/Station/items/Rail'
        module = root + '/items/TiaMcp_EncoderHardware'
        plc = root + '/items/CPU/plc/PLC'
        inventory = {'project': {'path': project, 'name': name}, 'processId': process, 'warnings': [],
                     'completeness': {'scopeComplete': True, 'truncated': False},
                     'plcs': [{'path': plc, 'hardwarePath': root + '/items/CPU', 'name': 'PLC'}],
                     'hardware': [{'path': root + '/items/CPU', 'name': 'CPU', 'positionNumber': 1, 'typeIdentifier': 'cpu'},
                                  {'path': module, 'name': 'TiaMcp_EncoderHardware', 'positionNumber': 2,
                                   'typeIdentifier': self.profile['moduleTypeIdentifier']},
                                  {'path': module + '/items/' + self.profile['channelItemName'], 'name': self.profile['channelItemName'],
                                   'positionNumber': 1, 'typeIdentifier': None}]}
        native = connected_native(self.profile, project, process, plc, module, inventory)
        calls = [{'tool': 'project_inventory', 'status': 'passed', 'arguments': {'maxItems': 2000}, 'result': copy.deepcopy(inventory)},
                 {'tool': 'technology_read', 'status': 'passed', 'arguments': {'projectName': name, 'plcPath': plc}, 'result': copy.deepcopy(native)}]
        def call(tool, arguments):
            result = hardware_inspection(inventory, arguments['hardwarePath'], self.profile)
            calls.append({'tool': tool, 'status': 'passed', 'arguments': arguments, 'result': copy.deepcopy(result)})
            return result
        capture_endpoint(call, native, inventory, self.profile)
        return native, calls

    def test_copies_can_have_new_context_and_provider_ids_after_independent_admission(self):
        first, calls = self.capture()
        second, other_calls = self.capture('recovered.ap21', 83, 'Recovered')
        self.assertNotEqual(first['hardwareConnections'], second['hardwareConnections'])
        self.assertTrue(same(require_endpoint_calls(first, self.profile, calls), require_endpoint_calls(second, self.profile, other_calls)))

    def test_each_endpoint_requires_exact_local_membership(self):
        for defect in ('foreignProject', 'foreignPlc', 'foreignPath', 'duplicatePath', 'missingParent', 'moduleType',
                       'ownerSlot', 'wrongAncestry', 'alias', 'missingBasis', 'membershipError', 'membershipCount', 'membershipBool'):
            native, _ = self.capture()
            owner = native['hardwareConnections'][0]['attributes'][2]['value']['parent']
            inventory = native['endpointEvidence']['inventory']
            coverage = native['hardwareConnectionCoverage']['referenceCoverage']
            if defect == 'foreignProject': owner['projectPath'] = 'foreign.ap21'
            elif defect == 'foreignPlc': owner['selectionPlcPath'] += '/wrong'
            elif defect == 'foreignPath': owner['hardwarePath'] = '/projects/Foreign/items/TiaMcp_EncoderHardware'
            elif defect == 'duplicatePath': inventory['hardware'].append(copy.deepcopy(inventory['hardware'][-1]))
            elif defect == 'missingParent': inventory['hardware'].pop()
            elif defect == 'moduleType': inventory['hardware'][1]['typeIdentifier'] = 'wrong'
            elif defect == 'ownerSlot': owner['positionNumber'] = True
            elif defect == 'wrongAncestry':
                owner['hardwarePath'] = inventory['hardware'][-1]['path'] = '/projects/Fixture/devices/Other/items/TiaMcp_EncoderHardware'
            elif defect == 'alias': owner['nativeIdentifier'] = owner['container']['nativeIdentifier']
            elif defect == 'missingBasis': owner.pop('hardwarePathBasis')
            elif defect == 'membershipError': coverage['hardwareMembershipStatus'] = 'readError'
            elif defect == 'membershipCount': coverage['hardwareItemsVisited'] += 1
            else: coverage['hardwareItemsVisited'] = True
            with self.subTest(defect=defect), self.assertRaises((AssertionError, RuntimeError)): endpoint(native, self.profile)

    def test_hardware_inspection_requires_exact_owner_channel_and_complete_values(self):
        for defect in ('project', 'process', 'path', 'name', 'slot', 'duplicateItem', 'duplicateChannel', 'missingChannel',
                       'wrongNumber', 'boolNumber', 'wrongType', 'wrongIo', 'truncated', 'warnings', 'mode', 'modeBool', 'unreadable', 'duplicateAttribute'):
            native, _ = self.capture()
            read = native['endpointEvidence']['hardware'][1]
            item = read['items'][0]; channel = item['channels'][0]; attribute = channel['attributes'][0]
            if defect == 'project': read['project']['path'] = 'foreign.ap21'
            elif defect == 'process': read['processId'] += 1
            elif defect == 'path': item['path'] += '/wrong'
            elif defect == 'name': item['name'] = 'other'
            elif defect == 'slot': item['positionNumber'] = True
            elif defect == 'duplicateItem': read['items'].append(copy.deepcopy(item))
            elif defect == 'duplicateChannel': item['channels'].append(copy.deepcopy(channel))
            elif defect == 'missingChannel': item['channels'] = []
            elif defect == 'wrongNumber': channel['number'] += 1
            elif defect == 'boolNumber': channel['number'] = False
            elif defect == 'wrongType': channel['type'] = 'Input'
            elif defect == 'wrongIo': channel['ioType'] = 'Input'
            elif defect == 'truncated': read['completeness']['truncated'] = True
            elif defect == 'warnings': read['warnings'] = ['incomplete']
            elif defect == 'mode': attribute['value'] = 0
            elif defect == 'modeBool': attribute['value'] = True
            elif defect == 'unreadable': attribute['valueComplete'] = False
            else: channel['attributes'].append(copy.deepcopy(attribute))
            with self.subTest(defect=defect), self.assertRaises((AssertionError, RuntimeError)): endpoint(native, self.profile)

    def test_addresses_and_extra_channel_configuration_remain_part_of_correspondence(self):
        first, _ = self.capture(); second, _ = self.capture('recovered.ap21', 83)
        second['hardwareConnections'][0]['attributes'][0]['value'] = 4
        self.assertFalse(same(endpoint(first, self.profile), endpoint(second, self.profile)))
        second['hardwareConnections'][0]['attributes'][0]['value'] = False
        with self.assertRaises((AssertionError, RuntimeError)): endpoint(second, self.profile)
        second, _ = self.capture('recovered.ap21', 83)
        attributes = second['endpointEvidence']['hardware'][1]['items'][0]['channels'][0]['attributes']
        attributes.append({**attributes[0], 'name': 'OtherSetting', 'value': 1})
        self.assertFalse(same(endpoint(first, self.profile), endpoint(second, self.profile)))

    def test_original_calls_cannot_be_replaced_reordered_or_crossed_by_mutation(self):
        for defect in ('missingInventory', 'filteredInventory', 'inventoryResult', 'selection', 'missingRead', 'duplicateRead',
                       'missingHardware', 'hardwareSelection', 'hardwareResult', 'reorderedHardware', 'interveningMutation'):
            native, calls = self.capture()
            if defect == 'missingInventory': calls.pop(0)
            elif defect == 'filteredInventory': calls[0]['arguments']['name'] = 'filtered'
            elif defect == 'inventoryResult': calls[0]['result']['hardware'][-1]['positionNumber'] += 1
            elif defect == 'selection': calls[1]['arguments']['plcPath'] += '/wrong'
            elif defect == 'missingRead': calls.pop(1)
            elif defect == 'duplicateRead': calls.append(copy.deepcopy(calls[1]))
            elif defect == 'missingHardware': calls.pop()
            elif defect == 'hardwareSelection': calls[-1]['arguments']['hardwarePath'] += '/wrong'
            elif defect == 'hardwareResult': calls[-1]['result']['items'][0]['name'] = 'wrong'
            elif defect == 'reorderedHardware': calls[-1], calls[-2] = calls[-2], calls[-1]
            else: calls.insert(2, {'tool': 'change_apply', 'status': 'passed'})
            with self.subTest(defect=defect), self.assertRaises((AssertionError, RuntimeError)): require_endpoint_calls(native, self.profile, calls)


if __name__ == '__main__':
    unittest.main()
