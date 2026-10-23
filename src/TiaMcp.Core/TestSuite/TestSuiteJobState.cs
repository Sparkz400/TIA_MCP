namespace TiaMcp.Core.TestSuite;

public sealed record TestSuiteJobState(string State = "prepared", bool StopRequested = false,
    bool Dispatched = false, bool CleanupVerified = false, string? ReconciliationSha256 = null)
{
    public bool NativeCancellationSupported => false;
    public bool ResourceReleasePermitted { get { Validate(); return CleanupVerified && State is "passed" or "failed" or "blocked" or "stopped"; } }

    public void Validate()
    {
        bool beforeDispatch = State is "prepared" or "queued" or "admitted" or "blocked" or "stopped";
        bool afterDispatch = State is "dispatched" or "collecting" or "verifying" or "passed" or "failed" or "outcomeUnknown";
        if ((!beforeDispatch && !afterDispatch) || afterDispatch != Dispatched ||
            (CleanupVerified && State is not ("passed" or "failed" or "blocked" or "stopped")))
            throw new BridgeException("InvalidJobState", "Phase, dispatch and cleanup evidence are inconsistent.");
        if (ReconciliationSha256 is not null)
        {
            Engineering.EngineeringIdentityGuard.Sha256(ReconciliationSha256, "reconciliationSha256");
            if (State is not ("passed" or "failed")) throw new BridgeException("InvalidJobState", "Reconciliation evidence must identify a terminal observation.");
        }
    }

    public TestSuiteJobState RequestStop()
    {
        Validate();
        if (State is "passed" or "failed" or "blocked" or "stopped") return this;
        return Dispatched ? this with { StopRequested = true } : this with { StopRequested = true, State = "stopped", CleanupVerified = false };
    }

    public TestSuiteJobState Advance(string next)
    {
        Validate();
        bool allowed = (State, next) switch
        {
            ("prepared", "queued") or ("queued", "admitted") or ("admitted", "dispatched") or
            ("dispatched", "collecting") or ("collecting", "verifying") or
            ("verifying", "passed") or ("verifying", "failed") or
            ("prepared", "blocked") or ("queued", "blocked") or ("admitted", "blocked") => true,
            _ => false
        };
        if (!allowed || (StopRequested && !Dispatched)) throw new BridgeException("InvalidJobTransition", "Illegal phase transition or dispatch after stop.");
        return this with { State = next, Dispatched = Dispatched || next == "dispatched" };
    }

    public TestSuiteJobState LoseResponse()
    {
        Validate();
        if (State is "passed" or "failed" or "blocked" or "stopped" or "outcomeUnknown")
            throw new BridgeException("InvalidJobTransition", "A terminal or already unknown outcome cannot be overwritten by a later disconnect.");
        return Dispatched ? this with { State = "outcomeUnknown", CleanupVerified = false, ReconciliationSha256 = null }
            : this with { State = "blocked", CleanupVerified = false };
    }

    public TestSuiteJobState VerifyCleanup()
    {
        Validate();
        return State is "passed" or "failed" or "blocked" or "stopped" ? this with { CleanupVerified = true }
            : throw new BridgeException("UnreconciledOutcome", "Unknown or in-flight outcomes cannot release resources.");
    }

    public TestSuiteJobState Reconcile(string terminalState, string evidenceSha256)
    {
        Validate();
        if (State != "outcomeUnknown" || terminalState is not ("passed" or "failed")) throw new BridgeException("InvalidReconciliation", "Reconciliation requires an unknown dispatched outcome and observed terminal result.");
        Engineering.EngineeringIdentityGuard.Sha256(evidenceSha256, "reconciliationSha256");
        return this with { State = terminalState, ReconciliationSha256 = evidenceSha256, CleanupVerified = false };
    }
}
