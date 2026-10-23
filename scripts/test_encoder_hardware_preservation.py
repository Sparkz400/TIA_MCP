"""Populated hardware/network fixtures exercise production encoder controllers."""
import copy
import json
import unittest

import encoder_hardware_preservation as hardware
import encoder_software_preservation as software
import test_encoder_lifecycle as lifecycle
import audit_encoder_preservation as auditor
import qualify_encoder_hardware as qualifier

PEER = lifecycle.RAIL + '/items/UnrelatedIo'


class HardwarePeerTests(unittest.TestCase):
    def harness(self):
        case = lifecycle.EncoderLifecycleTests()
        case.setUp(); self.addCleanup(case.doCleanups)
        return case

    def test_populated_hardware_network_and_fresh_copies(self):
        case = self.harness()
        def observed(tool, args, value):
            if tool == 'hardware_inspect': value['observedAtUtc'] = '2026-10-17T00:00:00.' + str(len(case.calls)).zfill(6) + 'Z'
            return value
        case.response_transform = observed
        source = case.run_qualifier()
        before = source['nativeBefore']
        self.assertEqual(5, len(before['hardwarePeers']['reads']))
        cpu = next(read['items'][0] for read in before['hardwarePeers']['reads'] if read['items'][0]['path'] == lifecycle.CPU_PATH)
        self.assertEqual(16, cpu['addresses'][0]['length'])
        self.assertEqual(lifecycle.CPU_PATH, cpu['network']['ioControllers'][0]['ioSystem']['controllerOwner']['ownerHardwarePath'])
        result = {}; auditor.audit(source, result)
        self.assertTrue(result['configuredHardwarePeersVerified'])
        self.assertFalse(result['fullProjectPreservationVerified'])
        case.audit_companion(case.run_companion())

    def test_unrelated_attribute_channel_and_network_changes_stop_and_restore(self):
        for defect in ('attribute', 'kind', 'channel', 'node', 'controller', 'connector', 'service', 'builtIn'):
            with self.subTest(defect=defect):
                case = self.harness()
                def transform(tool, args, value):
                    if tool == 'hardware_inspect' and case.current['Sensor.InverseDirection']['value']:
                        row = value['items'][0]
                        if row['path'] == PEER:
                            if defect == 'attribute': row['attributes'][0]['value'] = 8
                            if defect == 'kind': row['attributes'][0]['value'] = 7.0
                            if defect == 'channel': row['channels'][0]['attributes'][0]['value'] = 4
                            if defect == 'builtIn': row['isBuiltIn'] = True
                        if row['path'] == lifecycle.CPU_PATH:
                            if defect == 'node': row['network']['nodes'][0]['attributes'][0]['value'] = '192.0.2.2'
                            if defect == 'controller': row['network']['ioControllers'][0]['ioSystem']['number'] = 2
                            if defect == 'service': row.pop('network'); row['networkObservation'] = 'notExposed'
                        if row['path'] == lifecycle.MODULE and defect == 'connector':
                            row['network']['ioConnectors'][0]['connectedToIoSystem']['number'] = 2
                    return value
                case.response_transform = transform
                with self.assertRaisesRegex(AssertionError, 'Configured hardware peers changed'): case.run_qualifier()
                self.assertTrue(any(tool == 'change_restore' for _, tool, _ in case.calls))

    def test_changed_address_inventory_and_read_refuse_across_phases(self):
        case = self.harness()
        def transform(tool, args, value):
            if case.current['Sensor.InverseDirection']['value']:
                if tool == 'project_inventory': items = value['hardware']
                elif tool == 'hardware_inspect': items = value['items']
                else: return value
                for row in items:
                    if row['path'] == lifecycle.CPU_PATH: row['addresses'][0]['startAddress'] = 513
            return value
        case.response_transform = transform
        with self.assertRaisesRegex(AssertionError, 'Configured hardware peers changed'): case.run_qualifier()

    def test_added_or_removed_unrelated_hardware_refuses(self):
        for defect in ('added', 'removed'):
            with self.subTest(defect=defect):
                case = self.harness()
                def transform(tool, args, value):
                    if tool == 'project_inventory' and case.current['Sensor.InverseDirection']['value']:
                        if defect == 'removed': value['hardware'] = [row for row in value['hardware'] if row['path'] != PEER]
                        else: value['hardware'].append({'path': lifecycle.RAIL + '/items/Added', 'name': 'Added', 'positionNumber': 4,
                                                      'classification': 'Hardware', 'typeIdentifier': None, 'addresses': []})
                    return value
                case.response_transform = transform
                with self.assertRaisesRegex(AssertionError, 'Configured hardware peers changed'): case.run_qualifier()

    def test_incomplete_or_foreign_hardware_refuses_before_parameter_edit(self):
        for defect in ('owner', 'warning', 'truncated', 'missing', 'opaque', 'excluded', 'duplicateChannel', 'networkUnknown', 'boolFlag'):
            with self.subTest(defect=defect):
                case = self.harness()
                def transform(tool, args, value):
                    if tool == 'hardware_inspect' and args['hardwarePath'] == PEER:
                        row = value['items'][0]
                        if defect == 'owner': value['processId'] += 100
                        elif defect == 'warning': value['warnings'] = ['Unavailable']
                        elif defect == 'truncated': value['completeness']['truncated'] = True
                        elif defect == 'missing': row.pop('attributes')
                        elif defect == 'opaque': row['attributes'][0]['valueComplete'] = False
                        elif defect == 'excluded': row['attributes'][0]['excluded'] = True
                        elif defect == 'duplicateChannel': row['channels'] *= 2
                        elif defect == 'networkUnknown': row.pop('networkObservation')
                        else: row['isPlugged'] = 1
                    return value
                case.response_transform = transform
                with self.assertRaises(AssertionError): case.run_qualifier()
                self.assertFalse(any(tool == 'change_prepare' and args.get('kind') == 'to_parameters' for _, tool, args in case.calls))

    def test_unresolved_and_foreign_network_references_refuse(self):
        for defect in ('subnet', 'controller', 'connector', 'index', 'missing', 'scope', 'unassigned'):
            with self.subTest(defect=defect):
                case = self.harness()
                def transform(tool, args, value):
                    if tool == 'hardware_inspect' and args['hardwarePath'] == lifecycle.CPU_PATH:
                        net = value['items'][0]['network']; controller = net['ioControllers'][0]
                        if defect == 'subnet': net['nodes'][0]['subnet']['path'] = '/projects/Foreign/subnets/PN'
                        elif defect == 'controller': controller['ioSystem']['controllerOwner']['ownerHardwarePath'] = lifecycle.MODULE
                        elif defect == 'connector': controller['assignedConnectors'][0]['resolution'] = 'unresolved'
                        elif defect == 'index': controller['controllerIndex'] = False
                        elif defect == 'missing': net.pop('ioConnectors')
                        elif defect == 'scope': net['scopeComplete'] = False
                        else: controller['ioSystem'] = {'state': 'notAssigned'}
                    return value
                case.response_transform = transform
                with self.assertRaises(AssertionError): case.run_qualifier()

    def test_drift_during_capture_refuses(self):
        case = self.harness(); read_count = 0
        def transform(tool, args, value):
            nonlocal read_count
            if tool == 'hardware_inspect': read_count += 1
            if tool == 'project_inventory' and read_count >= 7:
                next(row for row in value['hardware'] if row['path'] == PEER)['positionNumber'] = 9
            return value
        case.response_transform = transform
        with self.assertRaisesRegex(AssertionError, 'inventory changed during'): case.run_qualifier()

    def test_recovery_and_both_saved_reopen_drift_refuse(self):
        for phase in ('recovery', 'recoveredReopen', 'changedReopen'):
            with self.subTest(phase=phase):
                case = self.harness()
                def transform(tool, args, value):
                    active = (phase == 'recovery' and case.path == getattr(case, 'recovered', None) or
                              phase == 'recoveredReopen' and case.serial == 2 or phase == 'changedReopen' and case.serial == 3)
                    if active and tool == 'hardware_inspect' and args['hardwarePath'] == PEER:
                        value['items'][0]['attributes'][0]['value'] = 99
                    return value
                case.response_transform = transform
                with self.assertRaisesRegex(AssertionError, 'Configured hardware peers changed'): case.run_qualifier()

    def test_inspection_copy_drift_refuses_and_cleans_up(self):
        case = self.harness(); case.run_qualifier(); closes = case.close_count
        def transform(tool, args, value):
            if tool == 'hardware_inspect' and args['hardwarePath'] == PEER: value['items'][0]['attributes'][0]['value'] = 99
            return value
        case.response_transform = transform
        with self.assertRaisesRegex(AssertionError, 'Configured hardware peers changed'): case.run_companion()
        self.assertEqual(closes + 1, case.close_count)

    def test_original_requests_and_summaries_cannot_be_substituted(self):
        case = self.harness(); source = case.run_qualifier()
        for defect in ('filter', 'interrupted', 'summary', 'missing', 'duplicate', 'owner', 'selection'):
            with self.subTest(defect=defect):
                native = copy.deepcopy(source['nativeBefore']); calls = copy.deepcopy(source['sessions'][0]['calls'])
                start = software.require_calls(native, calls)
                if defect == 'filter': calls[start]['arguments']['scopePath'] = native['plcPath']
                elif defect == 'interrupted': calls.insert(start + 1, {'tool': 'change_apply', 'status': 'passed', 'arguments': {}, 'result': {}})
                elif defect == 'summary': native['hardwarePeers']['reads'][0]['items'][0]['attributes'][0]['value'] = 1
                elif defect == 'missing': native['hardwarePeers']['reads'].pop()
                elif defect == 'duplicate': native['hardwarePeers']['reads'][1] = copy.deepcopy(native['hardwarePeers']['reads'][0])
                elif defect == 'owner': calls[start + 1]['result']['processId'] += 100
                else: calls[start + 1]['result'], calls[start + 2]['result'] = calls[start + 2]['result'], calls[start + 1]['result']
                with self.assertRaises(AssertionError): hardware.require_calls(native, calls, start)

    def test_wrong_selected_response_stops_before_next_read(self):
        case = self.harness()
        def transform(tool, args, value):
            if tool == 'hardware_inspect' and args['hardwarePath'] == lifecycle.CPU_PATH:
                value['processId'] += 100
            return value
        case.response_transform = transform
        with self.assertRaises(AssertionError): case.run_qualifier()
        index = next(i for i, (_, tool, args) in enumerate(case.calls) if tool == 'hardware_inspect' and args['hardwarePath'] == lifecycle.CPU_PATH)
        self.assertFalse(any(tool in ('hardware_inspect', 'project_inventory', 'change_prepare', 'change_apply') for _, tool, _ in case.calls[index + 1:]))

    def test_hardware_proof_requires_software_proof_and_legacy_cannot_open(self):
        case = self.harness(); source = case.run_qualifier()
        damaged = copy.deepcopy(source); del damaged['softwarePeerPolicy']
        with self.assertRaises(AssertionError): qualifier.require_source_compiles(damaged)
        del source['hardwarePeerPolicy']
        result = {}; auditor.audit(source, result)
        self.assertFalse(result['configuredHardwarePeersVerified'])
        case.report_path.write_text(json.dumps(source), encoding='utf-8')
        serial = case.serial
        with self.assertRaisesRegex(AssertionError, 'Current hardware peer evidence'): case.run_companion()
        self.assertEqual(serial, case.serial)

    def test_native_inventory_type_and_address_shape_are_required(self):
        case = self.harness(); source = case.run_qualifier(); native = source['nativeBefore']
        for defect in ('typeIdentifier', 'addresses', 'classification', 'byteSpan', 'lengthUnit', 'orphan'):
            damaged = copy.deepcopy(native['hardwarePeers']['inventoryBefore'])
            row = next(row for row in damaged['hardware'] if row['path'] == lifecycle.CPU_PATH)
            if defect in ('typeIdentifier', 'addresses', 'classification'): row.pop(defect)
            elif defect == 'orphan': damaged['hardware'] = [row for row in damaged['hardware'] if row['path'] != lifecycle.RAIL]
            else: row['addresses'][0][defect] = 16 if defect == 'byteSpan' else 'bytes'
            with self.subTest(defect=defect), self.assertRaises(AssertionError): hardware.inventory_view(damaged, native)


if __name__ == '__main__':
    unittest.main()
