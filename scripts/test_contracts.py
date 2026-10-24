"""Check cross-contract coverage and evidence integrity using only the standard library.

These checks complement JSON Schema documents; they are not a general JSON Schema implementation.
"""
import json
import pathlib
import re
import unittest

from generate_conformance import build_manifest, load_specifications, validate_manifest
from api_coverage import validate_feature_obligations

ROOT = pathlib.Path(__file__).resolve().parents[1]


def claim_errors(features, evidence, root=ROOT):
    """Return catalog claims that outrun the evidence retained in this checkout."""
    def retained(artifact):
        # Ignored output trees are never part of a published checkout.
        return not artifact.startswith(("artifacts/", "output/")) and (root / artifact).is_file()
    errors = []
    for identifier, feature in features.items():
        status = feature["qualification"]["status"]
        if status == "verified":
            missing = [a for proof in feature["qualification"]["evidenceIds"]
                       for a in evidence[proof]["artifacts"] if not retained(a)]
            if missing:
                errors.append(f"{identifier}: verified without retained artifacts {missing}")
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
        self.evidence = {"kept": {"artifacts": ["docs/README.md"]},
                         "ignored": {"artifacts": ["docs/README.md", "artifacts/run.json"]},
                         "absent": {"artifacts": ["docs/not-present.md"]}}

    def feature(self, status, proofs, availability="conditional"):
        return {"x": {"qualification": {"status": status, "evidenceIds": proofs}, "availability": {"status": availability}}}

    def test_verified_needs_every_cited_artifact_retained(self):
        self.assertEqual(claim_errors(self.feature("verified", ["kept"]), self.evidence), [])
        for proof in ("ignored", "absent"):
            with self.subTest(proof=proof):
                self.assertEqual(len(claim_errors(self.feature("verified", ["kept", proof]), self.evidence)), 1)

    def test_unverified_may_keep_historical_evidence_but_not_claim_available(self):
        self.assertEqual(claim_errors(self.feature("unverified", ["ignored"]), self.evidence), [])
        self.assertEqual(len(claim_errors(self.feature("unverified", [], "available"), self.evidence)), 1)

    def test_bundled_catalog(self):
        check_contracts()


if __name__ == "__main__":
    check_contracts()
