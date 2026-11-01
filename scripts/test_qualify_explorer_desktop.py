"""Offline checks of the desktop roundtrip gates in qualify_explorer_desktop.py; nothing here starts TIA or a product."""
import copy
import re
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

    def test_stateless_discover_negotiates_through_meta(self):
        records = roundtrip()
        records[0]["message"].update(method="server/discover", params={"_meta": {
            "io.modelcontextprotocol/protocolVersion": "2026-07-28", "io.modelcontextprotocol/clientInfo": {"name": "copilot-cli", "version": "1"},
            "io.modelcontextprotocol/clientCapabilities": {"extensions": {"io.modelcontextprotocol/ui": {"mimeTypes": [q.MIME]}}}}})
        self.assertEqual("copilot-cli", q.review(records)["clientInfo"]["name"])
        records[0]["message"]["params"]["_meta"]["io.modelcontextprotocol/clientCapabilities"] = {}
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

    def test_product_host_gets_only_the_roundtrip_toolsets(self):
        import argparse
        from pathlib import Path
        args = argparse.Namespace(worker=Path("w.exe"))
        self.assertEqual(["--toolsets", "core,explorer"], q.host_arguments(args, Path("r"), q.PRODUCT_TOOLSETS)[-2:])
        self.assertNotIn("--toolsets", q.host_arguments(args, Path("r")))
        groups = (Path(__file__).resolve().parents[1] / "src/TiaMcp.Host/ToolGroups.cs").read_text(encoding="utf-8")
        rules = [(p, g.strip('"')) for p, g in re.findall(r'\("([a-z_0-9]+)", ("?\w+"?)\)', groups.split("Rules =")[1].split("];")[0])]
        for tool in ("tia_start", "tia_create_scratch", "project_open", "block_read", "tia_detach", "project_model_capture",
                     "project_model_verify", "project_model_details", "project_model_export"):
            self.assertIn(next(g for p, g in rules if tool.startswith(p)), q.PRODUCT_TOOLSETS.split(",") + ["base"], tool)


if __name__ == "__main__":
    unittest.main()
