"""Offline checks of hardware_paths.py: TIA-generated item paths resolved by structure in any UI language; nothing here starts TIA."""
import unittest

import hardware_paths as hp

CPU = "/projects/P/devices/St/items/Plc"


def interface(name, ports, nodes=("X1",)):
    """A hardware_inspect row of one interface; ports are listed last first, so the order must come from portIndex."""
    listed = [{"portIndex": index, "port": {"resolution": "resolved", "ownerHardwarePath": port}, "connectedPorts": []} for index, port in enumerate(ports)]
    return {"path": CPU + "/items/" + name, "network": {"nodes": [{"name": n} for n in nodes], "ports": listed[::-1]}}


def german():
    """A German installation's CPU 1511-1 PN: display, interface with two ports, and the station's rail beside the CPU."""
    iface = CPU + "/items/PROFINET-Schnittstelle_1"
    return {"completeness": {"truncated": False}, "items": [
        {"path": CPU, "positionNumber": 1, "classification": "CPU", "network": {"nodes": []}},
        {"path": "/projects/P/devices/St/items/Profilschiene_0", "positionNumber": 0, "classification": "None"},
        {"path": CPU + "/items/CPU-Display_1", "attributes": [{"name": "DisplayProtection"}, {"name": "DisplayDefaultLanguage"}]},
        {"path": CPU + "/items/OPC%20UA_1", "attributes": [{"name": "Name"}]},
        interface("PROFINET-Schnittstelle_1", [iface + "/items/Port_1", iface + "/items/Port_2"]),
        {"path": iface + "/items/Port_1", "network": None}, {"path": iface + "/items/Port_2", "network": None}]}


class HardwarePathTests(unittest.TestCase):
    def test_interface_ports_and_node_come_from_structure(self):
        response = german()
        iface = CPU + "/items/PROFINET-Schnittstelle_1"
        self.assertEqual(iface, hp.network_interface(response, CPU))
        self.assertEqual("X1", hp.node_name(response, iface))
        self.assertEqual([iface + "/items/Port_1", iface + "/items/Port_2"], hp.ports(response, iface))
        self.assertEqual(iface + "/items/Port_2", hp.port(response, iface, 2))
        with self.assertRaises(AssertionError):
            hp.port(response, iface, 3)

    def test_every_source_shape_gives_the_same_rows(self):
        response = german()
        rows = hp.rows(response)
        state = {"hw:" + path: row for path, row in rows.items()}
        state["inv:" + CPU] = {}  # Other compared keys are not hardware rows.
        for source in (response, response["items"], rows, state):
            self.assertEqual(rows, hp.rows(source))
        with self.assertRaises(AssertionError):
            hp.rows({**response, "completeness": {"truncated": True}})

    def test_ambiguity_and_absence_fail(self):
        response = german()
        response["items"].append(interface("PROFINET-Schnittstelle_2", [CPU + "/items/PROFINET-Schnittstelle_2/items/Port_1"]))
        with self.assertRaises(AssertionError):
            hp.network_interface(response, CPU)
        self.assertEqual(CPU + "/items/PROFINET-Schnittstelle_2", hp.network_interface(response, CPU, index=1))
        with self.assertRaises(AssertionError):
            hp.network_interface(response, CPU + "/items/CPU-Display_1")
        unresolved = interface("X", [None])
        with self.assertRaises(AssertionError):
            hp.ports([unresolved], unresolved["path"])
        portless = interface("Z", [])
        self.assertEqual([], hp.ports([portless], portless["path"]))
        with self.assertRaises(AssertionError):
            hp.port([portless], portless["path"], 1)
        with self.assertRaises(AssertionError):
            hp.node_name([interface("Y", [], nodes=("X1", "X2"))], CPU + "/items/Y")

    def test_only_direct_children_count(self):
        # A port's own network row (or any deeper item) below the interface is not a second interface of the CPU.
        response = german()
        response["items"].append({"path": CPU + "/items/PROFINET-Schnittstelle_1/items/Port_1/items/Deep", "network": {"nodes": [{"name": "Z"}]}})
        self.assertEqual(CPU + "/items/PROFINET-Schnittstelle_1", hp.network_interface(response, CPU))

    def test_rack_and_attribute_lookups(self):
        response = german()
        self.assertEqual("/projects/P/devices/St/items/Profilschiene_0", hp.root_rack(response, "/projects/P/devices/St"))
        with self.assertRaises(AssertionError):
            hp.root_rack(response, "/projects/P/devices/St", exclude=("/projects/P/devices/St/items/Profilschiene_0",))
        self.assertEqual(CPU + "/items/CPU-Display_1", hp.child_with_attribute(response, CPU, "DisplayProtection"))
        # A compared state keeps attributes as a name-to-value dict.
        self.assertEqual(CPU + "/items/D", hp.child_with_attribute({"hw:" + CPU + "/items/D": {"attributes": {"DisplayProtection": False}}}, CPU, "DisplayProtection"))
        with self.assertRaises(AssertionError):
            hp.child_with_attribute(response, CPU, "Missing")


if __name__ == "__main__":
    unittest.main()
