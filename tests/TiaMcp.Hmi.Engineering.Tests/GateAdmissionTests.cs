using Newtonsoft.Json.Linq;
using TiaMcp.Model;

// Change kinds hmi_classic_import and hmi_external_tag_create: admission, standing capture notes and the compile rule they share with the text list.
static class GateAdmissionTests
{
    static readonly string[] AllProfiles = { "classic-comfort", "classic-rt-advanced", "classic-basic", "classic-rt-professional", "unified-comfort", "unified-pc" };
    static readonly string[] Kinds = { "hmi_classic_import", "hmi_external_tag_create" };
    const string Screen = "Siemens.Engineering.Hmi.Screen.Screen", TagTable = "Siemens.Engineering.Hmi.Tag.TagTable";
    static string Family(string profile) => profile.StartsWith("classic-", StringComparison.Ordinal) ? "classic" : "unified";
    static void Assert(bool value, string label) { if (!value) throw new Exception(label); }
    static bool Open(string kind, string profile, string operation, string type, string qualification)
    {
        var gate = HmiEngineeringRules.KindGate(kind, Family(profile), profile, operation, type, qualification);
        bool open = gate.Value<bool>("available");
        Assert(open == (gate.Value<string>("admission") == "qualification") && open == (gate.Value<string>("reasonCode") == null), "admission and reason code follow availability: " + gate);
        Assert(gate.Value<string>("nativeQualification") == "unverified" && !gate.Value<bool>("runtimeVerified") && !string.IsNullOrWhiteSpace(gate.Value<string>("reason")), "never verified before curation: " + gate);
        return open;
    }

    internal static void Run(Action<string, Action> check)
    {
        check("Admitted kinds stay closed on every profile without a qualification profile", () => {
            foreach (string kind in Kinds)
                foreach (string profile in AllProfiles)
                    foreach (string operation in HmiEngineeringRules.Operations)
                        foreach (string type in new[] { Screen, TagTable, null })
                        {
                            Assert(!Open(kind, profile, operation, type, null), "closed: " + kind + " " + profile + " " + operation);
                            Assert(HmiEngineeringRules.KindGate(kind, Family(profile), profile, operation, type).Value<string>("reasonCode") == "HmiPreservationUnavailable", "reason code");
                        }
            Assert(!Open("hmi_external_tag_create", "unified-comfort", "external_tag_create", null, "classic-comfort") && !Open("hmi_classic_import", "classic-comfort", "scalar_property_set", Screen, "unified-pc"), "a foreign qualification profile");
            foreach (string kind in new[] { "hmi_engineering", "hmi_text_list_import", "invented", null })
                Reject(() => HmiEngineeringRules.KindGate(kind, "classic", "classic-comfort", "classic_xml_import", Screen), "unknown kind " + kind);
        });
        check("A qualification profile opens only its own kind, family and profile", () => {
            Assert(Open("hmi_classic_import", "classic-comfort", "classic_xml_import", Screen, "classic-comfort"), "Classic screen import");
            Assert(Open("hmi_classic_import", "classic-comfort", "classic_xml_import", TagTable, "classic-comfort"), "Classic tag-table import");
            Assert(Open("hmi_classic_import", "classic-comfort", "scalar_property_set", Screen, "classic-comfort"), "Classic BackColor");
            foreach (string profile in new[] { "unified-comfort", "unified-pc" })
            {
                Assert(Open("hmi_external_tag_create", profile, "external_tag_create", "Siemens.Engineering.HmiUnified.HmiTags.HmiTagTable", profile), "external tag on " + profile);
                Assert(!Open("hmi_classic_import", profile, "classic_xml_import", Screen, profile), "the Classic kind on " + profile);
            }
            Assert(!Open("hmi_external_tag_create", "unified-comfort", "external_tag_create", null, "unified-pc"), "another Unified profile");
            Assert(!Open("hmi_external_tag_create", "classic-comfort", "external_tag_create", null, "classic-comfort"), "the Unified kind on Classic");
            foreach (string profile in new[] { "classic-rt-advanced", "classic-basic", "classic-rt-professional" })
                Assert(!Open("hmi_classic_import", profile, "classic_xml_import", Screen, profile), "unqualified Classic profile " + profile);
            Assert(!Open("hmi_classic_import", "classic-comfort", "classic_xml_import", Screen, null) && !Open("hmi_classic_import", "classic-comfort", "classic_xml_import", Screen, "classic-basic"), "no or another qualification profile");
            Assert(HmiEngineeringRules.QualificationProfiles.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(new[] { "classic-comfort", "unified-comfort", "unified-pc" }), "flag profiles");
        });
        check("Classic import opens only for screens and tag tables", () => {
            foreach (string type in new[] { "Siemens.Engineering.Hmi.Screen.ScreenTemplate", "Siemens.Engineering.Hmi.Screen.ScreenPopup", "Siemens.Engineering.Hmi.Screen.ScreenSlidein",
                "Siemens.Engineering.Hmi.Screen.ScreenGlobalElements", "Siemens.Engineering.Hmi.Screen.ScreenOverview", "Siemens.Engineering.Hmi.RuntimeScripting.VBScript",
                "Siemens.Engineering.Hmi.TextGraphicList.TextList", "Siemens.Engineering.Hmi.Cycle.Cycle", "Siemens.Engineering.Hmi.Communication.Connection", "Siemens.Engineering.Hmi.Screen.screen", "", null })
            {
                var gate = HmiEngineeringRules.KindGate("hmi_classic_import", "classic", "classic-comfort", "classic_xml_import", type, "classic-comfort");
                Assert(!gate.Value<bool>("available") && gate.Value<string>("reasonCode") == "HmiPreservationUnavailable" && gate.Value<string>("reason").Contains("screen or tag table"), "closed: " + type);
            }
        });
        check("Operations outside the kind stay closed and refuse as a request", () => {
            foreach (string operation in HmiEngineeringRules.Operations.Except(new[] { "classic_xml_import", "scalar_property_set" }))
                Assert(!Open("hmi_classic_import", "classic-comfort", operation, Screen, "classic-comfort"), "Classic " + operation);
            foreach (string operation in HmiEngineeringRules.Operations.Where(o => o != "external_tag_create"))
                Assert(!Open("hmi_external_tag_create", "unified-comfort", operation, null, "unified-comfort"), "Unified " + operation);
            JObject Payload(string family, string operation) => new JObject { ["operation"] = operation, ["target"] = new JObject { ["family"] = family } };
            HmiEngineeringRules.RequireKind("hmi_classic_import", Payload("classic", "classic_xml_import"));
            HmiEngineeringRules.RequireKind("hmi_classic_import", Payload("classic", "scalar_property_set"));
            HmiEngineeringRules.RequireKind("hmi_external_tag_create", Payload("unified", "external_tag_create"));
            HmiEngineeringRules.RequireKind("hmi_engineering", Payload("unified", "screen_create"));
            Reject(() => HmiEngineeringRules.RequireKind("hmi_classic_import", Payload("classic", "tag_create")), "Classic tag_create");
            Reject(() => HmiEngineeringRules.RequireKind("hmi_classic_import", Payload("unified", "scalar_property_set")), "Unified scalar under the Classic kind");
            Reject(() => HmiEngineeringRules.RequireKind("hmi_external_tag_create", Payload("unified", "tag_create")), "internal tag");
            Reject(() => HmiEngineeringRules.RequireKind("hmi_external_tag_create", Payload("classic", "external_tag_create")), "Classic external tag");
            Reject(() => HmiEngineeringRules.RequireKind("hmi_external_tag_create", null), "no payload");
            Assert(HmiEngineeringRules.IsAdmittedKind("hmi_classic_import") && HmiEngineeringRules.IsAdmittedKind("hmi_external_tag_create") && !HmiEngineeringRules.IsAdmittedKind("hmi_engineering")
                && !HmiEngineeringRules.IsAdmittedKind("hmi_text_list_import") && !HmiEngineeringRules.IsAdmittedKind(null), "admitted kinds");
        });
        check("The hmi_engineering gate names the kind that carries its three admitted operations", () => {
            Assert(HmiEngineeringRules.Gate("classic", "classic-comfort", "scalar_property_set").Value<string>("reason").Contains("hmi_classic_import"), "Classic BackColor");
            Assert(HmiEngineeringRules.Gate("classic", "classic-comfort", "classic_xml_import").Value<string>("reason").Contains("hmi_classic_import"), "Classic import");
            Assert(HmiEngineeringRules.Gate("unified", "unified-pc", "external_tag_create").Value<string>("reason").Contains("hmi_external_tag_create"), "Unified external tag");
            string scalarReason = HmiEngineeringRules.Gate("classic", "classic-basic", "scalar_property_set").Value<string>("reason");
            string importReason = HmiEngineeringRules.Gate("classic", "classic-basic", "classic_xml_import").Value<string>("reason");
            Assert(scalarReason.Contains("only on classic-comfort") && !scalarReason.Contains("screen or tag table"), "the Classic scalar names its profile scope");
            Assert(importReason.Contains("only on classic-comfort and only for a screen or tag table"), "the Classic import names its profile and type scope");
            Assert(HmiEngineeringRules.Gate("unified", "unified-pc", "external_tag_create").Value<string>("reason").Contains("only on unified-comfort or unified-pc"), "the Unified kind names its profiles");
            foreach (var (family, operation) in new[] { ("unified", "scalar_property_set"), ("unified", "classic_xml_import"), ("unified", "tag_create"), ("classic", "external_tag_create"), ("unified", "screen_create") })
                Assert(!HmiEngineeringRules.Gate(family, "x", operation).Value<string>("reason").Contains("Change kind"), "no kind for " + family + " " + operation);
        });
        check("Standing notes only: exactly the kind's notes of one family, no failure, a valid hash", () => {
            JObject Manifest(string family, int targets, params string[] notes) => new JObject { ["family"] = family, ["nativeFailure"] = null, ["observedArtifactsHash"] = new string('e', 64),
                ["targets"] = new JArray(Enumerable.Range(0, targets).Select(i => new JObject { ["family"] = family, ["hmiPath"] = "/d/hmi/H" + i })), ["missingCoverage"] = new JArray(notes) };
            string classic = HmiPreservationRules.ClassicExportNote, table = HmiPreservationRules.UnifiedTagTableExportNote, typed = HmiPreservationRules.UnifiedTypedReadbackNote;
            Assert(HmiEngineeringRules.ScopeAccepted("hmi_classic_import", Manifest("classic", 2, classic, classic)), "two Classic targets");
            Assert(HmiEngineeringRules.ScopeAccepted("hmi_external_tag_create", Manifest("unified", 2, table, typed, table, typed)), "two Unified targets");
            Assert(HmiEngineeringRules.ScopeAccepted("hmi_text_list_import", Manifest("unified", 1, table, typed)) && HmiTextListImportRules.ScopeAccepted(Manifest("unified", 1, table, typed)), "text list unchanged");
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", Manifest("classic", 1, classic, "Another gap.")), "an extra note");
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_external_tag_create", Manifest("unified", 1, table, "2 Unified typed attribute read(s) failed; their readError markers are compared, their values are not. See typed-model.json.", typed)), "a typed read error");
            var failed = Manifest("classic", 1, classic); failed["nativeFailure"] = new JObject { ["message"] = "x" };
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", failed), "a capture failure");
            var mixed = Manifest("classic", 1, classic, table, typed); ((JArray)mixed["targets"]).Add(new JObject { ["family"] = "unified" });
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", mixed), "mixed families");
            var classicOnly = Manifest("classic", 1, classic); ((JArray)classicOnly["targets"]).Add(new JObject { ["family"] = "unified" });
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", classicOnly), "a Unified target without its notes");
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", Manifest("unified", 1, table, typed)) && !HmiEngineeringRules.ScopeAccepted("hmi_external_tag_create", Manifest("classic", 1, classic)), "the other family's capture");
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_external_tag_create", Manifest("unified", 1, table, typed, classic)), "a Classic note under the Unified kind");
            var unhashed = Manifest("classic", 1, classic); unhashed["observedArtifactsHash"] = "abc";
            Assert(!HmiEngineeringRules.ScopeAccepted("hmi_classic_import", unhashed) && !HmiEngineeringRules.ScopeAccepted("hmi_classic_import", null) && !HmiEngineeringRules.ScopeAccepted("hmi_classic_import", Manifest("classic", 0)), "no hash, no manifest, no target");
            Assert(HmiEngineeringRules.StandingNotes("hmi_engineering") == null && !HmiEngineeringRules.ScopeAccepted("hmi_engineering", Manifest("classic", 1, classic)), "hmi_engineering requires a complete capture");
        });
        check("The compile rule accepts the observed Classic and Unified trees and refuses an added warning", () => {
            JObject Tree(string target, params (int Depth, string State, string Text, string Path)[] rows) => new JObject { ["targets"] = new JArray(new JObject { ["target"] = target, ["messagesTruncated"] = false,
                ["messages"] = new JArray(rows.Select(r => new JObject { ["state"] = r.State, ["description"] = r.Text, ["path"] = r.Path, ["depth"] = r.Depth })) }) };
            // Run artifacts/hc/eh_18df891c (TP700 Comfort, HMI_RT_1): the 6-row and the 5-row tree of one target.
            const string rt1 = "/projects/P/devices/HmiDevice_A/items/HMI_RT_1/hmi/HMI_RT_1", done = "Compiling finished (errors: 0; warnings: 0)";
            var six = Tree(rt1, (0, "Information", "Time stamp: 11/1/2026 10:39 AM - 89800 bytes used of 12582912 bytes, maximum", "HmiDevice_A"), (1, "Information", "Software compilation started.", null),
                (1, "Information", "No HMI runtime-relevant changes have been made since the last error-free HMI compilation. New compilation is not necessary.", null), (1, "Information", "Number of tags: 2", null),
                (1, "Information", "Software compilation completed (device version: 17.0.0.0).", null), (0, "Success", done, null));
            var five = Tree(rt1, (0, "Information", "Time stamp: 11/1/2026 10:41 AM - 89812 bytes used of 12582912 bytes, maximum", "HmiDevice_A"), (1, "Information", "Software compilation started.", null),
                (1, "Information", "No HMI runtime-relevant changes have been made since the last error-free HMI compilation. New compilation is not necessary.", null),
                (1, "Information", "Software compilation completed (device version: 17.0.0.0).", null), (0, "Success", done, null));
            Assert(HmiTextListImportRules.CompileProblemsUnchanged(six, five) && HmiTextListImportRules.CompileProblemsUnchanged(five, six), "Classic Information rows and time stamps are ignored");
            // Run artifacts/hd/uc/native (MTP700 Unified Comfort, compileAfter of HMI_RT_1, flattened by depth).
            var unified = Tree("/projects/P/devices/HmiDevice_A", (0, "Information", "", "HmiDevice_A"), (1, "Information", "", "Hardware configuration"),
                (2, "Information", "Hardware was not compiled. The configuration is up-to-date.", null), (1, "Information", "Software compilation started.", null),
                (1, "Information", "Software compilation completed.", null), (0, "Success", done, null));
            Assert(HmiTextListImportRules.CompileProblemsUnchanged(unified, unified), "the clean Unified tree");
            var warned = Tree(rt1, (0, "Warning", "", "HmiDevice_A"), (1, "Information", "Software compilation started.", null), (1, "Warning", "Tag 'QualificationTags_SharedValue' is not used.", "Tags"),
                (1, "Information", "Software compilation completed (device version: 17.0.0.0).", null), (0, "Success", "Compiling finished (errors: 0; warnings: 1)", null));
            Assert(!HmiTextListImportRules.CompileProblemsUnchanged(six, warned), "an added Classic warning");
            var unifiedWarned = Tree("/projects/P/devices/HmiDevice_A", (0, "Warning", "", "HmiDevice_A"), (1, "Warning", "", "Tags"), (2, "Warning", "The PLC tag of HMI tag 'SharedSignal' has no access.", null),
                (0, "Success", "Compiling finished (errors: 0; warnings: 1)", null));
            Assert(!HmiTextListImportRules.CompileProblemsUnchanged(unified, unifiedWarned), "an added Unified warning");
            Assert(!HmiTextListImportRules.CompileProblemsUnchanged(six, Tree("/projects/P/devices/HmiDevice_B/items/HMI_RT_2/hmi/HMI_RT_2", (0, "Success", done, null))), "another compile target");
        });
    }

    static void Reject(Action action, string label)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected a refusal: " + label);
    }
}
