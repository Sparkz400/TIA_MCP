"""Qualify the D12/X03/D01 certificate, UMAC, web server and VCI workspace kinds natively in one owned scratch CPU 1511-1 PN.

Phase A applies every covered kind with checkpoint, compile and save. Each preview is asserted exactly against the request
and the read tools; change_status follows each apply; and a complete state (every block, type and tag-table export plus
the certificate_inspect, access_inspect, web_inspect and vci_workspace_inspect readback) must differ from the previous one
in exactly the intended keys. Native side effects are tolerated only where a change names them, and are recorded. A fresh
Portal then reopens the saved project, which must show the identical state. Phase B applies one more change per kind from
that state and restores its checkpoint in the applying Portal; the recovered copy must equal the pre-change state. Typed
refusals must change nothing. Phase C runs webserver_user_pages_generate (no earlier native run) through the same gates
with its own reopen; its failure is recorded under skipped and leaves only that kind unqualified.
"""
from __future__ import annotations

import argparse
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import re

from compilation_evidence import require_clean_compile, require_plc_compile
from library_native_harness import Harness, require
from qualification_owned_scratch import complete_inventory, snapshot
from qualify_plc_object_mutations import STAGES, coverage_gaps, diff, in_order, matches, view

KINDS = ("webserver_page_set", "webserver_user_pages_generate", "certificate_create", "certificate_assign", "certificate_delete",
         "certificate_import", "umac_role_set", "vci_workspace_create", "vci_workspace_delete")
_MAPPING = "Not in this item: mappings need exported package fixtures and package file-effect gates; this run's workspaces have no mappings."
# D12/X03/D01-owned change kinds this run does not qualify; scripts/test_qualify_access_changes.py keeps KINDS plus these
# equal to the catalog's change kinds owned by D12, X03 or D01.
EXCLUDED = {"vci_mapping_add": _MAPPING, "vci_mapping_remove": _MAPPING, "vci_synchronize": _MAPPING}
PLC_COMPILED = ("webserver_user_pages_generate", "vci_workspace_create", "vci_workspace_delete")  # The other kinds compile all devices.
ATTRIBUTE = "PlcCommunicationCertificate"
UNUSED_ID = 4294967295  # Largest certificate Id; a scratch store never allocates it.
ROLE = "TiaMcpQualifyRole"
APP = "tiamcpq"
PAGE = "<!DOCTYPE html>\n<html><head><title>TIA MCP</title></head><body><p>TIA MCP qualification page</p></body></html>\n"
IDENTITY = ("certificateId", "subject", "issuer", "serialNumber", "sha256", "notBeforeUtc", "notAfterUtc", "subjectAlternativeNames", "hasPrivateKey")
CRITERIA = {
    "AC-identity": "refused: typed refusals of missing, stale, drifted, protected or unsafe targets, each leaving the program and the certificate, access, web and VCI state unchanged.",
    "AC-preview": "Every preview is asserted exactly against the request and the read tools before apply; previews made stale by a later change refuse at apply.",
    "AC-preserve": "applied: complete program exports plus the certificate, access, web and VCI state differ in exactly the intended keys; named native side effects are recorded under sideEffects.",
    "AC-native": "applied + persisted: checkpoint, device or PLC compile and save stages, change_status, independent readback and a fresh owned Portal reopen with identical state and applied journals.",
    "AC-recovery": "restored: change_restore in the applying Portal into a new directory equals the pre-change state; a native generation failure keeps its checkpoint (nativeBound).",
    "AC-evidence": "This report: binaries, tool calls, step results, snapshots with state.json, fixture, limitations, sideEffects, certificateIdentity and notQualified.",
}
LIMITATIONS = [
    "One owned scratch CPU 1511-1 PN with its local certificate store; the global certificate store, other CPUs, HMI devices and UMC servers are not exercised.",
    "certificate_assign writes only PlcCommunicationCertificate on the store-owning CPU; WebserverCertificate, OPC UA attributes and null unassignment are not written. "
    "certificate_inspect renders certificate attributes display-only, so the assigned Id is read back through typed refusals (AssignmentDrift currentCertificateId, CertificateInUse references).",
    "Certificates are created with usage WebServer and one Dns alternative name. The persisted identity after the device compile is compared with the request and recorded under "
    "certificateIdentity; the wave 10 probe saw the compile re-issue a created certificate (CN-only subject, new serial and notBefore, the CPU address as alternative names), "
    "and a mismatch lists the kind under notQualified.",
    "certificate_import imports the public DER of a certificate this run created and deleted; PEM, CA-issued and foreign certificates are not imported natively.",
    "umac_role_set: one custom role, one device function right taken from the scratch's custom role and one active project user; engineering function rights are never "
    "granted or revoked (no read tool lists the available ones), so AdministratorLockout is not exercised; anonymous and UMC principals are untouched.",
    "webserver_page_set writes webServerActive and systemWebPages; defaultApplication is only refused (stale or missing application), never written.",
    "Switching web serving on or off is allowed to change the CPU's web- and certificate-named attributes and to add or remove certificates in its store; these side effects are recorded under sideEffects.",
    "webserver_user_pages_generate: one static HTML page; generated DB identities come from the apply readback; the CPU's web configuration and web-named attributes may change "
    "with generation and are recorded under sideEffects; page content, browsers and downloads are not checked.",
    "VCI workspaces are created in the root workspace group without mappings, with roots outside the artifact store and the project; change_restore never restores workspace files.",
    "Not covered here: " + ", ".join(sorted(EXCLUDED)) + ".",
    "Engineering only; nothing is downloaded and runtime behavior is not observed.",
]


def domains(certificates, access, web, vci):
    """Comparable certificate, UMAC/access, web and VCI state from the four read tools, keyed by native identity. Envelopes,
    warnings and the project path are left out, so a recovered copy compares equal to its original."""
    for result in (certificates, access, web, vci):
        require(result["completeness"]["truncated"] is False, "A read tool truncated its scope: %s" % result["completeness"])
    state = {"syslog": certificates["syslogServers"]}
    for row in certificates["devices"]:
        path = row["objectPath"]
        state["certStore:" + path] = {key: row[key] for key in ("globalCertificateStoreEnabled", "certificateManagement")}
        for item in row["certificates"]:
            state["cert:%s/%s" % (path, item["certificateId"])] = {**{k: v for k, v in item.items() if k != "observation"}, "der": item["observation"]["contentBase64"]}
        for item in row.get("attributes") or []:
            state["certAttr:%s/%s" % (path, item["name"])] = item
    umac = access["projectUmac"]
    if umac is not None:
        names = umac["roleNames"]
        require(umac["snapshot"]["entitiesComplete"] is True, "access_inspect left the UMAC entities incomplete")
        state["umac"] = {"anonymousUserActive": umac["anonymousUserActive"], "deviceRightScopes": umac["deviceRightScopes"]}
        for entity in umac["snapshot"]["entities"]:
            key = "role:" + names.get(entity["id"], entity["id"]) if entity["kind"] == "role" else entity["id"]
            state["umac:" + key] = {**entity, "inherits": sorted(names.get(i, i) for i in entity["inherits"])}
    for row in access["devices"]:
        state["access:" + row["objectPath"]] = row
    for row in web["devices"]:
        path, configuration = row["objectPath"], row["observation"]["configuration"]
        state["web:" + path] = {**{k: configuration[k] for k in ("webEnabled", "systemPagesEnabled", "applicationInventoryComplete", "applications")},
                                "userDefinedPagesServiceExposed": row["userDefinedPagesServiceExposed"]}
        for item in row["attributes"]:
            state["webAttr:%s/%s" % (path, item["name"])] = item
    for row in vci["groups"]:
        state["vciGroup:" + row["path"]] = row
    for row in vci["workspaces"]:
        state["vciWorkspace:" + row["path"]] = row
    return state


def split(observed, wanted, tolerated):
    """Diff keys outside the wanted ones, as (tolerated native side effects, unexpected changes)."""
    side, unexpected = {}, {}
    for category, keys in observed.items():
        for key in keys:
            if key not in wanted[category]:
                (side if tolerated(category, key) else unexpected).setdefault(category, []).append(key)
    return side, unexpected


def umac_keyed(state):
    """A umac_role_set preview state keyed like diff() expects: roles by name, principals by kind and name."""
    return {**{"role:" + row["name"]: row for row in state["roles"]}, **{row["kind"] + ":" + row["name"]: row for row in state["principals"]}}


def umac_effect(payload):
    return {"action": payload["action"], "roleName": payload["roleName"], "userName": payload.get("userName"),
            "engineeringRights": payload.get("engineeringRights", []), "deviceRights": payload.get("deviceRights", [])}


def identity_exact(request, row):
    """Whether a persisted certificate carries the requested identity: exactly, or for WebServer usage the documented
    compile re-issue (common name and validity end kept; subject, alternative names, notBefore and serial TIA's)."""
    names = lambda rows: sorted((item["type"], item["value"]) for item in rows or [])
    kept = row["subjectCommonName"] == request["subjectCommonName"] and row["notAfterUtc"] == request["validUntilUtc"]
    if request.get("usage") == "WebServer":
        return kept
    return kept and row["notBeforeUtc"] == request["validFromUtc"] and names(row["subjectAlternativeNames"]) == names(request["subjectAlternativeNames"])


def web_pages(entry):
    """webServerBefore/After as the worker previews it, from a web_inspect state entry."""
    return {"systemWebPagesEnabled": entry["systemPagesEnabled"], "webserverActivate": entry["webEnabled"],
            "applications": [{key: row[key] for key in ("name", "applicationType", "isDefault")} for row in entry["applications"]]}


def files_under(root):
    root = Path(root)
    return sorted(path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file()) if root.exists() else []


def utc(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


class Run:
    """The owned scratch's current state, the reopened pre-change state and per-kind criterion coverage."""

    def __init__(self, h):
        self.h, self.count, self.coverage, self.applied, self.skipped = h, 0, {}, [], {}
        self.plc = self.cpu = self.project = self.current = self.b0 = None
        h.report.update(coverage=self.coverage, excluded=EXCLUDED, skipped=self.skipped, criteria=CRITERIA, sideEffects={}, certificateIdentity={}, notQualified={})

    # Keys of the compared state; fixture names are plain identifiers.
    def cert(self, identifier): return "cert:%s/%s" % (self.cpu, identifier)
    def webattr(self, name): return "webAttr:%s/%s" % (self.cpu, name)
    @property
    def web(self): return "web:" + self.cpu
    @property
    def hw(self): return {"hardwarePath": self.cpu}

    def root(self, suffix):
        # Beside the run's artifact store, so outside it and outside the project.
        return self.h.directory.parent / ("vci-%s-%s" % (self.h.directory.name, suffix))

    def store_ids(self):
        prefix = self.cert("")
        return sorted(int(key[len(prefix):]) for key in self.current["view"] if key.startswith(prefix))

    def user_pages(self, override, blocks=None):
        return {"htmlDirectory": str(self.pages), "defaultPage": "index.html", "applicationName": APP, "override": override,
                **({"overrideBlocks": blocks} if blocks is not None else {})}

    def web_side_effects(self, category, key):
        """Switching web serving changes the CPU's web- and certificate-named attributes and its web server certificate."""
        return key.startswith((self.webattr(""), "certAttr:%s/" % self.cpu)) or category != "changed" and key.startswith(self.cert(""))

    def page_side_effects(self, category, key):
        return key == self.web or key.startswith(self.webattr(""))

    def mark(self, kind, need, label):
        self.coverage.setdefault(kind, {}).setdefault(need, []).append(label)

    def domains(self):
        t = self.h.tool
        return domains(t("certificate_inspect"), t("access_inspect"), t("web_inspect"), t("vci_workspace_inspect"))

    def state(self):
        return {**view(complete_inventory(self.h.tool)), **self.domains()}

    def snap(self, label):
        self.count += 1
        destination = self.h.directory / "snapshots" / ("%03d-%s" % (self.count, re.sub(r"[^A-Za-z0-9]+", "-", label)[:40]))
        taken = {}
        def take():
            taken.update(snapshot(self.h.tool, destination), domains=self.domains())
            return {"objects": len(taken["objects"]), "domainKeys": len(taken["domains"]), "manifest": str(destination / "manifest.json")}
        self.h.stage("snapshot " + label, take)
        state = {**view(taken["inventory"], taken["objects"]), **taken["domains"]}
        (destination / "state.json").write_text(json.dumps(state, indent=1), encoding="utf-8")
        return {"view": state, "inventory": taken["inventory"]}

    def args(self, kind, payload, **top):
        # Web kinds select their CPU by the exact top-level plcPath; the others name hardware or workspace paths in the payload.
        return {"kind": kind, "payload": payload, **({"plcPath": self.plc} if kind.startswith("webserver_") else {}), **top}

    def prepare(self, label, kind, payload, preview=None, shown=None, exact=None):
        """preview must match recursively, exact must be equal and the echoed payload must equal shown (default: payload)."""
        prepared = self.h.stage("prepare " + label, lambda: self.h.tool("change_prepare", self.args(kind, payload)))
        actual = prepared["preview"]
        expected = {"changeId": prepared["changeId"], "kind": kind, "project": {"path": self.project}, **(preview or {})}
        require(prepared["state"] == "prepared" and actual["payload"] == (shown or payload) and matches(expected, actual)
                and all(actual.get(key) == value for key, value in (exact or {}).items()),
                "Preview of %s differs: %s" % (label, {key: actual.get(key) for key in [*expected, *(exact or {}), "payload"]}))
        return prepared

    def prepare_store(self, label, kind, payload, shown=None, **effect):
        ids = self.store_ids()
        prepared = self.prepare(label, kind, payload, {"effects": [{"action": kind, "store": self.cpu + "/certificates", "globalCertificateStoreEnabled": False, **effect}]}, shown)
        require(sorted(prepared["preview"]["effects"][0]["storeCertificateIds"]) == ids, "Previewed store Ids differ from certificate_inspect %s" % ids)
        return prepared

    def finish(self, label, kind, prepared, added=(), removed=(), changed=(), rows=None, tolerated=None, check=None, restore=False):
        """Apply with checkpoint, compile and save, read change_status, then require a complete state that differs in exactly
        the listed keys (lists or callables of (applied, state)); tolerated(category, key) names recorded native side effects."""
        h, before = self.h, self.current
        applied = h.apply_prepared(kind, prepared)
        require(applied["recoveryArchiveSha256"] and applied["preview"] == prepared["preview"], "Applied change lost its checkpoint or preview")
        require(in_order(applied["stages"], STAGES), "Checkpoint, apply, compile and save stages differ: %s" % applied["stages"])
        if kind in PLC_COMPILED:
            require_plc_compile(applied["compile"], self.plc)
        else:
            require(applied["compile"].get("targetIdentityPolicy") == "device-path-v1", "Expected the all-devices compile receipt")
            require_clean_compile(applied["compile"])
        def status():
            result = h.tool("change_status", {"changeId": prepared["changeId"]})
            require(result["state"] == "applied" and result["stages"] == applied["stages"] and result["afterFingerprint"] == applied["afterFingerprint"]
                    and result["recoveryArchiveSha256"] == applied["recoveryArchiveSha256"], "change_status differs from the apply result")
            return {"state": result["state"], "stages": result["stages"]}
        h.stage("change_status " + label, status)
        if not restore:  # Journals the next reopen must read as applied.
            self.applied.append(prepared["changeId"])
        self.current = self.snap(label)
        def verify():
            state = self.current["view"]
            wanted = {c: sorted(v(applied, state) if callable(v) else v) for c, v in (("added", added), ("removed", removed), ("changed", changed))}
            observed = diff(before["view"], state)
            side, unexpected = split(observed, wanted, tolerated or (lambda category, key: False))
            missing = {c: sorted(set(keys) - set(observed[c])) for c, keys in wanted.items() if set(keys) - set(observed[c])}
            require(not missing and not unexpected, "Native effect of %s differs: missing %s, unexpected %s" % (label, missing, unexpected))
            for key, row in (rows(applied, state) if callable(rows) else rows or {}).items():
                require(matches(row, state.get(key)), "Readback of %s differs: %s" % (key, state.get(key)))
            if check:
                check(applied, state)
            if side:
                h.report["sideEffects"][label] = {c: {k: {"before": before["view"].get(k), "after": state.get(k)} for k in keys} for c, keys in side.items()}
            return {"observed": observed, "sideEffects": side}
        h.stage("verify " + label, verify)
        self.mark(kind, "applied", label)
        if restore:
            self.restore(label, kind, prepared)
        return applied

    def refuse(self, kind, label, payload, code, **top):
        """A refused preparation changes no program object and no certificate, access, web or VCI state."""
        def check():
            before = self.state()
            error = self.h.call_error("change_prepare", self.args(kind, payload, **top))
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            require(self.state() == before, "Refused preparation changed the project")
            return error
        error = self.h.stage("refuse %s: %s" % (kind, label), check)
        self.mark(kind, "refused", code)
        return error

    def refuse_apply(self, kind, label, prepared, code):
        """A refused apply leaves the project unchanged and the change prepared, without stages or checkpoint."""
        def check():
            before = self.state()
            error = self.h.call_error("change_apply", self.h.apply_args(prepared))
            require(error.get("code") == code, "Expected %s, observed %s: %s" % (code, error.get("code"), error.get("message")))
            status = self.h.tool("change_status", {"changeId": prepared["changeId"]})
            require(status["state"] == "prepared" and not status["stages"] and not status["recoveryArchive"] and self.state() == before,
                    "Refused apply changed the change state or the project")
            return {"error": error, "state": status["state"]}
        self.h.stage("refuse apply %s: %s" % (kind, label), check)
        self.mark(kind, "refused", code)

    def restore(self, label, kind, prepared):
        h = self.h
        recovered = h.stage("restore " + label, lambda: h.tool("change_restore", {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}))
        require(recovered["state"] == "restored" and "checkpoint_retrieved_to_new_directory" in recovered["stages"], "Checkpoint recovery of %s was not verified" % label)
        path = recovered["recoveredProject"]["path"]
        require(Path(path).resolve() != Path(self.project).resolve(), "Recovery reused the changed project's directory")
        self.project = path
        self.current = self.snap("restored " + label)
        def equal():
            require([p["path"] for p in h.tool("tia_session_status")["projects"]] == [path], "The recovered copy is not the only open project")
            require(self.current["view"] == self.b0["view"], "Recovered state differs: %s" % diff(self.b0["view"], self.current["view"]))
            return len(self.b0["view"])
        h.stage("restored " + label + " equals the pre-change state", equal)
        self.mark(kind, "restored", label)

    def assigned(self, label=None):
        """The Id PlcCommunicationCertificate holds, read natively from the AssignmentDrift refusal of a fresh preparation."""
        payload = {**self.hw, "targetPath": self.cpu, "attributeName": ATTRIBUTE, "certificateId": None, "expectedCertificateId": UNUSED_ID}
        if label:
            error = self.refuse("certificate_assign", label, payload, "AssignmentDrift")
        else:
            error = self.h.call_error("change_prepare", self.args("certificate_assign", payload))
            require(error.get("code") == "AssignmentDrift", "Expected AssignmentDrift: %s" % error)
        return error["details"]["currentCertificateId"]

    def identity(self, kind, label, requested, readback, row):
        exact = identity_exact(requested, row) if kind == "certificate_create" else row["sha256"] == requested["sha256"] and row["hasPrivateKey"] is False
        self.h.report["certificateIdentity"][label] = {"requested": requested, "changeReadback": readback, "persisted": {k: row.get(k) for k in IDENTITY}, "exact": exact}
        if not exact:
            self.h.report["notQualified"].setdefault(kind, []).append(label)


def web_on(run):
    """Refusals while web serving is off, then activation with system pages and a duplicate preview it makes stale."""
    k, entry = "webserver_page_set", run.current["view"].get(run.web)
    require(entry and entry["webEnabled"] is False and entry["systemPagesEnabled"] is False, "Expected the scratch CPU with web serving and system pages off: %s" % entry)
    activate, pages = {"webServerActive": {"expected": False, "value": True}}, {"systemWebPages": {"expected": False, "value": True}}
    run.refuse(k, "system pages while web serving is off", pages, "WebServerNotActivated")
    run.refuse(k, "a stale webServerActive.expected", {"webServerActive": {"expected": True, "value": False}}, "StaleRevision")
    run.refuse(k, "a field outside the payload", {**activate, "webUsers": []}, "InvalidRequest")
    run.refuse(k, "a scopePath", activate, "InvalidRequest", scopePath=run.plc)
    run.refuse("webserver_user_pages_generate", "web serving off", run.user_pages(False), "WebServerNotActivated")
    before = web_pages(entry)
    setting = lambda name, old, new: {"plcPath": run.plc, "setting": name, "before": old, "after": new}
    preview = {"effects": [setting("WebserverActivate", False, True), setting("EnableSystemWebPages", False, True)]}
    exact = {"webServerBefore": before, "webServerAfter": {**before, "webserverActivate": True, "systemWebPagesEnabled": True}}
    label = "activate web serving with system pages"
    stale = run.prepare(label + " (duplicate, made stale below)", k, {**activate, **pages}, preview, exact=exact)
    prepared = run.prepare(label, k, {**activate, **pages}, preview, exact=exact)
    run.finish(label, k, prepared, changed=[run.web, run.webattr("WebserverActivate")], tolerated=run.web_side_effects,
               rows={run.web: {"webEnabled": True, "systemPagesEnabled": True, "applications": entry["applications"]}, run.webattr("WebserverActivate"): {"value": True}})
    run.refuse_apply(k, "a preview made stale by the activation", stale, "StaleRevision")
    entry = run.current["view"][run.web]
    default = next((row["name"] for row in entry["applications"] if row["isDefault"]), None)
    run.refuse(k, "a stale systemWebPages.expected", pages, "StaleRevision")
    run.refuse(k, "a stale defaultApplication.expected", {"defaultApplication": {"expected": "TiaMcpMissing", "value": None}}, "StaleRevision")
    run.refuse(k, "a missing default application", {"defaultApplication": {"expected": default, "value": "TiaMcpMissing"}}, "ObjectNotFound")
    if entry["userDefinedPagesServiceExposed"] is not True:
        run.skipped["webserver_user_pages_generate"] = "The active CPU exposes no WebserverUserDefinedPages service (web_inspect userDefinedPagesServiceExposed)."


def web_off(run):
    """Phase B: switching web serving off also clears the system pages flag, as previewed; then restored."""
    k, entry = "webserver_page_set", run.current["view"][run.web]
    before = web_pages(entry)
    require(before["webserverActivate"] is True and before["systemWebPagesEnabled"] is True, "Expected web serving and system pages on: %s" % before)
    label = "switch web serving off"
    prepared = run.prepare(label, k, {"webServerActive": {"expected": True, "value": False}},
                           {"effects": [{"plcPath": run.plc, "setting": "WebserverActivate", "before": True, "after": False}]},
                           exact={"webServerBefore": before, "webServerAfter": {**before, "webserverActivate": False, "systemWebPagesEnabled": False}})
    run.finish(label, k, prepared, changed=[run.web, run.webattr("WebserverActivate")], tolerated=run.web_side_effects, restore=True,
               rows={run.web: {"webEnabled": False, "systemPagesEnabled": False, "applications": entry["applications"]}, run.webattr("WebserverActivate"): {"value": False}})


def user_pages(run):
    """Generate user-defined page DBs from the run's own HTML directory; returns the generated block names."""
    k, payload = "webserver_user_pages_generate", run.user_pages(False)
    run.refuse(k, "overrideBlocks without override", {**payload, "overrideBlocks": ["TiaMcpMissingDb"]}, "InvalidRequest")
    run.refuse(k, "a default page outside htmlDirectory", {**payload, "defaultPage": "../outside.html"}, "InvalidRequest")
    run.refuse(k, "a relative htmlDirectory", {**payload, "htmlDirectory": run.pages.name}, "InvalidRequest")
    run.refuse(k, "an override block that does not exist", run.user_pages(True, ["TiaMcpMissingDb"]), "ObjectNotFound")
    label = "generate user pages"
    prepared = run.prepare(label, k, payload, {"effects": [{"action": "generateUserPageBlocks", "plcPath": run.plc, "option": "None", "overrideBlocks": []}],
                                               "webSource": {"defaultPage": "index.html", "applicationName": APP}, "backups": []})
    generated = {}
    def added(applied, state):
        readback = next(r for r in applied["readbacks"] if r["role"] == "webserver-generated-blocks")
        blocks = {row["name"]: row["number"] for row in readback["blocks"]}
        keys = sorted(key for key, row in state.items() if key.startswith("block:") and row.get("name") in blocks)
        require(blocks and readback["overwritten"] == [] and len(keys) == len(blocks) and all(state[key]["number"] == blocks[state[key]["name"]] for key in keys),
                "Generated blocks differ from the readback %s" % blocks)
        generated.update(names=sorted(blocks), keys=keys)
        return keys
    run.finish(label, k, prepared, added=added, tolerated=run.page_side_effects)
    run.page_keys = generated["keys"]
    return generated["names"]


def regenerate(run, names):
    """Regenerate with override=true over exactly the generated DBs; then restored."""
    k, label = "webserver_user_pages_generate", "regenerate user pages with override"
    prepared = run.prepare(label, k, run.user_pages(True, names), {"effects": [{"action": "generateUserPageBlocks", "plcPath": run.plc, "option": "Override", "overrideBlocks": names}]})
    overwritten = lambda applied: next(r for r in applied["readbacks"] if r["role"] == "webserver-generated-blocks")["overwritten"]
    run.finish(label, k, prepared, tolerated=lambda c, key: c == "changed" and key in run.page_keys or run.page_side_effects(c, key), restore=True,
               check=lambda applied, state: require(sorted(overwritten(applied)) == names, "Overwritten blocks differ from %s" % names))


def regenerate_existing(run):
    """Last: override=false over the existing DBs, the documented after-checkpoint failure; the outcome and its retained
    checkpoint are recorded, never replayed, and the checkpoint is restored."""
    h, k, label = run.h, "webserver_user_pages_generate", "generate user pages again without override"
    prepared = run.prepare(label, k, run.user_pages(False), {"effects": [{"action": "generateUserPageBlocks", "plcPath": run.plc, "option": "None", "overrideBlocks": []}]})
    def dispatch():
        row = {"kind": k, "changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "state": "dispatching"}
        h.report["mutations"].append(row)
        h.persist()
        outcome = h.client.request("tools/call", {"name": "change_apply", "arguments": h.apply_args(prepared)})
        status = h.tool("change_status", {"changeId": prepared["changeId"]})
        row["state"] = status["state"]
        failed = outcome.get("isError") is True
        require(status["state"] == ("recovery_required" if failed else "applied") and status["recoveryArchiveSha256"], "The apply outcome must retain its checkpoint: %s" % status["state"])
        return {"isError": failed, "error": (outcome.get("structuredContent") or {}).get("error"), "state": status["state"],
                "stages": status["stages"], "readbacks": status["readbacks"], "failure": status["failure"]}
    h.report["nativeBound"] = h.stage("apply " + label, dispatch)
    refused = h.client.request("tools/call", {"name": "change_restore", "arguments": {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"]}})
    if refused.get("isError"):  # Recorded, never retried; the retained checkpoint stays the recovery input.
        h.report["nativeBound"]["restoreRefusal"] = (refused.get("structuredContent") or {}).get("error")
        return
    run.restore(label, k, prepared)  # change_restore returns the restored change again; the recovered copy is verified here.


def pages(run):
    """Phase C, last because GenerateBlocks has no earlier native run: generate, reopen in a fresh Portal, regenerate with
    override and restore, then the after-checkpoint failure. A failure here is recorded under skipped and leaves only this
    kind unqualified; the other kinds' evidence is already complete."""
    try:
        names = user_pages(run)
        reopen(run)
        regenerate(run, names)
        regenerate_existing(run)
    except Exception as error:
        run.skipped["webserver_user_pages_generate"] = "Phase C failed: %s: %s" % (type(error).__name__, error)
        run.h.persist()


def create(run, name, restore=False):
    now = datetime.now(timezone.utc).replace(microsecond=0)
    request = {"usage": "WebServer", "subjectCommonName": name, "subjectAlternativeNames": [{"type": "Dns", "value": name.lower() + ".local"}],
               "validFromUtc": utc(now), "validUntilUtc": utc(now + timedelta(days=365))}
    label, ids, created = "create certificate " + name, run.store_ids(), {}
    prepared = run.prepare_store(label, "certificate_create", {**run.hw, **request}, request=request)
    def added(applied, state):
        readback = applied["readbacks"][0]
        created.update(readback["certificate"])
        require(readback["storeCertificateIds"] == sorted(ids + [created["certificateId"]]), "Store readback differs")
        return [run.cert(created["certificateId"])]
    def check(applied, state):
        row = state[run.cert(created["certificateId"])]
        require(row["subjectCommonName"] == name and row["hasPrivateKey"] is True, "Persisted certificate differs: %s" % row)
        run.identity("certificate_create", label, request, applied["readbacks"][0]["certificate"], row)
    run.finish(label, "certificate_create", prepared, added=added, check=check, restore=restore)
    return created["certificateId"]


def assign(run, current, wanted, restore=False, stale=False):
    state = run.current["view"]
    summary = lambda i: None if i is None else {"certificateId": i, "privateKeyPresent": state[run.cert(i)]["hasPrivateKey"], "publicIdentity": {"sha256": state[run.cert(i)]["sha256"]}}
    payload = {**run.hw, "targetPath": run.cpu, "attributeName": ATTRIBUTE, "certificateId": wanted, "expectedCertificateId": current}
    label, effect = "assign %s from %s to %s" % (ATTRIBUTE, current, wanted), {"targetPath": run.cpu, "attributeName": ATTRIBUTE, "before": summary(current), "after": summary(wanted)}
    duplicate = run.prepare_store(label + " (duplicate, made stale below)", "certificate_assign", payload, **effect) if stale else None
    prepared = run.prepare_store(label, "certificate_assign", payload, **effect)
    def check(applied, state):
        require(applied["readbacks"][0]["assignment"]["certificate"]["certificateId"] == wanted, "Change readback names another certificate")
        require(run.assigned() == wanted, "A fresh native read of %s differs from %s" % (ATTRIBUTE, wanted))
    run.finish(label, "certificate_assign", prepared, check=check, restore=restore)  # The attribute is display-only in certificate_inspect: no state key changes.
    if duplicate:
        run.refuse_apply("certificate_assign", "a preview made stale by the assignment", duplicate, "StaleRevision")


def delete(run, identifier, restore=False):
    row, ids = run.current["view"][run.cert(identifier)], run.store_ids()
    prepared = run.prepare_store("delete certificate %s" % identifier, "certificate_delete", {**run.hw, "certificateId": identifier},
                                 certificate={"certificateId": identifier, "privateKeyPresent": row["hasPrivateKey"], "publicIdentity": {"sha256": row["sha256"]}})
    run.finish("delete certificate %s" % identifier, "certificate_delete", prepared, removed=[run.cert(identifier)], restore=restore,
               check=lambda applied, state: require(applied["readbacks"][0]["storeCertificateIds"] == [i for i in ids if i != identifier], "Store readback differs"))


def upload(h, name, data):
    def run():
        started = h.tool("artifact_upload_begin", {"fileName": name, "expectedBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        h.tool("artifact_upload_chunk", {"uploadId": started["uploadId"], "offset": 0, "data": base64.b64encode(data).decode("ascii")})
        return h.tool("artifact_upload_commit", {"uploadId": started["uploadId"]})["artifactRef"]
    return h.stage("upload " + name, run)


def import_(run, der, reference, restore=False):
    sha, ids, created = hashlib.sha256(der).hexdigest(), run.store_ids(), {}
    payload, label = {**run.hw, "uploadRef": reference, "format": "der"}, "import certificate %s%s" % (sha[:12], " again" if restore else "")
    prepared = run.prepare_store(label, "certificate_import", payload, {**payload, "sha256": sha}, certificate={"sha256": sha, "privateKeyPresent": False})
    def added(applied, state):
        readback = applied["readbacks"][0]
        created.update(readback["certificate"])
        require(created["sha256"] == sha and created["privateKeyPresent"] is False and readback["storeCertificateIds"] == sorted(ids + [created["certificateId"]]),
                "Import readback differs: %s" % readback)
        return [run.cert(created["certificateId"])]
    def check(applied, state):
        run.identity("certificate_import", label, {"sha256": sha}, applied["readbacks"][0]["certificate"], state[run.cert(created["certificateId"])])
    run.finish(label, "certificate_import", prepared, added=added, check=check, restore=restore)


def certificates(run):
    """Phase A: refusals, create X, assign it and back, delete it, import its public DER, delete that, and create Y for phase B."""
    h, hw = run.h, run.hw
    first = run.assigned("an expectedCertificateId the attribute does not hold (drift)")
    require(first is not None and run.cert(first) in run.current["view"], "%s holds no certificate of the store: %s" % (ATTRIBUTE, first))
    refused = run.refuse("certificate_delete", "the certificate %s holds" % ATTRIBUTE, {**hw, "certificateId": first}, "CertificateInUse")
    require(any(r["source"] == ATTRIBUTE and r["certificateIds"] == [first] for r in refused["details"]["references"]), "CertificateInUse names other references")
    run.refuse("certificate_delete", "a missing certificate", {**hw, "certificateId": UNUSED_ID - 1}, "CertificateNotFound")
    now = utc(datetime.now(timezone.utc))
    request = {**hw, "usage": "WebServer", "subjectCommonName": "TiaMcpQualifyR", "subjectAlternativeNames": [], "validFromUtc": now, "validUntilUtc": "2099-01-01T00:00:00Z"}
    run.refuse("certificate_create", "usage None", {**request, "usage": "None"}, "InvalidRequest")
    run.refuse("certificate_create", "validUntil equal to validFrom", {**request, "validUntilUtc": now}, "InvalidRequest")
    run.refuse("certificate_create", "a time with an offset", {**request, "validFromUtc": now[:-1] + "+00:00"}, "InvalidRequest")
    run.refuse("certificate_create", "a field outside the payload", {**request, "issuer": "TiaMcpCa"}, "InvalidRequest")
    x = create(run, "TiaMcpQualifyX")
    swap = {**hw, "targetPath": run.cpu, "attributeName": ATTRIBUTE}
    run.refuse("certificate_assign", "an attribute not ending in Certificate", {**swap, "attributeName": "WebserverActivate", "certificateId": x, "expectedCertificateId": first}, "ProtectedAttribute")
    run.refuse("certificate_assign", "a missing certificate", {**swap, "certificateId": UNUSED_ID - 1, "expectedCertificateId": first}, "CertificateNotFound")
    run.refuse("certificate_assign", "the certificate already assigned", {**swap, "certificateId": first, "expectedCertificateId": first}, "InvalidRequest")
    assign(run, first, x, stale=True)
    refused = run.refuse("certificate_delete", "the newly assigned certificate", {**hw, "certificateId": x}, "CertificateInUse")
    require(any(r["source"] == ATTRIBUTE and r["certificateIds"] == [x] for r in refused["details"]["references"]), "CertificateInUse does not name the new assignment")
    assign(run, x, first)
    der = base64.b64decode(run.current["view"][run.cert(x)]["der"])
    delete(run, x)
    reference = upload(h, "tiamcp-qualify-x.cer", der)
    run.refuse("certificate_import", "format pem for DER bytes", {**hw, "uploadRef": reference, "format": "pem"}, "InvalidPublicCertificateReview")
    run.refuse("certificate_import", "a field outside the payload", {**hw, "uploadRef": reference, "format": "der", "trusted": True}, "InvalidPublicCertificateReview")
    pending = h.tool("artifact_upload_begin", {"fileName": "pending.cer", "expectedBytes": len(der), "sha256": hashlib.sha256(der).hexdigest()})
    run.refuse("certificate_import", "an uncommitted upload", {**hw, "uploadRef": "upload:" + pending["uploadId"], "format": "der"}, "UploadIncomplete")
    h.tool("artifact_upload_abort", {"uploadId": pending["uploadId"]})
    imported = import_(run, der, reference)
    delete(run, imported)
    return first, create(run, "TiaMcpQualifyY"), der, reference


def umac_set(run, label, payload, delta, preview_rows, prepared=None, restore=False, **domain):
    """umacAfter must differ from umacBefore in exactly delta, with preview_rows (or preview_rows(before)) as the edited
    entries, and umacBefore must list the roles access_inspect reads; then the readback must differ in exactly the domain keys."""
    prepared = prepared or run.prepare(label, "umac_role_set", payload, {"effects": [umac_effect(payload)]})
    before, after = (umac_keyed(prepared["preview"][name]) for name in ("umacBefore", "umacAfter"))
    rows = preview_rows(before) if callable(preview_rows) else preview_rows
    require(diff(before, after) == {"added": [], "removed": [], "changed": [], **delta} and all(matches(row, after.get(key)) for key, row in rows.items()),
            "umacAfter of %s differs: %s" % (label, diff(before, after)))
    require(sorted(k[5:] for k in before if k.startswith("role:")) == sorted(k[10:] for k in run.current["view"] if k.startswith("umac:role:")),
            "umacBefore roles differ from access_inspect")
    return run.finish(label, "umac_role_set", prepared, restore=restore, **domain)


def umac(run):
    k, role = "umac_role_set", "umac:role:" + ROLE
    run.refuse(k, "an unknown action", {"action": "delete", "roleName": ROLE}, "InvalidRequest")
    run.refuse(k, "create with a userName", {"action": "create", "roleName": ROLE, "userName": "TiaMcpNobody"}, "InvalidRequest")
    run.refuse(k, "grant without rights", {"action": "grant", "roleName": ROLE}, "InvalidRequest")
    created = run.prepare("create role " + ROLE, k, {"action": "create", "roleName": ROLE}, {"effects": [umac_effect({"action": "create", "roleName": ROLE})]})
    before = created["preview"]["umacBefore"]
    system = next(r["name"] for r in before["roles"] if r["system"])
    other = next(r["name"] for r in before["roles"] if not r["system"])
    run.right = next(r for x in before["roles"] if not x["system"] for r in x["deviceRights"])
    run.user = next(p["name"] for p in before["principals"] if p["kind"] == "projectUser" and p["active"])
    run.h.report["fixture"].update(umacDeviceRight=run.right, umacUser=run.user, umacSystemRole=system)
    grant = {"action": "grant", "roleName": ROLE, "deviceRights": [run.right]}
    revoke, user = {**grant, "action": "revoke"}, "umac:user:" + run.user
    run.refuse(k, "the system role " + system, {**grant, "roleName": system.upper()}, "ProtectedObject")
    run.refuse(k, "an existing custom role name in another case", {"action": "create", "roleName": other.swapcase()}, "ObjectExists")
    run.refuse(k, "a role that does not exist", grant, "ObjectNotFound")
    umac_set(run, "create role " + ROLE, None, {"added": ["role:" + ROLE]}, {"role:" + ROLE: {"identifier": None, "system": False, "engineeringRights": [], "deviceRights": []}},
             prepared=created, added=[role], rows={role: {"kind": "role", "rights": [], "inherits": []}})
    run.refuse(k, "revoking a right the role lacks", revoke, "ObjectNotFound")
    granted = [{"id": run.right["identifier"], "scope": run.right["device"]}]
    umac_set(run, "grant a device right to " + ROLE, grant, {"changed": ["role:" + ROLE]}, {"role:" + ROLE: {"engineeringRights": [], "deviceRights": [run.right]}},
             changed=[role], rows={role: {"rights": granted}})
    run.refuse(k, "granting an assigned right again", grant, "ObjectExists")
    assign = {"action": "assign", "roleName": ROLE, "userName": run.user}
    run.refuse(k, "an unknown project user", {**assign, "userName": "TiaMcpNobody"}, "ObjectNotFound")
    stale = run.prepare("revoke the device right (made stale below)", k, revoke, {"effects": [umac_effect(revoke)]})
    inherits, principal = run.current["view"][user]["inherits"], "projectUser:" + run.user
    umac_set(run, "assign %s to %s" % (ROLE, run.user), assign, {"changed": [principal]},
             lambda b: {principal: {"roles": sorted(b[principal]["roles"] + [b["role:" + ROLE]["identifier"]])}}, changed=[user], rows={user: {"inherits": sorted(inherits + [ROLE])}})
    run.refuse_apply(k, "a preview made stale by the assignment", stale, "StaleRevision")
    umac_set(run, "unassign %s from %s" % (ROLE, run.user), {**assign, "action": "unassign"}, {"changed": [principal]},
             lambda b: {principal: {"roles": [r for r in b[principal]["roles"] if r != b["role:" + ROLE]["identifier"]]}}, changed=[user], rows={user: {"inherits": inherits}})
    umac_set(run, "revoke the device right", revoke, {"changed": ["role:" + ROLE]}, {"role:" + ROLE: {"deviceRights": []}}, changed=[role], rows={role: {"rights": []}})
    return grant, granted


def workspace_create(run, name, root, restore=False):
    group, language = run.group, run.language
    path, payload = group + "/" + name, {"groupPath": group, "name": name, "rootPath": str(root), "language": language}
    prepared = run.prepare("create workspace " + name, "vci_workspace_create", payload,
                           {"effects": [{"action": "createWorkspace", "groupPath": group, "name": name, "language": language, "rootExists": False, "rootPath": str(root)}],
                            "vci": {"rootPath": str(root), "fileCount": 0, "writablePackages": [], "reportOnly": True}, "backups": []})
    run.finish("create workspace " + name, "vci_workspace_create", prepared, added=["vciWorkspace:" + path], restore=restore,
               rows={"vciWorkspace:" + path: {"name": name, "groupPath": group, "rootPath": str(root), "language": language, "mappedObjects": []}})
    return path


def prepare_delete(run, path, label):
    row = run.current["view"]["vciWorkspace:" + path]
    return run.prepare(label, "vci_workspace_delete", {"workspacePath": path},
                       {"effects": [{"action": "deleteWorkspace", "workspacePath": path, "language": row["language"], "mappingsRemoved": [], "rootPath": row["rootPath"]}],
                        "vci": {"rootPath": row["rootPath"], "fileCount": len(files_under(row["rootPath"])), "writablePackages": [], "reportOnly": True}})


def workspace_delete(run, path, restore=False):
    run.finish("delete workspace " + path, "vci_workspace_delete", prepare_delete(run, path, "delete workspace " + path), removed=["vciWorkspace:" + path], restore=restore)


def vci(run):
    k, group = "vci_workspace_create", run.group
    payload = lambda name, root: {"groupPath": group, "name": name, "rootPath": str(root), "language": run.language}
    full = run.root("full")
    full.mkdir()
    (full / "keep.txt").write_text("not empty", encoding="utf-8")
    run.refuse(k, "a root inside the artifact store", payload("TiaMcpQ1", run.h.directory / "vci-inside"), "VciUnsafeRoot")
    run.refuse(k, "a non-empty root", payload("TiaMcpQ1", full), "VciUnsafeRoot")
    run.refuse(k, "a drive root", payload("TiaMcpQ1", run.h.directory.anchor), "VciUnsafeRoot")
    run.refuse(k, "an unknown group", {**payload("TiaMcpQ1", run.root(1)), "groupPath": group + "/TiaMcpMissing"}, "VciGroupNotFound")
    run.refuse(k, "an inactive language", {**payload("TiaMcpQ1", run.root(1)), "language": "zu-ZA"}, "InvalidRequest")
    first = workspace_create(run, "TiaMcpQ1", run.root(1))
    run.refuse(k, "an existing workspace name", payload("TiaMcpQ1", run.root(2)), "VciWorkspaceExists")
    run.refuse(k, "a root inside another workspace root", payload("TiaMcpQ2", run.root(1) / "nested"), "VciRootInUse")
    run.refuse("vci_workspace_delete", "an unknown workspace", {"workspacePath": group + "/TiaMcpMissing"}, "VciWorkspaceNotFound")
    stale = prepare_delete(run, first, "delete workspace TiaMcpQ1 (root changed below)")
    drift = run.root(1) / "drift.txt"
    drift.write_text("changed after preview", encoding="utf-8")
    run.refuse_apply("vci_workspace_delete", "a root file added after the preview", stale, "StaleRevision")
    drift.unlink()
    workspace_delete(run, first)
    return workspace_create(run, "TiaMcpQ2", run.root(2))


def reopen(run):
    h, final = run.h, run.current
    h.stage("close the changed session", lambda: h.tool("tia_detach"))
    h.owned_portal("reopen")
    h.stage("reopen the saved project", lambda: h.reopen_project(run.project))
    run.current = run.b0 = run.snap("reopened")
    h.stage("reopened state equals every verified change",
            lambda: require(run.b0["view"] == final["view"], "Reopened state differs: %s" % diff(final["view"], run.b0["view"])) or len(final["view"]))
    def journals():
        states = {}
        for change in run.applied:
            status = h.tool("change_status", {"changeId": change})
            states[change] = status.get("state") or status["journal"]["state"]
        require(set(states.values()) == {"applied"}, "A journal does not read applied after the reopen: %s" % states)
        return states
    h.stage("change_status of every applied change after the reopen", journals)
    run.applied.clear()
    for kind, rows in list(run.coverage.items()):
        if rows.get("applied"):
            run.mark(kind, "persisted", "fresh Portal reopen")


def body(h):
    run = Run(h)
    run.plc = h.owned_scratch("access changes")
    run.cpu, run.language = run.plc.rsplit("/plc/", 1)[0], h.args.language
    run.project = h.tool("tia_session_status")["projects"][0]["path"]
    run.pages = h.directory / "web-pages"
    run.pages.mkdir()
    (run.pages / "index.html").write_text(PAGE, encoding="utf-8")
    run.current = run.snap("baseline")
    run.group = next(row["path"] for key, row in sorted(run.current["view"].items()) if key.startswith("vciGroup:") and row["kind"] == "system")
    h.report["fixture"] = {"plc": run.plc, "cpu": run.cpu, "certificateAttribute": ATTRIBUTE, "vciGroup": run.group, "language": run.language, "htmlDirectory": str(run.pages)}
    web_on(run)
    first, spare, der, reference = certificates(run)
    grant, granted = umac(run)
    workspace = vci(run)
    reopen(run)
    role = "umac:role:" + ROLE
    umac_set(run, "grant the device right again", grant, {"changed": ["role:" + ROLE]}, {"role:" + ROLE: {"deviceRights": [run.right]}},
             changed=[role], rows={role: {"rights": granted}}, restore=True)
    workspace_create(run, "TiaMcpQ3", run.root(3), restore=True)
    workspace_delete(run, workspace, restore=True)
    create(run, "TiaMcpQualifyZ", restore=True)
    assign(run, first, spare, restore=True)
    delete(run, spare, restore=True)
    import_(run, der, reference, restore=True)
    web_off(run)
    if "webserver_user_pages_generate" not in run.skipped:
        pages(run)
    kinds = [kind for kind in KINDS if kind not in run.skipped]
    gaps = coverage_gaps(run.coverage, kinds)
    h.report["qualified"] = [kind for kind in kinds if kind not in gaps and kind not in h.report["notQualified"]]
    require(not gaps, "Kinds without applied, persisted, refused and restored evidence: %s" % gaps)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--language", default="en-US", help="An active scratch project language for the VCI workspaces.")
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "ac_", "D12/X03/D01 certificate, UMAC, web server and VCI workspace changes in one owned scratch CPU 1511-1 PN", LIMITATIONS, []).run(body)
