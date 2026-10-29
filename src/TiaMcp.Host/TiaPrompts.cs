using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

/// <summary>Workflow prompts that walk a client through the documented tool sequences; they never act on their own.</summary>
[McpServerPromptType]
public sealed class TiaPrompts
{
    // A prompt is advertised only when every group whose tools it names is advertised.
    internal static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["safe_change"] = ["core"],
        ["project_health"] = ["core", "explorer"],
        ["block_test"] = ["core", "simulation"],
        ["diagnose_fault"] = ["explorer", "diagnostics"],
    };

    private const string OfflineOnly = "Work only on offline projects, copies, scratch projects and simulators I own; never on a production PLC or plant.";

    [McpServerPrompt(Name = "safe_change", Title = "Make a recoverable engineering change")]
    [Description("Walk through prepare, preview, checkpoint, apply and verify for one offline TIA Portal change.")]
    public static string SafeChange(
        [Description("The change to make, for example 'add input Speed:Real to FB MotorControl'.")] string goal,
        [Description("Project name, if known.")] string? project = null) => $"""
        Make this offline TIA Portal change: {goal}
        Project: {project ?? "ask me if more than one is open"}

        Follow the prepare, preview, checkpoint, apply, verify workflow:
        1. Find the session and project with `tia_discover` and `project_open`, then resolve exact targets with `project_inventory` or `object_resolve`. Never guess a path.
        2. Check `target_capabilities` and `feature_catalog` for the change kind. Stop and tell me if it is unsupported or unverified for this target.
        3. For interface, symbol or call changes, run `change_impact` and list the affected dependents.
        4. Call `change_prepare` and show me the preview: effects, original exports, warnings and the previewHash. Wait for my approval.
        5. Apply with `change_apply` using the exact changeId and previewHash, a new idempotencyKey, checkpointSave: true and compile: true. Ask me before choosing save: true.
        6. Verify with `change_status` and a fresh read (`block_read`, `tag_table_read`, `type_read` or `hardware_inspect`).
        7. If apply fails or its outcome is uncertain, never retry it. Report `change_status`; `change_restore` recovers the checkpoint into a new project directory, and `engineering_admission_reconcile` releases an unresolved admission only after the outcome is known.
        {OfflineOnly}
        """;

    [McpServerPrompt(Name = "project_health", Title = "Report project health")]
    [Description("Capture a saved review of a TIA Portal project and summarize its health report without changing it.")]
    public static string ProjectHealth([Description("Project name, if known.")] string? project = null) => $"""
        Give me a health report of the TIA Portal project {project ?? "that is open (ask me if there are several)"}.

        1. Find it with `tia_discover` and `project_open`.
        2. Save a persistent review with `project_snapshot_capture`.
        3. Run `project_health_report` on that reviewId.
        4. Summarize the findings by severity: native inconsistencies, protected or unavailable sources and relationship gaps. Name exact object paths and say which findings are incomplete or bounded.
        5. Suggest next steps, but make no changes.
        {OfflineOnly}
        """;

    [McpServerPrompt(Name = "block_test", Title = "Test block behavior in PLCSIM")]
    [Description("Download the project to a new owned PLCSIM Advanced instance, run stimulus and assertion steps, and report the result.")]
    public static string BlockTest(
        [Description("Behavior to check, for example 'Motor_On follows Start and drops on Stop'.")] string behavior,
        [Description("Project name, if known.")] string? project = null) => $"""
        Test this behavior in a new PLCSIM Advanced instance: {behavior}
        Project: {project ?? "ask me if more than one is open"}

        1. Open the project with `tia_discover` and `project_open` and find the PLC with `project_inventory`.
        2. Create an owned instance with `sim_create`, then load it with `simulation_download` using the returned downloadTicket. If the download refuses because simulation support is disabled, the `project_simulation_enable` change goes through `change_prepare` and `change_apply` first, with my approval.
        3. Power on and run it with `sim_state`, and find the exact symbols with `sim_tags`.
        4. Run `sim_test` with the stimulus writes and assertions for the behavior. Polling is not scan-synchronized, so allow for timer and scan delays.
        5. Report each assertion as passed or failed with the saved report, then release the instance with `sim_release` (deleteOwned: true unless I want to keep it).
        {OfflineOnly}
        """;

    [McpServerPrompt(Name = "diagnose_fault", Title = "Diagnose a PLC fault offline")]
    [Description("Plan a diagnosis from a saved project review, prepare a trace, analyze the recorded measurement and export a report.")]
    public static string DiagnoseFault([Description("The observed symptom, for example 'conveyor stops randomly after 2 h'.")] string symptom) => $"""
        Diagnose this PLC fault offline: {symptom}

        1. Use an existing saved review, or capture one with `project_snapshot_capture`.
        2. Call `diagnostic_plan` with the symptom as search terms and explicit hypotheses, and show me the candidate signals.
        3. Persist a trace specification with `trace_prepare` for the signals and trigger we agree on. I record the trace in TIA Portal and give you the measurement file.
        4. Import it with `trace_read` and evaluate the hypotheses with `trace_analyze` (or `trace_compare` against a good run).
        5. Export the result with `diagnostic_report_export`.
        Keep every hypothesis unresolved unless the measurement supports it, and never claim a fix without a new measurement.
        {OfflineOnly}
        """;
}
