"""Transport/report tests with a protocol fake. These never qualify TIA, SSH or a VM."""
import base64
import contextlib
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest import mock

from qualify_deployment import McpClient, ProtocolFailure, load_configuration, qualify, read_artifact, read_catalog

ROOT = Path(__file__).resolve().parents[1]
READ_ONLY_TOOLS = {"feature_catalog", "tia_capabilities", "tia_discover", "tia_session_status"}
ALL_TOOLS = sorted(READ_ONLY_TOOLS | {"sim_status", "tia_start", "tia_create_scratch", "tia_detach", "project_inventory", "block_read", "artifact_read"})
EXPORT = b"<Document><FC Name='TiaMcp_EnableGate'/></Document>"


def fake_mcp(scenario, log_file):
    owned = False
    for line in sys.stdin:
        message = json.loads(line)
        with open(log_file, "a", encoding="utf-8") as log:
            log.write(json.dumps(message) + "\n")
        if "id" not in message:
            continue
        method = message["method"]
        if scenario == "timeout" and method != "ping":
            time.sleep(2)
        if scenario == "banner":
            print("unexpected startup banner", flush=True)
            continue
        name = message.get("params", {}).get("name")
        args = message.get("params", {}).get("arguments", {})
        error = False
        if method == "initialize":
            result = {"protocolVersion": "2025-11-25", "serverInfo": {"name": "protocol-fake-not-TIA", "version": "test"}}
        elif method == "tools/list":
            result = {"tools": [{"name": name} for name in ALL_TOOLS]}
        elif method == "tools/call":
            if name == "feature_catalog":
                result = {"targetAvailabilityProbed": False, "catalogVersion": "fake",
                    "features": [{"featureId": name} for name in ALL_TOOLS],
                    "pagination": {"total": len(ALL_TOOLS), "complete": True}}
            elif name == "tia_capabilities":
                if scenario == "missing_tia":
                    error = True
                    result = {"error": {"code": "WorkerStartFailed", "message": "Native API intentionally absent in fake."}}
                else:
                    result = {"tiaVersion": 21, "architecture": "x64", "apiDirectory": "fake-api", "apiAssemblyVersion": "fake-21"}
            elif name == "tia_discover":
                result = {"processes": []}
            elif name == "tia_session_status":
                result = {"attached": owned, "ownsPortalProcess": owned, "projects": []}
            elif name == "sim_status":
                result = {"available": True, "instances": []}
            elif name == "tia_start":
                owned = True
                result = {"attached": True, "ownsPortalProcess": True, "projects": []}
            elif name == "tia_create_scratch":
                result = {"completed": scenario != "compile_failure", "compile": {"errorCount": 1 if scenario == "compile_failure" else 0}}
            elif name == "project_inventory":
                result = {"blocks": [{"name": "TiaMcp_EnableGate"}], "plcs": [{"name": "FakePLC"}]}
            elif name == "block_read":
                result = {"artifact": {"path": "C:\\remote artifacts\\scratch\\gate.xml", "sha256": hashlib.sha256(EXPORT).hexdigest()}}
            elif name == "artifact_read":
                # Deliberately use several chunks to exercise offset boundaries.
                offset = args["offset"]
                data = EXPORT[offset:offset + 10]
                result = {"offset": offset, "count": len(data), "nextOffset": offset + len(data),
                    "encoding": "base64", "data": base64.b64encode(data).decode(), "bytes": len(EXPORT),
                    "sha256": "0" * 64 if scenario == "bad_hash" else hashlib.sha256(EXPORT).hexdigest(),
                    "complete": offset + len(data) == len(EXPORT)}
            elif name == "tia_detach":
                owned = False
                result = {"attached": False, "ownsPortalProcess": False, "projects": [], "disposedOwnedPortalConnection": True}
            else:
                error, result = True, {"error": {"code": "UnexpectedFakeTool"}}
            result = {"isError": error, "structuredContent": result}
        else:
            result = {}
        response_id = message["id"] + 1 if scenario == "wrong_id" else message["id"]
        print(json.dumps({"jsonrpc": "2.0", "id": response_id, "result": result}), flush=True)
    return 9 if scenario == "bad_exit" else 0


class DeploymentQualificationTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="qualification-pure-", dir=ROOT / "artifacts")
        self.directory = Path(self.temporary.name)
        self.addCleanup(self.temporary.cleanup)

    def config(self, scenario="success", server=None):
        server = server or {"command": sys.executable, "args": [str(Path(__file__).resolve()), "--fake-mcp", scenario, str(self.directory / "calls.jsonl")]}
        path = self.directory / "mcp.json"
        path.write_text(json.dumps({"mcpServers": {"tia-v21": server}}), encoding="utf-8")
        return load_configuration(path, "tia-v21")

    def run_case(self, scenario="success", **arguments):
        with contextlib.redirect_stdout(io.StringIO()):
            result = qualify(self.config(scenario), self.directory / "report.json", timeout=3, **arguments)
        self.assertEqual(result, json.loads((self.directory / "report.json").read_text(encoding="utf-8")))
        return result

    def calls(self):
        return [json.loads(line) for line in (self.directory / "calls.jsonl").read_text(encoding="utf-8").splitlines()]

    def test_default_is_offline_read_only_and_reconnects(self):
        result = self.run_case(target_label="VMware label is not evidence")
        self.assertTrue(result["completed"])
        self.assertEqual(result["scope"]["virtualMachineQualification"], "unverified")
        self.assertEqual(result["scope"]["runtimeExecution"], "unverified")
        self.assertEqual(len(result["sessions"]), 2)
        tool_calls = [row["params"]["name"] for row in self.calls() if row["method"] == "tools/call"]
        self.assertEqual(set(tool_calls), READ_ONLY_TOOLS)
        self.assertEqual(tool_calls.count("tia_discover"), 1)

    def test_simulation_is_explicit_and_still_not_runtime_qualification(self):
        result = self.run_case(simulation=True)
        self.assertTrue(result["completed"])
        self.assertIn("sim_status", [row.get("params", {}).get("name") for row in self.calls()])
        self.assertEqual(result["scope"]["runtimeExecution"], "unverified")

    def test_missing_native_api_is_blocked_without_fabricating_vm_result(self):
        result = self.run_case("missing_tia")
        self.assertEqual(result["status"], "blocked")
        self.assertFalse(result["completed"])
        stages = {row["name"]: row for row in result["stages"]}
        self.assertEqual(stages["feature_catalog"]["status"], "passed")
        self.assertEqual(stages["engineering_environment"]["status"], "blocked")
        self.assertEqual(stages["engineering_discovery"]["status"], "blocked")
        self.assertEqual(stages["reconnect_catalog"]["status"], "passed")

    def test_explicit_engineering_uses_owned_scratch_and_transfers_remote_bytes(self):
        result = self.run_case(engineering=True)
        self.assertTrue(result["completed"])
        tools = [row.get("params", {}).get("name") for row in self.calls()]
        self.assertNotIn("tia_attach", tools)
        self.assertNotIn("project_open", tools)
        self.assertEqual(tools.count("tia_start"), 1)
        self.assertEqual(tools.count("tia_create_scratch"), 1)
        self.assertEqual(tools.count("tia_detach"), 1)
        transfer = next(row for row in result["stages"] if row["name"] == "owned_scratch_artifact_transfer")
        self.assertEqual(transfer["result"]["sha256"], hashlib.sha256(EXPORT).hexdigest())
        self.assertEqual(transfer["result"]["bytes"], len(EXPORT))

    def test_engineering_failure_detaches_once_and_never_replays_creation(self):
        result = self.run_case("compile_failure", engineering=True)
        self.assertEqual(result["status"], "failed")
        tools = [row.get("params", {}).get("name") for row in self.calls()]
        self.assertEqual(tools.count("tia_create_scratch"), 1)
        self.assertEqual(tools.count("tia_detach"), 1)
        self.assertNotIn("block_read", tools)

    def test_artifact_corruption_is_failed(self):
        result = self.run_case("bad_hash", engineering=True)
        row = next(row for row in result["stages"] if row["name"] == "owned_scratch_artifact_transfer")
        self.assertEqual(row["status"], "failed")
        self.assertIn("hash/length", row["reason"])

    def test_nonzero_launcher_exit_cannot_pass(self):
        result = self.run_case("bad_exit")
        self.assertEqual(result["status"], "failed")
        self.assertEqual(len(result["sessions"]), 1)
        self.assertEqual(result["sessions"][0]["exitCode"], 9)

    def test_stdout_banner_is_protocol_failure_and_not_retried(self):
        result = self.run_case("banner")
        self.assertEqual(result["status"], "failed")
        self.assertEqual(len(self.calls()), 1)

    def test_wrong_response_id_is_protocol_failure(self):
        result = self.run_case("wrong_id")
        self.assertEqual(result["status"], "failed")
        self.assertEqual(len(self.calls()), 1)

    def test_report_sharing_retry_does_not_replay_failed_protocol_request(self):
        original = Path.replace
        attempts = []
        def replace(path, target):
            data = path.read_bytes()
            if any(row.get('name') == 'initialize' and row.get('status') == 'failed'
                   for row in json.loads(data).get('stages', [])) and len(attempts) < 2:
                attempts.append(data)
                if len(attempts) == 1:
                    error = PermissionError('synthetic Windows sharing denial')
                    error.winerror = 5
                    raise error
            return original(path, target)
        with mock.patch.object(Path, 'replace', replace):
            result = self.run_case('wrong_id')
        self.assertEqual('failed', result['status'])
        self.assertEqual(2, len(attempts))
        self.assertEqual(attempts[0], attempts[1])
        self.assertEqual(1, len(self.calls()))
        self.assertFalse((self.directory / 'report.json.pending').exists())

    def test_report_nonretryable_error_keeps_pending_without_starting_process(self):
        error = OSError('synthetic disk failure')
        with mock.patch.object(Path, 'replace', side_effect=error) as replace, \
             mock.patch('qualify_deployment.McpClient') as client:
            with self.assertRaises(OSError):
                self.run_case()
        self.assertEqual(1, replace.call_count)
        client.assert_not_called()
        self.assertTrue((self.directory / 'report.json.pending').is_file())

    def test_timeout_marks_transport_terminal_and_does_not_replay(self):
        client = McpClient(self.config("timeout"), 3)
        try:
            # Establish readiness before measuring a request timeout; interpreter
            # startup under build load is not the behavior this test exercises.
            self.assertEqual(client.request("ping"), {})
            client.timeout = 0.1
            with self.assertRaisesRegex(ProtocolFailure, "exceeded"):
                client.request("initialize")
            with self.assertRaisesRegex(ProtocolFailure, "terminal"):
                client.request("initialize")
        finally:
            closed = client.close()
        self.assertTrue(closed["forcedLauncherStop"])
        self.assertEqual([call["method"] for call in self.calls()], ["ping", "initialize"])

    def test_configuration_requires_stdio_strings_and_existing_cwd(self):
        invalid = [
            {"command": ""}, {"command": "python", "args": [7]},
            {"command": "python", "args": "-c"}, {"command": "python", "env": {"A": 7}},
            {"command": "python", "env": {"A=B": "x"}}, {"command": "python", "cwd": "relative"},
            {"command": "python", "type": "http"}, {"command": "python", "url": "http://invalid"},
        ]
        for server in invalid:
            with self.subTest(server=server), self.assertRaises(ValueError):
                self.config(server=server)

    def test_ssh_requires_explicit_noninteractive_identity_and_timeout(self):
        arguments = ["-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=15", "--", "tia-vm", "opaque remote command"]
        loaded = self.config(server={"command": "ssh.exe", "args": arguments})
        self.assertEqual(loaded["transport"], "ssh")
        for removed in ("-T", "BatchMode=yes", "StrictHostKeyChecking=yes", "ConnectTimeout=15"):
            bad = [argument for argument in arguments if argument != removed]
            with self.subTest(removed=removed), self.assertRaises(ValueError):
                self.config(server={"command": "ssh.exe", "args": bad})
        for inserted in ("-oStrictHostKeyChecking=no", "-t", "-oConnectTimeout=999"):
            with self.subTest(inserted=inserted), self.assertRaises(ValueError):
                self.config(server={"command": "ssh.exe", "args": [inserted, *arguments]})

    def test_environment_values_are_not_written_to_report(self):
        configuration = self.config()
        configuration["environment"]["QUALIFICATION_SECRET_FOR_TEST"] = "do-not-record-test-secret"
        with contextlib.redirect_stdout(io.StringIO()):
            result = qualify(configuration, self.directory / "report.json", timeout=3)
        self.assertTrue(result["completed"])
        self.assertNotIn("do-not-record-test-secret", json.dumps(result))

    def test_stalled_catalog_cannot_be_marked_complete(self):
        class Fake:
            def tool(self, *_):
                return {"targetAvailabilityProbed": False, "features": [{"featureId": "x"}],
                    "pagination": {"total": 2, "complete": False, "nextOffset": 0}}
        with self.assertRaisesRegex(ValueError, "advance"):
            read_catalog(Fake())

    def test_changed_artifact_between_chunks_is_rejected(self):
        class Fake:
            count = 0
            def tool(self, *_):
                self.count += 1
                return {"offset": self.count - 1, "count": 1, "nextOffset": self.count,
                    "encoding": "base64", "data": "YQ==", "bytes": 2,
                    "sha256": "a" if self.count == 1 else "b", "complete": self.count == 2}
        with self.assertRaisesRegex(ValueError, "changed"):
            read_artifact(Fake(), {"path": "remote.xml"})


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--fake-mcp":
        raise SystemExit(fake_mcp(sys.argv[2], sys.argv[3]))
    unittest.main()
