"""Actual packaged/working Host MCP + DPAPI + real local encrypted UA fixture."""
import argparse
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import uuid

import psutil
from jsonschema import Draft202012Validator
from smoke_mcp import Client, ROOT
from opcua_runtime_checks import verify_values, verify_audit, controls, require


def write(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    host, server_dll, out = args.host.resolve(strict=True), args.server.resolve(strict=True), args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion="opcua-runtime-mcp-qualification.v1", status="failed", checks=[], processes=[],
                  nativeStarted=False, physicalQualified=False, realLoopback=True, actualDpapi=True)
    def check(condition, name):
        require(condition, name); report["checks"].append(name)
    def inputs():
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest()
                for folder in [host.parent, server_dll.parent] for p in sorted(folder.iterdir())
                if p.is_file() and p.suffix in {".dll", ".exe", ".json"}}
    before = inputs(); write(out / "inputs-before.json", before)
    source_paths = [
        "scripts/qualify_opcua_runtime.py", "scripts/opcua_runtime_checks.py", "scripts/smoke_mcp.py",
        "contracts/opcua-runtime.v1.schema.json", "src/TiaMcp.Core/PhysicalRuntime/OpcUaRuntimeContracts.cs",
        "src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeClient.cs", "src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeClient.Arrays.cs", "src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeSessionStore.cs",
        "src/TiaMcp.Host/OpcUaRuntimeSessions.cs", "src/TiaMcp.Host/OpcUaRuntimeTools.cs",
        "src/TiaMcp.Host/Security/CredentialVault.OpcUaIdentity.cs", "src/TiaMcp.Host/Security/UaClientIdentityCommand.cs",
        "src/TiaMcp.Host/Security/CredentialVault.cs", "src/TiaMcp.Host/HostOptions.cs", "src/TiaMcp.Host/Program.cs",
        "tests/TiaMcp.OpcUa.Runtime.Tests/Program.cs", "tests/TiaMcp.OpcUa.Runtime.Tests/FixtureServer.cs",
    ]
    def source_inputs():
        return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in source_paths}
    source_before = source_inputs(); write(out / "sources-before.json", source_before)
    clients = []; server = None; server_lines = queue.Queue(); server_errors = []
    birth = None
    secret = "fixture-only-secret"; vault = uuid.uuid4().hex; final_id = None
    identity_name = uuid.uuid4().hex
    identity_root = Path(os.environ["LOCALAPPDATA"]) / "TiaMcpOpcUaIdentities" / identity_name
    schema = json.loads((ROOT / "contracts/opcua-runtime.v1.schema.json").read_text())
    Draft202012Validator.check_schema(schema)
    def fixture(operation, **kwargs):
        server.stdin.write(json.dumps(dict(operation=operation, **kwargs)) + "\n"); server.stdin.flush()
        return json.loads(server_lines.get(timeout=20))
    def client(identity=None):
        extra = ["--ua-client-identity", identity] if identity else []
        c = Client(host, 35, ["--no-native-workers", "--artifacts", str(out / "store"), *extra])
        birth = psutil.Process(c.process.pid).create_time()
        clients.append((c, birth))
        c.request("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="ua-loopback", version="1")))
        c.notify("notifications/initialized")
        return c
    def close_client(c):
        owned = next(item for item in clients if item[0] is c)
        receipt = c.close()
        report["processes"].append(dict(role="Host", pid=c.process.pid, birth=owned[1], originalOwnedProcessWaited=True, **receipt))
        report.setdefault("hostLogs", []).extend(c.errors)
        clients.remove(owned)
        check(receipt["hostExitCode"] == 0 and not receipt["forcedHostStop"], "Identity lifecycle Host closes normally")
    def identity_command(operation, *extra, code=None):
        result = subprocess.run([str(host), "--ua-identity-store", operation, "--name", identity_name, *extra],
                                capture_output=True, timeout=30)
        check(result.returncode == (1 if code else 0), "Identity command outcome: " + operation)
        value = json.loads(result.stderr if code else result.stdout)
        if code:
            check(value["error"] == code and value["privateKeyExported"] is False, "Identity command refuses: " + code)
        else:
            check(value["privateKeyStorage"] == "windowsDpapiCurrentUser" and value["privateKeyExported"] is False,
                  "Identity command returns only public status")
        report.setdefault("identityCommands", []).append(dict(operation=operation, value=value))
        return value
    def call(c, name, arguments=None, code=None):
        response = c.request("tools/call", dict(name="runtime_ua_" + name, arguments=arguments or {}))
        check(secret not in json.dumps(response), "Response redacted: " + name)
        if code:
            check(response.get("isError") and response["structuredContent"]["error"]["code"] == code, "Refused " + code)
        else:
            check(not response.get("isError"), "Successful " + name)
        value = response["structuredContent"]
        if value.get("schemaVersion") == "opcua-runtime-session.v1":
            Draft202012Validator(schema).validate(value)
        report.setdefault("responses", []).append(dict(tool=name, value=value))
        return value
    try:
        server = subprocess.Popen(["dotnet", str(server_dll), "--serve", "--output", str(out / "server")],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        birth = psutil.Process(server.pid).create_time()
        def stdout_reader():
            for line in server.stdout:
                server_lines.put(line)
        def stderr_reader():
            for line in server.stderr:
                server_errors.append(line.rstrip())
        threading.Thread(target=stdout_reader, daemon=True).start()
        threading.Thread(target=stderr_reader, daemon=True).start()
        check(json.loads(server_lines.get(timeout=30)).get("ready"), "Actual fixture server ready")
        ready = json.loads((out / "server/ready.json").read_text())
        check(ready["processId"] == server.pid, "Fixture identity bound to original owned child")
        check(abs(dt.datetime.fromisoformat(ready["processBirthUtc"]).timestamp() - birth) < 0.02,
              "Managed fixture birth matches retained original process lifetime")
        listeners = [x for x in psutil.Process(server.pid).net_connections(kind="tcp") if x.status == psutil.CONN_LISTEN]
        check(bool(listeners) and all(x.laddr.ip in {"127.0.0.1", "::1"} for x in listeners), "Actual fixture listener sockets are loopback only")
        selected = ready["selection"]
        c = client()
        names = {t["name"] for t in c.request("tools/list")["tools"]}
        check({f"runtime_ua_{x}" for x in ["client_identity", "credential_binding", "connect", "browse", "read", "status", "disconnect"]} <= names, "Seven UA tools registered")
        identity = call(c, "client_identity")
        for binary in identity["runtimeBinaries"]:
            check(hashlib.sha256((host.parent / (binary["name"] + ".dll")).read_bytes()).hexdigest() == binary["sha256"],
                  "Actual loaded Host dependency " + binary["name"])
        check(identity["privateKeyStorage"] == "ephemeralHostMemory", "No exported client private key")
        attempt = uuid.uuid4().hex
        connected = call(c, "connect", dict(attemptId=attempt, selection=selected))
        check(connected["state"] == "connected" and connected["peerVerified"], "Real encrypted owned session")
        snapshot = fixture("audit")
        check(call(c, "connect", dict(attemptId=attempt, selection=selected)) == connected, "Lost reply attempt observes without replay")
        call(c, "status", dict(attemptId=attempt))
        check(fixture("audit")["audit"] == snapshot["audit"], "Status and repeated connect emit no UA service")
        call(c, "connect", dict(attemptId=uuid.uuid4().hex, selection=selected), code="UaEndpointSessionUnresolved")
        names = ["int64", "uint64", "double", "bool", "text", "bad", "uncertain", "missingTimes", "array",
                 "denied", "structure", "byteArray", "byteString", "minimumDate"]
        names += ["uint64Array", "int64Matrix", "emptyArray", "stringArray", "byteStrings", "doubleMatrix"]
        nodes = [dict(namespaceUri=ready["namespaceUri"], identifierType="string", identifier=name) for name in names]
        values = call(c, "read", dict(attemptId=attempt, nodes=nodes)); write(out / "values.json", values)
        verify_values(values); check(True, "Independent exact value/type/quality/time oracle")
        browse = call(c, "browse", dict(attemptId=attempt, node=dict(namespaceUri=ready["namespaceUri"], identifierType="string", identifier="fixture"), maximum=4))
        check(browse["truncated"] and browse["continuationReleased"] and len(browse["references"]) == 4, "Real BrowseNext release")
        check(call(c, "disconnect", dict(attemptId=attempt))["state"] == "closed", "Owned UA session closed")
        audit = fixture("audit")
        check(audit["sessions"] == 0, "Server confirms zero remaining owned sessions")
        verify_audit(audit["audit"], healthy=True)
        report["negativeControls"] = controls(values, audit["audit"])
        user = dict(selected, authentication="username", user="reader", vaultName=vault)
        binding = call(c, "credential_binding", dict(selection=user))
        write(out / "binding.json", binding)
        created = subprocess.run([str(host), "--credential-store", "create", "--vault-name", vault,
                                  "--binding", str(out / "binding.json"), "--stdin"],
                                 input=json.dumps(secret).encode(), capture_output=True, timeout=30)
        check(created.returncode == 0 and secret.encode() not in created.stdout + created.stderr, "Real DPAPI protected credential created without echo")
        reference = json.loads(created.stdout)["reference"]; write(out / "reference.json", reference)
        user["credential"] = reference
        attempt = uuid.uuid4().hex
        check(call(c, "connect", dict(attemptId=attempt, selection=user))["state"] == "connected", "Protected username authenticated on real server")
        call(c, "read", dict(attemptId=attempt, nodes=[nodes[0]]))
        retired = subprocess.run([str(host), "--credential-store", "retire", "--vault-name", vault,
                                  "--binding", str(out / "binding.json"), "--reference", str(out / "reference.json")],
                                 capture_output=True, timeout=30)
        check(retired.returncode == 0, "Protected credential retirement")
        snapshot = fixture("audit")
        call(c, "read", dict(attemptId=attempt, nodes=[nodes[0]]), code="UaCredentialUnavailable")
        check(fixture("audit")["audit"] == snapshot["audit"], "Retired credential refuses before any UA request")
        check(call(c, "disconnect", dict(attemptId=attempt))["state"] == "closed", "Retirement still allows owned cleanup")

        close_client(c)
        check(not identity_root.exists(), "Fresh isolated identity name")
        identity_command("inspect", code="UaClientIdentityMissing")
        persistent = identity_command("create")
        identity_command("create", code="UaClientIdentityAlreadyExists")
        public_path = out / "client-public.der"
        check(identity_command("export", "--output", str(public_path)) == persistent, "Public export preserves original status")
        check(public_path.read_bytes() == base64.b64decode(persistent["certificateDerBase64"]) and
              hashlib.sha256(public_path.read_bytes()).hexdigest() == persistent["certificateSha256"], "Export contains exact public DER only")
        identity_command("export", "--output", str(public_path), code="UaClientIdentityUnavailable")
        fixture("trustClient", sha256=persistent["certificateSha256"])
        c = client(identity_name)
        current = call(c, "client_identity")
        check(all(current[k] == persistent[k] for k in ["applicationUri", "certificateSha256", "certificateDerBase64", "privateKeyStorage", "state"]),
              "Host loads exact protected identity")
        identity_command("rotate", "--expected-sha256", persistent["certificateSha256"], code="CredentialStoreBusy")
        persistent_attempt = uuid.uuid4().hex
        check(call(c, "connect", dict(attemptId=persistent_attempt, selection=selected))["state"] == "connected", "Protected private key authenticates on pinned fixture")
        call(c, "read", dict(attemptId=persistent_attempt, nodes=[nodes[0]]))
        check(any(x["sha256"] == persistent["certificateSha256"] and x["accepted"] for x in fixture("audit")["clientCertificates"]),
              "Server independently observed and accepted exact public certificate")
        close_client(c)
        audit_before = fixture("audit")
        c = client(identity_name)
        reopened = call(c, "client_identity")
        check(reopened["hostId"] != current["hostId"] and all(reopened[k] == current[k] for k in
              ["applicationUri", "certificateSha256", "certificateDerBase64", "privateKeyStorage"]), "Host restart preserves identity while replacing session owner")
        check(call(c, "status", dict(attemptId=persistent_attempt))["state"] == "closed", "Restart retains closed session without adoption")
        check(fixture("audit")["audit"] == audit_before["audit"], "Identity reopen and retained status cause no remote requests")
        restart_attempt = uuid.uuid4().hex
        check(call(c, "connect", dict(attemptId=restart_attempt, selection=selected))["state"] == "connected", "Fresh session after restart uses unchanged server trust")
        close_client(c)
        identity_command("rotate", "--expected-sha256", "0" * 64, code="UaClientIdentityChanged")
        rotated = identity_command("rotate", "--expected-sha256", persistent["certificateSha256"])
        check(rotated["applicationUri"] == persistent["applicationUri"] and rotated["certificateSha256"] != persistent["certificateSha256"],
              "Explicit CLI rotation changes key while preserving application URI")
        identity_command("rotate", "--expected-sha256", persistent["certificateSha256"], code="UaClientIdentityChanged")
        c = client(identity_name)
        check(call(c, "client_identity")["certificateSha256"] == rotated["certificateSha256"], "Restart observes rotated certificate")
        denied = call(c, "connect", dict(attemptId=uuid.uuid4().hex, selection=selected))
        check(denied["state"] != "connected", "Rotated key is rejected until new server trust is explicitly provisioned")
        check(any(x["sha256"] == rotated["certificateSha256"] and not x["accepted"] for x in fixture("audit")["clientCertificates"]),
              "Server independently rejects untrusted rotated certificate")
        call(c, "disconnect", dict(attemptId=denied["attemptId"], acknowledgeUnknown=True))
        fixture("trustClient", sha256=rotated["certificateSha256"])
        final_id = uuid.uuid4().hex
        check(call(c, "connect", dict(attemptId=final_id, selection=selected))["state"] == "connected", "Fresh healthy capture-independent session for Host disposal")
        report["status"] = "passed"
    except BaseException as error:
        report["status"] = "failed"
        report["failureType"] = type(error).__name__
        report["failure"] = str(error).replace(secret, "<redacted>")[:2048]
    finally:
        cleanup_errors = []
        def cleanup_check(condition, name):
            if condition:
                report["checks"].append(name)
            else:
                cleanup_errors.append(name); report["status"] = "failed"
        for c, host_birth in clients:
            try:
                receipt = c.close()
                report["processes"].append(dict(role="Host", pid=c.process.pid, birth=host_birth, originalOwnedProcessWaited=True, **receipt))
                report.setdefault("hostLogs", []).extend(c.errors)
                cleanup_check(receipt["hostExitCode"] == 0 and not receipt["forcedHostStop"], "Host original process clean exit")
                cleanup_check(secret not in "\n".join(c.errors), "Host logs contain no credentials")
            except BaseException as error:
                cleanup_check(False, "Host cleanup " + type(error).__name__)
        if server is not None:
            forced = False
            try:
                if server.poll() is None:
                    audit = fixture("audit")
                    report["finalServerAudit"] = audit
                    try:
                        verify_audit(audit["audit"], healthy=True)
                        cleanup_check(True, "Complete final service trace obeys read-only allowlist and chronology")
                    except (ValueError, KeyError, TypeError):
                        cleanup_check(False, "Complete final service trace violated read-only allowlist or chronology")
                    cleanup_check(audit["sessions"] == 0, "Host disposal closes every owned session on server")
                    fixture_line = json.dumps(dict(operation="stop")) + "\n"
                    server.stdin.write(fixture_line); server.stdin.flush(); server.stdin.close()
                    server.wait(timeout=30)
            except BaseException as error:
                cleanup_check(False, "Server cleanup " + type(error).__name__)
            finally:
                if server.poll() is None:
                    forced = True
                    server.kill()  # Original Popen handle of this owned synthetic fixture only.
                    server.wait(timeout=10)
                report["processes"].append(dict(role="server", pid=server.pid, birth=birth, exitCode=server.returncode,
                                                originalOwnedProcessWaited=server.returncode is not None, forcedTermination=forced))
            report["serverLogs"] = server_errors
            cleanup_check(server.returncode == 0 and not forced, "Server original process clean exit")
        if final_id and report["status"] == "passed":
            state = json.loads((out / "store/runtime-ua" / (final_id + ".json")).read_text())
            cleanup_check(state["state"] == "closed", "Host disposal publishes terminal owned state")
        for path in (out / "store").rglob("*"):
            if path.is_file():
                cleanup_check(secret.encode() not in path.read_bytes(), "No credential in retained " + path.name)
        after = inputs(); write(out / "inputs-after.json", after)
        cleanup_check(before == after, "Exact Host/server binary inputs unchanged")
        report["inputHashes"] = before
        source_after = source_inputs(); write(out / "sources-after.json", source_after)
        cleanup_check(source_before == source_after, "Exact qualification source inputs unchanged")
        report["sourceHashes"] = source_before
        # Only this run's random name; never remove the shared identity parent or another profile.
        if identity_root.exists():
            expected_parent = (Path(os.environ["LOCALAPPDATA"]) / "TiaMcpOpcUaIdentities").resolve(strict=True)
            cleanup_check(identity_root.name == identity_name and len(identity_name) == 32 and
                          identity_root.resolve(strict=True).parent == expected_parent and
                          not identity_root.is_symlink() and not identity_root.is_junction(), "Owned identity cleanup path verified")
            if not cleanup_errors:
                try:
                    shutil.rmtree(identity_root)
                    cleanup_check(not identity_root.exists(), "Disposable protected identity removed after all owners exited")
                except OSError:
                    cleanup_check(False, "Owned identity cleanup failed; protected store retained")
        report["cleanupErrors"] = cleanup_errors
        write(out / "result.json", report)
    require(report["status"] == "passed", "Qualification failed; see retained original result.json")
    print("PASS OPC UA MCP: " + str(len(report["checks"])) + " checks")


if __name__ == "__main__":
    main()
