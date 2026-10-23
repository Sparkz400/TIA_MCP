namespace TiaMcp.Core.TestSuite;

// Result interpretation for the exact assisted qualification fixture. This does not
// establish scope, prerequisites or production qualification. Native bytes stay intact.
internal static class TestSuiteCasingResultProfile
{
    internal const string Profile = "v21.owned-root-codeblocks.ui-reviewed.qualification-v1";
    internal static string? Interpret(TestSuiteDefinitionManifest definition, NativeTestResult result)
    {
        if (definition.ProfileId != Profile || definition.Id != definition.Name ||
            definition.Name is not ("TiaMcp_Q01_Clean" or "TiaMcp_Q01_Violating") || result.WarningCount != 0 || result.Messages.Length != 2)
            return null;
        int errors = result.ErrorCount;
        if (errors is not (0 or 3) || result.State != (errors == 0 ? "Success" : "Error")) return null;
        var footer = result.Messages[1];
        if (footer.State != "Information" || footer.Path is not null || footer.ErrorCount != 0 || footer.WarningCount != 0 || footer.Messages.Length != 0 ||
            footer.Description != $"Ausführung von Regelsatz/-sätzen beendet (Fehler : {errors} ; Warnungen : 0)") return null;
        var group = result.Messages[0];
        bool Aggregate(NativeTestMessage m, string path, int count) => m.Path == path && m.State == (count == 0 ? "Success" : "Error") && m.ErrorCount == count && m.WarningCount == 0;
        if (!Aggregate(group, "Styleguide", errors) || group.Description != "" || group.Messages.Length != 1) return null;
        var ruleSet = group.Messages[0];
        if (!Aggregate(ruleSet, definition.Name, errors) || ruleSet.Description != "" || ruleSet.Messages.Length != 1) return null;
        var rule = ruleSet.Messages[0];
        if (!Aggregate(rule, "TiaMcp_CamelCase", errors) || rule.Messages.Length != 2 ||
            rule.Description != $"Regelverletzung/Validierung Codebaustein (FB,FC,OB) : {errors}/3, Prozentsatz Regelverletzung - {(errors == 0 ? 0 : 100)} %") return null;
        var violations = rule.Messages[0]; var clean = rule.Messages[1];
        if (!Aggregate(violations, "Elemente mit Regelverletzung", errors) || !Aggregate(clean, "Elemente ohne Regelverletzung", 0) ||
            violations.Description != "" || clean.Description != "" || violations.Messages.Length != errors || clean.Messages.Length != 3 - errors) return null;
        var leaves = errors == 0 ? clean.Messages : violations.Messages;
        string[] names = ["Main", "TiaMcp_EnableGate", "TiaMcp_IsEnabled"];
        if (!leaves.Select(l => l.Path).Order(StringComparer.Ordinal).SequenceEqual(names.Select(n => "ScratchPLC - " + n))) return null;
        foreach (var leaf in leaves)
        {
            if (leaf.State != (errors == 0 ? "Success" : "Error") || leaf.ErrorCount != (errors == 0 ? 0 : 1) || leaf.WarningCount != 0 || leaf.Messages.Length != 0) return null;
            if (errors != 0)
            {
                string name = leaf.Path!["ScratchPLC - ".Length..];
                if (leaf.Description != $"Konflikte mit {name} : Der Objektname Codebaustein (FB,FC,OB) muss in Camel Case geschrieben werden") return null;
            }
        }
        return errors == 0 ? "passed" : "assertionFailed";
    }
}
