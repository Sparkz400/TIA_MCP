"""Evidence gates must reject false connected-encoder qualification."""
import base64
import copy
import hashlib
import unittest

from qualify_encoder_hardware import assert_parameter_edit, connection_attributes, decode_native_artifact, parameter_map


def connected_native(profile, project_path, process_id, plc_path, module_path, inventory):
    fixture = EncoderEvidenceTests(); fixture.setUp()
    native = copy.deepcopy(fixture.native)
    def context(value):
        if isinstance(value, dict):
            for key, item in value.items():
                if key == 'projectPath': value[key] = project_path
                elif key == 'nativeIdentifier' and item: value[key] = str(process_id) + ':' + project_path + ':' + item
                elif isinstance(item, str) and item.startswith('P/PLC'): value[key] = item.replace('P/PLC', plc_path, 1)
                else: context(item)
        elif isinstance(value, list):
            for item in value: context(item)
    context(native)
    native.update(processId=process_id, project=copy.deepcopy(inventory['project']))
    native['technologyObject']['path'] = plc_path + '/technology/Encoder'
    channel = native['hardwareConnections'][0]['attributes'][2]['value']
    channel.update(number=profile['channelNumber'], ioType=profile['channelIoType'])
    owner, module = channel['parent'], channel['parent']['container']
    owner.update(name=profile['channelItemName'], hardwarePath=module_path + '/items/' + profile['channelItemName'],
                 hardwarePathBasis='boundedProjectHardwareMembership')
    module.update(typeIdentifier=profile['moduleTypeIdentifier'], hardwarePath=module_path, hardwarePathBasis='boundedProjectHardwareMembership')
    native['hardwareConnectionCoverage']['referenceCoverage'].update(hardwareMembershipStatus='complete',
        hardwareItemsVisited=len(inventory['hardware']), hardwareItemsLimit=50000)
    return native


def hardware_inspection(inventory, path, profile):
    row = next(copy.deepcopy(row) for row in inventory['hardware'] if row['path'] == path)
    def attribute(name, value):
        return {'name': name, 'value': value, 'valueCaptured': True, 'valueComplete': True,
                'valueStatus': 'captured', 'readAccessKnown': True, 'readable': True}
    row.update(isBuiltIn=row['positionNumber'] == 0, isPlugged=True, networkObservation='notExposed',
               attributes=[attribute('SyntheticSetting', 7)])
    row.setdefault('addresses', [])
    for address in row['addresses']: address['attributes'] = [attribute('SyntheticAddressSetting', 9)]
    row['channels'] = []
    cpu = next((item for item in inventory['hardware'] if item['name'] == 'CPU'), None)
    module = next((item for item in inventory['hardware'] if item['positionNumber'] == 2), None)
    if cpu and module and row['path'] in (cpu['path'], module['path']):
        from urllib.parse import quote
        subnet = {'state': 'connected', 'name': 'PN', 'path': '/projects/' + quote(inventory['project']['name'], safe='') + '/subnets/PN',
                  'resolution': 'resolved', 'reason': None}
        system = {'state': 'assigned', 'name': 'System', 'number': 1,
                  'controllerOwner': {'resolution': 'resolved', 'ownerHardwarePath': cpu['path']}, 'subnet': subnet}
        row['networkObservation'] = 'observed'
        row['network'] = {'origin': 'nativeConfiguredOffline', 'physicalTopologyVerified': False, 'scopeComplete': True,
                          'interfaceType': 'Ethernet', 'operatingMode': 'IoController' if row['path'] == cpu['path'] else 'IoDevice',
                          'associationBasis': 'Synthetic native-object equality fixture',
                          'nodes': [{'name': 'Node', 'type': 'Ethernet', 'subnet': subnet, 'attributes': [attribute('Address', '192.0.2.1')]}],
                          'ioControllers': [], 'ioConnectors': []}
        if row['path'] == cpu['path']:
            row['network']['ioControllers'] = [{'ownerHardwarePath': row['path'], 'controllerIndex': 0, 'ioSystem': system,
                'assignedConnectors': [{'resolution': 'resolved', 'ownerHardwarePath': module['path']}], 'attributes': []}]
        else:
            row['network']['ioConnectors'] = [{'ownerHardwarePath': row['path'], 'connectorIndex': 0,
                                              'connectedToIoSystem': system, 'attributes': []}]
    if row['name'] == 'UnrelatedIo':
        row['channels'] = [{'number': 4, 'type': 'DigitalInput', 'ioType': 'Input',
                            'attributes': [attribute('SyntheticChannelSetting', 3)]}]
    if row['positionNumber'] == 1 and row['name'] == profile['channelItemName']:
        row['channels'] = [{'number': profile['channelNumber'], 'type': 'Technology', 'ioType': profile['channelIoType'],
                            'attributes': [{'name': 'OperatingMode', 'value': 269, 'valueCaptured': True, 'valueComplete': True,
                                'valueStatus': 'captured', 'readAccessKnown': True, 'readable': True}]}]
    return {'project': copy.deepcopy(inventory['project']), 'processId': inventory['processId'], 'items': [row],
            'warnings': [], 'completeness': {'scopeComplete': True, 'truncated': False, 'complete': False}}


class EncoderEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.profile = {"channelNumber": 0, "channelIoType": "Complex", "channelItemName": "TiaMcp_EncoderHardware",
                        "moduleTypeIdentifier": "OrderNumber:6ES7 551-1AB01-0AB0/V2.0"}
        self.channel = {"number": 0, "type": "Technology", "ioType": "Complex", "parent": {
            "name": "TiaMcp_EncoderHardware", "positionNumber": 1, "typeIdentifier": None,
            "container": {"name": "TiaMcp_EncoderHardware", "positionNumber": 2, "typeIdentifier": self.profile["moduleTypeIdentifier"]}}}
        self.native = {"hardwareConnections": [{"role": "encoder", "isConnected": True, "attributes": [
            {"name": "InputAddress", "value": 0}, {"name": "OutputAddress", "value": 0},
            {"name": "Channel", "value": self.channel}, {"name": "PathToDBMember", "value": None}]}]}
        path = "P/PLC/technology/Encoder"
        self.native["technologyObject"] = {"path": path}
        self.native["hardwareConnections"][0].update(service="encoder", observationPath=path + "/hardwareConnections/encoder",
            status="observed", scopeComplete=True, connectOption="Hardware", fieldStatus={"isConnected": "captured", "connectOption": "captured", "attributes": "captured"})
        self.native["hardwareConnectionCoverage"] = {"schemaVersion": 1, "technologyPath": path, "complete": False,
            "scopeComplete": True, "truncated": False, "services": [
                {"service": "axis", "status": "notExposed", "scopeComplete": True, "rowsCaptured": 0},
                {"service": "encoder", "status": "observed", "scopeComplete": True, "rowsCaptured": 1},
                {"service": "measuringInput", "status": "notExposed", "scopeComplete": True, "rowsCaptured": 0},
                {"service": "outputCam", "status": "notExposed", "scopeComplete": True, "rowsCaptured": 0}]}
        self.native.update(project={"path": "P.ap21"}, plcPath="P/PLC")
        self.native["hardwareConnectionCoverage"]["referenceCoverage"] = {
            "providerStatus": "available", "projectPath": "P.ap21", "selectionPlcPath": "P/PLC", "visitedReads": 40,
            "maximumReads": 50000, "truncated": False, "identifierCollisionDetected": False,
            "persistenceVerified": False, "atomicSnapshot": False}
        for item, kind, identifier in ((self.channel, "channel", None), (self.channel["parent"], "hardware", "owner-id"),
                                       (self.channel["parent"]["container"], "hardware", "module-id")):
            item.update(valueSemantics="engineeringReference", valueStatus="capturedReference", valueComplete=True,
                        identityComplete=True, resolution="resolved", kind=kind, engineeringType="Synthetic." + kind,
                        nativeIdentifier=identifier, projectPath="P.ap21", selectionPlcPath="P/PLC", persistenceVerified=False,
                        identityBasis="ownerProviderRoundTripAndUniqueNativeChannel" if kind == "channel" else "projectProviderRoundTrip")
        for row in self.native["hardwareConnections"][0]["attributes"]:
            row.update(readAccessKnown=True, readable=True, valueCaptured=True, valueComplete=True, valueStatus="captured")
        self.before = {"Sensor.InverseDirection": {"name": "Sensor.InverseDirection", "type": "System.Boolean", "value": False, "writable": True},
                       "Sensor.Type": {"name": "Sensor.Type", "type": "System.Int32", "value": 0, "writable": True}}
        for row in self.before.values():
            row.update(readAccessKnown=True, readable=True, valueCaptured=True, valueComplete=True, valueStatus="captured")
        self.after = copy.deepcopy(self.before)
        self.after["Sensor.InverseDirection"]["value"] = True

    def test_exact_connected_native_channel_is_accepted(self):
        self.assertEqual(connection_attributes(self.native, self.profile)["Channel"], self.channel)

    def test_reference_identity_claims_cannot_replace_provider_evidence(self):
        for target in ("channel", "owner", "container"):
            fields = [("valueSemantics", None), ("valueStatus", "displayOnly"), ("identityComplete", None),
                      ("resolution", "unresolved"), ("projectPath", "foreign.ap21"), ("selectionPlcPath", "P/Other"),
                      ("persistenceVerified", True), ("identityBasis", "nameMatch"), ("engineeringType", None)]
            if target != "channel":
                fields += [("nativeIdentifier", value) for value in (None, "", " ", "x" * 4097, "bad\uD800")]
            else:
                fields += [("nativeIdentifier", "guessed"), ("number", True), ("type", None), ("ioType", "")]
            for field, value in fields:
                native = copy.deepcopy(self.native)
                item = native["hardwareConnections"][0]["attributes"][2]["value"]
                if target != "channel": item = item["parent"]
                if target == "container": item = item["container"]
                item[field] = value
                with self.subTest(target=target, field=field, value=value), self.assertRaises(AssertionError):
                    connection_attributes(native, self.profile)

    def test_missing_or_contradictory_reference_coverage_is_rejected(self):
        for field, value in (("providerStatus", "notExposed"), ("providerStatus", "notRequested"),
                             ("projectPath", "wrong.ap21"), ("selectionPlcPath", "P/Other"), ("truncated", True),
                             ("identifierCollisionDetected", True), ("visitedReads", True), ("visitedReads", -1),
                             ("visitedReads", 50001), ("maximumReads", 50001), ("persistenceVerified", True),
                             ("atomicSnapshot", True)):
            native = copy.deepcopy(self.native)
            native["hardwareConnectionCoverage"]["referenceCoverage"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(AssertionError):
                connection_attributes(native, self.profile)
        del self.native["hardwareConnectionCoverage"]["referenceCoverage"]
        with self.assertRaises(AssertionError): connection_attributes(self.native, self.profile)

    def test_unmarked_old_display_channel_and_owner_cannot_qualify(self):
        for target in ("channel", "owner", "container"):
            native = copy.deepcopy(self.native)
            item = native["hardwareConnections"][0]["attributes"][2]["value"]
            if target != "channel": item = item["parent"]
            if target == "container": item = item["container"]
            for key in list(item):
                if key not in ("number", "type", "ioType", "parent", "name", "positionNumber", "typeIdentifier", "container"):
                    del item[key]
            with self.subTest(target=target), self.assertRaises(AssertionError):
                connection_attributes(native, self.profile)

    def test_incomplete_connection_rows_and_projections_are_rejected(self):
        for defect in ("missing", "duplicate", "displayOnly", "identity", "truncated", "expanded"):
            native = copy.deepcopy(self.native)
            rows = native["hardwareConnections"][0]["attributes"]
            if defect == "missing": rows[0].pop("valueComplete")
            elif defect == "duplicate": rows.append(copy.deepcopy(rows[0]))
            elif defect == "displayOnly": rows[2]["valueComplete"] = False
            elif defect == "identity": rows[2]["value"]["identityComplete"] = False
            elif defect == "truncated": rows[2]["value"]["parent"]["truncated"] = True
            else: rows[2]["value"]["expanded"] = False
            with self.subTest(defect=defect), self.assertRaises(AssertionError):
                connection_attributes(native, self.profile)

    def test_disconnected_missing_and_wrong_channel_are_rejected(self):
        for changed in (None, {"engineeringType": "Channel", "expanded": False},
                        {**self.channel, "number": 1}, {**self.channel, "ioType": "Input"}):
            with self.subTest(changed=changed):
                native = copy.deepcopy(self.native)
                native["hardwareConnections"][0]["attributes"][2]["value"] = changed
                with self.assertRaises((AssertionError, KeyError, TypeError)):
                    connection_attributes(native, self.profile)
        self.native["hardwareConnections"][0]["isConnected"] = False
        with self.assertRaises(AssertionError):
            connection_attributes(self.native, self.profile)

    def test_wrong_module_dependency_is_rejected(self):
        self.channel["parent"]["container"]["typeIdentifier"] = "OrderNumber:wrong-module"
        with self.assertRaises(AssertionError):
            connection_attributes(self.native, self.profile)

    def test_exact_requested_edit_is_accepted(self):
        assert_parameter_edit(self.before, self.after, {"Sensor.InverseDirection": True})

    def test_incomplete_or_duplicate_parameter_maps_cannot_qualify(self):
        valid = {"scopeComplete": True, "truncated": False, "technologyObjects": [{"typeIdentifier": "TO_ExternalEncoder",
                 "version": "10.0", "parametersComplete": True, "parametersTruncated": False, "parameters": list(self.before.values())}]}
        self.assertEqual(self.before, parameter_map(valid))
        for defect in ("duplicate", "scope", "parameters", "missing", "excluded", "readError"):
            changed = copy.deepcopy(valid)
            obj = changed["technologyObjects"][0]
            if defect == "duplicate":
                obj["parameters"].append(copy.deepcopy(obj["parameters"][0]))
            elif defect == "scope":
                changed["scopeComplete"] = False
            elif defect == "parameters":
                obj["parametersComplete"] = False
            elif defect == "missing":
                obj["parameters"][0].pop("valueComplete")
            else:
                obj["parameters"][0]["valueStatus"] = defect
            with self.subTest(defect=defect), self.assertRaises(AssertionError):
                parameter_map(changed)

    def test_incomplete_matching_edit_rows_are_rejected(self):
        for field, value in (("valueComplete", False), ("valueCaptured", False), ("readable", False), ("readAccessKnown", False),
                             ("excluded", True), ("valueStatus", "displayOnly")):
            before, after = copy.deepcopy(self.before), copy.deepcopy(self.after)
            before["Sensor.Type"][field] = after["Sensor.Type"][field] = value
            with self.subTest(field=field), self.assertRaises(AssertionError):
                assert_parameter_edit(before, after, {"Sensor.InverseDirection": True})

    def test_unrelated_parameter_value_or_metadata_change_is_rejected(self):
        for field, value in (("value", 1), ("value", 0.0), ("writable", False), ("type", "System.UInt32")):
            with self.subTest(field=field, value=value):
                changed = copy.deepcopy(self.after)
                changed["Sensor.Type"][field] = value
                with self.assertRaises(AssertionError):
                    assert_parameter_edit(self.before, changed, {"Sensor.InverseDirection": True})

    def test_missing_parameter_and_nonactual_edit_are_rejected(self):
        changed = copy.deepcopy(self.after)
        del changed["Sensor.Type"]
        with self.assertRaises(AssertionError):
            assert_parameter_edit(self.before, changed, {"Sensor.InverseDirection": True})
        with self.assertRaises(AssertionError):
            assert_parameter_edit(self.before, self.before, {"Sensor.InverseDirection": False})

    def test_native_export_digest_is_checked_at_both_layers(self):
        content = b'<Document><Value>true</Value></Document>'
        digest = hashlib.sha256(content).hexdigest()
        exported = {"artifact": {"sha256": digest}}
        artifact = {"complete": True, "sha256": digest, "data": base64.b64encode(content).decode()}
        self.assertEqual(decode_native_artifact(exported, artifact), content.decode())
        for override in ({"data": base64.b64encode(b'changed').decode()}, {"sha256": "0" * 64}, {"complete": False}):
            with self.subTest(override=override), self.assertRaises(AssertionError):
                decode_native_artifact(exported, {**artifact, **override})


if __name__ == "__main__":
    unittest.main()
