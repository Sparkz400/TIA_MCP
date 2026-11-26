"""Offline checks of plc_tag_tables.py and the runners that resolve the default tag table with it; nothing here starts TIA."""
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest import mock

import plc_tag_tables as tt
import qualify_cancellation
import qualify_composite_rebind
import qualify_confirmed_intent
import qualify_progress_reporting
import qualify_technology_changes

PLC = "/projects/P/devices/St/items/Plc/plc/Plc"
# As a German installation names the default table (observed natively); the English name is "Default tag table".
GERMAN, ENGLISH = "Standard-Variablentabelle", "Default tag table"


def tables(default=GERMAN):
    """software_inspect tag table rows: the default table with system and user constants, a user table, and a software unit's table."""
    def row(path, name, scope, constants):
        return {"path": path, "name": name, "scopePath": scope, "constantsTruncated": False, "tags": [], "constants": constants}
    encoded = default.replace(" ", "%20")
    return [row(PLC + "/tags/UserTags", "UserTags", PLC, [{"name": "Limit", "kind": "user"}]),
            row(PLC + "/tags/" + encoded, default, PLC, [{"name": "Local~Common", "kind": "system"}, {"name": "Max", "kind": "user"}]),
            row(PLC + "/units/U/tags/UnitTags", "UnitTags", PLC + "/units/U", [])]


def state(default=GERMAN):
    """The compared state qualify_plc_object_mutations.view makes of those rows."""
    view = {}
    for row in tables(default):
        view["tagTable:" + row["path"]] = {k: v for k, v in row.items() if k not in ("tags", "constants")}
        for constant in row["constants"]:
            view["member:" + row["path"] + ("/systemConstants/" if constant["kind"] == "system" else "/constants/") + constant["name"]] = constant
    return view


class Stop(Exception):
    pass


class FakeClient:
    """smoke_mcp.Client answering a German scratch until the first tag_create is prepared, which it records and stops at."""
    prepared = []

    def __init__(self, *args, **kwargs):
        self.errors, self.tool_calls, self.sequence = [], [], 0

    def request(self, method, params=None):
        return {}

    def notify(self, method, params=None):
        pass

    def tool(self, name, arguments=None):
        if name == "tia_start":
            return {"ownsPortalProcess": True}
        if name == "project_inventory":
            return {"plcs": [{"path": PLC}]}
        if name == "software_inspect":
            assert arguments == {"plcPath": PLC, "include": "constants", "maxItems": 2000}, arguments
            return {"completeness": {"truncated": False}, "tagTables": tables()}
        if name == "change_prepare":
            FakeClient.prepared.append(arguments["payload"])
            raise Stop()
        return {}

    def close(self):
        return {}


class TagTableTests(unittest.TestCase):
    def test_default_table_is_the_plc_scopes_table_with_system_constants(self):
        for default in (ENGLISH, GERMAN):
            self.assertEqual(default, tt.default_table(tables(default), PLC)["name"])
            self.assertEqual(default, tt.default_table({"completeness": {"truncated": False}, "tagTables": tables(default)}, PLC)["name"])
            self.assertEqual(PLC + "/tags/" + default.replace(" ", "%20"), tt.default_table_path(state(default), PLC))
        calls = []
        self.assertEqual(GERMAN, tt.default_table(lambda name, arguments: calls.append((name, arguments)) or {"tagTables": tables()}, PLC)["name"])
        self.assertEqual([("software_inspect", {"plcPath": PLC, "include": "constants", "maxItems": 2000})], calls)

    def test_ambiguity_absence_and_truncation_fail(self):
        rows = tables()
        rows[0]["constants"].append({"name": "Local~Other", "kind": "system"})
        with self.assertRaises(AssertionError):
            tt.default_table(rows, PLC)
        with self.assertRaises(AssertionError):
            tt.default_table([row for row in tables() if row["name"] != GERMAN], PLC)
        with self.assertRaises(AssertionError):
            tt.default_table({"completeness": {"truncated": True}, "tagTables": tables()}, PLC)
        rows = tables()
        rows[0]["constantsTruncated"] = True
        with self.assertRaises(AssertionError):
            tt.default_table(rows, PLC)
        view = state()
        view["member:" + PLC + "/tags/UserTags/systemConstants/Local~X"] = {}
        with self.assertRaises(AssertionError):
            tt.default_table_path(view, PLC)
        with self.assertRaises(AssertionError):  # Another PLC's default table is not this PLC's.
            tt.default_table_path(state(), PLC + "2")

    def test_client_runners_create_their_tags_in_the_german_default_table(self):
        for module in (qualify_cancellation, qualify_confirmed_intent, qualify_progress_reporting):
            FakeClient.prepared = []
            with tempfile.TemporaryDirectory() as folder, mock.patch.object(module, "Client", FakeClient), \
                    mock.patch.object(sys, "argv", ["runner", "--artifacts", str(Path(folder) / "run")]):
                with self.assertRaises(Stop):
                    module.main()
            self.assertEqual(GERMAN, FakeClient.prepared[0]["tagTableName"], module.__name__)

    def test_state_runners_use_the_german_default_table(self):
        run = types.SimpleNamespace(plc=PLC, current={"view": state()}, applied=[])
        run.apply = lambda label, kind, payload, preview=None, **expected: run.applied.append((payload, expected))
        qualify_technology_changes.tag(run, "T", "%M1.0")
        payload, expected = run.applied[0]
        table = PLC + "/tags/" + GERMAN
        self.assertEqual((table, ["member:" + table + "/tags/T"], ["tagTable:" + table]), (payload["tagTablePath"], expected["added"], expected["changed"]))
        item = "/projects/P/devices/St/items/Enc/items/Enc"
        renumbered = qualify_composite_rebind.identifier(run, item)
        self.assertTrue(renumbered("changed", "member:" + table + "/systemConstants/Local~Enc"))
        self.assertFalse(renumbered("changed", "member:" + PLC + "/tags/Default%20tag%20table/systemConstants/Local~Enc"))
        coupled = qualify_composite_rebind.coupled(run, item)
        self.assertTrue(coupled("changed", "tagTable:" + table))
        self.assertTrue(coupled("added", "member:" + table + "/tags/RbAxis_Sensor1_Interface_AddressIn"))


if __name__ == "__main__":
    unittest.main()
