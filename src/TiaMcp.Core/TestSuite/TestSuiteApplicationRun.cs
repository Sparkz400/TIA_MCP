using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

/// <summary>
/// Application review binding. The ticket is the opaque one-use download ticket from sim_create; only the
/// owning worker decodes it and verifies the registered owned PLCSIM instance before SetScope or Run.
/// </summary>
public sealed record TestSuiteApplicationBinding(string ExecutionMode, string SimulatorTicket, bool WholeGroup);

public static class TestSuiteApplicationRun
{
    public static bool Applies(string? executionMode) => executionMode is "systemManaged" or "externallyManaged";

    public static void Validate(TestSuiteApplicationBinding binding)
    {
        if (binding is null || !Applies(binding.ExecutionMode) || !TestSuiteRunPlanner.Text(binding.SimulatorTicket, 16384))
            throw new BridgeException("InvalidExecutionMode", "Application reviews need systemManaged or externallyManaged and the owned simulator's ticket.");
    }

    /// <summary>Selects the one native TestCaseExecutor.Run overload. One call never mixes test cases and test sets.</summary>
    public static string Route(TestSuiteRunPlan plan, bool wholeGroup)
    {
        TestSuiteRunPlanner.Validate(plan);
        if (!Applies(plan.ExecutionMode)) throw new BridgeException("InvalidExecutionMode", "Only application plans have a TestCaseExecutor route.");
        var families = plan.DispatchDefinitionIds.Select(id => plan.Definitions.Single(d => d.Id == id).Family).Distinct(StringComparer.Ordinal).ToArray();
        if (families.Length != 1) throw new BridgeException("MixedApplicationDispatch", "One native Run dispatches test cases or test sets, never both.");
        bool sets = families[0] == "applicationSet";
        if (wholeGroup)
        {
            // The worker additionally verifies that the project group holds exactly these cases.
            if (sets || plan.ExcludedDefinitionIds.Length != 0 || plan.DispatchDefinitionIds.Length != plan.Definitions.Length)
                throw new BridgeException("InvalidSelection", "A whole-group run dispatches every reviewed test case, excludes nothing and has no other dependency.");
            return "group";
        }
        return (sets, plan.DispatchDefinitionIds.Length == 1) switch
        { (false, true) => "testCase", (false, false) => "testCaseList", (true, true) => "testSet", _ => "testSetList" };
    }

    /// <summary>
    /// One native Run returns one undivided TestResults tree, labelled by the worker with the first dispatched unit.
    /// It is kept whole for every required, non-excluded definition. Only a run whose closure is that one definition
    /// owns the tree exactly; every other unit stays scope-unverified (inconclusive). Style captures pass unchanged.
    /// </summary>
    public static NativeTestResult[] Captures(TestSuiteRunPlan plan, NativeTestResult[] captures)
    {
        if (!Applies(plan?.ExecutionMode)) return captures;
        TestSuiteRunPlanner.Validate(plan!);
        if (captures is null || captures.Length != 1 || captures[0] is null || captures[0].DefinitionId != plan!.DispatchDefinitionIds[0])
            throw new BridgeException("InvalidTestResults", "An application Run returns exactly one aggregate tree labelled with its first dispatched unit.");
        var tree = captures[0];
        EngineeringIdentityGuard.EnsureFresh(plan.Definitions.Single(d => d.Id == tree.DefinitionId).Reference, tree.DefinitionReference);
        bool exact = plan.RequiredDefinitionIds.Length == 1;
        // shortcut: fan-out multiplies the report's 64 MiB/100,000-message bound by unit count; per-case split needs a qualified tree profile.
        return plan.Definitions.Where(d => !plan.ExcludedDefinitionIds.Contains(d.Id, StringComparer.Ordinal))
            .Select(d => tree with { DefinitionId = d.Id, DefinitionReference = d.Reference, ScopeVerified = tree.ScopeVerified && exact }).ToArray();
    }
}
