using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Role-level guidance only. Templates never manufacture project symbols or target settings.</summary>
public static class DiagnosticTemplates
{
    public static JsonArray All() => new(
        Template("intermittentStop", "Which condition first diverges while enable is asserted?", ["enable", "internalCommand", "finalOutput", "feedback", "interlock", "state", "fault", "competingWriter"], "Final output falling edge; validate Bool signal and trigger support."),
        Template("missedEdge", "Does the consumer see the producer transition at its sampling point?", ["producer", "consumerInput", "edgeMemory", "taskState"], "Producer edge; one-scan or intra-task events require a qualified acquisition route."),
        Template("timer", "Do timer input, elapsed value and public output match the observed sequence?", ["timerInput", "timerElapsed", "timerOutput", "preset", "reset"], "Timer input/output edge; only observable public timer state, no assumed private instance layout."),
        Template("interlock", "Which interlock changes before the output is withdrawn?", ["command", "interlockSummary", "suspectInterlock", "output", "fault"], "Output falling edge, with pre-trigger context for upstream conditions."),
        Template("sequenceStall", "Which transition guard prevents the next step?", ["step", "nextStepGuard", "command", "feedback", "timeout", "fault"], "Supported state threshold or guard edge; no assumed compound trigger."),
        Template("competingWriters", "Does the final storage disagree with an earlier writer's command?", ["internalCommand", "finalOutput", "writerGuard", "enable", "taskState"], "Final output change; end-of-OB sampling may hide intermediate writes."),
        Template("analogNoise", "Do threshold crossings correlate with noise or hysteresis state?", ["rawInput", "filteredInput", "threshold", "hysteresisState", "output"], "One supported threshold; preserve engineering units and time base."),
        Template("communicationTimeout", "Do request, acknowledgement and timeout state diverge?", ["request", "acknowledgement", "sequenceNumber", "timeoutElapsed", "connectionState", "error"], "Timeout/fault edge; preserve clock uncertainty across devices."));
    private static JsonObject Template(string id, string question, string[] roles, string trigger) => new() {
        ["id"] = id, ["diagnosticQuestion"] = question, ["signalRoles"] = D.Strings(roles), ["triggerGuidance"] = trigger,
        ["budgetPolicy"] = "Start with the output/command pair and direct hypothesis dependencies; add a role only with captured evidence and a reason. Revalidate substitutions and repeated captures.",
        ["automaticSignalSelection"] = false };
}
