"""Qualify project_upgrade (X01) natively on owned copies of public Siemens and community example projects.

Each example is first copied into this run's inputs directory and only that copy reaches the Host; the original is only
read, and hashed before and after the run. Every V14-V20 project folder or archive is upgraded by project_upgrade in its
own empty owned Portal: the result must name the expected source version, the pinned V21 project name, a conversion log
that records success and an unchanged source, the input copy must be unchanged and the upgrade's shared mutation admission
returned. The upgraded copy then reopens in a fresh owned Portal with the same device, PLC and block inventory, which must
equal the inventory pinned per example (block consistency is the compile state). An example V21 cannot upgrade must fail
with its pinned native reason, an unchanged source, its owned copy retained and its admission returned. A V13 archive is
refused before any native call: no admission and no upgrade directory.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil

from library_native_harness import Harness, require

# Public Siemens and community examples by file name, with what V21 does with them: the upgraded project's name and
# inventory (PLC paths below the project), or the native refusal. Pinned from the first native run (31 October 2026).
# Their paths come from --source and --refused, so no local folder name enters the repository, the report or the evidence.
S7_1200 = {"hardwareItems": 17, "plcs": ["devices/SIMATIC%201200%20station_1/items/PLC_1/plc/PLC_1"], "blocks": 7, "inconsistentBlocks": 0}
EXAMPLES = {
    "TinyMySQLClient_V14_1.zap14": {"version": "V14", "format": "archive", "project": "TinyMySQLClient_V14_V21", "inventory": S7_1200},
    "TinyMySQLClient_V15.ap15": {"version": "V15", "format": "project", "project": "TinyMySQLClient_V15_V21", "inventory": S7_1200},
    "DigitalTwinAtEducation_TIAProject_Basic_V16.ap16": {"version": "V16", "format": "project", "refusal": {
        "nativeType": "Siemens.Engineering.MissingProductsException", "text": "The TP700 Comfort 15.0.0.0 device type is no longer supported."}},
    "109482675_TimeSyn_Project_V15.1_V16.ap16": {"version": "V16", "format": "project", "project": "109482675_TimeSyn_Project_V15.1_V16_V21",
        "inventory": {"hardwareItems": 17, "plcs": ["devices/S71500%2FET200MP-Station_1/items/PLC_2/plc/PLC_2"], "blocks": 2, "inconsistentBlocks": 0}},
}
REFUSED = {"TinyMySQLClient.zap13": "V13"}
LIMITATIONS = [
    "Four public example projects: one V14 SP1 archive, one V15 and two V16 project folders. V15.1 and V17-V20 sources are not exercised.",
    "DigitalTwinAtEducation_TIAProject_Basic_V16 cannot be upgraded by V21: it contains a TP700 Comfort device of version 15.0.0.0, which V21 no longer supports (MissingProductsException); only that typed refusal is qualified for it.",
    "The pinned inventory is device, PLC and block counts and PLC paths; content is not compared with the older version, which needs that TIA version.",
    "Nothing is compiled: block consistency after the upgrade is recorded as TIA reports it.",
    "Protected (UMAC) projects, missing optional products and locked sources are not exercised.",
]


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def is_archive(path):
    return path.suffix.lower().startswith(".zap")


def fingerprint(path):
    """What a source holds: an archive's bytes, or every file (relative name and bytes) of a project file's folder."""
    if is_archive(path):
        return {"files": 1, "bytes": path.stat().st_size, "sha256": sha256(path)}
    files = sorted(p for p in path.parent.rglob("*") if p.is_file())
    digest = hashlib.sha256()
    for p in files:
        digest.update(p.relative_to(path.parent).as_posix().encode() + b"\0" + hashlib.sha256(p.read_bytes()).digest())
    return {"files": len(files), "bytes": sum(p.stat().st_size for p in files), "sha256": digest.hexdigest()}


def public(paths, table):
    """Every public example exactly once, by file name; anything else is refused before it is read."""
    names = sorted(p.name for p in paths)
    require(names == sorted(table), "Pass exactly these examples: %s (got %s)" % (sorted(table), names))
    require(all(p.is_file() for p in paths), "An example file is missing")
    return {p.name: p for p in paths}


def stage_input(run, original):
    """Copy an example into the run's inputs (an archive alone, a project with its whole folder); the copy must equal it."""
    target = run / "inputs" / original.stem
    if is_archive(original):
        target.mkdir(parents=True)
        shutil.copy2(original, target / original.name)
    else:
        shutil.copytree(original.parent, target)
    copy = target / original.name
    require(fingerprint(copy) == fingerprint(original), "The input copy differs from its example")
    return copy


def upgrade_view(result, run, spec):
    """Check one project_upgrade result against its example; keep names, sizes, hashes and paths relative to the run."""
    source, project = result.get("source") or {}, result.get("project") or {}
    require(result.get("upgraded") is True and result.get("compiled") is False, "Not upgraded: %s" % result.get("failure"))
    require((source.get("version"), source.get("format")) == (spec["version"], spec["format"]), "Source version or format differs: %s" % source)
    require(result.get("sourceUnchanged") is True, "The worker did not prove its source unchanged")
    path = Path(project.get("path") or "")
    require(path.suffix.lower() == ".ap21" and Path(run) in path.parents, "The upgraded project is not a V21 project inside this run: %s" % path)
    require(project.get("name") == spec["project"] == path.stem == path.parent.name, "The upgraded project is not %s: %s" % (spec["project"], path.name))
    logs = result.get("upgradeLogs") or []
    require(logs and all(log.get("succeeded") is True for log in logs), "TIA's conversion log does not record a successful project upgrade: %s" % logs)
    return {"version": spec["version"], "format": spec["format"], "sha256": source["sha256"], "sourceSha256": source["sourceSha256"],
            "project": {"name": project["name"], "path": path.relative_to(run).as_posix(), "isModified": project.get("isModified")},
            "logs": [{"name": Path(log["path"]).name, "bytes": log["bytes"], "sha256": log["sha256"],
                      "succeeded": log["succeeded"], "errors": log["errors"], "warnings": log["warnings"]} for log in logs]}


def inventory_view(inventory):
    """Devices, PLCs and blocks as project_inventory reports them; each block's consistency is the compile state."""
    require(inventory["completeness"]["truncated"] is False, "project_inventory is truncated")
    return {"hardware": sorted([row["path"], row.get("typeIdentifier")] for row in inventory["hardware"]),
            "plcs": sorted(row["path"] for row in inventory["plcs"]),
            "blocks": sorted([row["path"], row["isConsistent"]] for row in inventory["blocks"]),
            "warnings": sorted(inventory.get("warnings") or [])}


def refusal_view(error, run, spec):
    """A native refusal: its pinned reason, no upgraded project, an unchanged source and the owned copy retained in this run."""
    details, expected = error.get("details") or {}, spec["refusal"]
    require(error.get("code") == "ProjectUpgradeFailed" and details.get("nativeType") == expected["nativeType"] and expected["text"] in (error.get("message") or ""),
            "Expected the pinned refusal %s: %s" % (expected, {k: error.get(k) for k in ("code", "message")}))
    require(details.get("upgraded") is False and details.get("sourceUnchanged") is True and details.get("upgradedProjects") == [],
            "The refusal left an upgraded project or did not prove its source unchanged")
    destination = Path(details.get("destination") or "")
    require(Path(run) in destination.parents and destination.is_dir(), "The refused upgrade's owned copy is not retained inside this run")
    return {"version": spec["version"], "format": spec["format"], "code": error["code"], "nativeType": details["nativeType"], "reason": expected["text"],
            "sha256": details["source"]["sha256"], "sourceSha256": details["source"]["sourceSha256"], "ownedCopy": destination.relative_to(run).as_posix()}


def pinned(view):
    """What EXAMPLES pins: counts, and PLC paths below the /projects/<name>/ segment."""
    return {"hardwareItems": len(view["hardware"]), "plcs": [path.split("/", 3)[3] for path in view["plcs"]], "blocks": len(view["blocks"]),
            "inconsistentBlocks": sum(not consistent for _, consistent in view["blocks"])}


def admissions(run):
    return {folder.name for folder in (run / "engineering-admissions").glob("[0-9a-f]*") if folder.is_dir()}


def latest(run, admission):
    return json.loads(sorted((run / "engineering-admissions" / admission).glob("*.json"))[-1].read_text(encoding="utf-8"))


def released(run, before):
    """The upgrade added exactly one admission, it returned, and nothing is left unresolved."""
    new = sorted(admissions(run) - before)
    require(len(new) == 1, "Expected one new admission, found %s" % new)
    record = latest(run, new[0])
    require(record["scope"]["operation"] == "project_upgrade" and record["state"] == "returned", "Admission not returned: %s" % record["state"])
    unresolved = [a for a in admissions(run) if latest(run, a)["state"] in ("dispatchIntent", "outcomeUnknown")]
    require(not unresolved, "Unresolved admissions remain: %s" % unresolved)
    return {"admissionId": new[0], "state": record["state"], "resources": [r["effect"] for r in record["scope"]["resources"]]}


def unchanged(label, path, before):
    require(fingerprint(path) == before, label + " changed")
    return before


def refuse(h, original):
    """V13 is refused before any native call: no admission and no upgrade directory appear, the copy stays unchanged."""
    copy = Path(h.stage("copy %s into the run's inputs" % original.name, lambda: str(stage_input(h.directory, original))))
    before, known = fingerprint(copy), admissions(h.directory)
    h.owned_portal("refusal")
    error = h.stage("refuse the %s upgrade" % REFUSED[original.name], lambda: h.call_error("project_upgrade", {"path": str(copy), "expectedSha256": sha256(copy)}))
    require(error.get("code") == "ProjectVersionUnsupported", "Expected ProjectVersionUnsupported: %s" % error)
    require(admissions(h.directory) == known and not (h.directory / "upgrades").exists(), "The refusal created an admission or an upgrade directory")
    unchanged("The V13 input copy", copy, before)
    missing = h.stage("refuse a missing archive", lambda: h.call_error("project_upgrade", {"path": str(copy.with_suffix(".zap14")), "expectedSha256": "0" * 64}))
    require(missing.get("code") == "ProjectNotFound" and admissions(h.directory) == known, "Expected ProjectNotFound without an admission: %s" % missing)
    return {"error": {k: error.get(k) for k in ("code", "message")}, "inputCopy": before}


def upgrade(h, original):
    name, spec = original.name, EXAMPLES[original.name]
    copy = Path(h.stage("copy %s into the run's inputs" % name, lambda: str(stage_input(h.directory, original))))
    before = fingerprint(copy)
    h.stage("close the session before upgrading " + name, lambda: h.tool("tia_detach"))
    h.owned_portal("upgrade " + name)
    known = admissions(h.directory)
    wrong = h.stage("refuse %s with a wrong expectedSha256" % name, lambda: h.call_error("project_upgrade", {"path": str(copy), "expectedSha256": "0" * 64}))
    require(wrong.get("code") == "SourceChanged" and admissions(h.directory) == known, "Expected SourceChanged without an admission: %s" % wrong)
    if "refusal" in spec:
        error = h.stage("project_upgrade %s is refused by V21" % name, lambda: h.call_error("project_upgrade", {"path": str(copy), "expectedSha256": sha256(copy)}))
        view = h.stage("check the refusal of " + name, lambda: refusal_view(error, h.directory, spec))
        h.stage("the input copy of %s is unchanged" % name, lambda: unchanged("The input copy", copy, before))
        return {**view, "inputCopy": before, "admission": h.stage("the admission of %s returned" % name, lambda: released(h.directory, known))}
    result = h.stage("project_upgrade " + name, lambda: h.tool("project_upgrade", {"path": str(copy), "expectedSha256": sha256(copy)}))
    view = h.stage("check the upgrade of " + name, lambda: upgrade_view(result, h.directory, spec))
    h.stage("the input copy of %s is unchanged" % name, lambda: unchanged("The input copy", copy, before))
    admission = h.stage("the admission of %s returned" % name, lambda: released(h.directory, known))
    busy = h.stage("refuse a second upgrade while %s is open" % name, lambda: h.call_error("project_upgrade", {"path": str(copy), "expectedSha256": sha256(copy)}))
    require(busy.get("code") == "ProjectIsolationRequired", "Expected ProjectIsolationRequired: %s" % busy)
    at_upgrade = h.stage("inventory after the upgrade of " + name, lambda: inventory_view(h.tool("project_inventory", {"maxItems": 2000})))
    h.stage("close the upgrading session of " + name, lambda: h.tool("tia_detach"))
    h.owned_portal("reopen " + name)
    h.stage("reopen the upgraded " + name, lambda: h.reopen_project(str(h.directory / view["project"]["path"])))
    reopened = h.stage("inventory of the reopened " + name, lambda: inventory_view(h.tool("project_inventory", {"maxItems": 2000})))
    def same():
        require(reopened == at_upgrade, "The reopened inventory differs from the upgraded one")
        require(pinned(reopened) == spec["inventory"], "The inventory differs from the pinned one: %s" % pinned(reopened))
        return pinned(reopened)
    inventory = h.stage("the reopened %s has the pinned inventory" % name, same)
    return {**view, "inputCopy": before, "admission": admission, "inventory": inventory}


def paths_fit(run, names):
    """project_upgrade's own limit, checked before any TIA start: <run>/upgrades/u_<8>/[w/]<stem>_V21 within 143 characters
    (the runner copies each project folder under its file's stem)."""
    longest = max(len(str(run / "upgrades" / "u_12345678" / ("w" if is_archive(Path(name)) else "") / (Path(name).stem + "_V21"))) for name in names)
    require(longest <= 143, "Choose a shorter --artifacts path: the upgraded project directory would need %d characters" % longest)
    return longest


def body(h):
    h.report["longestUpgradeDirectory"] = paths_fit(h.directory, EXAMPLES)
    sources = public([Path(p) for p in h.args.source], EXAMPLES)
    refused = public([Path(h.args.refused)], REFUSED)
    originals = {**sources, **refused}
    before = {name: h.stage("hash the original " + name, lambda p=path: fingerprint(p)) for name, path in originals.items()}
    h.report["examples"] = {name: dict(before[name], version=EXAMPLES[name]["version"] if name in EXAMPLES else REFUSED[name]) for name in originals}
    h.report["refusal"] = refuse(h, refused["TinyMySQLClient.zap13"])
    results, failures = {}, []
    for name in sorted(sources):
        try:
            results[name] = upgrade(h, sources[name])
        except Exception as error:  # One example's failure must not hide the others; the run still fails below.
            results[name] = {"failure": str(error)}
            failures.append(name)
        h.report["upgrades"] = results
        h.persist()
    def originals_unchanged():
        for name, path in originals.items():
            unchanged(name, path, before[name])
        return len(originals)
    h.stage("every original example is unchanged", originals_unchanged)
    h.stage("every example upgraded and reopened or refused as pinned", lambda: require(not failures, "Failed: %s" % failures) or len(results))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--source", action="append", required=True, help="An example project file or archive; once per example in EXAMPLES")
    parser.add_argument("--refused", required=True, help="The V13 example archive")
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--run-native", action="store_true")
    args = parser.parse_args()
    args.runner = __file__
    Harness(args, "pu_", "X01 project_upgrade of public V14-V16 example projects and archives into V21 on owned copies", LIMITATIONS, [], scratch=False).run(body)
