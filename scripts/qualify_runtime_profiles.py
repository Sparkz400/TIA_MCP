"""Independent advertised-profile cases against production WebRuntimeClient.

Only a disposable pinned 127.0.0.1 TLS server is used. Expected refusals and
retained fields come from explicit fixtures, not the production parser.
"""
import argparse
import copy
import hashlib
import http.server
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import threading
import time

import psutil


REQUIRED = ["Api.Login", "Api.Logout", "Api.GetPermissions", "PlcProgram.Browse", "PlcProgram.Read"]
OPTIONAL = ["Api.Version", "Api.GetQuantityStructures", "Api.GetAuthenticationMode", "Api.GetSessionInfo"]
LIMITS = {"webapi_max_http_request_body_size": 131072, "webapi_max_parallel_requests": 4,
          "webapi_max_parallel_user_sessions": 200}


def fixtures():
    cases = []

    def add(name, server=None, action="probe", error=None, login=False, unusable=False, profile=None, idle=90, user="reader", mode="local", stop_after=None):
        config = server or {}
        calls = ["Api.Browse"]
        if stop_after != "Api.Browse":
            for method in OPTIONAL[:3]:
                if method in config.get("optional", OPTIONAL):
                    calls.append(method)
                    if method == stop_after:
                        break
        if login:
            calls.append("Api.Login")
            if action in {"verify", "verifyAgain"} and "Api.GetSessionInfo" in config.get("optional", OPTIONAL):
                calls.extend(["Api.GetSessionInfo"] * (2 if action == "verifyAgain" else 1))
            calls.append("Api.Logout")
        cases.append({"name": name, "server": server or {}, "request": {"action": action, "user": user, "mode": mode},
                      "expected": {"error": error, "loginDispatched": login, "sessionUnusable": unusable,
                                   "profile": profile or {}, "idleLimitSeconds": idle, "rpcMethods": calls}})

    add("all-advertised-success", action="verify", login=True,
        profile={"apiVersion": 3.28, "quantityProfile": "siemens-v21-table-max", "sessionInfoObserved": True})
    add("optional-methods-absent", {"optional": []}, action="verify", login=True,
        profile={"apiVersion": None, "quantityProfile": "unavailable", "authenticationModes": None, "sessionInfoObserved": False})
    for method, retained in [
        ("Api.Version", {"apiVersion": 3.28, "quantityProfile": "unavailable", "sessionInfoObserved": False}),
        ("Api.GetQuantityStructures", {"apiVersion": None, "maximumParallelRequests": 4, "sessionInfoObserved": False}),
        ("Api.GetAuthenticationMode", {"apiVersion": None, "authenticationModes": ["local", "umc"], "sessionInfoObserved": False}),
        ("Api.GetSessionInfo", {"apiVersion": None, "quantityProfile": "unavailable", "sessionInfoObserved": True}),
    ]:
        add("only-" + method, {"optional": [method]}, action="verify", login=True, profile=retained)
    add("missing-required-read", {"required": REQUIRED[:-1]}, error="RuntimeMethodsUnavailable", stop_after="Api.Browse")
    for name, value in [("boolean", True), ("null", None), ("object", {}), ("zero", 0), ("unrepresentable", 1e100)]:
        add("version-" + name, {"version": value}, error="RuntimeProfileInvalid", stop_after="Api.Version")
    add("positive-fractional-version", {"version": 0.125}, profile={"apiVersion": 0.125})
    for field in LIMITS:
        for label, invalid in [("missing", None), ("fractional", 1.5), ("boolean", True), ("overflow", 9223372036854775808)]:
            values = dict(LIMITS)
            if label == "missing":
                del values[field]
            else:
                values[field] = invalid
            add(field + "-" + label, {"limits": values}, error="RuntimeQuantityProfileUnsupported", stop_after="Api.GetQuantityStructures")
    add("conflicting-quantity-alias", {"limits": {**LIMITS, "webapi_parallel_requests": 4}},
        error="RuntimeQuantityProfileUnsupported", stop_after="Api.GetQuantityStructures")
    add("maximum-integer-advertisements", {"limits": {key: 9223372036854775807 for key in LIMITS}},
        profile={"maximumRequestBytes": 9223372036854775807, "maximumParallelRequests": 9223372036854775807,
                 "maximumUserSessions": 9223372036854775807})
    add("small-advertised-request-refused-before-login", {"optional": ["Api.GetQuantityStructures"],
        "limits": {**LIMITS, "webapi_max_http_request_body_size": 40}}, action="login", error="RuntimeRequestLimit")
    for name, modes in [("empty", []), ("unknown", ["local", "future-auth"]), ("null-member", [None]),
                        ("duplicate", ["local", "local"]), ("oversized", ["local"] * 17)]:
        add("authentication-" + name, {"authentication": {"authentication_modes": modes}}, error="RuntimeProfileInvalid")
    add("sso-advertised-is-not-login-support", {"authentication": {"authentication_modes": ["umc_sso"]}},
        action="validate", mode="umc_sso", error="RuntimeAuthenticationModeUnsupported",
        profile={"authenticationModes": ["umc_sso"]})
    add("named-local-user-not-admitted-by-umc", {"authentication": {"authentication_modes": ["umc"]}},
        action="validate", error="RuntimeAuthenticationModeUnsupported")
    add("anonymous-local-exception-explicit", {"authentication": {"authentication_modes": ["umc"]}},
        action="validate", user="Anonymous", profile={"authenticationModes": ["umc"]})
    for name, value in [("zero", "PT0S"), ("negative", "-PT1S"), ("invalid", "90"), ("number", 90), ("oversized", "x" * 129)]:
        add("login-timeout-" + name, {"loginExtra": {"runtime_timeout": value}}, action="login",
            login=True, error="RuntimeProfileInvalid")
    add("advertised-long-timeout-does-not-extend-local-limit", {"loginExtra": {"runtime_timeout": "PT180S"}},
        action="login", login=True, profile={"runtimeTimeout": "PT180S"}, idle=90)
    add("short-timeout-cannot-be-extended-on-refresh", {"sessionInfo": [
        {"username": "reader", "authentication_mode": "local", "runtime_timeout": "PT5S"},
        {"username": "reader", "authentication_mode": "local", "runtime_timeout": "PT60S"}]},
        action="verifyAgain", login=True, idle=5, profile={"runtimeTimeout": "PT60S", "sessionInfoObserved": True})
    for name, info, error in [
        ("wrong-user-type", {"username": 1, "authentication_mode": "local"}, "RuntimeSessionIdentityMismatch"),
        ("wrong-mode-type", {"username": "reader", "authentication_mode": True}, "RuntimeSessionIdentityMismatch"),
        ("missing-mode-for-named-user", {"username": "reader"}, "RuntimeSessionIdentityMismatch"),
        ("malformed-session-timeout", {"username": "reader", "authentication_mode": "local", "runtime_timeout": "PT0S"}, "RuntimeProfileInvalid"),
        ("expiration-offset-not-supported", {"username": "reader", "authentication_mode": "local", "password_expiration":
            {"timestamp": "2027-01-01T00:00:00+00:00", "warning": False}}, "RuntimeProfileInvalid"),
        ("expiration-warning-not-boolean", {"username": "reader", "authentication_mode": "local", "password_expiration":
            {"timestamp": "2027-01-01T00:00:00Z", "warning": 1}}, "RuntimeProfileInvalid"),
    ]:
        add(name, {"sessionInfo": [info]}, action="verify", login=True, unusable=True, error=error)
    add("expiration-retained-as-public-metadata", {"sessionInfo": [{"username": "reader", "authentication_mode": "local",
        "password_expiration": {"timestamp": "2027-01-01T00:00:00Z", "warning": True}}]},
        action="verify", login=True, profile={"passwordExpiresUtc": "2027-01-01T00:00:00+00:00", "passwordExpirationWarning": True})
    return cases


def verify(case, result, calls):
    expected, observed = case["expected"], result["observed"]
    assert result["nativeExecuted"] is False and result["deviceIdentityVerified"] is False and result["programMatchVerified"] is False
    for field in ["error", "loginDispatched", "sessionUnusable", "idleLimitSeconds"]:
        assert observed[field] == expected[field], (case["name"], field, observed[field], expected[field])
    for field, value in expected["profile"].items():
        assert observed["profile"][field] == value, (case["name"], field, observed["profile"][field], value)
    assert observed["peerVerified"] is True
    assert observed["loginRejected"] is False and result["actualPinnedLoopbackTls"] is True
    assert calls.count("Api.Login") == int(expected["loginDispatched"]), (case["name"], "secret dispatch boundary", calls)
    assert calls == expected["rpcMethods"], (case["name"], "exact discovery, profile, admission and cleanup order", calls, expected["rpcMethods"])
    assert result["cleanupError"] is None
    for method in OPTIONAL:
        if method not in case["server"].get("optional", OPTIONAL):
            assert method not in calls, "absent optional method must not be guessed"


def qualify(assembly, tls, root, runtime_directory=None):
    assert os.name == "nt"
    root.mkdir(parents=True, exist_ok=False)
    source_root = Path(__file__).resolve().parents[1]
    source_names = ["scripts/qualify_runtime_profiles.py", "tests/TiaMcp.PhysicalRuntime.Tests/RuntimeAdvertisedProfileChecks.cs",
                    "tests/TiaMcp.PhysicalRuntime.Tests/Program.cs", "src/TiaMcp.Core/PhysicalRuntime/WebRuntimeClient.cs",
                    "src/TiaMcp.Core/PhysicalRuntime/WebRuntimeProtocol.cs"]
    original_runtime = sorted(p for p in assembly.parent.iterdir() if p.is_file() and p.suffix in {".dll", ".json", ".exe"})
    override = [] if runtime_directory is None else [(runtime_directory / name).resolve(strict=True)
                                                   for name in ["TiaMcp.Core.dll", "TiaMcp.Graph.dll"]]

    def hashes(paths):
        return {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}

    def inputs():
        return {"sources": hashes([source_root / name for name in source_names]),
                "runtimeFiles": hashes(original_runtime), "runtimeOverride": hashes(override),
                "tlsPublicFiles": hashes([tls / "server.crt", tls / "pins.json"])}

    original_inputs = inputs()
    (root / "inputs-before.json").write_text(json.dumps(original_inputs, indent=2) + "\n", encoding="utf-8")
    staged = root / "probe-runtime"
    staged.mkdir()
    for path in original_runtime:
        shutil.copyfile(path, staged / path.name)
    for path in override:
        shutil.copyfile(path, staged / path.name)
        assert hashlib.sha256((staged / path.name).read_bytes()).hexdigest() == original_inputs["runtimeOverride"][str(path)], \
            "isolated production DLL copy differs from the requested runtime"
    assembly = staged / assembly.name
    staged_inputs = hashes(sorted(staged.iterdir()))
    loaded = {key: {"path": str((staged / name).resolve()), "sha256": hashlib.sha256((staged / name).read_bytes()).hexdigest()}
              for key, name in [("loadedCore", "TiaMcp.Core.dll"), ("loadedGraph", "TiaMcp.Graph.dll")]}
    pin = json.loads((tls / "pins.json").read_text(encoding="utf-8"))["server"]
    state = {"case": None, "calls": [], "infoIndex": 0, "serverErrors": []}

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_POST(self):
            try:
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                method = request["method"]
                case = state["case"]
                config = case["server"]
                state["calls"].append(method)
                assert self.path == "/api/jsonrpc"
                if method == "Api.Browse":
                    value = [{"name": name} for name in config.get("required", REQUIRED) + config.get("optional", OPTIONAL)]
                elif method == "Api.Version":
                    value = config.get("version", 3.28)
                elif method == "Api.GetQuantityStructures":
                    value = config.get("limits", LIMITS)
                elif method == "Api.GetAuthenticationMode":
                    value = config.get("authentication", {"authentication_modes": ["local", "umc"]})
                elif method == "Api.Login":
                    assert request["params"] == {"user": case["request"]["user"], "mode": case["request"]["mode"],
                                                  "password": "synthetic-profile-password"}
                    value = {"token": "synthetic-profile-token", "runtime_timeout": "PT90S", **config.get("loginExtra", {})}
                elif method == "Api.GetSessionInfo":
                    assert self.headers["X-Auth-Token"] == "synthetic-profile-token"
                    options = config.get("sessionInfo", [{"username": "reader", "authentication_mode": "local", "runtime_timeout": "PT90S"}])
                    value = options[min(state["infoIndex"], len(options) - 1)]
                    state["infoIndex"] += 1
                elif method == "Api.Logout":
                    value = True
                else:
                    raise AssertionError("Unexpected RPC " + method)
                body = json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": value}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except (ConnectionError, ssl.SSLError):
                pass
            except Exception as error:
                state["serverErrors"].append(type(error).__name__ + ": " + str(error))
                raise

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(str(tls / "server.crt"), str(tls / "server.key"))
    server.socket = context.wrap_socket(server.socket, server_side=True)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    reports = []
    spec = fixtures()
    (root / "original-cases.json").write_text(json.dumps(spec, indent=2) + "\n", encoding="utf-8")
    try:
        for case in spec:
            directory = root / case["name"]
            directory.mkdir()
            state.update(case=case, calls=[], infoIndex=0, serverErrors=[])
            request = directory / "request.json"
            request.write_text(json.dumps(case["request"]), encoding="utf-8")
            output = directory / "result.json"
            command = ["dotnet", str(assembly), "--profile-check", f"https://127.0.0.1:{server.server_port}", pin, str(request), str(output)]
            with (directory / "stdout.log").open("xb") as stdout, (directory / "stderr.log").open("xb") as stderr:
                process = subprocess.Popen(command, stdout=stdout, stderr=stderr, stdin=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
                life = {"pid": process.pid, "forcedTermination": False}
                try:
                    identity = psutil.Process(process.pid)
                    life.update(created=identity.create_time(), exe=identity.exe())
                    try:
                        life["exitCode"] = process.wait(timeout=20)
                    except subprocess.TimeoutExpired:
                        life["forcedTermination"] = True
                        process.kill()  # Exact original Windows Popen child handle only.
                        life["exitCode"] = process.wait(timeout=10)
                finally:
                    if process.poll() is None:
                        life["forcedTermination"] = True
                        process.kill()
                        life["exitCode"] = process.wait(timeout=10)
                    life["originalOwnedProcessWaited"] = process.returncode is not None
                    (directory / "lifetime.json").write_text(json.dumps(life, indent=2) + "\n", encoding="utf-8")
            result = json.loads(output.read_bytes()) if output.exists() else None
            receipt = {"case": case["name"], "lifetime": life, "rpcMethods": state["calls"], "serverErrors": state["serverErrors"], "result": result}
            (directory / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
            assert life["exitCode"] == 0 and not life["forcedTermination"] and result["processId"] == life["pid"]
            assert not state["serverErrors"], state["serverErrors"]
            assert all(result[key] == value for key, value in loaded.items()), "probe loaded a different production runtime"
            verify(case, result, state["calls"])
            reports.append(receipt)
    finally:
        server.shutdown()
        server.server_close()
    rejected = []
    for name, index, change in [
        ("refusal-promoted-to-success", next(i for i, c in enumerate(spec) if c["name"] == "version-boolean"), lambda r: r["observed"].update(error=None)),
        ("invented-login-dispatch", next(i for i, c in enumerate(spec) if c["name"] == "missing-required-read"), lambda r: r["observed"].update(loginDispatched=True)),
        ("missing-session-invalidation", next(i for i, c in enumerate(spec) if c["name"] == "wrong-user-type"), lambda r: r["observed"].update(sessionUnusable=False)),
        ("rounded-integer-limit", next(i for i, c in enumerate(spec) if c["name"] == "maximum-integer-advertisements"), lambda r: r["observed"]["profile"].update(maximumRequestBytes=9223372036854775808)),
        ("native-qualification-promoted", 0, lambda r: r.update(deviceIdentityVerified=True)),
    ]:
        altered = copy.deepcopy(reports[index]["result"])
        change(altered)
        try:
            verify(spec[index], altered, reports[index]["rpcMethods"])
        except AssertionError:
            rejected.append(name)
        else:
            raise AssertionError("Profile mutation accepted: " + name)
    for name, index, change in [
        ("discovery-omitted", 0, lambda calls: calls.pop(0)),
        ("unadvertised-optional-call", 1, lambda calls: calls.insert(1, "Api.Version")),
        ("cleanup-reordered", 0, lambda calls: calls.insert(0, calls.pop())),
    ]:
        altered = reports[index]["rpcMethods"].copy()
        change(altered)
        try:
            verify(spec[index], reports[index]["result"], altered)
        except AssertionError:
            rejected.append(name)
        else:
            raise AssertionError("Profile trace mutation accepted: " + name)
    assert inputs() == original_inputs and hashes(sorted(staged.iterdir())) == staged_inputs, "Qualification inputs changed"
    report = {"completed": True, "cases": len(reports), "reports": reports, "rejectedMutations": rejected,
              "actualPinnedLoopbackTls": True, "nativeExecuted": False,
              "inputs": original_inputs, "stagedInputs": staged_inputs, "inputsUnchangedThroughRun": True,
              "loadedRuntime": loaded, "runtimeOverrideUsed": runtime_directory is not None,
              "requestedRuntimeDirectory": str(runtime_directory) if runtime_directory else None,
              "assemblySha256": hashlib.sha256(assembly.read_bytes()).hexdigest(),
              "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    (root / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"PASS {len(reports)} independent advertised-profile cases, {len(rejected)} altered-evidence controls; pinned loopback TLS only.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, required=True)
    parser.add_argument("--tls", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime-directory", type=Path, help="Use exact Core/Graph DLLs from this production Host/package directory in an isolated probe copy")
    args = parser.parse_args()
    qualify(args.assembly.resolve(strict=True), args.tls.resolve(strict=True), args.output.resolve(),
            args.runtime_directory.resolve(strict=True) if args.runtime_directory else None)
