"""Native qualification of the production Unified text-list import route (change kind hmi_text_list_import).

For each selected profile, `HmiSeeder textlist-fixture` creates and archives an owned seeded Unified fixture (the
textlist-import baseline: two screens with items, a tag, the start screen, a discrete alarm, a Loaded script and the
documented bilingual peer list). Then, on the production Host with native workers:

1. Refusals: actual records published before the authoring refusals existed (content V21's import changes) and malformed
   selections are refused at the Host before any native call; a candidate whose languages differ from the project's is
   refused by the worker before any write.
2. Apply: an owned copy of the fixture is retrieved; each candidate is prepared (expectedContentHash from the first
   StaleRevision) and applied with checkpoint, compile and save. An independent oracle (PyYAML,
   qualify_hmi_text_list_import.resolve) compares hmi_inspect's native export with the candidate and the pre-import
   export. An idempotent replay, a stale preview and every other HMI node's typed readback are checked.
3. Restore: change_restore of the last import retrieves its checkpoint; the recovered text lists equal the pre-import ones.
4. Fresh reopen: a new Host and owned TIA open the saved project; the export equals the last applied state.
Interruption at each durable boundary is qualify_interruption.py --hmi-fixture with the tl-* scenarios.
"""
import argparse
import base64
import hashlib
import json
import pathlib
import subprocess
import uuid

from qualify_hmi_text_list_import import boundary_requests, canonical, evaluate_import, resolve
from qualify_reliability import ReliabilityClient, decoded, require
from smoke_mcp import ROOT

KIND = "hmi_text_list_import"
PROFILES = ("unified-comfort", "unified-pc")
RETAINED = {"author": ROOT / "fixtures/hmi-coordinated/retained-v1-v3", "set": ROOT / "fixtures/hmi-evolution/retained-v1-sets",
            "refused": ROOT / "fixtures/hmi/unified-text-list/retained-refused"}


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def install_retained(store):
    """Copy the actual prior Host records into the Host artifact root, byte-checked."""
    records = {}
    for name, root in RETAINED.items():
        manifest = json.loads((root / "fixture.json").read_text(encoding="utf-8"))
        for row in manifest["files"]:
            raw = (root / "store" / row["path"]).read_bytes()
            require(digest(raw) == row["sha256"], "Retained fixture byte changed: " + row["path"])
            target = store / row["path"]
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(raw)
        records[name] = manifest["records"]
    return records


def upload(client, data, name="request.json"):
    status = client.tool("artifact_upload_begin", {"fileName": name, "expectedBytes": len(data), "sha256": digest(data)})
    for offset in range(0, len(data), status["maxChunkBytes"]):
        client.tool("artifact_upload_chunk", {"uploadId": status["uploadId"], "offset": offset,
                                              "data": base64.b64encode(data[offset:offset + status["maxChunkBytes"]]).decode()})
    return client.tool("artifact_upload_commit", {"uploadId": status["uploadId"]})["artifactRef"]


def author(client, request):
    return client.tool("hmi_text_list_author", {"uploadRef": upload(client, json.dumps(request, ensure_ascii=False).encode("utf-8"))})["recordId"]


def unified_device(client):
    devices = [d for d in client.tool("hmi_inspect", {"request": {"section": "devices"}})["items"] if d.get("family") == "unified"]
    require(len(devices) == 1, "The text-list fixture holds exactly one Unified HMI")
    return devices[0]


def text_list_rows(client, device):
    rows = client.tool("hmi_inspect", {"request": {"section": "resources", "target": device["identity"], "limit": 200}})["items"]
    prefix = device["hmiPath"] + "/textLists/"
    return [r for r in rows if r["identity"]["objectPath"].startswith(prefix)]


def text_list_names(client):
    return {r["name"] for r in text_list_rows(client, unified_device(client))}


def native_lists(client, device=None):
    """Text lists by meaning, from hmi_inspect's native export (production read route), read by the PyYAML oracle."""
    device = device or unified_device(client)
    rows = text_list_rows(client, device)
    require(rows, "The fixture always holds at least the peer list")
    inspected = client.tool("hmi_inspect", {"request": {"section": "resources", "target": device["identity"],
                                                        "objectPath": rows[0]["identity"]["objectPath"], "includeSource": True}})
    files = {pathlib.Path(a["path"]).name: a for a in inspected["artifacts"] if a["path"].endswith(".hmi.yml")}
    library = next(name for name in files if name.endswith(".TextLibrary.hmi.yml"))
    configuration = next(name for name in files if name != library)
    require(len(files) == 2, "hmi_inspect exports exactly one configuration and its TextLibrary")
    raw = {name: pathlib.Path(a["path"]).read_bytes() for name, a in files.items()}
    require(all(digest(raw[name]) == files[name]["sha256"] for name in raw), "Native export changed after hmi_inspect")
    return resolve(raw[configuration], raw[library])


def typed_nodes(client, device):
    """path -> contentSha256 of every typed-model node outside the text lists (hmi_inspect section=model)."""
    nodes, offset = {}, 0
    while offset is not None:
        page = client.tool("hmi_inspect", {"request": {"section": "model", "target": device["identity"], "limit": 200, "offset": offset}})
        require(not page["coverage"]["truncated"], "Untruncated typed readback")
        for row in page["items"]:
            path = row["identity"]["objectPath"]
            if not path.startswith(device["hmiPath"] + "/textLists/"):
                nodes[path] = row["contentSha256"]
        offset = page["nextOffset"]
    return nodes


def summary(value):
    """Change views carry whole HMI manifests; the report keeps what the gates read and the journal path for the rest."""
    if not isinstance(value, dict) or "changeId" not in value:
        return value
    evidence = value.get("hmiEvidence") or {}
    return {"changeId": value["changeId"], "state": value.get("state"), "stages": value.get("stages"), "failure": value.get("failure"),
            "journalPath": value.get("journalPath"), "effects": (value.get("preview") or {}).get("effects"), "compile": value.get("compile"),
            "gate": evidence.get("gate"), "importReadback": evidence.get("importReadback"), "baselineCompile": evidence.get("baselineCompile"),
            "recoveredProject": value.get("recoveredProject")}


def prepare(client, device, record_id, output_id=None):
    """Two-step prepare: the first StaleRevision names the complete capture's contentHash, which the second binds."""
    payload = {"schemaVersion": 1, "target": device["identity"], "expectedContentHash": "0" * 64, "recordId": record_id}
    if output_id:
        payload["outputId"] = output_id
    stale = decoded(client.raw_tool("change_prepare", {"kind": KIND, "payload": payload}))
    require(stale.get("error", {}).get("code") == "StaleRevision", "An unbound prepare must stop at StaleRevision: " + json.dumps(stale)[:600])
    payload["expectedContentHash"] = stale["error"]["details"]["baseline"]["contentHash"]
    return client.tool("change_prepare", {"kind": KIND, "payload": payload})


def apply_arguments(prepared, key=None):
    return {"changeId": prepared["changeId"], "previewHash": prepared["previewHash"], "idempotencyKey": key or uuid.uuid4().hex,
            "checkpointSave": True, "compile": True, "save": True}


def candidate_lists(prepared):
    files = prepared["preview"]["payload"]["candidate"]["files"]
    raw = [pathlib.Path(f["path"]).read_bytes() for f in files]
    require(all(digest(r) == f["sha256"] for r, f in zip(raw, files)), "Candidate bytes differ from the prepared payload")
    return resolve(raw[0], raw[1])


def run_profile(args, profile, base, report):
    row = report["profiles"][profile] = {"checks": [], "failures": [], "steps": []}

    def gate(value, label, detail=None):
        (row["checks"] if value else row["failures"]).append(label if value or detail is None else f"{label}: {detail}")
        return value

    def step(name, value):
        row["steps"].append({"name": name, "result": summary(value)})
        print(f"  {profile}: {name}", flush=True)
        return value

    # The owned seeded fixture, archived before any import.
    fixture = base / ("f1" if profile == "unified-comfort" else "f2")
    with open(base / (fixture.name + ".log"), "wb") as log:
        seeded = subprocess.run([str(args.seeder.resolve()), "textlist-fixture", str(fixture), profile], stdout=log, stderr=subprocess.STDOUT, timeout=3600)
    native = json.loads((fixture / "native-report.json").read_text(encoding="utf-8"))
    row["seeder"] = {"exitCode": seeded.returncode, "completed": native.get("completed"), "selectedTarget": native.get("selectedTarget"),
                     "checkpoint": native.get("checkpoint"), "portals": native.get("portals")}
    if not gate(seeded.returncode == 0 and native.get("completed") is True, "Seeder created and archived the owned fixture; its Portal exited", native.get("portals")):
        return
    archive = pathlib.Path(native["checkpoint"]["path"])
    store = base / ("h1" if profile == "unified-comfort" else "h2")
    retained = install_retained(store)
    calls = row["toolCalls"] = []
    # The production gate ships with no profile; the qualification Host admits exactly this profile in its own retrieved copy.
    admission = ["--hmi-text-list-qualification", profile]
    client = ReliabilityClient(args.host.resolve(strict=True), args.worker.resolve(strict=True), store, timeout=900, record=calls, extra=admission)
    applied, history, project = [], [], None
    try:
        client.initialize()
        step("tia_start", client.tool("tia_start"))
        retrieved = step("project_retrieve fixture", client.tool("project_retrieve", {"archivePath": str(archive), "expectedSha256": native["checkpoint"]["sha256"]}))
        project = retrieved["project"]["path"]
        device = unified_device(client)
        gate(device["profile"] == profile, "The fixture's native profile is " + profile, device["profile"])
        baseline, baseline_nodes = native_lists(client, device), typed_nodes(client, device)
        gate(list(baseline) == ["QualificationStates"], "Baseline holds exactly the documented peer list", list(baseline))

        # 1. Refusals before any native write.
        for record in retained["refused"]:
            refused = decoded(client.raw_tool("change_prepare", {"kind": KIND, "payload": {"schemaVersion": 1, "target": device["identity"],
                                                                                            "expectedContentHash": "0" * 64, "recordId": record["recordId"]}}))
            gate(refused.get("error", {}).get("code") == record["expectedCode"], "Retained record whose content V21 changes is refused: " + record["expectedCode"], refused.get("error"))
        author_id = retained["author"][0]["recordId"]
        for label, payload, code in (
                ("an author record with outputId", {"recordId": author_id, "outputId": "run"}, "InvalidRequest"),
                ("an unknown record", {"recordId": "0" * 64}, "HmiTextListRecordNotFound"),
                ("a client-supplied candidate", {"recordId": author_id, "candidate": {}}, "InvalidRequest"),
                ("an unknown set output", {"recordId": retained["set"][0]["recordId"], "outputId": "missing"}, "HmiTextListCandidateUnavailable")):
            refused = decoded(client.raw_tool("change_prepare", {"kind": KIND, "payload": {"schemaVersion": 1, "target": device["identity"], "expectedContentHash": "0" * 64, **payload}}))
            gate(refused.get("error", {}).get("code") == code, "Refused at the Host: " + label, refused.get("error"))
        single = json.loads((ROOT / "fixtures/hmi/unified-text-list/author-request.json").read_text(encoding="utf-8"))
        single.update(languages=["en-US"], texts=[dict(t, translations={"en-US": t["translations"]["en-US"]}) for t in single["texts"]])
        mismatch = decoded(client.raw_tool("change_prepare", {"kind": KIND, "payload": {"schemaVersion": 1, "target": device["identity"],
                                                                                         "expectedContentHash": "0" * 64, "recordId": author(client, single)}}))
        code = mismatch.get("error", {}).get("code")
        if code == "StaleRevision":  # The capture check precedes the language check; bind the hash and retry.
            mismatch = decoded(client.raw_tool("change_prepare", {"kind": KIND, "payload": {"schemaVersion": 1, "target": device["identity"],
                "expectedContentHash": mismatch["error"]["details"]["baseline"]["contentHash"], "recordId": author(client, single)}}))
        gate(mismatch.get("error", {}).get("code") == "HmiTextListLanguageMismatch", "A candidate without every active project language is refused before any write", mismatch.get("error"))
        gate(native_lists(client, device) == baseline, "Refusals leave the text lists unchanged")

        # 2. Apply, in order: new lists, replacements of the peer list and of earlier imports, every record kind.
        base_request = json.loads((ROOT / "fixtures/hmi/unified-text-list/author-request.json").read_text(encoding="utf-8"))
        reordered = json.loads(json.dumps(base_request))
        reordered["languages"].reverse(); reordered["defaultLanguage"] = "de-DE"; reordered["lists"][0]["name"] = "Reordered Languages"
        base_id, boundary = author(client, base_request), boundary_requests()
        sequence = [("boundary-values", author(client, boundary["boundary-values"]), None), ("author-v1-base", base_id, None)]
        sequence += [(label, author(client, boundary[label]), None) for label in ("boundary-texts", "boundary-names")]
        sequence += [("author-v2-initial", retained["author"][1]["recordId"], None), ("author-v3-regenerated", retained["author"][4]["recordId"], None),
                     ("set-v1-run", retained["set"][0]["recordId"], "run"), ("set-v1-state", retained["set"][0]["recordId"], "state"),
                     ("languages-reordered", author(client, reordered), None)]
        stale = step("change_prepare stale probe", prepare(client, device, base_id))
        history.append(baseline)
        for label, record_id, output_id in sequence:
            prepared = step("change_prepare " + label, prepare(client, device, record_id, output_id))
            evidence = prepared["preview"]["hmiEvidence"]
            gate(evidence["gate"]["available"] is True and evidence["gate"]["targetProfile"] == profile and evidence["gate"]["admission"] == "qualification",
                 "Gate admits this kind and profile by qualification only: " + label)
            candidate, before = candidate_lists(prepared), history[-1]
            effect = prepared["preview"]["effects"][0]
            gate(sorted(effect["created"]) == sorted(set(candidate) - set(before)) and sorted(effect["replaced"]) == sorted(set(candidate) & set(before)),
                 "Preview names the created and replaced lists: " + label, effect)
            key = uuid.uuid4().hex
            result = step("change_apply " + label, client.tool("change_apply", apply_arguments(prepared, key)))
            stages = result["stages"]
            gate(result["state"] == "applied" and {"hmi_text_lists_read_back_exactly", "hmi_unrelated_content_preserved", "changed_project_saved"} <= set(stages),
                 "Applied with exact readback, preserved HMI content and save: " + label, {"state": result["state"], "stages": stages, "failure": result.get("failure")})
            gate(result["hmiEvidence"]["importReadback"]["exact"] is True, "Worker import readback exact: " + label)
            again = client.tool("change_apply", apply_arguments(prepared, key))
            gate(again["state"] == "applied" and again["stages"] == stages, "Replay with the same key returns the recorded outcome without a second import: " + label)
            after = native_lists(client, device)
            outcome = evaluate_import(before, after, candidate, True)
            gate(outcome["outcome"] == "exact", "Independent oracle: candidate lists exact, other lists unchanged: " + label,
                 {k: outcome[k] for k in ("lists", "peersChanged", "unexpectedLists")})
            applied.append((label, prepared))
            history.append(after)
            if len(history) == 2:
                refused = decoded(client.raw_tool("change_apply", apply_arguments(stale)))
                gate(refused.get("error", {}).get("code") == "StaleRevision", "A preview prepared before another saved import is refused", refused.get("error"))
                gate(json.loads(pathlib.Path(stale["journalPath"]).read_text(encoding="utf-8"))["state"] == "prepared", "The stale preview stays prepared and unclaimed")
        gate(typed_nodes(client, device) == baseline_nodes, "Every typed HMI node outside the text lists is unchanged by all imports")

        # 3. Restore the last import's checkpoint into a new directory; the saved original keeps the last state.
        label, last = applied[-1]
        restored = step("change_restore " + label, client.tool("change_restore", {"changeId": last["changeId"], "previewHash": last["previewHash"]}))
        gate(restored["state"] == "restored" and "hmi_recovered_content_equality_verified" in restored["stages"], "Checkpoint restored with HMI content equality", restored["stages"])
        gate(canonical(native_lists(client)) == canonical(history[-2]), "Restored copy holds the text lists before the last import")
        client.tool("tia_detach")
    except Exception as error:
        gate(False, "Route run completed", repr(error))
    finally:
        client.close()
        row["hostExitCode"] = client.process.returncode

    # 4. Fresh reopen of the saved original in a new Host and owned TIA.
    if project is None or len(history) < 2:
        row["passed"] = False
        return
    fresh = ReliabilityClient(args.host.resolve(strict=True), args.worker.resolve(strict=True), store, timeout=900, record=calls, extra=admission)
    try:
        fresh.initialize()
        step("fresh tia_start", fresh.tool("tia_start"))
        step("project_open saved original", fresh.tool("project_open", {"path": project}))
        gate(canonical(native_lists(fresh)) == canonical(history[-1]), "Fresh reopen: text lists equal the last applied state")
        gate(typed_nodes(fresh, unified_device(fresh)) == baseline_nodes, "Fresh reopen: every typed HMI node outside the text lists equals the baseline")
        fresh.tool("tia_detach")
    except Exception as error:
        gate(False, "Fresh reopen completed", repr(error))
    finally:
        fresh.close()
        row["freshHostExitCode"] = fresh.process.returncode
    row["passed"] = not row["failures"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path, required=True)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--seeder", type=pathlib.Path, required=True)
    parser.add_argument("--base", type=pathlib.Path, required=True, help="Short new artifact base; TIA limits project path length.")
    parser.add_argument("--profiles", nargs="+", choices=PROFILES, default=list(PROFILES))
    args = parser.parse_args()
    base = args.base.resolve()
    require(not base.exists(), "Fresh evidence directory required")
    require(len(str(base)) <= 50, "TIA refuses long project paths; choose a shorter base")
    base.mkdir(parents=True)
    report = {"schemaVersion": 1, "completed": False, "nativeExecuted": True, "runtimeVerified": False, "kind": KIND,
              "hostSha256": digest(args.host.read_bytes()), "workerSha256": digest(args.worker.read_bytes()), "seederSha256": digest(args.seeder.read_bytes()), "profiles": {}}
    for profile in args.profiles:
        print("RUN " + profile, flush=True)
        run_profile(args, profile, base, report)
        (base / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    report["completed"] = all(p.get("passed") for p in report["profiles"].values())
    (base / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({p: {"passed": r.get("passed"), "checks": len(r["checks"]), "failures": r["failures"]} for p, r in report["profiles"].items()}, ensure_ascii=False))
    return 0 if report["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
