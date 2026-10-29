"""Exact UA polling capture, Host crash/reopen and export on a real encrypted loopback server."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import uuid
import time
import base64
import csv
import io
import copy

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
    report = dict(schemaVersion="opcua-capture-mcp-qualification.v1", status="failed", checks=[], processes=[],
                  nativeStarted=False, physicalQualified=False, realLoopback=True, actualDpapi=True)
    def check(condition, name):
        require(condition, name); report["checks"].append(name)
    def inputs():
        return {str(p): hashlib.sha256(p.read_bytes()).hexdigest()
                for folder in [host.parent, server_dll.parent] for p in sorted(folder.iterdir())
                if p.is_file() and p.suffix in {".dll", ".exe", ".json"}}
    before = inputs(); write(out / "inputs-before.json", before)
    source_paths = [
        "scripts/qualify_opcua_capture.py", "scripts/opcua_runtime_checks.py", "scripts/smoke_mcp.py",
        "contracts/opcua-runtime.v1.schema.json", "src/TiaMcp.Core/PhysicalRuntime/OpcUaRuntimeContracts.cs",
        "src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeClient.cs", "src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeClient.Arrays.cs", "src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeSessionStore.cs",
        "src/TiaMcp.Host/OpcUaRuntimeSessions.cs", "src/TiaMcp.Host/OpcUaRuntimeTools.cs",
        "src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeCaptures.cs", "src/TiaMcp.Core/PhysicalRuntime/RuntimeCaptureStore.cs",
        "src/TiaMcp.Core/PhysicalRuntime/OpcUaCaptureContracts.cs", "contracts/opcua-runtime-capture.v1.schema.json",
        "tests/TiaMcp.OpcUa.Runtime.Tests/CaptureChecks.cs", "tests/TiaMcp.OpcUa.Runtime.Tests/Program.cs", "tests/TiaMcp.OpcUa.Runtime.Tests/FixtureServer.cs",
    ]
    source_paths += ["src/TiaMcp.Core/PhysicalRuntime/OpcUaReconnectEvent.cs", "tests/TiaMcp.OpcUa.Runtime.Tests/ReconnectChecks.cs", "tests/TiaMcp.OpcUa.Runtime.Tests/FixtureServer.Subscriptions.cs"]
    def source_inputs():
        return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in source_paths}
    source_before = source_inputs(); write(out / "sources-before.json", source_before)
    clients = []; crashed = set(); server = None; server_lines = queue.Queue(); server_errors = []
    birth = None
    secret = "fixture-only-secret"; vault = uuid.uuid4().hex; final_id = None
    schema = json.loads((ROOT / "contracts/opcua-runtime.v1.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    capture_schema=json.loads((ROOT / "contracts/opcua-runtime-capture.v1.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(capture_schema)
    def fixture(operation, **kwargs):
        server.stdin.write(json.dumps(dict(operation=operation, **kwargs)) + "\n"); server.stdin.flush()
        return json.loads(server_lines.get(timeout=20))
    def client():
        c = Client(host, 35, ["--no-native-workers", "--artifacts", str(out / "store")])
        birth = psutil.Process(c.process.pid).create_time()
        clients.append((c, birth))
        c.request("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="ua-loopback", version="1")))
        c.notify("notifications/initialized")
        return c
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
        if value.get("schemaVersion")=="opcua-runtime-capture-status.v1":
            Draft202012Validator(capture_schema).validate(value)
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
        ready = json.loads((out / "server/ready.json").read_text(encoding="utf-8"))
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
        capture = uuid.uuid4().hex
        request = dict(attemptId=attempt, nodes=nodes, intervalMilliseconds=100, durationSeconds=10, maximumSamples=len(nodes))
        call(c,"capture_start",dict(captureId=capture,request=request))
        deadline=time.monotonic()+20
        while True:
            status=call(c,"capture_status",dict(captureId=capture))
            if status["state"] != "running": break
            require(time.monotonic()<deadline,"capture termination deadline");time.sleep(0.05)
        check(status["recordedProgress"]["terminalReason"]=="sampleLimit","Capture sample limit")
        snapshot_ref=status["snapshotRef"]
        page=call(c,"capture_page",dict(snapshotRef=snapshot_ref,count=200))
        reads=[json.loads(x["readResultJson"]) for x in page["rows"] if x["kind"]=="read"]
        packet=dict(attemptId=attempt,startedAt=reads[0]["startedAt"],completedAt=reads[-1]["completedAt"],items=[r["items"][0] for r in reads])
        verify_values(packet);check(True,"Independent captured value/status/quality/timestamp oracle")
        exported=call(c,"capture_export",dict(snapshotRef=snapshot_ref,format="csv"))
        def download(artifact):
            data=b"";offset=0
            while True:
                part=c.tool("artifact_read",dict(path=artifact["path"],offset=offset))
                data+=base64.b64decode(part["data"])
                if part["complete"]:break
                offset=part["nextOffset"]
            check(len(data)==artifact["bytes"] and hashlib.sha256(data).hexdigest()==artifact["sha256"],"Capture artifact_read complete hash-bound transfer")
            return data
        originals=[download(x) for x in exported["artifacts"]]
        exact=json.loads(originals[0]);csv_rows=list(csv.DictReader(io.StringIO(originals[1].decode())))
        check(exact["schemaVersion"]=="opcua-runtime-capture-export.v1" and exact["rows"]==page["rows"],"JSON export retains exact original read JSON")
        check([r["read_result_json"] for r in csv_rows]==[r["readResultJson"] or "" for r in exact["rows"]],"CSV preserves complete original read JSON")
        Draft202012Validator({**capture_schema,"$ref":"#/$defs/export"}).validate(exact)
        write(out/"capture-original-export.json",exact)
        read_copy=copy.deepcopy(packet)
        report["captureNegativeControls"]=controls(read_copy,fixture("audit")["audit"])
        before_audit=fixture("audit")["audit"]
        call(c,"capture_start",dict(captureId=capture,request=request))
        call(c,"capture_stop",dict(captureId=capture))
        call(c,"capture_start",dict(captureId=capture,request=dict(request,durationSeconds=11)),code="UaCaptureRequestChanged")
        check(fixture("audit")["audit"]==before_audit,"Completed capture observations never replay")
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
        protected_capture=uuid.uuid4().hex
        call(c,"capture_start",dict(captureId=protected_capture,request=dict(attemptId=attempt,nodes=nodes[:2],intervalMilliseconds=100,durationSeconds=10,maximumSamples=2)))
        deadline=time.monotonic()+15
        while True:
            protected_status=call(c,"capture_status",dict(captureId=protected_capture))
            if protected_status["state"]!="running":break
            require(time.monotonic()<deadline,"protected capture completion");time.sleep(0.05)
        protected_rows=call(c,"capture_page",dict(snapshotRef=protected_status["snapshotRef"]))["rows"]
        check([json.loads(r["readResultJson"])["items"][0]["value"] for r in protected_rows if r["kind"]=="read"]==["-9223372036854775808","18446744073709551615"],"Actual protected DPAPI capture retains exact integer extrema")
        recovery_capture=uuid.uuid4().hex
        before_recovery=fixture("audit")["audit"]
        fixture("failValueReads",value=1)
        recovery_request=dict(attemptId=attempt,nodes=nodes[:1],intervalMilliseconds=100,durationSeconds=8,maximumSamples=3,maximumReconnectAttempts=2)
        call(c,"capture_start",dict(captureId=recovery_capture,request=recovery_request))
        deadline=time.monotonic()+15
        while True:
            recovery_status=call(c,"capture_status",dict(captureId=recovery_capture))
            if recovery_status["state"]!="running":break
            require(time.monotonic()<deadline,"recovered polling completion");time.sleep(0.05)
        recovery_ref=recovery_status["snapshotRef"]
        recovery_rows=call(c,"capture_page",dict(snapshotRef=recovery_ref,count=200))["rows"]
        transitions=[r["reconnect"] for r in recovery_rows if "reconnect" in r]
        check([r["state"] for r in transitions]==["starting","succeeded"],"Protected polling records reactivation boundaries")
        check([r["errorCode"] for r in recovery_rows if r["errorCode"]]==["UaTransportInterrupted"],"Original failed read is retained")
        check([json.loads(r["readResultJson"])["items"][0]["value"] for r in recovery_rows if r["readResultJson"]]==["-9223372036854775808"]*2,"Protected polling continues with exact values")
        recovered_export=call(c,"capture_export",dict(snapshotRef=recovery_ref,format="csv"))
        recovered_json=json.loads(download(recovered_export["artifacts"][0]))
        recovered_csv=list(csv.DictReader(io.StringIO(download(recovered_export["artifacts"][1]).decode())))
        check([json.loads(r["reconnect_json"]) for r in recovered_csv if r["reconnect_json"]]==transitions,"CSV preserves reconnect details")
        Draft202012Validator({**capture_schema,"$ref":"#/$defs/export"}).validate(recovered_json)
        call(c,"capture_start",dict(captureId=uuid.uuid4().hex,request=dict(recovery_request,maximumReconnectAttempts=4)),code="UaCaptureInvalid")
        recovery_audit=fixture("audit")["audit"][len(before_recovery):]
        check(sum(e["operation"]=="ActivateSession" for e in recovery_audit)==1 and not any(e["operation"] in {"CreateSession","TransferSubscriptions","Write","Call"} for e in recovery_audit),"Exactly one same-session activation without replacement or mutation")
        write(out/"recovered-export.json",recovered_json)
        retired = subprocess.run([str(host), "--credential-store", "retire", "--vault-name", vault,
                                  "--binding", str(out / "binding.json"), "--reference", str(out / "reference.json")],
                                 capture_output=True, timeout=30)
        check(retired.returncode == 0, "Protected credential retirement")
        snapshot = fixture("audit")
        call(c, "read", dict(attemptId=attempt, nodes=[nodes[0]]), code="UaCredentialUnavailable")
        check(fixture("audit")["audit"] == snapshot["audit"], "Retired credential refuses before any UA request")
        check(call(c, "disconnect", dict(attemptId=attempt))["state"] == "closed", "Retirement still allows owned cleanup")
        final_id = uuid.uuid4().hex
        check(call(c, "connect", dict(attemptId=final_id, selection=selected))["state"] == "connected", "Fresh healthy capture-independent session for Host disposal")
        interrupted=uuid.uuid4().hex
        crash_request=dict(attemptId=final_id,nodes=[nodes[0]],intervalMilliseconds=100,durationSeconds=60)
        call(c,"capture_start",dict(captureId=interrupted,request=crash_request))
        deadline=time.monotonic()+15
        while True:
            prefix=call(c,"capture_status",dict(captureId=interrupted))
            if prefix["recordedProgress"]["rows"]>=2: break
            require(time.monotonic()<deadline,"committed prefix before crash");time.sleep(0.1)
        c.process.kill();c.process.wait(timeout=10);crashed.add(c.process.pid)
        check(c.process.returncode is not None,"Original owned Host intentionally crashed after committed acquisition")
        paths=[p for p in (out/"store/runtime-ua-acquisition/runtime-captures").rglob("*") if p.is_file() and p.name != "owner.lock"]
        prior_hashes={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
        before_audit=fixture("audit")["audit"]
        c=client()
        check(call(c,"capture_status",dict(captureId=recovery_capture))["snapshotRef"]==recovery_ref,"Recovered capture reopens unchanged after Host restart")
        call(c,"capture_start",dict(captureId=recovery_capture,request=recovery_request))
        check(call(c,"capture_page",dict(snapshotRef=recovery_ref,count=200))["rows"]==recovery_rows,"Exact recovery events survive another Host")
        interrupted_status=call(c,"capture_status",dict(captureId=interrupted))
        check(interrupted_status["state"]=="interrupted" and interrupted_status["uncommittedDataUnknown"],"New Host reports interrupted prefix and unknown tail")
        call(c,"capture_start",dict(captureId=interrupted,request=crash_request))
        call(c,"capture_stop",dict(captureId=interrupted))
        call(c,"capture_page",dict(snapshotRef=interrupted_status["snapshotRef"]))
        call(c,"capture_export",dict(snapshotRef=interrupted_status["snapshotRef"]))
        check(call(c,"capture_status",dict(captureId=capture))["snapshotRef"]==snapshot_ref,"Prior completed capture reopens unchanged in another Host")
        check(fixture("audit")["audit"]==before_audit,"Crash reopen/export and repeated start/stop cause no UA request")
        check(all(hashlib.sha256(Path(p).read_bytes()).hexdigest()==digest for p,digest in prior_hashes.items()),"Every original capture byte unchanged across restart")
        check(call(c,"status",dict(attemptId=final_id))["state"]=="hostEndedRemoteUnknown","Crashed session cleanup remains honestly unknown")
        call(c,"connect",dict(attemptId=uuid.uuid4().hex,selection=selected),code="UaEndpointSessionUnresolved")
        check(call(c,"disconnect",dict(attemptId=final_id,acknowledgeUnknown=True))["state"]=="abandonedRemoteUnknown","Explicit local acknowledgement does not claim remote logout")
        final_id=None
        # Wait for this owned synthetic server's actual timeout; retain proof instead of assuming crash equals logout.
        expiry=time.monotonic()+75
        while fixture("audit")["sessions"]:
            require(time.monotonic()<expiry,"Owned crashed fixture session timeout");time.sleep(0.25)
        check(True,"Actual fixture expires crashed session independently")
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
                report["processes"].append(dict(role="Host", pid=c.process.pid, birth=host_birth, intentionalCrash=c.process.pid in crashed, originalOwnedProcessWaited=True, **receipt))
                report.setdefault("hostLogs", []).extend(c.errors)
                cleanup_check((c.process.pid in crashed or receipt["hostExitCode"] == 0) and not receipt["forcedHostStop"], "Original Host exited: intentional crash or clean shutdown")
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
            state = json.loads((out / "store/runtime-ua" / (final_id + ".json")).read_text(encoding="utf-8"))
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
        report["cleanupErrors"] = cleanup_errors
        write(out / "result.json", report)
    require(report["status"] == "passed", "Qualification failed; see retained original result.json")
    print("PASS OPC UA capture MCP: " + str(len(report["checks"])) + " checks")


if __name__ == "__main__":
    main()
