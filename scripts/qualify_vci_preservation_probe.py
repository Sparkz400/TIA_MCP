"""Bounded sequential VCI preservation coordinator. Default prints a plan only.

--execute admits native work only after the six retained actual-MCP recovery
cases reverify. Failing VCI import profiles stay disabled; this never imports.
"""
import argparse
import copy
import ctypes
from datetime import datetime, timedelta, timezone
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

import psutil
from block_test_process import OwnedTiaProcessWatcher
from qualify_worker_startup_exact import identity
from smoke_mcp import Client
from retained_native_lifecycle import RetainedNativeTree, close_host_without_kill
from qualify_translation_category_probe import (StoreExclusiveWindow, StoreReadWindow,
    verify_recovery_gate, safe_path, ref, sha, save, require, utc, overlaps, resource_key)

REPO = Path(__file__).resolve().parents[1]
LANE = REPO / "artifacts/startup-unblock-20261015/vci"
BUILD = REPO / "artifacts/deployment-build/b274e08b806a44f4928e429f190b153d"
SOURCE_PATHS = [Path(__file__), *(REPO / "scripts" / name for name in (
    "qualify_translation_category_probe.py", "block_test_process.py", "smoke_mcp.py", "qualify_worker_startup_exact.py",
    "verify_watch_expansion.py", "verify_watch_workflow.py", "verify_translation_populated_category_probe.py", "retained_native_lifecycle.py")),
    REPO / "scripts/native/VciPreservationProbe/verify_probe.py"]
INPUT = {"kind": "reviewed-vci-bilingual-fixture-input", "provenance": "Exact inputs retained before native seed dispatch; independent of export output", "blocks": [
    {"name": name, "kind": kind, "number": 1, "comments": [
        {"culture": "de-DE", "text": "Prueftext " + name + " {0}"},
        {"culture": "en-GB", "text": "Verification text " + name + " {0}"}]}
    for name, kind in (("TiaMcp_IsEnabled", "FC"), ("TiaMcp_EnableGate", "FB"))]}


def freeze_binding(binary, manifest, source_names):
    """Require exact admitted executable, dependencies and source bytes."""
    manifest = safe_path(manifest); packet = json.loads(manifest.read_text(encoding="utf-8-sig"))
    rows = packet["files"]
    require(rows and len(rows) <= 64, "Bounded source/binary manifest required")
    bound = {}
    for row in rows:
        path = safe_path(row["path"])
        require(str(path).upper() not in bound and sha(path) == row["sha256"], "Manifest file changed/duplicate")
        bound[str(path).upper()] = row
    binary = safe_path(binary)
    require(str(binary).upper() in bound, "Executable not in reviewed manifest")
    for path in binary.parent.iterdir():
        if path.is_file() and path.suffix.lower() in {".exe", ".dll", ".config"}:
            require(str(path.resolve()).upper() in bound, "Executable dependency/config not frozen: " + str(path))
    sources = []
    for name in source_names:
        found = [row for row in rows if Path(row["path"]).name == name]
        require(len(found) == 1, "Exactly one source must be bound: " + name)
        sources.append(found[0])
    return {"binary": ref(binary), "manifest": ref(manifest), "sources": sources, "files": rows}


def filetime_iso(ticks):
    seconds, fraction = divmod(ticks, 10000000)
    value = datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=seconds)
    return value.strftime("%Y-%m-%dT%H:%M:%S") + f".{fraction:07d}Z"


def utc_ticks(value):
    """Compare exact 100ns .NET ISO times even when JSON trims trailing zeroes."""
    import re
    match = re.fullmatch(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?Z", value)
    require(match is not None, "Exact UTC timestamp required")
    base = datetime.strptime(match[1], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc)
    delta = base - datetime(1601, 1, 1, tzinfo=timezone.utc)
    return (delta.days * 86400 + delta.seconds) * 10000000 + int((match[2] or "").ljust(7, "0"))


def checked_json(artifact):
    path = safe_path(artifact["path"])
    require(sha(path) == artifact["sha256"], "Bound receipt hash differs")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def scan_overlays(roots, keys, owner=None):
    """Own durable overlay only; never fabricate/clear production Host journals."""
    observations = []
    for root in roots:
        folder = Path(root) / "isolated-vci-reservations"
        if not folder.exists(): continue
        safe_path(folder)
        intents = sorted(folder.glob("*-intent.json"))
        require(len(intents) <= 1000, "Overlay count bound exceeded")
        for path in intents:
            safe_path(path)
            record = json.loads(path.read_text(encoding="utf-8-sig"))
            require(record["kind"] == "isolated-vci-resource-reservation" and record["productionMutationAdmission"] is False and record["automaticReplay"] is False,
                    "Invalid isolated VCI reservation")
            owned = [resource_key(r["kind"], r["path"]) for r in record["resources"]]
            require(owned == record["resourceKeys"] and len(set(owned)) == len(owned) and 1 <= len(owned) <= 64, "Overlay scope differs")
            outcome_path = path.with_name(path.name.removesuffix("-intent.json") + "-outcome.json")
            completed = False
            if outcome_path.exists():
                safe_path(outcome_path)
                outcome = json.loads(outcome_path.read_text(encoding="utf-8-sig"))
                require(outcome["intentSha256"] == sha(path), "Overlay intent binding differs")
                if outcome["state"] == "verified":
                    require(not (path.parent.parent / "reservation-finalization-failed.json").exists(), "Reservation finalization failure remains unresolved")
                    final = checked_json(outcome["checksResult"])
                    public_result = checked_json(outcome["finalResult"])
                    require(public_result.get("completed") is True and public_result.get("parentReservationResolved") is True, "Final public run result did not pass")
                    checked_json(outcome["finalStoreIntent"])
                    require(checked_json(outcome["finalStoreClose"]).get("allClosed") is True, "Final parent store release did not complete")
                    review = require_review(checked_json(outcome["independentReview"]), restored=True)
                    completed = all(final.get(key) is True for key in ("completed", "preseedRestorationVerified", "postseedRestorationVerified", "originalUnchangedAfterRecovery", "freshReopenVerified")) and final.get("lifecycle", {}).get("completed") is True and review.get("allExportsReturned") is True
                    require(completed and final.get("nativeImportEnabled") is False, "Overlay verification is incomplete")
                    required_stages = {name + ".json" for name in ("seed-verified", "postseed-restoration", "preseed-restoration", "original-verified", "lifecycle-close")}
                    require(len(outcome["evidence"]) == len(required_stages) and {Path(a["path"]).name for a in outcome["evidence"]} == required_stages, "Overlay stage evidence is incomplete")
                    for artifact in outcome["evidence"]:
                        require(checked_json(artifact).get("completed") is True, "Overlay stage did not complete")
            owned_parent = bool(owner and safe_path(owner["intent"]["path"]) == path and sha(path) == owner["intent"]["sha256"] and
                record.get("coordinatorIdentity") == owner["identity"] and record.get("runNonce") == owner["nonce"] and record.get("parentReservation") is True)
            require(completed or owned_parent or not any(overlaps(a, b) for a in owned for b in keys), "Unresolved isolated VCI reservation: " + str(path))
            observations.append({"intent": ref(path), "completed": completed, "allowedCurrentCoordinatorParent": owned_parent})
    return observations


class RetainedHelper:
    """Borrow the exact CreateProcess handle owned by Popen; never reopen its PID."""
    def __init__(self, process, executable):
        from ctypes import wintypes
        self.process = process; self.handle = process._handle
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.k.GetProcessTimes.argtypes = [wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)]
        self.k.GetProcessTimes.restype = wintypes.BOOL
        self.k.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.k.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self.k.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        self.k.GetExitCodeProcess.restype = wintypes.BOOL
        times = [wintypes.FILETIME() for _ in range(4)]
        require(bool(self.k.GetProcessTimes(self.handle, *(ctypes.byref(t) for t in times))), "Original helper handle birth unavailable")
        ticks = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        length = wintypes.DWORD(32768); buffer = ctypes.create_unicode_buffer(length.value)
        require(bool(self.k.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(length))), "Original helper image unavailable")
        require(safe_path(buffer.value) == safe_path(executable), "Created helper executable differs")
        self.identity = {"pid": process.pid, "parentPid": os.getpid(), "created": ticks / 10000000 - 11644473600,
                         "processStartedAtUtc": filetime_iso(ticks), "exe": str(safe_path(executable)), "name": Path(executable).name}
        self.proof = {"identity": self.identity, "sameCreateProcessHandle": True, "birthAndImageVerified": True,
                      "capturedAtUtc": utc(), "processExitVerified": False, "handleClosed": False, "forcedTermination": False}

    def finish(self):
        from ctypes import wintypes
        code = self.process.wait(timeout=0)
        observed = wintypes.DWORD()
        require(bool(self.k.GetExitCodeProcess(self.handle, ctypes.byref(observed))) and observed.value == code, "Original helper exit code differs")
        self.proof.update(processExitVerified=True, exitCode=code, exitedAtUtc=utc())
        self.handle.Close()
        self.proof["handleClosed"] = self.handle.closed is True
        self.proof["completed"] = self.proof["processExitVerified"] and self.proof["handleClosed"]
        return copy.deepcopy(self.proof)


def retained_birth(watcher):
    """Use the original captured OS handle, preserving .NET's 100ns precision."""
    from ctypes import wintypes
    get = watcher.api.kernel.GetProcessTimes
    get.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.FILETIME), ctypes.POINTER(wintypes.FILETIME), ctypes.POINTER(wintypes.FILETIME), ctypes.POINTER(wintypes.FILETIME)]
    get.restype = wintypes.BOOL
    values = [wintypes.FILETIME() for _ in range(4)]
    require(bool(get(watcher._handle, *(ctypes.byref(v) for v in values))), "Retained handle birth query failed")
    return filetime_iso((values[0].dwHighDateTime << 32) | values[0].dwLowDateTime)


def native_xml(row):
    data = Path(row["path"]).read_bytes()
    require(sha(row["path"]) == row["sha256"] and len(data) == row["bytes"] <= 16 * 1024 * 1024, "Retained native XML differs")
    simple = data.upper().replace(b"\0", b"")
    require(b"<!DOCTYPE" not in simple and b"<!ENTITY" not in simple, "XML entities refused")
    root = ET.fromstring(data)
    require(root.tag == "Document", "Expected native Document")
    # Only non-engineering Document metadata is excluded. Every engineering
    # attribute, ID, text, namespace, child order and tail is compared exactly.
    nodes = [node for node in root if node.tag not in {"Engineering", "DocumentInfo"}]
    require(len(nodes) == 1 and nodes[0].tag.startswith("SW."), "Unknown native document shape")
    return nodes[0]


def tree(node):
    return (node.tag, tuple(sorted(node.attrib.items())), node.text,
            tuple((tree(child), child.tail) for child in node))


def fixture_snapshot(folder, label="before"):
    folder = Path(folder); data = json.loads((folder / (label + ".json")).read_text(encoding="utf-8-sig"))
    require(data.get("completeWithinBounds") is True, "Fixture inventory incomplete")
    peers = {key: copy.deepcopy(data[key]) for key in ("plcName", "languages", "watchGroups", "watchForceAccess")}
    for key in ("blocks", "tags", "types", "watchForceTables"):
        peers[key] = []
        for item in data[key]:
            require(safe_path(item["artifact"]["path"]).is_relative_to(safe_path(folder / label)), "Native artifact escapes its capture")
            item = copy.deepcopy(item); node = native_xml(item.pop("artifact")); item["nativeXml"] = tree(node)
            if key == "blocks":
                direct = {c["culture"]: c["text"] for c in item["comments"]}
                texts = {}
                compositions = node.findall("./ObjectList/MultilingualText[@CompositionName='Comment']")
                require(len(compositions) == 1 and set(compositions[0].attrib) <= {"ID", "CompositionName"} and len(compositions[0]) == 1 and compositions[0][0].tag == "ObjectList" and not compositions[0][0].attrib, "Unknown native comment composition")
                for entry in compositions[0][0]:
                    require(entry.tag == "MultilingualTextItem" and set(entry.attrib) <= {"ID", "CompositionName"} and entry.get("CompositionName") == "Items" and len(entry) == 1 and entry[0].tag == "AttributeList" and not entry[0].attrib, "Unknown native comment item")
                    text = entry[0]
                    require(len(text) == 2 and {c.tag for c in text} == {"Culture", "Text"} and all(not list(c) and not c.attrib for c in text), "Unknown/rich raw comment representation")
                    culture = text.findtext("Culture"); require(culture not in texts, "Duplicate XML comment culture")
                    texts[culture] = text.findtext("Text") or ""
                require(len(direct) == len(item["comments"]) and texts == direct, "Direct comment readback disagrees with raw XML")
            peers[key].append(item)
        require(len({(r["kind"], r["group"], r["name"]) for r in peers[key]}) == len(peers[key]) <= 32, "Duplicate/excessive native peers")
    require(peers["blocks"], "Empty block inventory")
    return peers


def fixture_vci(folder, label="before"):
    packet = json.loads((Path(folder) / (label + ".json")).read_text(encoding="utf-8-sig"))
    require(packet.get("completeWithinBounds") is True and "vci" in packet, "Complete native VCI fixture state missing")
    state = packet["vci"]
    require(set(state) == {"projectLanguageImportOptions", "workspaces"} and isinstance(state["projectLanguageImportOptions"], str) and isinstance(state["workspaces"], list) and len(state["workspaces"]) <= 64, "VCI fixture state malformed")
    require(len({json.dumps(w["identity"], sort_keys=True) for w in state["workspaces"]}) == len(state["workspaces"]), "Duplicate native workspace identity")
    for workspace in state["workspaces"]:
        require(set(workspace) == {"identity", "name", "root", "culture", "mappings"} and workspace["identity"] and isinstance(workspace["mappings"], list) and len(workspace["mappings"]) <= 128, "Incomplete workspace metadata")
        require(len({json.dumps(m["identity"], sort_keys=True) for m in workspace["mappings"]}) == len(workspace["mappings"]), "Duplicate native mapped-object identity")
        for mapping in workspace["mappings"]:
            require(set(mapping) == {"identity", "format", "directory", "fileName", "compareState"} and mapping["identity"], "Incomplete native mapping metadata")
    return state


def collector_vci(folder, label):
    packet = json.loads((Path(folder) / (label + ".json")).read_text(encoding="utf-8-sig"))
    return {key: packet[key] for key in ("projectLanguageImportOptions", "workspaces")}


def require_review(review, restored=False):
    require(review.get("boundedPeersUnchanged") is True and review.get("allExportsReturned") is True and review.get("nativeImportEnabled") is False,
            "Independent review reports incomplete exports/changed peers")
    if restored: require(review.get("reopenedSnapshotMatches") is True and review.get("restoredSnapshotMatches") is True, "Independent reopen/restoration review failed")
    return review


def seed_projection(value, *, selected_comment=False, english_activation=False):
    """Seed-only reviewed projection; export/reopen/recovery stay fully strict.

    Activating English adds empty MultilingualTextItems and shifts document ID
    numbering. Drop only empty English items, preserve UIds/code/relationships,
    and assign document IDs in traversal order. RefID is remapped consistently.
    """
    removed = []
    def english_item(node, *, target):
        if not english_activation or node[0] != "MultilingualTextItem": return False
        attributes = dict(node[1])
        if set(attributes) - {"ID", "CompositionName"} or attributes.get("CompositionName") != "Items": return False
        children = node[3]
        if len(children) != 1 or children[0][0][0] != "AttributeList" or children[0][0][1]: return False
        fields = children[0][0][3]
        if len(fields) != 2 or {n[0] for n, _ in fields} != {"Culture", "Text"}: return False
        if any(n[1] or n[3] for n, _ in fields): return False
        content = {n[0]: n[2] for n, _ in fields}
        return content["Culture"] == "en-GB" and (target or content["Text"] in (None, ""))
    def prune(node, ancestors=(), target=False, multilingual=False):
        tag, attributes, text, children = node
        target = target or (selected_comment and len(ancestors) == 2 and ancestors[-1] == "ObjectList" and tag == "MultilingualText" and dict(attributes).get("CompositionName") == "Comment")
        if tag == "MultilingualText": multilingual = dict(attributes).get("CompositionName") in {"Comment", "Title"}
        if target and tag == "Text" and ancestors[-2:] == ("MultilingualTextItem", "AttributeList"):
            require(not attributes and not children, "Rich target comment text refused")
            text = "<reviewed-target-comment-text>"
        keep = []
        for index, (child, tail) in enumerate(children):
            if tag == "ObjectList" and ancestors[-1:] == ("MultilingualText",) and multilingual and index == len(children) - 1 and english_item(child, target=target):
                require(keep and (tail is None or tail.isspace()) and (keep[-1][1] is None or keep[-1][1].isspace()), "Unknown English insertion whitespace/order")
                removed.append({"id": dict(child[1]).get("ID"), "parent": list(ancestors) + [tag], "index": index, "targetComment": target,
                    "closingIndentTransferredToPriorSibling": True})
                keep[-1] = (keep[-1][0], tail)
                continue
            keep.append((prune(child, ancestors + (tag,), target, multilingual), tail))
        return tag, attributes, text, tuple(keep)
    pruned = prune(value)
    if not english_activation: return pruned, 0, {"canonicalized": False, "removedEnglishItems": []}
    identifiers = {}
    def number(value):
        require(isinstance(value, str) and value and all(c in "0123456789abcdefABCDEF" for c in value), "Generated native document ID is invalid")
        return int(value, 16)
    def collect(node, position=()):
        attributes = dict(node[1])
        if "ID" in attributes:
            old = attributes["ID"]
            key = number(old); require(key not in identifiers, "Generated native document ID is ambiguous")
            identifiers[key] = {"original": old, "canonical": str(len(identifiers)), "position": list(position), "kind": node[0]}
        for index, (child, _) in enumerate(node[3]): collect(child, position + (index,))
    collect(pruned)
    removed_ids = [number(item["id"]) for item in removed]
    require(len(set(removed_ids)) == len(removed_ids) and not set(removed_ids).intersection(identifiers), "Removed English item ID collides with preserved native identity")
    references = []
    def remap(node):
        attributes = []
        for key, val in node[1]:
            if key in {"ID", "RefID"}:
                parsed = number(val); require(parsed in identifiers, "Unresolved native document ID reference")
                if key == "RefID": references.append({"original": val, "canonicalTarget": identifiers[parsed]["canonical"]})
                val = identifiers[parsed]["canonical"]
            attributes.append((key, val))
        return node[0], tuple(attributes), node[2], tuple((remap(c), tail) for c, tail in node[3])
    canonical = remap(pruned)
    return canonical, len(removed), {"canonicalized": True, "preservedIds": list(identifiers.values()), "references": references, "removedEnglishItems": removed}


def verify_seed(before, after):
    wanted = {b["name"]: b for b in INPUT["blocks"]}
    require(all(sum(row["name"] == name for row in before["blocks"]) == 1 for name in wanted), "Missing/ambiguous selected fixture blocks")
    require(set(before["languages"]["active"]) <= {"de-DE", "en-GB"} and "de-DE" in before["languages"]["active"], "Third/missing baseline culture")
    require(after["languages"] == {**before["languages"], "active": ["de-DE", "en-GB"]}, "Only English activation permitted")
    activation = "en-GB" not in before["languages"]["active"]
    for key in ("watchGroups", "watchForceAccess", "plcName"):
        require(before[key] == after[key], "Seed changed peer: " + key)
    projections = []
    def equal_projection(old, new, selected=False):
        a = seed_projection(old["nativeXml"], selected_comment=selected, english_activation=activation)
        b = seed_projection(new["nativeXml"], selected_comment=selected, english_activation=activation)
        require(a[0] == b[0], "Seed changed block beyond reviewed comments/empty English activation: " + old["name"])
        projections.append({"kind": old["kind"], "group": old["group"], "name": old["name"], "before": a[2], "after": b[2], "referencePreservingBijectionVerified": True})
    for key in ("tags", "types", "watchForceTables"):
        require(len(before[key]) == len(after[key]), "Seed changed peer: " + key)
        for old, new in zip(before[key], after[key]):
            require({k: v for k, v in old.items() if k != "nativeXml"} == {k: v for k, v in new.items() if k != "nativeXml"}, "Seed changed peer identity: " + key)
            equal_projection(old, new)
    require([(b["group"], b["name"]) for b in before["blocks"]] == [(b["group"], b["name"]) for b in after["blocks"]], "Seed changed block inventory")
    for old, new in zip(before["blocks"], after["blocks"]):
        if old["name"] not in wanted:
            expected_comments = copy.deepcopy(old["comments"])
            if activation and not any(c["culture"] == "en-GB" for c in expected_comments): expected_comments.append({"culture": "en-GB", "text": ""})
            expected_comments.sort(key=lambda c: c["culture"])
            require(new["comments"] == expected_comments and {k: v for k, v in old.items() if k not in {"comments", "nativeXml"}} == {k: v for k, v in new.items() if k not in {"comments", "nativeXml"}}, "Seed changed unrelated block identity/comments")
        else:
            require(new["group"] == "root" and new["number"] == 1 and new["comments"] == wanted[new["name"]]["comments"] and new["nativeXml"][0] == "SW.Blocks." + wanted[new["name"]]["kind"], "Exact input/native readback mismatch")
        equal_projection(old, new, old["name"] in wanted)
    return {"completed": True, "englishActivated": activation, "projections": projections,
            "allowedCompositions": ["Comment", "Title"], "rawEngineeringXmlUnchangedClaimed": False}


def verify_inventory_coverage(native, host):
    software = host["software"]
    for native_key, host_key in (("blocks", "blocks"), ("types", "types"), ("tags", "tagTables")):
        require(all(row["group"] == "root" for row in native[native_key]), "Only fresh root-group scratch fixture supported")
        names = [row["name"] for row in native[native_key]]
        require(len(set(names)) == len(names) and sorted(names) == sorted(row["name"] for row in software[host_key]),
                "Native/XML and complete Host peer inventory differ: " + native_key)
    return True


def inspection_payload(packet):
    require(not packet.get("warnings") and packet.get("completeness", {}).get("truncated") is False and packet.get("completeness", {}).get("scopeComplete") is True, "Inspection incomplete/warnings")
    result = copy.deepcopy(packet)
    for key in ("origin", "observedAtUtc", "processId"):
        result.pop(key, None)
    if "project" in result:
        project_path = result["project"].pop("path")
        def relocate(value):
            if isinstance(value, dict):
                for key, item in value.items():
                    if key == "projectPath":
                        require(item == project_path, "Foreign nested project identity")
                        value[key] = "<exact-bound-current-project>"
                    else: relocate(item)
            elif isinstance(value, list):
                for item in value: relocate(item)
        relocate(result)
    return result


class Coordinator:
    def __init__(self, args):
        self.args = args; self.root = safe_path(args.root)
        require(not self.root.exists() and len(str(self.root)) <= 50, "New short root (<=50 characters) required")
        self.host, self.worker = safe_path(args.host), safe_path(args.worker)
        require(self.host.is_relative_to(BUILD.resolve()) and self.worker.is_relative_to(BUILD.resolve()), "Actual frozen b274 Host/worker required")
        source_manifest = json.loads(safe_path(args.controller_manifest).read_text(encoding="utf-8-sig"))
        declared = {str(safe_path(r["path"])).upper(): r for r in source_manifest["files"]}
        require(len(declared) == len(source_manifest["files"]) and all(str(p.resolve()).upper() in declared and sha(p) == declared[str(p.resolve()).upper()]["sha256"] for p in SOURCE_PATHS), "Reviewed coordinator/dependency source bytes differ")
        self.source_rows = [declared[str(p.resolve()).upper()] for p in SOURCE_PATHS]
        self.collector = freeze_binding(args.collector, args.collector_manifest, ["Program.cs", "VciPreservationProbe.csproj"])
        self.fixture = freeze_binding(args.fixture_helper, args.fixture_manifest, ["Program.cs", "VciFixtureEvidence.csproj"])
        self.gate = verify_recovery_gate(args.recovery_gate, self.host, self.worker)
        self.root.mkdir(parents=True)
        self.stores = sorted({self.root, *(safe_path(p) for p in args.shared_state_root), *(safe_path(c["root"]) for c in self.gate["cases"])}, key=lambda p: str(p).upper())
        self.client = None; self.native = None; self.worker_watch = None; self.host_watch = None; self.tree = None; self.native_row = None
        self.helper_alive = False; self.calls = 0; self.lifetimes = []; self.identities = set()
        self.parent_reservation = None; self.overlay_owner = None; self.pending_helper = None
        self.coordinator_identity = identity(psutil.Process(os.getpid())); self.run_nonce = uuid.uuid4().hex
        self.write("recovery-gate-verified", self.gate); self.write("reviewed-input", INPUT)
        copied = self.root / "controller-source"; copied.mkdir()
        for row in self.source_rows: shutil.copyfile(row["path"], copied / Path(row["path"]).name)
        self.host_files = [ref(p) for p in self.host.parent.iterdir() if p.is_file() and p.suffix.lower() in {".exe", ".dll", ".json", ".config"}]
        self.write("admission", {"kind": "isolated-vci-preservation-coordinator", "actualMcpHost": True, "nativeSequential": True,
            "automaticReplay": False, "nativeImportEnabled": False, "host": ref(self.host), "worker": ref(self.worker), "hostFiles": self.host_files,
            "controllerManifest": ref(args.controller_manifest),
            "collector": self.collector, "fixtureHelper": self.fixture, "sources": self.source_rows, "storeRoots": [str(p) for p in self.stores],
            "productionMutationAdmission": False,
            "effects": "Create one owned scratch; seed two exact native bilingual comments; save; archive preseed and postseed separately; create twelve VCI export workspaces/save; fresh reopen; retrieve both checkpoints into new destinations; reopen unchanged original"})

    def write(self, label, value): return save(self.root / (label + ".json"), value)

    def begin_reservation(self, window):
        require(self.parent_reservation is None, "Parent reservation already exists")
        scan_overlays(self.stores, window.keys)
        folder = self.root / "isolated-vci-reservations"; folder.mkdir(exist_ok=True)
        path = folder / "run-intent.json"
        save(path, {"kind": "isolated-vci-resource-reservation", "parentReservation": True, "productionMutationAdmission": False,
            "resources": window.evidence["resources"], "resourceKeys": window.keys, "atUtc": utc(), "automaticReplay": False,
            "coordinatorIdentity": self.coordinator_identity, "runNonce": self.run_nonce,
            "effects": window.evidence["effects"], "retainedThrough": "All independent validation, both recovery/reopens, original verification and exact final lifecycle"})
        self.parent_reservation = path
        self.overlay_owner = {"intent": ref(path), "identity": self.coordinator_identity, "nonce": self.run_nonce}

    def complete_reservation(self, checks):
        require(self.parent_reservation is not None and checks.get("completed") is True and checks.get("lifecycle", {}).get("completed") is True, "Parent cannot close without all checks and cleanup")
        record = checked_json(self.overlay_owner["intent"])
        with StoreExclusiveWindow(self.stores, self.original, self.root / "final-reservation-locks", mode="readOnlyProbe", resources=record["resources"],
                effects="File-only verification of full VCI run evidence before resolving own isolated parent overlay; no native dispatch") as held:
            scan_overlays(self.stores, held.keys, self.overlay_owner)
            review = require_review(checked_json(ref(self.root / "independent-vci-review.json")), restored=True)
            require(all(checks.get(key) is True for key in ("preseedRestorationVerified", "postseedRestorationVerified", "originalUnchangedAfterRecovery", "freshReopenVerified")), "Incomplete parent verification")
            evidence = [ref(self.root / (name + ".json")) for name in ("seed-verified", "postseed-restoration", "preseed-restoration", "original-verified", "lifecycle-close")]
            for item in evidence: require(checked_json(item).get("completed") is True, "Parent evidence has incomplete stage")
            candidate = {"intentSha256": sha(self.parent_reservation), "state": "verified",
                "checksResult": ref(self.root / "checks-result.json"), "independentReview": ref(self.root / "independent-vci-review.json"),
                "evidence": evidence, "verifiedAtUtc": utc(), "productionMutationAdmission": False, "automaticReplay": False}
        candidate["finalStoreIntent"] = ref(self.root / "final-reservation-locks-intent.json")
        candidate["finalStoreClose"] = ref(self.root / "final-reservation-locks-close.json")
        require(checked_json(candidate["finalStoreClose"]).get("allClosed") is True, "Final parent store release did not complete")
        return candidate

    def load_oracle(self):
        spec = importlib.util.spec_from_file_location("vci_retained_oracle", REPO / "scripts/native/VciPreservationProbe/verify_probe.py")
        oracle = importlib.util.module_from_spec(spec); spec.loader.exec_module(oracle)
        return oracle

    def call(self, name, arguments=None):
        self.calls += 1; prefix = f"call-{self.calls:03}-{name}"
        self.write(prefix + "-intent", {"tool": name, "arguments": arguments or {}, "atUtc": utc(), "automaticReplay": False})
        print(name, flush=True)
        result = self.client.tool(name, arguments)
        self.write(prefix + "-return", result)
        return result

    def start(self):
        require(all(sha(r["path"]) == r["sha256"] for r in self.host_files + self.source_rows), "Frozen Host/controller changed")
        # Keep a partially initialized Client if reader startup is interrupted
        # after Popen already owns a Host. Assignment-after-construction loses it.
        self.client = Client.__new__(Client); client_error = None
        try: self.client.__init__(self.host, 240, ["--worker", str(self.worker), "--artifacts", str(self.root)])
        except BaseException as error: client_error = error
        if not getattr(self.client, "process", None):
            packet = {"hostCreationOutcomeUnknown": True, "automaticTermination": False,
                "dependentDispatchProhibited": True, "requiresRootReconciliation": True,
                "failure": type(client_error).__name__ + ": " + str(client_error), "atUtc": utc()}
            try: self.write("host-constructor-unknown", packet)
            except BaseException: pass
            try: print("BLOCKED_UNCAPTURED_HOST_CREATION " + json.dumps(packet), flush=True)
            except BaseException: pass
            while True:
                try: time.sleep(.2)
                except BaseException: pass
        # Retain the tracker object even if initial capture fails. No failure
        # may reduce cleanup to a later PID lookup or Client.close auto-kill.
        self.tree = RetainedNativeTree.__new__(RetainedNativeTree)
        try: self.tree.__init__(self.client.process, self.host, self.worker, self.root / "process-tree")
        except BaseException:
            self.tree.unknown = True
            raise
        self.host_id = self.tree.anchor["identity"]; self.host_watch = self.tree.anchor["watcher"]
        self.write("host-start", self.host_id)
        if client_error is not None: raise client_error
        self.client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "vci-preservation", "version": "1"}})
        self.client.notify("notifications/initialized")
        self.discovery = self.call("tia_discover")
        worker = self.tree.one("worker")
        self.worker_id = worker["identity"]; self.worker_watch = worker["watcher"]
        self.worker_iso = self.worker_watch.evidence["processStartedAtUtc"]
        self.write("worker-start", self.worker_id)
        self.start_native()

    def start_native(self):
        require(self.native is None, "Previous native lifetime remains")
        self.tree.begin_start()
        result = self.call("tia_start")
        require(result["ownsPortalProcess"] is True and result["projects"] == [], "Empty owned start required")
        row = self.tree.accept_start(result["processId"]); info = row["identity"]
        require(info["parentPid"] == self.worker_id["pid"] and info["name"] == "Siemens.Automation.Portal.exe", "Native exact worker ancestry differs")
        key = (info["pid"], info["created"]); require(key not in self.identities, "Native process identity reused")
        watcher = row["watcher"]
        self.identities.add(key); self.native = (info, watcher); self.native_row = row; self.native_iso = watcher.evidence["processStartedAtUtc"]
        self.write(f"native-{len(self.identities):02}-start", {"identity": info, "processStartedAtUtc": self.native_iso, "handle": watcher.evidence})

    def close_native(self):
        if self.native is None: return
        self.tree.await_capture_complete()
        info, watcher = self.native
        detach_error = None
        try:
            detached = self.call("tia_detach")
            require(detached.get("disposedOwnedPortalConnection") is True and detached.get("attached") is False, "Owned native detach returned false")
        except BaseException as error: detach_error = type(error).__name__ + ": " + str(error)
        # This never destroys a live handle. Timeout/cancellation reports unknown
        # and holds until that same original native handle proves terminal.
        proof = self.tree.close_record(self.native_row)
        self.lifetimes.append({"identity": info, "proof": proof}); self.native = None; self.native_row = None
        self.write(f"native-{len(self.identities):02}-exit", {"identity": info, "proof": proof, "completed": proof["completed"], "detachError": detach_error})
        require(detach_error is None and proof["completed"] is True and proof["exitCode"] == 0 and not proof["deadlineOrCancellationObserved"], "Unclean/unknown native exit: " + str(detach_error))

    def reopen(self, project):
        self.close_native(); self.start_native()
        opened = self.call("project_open", {"path": str(project)})
        require(opened["opened"] is True and safe_path(opened["project"]["path"]) == safe_path(project), "Exact project reopen differs")

    def await_helper_terminal_after_unknown(self, label, process, lifetime, error):
        """Remain inside the caller's store window; do not race Host/native close."""
        packet = {"identity": lifetime.identity if lifetime else {"pid": process.pid}, "automaticReplay": False,
            "helperMayStillRun": process.returncode is None, "storeLocksRetained": True, "originalCreateProcessHandleRetained": True,
            "hostCloseProhibited": True, "requiresRootReconciliation": True, "failure": type(error).__name__ + ": " + str(error), "atUtc": utc()}
        try: self.write(label + "-outcome-unknown", packet)
        except BaseException as receipt_error: packet["receiptFailure"] = str(receipt_error)
        try: print("BLOCKED_HELPER_CLEANUP " + json.dumps(packet), flush=True)
        except BaseException: pass
        # Timeout/cancellation cannot release store locks while the helper may
        # still own native ExclusiveAccess. Root sees the exact held identity.
        # Only an exit on the original Popen handle ends this hold; no PID kill.
        while process.returncode is None:
            try: process.wait(timeout=1)
            except subprocess.TimeoutExpired: continue
            except BaseException as interrupted:
                try: print("HELPER_HOLD_CONTINUES " + type(interrupted).__name__, flush=True)
                except BaseException: pass
        try:
            proof = lifetime.finish() if lifetime else {"completed": False, "exitCode": process.returncode, "reason": "Original handle birth/image capture failed"}
            if lifetime is None and hasattr(process, "_handle"): process._handle.Close()
            self.write(label + "-terminal-after-unknown", {"proof": proof, "outcomeRemainsUnknown": True, "sameStoreLocksHeldUntilTerminal": True, "atUtc": utc()})
        finally:
            self.helper_alive = False; self.pending_helper = None
        raise RuntimeError("Helper outcome remains unknown after exact terminal cleanup: " + label) from error

    def helper(self, binding, mode, label, project, extra, window):
        require(all(sha(row["path"]) == row["sha256"] for row in binding["files"]), "Frozen helper bytes changed")
        info, watcher = self.native
        require(identity(psutil.Process(info["pid"])) == info and watcher.api.wait(watcher._handle, 1) == 258, "Exact native owner lost")
        output = self.root / label
        admission = {"mode": mode, "binarySha256": binding["binary"]["sha256"], "artifactRoot": str(self.root), "outputDirectory": str(output),
            "projectPath": str(project), "plcName": "ScratchPLC", "processId": info["pid"], "processStartedAtUtc": self.native_iso,
            "ownedScratch": True, "sequentialNativeAdmission": True, "checkpointRetrievalFoundationPassed": True, "automaticReplay": False,
            "hostBinary": ref(self.host), "workerBinary": ref(self.worker), "sourceFiles": binding["sources"],
            "recoveryFoundationEvidence": ref(self.root / "recovery-gate-verified.json"), "storeWindowIntent": ref(str(window.prefix) + "-intent.json"), **extra}
        self.write(label + "-admission", admission)
        command = [binding["binary"]["path"], mode, str(self.root / (label + "-admission.json"))]
        observations = scan_overlays(self.stores, window.keys, self.overlay_owner)
        self.write(label + "-overlay-scan", {"sameStoreHandlesHeld": True, "observations": observations})
        require(self.parent_reservation is not None, "Whole-run parent reservation required before any helper")
        self.write(label + "-dispatch-intent", {"command": command, "admission": ref(self.root / (label + "-admission.json")), "atUtc": utc(), "automaticReplay": False})
        with (self.root / (label + "-stdout.txt")).open("x", encoding="utf-8") as out, (self.root / (label + "-stderr.txt")).open("x", encoding="utf-8") as err:
            process = subprocess.Popen(command, stdout=out, stderr=err, cwd=REPO)
            self.helper_alive = True; self.pending_helper = process; lifetime = None
            try:
                lifetime = RetainedHelper(process, binding["binary"]["path"])
                self.write(label + "-process", lifetime.proof)
                code = process.wait(timeout=240)
                proof = lifetime.finish()
            except BaseException as error: self.await_helper_terminal_after_unknown(label, process, lifetime, error)
            finally:
                if process.returncode is not None: self.helper_alive = False; self.pending_helper = None
        self.write(label + "-dispatch-return", {"identity": lifetime.identity, "exitCode": code, "atUtc": utc(), "automaticReplay": False, "proof": proof})
        require(proof["completed"] is True, "Helper retained handle cleanup failed")
        require(code == 0, "Native helper failed: " + label)
        result = json.loads((output / "result.json").read_text(encoding="utf-8-sig"))
        require(result["completed"] is True and result["mode"] == mode and safe_path(result["projectPath"]) == safe_path(project), "Helper receipt binding differs")
        return output, result

    def inventory(self, label, project):
        with StoreReadWindow(self.stores, project, self.root / (label + "-locks")) as held:
            directory, result = self.helper(self.fixture, "inventory", label, project, self.fixture_extra, held)
        return {"peers": fixture_snapshot(directory), "vci": fixture_vci(directory)}

    def host_peers(self, label):
        software = self.call("software_inspect", {"maxItems": 10000})
        require(len(software["softwareScopes"]) == 1 and software["softwareScopes"][0]["kind"] == "plc" and not software["technologyObjects"], "Only root PLC/no technology fixture supported")
        result = {"software": inspection_payload(software), "hardware": inspection_payload(self.call("hardware_inspect", {"maxItems": 10000}))}
        self.write(label, result); return result

    def snapshot(self, label, project, checkpoint):
        oracle = {"projectPath": str(project), "plcName": "ScratchPLC", "blocks": INPUT["blocks"]}
        self.write(label + "-oracle", oracle)
        with StoreReadWindow(self.stores, project, self.root / (label + "-locks")) as held:
            directory, _ = self.helper(self.collector, "snapshot", label, project, {"checkpoint": checkpoint, "oracle": ref(self.root / (label + "-oracle.json"))}, held)
        return directory

    def restore(self, role, checkpoint, expected, expected_vci, host_expected):
        self.close_native(); self.start_native()
        result = self.call("project_retrieve", {"archivePath": checkpoint["path"], "expectedSha256": checkpoint["sha256"]})
        require(result["retrieved"] is True and result["archive"]["sha256"] == checkpoint["sha256"], "Native checkpoint recovery differs")
        project = safe_path(result["project"]["path"])
        require(project != self.original and project.is_relative_to(self.root), "Recovery must use new owned destination")
        self.reopen(project)
        actual = self.inventory(role + "-inventory", project)
        require(actual == {"peers": expected, "vci": expected_vci} and self.host_peers(role + "-host-peers") == host_expected, "Recovered " + role + " peers/mappings/settings differ")
        self.write(role + "-restoration", {"completed": True, "checkpoint": checkpoint, "originalProjectPath": str(self.original), "restoredProjectPath": str(project), "freshReopen": True, "baseline": role, "vciMappingsAndSettingsMatched": True, "automaticReplay": False})
        return project

    def run(self):
        oracle = self.load_oracle()
        self.start()
        scratch = self.call("tia_create_scratch"); self.write("scratch", scratch)
        require(scratch["completed"] is True and scratch["processId"] == self.native[0]["pid"] and scratch["compile"]["errorCount"] == 0, "Scratch creation failed")
        self.original = safe_path(scratch["project"]["path"])
        require(self.original.is_relative_to(self.root), "Scratch escaped owned root")
        self.fixture_extra = {"scratchReceipt": ref(self.root / "scratch.json"), "reviewedInput": ref(self.root / "reviewed-input.json")}
        before_host = self.host_peers("preseed-host-peers")
        resources = [{"kind": "project", "path": str(self.original), "effect": "Seed native bilingual comments/save, VCI workspace creation/save; retain through restoration and unchanged-original verification"},
            {"kind": "workspace", "path": str(self.root), "effect": "Exact fresh owned run root: all archives, export workspace/direct-SD packages, helper captures and unique future recovery destinations"}] + [
            {"kind": "workspace", "path": str(self.root / "export" / "workspaces" / f"profile-{n:02}"), "effect": "One of exactly twelve VCI export packages"} for n in range(1, 13)]
        with StoreExclusiveWindow(self.stores, self.original, self.root / "seed-locks", mode="isolatedQualificationMutation", resources=resources,
                effects="Archive preseed; activate en-GB if missing; set exactly reviewed FC/FB de-DE/en-GB comments; save; archive postseed; no import/watch/force edit") as held:
            self.begin_reservation(held)
            seed_dir, seed = self.helper(self.fixture, "seed", "seed", self.original, self.fixture_extra, held)
        before, seeded = fixture_snapshot(seed_dir), fixture_snapshot(seed_dir, "after")
        before_vci, seeded_vci = fixture_vci(seed_dir), fixture_vci(seed_dir, "after")
        require(before_vci == seeded_vci, "Seed changed native VCI mappings/settings")
        verify_inventory_coverage(before, before_host)
        seed_comparison = verify_seed(before, seeded)
        require(self.host_peers("postseed-host-peers") == before_host, "Seed changed hardware/software metadata peers")
        preseed, postseed = seed["preseedCheckpoint"], seed["postseedCheckpoint"]
        require(preseed["path"] != postseed["path"] and preseed["role"] == "preseed" and postseed["role"] == "postseed", "Distinct checkpoint roles required")
        self.write("seed-verified", {"completed": True, "reviewedInput": self.fixture_extra["reviewedInput"], "preseedCheckpoint": preseed, "postseedCheckpoint": postseed,
            "semanticPeersPreservedOutsideReviewedSeedEffects": True, "rawPeerXmlUnchangedClaimed": False,
            "seedComparison": seed_comparison,
            "seedOnlyExpectedRepresentationChanges": "Empty en-GB MultilingualTextItems from activation and generated document ID renumbering; every UId, code value, other text, attribute and child order preserved",
            "exportReopenRecoveryUseStrictRawEngineeringTreeComparison": True})
        self.write("export-oracle", {"projectPath": str(self.original), "plcName": "ScratchPLC", "blocks": INPUT["blocks"]})
        resources = [{"kind": "project", "path": str(self.original), "effect": "Create twelve VCI workspaces/mappings and save"}] + [
            {"kind": "workspace", "path": str(self.root / "export" / "workspaces" / f"profile-{n:02}"), "effect": "Create one VCI workspace/config/export package"} for n in range(1, 13)]
        resources.append({"kind": "workspace", "path": str(self.root / "export" / "direct-sd"), "effect": "Two direct SimaticSD export packages"})
        with StoreExclusiveWindow(self.stores, self.original, self.root / "export-locks", mode="isolatedQualificationMutation", resources=resources,
                effects="Twelve VCI workspace exports plus two direct SD exports; save; no import/settings/language changes") as held:
            export_dir, _ = self.helper(self.collector, "export", "export", self.original, {"checkpoint": postseed, "oracle": ref(self.root / "export-oracle.json"), "workspacePrefix": "VCI_Preservation_01"}, held)
        require_review(oracle.review(export_dir))
        require(collector_vci(export_dir, "before") == seeded_vci, "Export collector baseline differs from postseed mappings/settings")
        exported_vci = collector_vci(export_dir, "after")
        exported_expected = {"peers": seeded, "vci": exported_vci}
        require(self.inventory("postexport-inventory", self.original) == exported_expected and self.host_peers("postexport-host-peers") == before_host, "VCI export changed non-VCI peers")
        self.reopen(self.original)
        reopened = self.snapshot("reopened", self.original, postseed)
        require(self.inventory("reopened-inventory", self.original) == exported_expected and self.host_peers("reopened-host-peers") == before_host, "Fresh reopen peers differ")
        restored = self.restore("postseed", postseed, seeded, seeded_vci, before_host)
        restored_dir = self.snapshot("restored-postseed", restored, postseed)
        review = require_review(oracle.review(export_dir, reopened, restored_dir), restored=True); self.write("independent-vci-review", review)
        self.restore("preseed", preseed, before, before_vci, before_host)
        self.reopen(self.original)
        final = self.snapshot("original-final", self.original, postseed)
        final_snapshot, final_peers = oracle.snapshot(final, "before")
        reopened_snapshot, reopened_peers = oracle.snapshot(reopened, "before")
        require(final_peers == reopened_peers and final_snapshot["workspaces"] == reopened_snapshot["workspaces"], "Original VCI mapping/peers changed during separate recovery")
        require(self.inventory("original-final-inventory", self.original) == exported_expected and self.host_peers("original-final-host-peers") == before_host, "Original peers changed during recovery")
        self.write("original-verified", {"completed": True, "projectPath": str(self.original), "exactPeersMappingsSettingsPreserved": True,
            "finalSnapshot": ref(final / "before.json"), "reopenedSnapshot": ref(reopened / "before.json"), "automaticReplay": False})
        self.close_native()
        require(self.call("tia_discover")["processes"] == self.discovery["processes"], "Preexisting discovery changed")
        return {"completed": True, "actualMcpHost": True, "preservationProbeCompleted": True, "preseedRestorationVerified": True, "postseedRestorationVerified": True,
            "originalUnchangedAfterRecovery": True, "freshReopenVerified": True, "nativeImportEnabled": False, "fullNativeQualification": False,
            "scope": "Bounded exact owned scratch, all blocks/types/tags/watch/force XML/access, configured hardware and software inspection, VCI mappings/settings", "automaticReplay": False}

    def execute(self):
        result = {"completed": False, "nativeImportEnabled": False, "automaticReplay": False}
        try: result = self.run()
        except BaseException as error: result["failure"] = type(error).__name__ + ": " + str(error)
        finally:
            try: close = self.close()
            except BaseException as error: close = {"completed": False, "error": str(error)}
            result["completed"] = result["completed"] and close["completed"]
            result["lifecycle"] = close
            self.write("checks-result", result)
            candidate = None
            if result["completed"]:
                try: candidate = self.complete_reservation(result)
                except BaseException as error:
                    result["completed"] = False; result["reservationFailure"] = str(error)
            result["parentReservationResolved"] = result["completed"]
            self.write("result", result)
            if candidate is not None:
                try:
                    candidate["finalResult"] = ref(self.root / "result.json")
                    save(self.parent_reservation.with_name("run-outcome.json"), candidate)
                except BaseException as error:
                    result = {**result, "completed": False, "parentReservationResolved": False, "reservationFailure": str(error)}
                    self.write("reservation-finalization-failed", result)
        return result

    def close(self):
        if self.helper_alive:
            return {"completed": False, "cleanupDeferred": True, "reason": "Exact helper still running; retained dispatch identity requires coordinator reconciliation; no new native run admitted"}
        errors = []
        if self.native:
            try: self.close_native()
            except BaseException as error: errors.append(str(error))
        close = close_host_without_kill(self.client, self.tree) if self.client else None
        exits = [] if close is None else [{"kind": e["role"], "identity": e["identity"], "proof": e["proof"]} for e in close["tree"]["exits"] if e["role"] in {"host", "worker"}]
        receipts = (close or {}).get("workerExits", [])
        passed = bool(close and close["tree"]["completed"] and close["closeInterrupted"] is None and not close["forcedHostStop"] and close["hostExitCode"] == 0 and close["stderrEofObserved"] and len(receipts) == 1 and len(exits) == 2 and
            receipts[0]["processId"] == self.worker_id["pid"] and receipts[0]["processExitVerified"] and receipts[0]["exitCode"] == 0 and receipts[0]["forcedTermination"] is False and utc_ticks(receipts[0]["startedAtUtc"]) == utc_ticks(self.worker_iso) and
            all(e["proof"].get("completed") is True and e["proof"].get("exitCode") == 0 for e in exits) and self.lifetimes and len(self.lifetimes) == len(self.identities) and not errors)
        return self.write("lifecycle-close", {"completed": passed, "close": close, "exits": exits, "nativeLifetimes": self.lifetimes, "errors": errors, "logs": self.client.errors if self.client else [], "automaticReplay": False})


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--execute", action="store_true")
    p.add_argument("--host", type=Path, default=BUILD / "published-host/TiaMcp.Host.exe")
    p.add_argument("--worker", type=Path, default=BUILD / "src/TiaMcp.Openness.V21/bin/Release/net48/TiaMcp.Openness.V21.exe")
    p.add_argument("--root", type=Path, required=True)
    p.add_argument("--controller-manifest", type=Path, default=LANE / "coordinator-source-manifest-04.json")
    p.add_argument("--collector", type=Path, required=True)
    p.add_argument("--collector-manifest", type=Path, required=True)
    p.add_argument("--fixture-helper", type=Path, required=True)
    p.add_argument("--fixture-manifest", type=Path, required=True)
    p.add_argument("--recovery-gate", type=Path, required=True)
    p.add_argument("--shared-state-root", type=Path, action="append", default=[])
    args = p.parse_args()
    if not args.execute:
        print(json.dumps({"nativeInvoked": False, "executablePlan": True, "root": str(args.root), "recoveryGateRequired": str(args.recovery_gate),
            "stages": ["six actual interruption cases independently reverified", "owned start/scratch", "preseed archive", "reviewed bilingual setters/save/readback", "postseed archive", "VCI export+all-peer wrap", "fresh reopen", "postseed retrieval/fresh reopen", "preseed retrieval/fresh reopen", "unchanged original reopen", "exact cleanup"], "nativeImportEnabled": False}, indent=2))
        return 0
    run = Coordinator(args); result = run.execute()
    print(json.dumps({"completed": result["completed"], "nativeImportEnabled": False, "failure": result.get("failure")}, indent=2))
    return 0 if result["completed"] else 1


if __name__ == "__main__": raise SystemExit(main())
