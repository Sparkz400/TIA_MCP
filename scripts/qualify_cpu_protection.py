"""Qualify the CPU protection level natively: hardware_cpu_parameters with protectionLevel and the full-access password
from the Host credential route, in one owned scratch CPU.

Every password is generated here and enters only the Host credential CLI's stdin. Typed refusals come first: at prepare
(no level, a downgrade, no credential, a plaintext field) and at apply before admission (a credential bound to another
CPU, a missing or retired one), each leaving the protection unchanged. Then a password outside TIA's policy, the level
set with its access-level mode, a level-only change, its checkpoint restore and the original mode back. At the end every
file of the run, every tool response and the Host's log are scanned for the generated passwords.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import secrets
import uuid

from library_native_harness import Harness, require
from qualify_block_protection import Vault, leaks, secret_scan

KIND, VIA = "hardware_cpu_parameters", "EnabledWithAccessControlViaAccessLevel"


def binding(plc, cpu):
    """The exact public binding the worker derives for the CPU full-access password (protected-credentials.md)."""
    return {"resource": {"provider": "siemensTiaPortalV21", "authority": plc, "ownerId": cpu,
                         "resourceKind": "cpuProtection", "resourceId": "fullAccess"},
            "purpose": "cpuProtection", "subjectId": "fullAccessPassword"}


def protection(h, cpu):
    """The CPU's access-control mode and protection level as access_inspect reads them, independent of the change route."""
    rows = [d for d in h.tool("access_inspect", {"hardwarePath": cpu, "maxItems": 500})["devices"] if d["objectPath"] == cpu]
    require(len(rows) == 1, "access_inspect does not list the CPU once: %s" % rows)
    return {"configuration": rows[0]["accessControl"]["configuration"], "level": rows[0]["accessLevel"]}


def body(h):
    plc = h.owned_scratch("cpu protection")
    cpu = plc.rsplit("/plc/", 1)[0]
    h.report["fixture"] = {"plcPath": plc, "cpuPath": cpu}
    original = h.stage("read the original protection", lambda: protection(h, cpu))
    require("ViaAccessLevel" not in str(original["configuration"]), "The scratch CPU already uses an access-level mode: %s" % original)
    vault = Vault(h)
    full = vault.create("fullAccess", binding(plc, cpu))
    other = vault.create("otherCpu", binding(plc, cpu + "Other"))
    h.report["publicBinding"] = binding(plc, cpu)

    def payload(reference=None, level=None, mode=None, **extra):
        result = {"hardwarePath": cpu, "attributes": {} if mode is None else {"PlcAccessControlConfiguration": mode}, **extra}
        if level is not None:
            result["protectionLevel"] = level
        if reference is not None:
            result.update(credential=reference, vaultName=vault.name)
        return result

    def prepare(request):
        prepared = h.prepare(KIND, request)
        slots = prepared["preview"]["credentials"]
        require(len(slots) == 1 and slots[0]["slot"] == "credential" and slots[0]["vaultName"] == vault.name and slots[0]["binding"] == binding(plc, cpu)
                and slots[0]["reference"] == request["credential"], "Preview must carry only the public slot and binding: %s" % slots)
        effect = prepared["preview"]["effects"][0]
        require(effect["protectionLevelAfter"] == request["protectionLevel"], "Preview lacks the protection level: %s" % effect)
        require(not leaks(json.dumps(prepared).encode("utf-8"), vault.passwords), "Preview leaked a secret")
        return prepared

    # Typed refusals at prepare: nothing is prepared, nothing changes.
    h.refuse("refuse an access-level mode without a protection level", KIND, payload(mode=VIA), "InvalidRequest")
    h.refuse("refuse full access", KIND, payload(full, "FullAccess", VIA), "ProtectionDowngradeRefused")
    h.refuse("refuse full access including fail-safe", KIND, payload(full, "FullAccessIncludingFailsafe", VIA), "ProtectionDowngradeRefused")
    h.refuse("refuse a level without its credential", KIND, payload(level="ReadAccess", mode=VIA), "CredentialInputInvalid")
    h.refuse("refuse a level under the original mode", KIND, payload(full, "ReadAccess"), "InvalidRequest")
    h.refuse("refuse a plaintext password field", KIND, payload(full, "ReadAccess", VIA, password="plaintext-is-refused"), "CredentialInputInvalid")
    h.refuse("refuse disabling access control", KIND, payload(mode="Disabled"), "ProtectionDowngradeRefused")
    require(protection(h, cpu) == original, "A refused preparation changed the protection")

    # Refusals at apply, before admission and the checkpoint.
    def refused(label, reference, code):
        def check():
            prepared = prepare(payload(reference, "ReadAccess", VIA))
            error = h.call_error("change_apply", h.apply_args(prepared))
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            status = h.tool("change_status", {"changeId": prepared["changeId"]})
            require(status["state"] == "prepared" and not status["stages"], "A refused credential must leave the change prepared, without a checkpoint")
            require(protection(h, cpu) == original and not h.unresolved_admissions(), "Refusal changed the protection or left an admission")
            return {"error": error, "changeId": prepared["changeId"]}
        return h.stage(label, check)
    mismatch = refused("refuse a credential bound to another CPU", other, "CredentialContextMismatch")
    require(mismatch["error"]["details"]["expectedBinding"] == binding(plc, cpu), "Mismatch must name the expected public binding")
    refused("refuse a missing credential", {**full, "credentialId": uuid.uuid4().hex}, "CredentialUnavailable")
    retired = vault.create("retired", binding(plc, cpu))
    vault.retire("retired")
    refused("refuse a retired credential", retired, "CredentialRetired")

    def weak():
        password = "cp" + secrets.token_hex(8)  # Only lower case and digits: outside TIA's block password policy.
        vault.passwords.append(password)
        prepared = prepare(payload(vault.create("weak", binding(plc, cpu), secret=password.encode("utf-8")), "ReadAccess", VIA))
        result = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        require(not leaks(json.dumps(result).encode("utf-8"), vault.passwords), "The apply response leaked a secret")
        if result.get("isError"):
            error = (result.get("structuredContent") or {}).get("error") or {}
            require(error.get("code") == "CredentialPolicyViolation", "Expected CredentialPolicyViolation: %s" % error)
            outcome = {"outcome": "refused", "error": {"code": error["code"], "message": error["message"]}, "state": error["details"]["state"], "stages": error["details"]["stages"]}
        else:
            outcome = {"outcome": "applied", "protection": protection(h, cpu)}
        require(not h.unresolved_admissions(), "The weak-password apply left an admission")
        restored = h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]})
        require(protection(h, cpu) == original, "The restored checkpoint must hold the original protection")
        outcome["restore"] = {"state": restored["state"], "stages": restored["stages"]}
        return outcome
    policy = h.stage("a password outside TIA's block policy, then its checkpoint restore", weak)

    def applied(prepared, expected):
        result = h.apply_prepared(KIND, prepared)
        require(all(stage in result["stages"] for stage in ("recovery_archive_created", "mutation_applied", "changed_project_saved")), "Missing checkpoint/mutation stages")
        readback = [r for r in result["readbacks"] if r["role"] == "cpu-protection-readback"]
        require(len(readback) == 1 and readback[0]["protectionLevel"] == expected["level"], "Unexpected protection readback: %s" % result["readbacks"])
        observed = protection(h, cpu)
        require(observed == expected, "access_inspect reads %s, expected %s" % (observed, expected))
        require(not h.unresolved_admissions(), "Applied change left an unresolved admission")
        return {"changeId": prepared["changeId"], "stages": result["stages"], "readbacks": result["readbacks"], "protection": observed}

    # Set: the access-level mode with read access and the full-access password, compiled; then a level-only change.
    entered = applied(prepare(payload(full, "ReadAccess", VIA)), {"configuration": VIA, "level": "ReadAccess"})
    level_prepared = prepare(payload(full, "HMIAccess"))
    level = applied(level_prepared, {"configuration": VIA, "level": "HMIAccess"})

    def restore():
        recovered = h.tool("change_restore", {"changeId": level_prepared["changeId"], "previewHash": level_prepared["previewHash"]})
        require(recovered["state"] == "restored", "Checkpoint recovery was not verified: %s" % recovered["stages"])
        require(protection(h, cpu) == {"configuration": VIA, "level": "ReadAccess"}, "The restored checkpoint must hold read access")
        return {"stages": recovered["stages"], "protection": protection(h, cpu)}
    restored = h.stage("restore the level change from its checkpoint", restore)

    # Change back: the original mode needs no password; a level is refused outside the access-level modes.
    h.refuse("refuse a level while leaving the access-level mode", KIND, payload(full, "NoAccess", original["configuration"]), "InvalidRequest")
    back = h.apply(KIND, payload(mode=original["configuration"]))
    after = h.stage("the original mode is back", lambda: protection(h, cpu))
    require(after["configuration"] == original["configuration"], "The original access-control mode did not return: %s" % after)

    for label in [label for label in vault.labels if label != "retired"]:
        vault.retire(label)
    h.report["secretScan"] = h.stage("scan every run file, response and Host log for the passwords", lambda: secret_scan(h, vault))
    h.report["observed"] = {"original": original, "policy": policy, "entered": entered, "levelChange": level, "restored": restored,
                            "changeBack": {"changeId": back.get("changeId"), "stages": back["stages"], "protection": after}}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "cp_", "V21 CPU protection level with the full-access password through the Host credential route", [
        "One owned scratch CPU 1511-1 PN in TIA Portal V21, from its default UMAC mode to EnabledWithAccessControlViaAccessLevel; the central-user access-level modes, other CPU families and F-CPUs are not exercised.",
        "Only the full-access password is set; read-access and HMI-access passwords, password removal and FullAccessIncludingFailsafe are out of scope.",
        "V21 cannot read a CPU password back: device compilation and the access_inspect level are the evidence; a download and an online login are not exercised.",
        "Generated ASCII passwords from Python's secrets module; the vault lives in the current Windows profile."], []).run(body)
