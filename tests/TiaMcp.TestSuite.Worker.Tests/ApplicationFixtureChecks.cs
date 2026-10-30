using System;
using System.IO;
using Newtonsoft.Json.Linq;
using TiaMcp.TestSuite.V21;

// TS-Q02 worker-side admission and native scope readback, without loading the optional Siemens product.
internal static class ApplicationFixtureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ts-q02-" + Guid.NewGuid().ToString("N")), scratch = Path.Combine(root, "scratch");
        JObject Input() => new JObject {
            ["executionProfile"] = ApplicationQualificationFixture.Profile, ["artifactRoot"] = root,
            ["application"] = new JObject { ["executionMode"] = "externallyManaged", ["simulatorTicket"] = "opaque", ["route"] = "testCase" },
            ["run"] = new JObject { ["definitions"] = new JArray(new JObject { ["id"] = "TiaMcp_Q02_Pass", ["name"] = "TiaMcp_Q02_Pass", ["family"] = "application" }) },
            ["operation"] = new JObject { ["plan"] = new JObject { ["target"] = new JObject { ["projectFilePath"] = Path.Combine(scratch, "S", "S.ap21") } } } };
        check(ApplicationQualificationFixture.Refusal(Input(), scratch) == null, "The exact owned fixture case on an owned instance is admitted");
        foreach (var change in new (string Path, JToken Value)[] {
            ("executionProfile", "v21.owned-root-codeblocks.ui-reviewed.qualification-v1"), ("application.executionMode", "systemManaged"),
            ("application.route", "testCaseList"), ("application.simulatorTicket", ""), ("run.definitions[0].name", "UserCase"),
            ("run.definitions[0].id", "other"), ("run.definitions[0].family", "applicationSet") })
        {
            var input = Input(); input.SelectToken(change.Path).Replace(change.Value);
            check(ApplicationQualificationFixture.Refusal(input, scratch) == "TestSuiteProfileUnqualified", "Worker refuses " + change.Path + " = " + change.Value);
        }
        var listed = Input(); ((JArray)listed["run"]["definitions"]).Add(listed["run"]["definitions"][0].DeepClone());
        check(ApplicationQualificationFixture.Refusal(listed, scratch) == "TestSuiteProfileUnqualified", "Worker refuses more than one dispatched case");
        var styleShaped = Input(); styleShaped.Remove("application");
        check(ApplicationQualificationFixture.Refusal(styleShaped, scratch) == "TestSuiteProfileUnqualified", "Worker refuses a review without the simulator binding");
        foreach (string project in new[] { Path.Combine(root, "user", "Plant.ap21"), Path.Combine(root, "scratch-other", "P.ap21"), "relative\\P.ap21" })
        {
            var input = Input(); input["operation"]["plan"]["target"]["projectFilePath"] = project;
            check(ApplicationQualificationFixture.Refusal(input, scratch) == "ProjectIsolationRequired", "Worker refuses project " + project);
        }

        string Export(string instance, string mode = "ExternallyManagedPlcSimAdvInstance", string scope = "ScratchPLC") =>
            ApplicationQualificationFixture.Text(ApplicationQualificationFixture.Pass, scope)
                .Replace("PLCSIM_INSTANCE_NAME : \"\"", "PLCSIM_INSTANCE_NAME : \"" + instance + "\"")
                .Replace("\"SystemManagedPlcSimAdvInstance\"", "\"" + mode + "\"");
        check(ApplicationQualificationFixture.ScopeMatches("ScratchPLC", Export("TiaMcp_Owned"), "ScratchPLC", "TiaMcp_Owned"), "Controller, owned instance and external mode read back");
        check(!ApplicationQualificationFixture.ScopeMatches("ScratchPLC", Export(""), "ScratchPLC", "TiaMcp_Owned"), "An unset instance is not the owned target");
        check(!ApplicationQualificationFixture.ScopeMatches("ScratchPLC", Export("Foreign"), "ScratchPLC", "TiaMcp_Owned"), "A foreign instance is not the owned target");
        check(!ApplicationQualificationFixture.ScopeMatches("ScratchPLC", Export("TiaMcp_Owned", "SystemManagedPlcSimAdvInstance"), "ScratchPLC", "TiaMcp_Owned"), "System-managed mode is not the owned target");
        check(!ApplicationQualificationFixture.ScopeMatches("OtherPLC", Export("TiaMcp_Owned"), "ScratchPLC", "TiaMcp_Owned"), "GetScope must name the owned controller");
        check(!ApplicationQualificationFixture.ScopeMatches("ScratchPLC", Export("TiaMcp_Owned", scope: "OtherPLC"), "ScratchPLC", "TiaMcp_Owned"), "The export must name the owned controller");
        string twice = Export("TiaMcp_Owned").Replace("END_PROPERTY", "PLCSIM_INSTANCE_NAME : \"Foreign\"\r\nEND_PROPERTY");
        check(!ApplicationQualificationFixture.ScopeMatches("ScratchPLC", twice, "ScratchPLC", "TiaMcp_Owned"), "An ambiguous repeated property refuses");
        check(ApplicationQualificationFixture.Text(ApplicationQualificationFixture.Pass, "ScratchPLC").Contains("Active,true)") &&
            ApplicationQualificationFixture.Text(ApplicationQualificationFixture.Fail, "ScratchPLC").Contains("Active,false)"), "Pass asserts the observed TRUE, Fail deliberately asserts FALSE");
    }
}
