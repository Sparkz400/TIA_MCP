namespace TiaMcp.Core.TestSuite;

// Result interpretation for the exact TS-Q02 fixture only: the tree TIA V21 Update 3 wrote under its English UI on
// 30 October 2026. Another UI language or any other shape returns null, so the run stays inconclusive or an execution error.
internal static class TestSuiteApplicationResultProfile
{
    internal static string? Interpret(TestSuiteDefinitionManifest definition, NativeTestResult result)
    {
        if (definition.ProfileId != TestSuiteApplicationQualification.Profile || definition.Family != "application" || definition.Id != definition.Name ||
            !TestSuiteApplicationQualification.Cases.Contains(definition.Name, StringComparer.Ordinal) || result.Messages.Length != 2)
            return null;
        bool pass = definition.Name == "TiaMcp_Q02_Pass";
        int errors = pass ? 0 : 1;
        string state = pass ? "Success" : "Error";
        bool Node(NativeTestMessage m, string? path, string description, int children, string nodeState, int nodeErrors) =>
            m.Path == path && m.Description == description && m.State == nodeState && m.ErrorCount == nodeErrors && m.WarningCount == 0 && m.Messages.Length == children;
        if (result.State != state || result.ErrorCount != errors || result.WarningCount != 0 ||
            !Node(result.Messages[1], null, "Test case(s) execution completed.", 0, "Information", 0))
            return null;
        var group = result.Messages[0];
        if (!Node(group, "Application test", "Test cases executed : 1 ; Passed : " + (1 - errors) + " ; Failed : " + errors + " ; Inconclusive : 0", 1, state, errors))
            return null;
        // The case node names the controller and the instance it ran on, which must be the reviewed owned instance.
        var test = group.Messages[0];
        const string prefix = "ScratchPLC[PLCSIM Instance : ";
        string instance = test.Description.StartsWith(prefix, StringComparison.Ordinal) && test.Description.EndsWith(']') ? test.Description[prefix.Length..^1] : "";
        if (instance.Length == 0 || !definition.RequiredScope.Any(r => r.Object.Kind == "plcsimInstance" && r.Object.ObjectPath.EndsWith("/plcsim/" + instance, StringComparison.Ordinal)) ||
            !Node(test, definition.Name, test.Description, 1, state, errors))
            return null;
        var step = test.Messages[0];
        if (!Node(step, "\"Enable gate output\"", pass ? "Pass" : "Fail", 1, state, errors) ||
            !Node(step.Messages[0], "TiaMcp_EnableGate_DB.Active", "Actual: True, Expected: " + (pass ? "True" : "False") + ", Assert type: Equal", 0, state, errors))
            return null;
        return pass ? "passed" : "assertionFailed";
    }
}
