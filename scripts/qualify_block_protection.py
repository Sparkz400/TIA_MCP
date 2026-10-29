"""Qualify block know-how and write protection natively through the Host credential route in one owned scratch CPU.

Every password is generated here and enters only the Host credential CLI's stdin. Each change carries a credential
reference; the Host resolves it from an isolated named vault and seals the secret to that one admitted apply.
Refusals for a mismatched, missing, retired, non-UTF-8 or busy credential must happen before admission and leave
the block unchanged. Two checkpoint restores show what a checkpoint holds: protection state, never a secret. At the
end every file of the run, every tool response and the Host's log are scanned for the generated passwords.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
from pathlib import Path
import secrets
import subprocess
import uuid

from library_native_harness import Harness, require

FC, FB = "TiaMcp_IsEnabled", "TiaMcp_EnableGate"


def binding(plc, block_path, kind):
    """The exact public binding the worker derives for a block protection credential (protected-credentials.md)."""
    return {"resource": {"provider": "siemensTiaPortalV21", "authority": plc, "ownerId": block_path,
                         "resourceKind": "blockProtection", "resourceId": kind},
            "purpose": "blockProtection", "subjectId": "blockPassword"}


def leaks(data, passwords):
    """True when any password appears as UTF-8, UTF-16LE or base64 of its UTF-8 bytes."""
    return any(form in data for p in passwords for form in
               (p.encode("utf-8"), p.encode("utf-16-le"), base64.b64encode(p.encode("utf-8"))))


class Vault:
    """An isolated named vault driven only through the Host credential CLI."""

    def __init__(self, h):
        self.h, self.name, self.labels, self.passwords = h, uuid.uuid4().hex, [], []
        self.folder = h.directory / "credentials"
        self.folder.mkdir()
        h.report["vaultName"] = self.name

    def cli(self, *arguments, secret=None):
        command = [str(self.h.args.host.resolve()), "--credential-store", *arguments, "--vault-name", self.name] + (["--stdin"] if secret is not None else [])
        done = subprocess.run(command, input=secret, capture_output=True, timeout=120)
        require(done.returncode == 0, "credential-store %s refused: %s" % (arguments[0], done.stderr.decode("utf-8", "replace")))
        return json.loads(done.stdout)

    def create(self, label, public_binding, secret=None):
        """Store a generated password (or exact bytes) under a public binding; returns the public reference."""
        if secret is None:
            # TIA V21's policy: 8 to 120 characters with a digit, a special character, upper and lower case.
            password = "Bp7!" + secrets.token_urlsafe(18)
            self.passwords.append(password)
            secret = password.encode("utf-8")
        (self.folder / (label + ".binding.json")).write_text(json.dumps(public_binding), encoding="utf-8")
        status = self.cli("create", "--binding", str(self.folder / (label + ".binding.json")), secret=secret)
        require(status["secretReturned"] is False and status["state"] == "active", "Credential creation returned more than a public reference")
        (self.folder / (label + ".reference.json")).write_text(json.dumps(status["reference"]), encoding="utf-8")
        self.labels.append(label)
        return status["reference"]

    def retire(self, label):
        status = self.cli("retire", "--binding", str(self.folder / (label + ".binding.json")), "--reference", str(self.folder / (label + ".reference.json")))
        require(status["state"] == "retired", "Retirement did not take effect")
        return status


def body(h):
    plc = h.owned_scratch("block protection")
    blocks = {b["name"]: b for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"]}
    require(FC in blocks and FB in blocks and not blocks[FC]["isKnowHowProtected"], "Scratch fixture blocks are missing or protected")
    fc, fb = blocks[FC]["path"], blocks[FB]["path"]
    vault = Vault(h)
    know = vault.create("knowHow", binding(plc, fc, "knowHow"))
    wrong = vault.create("knowHowWrongSecret", binding(plc, fc, "knowHow"))
    write_1 = vault.create("write1", binding(plc, fb, "write"))
    write_2 = vault.create("write2", binding(plc, fb, "write"))
    h.report["publicBindings"] = {"knowHow": binding(plc, fc, "knowHow"), "write": binding(plc, fb, "write")}

    def protected(name):
        return next(b for b in h.tool("project_inventory", {"maxItems": 1000})["blocks"] if b["name"] == name)["isKnowHowProtected"]

    def prepare(kind, block, reference, **extra):
        prepared = h.prepare(kind, {"blockName": block, "credential": reference, "vaultName": vault.name, **extra})
        slots = prepared["preview"]["credentials"]
        require(all(slot["vaultName"] == vault.name and "binding" in slot for slot in slots) and slots[0]["reference"] == reference,
                "Preview must carry only public slots: %s" % slots)
        require(not leaks(json.dumps(prepared).encode("utf-8"), vault.passwords), "Preview leaked a secret")
        return prepared

    def applied(kind, prepared, check):
        result = h.apply_prepared(kind, prepared)
        require(all(stage in result["stages"] for stage in ("recovery_archive_created", "mutation_applied", "changed_project_saved")), "Missing checkpoint/mutation stages")
        require(check(), "%s readback differs" % kind)
        require(not h.unresolved_admissions(), "Applied change left an unresolved admission")
        return {"changeId": prepared["changeId"], "stages": result["stages"], "readbacks": result["readbacks"]}

    def refused(label, kind, block, reference, code, **extra):
        def check():
            before = protected(block)
            prepared = prepare(kind, block, reference, **extra)
            error = h.call_error("change_apply", h.apply_args(prepared))
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            status = h.tool("change_status", {"changeId": prepared["changeId"]})
            require(status["state"] == "prepared" and not status["stages"], "A refused credential must leave the change prepared, without a checkpoint")
            require(protected(block) == before and not h.unresolved_admissions(), "Refusal changed the block or left an admission")
            return {"error": error, "changeId": prepared["changeId"]}
        return h.stage(label, check)

    # Refusals before admission: the reference, vault record, lifecycle or secret cannot serve this exact block.
    mismatch = refused("refuse a credential bound to another block", "block_protect", FC, write_1, "CredentialContextMismatch")
    require(mismatch["error"]["details"]["expectedBinding"] == binding(plc, fc, "knowHow"), "Mismatch must name the expected public binding")
    refused("refuse a missing credential", "block_protect", FC, {**know, "credentialId": uuid.uuid4().hex}, "CredentialUnavailable")
    retired = vault.create("retired", binding(plc, fc, "knowHow"))
    vault.retire("retired")
    refused("refuse a retired credential", "block_protect", FC, retired, "CredentialRetired")
    refused("refuse a non-UTF-8 secret", "block_protect", FC, vault.create("binary", binding(plc, fc, "knowHow"), secret=b"\xff\xfe\x00"), "CredentialFormatInvalid")
    lock = Path(os.environ["LOCALAPPDATA"]) / "TiaMcpProtectedCredentials" / vault.name / ".lock"
    with open(lock, "rb"):  # Any open handle defeats the Host's exclusive vault lock until its bounded wait expires.
        refused("refuse while the vault is busy", "block_protect", FC, know, "CredentialStoreBusy")

    def weak():
        password = "bp" + secrets.token_hex(8)  # Only lower case and digits: violates TIA's password policy.
        vault.passwords.append(password)
        prepared = prepare("block_protect", FC, vault.create("weak", binding(plc, fc, "knowHow"), secret=password.encode("utf-8")))
        error = h.call_error("change_apply", h.apply_args(prepared))
        require(error.get("code") == "CredentialPolicyViolation" and not protected(FC), "Expected CredentialPolicyViolation and an unprotected block: %s" % error)
        require(not h.unresolved_admissions() and not leaks(json.dumps(error).encode("utf-8"), vault.passwords), "Policy refusal left an admission or leaked")
        return {"error": {"code": error["code"], "message": error["message"]}, "state": error["details"]["state"], "stages": error["details"]["stages"]}
    policy = h.stage("TIA refuses a password that violates its policy", weak)

    # Know-how protection with readback, a wrong secret, unprotect, and a checkpoint restore after each direction.
    protect = applied("block_protect", prepare("block_protect", FC, know), lambda: protected(FC))

    def wrong_secret():
        prepared = prepare("block_unprotect", FC, wrong)
        error = h.call_error("change_apply", h.apply_args(prepared))
        require(protected(FC), "A wrong password must leave the block protected")
        h.reconcile_admission("release the wrong-secret attempt") if h.unresolved_admissions() else None
        return {"error": error, "status": h.tool("change_status", {"changeId": prepared["changeId"]})}
    wrong_result = h.stage("unprotect with a correctly bound wrong secret", wrong_secret)
    unprotect_prepared = prepare("block_unprotect", FC, know)
    unprotect = applied("block_unprotect", unprotect_prepared, lambda: not protected(FC))
    h.stage("restore the unprotect checkpoint", lambda: h.tool("change_restore", {"changeId": unprotect_prepared["changeId"], "previewHash": unprotect_prepared["previewHash"]}))
    require(protected(FC), "The restored unprotect checkpoint must hold the protected block")
    again = applied("block_unprotect", prepare("block_unprotect", FC, know), lambda: not protected(FC))
    protect_prepared = prepare("block_protect", FC, know)
    applied("block_protect", protect_prepared, lambda: protected(FC))
    h.stage("restore the protect checkpoint", lambda: h.tool("change_restore", {"changeId": protect_prepared["changeId"], "previewHash": protect_prepared["previewHash"]}))
    require(not protected(FC), "The restored protect checkpoint must hold the unprotected block")

    # Write protection: define, enable, change the password, prove the change, disable, restore.
    def write_state():
        preview = h.tool("change_prepare", {"kind": "block_write_protection_set", "payload": {"blockName": FB, "action": "change", "credential": write_1,
                         "newCredential": write_2, "vaultName": vault.name}})
        return preview["preview"]["protectionBefore"]

    def write(action, reference, check, **extra):
        prepared = prepare("block_write_protection_set", FB, reference, action=action, **extra)
        return applied("block_write_protection_set", prepared, check), prepared
    define, _ = write("define", write_1, lambda: write_state()["writeProtectionDefined"])
    h.report["defineEnables"] = write_state()["writeProtectionEnabled"]
    enable = None
    if not h.report["defineEnables"]:
        enable, _ = write("enable", write_1, lambda: write_state()["writeProtectionEnabled"])
    change, _ = write("change", write_1, lambda: write_state()["writeProtectionEnabled"], newCredential=write_2)

    def old_secret():
        prepared = prepare("block_write_protection_set", FB, write_1, action="disable")
        error = h.call_error("change_apply", h.apply_args(prepared))
        require(write_state()["writeProtectionEnabled"], "The replaced password must not disable write protection")
        h.reconcile_admission("release the replaced-secret attempt") if h.unresolved_admissions() else None
        return {"error": error}
    old_result = h.stage("disable with the replaced password", old_secret)
    disable, disable_prepared = write("disable", write_2, lambda: not write_state()["writeProtectionEnabled"])
    h.stage("restore the disable checkpoint", lambda: h.tool("change_restore", {"changeId": disable_prepared["changeId"], "previewHash": disable_prepared["previewHash"]}))
    restored = write_state()
    require(restored["writeProtectionDefined"] and restored["writeProtectionEnabled"], "The restored disable checkpoint must hold enabled write protection")

    for label in [label for label in vault.labels if label != "retired"]:
        vault.retire(label)

    def scan():
        hits = []
        for path in sorted(p for p in h.directory.rglob("*") if p.is_file()):
            if leaks(path.read_bytes(), vault.passwords):
                hits.append(str(path.relative_to(h.directory)))
        report_hit = leaks(json.dumps(h.report).encode("utf-8"), vault.passwords) or leaks(json.dumps(h.client.tool_calls).encode(), vault.passwords)
        log_hit = leaks("\n".join(h.client.errors).encode("utf-8"), vault.passwords)
        # TIA's own project and archive files are listed apart: what TIA stores for protection is not Host retention.
        native = [p for p in hits if p.endswith((".zap21", ".ap21")) or "/scratch/" in p.replace("\\", "/") or "/recovery/" in p.replace("\\", "/")]
        host = [p for p in hits if p not in native]
        require(not host and not report_hit and not log_hit, "Secret retained outside TIA: files %s, report %s, log %s" % (host, report_hit, log_hit))
        return {"passwords": len(vault.passwords), "filesScanned": sum(1 for p in h.directory.rglob("*") if p.is_file()),
                "hostRetained": host, "tiaProjectFilesContaining": native, "toolResponsesOrReport": report_hit, "hostLog": log_hit}
    h.report["secretScan"] = h.stage("scan every run file, response and Host log for the passwords", scan)
    h.report["observed"] = {"policyViolation": policy, "wrongSecretUnprotect": wrong_result, "replacedSecretDisable": old_result,
                            "applied": [protect, unprotect, again, define, enable, change, disable]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "bp_", "V21 block know-how and write protection through the Host credential route", [
        "One owned scratch CPU 1511-1 PN: one SCL FC (know-how) and one SCL FB (write protection) at PLC-root scope; software units, namespaces, LAD/FBD and safety blocks are not exercised.",
        "Generated ASCII passwords from Python's secrets module; TIA's password policy and non-ASCII passwords are not probed.",
        "The vault lives in the current Windows profile; another user, a roamed profile and packaged-context redirection are not exercised.",
        "Engineering only; a protected block is not downloaded or run."], []).run(body)
