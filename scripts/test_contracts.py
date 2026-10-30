"""Check cross-contract coverage and evidence integrity using only the standard library.

These checks complement JSON Schema documents; they are not a general JSON Schema implementation.
"""
import functools
import json
import pathlib
import re
import subprocess
import tempfile
import unittest

from generate_conformance import build_manifest, load_specifications, validate_manifest
from api_coverage import validate_feature_obligations

ROOT = pathlib.Path(__file__).resolve().parents[1]


# Evidence IDs containing HOST_ONLY ran the Host with native workers deliberately absent.
HOST_ONLY = ".hostonly."
NATIVE_GATES = {"AC-native", "AC-recovery", "AC-runtime", "AC-simulator-lifecycle", "AC-simulation-download"}
# Catalog records from before scripts/curate_evidence.py: no summary, so no source commit to check.
# claim_errors refuses them for any verified feature; every newer record must cite a curated summary.
LEGACY_UNCURATED = {
    "vm.ssh.recovery.20261008": "VM recovery reports under artifacts/ only",
}


def claim_errors(features, evidence, root=ROOT):
    """Return catalog claims that outrun the evidence retained in this checkout."""
    @functools.cache
    def on_history(commit):
        # Evidence PRs are merged, never squashed, so the frozen build commit stays reachable from HEAD.
        return bool(re.fullmatch(r"[0-9a-f]{40}", commit)) and subprocess.run(
            ["git", "merge-base", "--is-ancestor", commit, "HEAD"], cwd=root, capture_output=True).returncode == 0

    def retained(artifact):
        # Ignored output trees are never part of a published checkout.
        return not artifact.startswith(("artifacts/", "output/")) and (root / artifact).is_file()

    def summaries(proof):
        # Curated summaries (scripts/curate_evidence.py) are the committed form of native evidence.
        rows = [json.loads((root / a).read_text(encoding="utf-8")) for a in evidence[proof]["artifacts"]
                if a.startswith("evidence/") and retained(a)]
        return [r for r in rows if r.get("schema") == "tia-mcp.evidence-summary.v1" and r.get("evidenceId") == proof
                and r.get("outcome") == "passed" and r.get("runs") and all(run["completed"] for run in r["runs"])]
    errors = []
    for identifier, feature in features.items():
        status = feature["qualification"]["status"]
        if status == "verified":
            proofs = feature["qualification"]["evidenceIds"]
            missing = [a for proof in proofs for a in evidence[proof]["artifacts"] if not retained(a)]
            if missing:
                errors.append(f"{identifier}: verified without retained artifacts {missing}")
            passed = [summaries(proof) for proof in proofs]
            if not all(passed):
                errors.append(f"{identifier}: verified without a passed evidence summary for every cited record")
            for proof, rows in zip(proofs, passed):
                for source in (r.get("source") or {} for r in rows):
                    if source.get("dirty") is not False:
                        errors.append(f"{identifier}: {proof} was not curated from a clean worktree")
                    if not on_history(str(source.get("commit", ""))):
                        errors.append(f"{identifier}: {proof} source commit {source.get('commit')} is not an ancestor of HEAD")
            exercised = {tool for rows in passed for r in rows for run in r["runs"] for tool in run["toolsPassed"]}
            if set(feature["mcpTools"]) - exercised:
                errors.append(f"{identifier}: verified tools not exercised {sorted(set(feature['mcpTools']) - exercised)}")
            native_gates = NATIVE_GATES & set(feature.get("acceptanceCriteria", []))
            # Native gates need every bound tool exercised by native proofs; a Host-only record cannot stand in.
            native = {tool for proof, rows in zip(proofs, passed) if HOST_ONLY not in proof
                      for r in rows for run in r["runs"] for tool in run["toolsPassed"]}
            if native_gates and (all(HOST_ONLY in proof for proof in proofs) or set(feature["mcpTools"]) - native):
                errors.append(f"{identifier}: Host-only evidence cannot close {sorted(native_gates)}; "
                              f"tools without native proof {sorted(set(feature['mcpTools']) - native)}")
        if feature["availability"]["status"] == "available" and status != "verified":
            errors.append(f"{identifier}: available without verified qualification")
    return errors


def check_contracts():
    catalog = json.loads((ROOT / "contracts/feature-catalog.v1.json").read_text(encoding="utf-8"))
    assert catalog["schemaVersion"] == 1 and catalog["targetTiaVersions"] == [21]
    features = {row["featureId"]: row for row in catalog["features"]}
    registry = json.loads((ROOT / "contracts/api-coverage/registry.v1.json").read_text(encoding="utf-8"))
    validate_feature_obligations(registry, catalog["features"])
    evidence = {row["id"]: row for row in catalog["evidence"]}
    criteria = {row["id"] for row in catalog["criteria"]}
    assert len(features) == len(catalog["features"]), "Duplicate feature ID"
    assert len(evidence) == len(catalog["evidence"]), "Duplicate evidence ID"
    tools = []
    for source in (ROOT / "src/TiaMcp.Host").glob("*.cs"):
        tools.extend(re.findall(r'McpServerTool\(Name\s*=\s*"([^"]+)"', source.read_text(encoding="utf-8")))
    assert len(tools) == len(set(tools)), "Duplicate public tool"
    assert set(tools) <= features.keys(), "Public tool missing feature contract: " + str(set(tools) - features.keys())
    bindings = {tool for feature in features.values() for tool in feature["mcpTools"]}
    assert bindings == set(tools), "Catalog/tool binding mismatch"
    change_source = (ROOT / "src/TiaMcp.Openness.V21/OpennessSession.Changes.cs").read_text(encoding="utf-8")
    kinds = set(re.findall(r'case "([^"]+)":', change_source))
    # Some independently typed families dispatch before the legacy switch.
    kinds |= set(re.findall(r'if \(change.Kind == "([^"]+)"\) return Prepare\w+Mutation\(change, project\);', change_source))
    library_source = (ROOT / "src/TiaMcp.Openness.V21/OpennessSession.LibraryChanges.cs").read_text(encoding="utf-8")
    library_registry = re.search(r'LibraryChangeKinds\s*=\s*\{([^}]+)\}', library_source)
    assert library_registry, "Native library change-kind registry is missing"
    library_kinds = set(re.findall(r'"(library_[a-z_]+)"', library_registry.group(1)))
    assert library_kinds, "Empty native library change-kind registry"
    library_cases = set(re.findall(r'case "(library_[a-z_]+)":', library_source))
    assert library_kinds == library_cases, "Native library registry and implementation cases differ"
    dispatcher = (ROOT / "src/TiaMcp.Openness.V21/OpennessSession.cs").read_text(encoding="utf-8")
    for operation, prefix, handler in (("change_prepare", "library_", "LibraryChangePrepare"),
                                        ("change_apply", "lib-", "LibraryChangeApply"),
                                        ("change_status", "lib-", "LibraryChangeStatus"),
                                        ("change_restore", "lib-", "LibraryChangeRestore")):
        route = re.search(r'case "' + operation + r'":([^\n]+)', dispatcher)
        assert route and prefix in route.group(1) and handler + "(" in route.group(1), "Library route lost from shared dispatcher: " + operation
    kinds |= library_kinds
    recorded_kinds = {row["changeKind"] for row in features.values() if row["changeKind"]}
    assert recorded_kinds == kinds, "Prepared mutation kind lacks an exact contract"
    for identifier, feature in features.items():
        assert re.fullmatch(r"[a-z0-9_]+(?:\.[a-z0-9_]+)*", identifier), identifier
        assert feature["acceptanceCriteria"] and set(feature["acceptanceCriteria"]) <= criteria, identifier
        assert feature["api"]["status"] in {"available", "unavailable", "unknown"}, identifier
        assert feature["implementation"]["status"] in {"implemented", "partial", "not_implemented"}, identifier
        assert feature["qualification"]["status"] in {"verified", "unverified", "failed"}, identifier
        assert feature["availability"]["status"] in {"available", "conditional", "blocked", "unsupported", "unknown"}, identifier
        proofs = feature["qualification"]["evidenceIds"]
        assert set(proofs) <= evidence.keys(), identifier
        if feature["qualification"]["status"] != "unverified":
            assert proofs, "Observed qualification requires evidence: " + identifier
        if feature["qualification"]["status"] == "verified":
            assert all(evidence[proof]["status"] == "passed" for proof in proofs), identifier
        if feature["qualification"]["status"] == "failed":
            assert any(evidence[proof]["status"] == "failed" for proof in proofs), identifier
        if feature["implementation"]["status"] == "not_implemented":
            assert not feature["mcpTools"] and feature["qualification"]["status"] == "unverified", identifier
        if feature["changeKind"]:
            assert identifier == "change." + feature["changeKind"]
            assert {"AC-preview", "AC-native", "AC-recovery", "AC-preserve"} <= set(feature["acceptanceCriteria"])
    errors = claim_errors(features, evidence)
    assert not errors, "Catalog claim exceeds retained evidence: " + "; ".join(errors)
    cited = {proof for feature in features.values() for proof in feature["qualification"]["evidenceIds"]}
    assert evidence.keys() <= cited, "Catalog evidence cited by no feature; retire it to docs/history: " + ", ".join(sorted(evidence.keys() - cited))
    # Every curated summary stays traceable: a catalog record, or listed in a retired-records history file.
    retired = {row["id"] for path in (ROOT / "docs/history").glob("catalog-evidence-retired-*.json")
               for key in ("records", "summariesNeverCataloged") for row in json.loads(path.read_text(encoding="utf-8")).get(key, [])}
    for path in (ROOT / "evidence").glob("*.json"):
        summary = json.loads(path.read_text(encoding="utf-8"))
        if summary.get("schema") == "tia-mcp.evidence-summary.v1":
            assert summary["evidenceId"] in evidence.keys() | retired, "Summary in neither the catalog nor a retired list: " + path.name
    uncurated = {proof for proof, record in evidence.items()
                 if not any(a.startswith("evidence/") and (ROOT / a).is_file()
                            and (json.loads((ROOT / a).read_text(encoding="utf-8")).get("source") or {}).get("commit")
                            for a in record["artifacts"])}
    assert uncurated == LEGACY_UNCURATED.keys(), "Catalog records without a committed summary differ from LEGACY_UNCURATED: " + ", ".join(sorted(uncurated ^ LEGACY_UNCURATED.keys()))
    for proof in evidence.values():
        assert proof["status"] in {"passed", "failed", "blocked", "skipped"}
        assert proof["profile"] and proof["limitations"], "Evidence requires exact scope and limits"
        for artifact in proof["artifacts"]:
            path = pathlib.PurePosixPath(artifact)
            assert not path.is_absolute() and ".." not in path.parts and ":" not in artifact, artifact
    for schema in (ROOT / "contracts").glob("*.schema.json"):
        data = json.loads(schema.read_text(encoding="utf-8"))
        assert data["$schema"] == "https://json-schema.org/draft/2020-12/schema" and data["$id"]
    for specification in load_specifications("Tcf"):
        manifest = build_manifest(specification)
        validate_manifest(manifest)
        assert set(manifest["featureIds"]) <= features.keys(), manifest["fixtureId"]
    print(f"PASS Contract coverage: {len(tools)} MCP tools, {len(kinds)} mutation kinds, {len(features)} feature entries; evidence/criteria references valid.")


class ClaimErrorTests(unittest.TestCase):
    def setUp(self):
        # TemporaryDirectory also removes Git's read-only object files on Windows.
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = pathlib.Path(directory.name)
        self.git("init", "-q")
        self.git("commit", "-q", "--allow-empty", "-m", "base")
        self.source = {"commit": self.git("rev-parse", "HEAD"), "dirty": False}
        (self.root / "evidence").mkdir()
        (self.root / "docs").mkdir()
        (self.root / "docs/note.md").write_text("note", encoding="utf-8")
        self.evidence = {"prose": {"artifacts": ["docs/note.md"]},
                         "ignored": {"artifacts": ["evidence/kept.json", "artifacts/run.json"]},
                         "absent": {"artifacts": ["evidence/not-present.json"]}}
        for name, outcome in (("kept", "passed"), ("failing", "failed")):
            self.summary(name, name, outcome=outcome)

    def git(self, *arguments):
        identity = ["-c", "user.name=test", "-c", "user.email=test@example.invalid"]
        return subprocess.run(["git", *identity, *arguments], cwd=self.root, check=True, capture_output=True, text=True).stdout.strip()

    def summary(self, name, proof, tools=("block_read",), outcome="passed", source=None):
        (self.root / f"evidence/{name}.json").write_text(json.dumps({
            "schema": "tia-mcp.evidence-summary.v1", "evidenceId": proof, "outcome": outcome,
            "source": self.source if source is None else source,
            "runs": [{"completed": outcome == "passed", "toolsPassed": list(tools)}]}), encoding="utf-8")
        self.evidence[proof] = {"artifacts": [f"evidence/{name}.json"]}

    def feature(self, status, proofs, availability="conditional", tools=("block_read",)):
        return {"x": {"mcpTools": list(tools), "qualification": {"status": status, "evidenceIds": proofs},
                      "availability": {"status": availability}}}

    def errors(self, *arguments, **options):
        return claim_errors(self.feature(*arguments, **options), self.evidence, self.root)

    def test_verified_needs_a_passed_summary_for_every_cited_record(self):
        self.assertEqual(self.errors("verified", ["kept"]), [])
        for proof in ("ignored", "absent", "prose", "failing"):
            with self.subTest(proof=proof):
                self.assertTrue(self.errors("verified", ["kept", proof]))

    def test_verified_tools_must_be_exercised(self):
        self.assertEqual(len(self.errors("verified", ["kept"], tools=("block_read", "xref_query"))), 1)

    def test_host_only_evidence_cannot_close_native_gates(self):
        self.summary("hostonly", "local.hostonly.x")
        hosted = self.feature("verified", ["local.hostonly.x"])
        hosted["x"]["acceptanceCriteria"] = ["AC-identity", "AC-recovery"]
        self.assertEqual(len(claim_errors(hosted, self.evidence, self.root)), 1)
        hosted["x"]["acceptanceCriteria"] = ["AC-identity", "AC-preserve"]
        self.assertEqual(claim_errors(hosted, self.evidence, self.root), [])

    def test_native_gates_need_every_tool_from_a_native_proof(self):
        self.summary("hostonly", "local.hostonly.y", tools=("xref_query",))
        self.summary("native", "local.native.y", tools=("block_read", "xref_query"))
        mixed = self.feature("verified", ["kept", "local.hostonly.y"], tools=("block_read", "xref_query"))
        mixed["x"]["acceptanceCriteria"] = ["AC-identity", "AC-native"]
        self.assertEqual(len(claim_errors(mixed, self.evidence, self.root)), 1)
        mixed["x"]["qualification"]["evidenceIds"] = ["local.native.y", "local.hostonly.y"]
        self.assertEqual(claim_errors(mixed, self.evidence, self.root), [])

    def test_cited_summary_must_be_clean_and_on_head_history(self):
        orphan = self.git("commit-tree", "HEAD^{tree}", "-m", "unmerged")
        for case, source in (("dirty", dict(self.source, dirty=True)), ("unrecorded", {"commit": self.source["commit"]}),
                             ("orphan", dict(self.source, commit=orphan)), ("unknown", dict(self.source, commit="0" * 40)),
                             ("option", dict(self.source, commit="--all")), ("missing", {"dirty": False})):
            with self.subTest(case=case):
                self.summary("kept", "kept", source=source)
                self.assertEqual(len(self.errors("verified", ["kept"])), 1)

    def test_unverified_may_keep_historical_evidence_but_not_claim_available(self):
        self.assertEqual(self.errors("unverified", ["ignored"]), [])
        self.assertEqual(len(self.errors("unverified", [], "available")), 1)

    def test_bundled_catalog(self):
        check_contracts()


if __name__ == "__main__":
    check_contracts()
