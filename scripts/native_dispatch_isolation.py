"""Narrow, explicitly requested separation from exact reviewed prior-boot failures.

This never repairs historical cleanup. A corroborated later OS boot removes the
old lifetime from the *current* machine; a fresh owned job/copy is still required.
No arbitrary unresolved ticket, current PID absence or supplied boot JSON admits
execution. This policy is not a security boundary against a malicious local user.
"""
import ctypes
from datetime import datetime, timezone
import math
import os
from pathlib import Path
import subprocess
import time
import stat

import native_dispatch_guard as guard

# Exact artifacts independently reviewed in native-isolated-dispatch-20261021.md.
# Deliberately not a generic age-based exception for unknown legacy attempts.
LEGACY_INTENT_SHA256 = "5c18e026d371bc6d3034693abbc345a504ccbee6d6fa2d33adedbe899454b990"
LEGACY_REPORT_SHA256 = "1c8c8c7be74e012f622e8b5b0201ae9efb15441fd1cdbd080ee3567f5667c4c0"
SAFETY_MARGIN_SECONDS = 60


def epoch(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    guard.require(parsed.tzinfo is not None, "Boot timestamp must have an explicit offset")
    return parsed.timestamp()


def observe_boot():
    """Read current local CIM, event-log and kernel uptime evidence; no mutation."""
    guard.require(os.name == "nt", "Boot isolation requires the local Windows machine")
    script = r'''
$ErrorActionPreference='Stop'
$os=Get-CimInstance Win32_OperatingSystem
$event=Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-General'; Id=12} -MaxEvents 1
[xml]$xml=$event.ToXml()
$start=($xml.Event.EventData.Data | Where-Object {$_.Name -eq 'StartTime'}).'#text'
$boot=Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Boot'; Id=27} -MaxEvents 1
[xml]$bootXml=$boot.ToXml()
$bootTypes=@($bootXml.Event.EventData.Data | Where-Object {$_.Name -eq 'BootType'})
if ($bootTypes.Count -ne 1 -or $bootTypes[0].'#text' -cnotmatch '^[0-9]+$') { throw 'Missing or ambiguous exact BootType' }
$bootType=$bootTypes[0].'#text'
[ordered]@{cimBootUtc=$os.LastBootUpTime.ToUniversalTime().ToString('o'); computer=$os.CSName; eventBootUtc=$start; eventRecordId=$event.RecordId; eventComputer=$event.MachineName; eventProvider=$event.ProviderName; eventId=$event.Id; bootType=[int]$bootType; bootTypeRecordId=$boot.RecordId; bootTypeComputer=$boot.MachineName; bootTypeAtUtc=$boot.TimeCreated.ToUniversalTime().ToString('o')} | ConvertTo-Json -Compress
'''
    import json
    before = time.time()
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                            capture_output=True, text=True, check=True, timeout=30)
    observation = json.loads(result.stdout)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetTickCount64.argtypes = []
    kernel.GetTickCount64.restype = ctypes.c_ulonglong
    observation.update(observedEpoch=time.time(), observedStartEpoch=before, uptimeMilliseconds=kernel.GetTickCount64())
    validate_boot(observation)
    return observation


def validate_boot(value):
    guard.require(isinstance(value, dict), "Missing local boot observation")
    observed, started, uptime = (value.get(k) for k in ("observedEpoch", "observedStartEpoch", "uptimeMilliseconds"))
    guard.require(all(type(v) in (int, float) and math.isfinite(v) for v in (observed, started, uptime))
                  and 0 <= observed - started <= 30 and uptime > 0, "Invalid boot observation clock bounds")
    guard.require(value.get("eventProvider") == "Microsoft-Windows-Kernel-General" and value.get("eventId") == 12
                  and type(value.get("eventRecordId")) is int and value["eventRecordId"] > 0,
                  "Exact local OS boot event required")
    machine, event_machine = value.get("computer"), value.get("eventComputer")
    guard.require(isinstance(machine, str) and machine and isinstance(event_machine, str)
                  and machine.casefold().split(".")[0] == event_machine.casefold().split(".")[0],
                  "Boot evidence machine identities disagree")
    guard.require(type(value.get("bootType")) is int and value["bootType"] == 0
                  and type(value.get("bootTypeRecordId")) is int and value["bootTypeRecordId"] > value["eventRecordId"]
                  and value.get("bootTypeComputer") == event_machine, "Matching full OS boot event required; hybrid/resume refuses")
    try:
        cim, event, boot_type_time = epoch(value["cimBootUtc"]), epoch(value["eventBootUtc"]), epoch(value["bootTypeAtUtc"])
    except (KeyError, TypeError, AttributeError, ValueError) as error:
        raise ValueError("Missing or invalid current boot timestamps") from error
    estimate = observed - uptime / 1000
    guard.require(abs(cim - event) <= 2 and abs(cim - estimate) <= 10 and 0 <= boot_type_time - cim <= 10 and cim < started,
                  "Ambiguous boot clock: CIM, event and kernel uptime must agree")
    return cim


def canonical(path):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        guard.require(not item.is_symlink() and not (hasattr(item, "is_junction") and item.is_junction()),
                      "Isolation path has reparse ancestry")
        if item.exists():
            guard.require(not (getattr(item.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT),
                          "Isolation path has reparse ancestry")
    return path.resolve()


def overlaps(left, right):
    left, right = canonical(left), canonical(right)
    return left == right or left in right.parents or right in left.parents


def local_volume_type(anchor):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetDriveTypeW.argtypes = [ctypes.c_wchar_p]
    kernel.GetDriveTypeW.restype = ctypes.c_uint
    return kernel.GetDriveTypeW(anchor)


def require_local_workspace(path):
    if os.name == "nt":
        # Reject UNC before touching ancestors; mapped network drives also refuse.
        guard.require(len(path.drive) == 2 and path.drive[1] == ":" and local_volume_type(path.anchor) == 3,
                      "Isolated native workspace must be on a local fixed volume; network paths refuse")


class RebootIsolation:
    """One new disposable workspace; only the fixture job coordinator uses this."""
    def __init__(self, workspace):
        require_local_workspace(Path(workspace).absolute())
        self.workspace = canonical(workspace)
        self.observation = None

    def validate_workspace(self):
        guard.require(not self.workspace.exists(), "Isolated native workspace must be new")
        canonical(self.workspace)
        require_local_workspace(self.workspace)

    def admit(self, intent_path):
        self.validate_workspace()
        intent_ref = guard.reference(intent_path)
        from native_startup_isolation import STARTUP_INTENT_SHA256, startup_evidence
        if intent_ref["sha256"] == STARTUP_INTENT_SHA256:
            source, references, births = startup_evidence(intent_path, self.workspace)
            return self.finish(intent_ref, source, references, births)
        guard.require(intent_ref["sha256"] == LEGACY_INTENT_SHA256,
                      "Unresolved native dispatch is not the exact reviewed prior-boot attempt")
        intent = guard.read(intent_path)
        source = intent.get("source", {})
        guard.require(source.get("sha256") == LEGACY_REPORT_SHA256 and source == guard.reference(source.get("path", "")),
                      "Historical reconciliation changed or is missing")
        report = guard.read(source["path"])
        guard.require(report.get("failedCoordinatorReleased") is True and report.get("originalAttemptPassed") is False
                      and report.get("allCapturedOriginalTargetsExitedBeforeRelease") is True
                      and report.get("completeOwnedTreeExitVerified") is False and report.get("nativeQualified") is False
                      and report.get("uncapturedObservedProcesses") == [{"pid": 38560,
                          "name": "Siemens.Automation.Diagnostics.Report.exe", "originalHandleCaptured": False,
                          "originalLifetimeTerminalVerified": False}], "Historical failure boundary changed")
        references = [intent_ref, source, *report["sources"]]
        for ref in references:
            canonical(ref["path"])
            guard.require(ref == guard.reference(ref["path"]), "Historical source evidence changed or is missing")
            guard.read(ref["path"])
        old_root = canonical(Path(source["path"]).parent)
        guard.require(not overlaps(self.workspace, old_root), "Isolated workspace overlaps historical artifacts")
        native = next(ref for ref in references if Path(ref["path"]).name == "result.json")
        native = guard.read(native["path"])
        guard.require(canonical(native["projectPath"]).is_relative_to(old_root),
                      "Historical mutable project must be contained in its original artifact root")
        configured_archive = native["archive"]
        canonical(configured_archive["path"])
        guard.require(configured_archive == guard.reference(configured_archive["path"])
                      and canonical(configured_archive["path"]).is_relative_to(old_root), "Configured historical archive changed")
        references.append(configured_archive)
        release = next(ref for ref in references if Path(ref["path"]).name == "failed-holder-release.json")
        hold = next(ref for ref in references if Path(ref["path"]).name == "fixture-tree-hold.json")
        release, hold = guard.read(release["path"]), guard.read(hold["path"])
        guard.require(release.get("completed") is True and release.get("originalAttemptPassed") is False
                      and release.get("uncapturedLifetimeGap") is True and release.get("coordinatorForcedExit") is True,
                      "Historical coordinator release is not the reviewed failed release")
        births = [release["coordinator"]["created"], hold["anchor"]["created"]]
        return self.finish(intent_ref, source, references, births)

    def finish(self, intent_ref, source, references, births):
        boot = observe_boot()
        boot_epoch = validate_boot(boot)
        latest = max(max(Path(ref["path"]).stat().st_mtime, Path(ref["path"]).stat().st_ctime) for ref in references)
        guard.require(all(type(b) in (int, float) and math.isfinite(b) and b > 0 for b in births)
                      and max(latest, *births) + SAFETY_MARGIN_SECONDS < boot_epoch,
                      "Historical evidence is not strictly before the current boot")
        if self.observation is not None:
            guard.require(boot["eventRecordId"] == self.observation["eventRecordId"]
                          and boot["eventComputer"] == self.observation["eventComputer"]
                          and boot["eventBootUtc"] == self.observation["eventBootUtc"],
                          "Current boot changed within the isolated window")
        self.observation = boot
        return {"intent": intent_ref, "historicalReconciliation": source, "bootObservation": boot,
                "disposition": "isolated-new-attempt-after-corroborated-os-boot",
                "historicalCleanupVerified": False, "originalAttemptPromoted": False,
                "legacyTicketModified": False, "workspace": str(self.workspace),
                "requiresAtomicOwnedJob": True, "automaticReplay": False}
