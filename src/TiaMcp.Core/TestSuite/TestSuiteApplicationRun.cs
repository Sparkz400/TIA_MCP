using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

/// <summary>
/// Application or system (executionMode endpoint) review binding. The ticket is the opaque one-use download ticket from
/// sim_create; only the owning worker decodes it and verifies the registered owned PLCSIM instance before SetScope or Run.
/// </summary>
public sealed record TestSuiteApplicationBinding(string ExecutionMode, string SimulatorTicket, bool WholeGroup)
{
    // System reviews only: the owned instance's opc.tcp address. Omission keeps application review hashes unchanged.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerAddress { get; init; }
}

public static class TestSuiteApplicationRun
{
    public static bool Applies(string? executionMode) => executionMode is "systemManaged" or "externallyManaged";
    /// <summary>Application or system plans: one native executor Run returns one aggregate TestResults tree.</summary>
    public static bool Bound(string? executionMode) => Applies(executionMode) || executionMode == "endpoint";

    public static void Validate(TestSuiteApplicationBinding binding)
    {
        if (binding is null || !Bound(binding.ExecutionMode) || !TestSuiteRunPlanner.Text(binding.SimulatorTicket, 16384) ||
            (binding.ExecutionMode == "endpoint" ? !OwnedEndpoint(binding.ServerAddress) : binding.ServerAddress is not null))
            throw new BridgeException("InvalidExecutionMode", "Application reviews need systemManaged or externallyManaged; system reviews need endpoint and a canonical opc.tcp serverAddress with an IPv4 host and port. Both need the owned simulator's ticket.");
    }

    // The worker additionally requires the host to be a ControllerIP of the ticket-owned PLCSIM instance.
    private static bool OwnedEndpoint(string? address) =>
        TestSuiteRunPlanner.Text(address, 2048) && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "opc.tcp" &&
        uri.HostNameType == UriHostNameType.IPv4 && uri.Port is >= 1 and <= 65535 && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 && address == uri.AbsoluteUri;

    /// <summary>Selects the one native TestCaseExecutor/SystemTestCaseExecutor.Run overload. One call never mixes test cases and test sets.</summary>
    public static string Route(TestSuiteRunPlan plan, bool wholeGroup)
    {
        TestSuiteRunPlanner.Validate(plan);
        if (!Bound(plan.ExecutionMode)) throw new BridgeException("InvalidExecutionMode", "Only application and system plans have an executor route.");
        var families = plan.DispatchDefinitionIds.Select(id => plan.Definitions.Single(d => d.Id == id).Family).Distinct(StringComparer.Ordinal).ToArray();
        if (families.Length != 1) throw new BridgeException("MixedApplicationDispatch", "One native Run dispatches test cases or test sets, never both.");
        bool sets = families[0] == "applicationSet", system = families[0] == "system";
        if (wholeGroup)
        {
            // The worker additionally verifies that the project group holds exactly these cases.
            if (sets || plan.ExcludedDefinitionIds.Length != 0 || plan.DispatchDefinitionIds.Length != plan.Definitions.Length)
                throw new BridgeException("InvalidSelection", "A whole-group run dispatches every reviewed test case, excludes nothing and has no other dependency.");
            return system ? "systemGroup" : "group";
        }
        if (system) return plan.DispatchDefinitionIds.Length == 1 ? "systemTestCase" : "systemTestCaseList";
        return (sets, plan.DispatchDefinitionIds.Length == 1) switch
        { (false, true) => "testCase", (false, false) => "testCaseList", (true, true) => "testSet", _ => "testSetList" };
    }

    /// <summary>
    /// One native application or system Run returns one undivided TestResults tree, labelled by the worker with the first
    /// dispatched unit. It is kept whole for every required, non-excluded definition. Only a run whose closure is that one
    /// definition owns the tree exactly; every other unit stays scope-unverified (inconclusive). Style captures pass unchanged.
    /// </summary>
    public static NativeTestResult[] Captures(TestSuiteRunPlan plan, NativeTestResult[] captures)
    {
        if (!Bound(plan?.ExecutionMode)) return captures;
        TestSuiteRunPlanner.Validate(plan!);
        if (captures is null || captures.Length != 1 || captures[0] is null || captures[0].DefinitionId != plan!.DispatchDefinitionIds[0])
            throw new BridgeException("InvalidTestResults", "An application or system Run returns exactly one aggregate tree labelled with its first dispatched unit.");
        var tree = captures[0];
        EngineeringIdentityGuard.EnsureFresh(plan.Definitions.Single(d => d.Id == tree.DefinitionId).Reference, tree.DefinitionReference);
        bool exact = plan.RequiredDefinitionIds.Length == 1;
        // shortcut: fan-out multiplies the report's 64 MiB/100,000-message bound by unit count; per-case split needs a qualified tree profile.
        return plan.Definitions.Where(d => !plan.ExcludedDefinitionIds.Contains(d.Id, StringComparer.Ordinal))
            .Select(d => tree with { DefinitionId = d.Id, DefinitionReference = d.Reference, ScopeVerified = tree.ScopeVerified && exact }).ToArray();
    }
}
