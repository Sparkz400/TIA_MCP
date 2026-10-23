"""Prepare an owned HMI fixture for prerequisite-only editor repair.

Keeps the shared quarantine/store locks, original helper/main handles and atomic
non-breakaway job until captured handles signal and job accounting drains.
Individual ephemeral handles and brokered descendants are not inferred.
Never automatically terminates or replays a call.
The editor must Save before the exact ui-ready capture command is supplied.
Use --prepare-only for a hash-bound packet without native access or admission.
"""
import argparse
import hashlib
import json
import subprocess
import threading
import time
from pathlib import Path

import psutil
import native_dispatch_guard
from owned_process_job import OwnedProcessJob
from qualify_force_capture import Processes
from qualify_worker_startup_exact import identity
from qualify_translation_category_probe import StoreExclusiveWindow, require, safe_path

PROFILES = {
    'classic-comfort': 'configure-existing-start-screen',
    'unified-comfort': 'configure-encrypted-transfer-password-and-confirmation',
    'unified-pc': 'configure-encrypted-transfer-password-and-confirmation',
}

class FixtureProcesses(Processes):
    """Main editor identity is separate from its compiler/background descendants."""
    maximum_descendants = 64

    def __init__(self, anchor, folder, native_output=None, job=None):
        self.discovery_gate = threading.RLock()
        self.main_identity = None
        self.native_output = native_output
        self.tree_hold_written = False
        self.job = job
        self.ephemeral_observations = []
        super().__init__(anchor, folder)

    def retain(self, process, role, ancestry):
        entry = super().retain(process, role, ancestry)
        if self.job is not None:
            require(self.job.contains(entry["handle"]._handle),
                    "Observed fixture descendant is outside its original non-breakaway job")
        return entry

    def collect(self):
        # A retained compiler/editor is still an ancestry root after its helper
        # exits. Never reconstruct an old ancestry or use PID-only exit proof.
        with self.discovery_gate:
            for owner in list(self.entries.values()):
                if owner["handle"].api.wait(owner["handle"]._handle, 0) == 0:
                    continue
                try:
                    parent = psutil.Process(owner["identity"]["pid"])
                    require(identity(parent) == owner["identity"], "Owned fixture discovery identity changed")
                    children = parent.children(recursive=True)
                except psutil.NoSuchProcess:
                    if owner["handle"].api.wait(owner["handle"]._handle, 0) == 0:
                        continue
                    self.capture_incomplete = True
                    raise
                for process in children:
                    existing = None
                    try:
                        # psutil caches creation time on each discovered Process.
                        # Match the birth first: an already retained short-lived
                        # compiler can exit before the remaining identity reads.
                        key = process.pid, process.create_time()
                        existing = self.entries.get(key)
                        if existing is not None and existing["handle"].evidence.get("birthAndImageVerified") is True and \
                                existing["handle"].api.wait(existing["handle"]._handle, 0) == 0:
                            continue
                        info = identity(process)
                        require((info["pid"], info["created"]) == key, "Fixture candidate birth changed during discovery")
                        if existing is not None:
                            require(existing["identity"] == info and existing["handle"].evidence.get("birthAndImageVerified") is True,
                                    "Previously retained fixture descendant identity changed")
                            continue
                        require(len(self.entries) - 1 < self.maximum_descendants,
                                "Owned fixture descendant capture exceeds its 64-process bound")
                        chain = []
                        for ancestor in process.parents():
                            captured = identity(ancestor)
                            chain.append(captured)
                            if captured == owner["identity"]:
                                break
                        require(chain and chain[-1] == owner["identity"], "Owned fixture descendant ancestry unavailable")
                        if owner["role"] != "anchor":
                            chain += owner["ancestry"]
                        require(chain[-1] == self.anchor, "Fixture descendant is outside the original helper tree")
                        role = "worker" if info["name"] == "TiaMcp.Openness.V21.exe" else "native"
                        self.retain(process, role, chain)
                    except BaseException as error:
                        if isinstance(error, psutil.NoSuchProcess) and existing is not None and \
                                existing["handle"].evidence.get("birthAndImageVerified") is True and \
                                existing["handle"].api.wait(existing["handle"]._handle, 0) == 0:
                            # Only that original birth-matched handle proves this
                            # raced lifetime terminal. Never fall back to PID.
                            continue
                        if isinstance(error, psutil.NoSuchProcess) and getattr(self, "job", None) is not None:
                            # No individual exit/identity assertion for this race.
                            # The original kernel job must drain before release;
                            # the main editor still needs its exact retained handle.
                            require(len(self.ephemeral_observations) < self.maximum_descendants,
                                    "Ephemeral observation bound exceeded")
                            self.ephemeral_observations.append({
                                "pidObserved": process.pid, "individualLifetimeVerified": False,
                                "terminalBasis": "Original ordinary-descendant job must drain"})
                            continue
                        self.capture_incomplete = True
                        self.errors.append("Fixture descendant capture failed: " + type(error).__name__ + ": " + str(error))
                        raise

    def native(self, pid):
        self.collect()
        matches = [entry for entry in self.entries.values()
                   if entry["identity"]["pid"] == pid and entry["identity"]["name"] == "Siemens.Automation.Portal.exe"]
        require(len(matches) == 1 and not self.capture_incomplete and not self.errors,
                "Exact main fixture editor handle is unavailable or unqualified")
        main = matches[0]
        require(main["identity"]["parentPid"] == self.anchor["pid"] and main["ancestry"] == [self.anchor]
                and main["handle"].evidence.get("birthAndImageVerified") is True,
                "Reported main fixture editor is not the original helper's direct owned child")
        require(self.main_identity is None or self.main_identity == main["identity"], "Main fixture editor identity changed")
        self.main_identity = main["identity"]
        return main

    def all_terminal(self):
        try:
            self.collect()
        except BaseException as error:
            self.capture_incomplete = True
            self.errors.append("Fixture terminal discovery failed: " + type(error).__name__ + ": " + str(error))
        terminal = super().all_terminal()
        # A native start that escaped discovery must not become an empty-tree
        # success after the helper exits. The helper writes intent before TIA start.
        if terminal and self.native_output is not None and (self.native_output / "start-intent.json").exists():
            try:
                startup = read(self.native_output / "start-return.json")
                matches = [entry for entry in self.entries.values()
                           if entry["identity"]["pid"] == startup["processId"]
                           and entry["identity"]["name"] == "Siemens.Automation.Portal.exe"
                           and entry["identity"]["parentPid"] == self.anchor["pid"]
                           and entry["ancestry"] == [self.anchor]
                           and entry["handle"].evidence.get("birthAndImageVerified") is True]
                require(len(matches) == 1, "Native startup lacks its original main editor handle")
                require(self.main_identity is None or self.main_identity == matches[0]["identity"], "Main fixture startup identity changed")
                self.main_identity = matches[0]["identity"]
            except BaseException as error:
                self.capture_incomplete = True
                self.errors.append("Native startup terminal proof incomplete: " + type(error).__name__ + ": " + str(error))
        if self.capture_incomplete:
            # The shared reconciliation loop still exposes exact bound cleanup
            # commands for captured handles. A missing lifetime cannot be guessed.
            if not self.tree_hold_written:
                try:
                    write(self.folder / "fixture-tree-hold.json", dict(
                        reason="Owned tree capture is incomplete; captured-handle cleanup cannot prove an uncaptured lifetime.",
                        anchor=self.anchor, errors=self.errors, originalHandlesRetained=True,
                        dependentDispatchProhibited=True, automaticTermination=False,
                        reconciliationReceipt=str(self.folder / "cleanup-live.json"),
                        commandPattern="process-cleanup-command-*.json"))
                    self.tree_hold_written = True
                except BaseException:
                    pass
            return False
        if getattr(self, "job", None) is not None:
            accounting = self.job.snapshot()
            require(accounting["totalProcesses"] >= len(self.entries),
                    "Owned job accounting does not cover retained members")
            return terminal and accounting["activeProcesses"] == 0 and accounting["helperHandleSignaled"]
        return terminal

    def finish(self, explicit_native_dispose=False):
        value = super().finish(explicit_native_dispose=explicit_native_dispose)
        if getattr(self, "job", None) is not None:
            proof = self.job.terminal_proof()
            proof["ephemeralObservations"] = self.ephemeral_observations
            value["jobProof"] = write(self.folder / "owned-job-terminal.json", proof)
            value["allDescendantIndividualHandlesClaimed"] = False
        return value

    def await_terminal(self, reason, **kwargs):
        # Dynamic descendant count is not the main-editor admission condition.
        # Keep the shared visible, explicitly bound cleanup route, not hold_missing.
        kwargs.pop("expected_native", None)
        kwargs.pop("expected_worker", None)
        kwargs.pop("missing_capture", None)
        return super().await_terminal(reason, **kwargs)


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def reference(path):
    path = Path(path).resolve(strict=True)
    return dict(path=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest())


def write(path, value):
    with Path(path).open('x', encoding='utf-8') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')
    return reference(path)


def hold_missing(root, reason):
    # Even a full disk or a failed receipt must not unwind the held store window.
    try:
        write(root / 'uncaptured-startup.json', dict(reason=reason, automaticTermination=False,
            originalHandlesRetained=True, dependentDispatchProhibited=True))
    except BaseException:
        pass
    while True:
        try: time.sleep(1)
        except BaseException: pass


def execute_helper(helper, request, root, output, result, isolation=None, command='repair-ui'):
    """Caller holds the store window throughout launch, capture and terminal proof."""
    process = tracker = ticket = None
    require(command in {'repair-ui', 'verify-configured'}, 'Only exact fixture helper modes are allowed')
    with (root / 'stdout.log').open('xb') as stdout, (root / 'stderr.log').open('xb') as stderr:
        try:
            process = OwnedProcessJob()
            if isolation is not None:
                # Recheck source fingerprints, empty output and the current boot
                # while the caller still holds both registry and store locks.
                result['isolatedAdmission'] = native_dispatch_guard.scan(native_dispatch_guard.DEFAULT_ROOT, isolation=isolation)
            ticket = native_dispatch_guard.begin(native_dispatch_guard.DEFAULT_ROOT,
                job_name=process.name, source=request['path'])
            process.start([str(helper.resolve()), command, request['path']], stdout=stdout, stderr=stderr)
            tracker = FixtureProcesses(identity(psutil.Process(process.pid)), root, output, job=process)
            ready_seen = False
            while process.poll() is None:
                if (output / 'ui-ready.json').exists() and not ready_seen:
                    ready = read(output / 'ui-ready.json'); native = tracker.native(ready['processId'])
                    require(Path(ready['projectPath']).resolve().is_relative_to(output), 'Unexpected editor project')
                    require(native['identity']['parentPid'] == process.pid, 'Unexpected native ancestry')
                    write(root / 'editor-ready.json', dict(uiReady=reference(output / 'ui-ready.json'),
                        nativeIdentity=native['identity'], originalHandleRetained=True, storeLocksHeld=True))
                    print('EDITOR_READY ' + str(root / 'editor-ready.json'), flush=True)
                    ready_seen = True
                time.sleep(.1)
        finally:
            # This finally begins before process identity/capture can throw. Never
            # let the outer error handler release the reservation around a live child.
            if process is not None and process.process_handle is not None:
                if tracker is None:
                    hold_missing(root, 'Helper started but exact startup capture did not complete')
                tracker.await_terminal('Wait for exact fixture helper and every retained owned descendant')
                disposed = False
                try:
                    native_result = read(output / 'result.json')
                    disposed = native_result.get('explicitOwnedSdkDispose') is True
                except (OSError, ValueError):
                    pass
                result['processProof'] = tracker.finish(explicit_native_dispose=disposed)
                result['helperExitCode'] = process.wait()
                native_dispatch_guard.complete(ticket, result['processProof']['jobProof']['path'])
                process.close()
            elif process is not None:
                # A refused atomic launch never ran an uncontained helper.
                proof = process.snapshot()
                require(proof['totalProcesses'] == proof['activeProcesses'] == 0,
                        'Failed launch has unexpected job members')
                proof.update(schemaVersion='owned-job-terminal.v1', launchNeverCreated=True)
                receipt = write(root / 'owned-job-terminal.json', proof)
                if ticket is not None:
                    native_dispatch_guard.complete(ticket, receipt['path'])
                process.close()


def prerequisites(args):
    """File-only inspection; does not acquire stores or grant native admission."""
    require(args.profile in PROFILES, 'Unsupported exact fixture profile')
    source = read(args.source_report)
    matches = [p for p in source['profiles'] if p['profile'] == args.profile]
    require(len(matches) == 1, 'Exactly one selected fixture profile is required')
    profile = matches[0]
    require(source.get('completed') is True and profile.get('fixtureMode') == 'positive' and
            profile.get('deliberateInvalidReferences') is False and profile.get('archiveRetrievalVerified') is True and
            Path(profile['projectPath']).name.startswith('TiaMcpHmi_'),
            'An original positive owned fixture with observed archive retrieval is required')
    require(reference(profile['archive']['path']) == profile['archive'], 'Original archive changed')
    recovery = read(args.recovery_report)
    require(recovery.get('completed') is True and recovery.get('preservation', {}).get('checkpointRecovery') is True and
            recovery.get('preservation', {}).get('recoveredFreshReopen') is True and
            isinstance(recovery.get('sessions'), list) and len(recovery['sessions']) > 0 and
            all(s.get('cleanup', {}).get('completed') is True for s in recovery['sessions']), 'Recovery foundation failed')
    owners = [o['name'] for o in profile['nativeBefore']]
    require(owners and all(isinstance(o, str) and o.strip() for o in owners) and
            len(set(owners)) == len(owners), 'Distinct nonempty HMI owners are required')
    references = dict(helper=reference(safe_path(args.helper)), sourceReport=reference(safe_path(args.source_report)),
                      recoveryGate=reference(safe_path(args.recovery_report)), archive=profile['archive'])
    return profile, owners, references


def run(args):
    profile, owners, references = prerequisites(args)
    root = safe_path(args.output); root.mkdir(parents=True, exist_ok=False)
    output = root / 'native'
    if args.prepare_only:
        packet = dict(kind='hmi-fixture-ui-preparation.v1', profile=args.profile, inputs=references,
            owners=owners, allowedEdit=PROFILES[args.profile], preparedOnly=True, nativeDispatched=False,
            admissionGranted=False, originalReportsUnchanged=True, runtimeAllowed=False, automaticReplay=False,
            disableSecurityAllowed=False, executeScriptsAllowed=False,
            prerequisiteCompileQualified=False, freshHmiReopenQualified=False, bridgeRecoveryQualified=False,
            recoveryEvidenceScope='Existing PLC checkpoint/reopen foundation; does not qualify HMI recovery',
            requiredNextSteps=['Resume the previously paused native editor workflow explicitly',
                'Revalidate original inputs and acquire current sequential native/store ownership',
                'Retrieve a new owned copy; edit only the selected prerequisite and Save',
                'Capture complete error-free HMI diagnostics and exact cleanup',
                'Separately verify fresh reopen and operation-specific bridge recovery/preservation'])
        receipt = write(root / 'preparation.json', packet)
        print(json.dumps(dict(preparedOnly=True, nativeDispatched=False, packet=receipt)))
        return 0
    review = write(root / 'review.json', dict(approved=True, scope='Owned HMI fixture prerequisite only',
        sourceArchiveSha256=profile['archive']['sha256'], owners=owners,
        allowedEdit=PROFILES[args.profile], disableSecurityAllowed=False, executeScriptsAllowed=False))
    resources = [dict(kind='project', path=profile['projectPath'], effect='Read retained fixture provenance'),
                 dict(kind='workspace', path=str(output), effect='Retrieve fresh owned copy and ' + PROFILES[args.profile])]
    result = dict(completed=False, automaticReplay=False, runtimeVerified=False, nativeQualified=False)
    isolation = None
    if getattr(args, 'isolate_prior_boot', False):
        from native_dispatch_isolation import RebootIsolation
        isolation = RebootIsolation(output)
    try:
        with StoreExclusiveWindow([root, *args.known_store], profile['projectPath'], root / 'window',
                mode='isolatedQualificationMutation', resources=resources,
                effects='Prerequisite-only UI repair in a new archive copy; no online/runtime operation', isolation=isolation):
            admission = dict(ownedFixture=True, sequentialNativeAdmission=True, checkpointRetrievalGatePassed=True,
                automaticReplay=False, runtimeAllowed=False, helperSha256=reference(args.helper)['sha256'],
                archive=profile['archive'], sourceReport=reference(args.source_report), review=review,
                storeWindowIntent=reference(root / 'window-intent.json'), recoveryGate=reference(args.recovery_report),
                outputDirectory=str(output), artifactRoot=str(root), uiTimeoutMinutes=30)
            request = write(root / 'admission.json', admission)
            execute_helper(args.helper, request, root, output, result, isolation=isolation)
            result['nativeResult'] = read(output / 'result.json')
            require(result['helperExitCode'] == 0 and result['processProof']['completed'] and
                    result['nativeResult']['completed'], 'Fixture preparation or exact cleanup failed')
            result['completed'] = True
    except BaseException as error:
        result['failure'] = dict(type=type(error).__name__, message=str(error))
    finally:
        write(root / 'result.json', result)
    print(json.dumps(dict(completed=result['completed'], report=str(root / 'result.json'))))
    return 0 if result['completed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['helper', 'source-report', 'recovery-report', 'output']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--known-store', type=Path, action='append', default=[])
    parser.add_argument('--profile', choices=sorted(PROFILES), default='classic-comfort')
    parser.add_argument('--prepare-only', action='store_true', help='Validate and retain inputs only; no locks, native start or admission')
    parser.add_argument('--isolate-prior-boot', action='store_true', help='Explicit fresh-job/copy separation from the exact reviewed legacy failure after corroborated OS reboot; never clears its quarantine')
    raise SystemExit(run(parser.parse_args()))
