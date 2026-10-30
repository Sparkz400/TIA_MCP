namespace TiaMcp.Core.TestSuite;

/// <summary>
/// TS-Q02 qualification-mode admission for application tests. Admits only one exact owned fixture case, run once with
/// Run(TestCase) on an externally managed PLCSIM instance (system-managed mode would let Test Suite create its own,
/// unowned instance), in a worker-created scratch under the Host artifact root. The owning worker independently decodes the
/// simulator ticket and refuses foreign or released instances. Production admission stays closed.
/// </summary>
public static class TestSuiteApplicationQualification
{
    public const string Profile = "v21.owned-plcsim.application.qualification-v1";
    public static readonly string[] Cases = ["TiaMcp_Q02_Fail", "TiaMcp_Q02_Pass"];

    public static void Admit(TestSuiteStyleContext context, string artifactRoot)
    {
        var binding = context?.Application;
        var definitions = context?.Run?.Definitions;
        if (context?.ExecutionProfile != Profile || binding is not { ExecutionMode: "externallyManaged", WholeGroup: false } ||
            definitions is not { Length: 1 } || definitions[0].Family != "application" || definitions[0].Id != definitions[0].Name ||
            !Cases.Contains(definitions[0].Name, StringComparer.Ordinal))
            throw new BridgeException("TestSuiteProfileUnqualified", "Only the exact owned application fixture case, externally managed on an owned PLCSIM instance, is admitted.");
        string? project = context.Operation?.Plan?.Target?.ProjectFilePath;
        string scratch = Path.Combine(Path.GetFullPath(artifactRoot), "scratch") + Path.DirectorySeparatorChar;
        if (project is null || !Path.IsPathFullyQualified(project) || !Path.GetFullPath(project).StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ProjectIsolationRequired", "Qualification cannot adopt a project outside this Host's owned scratch folder.");
    }
}
