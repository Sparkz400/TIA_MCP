"""Offline checks of qualify_online_routes.py; nothing here starts TIA or PLCSIM."""
import copy
import json
import pathlib
import tempfile
import unittest

import qualify_online_routes as q


def row(left, state, *children):
    return {"left": left, "right": left, "state": state, "detail": None, "children": list(children)}


SESSION = {"completed": True, "operatingStateBefore": "Run", "operatingStateAfter": "Run",
           "login": {"username": "TiaMcpTest_x", "secretReturned": False},
           "onlineChallenges": [{"type": "OnlineAuthenticationConfiguration", "decision": "ProjectUser", "offered": ["ProjectUser"], "username": "TiaMcpTest_x"}],
           "cleanup": [{"action": "goOffline", "completed": True}, {"action": "stopDiscoveryObserver", "completed": True}]}


class OnlineRoutesRunnerTests(unittest.TestCase):
    def test_only_rows_that_are_neither_identical_nor_irrelevant_differ(self):
        tree = row("PLC", "FolderContentsIdentical", row("Program blocks", "FolderContentsIdentical", row("OnDb", "ObjectsIdentical"), row("Info", "CompareIrrelevant")))
        self.assertEqual([], q.differences(tree))
        tree["children"][0]["children"][0]["state"] = "ObjectsDifferent"
        self.assertEqual([{"left": "OnDb", "right": "OnDb", "state": "ObjectsDifferent"}], q.differences(tree))
        self.assertEqual(["PLC", "Program blocks", "OnDb", "Info"], [n["left"] for n in q.nodes(tree)])

    def test_an_online_session_answers_exactly_one_login_as_the_runtime_user(self):
        self.assertEqual(SESSION["onlineChallenges"], q.online_session(SESSION, "TiaMcpTest_x")["onlineChallenges"])
        for mutate in (lambda r: r.update(login={"username": "TiaMcpTest_x", "secretReturned": True}),
                       lambda r: r["onlineChallenges"].append({"type": "TlsVerificationConfiguration", "decision": "NonTrusted"}),
                       lambda r: r["onlineChallenges"].append({"type": "OnlineAuthenticationConfiguration", "decision": "ProjectUser", "username": "TiaMcpTest_x"}),
                       lambda r: r.update(operatingStateAfter="Stop"),
                       lambda r: r["cleanup"].append({"action": "goOffline", "completed": False}),
                       lambda r: r.update(failure={"code": "X"})):
            report = copy.deepcopy(SESSION)
            mutate(report)
            with self.assertRaises(AssertionError):
                q.online_session(report, "TiaMcpTest_x")
        with self.assertRaises(AssertionError):
            q.online_session(SESSION, "TiaMcpTest_y")

    def test_only_the_upload_pin_may_answer_tls_trusted(self):
        report = copy.deepcopy(SESSION)
        report["onlineChallenges"][:0] = [{"type": "TlsVerificationConfiguration", "decision": "Trusted", "pinnedCertificateSha256": "a" * 64,
                                           "tlsAuthentication": "firstUsePinnedCertificate"}] * 2
        q.online_session(report, "TiaMcpTest_x", pin="a" * 64)
        for pin in (None, "b" * 64):
            with self.assertRaises(AssertionError):
                q.online_session(report, "TiaMcpTest_x", pin=pin)
        with self.assertRaises(AssertionError):
            q.online_session(report, "TiaMcpTest_x", unauthenticated=True)

    def test_certificate_less_tls_is_trusted_only_as_the_upload_unauthenticated_trust(self):
        report = copy.deepcopy(SESSION)
        report["onlineChallenges"][:0] = [{"type": "TlsVerificationConfiguration", "decision": "Trusted", "certificates": [],
                                           "tlsAuthentication": "unauthenticated"}]
        q.online_session(report, "TiaMcpTest_x", unauthenticated=True)
        with self.assertRaises(AssertionError):
            q.online_session(report, "TiaMcpTest_x")
        report["onlineChallenges"][0]["certificates"] = [{"sha256": "a" * 64}]
        with self.assertRaises(AssertionError):
            q.online_session(report, "TiaMcpTest_x", unauthenticated=True)

    def test_an_edited_metadata_copy_keeps_its_file_name_and_leaves_the_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "scratch-credentials" / "abc.json"
            source.parent.mkdir()
            source.write_text(json.dumps({"credentialId": "abc", "cpuName": "ScratchPLC"}), encoding="utf-8")
            copied = pathlib.Path(q.edited_copy(source, root / "cpu" / "scratch-credentials", cpuName="ScratchPLCOther"))
            self.assertEqual("abc.json", copied.name)
            self.assertEqual({"credentialId": "abc", "cpuName": "ScratchPLCOther"}, json.loads(copied.read_text(encoding="utf-8")))
            self.assertEqual({"credentialId": "abc", "cpuName": "ScratchPLC"}, json.loads(source.read_text(encoding="utf-8")))
            self.assertEqual([], list(copied.parent.glob("*.dpapi")))

    def test_program_keys_blocks_by_name_and_refuses_a_truncated_inventory(self):
        software = {"completeness": {"truncated": False}, "blocks": [{"name": "Main", "kind": "OB", "number": 1, "path": "/a/blocks/Main"},
                                                                     {"name": "OnDb", "kind": "GlobalDB", "number": 1, "path": "/a/blocks/OnDb"}]}
        self.assertEqual({"Main": ["OB", 1], "OnDb": ["GlobalDB", 1]}, q.program(software))
        software["completeness"]["truncated"] = True
        with self.assertRaises(AssertionError):
            q.program(software)


if __name__ == "__main__":
    unittest.main()
