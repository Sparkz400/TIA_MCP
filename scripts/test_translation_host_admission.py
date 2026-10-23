"""Actual stdio translation admission boundaries with no installed registry or native workers."""
import argparse
import hashlib
import json
import os
import pathlib
import subprocess

from smoke_mcp import Client, ROOT


PRODUCTION = {"translation_native_" + suffix for suffix in
              ("capture", "prepare", "apply", "status", "recover", "reconcile")}
QUALIFICATION = {"qualification_translation_" + suffix for suffix in (
    "fixture", "categories", "capture", "prepare", "apply", "recover", "reconcile", "status",
    "reconcile_before_checkpoint", "resume_closed_fixture",
)}
OPERATION_ID = "a" * 32
PLAN_SHA256 = "b" * 64


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class RecordedClient(Client):
    """Keep exact stdout JSON-RPC lines while using the shared transport client."""
    def __init__(self, executable, timeout, extra_arguments):
        self.raw_stdout = []
        super().__init__(executable, timeout, extra_arguments)

    def _read_output(self):
        for line in self.process.stdout:
            self.raw_stdout.append(line)
            self.lines.put(line)
        self.lines.put(None)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    host = args.host.resolve()
    output = args.output.resolve()
    require(host.is_file(), "Host executable must exist")
    require(not output.exists(), "Use a new isolated output directory")
    output.mkdir(parents=True)
    registry = host.parent / "translation-admission/registry.json"
    missing = output / "intentionally-absent-native-worker.exe"
    report = {"schemaVersion": 1, "completed": False, "nativeExecuted": False,
              "admissionRegistry": str(registry), "registryCreated": False,
              "checks": [], "hosts": [], "modeRejections": []}
    client = None
    current = None
    binary_paths = sorted(path for path in host.parent.iterdir()
                          if path.is_file() and (path.suffix in {".exe", ".dll"} or
                                                path.name.endswith((".deps.json", ".runtimeconfig.json", ".config"))))
    binaries = [{"name": path.name, "sha256": digest(path)} for path in binary_paths]
    report["binaries"] = binaries
    report["hostSha256"] = digest(host)
    report["scriptSha256"] = digest(pathlib.Path(__file__).resolve())

    def check(name, condition):
        require(condition, name)
        report["checks"].append(name)

    def absent_registry():
        # A dangling link is still an unexpected installed registry entry.
        require(not os.path.lexists(registry), "Admission registry must remain absent; this harness never edits installation admission")
        require(not os.path.lexists(missing), "Native worker path must remain absent")

    def save_report():
        report["checkCount"] = len(report["checks"])
        (output / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def request(method, parameters=None):
        absent_registry()
        entry = {"request": {"method": method, "params": parameters or {}}}
        current["requests"].append(entry)
        try:
            value = client.request(method, parameters)
            entry["response"] = value
            return value
        except Exception as error:
            entry["transportError"] = str(error)
            raise
        finally:
            (output / (current["name"] + "-requests.json")).write_text(
                json.dumps(current["requests"], indent=2), encoding="utf-8")

    def start(name, extra=()):
        nonlocal client, current
        absent_registry()
        artifacts = output / (name + "-artifacts")
        arguments = ["--worker", str(missing), "--sim-worker", str(missing), "--artifacts", str(artifacts), *extra]
        current = {"name": name, "arguments": arguments, "artifacts": str(artifacts), "requests": []}
        report["hosts"].append(current)
        client = RecordedClient(host, 30, arguments)
        request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                               "clientInfo": {"name": "translation-admission-boundary", "version": "1"}})
        client.notify("notifications/initialized")
        return {item["name"]: item for item in request("tools/list")["tools"]}

    def stop():
        nonlocal client, current
        if client is None:
            return
        owned = client
        client = None  # Do not close this same process twice after a failed assertion.
        receipt = owned.close()
        current["exit"] = receipt
        current["stderr"] = owned.errors
        (output / (current["name"] + "-stdout.jsonl")).write_text("".join(owned.raw_stdout), encoding="utf-8")
        (output / (current["name"] + "-stderr.log")).write_text("\n".join(owned.errors) + "\n", encoding="utf-8")
        (output / (current["name"] + "-exit.json")).write_text(json.dumps(receipt, indent=2), encoding="utf-8")
        save_report()
        check(current["name"] + " Host exits cleanly without forced stop", receipt["hostExitCode"] == 0 and
              not receipt["forcedHostStop"] and receipt["stderrEofObserved"])
        check(current["name"] + " has zero native worker exits", receipt["workerExits"] == [])
        absent_registry()

    def reject(name, arguments, code):
        allowed = PRODUCTION if current["name"] == "production" else {"qualification_translation_apply"}
        require(name in allowed, "Harness attempted an unauthorized tool call: " + name)
        response = request("tools/call", {"name": name, "arguments": arguments})
        error = response.get("structuredContent", {}).get("error", {})
        check(name + " refuses with " + code, response.get("isError") is True and error.get("code") == code)
        require(isinstance(error.get("message"), str) and error["message"], "Refusal must include a message")
        # Both MCP representations must retain the specific refusal, without parsing stderr.
        text = "\n".join(item["text"] for item in response.get("content", []) if item.get("type") == "text")
        require(json.loads(text).get("error", {}).get("code") == code, "Text and structured refusal codes differ")
        absent_registry()

    def no_operations(name):
        root = output / (name + "-artifacts")
        operations = root / "engineering-operations"
        check(name + " creates no native operation records", not operations.exists() or
              not any(path.is_dir() or path.suffix == ".json" for path in operations.iterdir()))
        check(name + " creates no qualification selectors or failure-injection records", not any(
            (root / folder).exists() and any((root / folder).iterdir())
            for folder in ("translation-qualification", "translation-native")))

    def rejected_mode(name, extra, messages):
        absent_registry()
        artifacts = output / (name + "-artifacts")
        command = [str(host), "--timeout-seconds", "30", "--worker", str(missing), "--sim-worker", str(missing),
                   "--artifacts", str(artifacts), "--translation-qualification", *extra]
        result = subprocess.run(command, cwd=ROOT, input="", text=True, encoding="utf-8",
                                capture_output=True, timeout=20)
        receipt = {"name": name, "command": command, "hostExitCode": result.returncode,
                   "forcedHostStop": False, "stdout": result.stdout, "stderr": result.stderr,
                   "expectedDiagnosticAlternatives": messages}
        report["modeRejections"].append(receipt)
        (output / (name + "-exit.json")).write_text(json.dumps(receipt, indent=2), encoding="utf-8")
        check(name + " rejects before starting MCP", result.returncode == 1 and not result.stdout.strip())
        check(name + " reports explicit mode rejection", any(message in result.stderr for message in messages))
        check(name + " creates no artifact directory", not artifacts.exists())
        absent_registry()

    try:
        absent_registry()
        check("installation registry absent before starting Host", not os.path.lexists(registry))
        tools = start("production")
        check("all six production native translation tools registered", PRODUCTION <= tools.keys())
        check("production excludes every translation qualification tool", not any(
            name.startswith("qualification_translation_") for name in tools))
        check("production native tools declare closed-world operations", all(
            tools[name]["annotations"]["openWorldHint"] is False for name in PRODUCTION))
        reject("translation_native_capture", {"profileId": "absent-profile"}, "TranslationImportProfileUnqualified")
        reject("translation_native_prepare", {"profileId": "absent-profile", "candidateRef": "absent",
               "candidateReceiptSha256": "0" * 64, "idempotencyKey": "boundary"}, "TranslationImportProfileUnqualified")
        operation = {"profileId": "absent-profile", "operationId": OPERATION_ID, "planSha256": PLAN_SHA256, "revision": 0}
        reject("translation_native_apply", operation, "TranslationImportProfileUnqualified")
        reject("translation_native_recover", {**operation, "newProjectPath": str(output / "never-created-recovery")},
               "TranslationImportProfileUnqualified")
        reject("translation_native_reconcile", operation, "TranslationImportProfileUnqualified")
        reject("translation_native_status", {"operationId": OPERATION_ID}, "OperationNotFound")
        check("missing admission never reserves recovery destination", not (output / "never-created-recovery").exists())
        no_operations("production")
        stop()

        qualified = start("qualification", ["--translation-qualification"])
        check("same executable retains all six production tools in qualification mode", PRODUCTION <= qualified.keys())
        check("explicit mode exposes the exact ten translation qualification tools", {
            name for name in qualified if name.startswith("qualification_translation_")} == QUALIFICATION)
        check("qualification registration preserves production schemas", all(qualified[name]["inputSchema"] == tools[name]["inputSchema"]
                                                                         for name in PRODUCTION))
        plan = {"operationId": OPERATION_ID, "planSha256": PLAN_SHA256, "revision": 0}
        for bad in ({"loseReplyAfterStage": "invalid"},
                    {"loseReplyAfterStage": "import", "interruptBeforeDispatch": True},
                    {"loseReplyAfterStage": "save", "driftSourceAfterCheckpoint": True},
                    {"interruptBeforeDispatch": True, "driftSourceAfterCheckpoint": True}):
            reject("qualification_translation_apply", {**plan, **bad}, "TranslationQualificationCaseInvalid")
        no_operations("qualification")
        stop()

        conflict = "Translation qualification requires its own sequential Host."
        rejected_mode("conflict-component", ["--component-qualification"], [conflict])
        rejected_mode("conflict-watch", ["--component-qualification", "--watch-qualification", "none"], [conflict])
        rejected_mode("conflict-testsuite-scope", ["--testsuite-scope-probe"], [conflict])
        # Normal builds do not parse the Test Suite qualification-only option.
        # Builds including that option must instead hit the dedicated conflict gate.
        rejected_mode("conflict-testsuite-qualification", ["--testsuite-qualification"],
                      [conflict, "Missing value for --testsuite-qualification", "Unknown option: --testsuite-qualification"])
        check("same executable/dependencies remain unchanged across all mode checks", binaries == [
            {"name": path.name, "sha256": digest(path)} for path in binary_paths])
        check("installation registry remains absent after all checks", not os.path.lexists(registry))
        check("configured native worker remains nonexistent", not os.path.lexists(missing))
        report["completed"] = True
    except Exception as error:
        report["failure"] = str(error)
    finally:
        try:
            stop()
        except Exception as error:
            report["completed"] = False
            report["shutdownFailure"] = str(error)
        if os.path.lexists(registry):
            report["completed"] = False
            report["failure"] = "Installed admission registry appeared during the boundary test; it was not modified by this harness"
        save_report()
    print(("PASS" if report["completed"] else "FAIL") + " translation actual Host admission: " +
          str(report["checkCount"]) + " checks; " + str(output / "verification.json"))
    if not report["completed"]:
        print(report.get("failure", report.get("shutdownFailure", "Unknown failure")))
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
