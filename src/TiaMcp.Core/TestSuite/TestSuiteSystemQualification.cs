namespace TiaMcp.Core.TestSuite;

/// <summary>TS-Q03 admits one exact fixture case on the owned TCP/IP simulator. No production admission.</summary>
public static class TestSuiteSystemQualification
{
    public const string Profile = "v21.owned-plcsim.system.qualification-v1";
    public static readonly string[] Cases = ["TiaMcp_Q03_Fail", "TiaMcp_Q03_Pass"];

    public static bool Endpoint(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == "opc.tcp" && uri.HostNameType == UriHostNameType.IPv4 && uri.Port == 4840 &&
        uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        address == uri.AbsoluteUri;

    public static void Admit(TestSuiteStyleContext context, string artifactRoot)
    {
        var binding = context?.Application;
        var definitions = context?.Run?.Definitions;
        if (context?.ExecutionProfile != Profile || context.Run.ExecutionMode != "endpoint" ||
            binding is not { ExecutionMode: "endpoint", WholeGroup: false } ||
            string.IsNullOrWhiteSpace(binding.SimulatorTicket) || !Endpoint(binding.ServerAddress) ||
            definitions is not { Length: 1 } || definitions[0].Family != "system" ||
            definitions[0].ProfileId != Profile || definitions[0].Id != definitions[0].Name ||
            !Cases.Contains(definitions[0].Name, StringComparer.Ordinal) ||
            context.Run.DispatchDefinitionIds is not { Length: 1 } || context.Run.DispatchDefinitionIds[0] != definitions[0].Id ||
            context.Run.ExcludedDefinitionIds.Length != 0)
            throw new BridgeException("TestSuiteProfileUnqualified", "Only one exact TS-Q03 system fixture case on the owned simulator endpoint is admitted.");
        string? project = context.Operation?.Plan?.Target?.ProjectFilePath;
        string scratch = Path.Combine(Path.GetFullPath(artifactRoot), "scratch") + Path.DirectorySeparatorChar;
        if (project is null || !Path.IsPathFullyQualified(project) || !Path.GetFullPath(project).StartsWith(scratch, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ProjectIsolationRequired", "System qualification requires this Host's worker-created scratch.");
    }
}
