using System;
using System.IO;
using Newtonsoft.Json.Linq;
using TiaMcp.TestSuite.V21;

internal static class SystemFixtureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "q03", "scratch"), address = "opc.tcp://192.168.0.1:4840/";
        JObject Input() => new JObject {
            ["executionProfile"] = SystemQualificationFixture.Profile,
            ["application"] = new JObject { ["executionMode"] = "endpoint", ["route"] = "systemTestCase", ["simulatorTicket"] = "opaque", ["serverAddress"] = address },
            ["run"] = new JObject { ["executionMode"] = "endpoint", ["dispatchDefinitionIds"] = new JArray(SystemQualificationFixture.Pass), ["excludedDefinitionIds"] = new JArray(),
                ["definitions"] = new JArray(new JObject { ["id"] = SystemQualificationFixture.Pass, ["name"] = SystemQualificationFixture.Pass,
                    ["family"] = "system", ["profileId"] = SystemQualificationFixture.Profile }) },
            ["operation"] = new JObject { ["plan"] = new JObject { ["target"] = new JObject { ["projectFilePath"] = Path.Combine(scratch, "S", "S.ap21") } } } };
        check(SystemQualificationFixture.Refusal(Input(), scratch) == null, "TS-Q03 exact worker admission");
        foreach (var mutation in new (string Path, string Value)[] {
            ("application.executionMode", "systemManaged"), ("application.route", "systemGroup"), ("application.simulatorTicket", ""),
            ("application.serverAddress", "opc.tcp://localhost:4840/"), ("application.serverAddress", "opc.tcp://192.168.0.1:4840/other"),
            ("run.definitions[0].family", "application"), ("run.definitions[0].name", "UserCase"), ("run.definitions[0].profileId", "unqualified"),
            ("run.dispatchDefinitionIds[0]", "Other"), ("run.executionMode", "externallyManaged") })
        { var input = Input(); input.SelectToken(mutation.Path).Replace(mutation.Value); check(SystemQualificationFixture.Refusal(input, scratch) == "TestSuiteProfileUnqualified", "TS-Q03 refuses " + mutation.Path); }
        var external = Input(); external["operation"]["plan"]["target"]["projectFilePath"] = Path.Combine(scratch, "..", "User.ap21");
        check(SystemQualificationFixture.Refusal(external, scratch) == "ProjectIsolationRequired", "TS-Q03 refuses path escape");
        var scope = new JObject { ["serverAddress"] = address, ["serverInterface"] = "StandardSIMATIC", ["interfaceFolderPath"] = "" };
        string text = SystemQualificationFixture.Text(SystemQualificationFixture.Pass, address);
        check(SystemQualificationFixture.ScopeMatches(scope, text, address), "System scope and export agree");
        foreach (string altered in new[] { text.Replace(address, "opc.tcp://192.168.0.2:4840/"), text.Replace("Standard_SIMATIC", "UserDefined"),
            text.Replace("FOLDER_PATH : \"\"", "FOLDER_PATH : \"foreign\""), text.Replace("END_PROPERTY", "SERVER_ADDRESS : \"" + address + "\"\r\nEND_PROPERTY") })
            check(!SystemQualificationFixture.ScopeMatches(scope, altered, address), "Changed or duplicate export scope refused");
        scope["serverAddress"] = "opc.tcp://192.168.0.2:4840/";
        check(!SystemQualificationFixture.ScopeMatches(scope, text, address), "Getter and export must both name the owned endpoint");
        check(text.Contains("ASSERT.Equal(gate,true)") && SystemQualificationFixture.Text(SystemQualificationFixture.Fail, address).Contains("ASSERT.Equal(gate,false)"), "Independent pass/fail expectations");
        check(!text.Contains("Active:="), "Fixture does not initialize the PLC output alias");
    }
}
