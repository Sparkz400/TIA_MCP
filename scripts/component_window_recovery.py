"""Explicit separate capability-bound component recovery. Never installation continuation."""
import copy
import hashlib
import json
from pathlib import Path

from component_window_lifetime import ComponentLifetime, scan_component_reservations
from qualify_translation_category_probe import StoreExclusiveWindow, resource_key, safe_path
from qualification_owned_scratch import require


def read_inputs(run, report_path):
    path = safe_path(report_path); previous = json.loads(path.read_text(encoding="utf-8-sig"))
    require(previous.get("status") == "failed", "Only an explicitly retained failed attempt enters recovery-only coordination")
    owner = previous.get("componentOwner")
    require(isinstance(owner, dict) and all(owner.get(k) for k in ("id", "capability", "manifestSha256", "workspace")),
            "Legacy report has no whole-window capability; use a separately reviewed legacy recovery packet, never unreserved retrieval")
    require(Path(previous["nativeArtifactRoot"]).resolve() == run.store_root, "Component parent uses a different canonical Host store")
    ledger = run.store_root / "engineering-component-windows" / owner["id"]
    files = sorted(ledger.glob("*.json")); require(files, "Component parent reservation absent")
    current = json.loads(files[-1].read_text(encoding="utf-8"))
    require(current["manifestSha256"] == owner["manifestSha256"] and current["manifest"]["workspace"] == owner["workspace"] and
            hashlib.sha256(owner["capability"].encode("ascii")).hexdigest() == current["ownerCapabilitySha256"] and current["state"] not in {"released", "recoveryReconciled"},
            "Exact retained owner/manifest is absent or terminal")
    return previous, owner, current, {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def run_recovery(run, previous_report, session_factory, client_factory, extra_tools):
    previous, owner, record, original_report = read_inputs(run, previous_report)
    manifest = record["manifest"]; project = safe_path(manifest["projectPath"])
    resources = copy.deepcopy(manifest["resources"]) + [{"kind": "project", "path": str(project), "effect": "Exact original observation selector"}]
    roots = {str(run.store_root).upper(): run.store_root, str(run.native).upper(): run.native}
    for value in [previous.get("fixtureWorkspace"), *getattr(run.args, "known_store_root", ())]:
        if value:
            root = safe_path(value); roots[str(root).upper()] = root
    roots = sorted(roots.values(), key=lambda p: str(p).upper())
    effects = "Resume exact retained component owner; retrieve only first inner checkpoint into admitted descendant; fresh native restoration and original observation; never installation replay"
    with StoreExclusiveWindow(roots, project, run.output / "recovery-admission", mode="isolatedQualificationMutation", resources=resources, effects=effects) as held:
        observations = [dict(root=str(root), reservations=scan_component_reservations(root, held.keys, owner)) for root in roots]
        run.report["componentRecoveryAdmission"] = {"held": copy.deepcopy(held.evidence), "reservations": observations, "knownStoresOnly": True}; run.persist()
    foreign = [root for root in roots if root != run.store_root]
    run.lifetime = ComponentLifetime(run); run.lifetime.owner = copy.deepcopy(owner)
    if foreign:
        run.lifetime.held = StoreExclusiveWindow(foreign, project, run.output / "recovery-known-stores", mode="isolatedQualificationMutation", resources=resources, effects=effects)
        run.lifetime.held.__enter__()
        for root in foreign: scan_component_reservations(root, run.lifetime.held.keys, owner)
    run.report.update(componentOwner=copy.deepcopy(owner), qualificationScope="first_checkpoint_recovery_only", installationVerified=False,
                      installationAccepted=False, originalReport=original_report, originalSourceAttempt=previous["status"])
    run.session = session_factory(run.args.host, run.args.worker, run.store_root, run.report, run.persist,
        timeout=run.args.timeout, client_factory=client_factory, extra_tools=extra_tools, lifecycle_directory=run.output / "exact-process-lifetimes")
    def status(): return run.lifetime.control("status")
    def open_project(path):
        path = str(safe_path(path)); run.session.known_paths.add(path); run.session.call("project_open", {"path": path})
    def recovered_path(record):
        child = next(c for c in record["children"] if c["step"] == "recover")
        require(child["state"] in {"returned", "reconciled"} and child.get("response"), "Unknown recovery is never retried")
        receipt = safe_path(run.store_root / child["response"]["relativePath"])
        require(hashlib.sha256(receipt.read_bytes()).hexdigest() == child["response"]["sha256"], "Retained recovery result changed")
        return json.loads(receipt.read_text(encoding="utf-8-sig"))["resources"][0]["project"]["path"]
    try:
        children = record["children"]; require(len(children) >= 2, "No first import checkpoint can exist before an import dispatch")
        require(children[-1]["step"] not in {"recover", "observeRestored", "observeRecoveryOriginal"} or children[-1]["state"] in {"returned", "reconciled"},
                "An interrupted recovery observation needs explicit retained-receipt reconciliation before this coordinator; it is never replayed")
        run.session.start()
        if children[-1]["step"] == "recover": open_project(recovered_path(record))
        elif children[-1]["step"] == "observeRestored" or len(children) == 6 and all(c["state"] in {"returned", "reconciled"} for c in children): open_project(project)
        run.lifetime.resume(); record = status()
        if not any(c["step"] == "recover" for c in record["children"]):
            if run.session.current_project is not None:
                run.session.close(); run.session.start(); run.lifetime.resume()
            restored = run.lifetime.control("recover"); run.report["recoveryReceipt"] = restored; run.persist()
            run.session.remember(restored["resources"][0]["project"]["path"])
            record = status()
        recovered = recovered_path(record)
        if record["children"][-1]["step"] == "recover":
            run.session.known_paths.add(str(safe_path(recovered))); run.session.reopen(recovered)
            run.report["restoredFresh"] = run.lifetime.resume(); record = status(); run.persist()
        if record.get("nextStep") == "observeRecoveryOriginal":
            run.session.known_paths.add(str(project)); run.session.reopen(str(project))
            run.report["recoveryOriginalFresh"] = run.lifetime.resume(); record = status(); run.persist()
        run.report["restorationObserved"] = any(c["step"] == "observeRestored" and c["state"] in {"returned", "reconciled"} for c in record["children"])
        run.report["recoveryReconciliation"] = run.lifetime.control("reconcile-recovery")
        require(run.report["recoveryReconciliation"].get("recoveryOnlyReconciled") is True and run.report["recoveryReconciliation"].get("installationAccepted") is False,
                "Recovery-only reconciliation cannot claim installation acceptance")
        run.report.update(status="recovery_only_reconciled", firstCheckpointRecoveryVerified=True)
    finally:
        try:
            if run.session is not None: run.session.close()
        finally:
            run.lifetime.close()
        require(hashlib.sha256(Path(original_report["path"]).read_bytes()).hexdigest() == original_report["sha256"], "Original failed report changed")
        run.persist()
