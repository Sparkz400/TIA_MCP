"""Owned PLCSIM backend for block tests; no physical PLC or attachment route."""
import copy
import datetime
import hashlib
import json
import pathlib
import re
import stat
import time
import uuid


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False) + "\n"


def digest(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def redact(value, secrets=()):
    """Scrub secret-bearing keys and the known ticket from error strings too."""
    if isinstance(value, dict):
        return {key: "<redacted>" if any(term in re.sub(r"[^a-z]", "", key.lower())
                for term in ("downloadticket", "password", "cleartext", "plaintext", "secret", "token"))
                else redact(item, secrets) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [redact(item, secrets) for item in value]
    if isinstance(value, str):
        for secret in secrets:
            if secret:
                value = value.replace(secret, "<redacted>")
    return value


class NativeToolError(RuntimeError):
    def __init__(self, tool, result):
        self.tool, self.result = tool, result
        failure = result.get("error") if isinstance(result, dict) else None
        code = failure.get("code", "NativeToolFailure") if isinstance(failure, dict) else "NativeToolFailure"
        super().__init__(tool + ": " + str(code))


def native_error_record(error):
    result = error.result if isinstance(error, NativeToolError) else None
    failure = result.get("error") if isinstance(result, dict) else None
    return failure if isinstance(failure, dict) else {}


class RuntimeCleanupUncertain(RuntimeError):
    """Retain cleanup facts even when successful release cannot settle admission."""
    def __init__(self, message, cleanup):
        self.cleanup = copy.deepcopy(cleanup)
        super().__init__(message)


class NativeAudit:
    """Persist before and after calls, including partial native failures."""
    ALLOWED_TOOLS = frozenset({"tia_start", "tia_create_scratch", "tia_detach", "change_prepare", "change_apply",
                              "change_status", "block_interface", "block_read", "type_read", "sim_create",
                              "simulation_download", "sim_state", "sim_tags", "sim_read", "sim_write", "sim_release"})
    OPTIONAL_TOOLS = frozenset({"simulation_provision_scratch_credential", "simulation_discovery_baseline"})

    def __init__(self, client, records, persist):
        self.client, self.records, self.persist = client, records, persist
        self.secrets = []

    def __call__(self, name, arguments=None):
        if name not in self.ALLOWED_TOOLS | self.OPTIONAL_TOOLS:
            raise ValueError("Tool is outside isolated block-test scope: " + name)
        arguments = arguments or {}
        record = {"tool": name, "startedAtUtc": utc_now(), "arguments": redact(copy.deepcopy(arguments), self.secrets), "status": "started"}
        self.records.append(record)
        self.persist()
        try:
            response = self.client.request("tools/call", {"name": name, "arguments": arguments})
            result = response.get("structuredContent")
            if result is None:
                message = "\n".join(item["text"] for item in response.get("content", []) if item.get("type") == "text")
                try:
                    result = json.loads(message)
                except json.JSONDecodeError:
                    # MCP argument guards may return a plain text error before
                    # the native worker runs. Retain that diagnostic instead of
                    # replacing the actual failure with a JSON decoder error.
                    result = {"error": {"code": "McpToolError" if response.get("isError") else "InvalidMcpToolResult", "message": message}}
                    record["result"] = redact(copy.deepcopy(result), self.secrets)
                    raise NativeToolError(name, record["result"]) from None
            if isinstance(result, dict) and isinstance(result.get("downloadTicket"), str):
                self.secrets.append(result["downloadTicket"])
            record["result"] = redact(copy.deepcopy(result), self.secrets)
            record["status"] = "failed" if response.get("isError") else "passed"
            if response.get("isError"):
                raise NativeToolError(name, record["result"])
            return result
        except Exception as error:
            record["status"] = "failed"
            record["failure"] = {"type": type(error).__name__, "message": redact(str(error), self.secrets)}
            if isinstance(error, NativeToolError):
                raise
            raise NativeToolError(name, {"error": {"code": type(error).__name__, "message": record["failure"]["message"]}}) from None
        finally:
            record["completedAtUtc"] = utc_now()
            self.persist()


def verify_memory_card(download):
    """Verify the exact generated image before starting its owned simulator."""
    revision, entries = download.get("programRevision", ""), download.get("files", [])
    if not re.fullmatch(r"[a-f0-9]{64}", revision) or not entries:
        raise ValueError("Download lacks a concrete memory-card revision and file manifest.")
    requested_root = pathlib.Path(download["memoryCardPath"])
    root_metadata = requested_root.lstat()
    if requested_root.is_symlink() or getattr(root_metadata, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
        raise ValueError("Memory-card root cannot be a link/reparse point.")
    root = requested_root.resolve(strict=True)
    expected = {}
    for entry in entries:
        relative = pathlib.PurePosixPath(entry["path"])
        if relative.is_absolute() or ".." in relative.parts or "\\" in entry["path"] or ":" in entry["path"] or entry["path"] in expected:
            raise ValueError("Invalid or duplicated memory-card manifest path.")
        expected[entry["path"]] = entry
    observed = {}
    for path in root.rglob("*"):
        metadata = path.lstat()
        if path.is_symlink() or getattr(metadata, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise ValueError("Memory-card links/reparse points cannot be qualified.")
        if path.is_file():
            relative = path.relative_to(root).as_posix()
            with path.open("rb") as stream:
                content_hash = hashlib.file_digest(stream, "sha256").hexdigest()
            observed[relative] = {"path": relative, "bytes": metadata.st_size, "sha256": content_hash}
    if observed != expected:
        raise ValueError("Generated memory-card content differs from the download manifest.")
    # Match SimulationDownload's Newtonsoft compact array (field order is part of
    # the native revision format, unlike our sorted-key review JSON format).
    manifest = [{"path": entry["path"], "bytes": entry["bytes"], "sha256": entry["sha256"]} for entry in entries]
    computed_revision = hashlib.sha256(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode("utf-8")).hexdigest()
    if computed_revision != revision:
        raise ValueError("Memory-card revision does not match its hashed file manifest.")
    return {"programRevision": revision, "programRevisionBasis": download.get("programRevisionBasis"),
            "files": entries, "verifiedAtUtc": utc_now(), "verifiedBeforePowerOn": True,
            "runtimeReadbackVerified": False}


def verify_credential_provisioning(provisioned, download, *, trust_owned_empty_cpu_certificate=False):
    """Bind provisioning evidence to the already verified generated image and owned CPU.

    Empty-CPU provisioning can create retain.pms. The original card is restored
    unchanged afterward; runtime-created files never become generated source.
    """
    if provisioned.get("completed") is not True or provisioned.get("credentialProvisioned") is not True:
        raise RuntimeError("Runtime credential provisioning did not complete successfully.")
    if (type(provisioned.get("instanceId")) is not int
            or provisioned["instanceId"] != download.get("instanceId")
            or provisioned.get("instanceName") != download.get("instanceName")):
        raise RuntimeError("Runtime credential provisioning targets a different simulator.")
    revision = download.get("programRevision", "")
    if not re.fullmatch(r"[a-f0-9]{64}", revision) or provisioned.get("originalProgramRevision") != revision:
        raise RuntimeError("Runtime credential provisioning does not match the original generated-card revision.")
    if provisioned.get("operatingState") != "Off" or provisioned.get("protectionDisabled") is not False:
        raise RuntimeError("Runtime credential provisioning did not preserve protection and return the CPU to OFF.")
    if provisioned.get("emptyStartupVerified") is not True or provisioned.get("originalProgramCardRestored") is not True:
        raise RuntimeError("Runtime credential provisioning did not verify empty startup and restoration of the original program card.")
    if not isinstance(provisioned.get("provisioningStrategy"), str) or not provisioned["provisioningStrategy"].strip():
        raise RuntimeError("Runtime credential provisioning lacks its explicit strategy.")
    if not isinstance(provisioned.get("reportPath"), str) or not provisioned["reportPath"].strip():
        raise RuntimeError("Runtime credential provisioning lacks its persisted protection report.")
    report_path = pathlib.Path(provisioned["reportPath"])
    download_report = pathlib.Path(download.get("reportPath", ""))
    card = pathlib.Path(download.get("memoryCardPath", ""))
    if (not all(path.is_absolute() for path in (report_path, download_report, card))
            or report_path.suffix.lower() != ".json"
            or report_path.resolve().parent != download_report.resolve().parent
            or report_path.resolve().parent != card.resolve().parent):
        raise RuntimeError("Runtime credential provisioning report is outside the exact downloaded instance storage.")
    tls = provisioned.get("tlsVerification", {})
    if type(trust_owned_empty_cpu_certificate) is not bool or not isinstance(tls, dict):
        raise RuntimeError("Runtime TLS trust authorization and evidence have invalid types.")
    if not trust_owned_empty_cpu_certificate:
        if provisioned.get("trustOwnedEmptyCpuCertificate") is True or tls.get("trustGranted") is True:
            raise RuntimeError("Runtime TLS trust was granted without explicit owned empty-CPU trust authorization.")
    else:
        pin = provisioned.get("tlsPinnedCertificateSha256", "")
        certificates = tls.get("certificates", [])
        if (provisioned.get("trustOwnedEmptyCpuCertificate") is not True
                or provisioned.get("tlsCertificatePinScope") != "currentProvisioningAttemptOnly"
                or not isinstance(pin, str) or not re.fullmatch(r"[a-f0-9]{64}", pin)
                or tls.get("attemptPinnedCertificateSha256") != pin or tls.get("observedCertificateSha256") != pin
                or any(tls.get(key) is not True for key in ("certificateSha256Matched", "certificateValidityVerified",
                    "ownedTargetRevalidated", "trustGranted", "callerTrustAuthorization", "authorizationRequested"))
                or tls.get("certificateIdentityIndependentlyVerified") is not False
                or tls.get("pinPolicyDecision") not in ("allowOwnedEmptyCpuFirstUse", "allowSameAttemptPinnedCertificate")
                or tls.get("appliedDecision") != "Trusted" or tls.get("certificatesTruncated") is not False
                or not isinstance(certificates, list) or len(certificates) != 1
                or not isinstance(certificates[0], dict) or certificates[0].get("sha256") != pin):
            raise RuntimeError("Runtime TLS trust evidence does not match the authorized owned empty CPU and its attempt pin.")
    return copy.deepcopy(provisioned)


class McpRuntimeBackend:
    capabilities = {"applicationCallControl": True, "scanSynchronized": False}
    limitations = ["The PLC request/acknowledgement program controls DUT calls, not CPU scans.",
                   "Acknowledgements are polled using host wall-clock time; timer observations include API and polling delay.",
                   "Symbol reads are sequential. Output/state snapshots remain stable between acknowledged DUT calls.",
                   "Revision identifies the verified generated memory card; runtime instruction-memory readback is unavailable."]

    def __init__(self, call, program, revision, credential_metadata, *, timeout_ms=10000, poll_ms=20,
                 clock=time.monotonic, sleep=time.sleep, card_verifier=verify_memory_card,
                 provision_runtime_credential=False, trust_owned_empty_cpu_certificate=False):
        if timeout_ms <= 0 or poll_ms <= 0:
            raise ValueError("Runtime timeouts and polling interval must be positive.")
        self.native, self.program, self.revision = call, program, revision
        self.credential_metadata = credential_metadata
        self.provision_runtime_credential = provision_runtime_credential
        if type(trust_owned_empty_cpu_certificate) is not bool:
            raise ValueError("Owned empty-CPU TLS trust authorization must be a boolean.")
        if trust_owned_empty_cpu_certificate and not provision_runtime_credential:
            raise ValueError("Owned empty-CPU TLS trust requires explicit runtime credential provisioning.")
        self.trust_owned_empty_cpu_certificate = trust_owned_empty_cpu_certificate
        self.timeout_ms, self.poll_ms = timeout_ms, poll_ms
        self.clock, self.sleep, self.card_verifier = clock, sleep, card_verifier
        self.instance_id = None
        self.instance = None
        self.creation_attempted = False
        self.identity_verified = False
        self.runtime_outcomes = []
        self.started_once = False
        self.ready = False
        self.request_id = self.call_count = 0
        self.bindings = {instance + "." + path: copy.deepcopy(binding)
                         for instance, bindings in program["bindings"].items() for path, binding in bindings.items()}
        self.controls = copy.deepcopy(program["signals"])
        self.evidence = {"isolation": "fresh_owned_instance", "revision": revision,
                         "trustOwnedEmptyCpuCertificate": self.trust_owned_empty_cpu_certificate,
                         "capabilities": self.capabilities, "limitations": list(self.limitations)}

    def _uncertain(self, kind, tool, error=None, *, details=None, can_reconcile=False):
        native_error = native_error_record(error)
        details = details if isinstance(details, dict) else native_error.get("details", {})
        details = details if isinstance(details, dict) else {}
        row = {"kind": kind, "tool": tool, "observedAtUtc": utc_now(), "reconciled": False,
               "canReconcileByExactUnregistration": can_reconcile,
               "nativeErrorCode": native_error.get("code"),
               "exceptionType": details.get("exceptionType") or (type(error).__name__ if error else None)}
        for key in ("stage", "reportPath", "originalProgramCardRestored", "credentialOpened", "credentialSubmissionAttempted",
                    "credentialNativeCallReturned", "cleanup", "mutationAdmission"):
            if key in details:
                row[key] = copy.deepcopy(details[key])
        self.runtime_outcomes.append(redact(row))

    def _state(self, action, timeout_ms):
        try:
            return self.native("sim_state", {"instanceId": self.instance_id, "action": action, "timeoutMs": timeout_ms})
        except Exception as error:
            native_error = native_error_record(error)
            code = native_error.get("code")
            details = native_error.get("details")
            terminal_timeout = (code == "Timeout" and isinstance(details, dict) and details.get("exceptionType")
                                == "Siemens.Simatic.Simulation.Runtime.SimulationRuntimeException")
            self._uncertain(action + "_outcome_unknown", "sim_state", error, can_reconcile=terminal_timeout)
            raise

    def _guarded_native(self, tool, arguments):
        # Creation, download, provisioning and state transitions have stricter
        # operation-specific reconciliation below. No other failed native call
        # may disappear merely because a later release reports success.
        try:
            return self.native(tool, arguments)
        except Exception as error:
            self._uncertain("native_operation_outcome_unverified", tool, error)
            raise

    def start(self):
        if self.started_once:
            raise RuntimeError("Backend start cannot be retried or reused.")
        self.started_once = True
        if self.provision_runtime_credential:
            # Before registration and before creation is attempted; a refusal leaves nothing owned.
            self.evidence["discoveryBaseline"] = self.native("simulation_discovery_baseline", {})["baseline"]
        self.creation_attempted = True
        self.evidence["creationId"] = uuid.uuid4().hex
        try:
            created = self.native("sim_create", {"cpuType": "CPU1511", "creationId": self.evidence["creationId"]})
            instance = created["instance"]
            if (instance.get("owned") is not True or type(instance.get("id")) is not int or instance["id"] < 0
                    or not isinstance(instance.get("name"), str) or not instance["name"]):
                raise RuntimeError("Simulator creation did not return a verified owned identity.")
            self.instance_id = instance["id"]  # ID zero is valid and must still be cleaned up.
            self.instance = instance
            self.identity_verified = True
        except Exception as error:
            self._uncertain("simulator_creation_identity_unknown", "sim_create", error)
            raise
        if instance.get("operatingState") != "Off":
            raise RuntimeError("A fresh owned OFF simulator is required.")
        self.evidence["instance"] = copy.deepcopy(instance)
        download_arguments = {"downloadTicket": created["downloadTicket"],
                              "credentialMetadataPath": self.credential_metadata}
        try:
            download = self.native("simulation_download", download_arguments)
            if download.get("completed") is not True or download.get("download", {}).get("errors") != 0:
                raise RuntimeError("Memory-card download did not complete successfully.")
            if download.get("instanceId") != self.instance_id or download.get("instanceName") != instance["name"]:
                raise RuntimeError("Downloaded program targets a different simulator.")
            self.evidence["memoryCard"] = self.card_verifier(download)
        except Exception as error:
            self._uncertain("simulation_download_outcome_unverified", "simulation_download", error)
            raise
        if self.provision_runtime_credential:
            provision_arguments = dict(download_arguments)
            if self.trust_owned_empty_cpu_certificate:
                provision_arguments["trustOwnedEmptyCpuCertificate"] = True
            try:
                provisioned = self.native("simulation_provision_scratch_credential", provision_arguments)
            except Exception as error:
                # Worker cleanup does not reconcile the Host's durable mutation admission.
                self._uncertain("credential_provisioning_admission_unreconciled",
                                "simulation_provision_scratch_credential", error)
                raise
            try:
                self.evidence["credentialProvisioning"] = verify_credential_provisioning(provisioned, download,
                    trust_owned_empty_cpu_certificate=self.trust_owned_empty_cpu_certificate)
                self.evidence["restoredMemoryCard"] = self.card_verifier(download)
            except Exception as error:
                self._uncertain("provisioned_original_card_evidence_unverified", "simulation_provision_scratch_credential",
                                error, details=provisioned)
                raise
        self._state("powerOn", 60000)
        running = self._state("run", 30000)
        running_instance = running.get("instance", {})
        if running_instance.get("operatingState") != "Run":
            raise RuntimeError("Fresh simulator did not enter RUN.")
        evidence = running_instance.get("programEvidence", {})
        if evidence.get("programRevision") != download["programRevision"]:
            raise RuntimeError("Running simulator's generation record does not match the verified program.")
        self._resolve_symbols()
        controls, _ = self._capture(self.controls)
        if any(type(controls[name]) is not int or controls[name] != 0 for name in ("requestId", "ackId", "callCount", "errorCode")):
            raise RuntimeError("Fresh program control counters are not at initial state; isolation is unproven.")
        self.ready = True
        self.evidence["initialControls"] = controls
        self.evidence["startedAtUtc"] = utc_now()
        return copy.deepcopy(self.evidence)

    def _resolve_symbols(self):
        # Native API spelling can quote DB/member identifiers; resolve exact unique symbols.
        prefix = self.controls["requestId"]["symbol"].split(".", 1)[0].replace('"', '')
        result = self._guarded_native("sim_tags", {"instanceId": self.instance_id, "filter": prefix, "maxItems": 2000})
        symbols = {}
        for tag in result.get("tags", []):
            symbol = tag["symbol"]
            symbols.setdefault(symbol.replace('"', ''), set()).add(symbol)
        for binding in [*self.bindings.values(), *self.controls.values()]:
            candidates = symbols.get(binding["symbol"].replace('"', ''), set())
            if len(candidates) != 1:
                raise ValueError("Missing or ambiguous simulation symbol: " + binding["symbol"])
            binding["symbol"] = next(iter(candidates))

    def _capture(self, bindings):
        entries = list(bindings.items())
        values, samples = {}, []
        for offset in range(0, len(entries), 64):
            batch = entries[offset:offset + 64]
            signals = [{"symbol": binding["symbol"], "type": binding["type"]} for _, binding in batch]
            result = self._guarded_native("sim_read", {"instanceId": self.instance_id, "signals": signals})
            if result.get("instanceId") != self.instance_id:
                raise RuntimeError("Simulation read identity mismatch.")
            observed = result.get("values", [])
            by_key = {(item["symbol"], item["type"]): item for item in observed}
            expected = {(item["symbol"], item["type"]) for item in signals}
            if len(observed) != len(signals) or set(by_key) != expected or any(item.get("quality") != "good" for item in observed):
                raise RuntimeError("Incomplete, duplicate, mismatched or bad-quality simulation values.")
            for name, binding in batch:
                values[name] = by_key[binding["symbol"], binding["type"]]["value"]
            samples.extend(observed)
        return values, samples

    def _write(self, values, bindings):
        items = list(values.items())
        for offset in range(0, len(items), 64):
            self._guarded_native("sim_write", {"instanceId": self.instance_id, "writes": [
                {"symbol": bindings[name]["symbol"], "type": bindings[name]["type"], "value": value}
                for name, value in items[offset:offset + 64]]})

    def write(self, values):
        if not self.ready:
            raise RuntimeError("Stimuli require an idle, successfully initialized program.")
        for path in values:
            if path not in self.bindings or self.bindings[path].get("writable") is not True:
                raise ValueError("Not a writable DUT input/in-out: " + path)
        try:
            self._write(values, self.bindings)
        except Exception:
            self.ready = False  # A partial write must never be followed by another call.
            raise

    def call(self, target):
        if not self.ready or target not in self.program["instanceIds"]:
            raise RuntimeError("DUT call requires an idle program and a declared instance.")
        if self.request_id >= 0xFFFFFFFF or self.call_count >= 0xFFFFFFFF:
            raise RuntimeError("Control counter exhausted; start a fresh case.")
        self.ready = False
        self._write({"instanceIndex": self.program["instanceIds"][target]}, self.controls)
        self.request_id += 1
        self._write({"requestId": self.request_id}, self.controls)  # Publish only after selector/input writes complete.
        deadline = self.clock() + self.timeout_ms / 1000
        while True:
            controls, _ = self._capture(self.controls)
            if any(type(value) is not int for value in controls.values()):
                raise RuntimeError("Invalid control counter value type.")
            if controls["ackId"] == self.request_id:
                if controls["requestId"] != self.request_id or controls["errorCode"] != 0 or controls["callCount"] != self.call_count + 1:
                    raise RuntimeError("DUT request was rejected or its exact call count cannot be proven.")
                self.call_count += 1
                self.ready = True
                return self.read()
            if self.clock() >= deadline:
                raise TimeoutError("Timed out waiting for the DUT application acknowledgement; no scan synchronization is available.")
            self.sleep(min(self.poll_ms / 1000, max(0, deadline - self.clock())))

    def read(self):
        if not self.ready:
            raise RuntimeError("Cannot capture DUT outputs before acknowledgement.")
        values, samples = self._capture(self.bindings)
        controls, control_samples = self._capture(self.controls)
        if any(type(value) is not int for value in controls.values()):
            self.ready = False
            raise RuntimeError("Invalid control counter value type.")
        if controls["ackId"] != self.request_id or controls["requestId"] != self.request_id or controls["callCount"] != self.call_count or controls["errorCode"] != 0:
            self.ready = False
            raise RuntimeError("Program changed during a supposedly idle output capture.")
        return {"values": values, "quality": "good", "timestampUtc": utc_now(), "monotonicMs": self.clock() * 1000,
                "controls": controls, "nativeSamples": samples + control_samples,
                "instanceId": self.instance_id, "programRevision": self.evidence["memoryCard"]["programRevision"]}

    def close(self):
        self.ready = False
        cleanup = {"completed": False, "instanceCreationAttempted": self.creation_attempted,
                   "instanceIdentityVerified": self.identity_verified}
        if self.instance_id is None:
            if not self.creation_attempted:
                cleanup["instanceCreated"] = False
            elif not self.identity_verified:
                cleanup["instanceCreationOutcome"] = "unknown"
        else:
            try:
                if self.instance.get("owned") is not True:
                    raise RuntimeError("Refusing cleanup deletion because returned simulator ownership was not proven.")
                result = self._guarded_native("sim_release", {"instanceId": self.instance_id, "deleteOwned": True})
                cleanup["release"] = copy.deepcopy(result)
                if (result.get("unregistered") is not True or type(result.get("instanceId")) is not int
                        or result["instanceId"] != self.instance_id or result.get("instanceName") != self.instance["name"]):
                    raise RuntimeError("Owned simulator cleanup did not confirm exact-identity unregistration.")
                self.instance_id = None
                cleanup["instanceUnregistrationVerified"] = True
                for row in self.runtime_outcomes:
                    if row["canReconcileByExactUnregistration"]:
                        row.update(reconciled=True, reconciliation="Exact owned instance unregistration confirmed after terminal native timeout.")
            except Exception as error:
                self._uncertain("owned_simulator_release_unverified", "sim_release", error)
                cleanup["releaseFailure"] = {"type": type(error).__name__, "message": str(error)}
        cleanup["runtimeOutcomes"] = copy.deepcopy(self.runtime_outcomes)
        cleanup["runtimeStateUncertain"] = ((self.creation_attempted and not self.identity_verified)
            or any(row["reconciled"] is not True for row in self.runtime_outcomes))
        cleanup["completed"] = not cleanup["runtimeStateUncertain"]
        if cleanup["runtimeStateUncertain"]:
            cleanup["admission"] = "stopped_due_to_uncertain_runtime"
            raise RuntimeCleanupUncertain("Further cases are stopped because the owned runtime outcome remains uncertain.", cleanup)
        return cleanup
