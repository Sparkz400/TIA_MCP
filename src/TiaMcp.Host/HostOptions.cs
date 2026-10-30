using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed record HostOptions(string WorkerPath, string ArtifactsDirectory, string? ApiDirectory, int TimeoutSeconds, bool Diagnose,
    string SimulatorWorkerPath, string? SimulatorApiDirectory)
{
    public PackageIntakeLimits LibraryLimits { get; init; } = new();
    public string? GitExecutable { get; init; }
    public string? UaClientIdentity { get; init; }
    public bool NoNativeWorkers { get; init; }
    // Selected --toolsets groups; null advertises every tool.
    public IReadOnlySet<string>? Toolsets { get; init; }
    public bool AddInHandoff { get; init; }
    // Combined PLC library_integrate apply of the verified shapes runs on every Host (decision of 29 October 2026).
    public bool LibraryIntegrateApply { get; init; } = true;
    internal TiaMcp.Core.AddIns.AddInEnrollment? AddInEnrollment { get; init; }
    // Deliberately excluded from normal feature/catalog advertisements.
    public bool ComponentQualification { get; init; }
    public bool TestSuiteScopeProbe { get; init; }
    public bool TestSuiteQualification { get; init; }
    public bool TranslationQualification { get; init; }
    public string? WatchQualificationPoint { get; init; }
    public string? RuntimeQualificationPoint { get; init; }

    public static HostOptions Parse(string[] args)
    {
        var root = FindRoot();
        var packaged = Directory.Exists(Path.Combine(root, "workers"));
        var worker = packaged
            ? Path.Combine(root, "workers", "openness", "TiaMcp.Openness.V21.exe")
            : Path.Combine(root, "src", "TiaMcp.Openness.V21", "bin", "Debug", "net48", "TiaMcp.Openness.V21.exe");
        var artifacts = Path.Combine(root, "artifacts");
        var simulator = packaged
            ? Path.Combine(root, "workers", "simulator", "TiaMcp.Simulator.V8.exe")
            : Path.Combine(root, "src", "TiaMcp.Simulator.V8", "bin", "Debug", "net48", "TiaMcp.Simulator.V8.exe");
        string? api = null;
        string? gitExecutable = null;
        string? uaClientIdentity = null;
        string? simulatorApi = null;
        var timeout = 120;
        var diagnose = false;
        var addInHandoff = false;
        var libraryIntegrateApply = true;
        TiaMcp.Core.AddIns.AddInEnrollment? addInEnrollment = null;
#if OFFLINE_PACKAGE
        var noNativeWorkers = true;
#else
        var noNativeWorkers = false;
#endif
        var componentQualification = false;
        var testSuiteScopeProbe = false;
        var testSuiteQualification = false;
        var translationQualification = false;
        string? watchQualificationPoint = null;
        string? runtimeQualificationPoint = null;
        IReadOnlySet<string>? toolsets = null;
        var toolsetsGiven = false;
        var libraryLimits = new PackageIntakeLimits();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == "--no-native-workers") { noNativeWorkers = true; continue; }
            if (option == "--addin-handoff") { addInHandoff = true; continue; }
            if (option == "--enable-library-integrate-apply") { libraryIntegrateApply = true; continue; } // the default; kept for existing launch configurations
            if (option == "--diagnose") { diagnose = true; continue; }
            if (option == "--component-qualification") { componentQualification = true; continue; }
            if (option == "--testsuite-scope-probe") { testSuiteScopeProbe = true; continue; }
            if (option == "--translation-qualification") { translationQualification = true; continue; }
#if TESTSUITE_QUALIFICATION
            if (option == "--testsuite-qualification") { testSuiteQualification = true; continue; }
#endif
            if (++i >= args.Length) throw new ArgumentException("Missing value for " + option);
            switch (option)
            {
                case "--worker": worker = Path.GetFullPath(args[i]); break;
                case "--git-executable":
                    if (!Path.IsPathFullyQualified(args[i])) throw new ArgumentException("--git-executable requires an absolute local executable path.");
                    gitExecutable = Path.GetFullPath(args[i]); break;
                case "--sim-worker": simulator = Path.GetFullPath(args[i]); break;
                case "--sim-api-dir": simulatorApi = Path.GetFullPath(args[i]); break;
                case "--artifacts": artifacts = Path.GetFullPath(args[i]); break;
                case "--ua-client-identity":
                    if (uaClientIdentity is not null || args[i] != "default" && !TiaMcp.Core.Security.ProtectedCredentialGuard.IsId(args[i]))
                        throw new ArgumentException("--ua-client-identity requires one default or 32-lowercase-hex name.");
                    uaClientIdentity = args[i]; break;
                case "--watch-qualification": watchQualificationPoint = args[i]; break;
                case "--runtime-qualification":
                    if (runtimeQualificationPoint is not null || args[i] is not ("ua.write.afterSend" or "web.write.afterSend"))
                        throw new ArgumentException("--runtime-qualification takes one of ua.write.afterSend or web.write.afterSend.");
                    runtimeQualificationPoint = args[i]; break;
                case "--addin-enrollment":
                    if (addInEnrollment is not null) throw new ArgumentException("--addin-enrollment may be given once.");
                    addInEnrollment = TiaMcp.Core.AddIns.AddInEnrollment.Read(args[i]); break;
                case "--library-limits-file": libraryLimits = ReadLibraryLimits(args[i]); break;
                case "--toolsets":
                    if (toolsetsGiven) throw new ArgumentException("--toolsets may be given once.");
                    toolsets = ToolGroups.Parse(args[i]); toolsetsGiven = true; break;
                case "--api-dir": api = Path.GetFullPath(args[i]); break;
                case "--timeout-seconds":
                    if (!int.TryParse(args[i], out timeout) || timeout < 5 || timeout > 900)
                        throw new ArgumentException("--timeout-seconds must be between 5 and 900.");
                    break;
                default: throw new ArgumentException("Unknown option: " + option);
            }
        }
        if (watchQualificationPoint is not null)
        {
            if (!componentQualification) throw new ArgumentException("--watch-qualification requires --component-qualification.");
            if (watchQualificationPoint is not ("none" or "checkpoint.beforeArchive" or "checkpoint.afterArchive" or
                "import.before" or "import.after" or "save.before" or "save.after" or "retrieve.before" or "retrieve.after" or
                "host.watch_checkpoint" or "host.watch_apply_native" or "host.watch_restore_native" or
                "host.watch_authoring_source_negative"))
                throw new ArgumentException("Unknown watch qualification interruption point.");
        }
        if (runtimeQualificationPoint is not null && !componentQualification)
            throw new ArgumentException("--runtime-qualification requires --component-qualification.");
        if (testSuiteScopeProbe && (componentQualification || watchQualificationPoint is not null))
            throw new ArgumentException("Test Suite scope probing requires its own sequential Host.");
        if (testSuiteQualification && (componentQualification || testSuiteScopeProbe || watchQualificationPoint is not null))
            throw new ArgumentException("Test Suite qualification requires its own sequential Host.");
        if (translationQualification && (componentQualification || testSuiteScopeProbe || testSuiteQualification || watchQualificationPoint is not null))
            throw new ArgumentException("Translation qualification requires its own sequential Host.");
        if (noNativeWorkers && (diagnose || componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || watchQualificationPoint is not null))
            throw new ArgumentException("Native diagnosis and qualification cannot run with native workers disabled.");
        if (diagnose && addInHandoff) throw new ArgumentException("Add-In handoff requires the long-running Host, not diagnosis mode.");
        if (addInEnrollment is not null && !addInHandoff) throw new ArgumentException("--addin-enrollment requires --addin-handoff.");
        return new HostOptions(worker, artifacts, api, timeout, diagnose, simulator, simulatorApi) { UaClientIdentity = uaClientIdentity, AddInHandoff = addInHandoff, LibraryIntegrateApply = libraryIntegrateApply, AddInEnrollment = addInEnrollment, NoNativeWorkers = noNativeWorkers, Toolsets = toolsets, GitExecutable = gitExecutable, LibraryLimits = libraryLimits, ComponentQualification = componentQualification, TestSuiteScopeProbe = testSuiteScopeProbe, TestSuiteQualification = testSuiteQualification, TranslationQualification = translationQualification, WatchQualificationPoint = watchQualificationPoint, RuntimeQualificationPoint = runtimeQualificationPoint };
    }

    private static PackageIntakeLimits ReadLibraryLimits(string path)
    {
        using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 65_536) throw new ArgumentException("--library-limits-file exceeds 64 KiB.");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("--library-limits-file must contain a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out _))
                throw new ArgumentException("--library-limits-file permits only unique integer-valued limit properties.");
        var options = new JsonSerializerOptions(BridgeProtocol.Json)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 4
        };
        var limits = document.RootElement.Deserialize<PackageIntakeLimits>(options) ?? throw new ArgumentException("Missing library limits.");
        limits.Validate();
        return limits;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TiaMcp.slnx"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
}
