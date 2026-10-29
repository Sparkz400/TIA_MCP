"""Independently establish (or retain native rejection of) duplicate tag setup.

The seeder is restricted to a newly created owned scratch. Production import
collision guards are unchanged. Native rejection is a blocked prerequisite,
never a typed ambiguity pass; no failed operation is replayed.
"""
import argparse
import hashlib
import pathlib

from qualification_owned_scratch import require, snapshot
from qualify_typed_resolution import ResolutionRun, artifact_policy
from run_conformance import safe_xml
from smoke_mcp import ROOT


def verify_native_setup(before, after, scope, created):
    old, new = before["objects"], after["objects"]
    additions = set(new) - set(old)
    require(not (set(old) - set(new)) and len(additions) == 1, "Duplicate fixture may add exactly one table and delete nothing.")
    require(all(old[key]["payloadSha256"] == new[key]["payloadSha256"] for key in old),
            "Native duplicate fixture changed pre-existing XML.")
    row = new[next(iter(additions))]
    require(row["kind"] == "tagTable" and row["name"] == "Tgp_ResolutionDuplicate", "Unexpected native addition.")
    tables = [table for table in after["inventory"]["tagTables"] if table["path"] == row["path"]]
    require(len(tables) == 1 and tables[0]["scopePath"] == scope, "Duplicate fixture scope differs.")
    document = safe_xml(pathlib.Path(row["file"]).read_bytes().decode("utf-8-sig"))
    require(document.findtext("SW.Tags.PlcTagTable/AttributeList/Name") == row["name"], "Native table identity differs.")
    tags = list(document.iter("SW.Tags.PlcTag"))
    require(len(tags) == int(created), "Native tag count differs from the observed seeder outcome.")
    if created:
        require(tags[0].findtext("AttributeList/Name") == "Ready"
                and tags[0].findtext("AttributeList/DataTypeName") == "Bool"
                and tags[0].findtext("AttributeList/LogicalAddress") == "%M101.0", "Native duplicate declaration differs.")
    return row


class TagAmbiguityRun(ResolutionRun):
    def __init__(self, args):
        super().__init__(args)
        path = pathlib.Path(__file__).resolve()
        self.report["harnessFiles"].append({"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
        self.persist()

    def setup(self):
        super().setup()
        before = snapshot(self.session.call, self.output / "duplicate-setup-before")
        row = self.resolution_seed("duplicate-tag-diagnostic")
        response = row["result"]
        after = snapshot(self.session.call, self.output / "duplicate-setup-after")
        row["addition"] = verify_native_setup(before, after, self.scope, response.get("duplicateCreated") is True)
        row["fullOriginalXmlPreserved"] = True
        row["outcome"] = "duplicate-created" if response.get("duplicateCreated") is True else "native-rejected"
        self.persist()
        require(response.get("duplicateCreated") is True,
                "Native API rejected duplicate Ready creation; typed native ambiguity remains unqualified. See retained nativeRejection.")
        require(response.get("compileErrors") == 0, "Native duplicate fixture did not compile cleanly; typed ambiguity qualification remains blocked.")

    def run_case(self, identifier):
        super().run_case(identifier)
        self.report["nativeDuplicateSetup"]["nativeAmbiguityQualified"] = True
        self.persist()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("host", "worker", "artifacts", "output", "resolution-seeder"):
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    parser.add_argument("--artifact-policy-root", type=pathlib.Path)
    parser.add_argument("--language", choices=("LAD", "FBD"), default="LAD")
    parser.add_argument("--timeout", type=int, default=300)
    args = parser.parse_args()
    if not 5 <= args.timeout <= 930:
        parser.error("--timeout must be within 5..930 seconds.")
    artifact_policy(args.artifacts, args.artifact_policy_root or (ROOT / "artifacts"))
    args.phase, args.case = "standard", ["tag-duplicate-name"]
    args.unit_seeder, args.namespace_unqualified = None, False
    return TagAmbiguityRun(args).run()


if __name__ == "__main__":
    raise SystemExit(main())
