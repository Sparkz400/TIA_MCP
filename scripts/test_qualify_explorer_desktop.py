"""Offline checks of the desktop roundtrip gates in qualify_explorer_desktop.py; nothing here starts TIA or a product."""
import copy
import unittest

import qualify_explorer_desktop as q

REF = {"schemaVersion": "project-model.v1", "snapshotId": "s1", "sessionGeneration": "g1", "projectPath": "D:/p/P.ap21",
       "nodeId": "n7", "objectPath": "/projects/P/devices/St/items/PLC/plc/PLC/blocks/TiaMcp_EnableGate"}


def call(name, request=None, **arguments):
    return {"name": name, "arguments": {"request": request} if request is not None else arguments}


def ok(value):
    return {"structuredContent": value}


def error(code):
    return {"isError": True, "structuredContent": {"error": {"code": code}}}


def transcript(*exchanges, ui=True):
    """(method, params, result) triples as one relayed session."""
    caps = {"extensions": {"io.modelcontextprotocol/ui": {"mimeTypes": [q.MIME]}}} if ui else {}
    exchanges = (("initialize", {"capabilities": caps, "clientInfo": {"name": "Visual Studio Code", "version": "1.140.0"}}, {}),) + exchanges
    records = []
    for number, (method, params, result) in enumerate(exchanges, 1):
        records.append({"session": 1, "dir": "c2s", "message": {"jsonrpc": "2.0", "id": number, "method": method, "params": params}})
        records.append({"session": 1, "dir": "s2c", "message": {"jsonrpc": "2.0", "id": number, "result": result}})
    return records


def roundtrip(reused=error("SessionMismatch"), details_ref=REF):
    return transcript(
        ("tools/list", {}, {"tools": [{"name": "project_model_capture", "_meta": {"ui": {"resourceUri": q.URI}}}]}),
        ("resources/read", {"uri": q.URI}, {"contents": [{"uri": q.URI, "mimeType": q.MIME, "text": "<html>"}]}),
        ("tools/call", call("project_model_capture", {"lifetimeSeconds": 900}), ok({"snapshotId": "s1"})),
        ("tools/call", call("project_model", {"action": "details", "reference": REF}), ok({"status": "available"})),
        ("tools/call", call("project_model", {"action": "verify", "reference": REF}), ok({"valid": True, "referenceVerified": True})),
        ("tools/call", call("project_model_details", {"reference": details_ref}), ok({"details": {"block": {"path": REF["objectPath"], "name": "TiaMcp_EnableGate"}}})),
        ("tools/call", call("tia_detach"), ok({"attached": False})),
        ("tools/call", call("project_model", {"action": "verify", "reference": REF}), reused))


class DesktopRoundtripTests(unittest.TestCase):
    def test_complete_roundtrip_passes_with_the_order_of_events(self):
        found = q.review(roundtrip())
        self.assertEqual(REF, found["reference"])
        self.assertEqual("SessionMismatch", found["staleCode"])
        self.assertEqual("Visual Studio Code", found["clientInfo"]["name"])
        order = found["order"]
        self.assertTrue(order["capture"] < order["sent"] < order["details"] < order["detach"] < order["reused"])

    def test_view_details_before_the_send_do_not_count_as_the_agents_call(self):
        records = roundtrip(details_ref={**REF, "nodeId": "n8"})
        with self.assertRaisesRegex(AssertionError, "identical reference"):
            q.review(records)

    def test_reference_accepted_after_detach_fails(self):
        with self.assertRaisesRegex(AssertionError, "accepted"):
            q.review(roundtrip(reused=ok({"valid": True})))
        with self.assertRaisesRegex(AssertionError, "InvalidRequest"):
            q.review(roundtrip(reused=error("InvalidRequest")))

    def test_unnegotiated_session_fails(self):
        records = roundtrip()
        records[0]["message"]["params"]["capabilities"] = {}
        with self.assertRaisesRegex(AssertionError, "negotiated"):
            q.review(records)

    def test_server_requests_and_other_sessions_are_ignored(self):
        records = copy.deepcopy(roundtrip())
        records.insert(4, {"session": 1, "dir": "s2c", "message": {"jsonrpc": "2.0", "id": 99, "method": "roots/list"}})
        records.insert(0, {"session": 0, "dir": "c2s", "message": {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}})
        self.assertEqual(1, q.review(records)["session"])

    def test_instructions_name_every_path(self):
        args = type("Args", (), {"host": q.Path("h/TiaMcp.Host.exe"), "worker": q.Path("w/W.exe")})()
        root = q.Path("D:/r").resolve()
        text = q.instructions(root, "D:/p/P.ap21", args)
        self.assertNotRegex(text, "<root>|<project>|<script>|<host>|<worker>")
        self.assertIn(str(root / "workspace"), text)
        self.assertIn('verify --root "%s" --host "%s"' % (root, args.host.resolve()), text)

    def test_block_identity_and_offline_html(self):
        direct = {"path": REF["objectPath"], "name": "TiaMcp_EnableGate", "number": 1, "kind": "FB"}
        self.assertEqual({"path": REF["objectPath"], "name": "TiaMcp_EnableGate"},
                         q.same_block({"details": {"block": {"path": REF["objectPath"], "name": "TiaMcp_EnableGate"}}}, direct))
        with self.assertRaises(AssertionError):
            q.same_block({"details": {"block": {"path": REF["objectPath"], "name": "Other"}}}, direct)
        q.offline_html('<script>const u="https://example.com";</script><a href="#x">x</a>')
        for remote in ('<script src="https://cdn.example/x.js"></script>', "<link href=//cdn.example/x.css>", "<style>@import url('https://x/y.css')</style>"):
            with self.assertRaises(AssertionError):
                q.offline_html(remote)


if __name__ == "__main__":
    unittest.main()
