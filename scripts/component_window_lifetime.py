"""Explicit component lifetime ownership and known-store envelope; no native call at import."""
import copy
import hashlib
import json
from pathlib import Path
import re

from qualify_translation_category_probe import StoreExclusiveWindow, resource_key, overlaps, safe_path
from verify_watch_workflow import canonical_sha


def require(value, message):
    if not value:
        raise RuntimeError(message)


def scan_component_reservations(root, keys, owner=None):
    """Caller holds this root's shared store.lock; fail closed on retained unknown ownership."""
    folder = Path(root) / "engineering-component-windows"
    if not folder.exists():
        return []
    observed = []
    directories = sorted(p for p in folder.iterdir() if p.is_dir())
    require(len(directories) <= 10000, "Component reservation count is excessive")
    for directory in directories:
        safe_path(directory)
        if re.fullmatch(r"\.preparing-[0-9a-f]{32}", directory.name):
            continue
        require(re.fullmatch(r"[0-9a-f]{32}", directory.name), "Unknown component reservation directory")
        files = sorted(directory.glob("*.json")); require(1 <= len(files) <= 96, "Invalid component revision count")
        prior = None
        for revision, path in enumerate(files):
            require(path.name == f"{revision:08d}.json" and path.stat().st_size <= 2 * 1024 * 1024, "Invalid component journal sequence")
            record = json.loads(path.read_text(encoding="utf-8"))
            require(record["id"] == directory.name and record["revision"] == revision and record["previousSha256"] == (None if prior is None else canonical_sha(prior)), "Component hash chain differs")
            require(record["state"] in {"active", "outcomeUnknown", "released", "recoveryReconciled"}, "Unknown component lifetime state")
            resources = sorted(set(resource_key(r["kind"], r["path"]) for r in record["manifest"]["resources"]))
            require(resources == record["resourceKeys"], "Component resource keys differ")
            if prior is not None:
                require(record["manifestSha256"] == prior["manifestSha256"] and record["manifest"] == prior["manifest"] and
                        record["ownerCapabilitySha256"] == prior["ownerCapabilitySha256"] and prior["state"] not in {"released", "recoveryReconciled"} and
                        (prior["installationAllowed"] or not record["installationAllowed"]), "Component immutable identity/terminal state changed")
            prior = record
        exact_owner = (owner is not None and prior["id"] == owner["id"] and prior["manifestSha256"] == owner["manifestSha256"]
                       and hashlib.sha256(owner["capability"].encode("ascii")).hexdigest() == prior["ownerCapabilitySha256"])
        require(prior["state"] in {"released", "recoveryReconciled"} or exact_owner or not any(overlaps(a, b) for a in prior["resourceKeys"] for b in keys), "Unresolved overlapping component reservation: " + str(directory))
        observed.append({"id": directory.name, "state": prior["state"], "latest": str(files[-1]), "sha256": hashlib.sha256(files[-1].read_bytes()).hexdigest()})
    return observed


class ComponentLifetime:
    def __init__(self, run):
        self.run, self.owner, self.held = run, None, None

    def control(self, action, **extra):
        request = {"action": action, **extra}
        if self.owner and action != "begin":
            request.update(id=self.owner["id"], capability=self.owner["capability"])
        result = self.run.session.audit("qualification_component_window", {"request": request})
        self.run.retain("component_window_" + action, result)
        return result

    def begin(self, prepared, library):
        run = self.run; project = run.session.current_project
        resources = [{"kind": "project", "path": project, "effect": "wholeComponentTarget"},
                     {"kind": "project", "path": str(Path(project).parent), "effect": "projectCompanions"},
                     {"kind": "workspace", "path": str(Path(prepared["journalPath"]).parent), "effect": "reviewedPlanAndSourceChecks"}]
        for field in ("workingDirectory", "sourcePath", "originalSourcePath"):
            path = Path(library[field]); path = path.parent if path.suffix.lower() == ".al21" else path
            resources.append({"kind": "library", "path": str(path), "effect": field})
        roots = {str(run.store_root.resolve()).upper(): run.store_root, str(run.native.resolve()).upper(): run.native}
        for root in getattr(run.args, "known_store_root", ()) or ():
            root = Path(root).resolve(); roots[str(root).upper()] = root
        previous_path = getattr(run.args, "reuse_source_report", None)
        if previous_path:
            previous = json.loads(Path(previous_path).read_text(encoding="utf-8"))
            original_root = Path(previous["nativeArtifactRoot"]).resolve(); roots[str(original_root).upper()] = original_root
            for item in run.report.get("sourceReuse", {}).get("originalInputFiles", []):
                resources.append({"kind": "workspace", "path": item["path"], "effect": "retainedOriginalSourceInputReadOnly"})
        unique = {resource_key(r["kind"], r["path"]): r for r in resources}; resources = list(unique.values())
        roots = sorted(roots.values(), key=lambda p: str(p).upper())
        effects = "Component qualification imports, creates reviewed caller/instance, checkpoints, compiles/saves and retrieves first checkpoint under canonical durable parent; unknown owners are never cleared"
        with StoreExclusiveWindow(roots, project, run.output / "component-admission", mode="isolatedQualificationMutation", resources=resources, effects=effects) as admission:
            extra = [dict(root=str(root), reservations=scan_component_reservations(root, admission.keys)) for root in roots]
            run.report["componentCoordinatorAdmission"] = {"held": copy.deepcopy(admission.evidence), "componentReservations": extra,
                "coordinationLimit": "Known retained stores only; unknown independently configured stores are not coordinated"}; run.persist()
        # Canonical store.lock must be released before its Host dispatcher records the parent.
        # Other known stores remain locked throughout native work; no Host in this run uses them.
        foreign = [root for root in roots if root != run.store_root]
        if foreign:
            self.held = StoreExclusiveWindow(foreign, project, run.output / "component-known-stores", mode="isolatedQualificationMutation", resources=resources, effects=effects)
            self.held.__enter__()
            for root in foreign:
                scan_component_reservations(root, self.held.keys)
            run.report["componentKnownStoresHeld"] = copy.deepcopy(self.held.evidence); run.persist()
        result = self.control("begin", planId=prepared["changeId"], planPreviewHash=prepared["previewHash"])
        self.owner = copy.deepcopy(result["componentOwner"]); run.report["componentOwner"] = copy.deepcopy(self.owner); run.persist()
        return self.control("start")

    def resume(self):
        return self.control("owner-resume")

    def close(self):
        if self.held is not None:
            held, self.held = self.held, None
            held.__exit__(None, None, None)
