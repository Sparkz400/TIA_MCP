"""Offline candidate extraction stays separate from the deployment catalog."""
import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest import mock

import extract_typed_graphical_profiles as extractor


def network(language, part, pins, storage=None):
    """A captured FB shaped like a native export: Storage member, one network, each pin wired to an access."""
    member = '<Member Name="Storage" Datatype="%s" />' % storage if storage else ""
    wires = "".join('<Wire UId="%d"><IdentCon UId="%d" /><NameCon UId="5" Name="%s" /></Wire>' % (40 + i, 20 + i, name) for i, name in enumerate(pins))
    return ('<Document><SW.Blocks.FB ID="0"><AttributeList><Interface><Sections xmlns="%s"><Section Name="Static">%s</Section></Sections></Interface>'
            '</AttributeList><ObjectList><SW.Blocks.CompileUnit ID="3"><AttributeList><NetworkSource><FlgNet xmlns="%s"><Parts>%s</Parts>'
            '<Wires>%s</Wires></FlgNet></NetworkSource><ProgrammingLanguage>%s</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>'
            '</ObjectList></SW.Blocks.FB></Document>') % (extractor.INTERFACE, member, extractor.NET, part, wires, language)


def storage(system):
    return '<Document><SW.Blocks.InstanceDB ID="0"><AttributeList><InstanceOfName>%s</InstanceOfName></AttributeList></SW.Blocks.InstanceDB></Document>' % system


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name).resolve()
        patch = mock.patch.object(extractor, "ROOT", self.root)
        patch.start()
        self.addCleanup(patch.stop)
        self.deploy = self.root / "fixtures/typed-graphical"
        (self.deploy / "native").mkdir(parents=True)
        self.catalog = self.deploy / "profiles.v1.json"
        self.catalog.write_bytes(b"unchanged deployment catalog")
        self.deployed_native = self.deploy / "native/Tgp_TON_LAD.xml"
        self.deployed_native.write_bytes(b"unchanged deployment reference")
        self.stage = self.root / "artifacts/candidates"
        captured = self.root / "artifacts/capture"
        captured.mkdir(parents=True)
        self.source = self.deploy / "reference.s7dcl"
        self.source.write_text("independent authored source\n", encoding="utf-8")
        self.native = captured / "Tgp_TON_LAD.xml"
        self.native.write_text('<Document><SW.Blocks.FB><AttributeList><Namespace>Unit</Namespace>'
            '<Interface><Member Name="Storage" Datatype="IEC_TIMER" /></Interface></AttributeList>'
            '<SW.Blocks.CompileUnit><NetworkSource><Part Name="TON" UId="1" />'
            '<Wire><NameCon UId="1" Name="IN" /></Wire><Wire><NameCon UId="1" Name="PT" /></Wire></NetworkSource></SW.Blocks.CompileUnit>'
            '</SW.Blocks.FB></Document>', encoding="utf-8")
        self.report_path = captured / "capture.json"
        self.report = {"origin": "independently_authored_native_references", "builderUsed": False,
            "scratch": {"selectedCpu": {"typeIdentifier": "CpuFixture"}},
            "fixtures": [{"engineeringVerified": True, "sourcePath": str(self.source), "changeId": "fixture",
                "sha256": hashlib.sha256(self.source.read_text().encode()).hexdigest(),
                "exports": [{"name": "Tgp_TON_LAD"}]}],
            "calls": [{"tool": "block_read", "status": "passed", "result": {"block": {"name": "Tgp_TON_LAD"},
                "artifact": {"path": str(self.native), "sha256": hashlib.sha256(self.native.read_bytes()).hexdigest()}}}]}
        block = {"name": "Tgp_TON_LAD", "path": "/plc/Tgp_TON_LAD", "scopePath": "/plc", "identity": {"namespace": ""}}
        self.report["calls"][0]["result"]["block"] = block
        self.report["fixtures"][0].update(scopePath="/plc", namespace="")
        self.report["fixtures"][0]["exports"][0].update(block=block,
            sha256=self.report["calls"][0]["result"]["artifact"]["sha256"])
        self.write_report()

    def write_report(self):
        self.report_path.write_text(json.dumps(self.report), encoding="utf-8")

    def unchanged(self):
        self.assertEqual(b"unchanged deployment catalog", self.catalog.read_bytes())
        self.assertEqual(b"unchanged deployment reference", self.deployed_native.read_bytes())

    def capture(self, documents):
        """Replace the single fixture's exports with these native documents and their exact read receipts."""
        entry = self.report["fixtures"][0]
        entry["exports"], self.report["calls"] = [], []
        for name, xml in documents.items():
            path = self.native.parent / (name + ".xml")
            path.write_text(xml, encoding="utf-8")
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            block = {"name": name, "path": "/plc/" + name, "scopePath": "/plc", "identity": {"namespace": ""}}
            entry["exports"].append({"name": name, "block": block, "sha256": digest})
            self.report["calls"].append({"tool": "block_read", "status": "passed",
                                         "result": {"block": block, "artifact": {"path": str(path), "sha256": digest}}})
        self.write_report()

    def wave9(self):
        tonr = '<Part Name="TONR" Version="1.0" UId="5"><Instance Scope="%s" UId="6"><Component Name="%s" /></Instance><TemplateValue Name="time_type" Type="Type">Time</TemplateValue></Part>'
        return {
            "Tgp_EqReal_LAD": network("LAD", '<Part Name="Eq" UId="5"><TemplateValue Name="SrcType" Type="Type">Real</TemplateValue></Part>', ("pre", "in1", "in2", "out")),
            "Tgp_MoveDInt_FBD": network("FBD", '<Part Name="Move" UId="5"><TemplateValue Name="Card" Type="Cardinality">1</TemplateValue></Part>', ("en", "in", "out1")),
            # Upper-case on purpose: the profile keeps whatever casing the capture shows.
            "Tgp_AddInt_LAD": network("LAD", '<Part Name="ADD" UId="5"><TemplateValue Name="SrcType" Type="Type">Int</TemplateValue></Part>', ("en", "in1", "in2", "out")),
            "Tgp_TONR_LAD": network("LAD", tonr % ("LocalVariable", "Storage"), ("IN", "R", "PT", "ET", "Q"), "TONR_TIME"),
            "Tgp_GlobalTonr": storage("IEC_TIMER"),
            "Tgp_TONR_Global_LAD": network("LAD", tonr % ("GlobalVariable", "Tgp_GlobalTonr"), ("IN", "R", "PT", "ET", "Q")),
            "Tgp_R_TRIG_FBD": network("FBD", '<Part Name="R_TRIG" Version="1.0" UId="5"><Instance Scope="LocalVariable" UId="6"><Component Name="Storage" /></Instance></Part>',
                                      ("en", "CLK", "Q"), "R_TRIG")}

    def test_wave9_families_become_inactive_candidates_with_reviewed_pin_contracts(self):
        self.capture(self.wave9())
        result, _ = extractor.extract_catalog([self.report_path], stage_output=self.stage)
        profiles = {profile["id"]: profile for profile in result["profiles"]}
        self.assertEqual({"v21.fw41.lad.compare.eq.real.v1", "v21.fw41.fbd.move.dint.v1", "v21.fw41.lad.math.ADD.int.v1",
                          "v21.fw41.lad.timer.TONR.time.v1", "v21.fw41.fbd.edge.R_TRIG.v1"}, set(profiles))
        pins = lambda key: [(p["name"], p["direction"], p["dataType"], p["required"]) for p in profiles[key]["pins"]]
        self.assertEqual([("pre", "input", "Bool", True), ("in1", "input", "Real", True), ("in2", "input", "Real", True), ("out", "output", "Bool", True)],
                         pins("v21.fw41.lad.compare.eq.real.v1"))
        move = profiles["v21.fw41.fbd.move.dint.v1"]
        self.assertEqual(("move", None, "DInt", "Move"), (move["kind"], move["instruction"], move["dataType"], move["nativeName"]))
        self.assertEqual([("en", "input", "Bool", True), ("eno", "output", "Bool", False), ("in", "input", "DInt", True), ("out1", "output", "DInt", False)],
                         pins("v21.fw41.fbd.move.dint.v1"))
        add = profiles["v21.fw41.lad.math.ADD.int.v1"]
        self.assertEqual(("math", "ADD", "Int", "ADD"), (add["kind"], add["instruction"], add["dataType"], add["nativeName"]))
        self.assertEqual(["en", "eno", "in1", "in2", "out"], [p["name"] for p in add["pins"]])
        tonr = profiles["v21.fw41.lad.timer.TONR.time.v1"]
        self.assertEqual(["IN", "R", "PT", "ET", "Q"], [p["name"] for p in tonr["pins"]])
        self.assertEqual(("TONR_TIME", "IEC_TIMER"), (tonr["instanceType"], tonr["globalInstanceType"]))
        global_hash = next(e["sha256"] for e in self.report["fixtures"][0]["exports"] if e["name"] == "Tgp_GlobalTonr")
        self.assertEqual(global_hash, tonr["globalInstanceSourceSha256"])
        edge = profiles["v21.fw41.fbd.edge.R_TRIG.v1"]
        self.assertEqual(("edge", "R_TRIG", None, "R_TRIG"), (edge["kind"], edge["instruction"], edge["dataType"], edge["instanceType"]))
        self.assertEqual(["en", "eno", "CLK", "Q"], [p["name"] for p in edge["pins"]])
        self.assertNotIn("globalInstanceType", edge)  # no global R_TRIG reference was captured
        for profile in profiles.values():
            for key in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified"):
                self.assertFalse(profile[key])
        self.unchanged()

    def test_pin_contract_mismatch_or_wrong_global_storage_type_stops_extraction(self):
        for ordinal, (name, change, message) in enumerate((
                ("Tgp_AddInt_LAD", lambda xml: xml.replace('Name="out" />', 'Name="in3" />'), "differs from the pins wired in Tgp_AddInt_LAD: en, in1, in2, in3"),
                ("Tgp_R_TRIG_FBD", lambda xml: xml.replace('Name="en" />', 'Name="Q" />'), "differs from the pins wired in Tgp_R_TRIG_FBD: CLK, Q"),
                ("Tgp_GlobalTonr", lambda xml: xml.replace("IEC_TIMER", "IEC_LTIMER"), "not one IEC_TIMER instance"))):
            with self.subTest(name=name):
                documents = self.wave9()
                documents[name] = change(documents[name])
                self.capture(documents)
                with self.assertRaisesRegex(ValueError, message):
                    extractor.extract_catalog([self.report_path], stage_output=self.stage / str(ordinal))
        self.unchanged()

    def test_edge_global_storage_from_an_instance_db_spec_binds_the_edge_profile(self):
        documents = self.wave9()
        documents["Tgp_GlobalRTrig"] = storage("R_TRIG")
        documents["Tgp_R_TRIG_Global_FBD"] = network("FBD", '<Part Name="R_TRIG" Version="1.0" UId="5"><Instance Scope="GlobalVariable" UId="6">'
                                                     '<Component Name="Tgp_GlobalRTrig" /></Instance></Part>', ("en", "CLK", "Q"))
        self.capture(documents)
        spec = self.deploy / "global-rtrig-storage.instancedb.json"
        spec.write_text('{"name": "Tgp_GlobalRTrig", "instanceOfName": "R_TRIG"}\n', encoding="utf-8")
        entry = self.report["fixtures"][0]
        export = next(e for e in entry["exports"] if e["name"] == "Tgp_GlobalRTrig")
        entry["exports"].remove(export)
        self.report["fixtures"].append({**entry, "sourcePath": str(spec), "changeId": "spec", "authoredFormat": "instance_db_spec",
                                        "sha256": hashlib.sha256(spec.read_text().encode()).hexdigest(), "exports": [export]})
        self.write_report()
        result, _ = extractor.extract_catalog([self.report_path], stage_output=self.stage)
        edge = next(p for p in result["profiles"] if p["id"] == "v21.fw41.fbd.edge.R_TRIG.v1")
        self.assertEqual(("R_TRIG", "R_TRIG", export["sha256"]), (edge["instanceType"], edge["globalInstanceType"], edge["globalInstanceSourceSha256"]))
        record = next(s for s in result["sources"] if s["path"].endswith("/Tgp_GlobalRTrig.xml"))
        self.assertEqual(("instance_db_spec", "spec"), (record["authoredFormat"], record["changeId"]))
        self.unchanged()

    def test_candidates_copy_native_bytes_without_enabling_profile_or_overwriting_deployment(self):
        result, output = extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.assertTrue(result["candidateOnly"])
        self.assertEqual(self.stage / "profiles.candidate.v1.json", output)
        self.assertEqual(1, len(result["profiles"]))
        profile = result["profiles"][0]
        for key in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified", "runtimeVerified"):
            self.assertFalse(profile[key])
        self.assertTrue(profile["provenance"].startswith("artifacts/candidates/"))
        self.assertEqual(self.native.read_bytes(), (self.stage / "native/Tgp_TON_LAD.xml").read_bytes())
        self.unchanged()

    def test_staging_cannot_append_to_deployment_catalog(self):
        with self.assertRaisesRegex(ValueError, "cannot append"):
            extractor.extract_catalog([self.report_path], stage_output=self.stage, append=True)
        self.assertFalse(self.stage.exists())
        self.unchanged()

    def test_existing_candidate_output_is_immutable(self):
        self.stage.mkdir(parents=True)
        with self.assertRaises(FileExistsError):
            extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.unchanged()

    def test_candidate_output_cannot_escape_artifacts(self):
        for destination in (self.deploy, self.root / "elsewhere", self.root / "artifacts/../outside"):
            with self.subTest(destination=destination), self.assertRaisesRegex(ValueError, "under repository artifacts"):
                extractor.extract_catalog([self.report_path], stage_output=destination)
        self.unchanged()

    def test_failed_native_hash_check_cannot_change_deployment(self):
        self.native.write_bytes(self.native.read_bytes() + b" ")
        with self.assertRaises(AssertionError):
            extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.unchanged()

    def test_missing_template_dependency_is_recorded_and_not_invented(self):
        self.report["fixtures"][0]["route"] = "namespace"
        self.report["fixtures"][0]["exports"][0]["name"] = "TgpNamespace_GlobalTemplateFB_LAD"
        self.report["calls"][0]["result"]["block"]["name"] = "TgpNamespace_GlobalTemplateFB_LAD"
        self.write_report()
        result, _ = extractor.extract_catalog([self.report_path], [self.report_path], stage_output=self.stage)
        self.assertEqual([], result["globalInstanceTemplates"])
        self.assertEqual([{"block": "Tgp_GlobalTemplateFB_LAD", "missingReference": "Tgp_TemplateFB_LAD", "route": "namespace"}], result["unresolvedTemplates"])
        self.unchanged()

    def test_duplicate_native_filename_cannot_overwrite_first_candidate(self):
        with self.assertRaisesRegex(ValueError, "filename collision"):
            extractor.extract_catalog([self.report_path, self.report_path], stage_output=self.stage)
        self.assertEqual(self.native.read_bytes(), (self.stage / "native/Tgp_TON_LAD.xml").read_bytes())
        self.unchanged()

    def test_identical_recapture_in_a_later_run_confirms_the_first(self):
        again = self.report_path.with_name("capture-again.json")
        self.report["fixtures"][0]["changeId"] = "fixture-again"
        again.write_text(json.dumps(self.report), encoding="utf-8")
        result, _ = extractor.extract_catalog([self.report_path, again], stage_output=self.stage)
        self.assertEqual(1, len(result["sources"]))
        self.assertEqual("fixture", result["sources"][0]["changeId"])
        self.assertEqual(["fixture-again"], [c["changeId"] for c in result["sources"][0]["confirmingCaptures"]])
        self.unchanged()

    def test_wrong_scope_identity_is_rejected_before_writing_native_candidate(self):
        self.report["fixtures"][0]["namespace"] = "other"
        self.write_report()
        with self.assertRaisesRegex(ValueError, "scope/namespace identity"):
            extractor.extract_catalog([self.report_path], stage_output=self.stage)
        self.assertFalse((self.stage / "native/Tgp_TON_LAD.xml").exists())
        self.unchanged()

    def test_missing_scope_or_namespace_is_not_an_exact_candidate_identity(self):
        for field in ("scopePath", "namespace"):
            with self.subTest(field=field):
                entry = self.report["fixtures"][0]
                entry[field] = None
                identity = entry["exports"][0]["block"]
                if field == "scopePath": identity[field] = None
                else: identity["identity"][field] = None
                self.write_report()
                with self.assertRaisesRegex(ValueError, "scope/namespace identity"):
                    extractor.extract_catalog([self.report_path], stage_output=self.stage / field)
        self.unchanged()

    def test_report_native_name_cannot_escape_staging_or_use_alternate_stream(self):
        for ordinal, name in enumerate(("../escape", "nested/name", "nested\\name", "name:stream")):
            with self.subTest(name=name):
                self.report["fixtures"][0]["exports"][0]["name"] = name
                self.report["calls"][0]["result"]["block"]["name"] = name
                self.write_report()
                with self.assertRaisesRegex(ValueError, "directly inside staging/native"):
                    extractor.extract_catalog([self.report_path], stage_output=self.stage / str(ordinal))
        self.unchanged()
        self.assertFalse((self.root / "escape.xml").exists())


if __name__ == "__main__":
    unittest.main()
