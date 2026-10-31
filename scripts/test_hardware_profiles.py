"""Offline checks of hardware_profiles.py and the bundled module capability profile contract; nothing here starts TIA."""
import json
import unittest

import jsonschema

import hardware_profiles as hp
import qualify_hardware_changes as q

BASE = "/projects/P/devices/"
DI = BASE + "St/items/Di"


def raw(path, name, identifier=None, slot=1, kind="None", **extra):
    return {"path": path, "name": name, "typeIdentifier": identifier, "positionNumber": slot, "classification": kind, "isBuiltIn": False,
            "attributes": [{"name": "Comment", "accessMode": "ReadWrite", "type": "System.String", "value": "secret"}], "addresses": [], "channels": [], **extra}


class ProfileTests(unittest.TestCase):
    def items(self):
        return {DI: raw(DI, "Di", "OrderNumber:X/V1.0", 2, addresses=[{"ioType": "Input", "startAddress": 0, "length": 16}],
                        channels=[{"number": 0, "type": "Digital", "ioType": "Input", "attributes": [{"name": "InputDelay", "accessMode": "Read", "type": "System.UInt32"}]}]),
                DI + "/items/Sub": raw(DI + "/items/Sub", "Sub", network={"interfaceType": "Ethernet", "nodes": [{"name": "X1"}],
                                                                           "ports": [{"port": {"ownerHardwarePath": DI + "/items/Sub/items/P1"}}]}),
                BASE + "St/items/Di2": raw(BASE + "St/items/Di2", "Di2", "OrderNumber:X/V1.0", 3)}

    def test_items_keep_access_and_shape_never_values(self):
        rows = hp.items(self.items(), DI)
        self.assertEqual(["", "items/Sub"], [row["relativePath"] for row in rows])
        self.assertEqual([{"name": "Comment", "accessMode": "ReadWrite", "type": "System.String"}], rows[0]["attributes"])
        self.assertEqual([{"ioType": "Input", "lengthBits": 16}], rows[0]["addresses"])
        self.assertEqual("Read", rows[0]["channels"][0]["attributes"][0]["accessMode"])
        self.assertEqual({"interfaceType": "Ethernet", "nodes": ["X1"], "ports": ["items/Sub/items/P1"]}, rows[1]["interface"])
        self.assertNotIn("secret", json.dumps(rows))
        with self.assertRaises(ValueError):
            hp.items(self.items(), BASE + "St/items/Missing")

    def test_contract_validates_and_refuses_duplicates(self):
        profile = hp.profile("OrderNumber:X/V1.0", "module", hp.items(self.items(), DI), {"articleNumber": "X", "version": "V1.0", "typeName": "DI", "catalogPath": "/c"},
                             [{"rack": "OrderNumber:R", "positionNumber": 2, "canPlugNew": False, "occupiedBy": "OrderNumber:X/V1.0"}], "hw_x/qualification.json")
        self.assertEqual("X", hp.contract([profile])["profiles"][0]["orderNumber"])
        with self.assertRaises(ValueError):
            hp.contract([profile, profile])
        with self.assertRaises(jsonschema.ValidationError):
            hp.contract([{**profile, "moduleKind": "rack"}])

    def test_bundled_contract_matches_its_schema(self):
        document = json.loads((hp.ROOT / "contracts" / "hardware-catalog-profiles.v1.json").read_text(encoding="utf-8"))
        self.assertEqual(document, hp.contract(document["profiles"]))

    def test_module_segment_is_canonical(self):
        raw_items = {DI: raw(DI, "Di", "OrderNumber:X/V1.0"), DI + "/items/Di": raw(DI + "/items/Di", "Di"), DI + "/items/Di/items/Di_P": raw(DI + "/items/Di/items/Di_P", "Di_P")}
        self.assertEqual(["", "items/{module}", "items/{module}/items/Di_P"], [row["relativePath"] for row in hp.items(raw_items, DI)])

    def test_merge_unions_rows_and_prefers_writable(self):
        first = hp.items(self.items(), DI)
        second = [dict(row, attributes=[{"name": "Comment", "accessMode": "Read", "type": None}, {"name": "Flag", "accessMode": "ReadWrite", "type": "System.Boolean"}])
                  if row["relativePath"] == "" else row for row in first] + [{**first[1], "relativePath": "items/New"}]
        merged = hp.merge(first, second)
        self.assertEqual(["", "items/New", "items/Sub"], [row["relativePath"] for row in merged])
        self.assertEqual({"Comment": "ReadWrite", "Flag": "ReadWrite"}, {a["name"]: a["accessMode"] for a in merged[0]["attributes"]})

    def test_versionless_profiles_are_keyed_by_firmware(self):
        rows = hp.items(self.items(), DI)
        gsd = lambda version: hp.profile("GSD:F/D", "device", rows, {"version": version}, [], "r")
        self.assertEqual(2, len(hp.contract([gsd("V1"), gsd("V2")])["profiles"]))
        with self.assertRaises(ValueError):
            hp.contract([gsd(None)])
        with self.assertRaises(ValueError):
            hp.contract([gsd("V1"), gsd("V1")])

    def test_runner_profiles_modules_and_heads_not_racks(self):
        run = q.Run.__new__(q.Run)
        run.base, run.profiled = BASE, {}
        run.raw = {**self.items(), BASE + "St/items/Rail_0": raw(BASE + "St/items/Rail_0", "Rail_0", "OrderNumber:R", 0),
                   BASE + "St/items/Cpu": raw(BASE + "St/items/Cpu", "Cpu", q.CPU, 1, "CPU")}
        self.assertEqual({DI, BASE + "St/items/Di2", BASE + "St/items/Cpu"}, set(run.modules()))
        run.collect()
        self.assertEqual({"OrderNumber:X/V1.0": "module", q.CPU: "device"}, {key: row["kind"] for key, row in run.profiled.items()})
        # A later snapshot's attributes join the type's profile.
        run.raw[DI] = {**run.raw[DI], "attributes": [{"name": "Later", "accessMode": "ReadWrite", "type": "System.String"}]}
        run.collect()
        self.assertEqual(["Comment", "Later"], [a["name"] for a in run.profiled["OrderNumber:X/V1.0"]["items"][0]["attributes"]])


if __name__ == "__main__":
    unittest.main()
