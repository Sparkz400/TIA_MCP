using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed record HostOptions(string WorkerPath, string ArtifactsDirectory, string? ApiDirectory, int TimeoutSeconds, bool Diagnose,
    string SimulatorWorkerPath, string? SimulatorApiDirectory)
{
    public PackageIntakeLimits LibraryLimits { get; init; } = new();
    public string? GitExecutable { get; init; }
    // The one remote vci_push/vci_fetch use; local unless --git-allow-network-remote.
    public TiaMcp.Core.VersionControl.GitRemote? GitRemote { get; init; }
    public string? UaClientIdentity { get; init; }
    public bool NoNativeWorkers { get; init; }
    public bool PhysicalPlcOnline { get; init; }
    public bool CheckOpennessRegistration { get; init; }
    // Selected --toolsets groups; null advertises every tool.
    public IReadOnlySet<string>? Toolsets { get; init; }
    public bool AddInHandoff { get; init; }
    // Combined PLC library_integrate apply of the verified shapes runs by default (decision of 29 October 2026); --disable-library-integrate-apply turns it off.
    public bool LibraryIntegrateApply { get; init; } = true;
    // Off by default: reuse relies on TIA's save state, which is not verified for edits made by hand in the TIA user interface.
    public bool FingerprintReuse { get; init; }
    internal TiaMcp.Core.AddIns.AddInEnrollment? AddInEnrollment { get; init; }
    // Deliberately excluded from normal feature/catalog advertisements.
    public bool ComponentQualification { get; init; }
    public bool TestSuiteScopeProbe { get; init; }
    public bool TestSuiteQualification { get; init; }
    public bool TranslationQualification { get; init; }
    // safety_fparameter_set may write declared-writable F-parameters the qualified allowlist lacks; for discovery runs only.
    public bool SafetyFParameterDiscovery { get; init; }
    public string? WatchQualificationPoint { get; init; }
    public string? RuntimeQualificationPoint { get; init; }
    public string? HmiTextListQualificationProfile { get; init; }
    public string? HmiChangeQualificationProfile { get; init; }
    // --tia-version: 21 (default) runs the full V21 worker; 19 runs the read-only V19 adapter (X12) and advertises only its tools.
    public int TiaVersion { get; init; } = 21;

    public static HostOptions Parse(string[] args)
    {
        var root = FindRoot();
        var packaged = Directory.Exists(Path.Combine(root, "workers"));
        string? worker = null;
        var tiaVersion = 21;
        var tiaVersionGiven = false;
        var artifacts = Path.Combine(root, "artifacts");
        var simulator = packaged
            ? Path.Combine(root, "workers", "simulator", "TiaMcp.Simulator.V8.exe")
            : Path.Combine(root, "src", "TiaMcp.Simulator.V8", "bin", "Debug", "net48", "TiaMcp.Simulator.V8.exe");
        string? api = null;
        string? gitExecutable = null;
        string? gitRemote = null;
        var gitAllowNetworkRemote = false;
        string? uaClientIdentity = null;
        string? simulatorApi = null;
        var timeout = 120;
        var diagnose = false;
        var checkOpennessRegistration = false;
        var addInHandoff = false;
        var libraryIntegrateApply = true;
        var enableLibraryIntegrateApplyGiven = false;
        var fingerprintReuse = false;
        TiaMcp.Core.AddIns.AddInEnrollment? addInEnrollment = null;
#if OFFLINE_PACKAGE
        var noNativeWorkers = true;
#else
        var noNativeWorkers = false;
#endif
        var componentQualification = false;
        var physicalPlcOnline = false;
        var testSuiteScopeProbe = false;
        var testSuiteQualification = false;
        var translationQualification = false;
        var safetyFParameterDiscovery = false;
        string? watchQualificationPoint = null;
        string? hmiTextListQualification = null;
        string? hmiChangeQualification = null;
        string? runtimeQualificationPoint = null;
        IReadOnlySet<string>? toolsets = null;
        var toolsetsGiven = false;
        var libraryLimits = new PackageIntakeLimits();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == "--no-native-workers") { noNativeWorkers = true; continue; }
            if (option == "--physical-plc-online") { physicalPlcOnline = true; continue; }
            if (option == "--addin-handoff") { addInHandoff = true; continue; }
            if (option == "--git-allow-network-remote") { gitAllowNetworkRemote = true; continue; }
            if (option == "--enable-library-integrate-apply") { enableLibraryIntegrateApplyGiven = true; continue; } // the default; kept for existing launch configurations
            if (option == "--disable-library-integrate-apply") { libraryIntegrateApply = false; continue; }
            if (option == "--enable-fingerprint-reuse") { fingerprintReuse = true; continue; }
            if (option == "--diagnose") { diagnose = true; continue; }
            if (option == "--check-openness-registration") { checkOpennessRegistration = true; continue; }
            if (option == "--component-qualification") { componentQualification = true; continue; }
            if (option == "--testsuite-scope-probe") { testSuiteScopeProbe = true; continue; }
            if (option == "--translation-qualification") { translationQualification = true; continue; }
#if SAFETY_FPARAMETER_DISCOVERY
            // Only builds with -p:SafetyFParameterDiscovery=true, never the packaged ones, accept this option.
            if (option == "--safety-fparameter-discovery") { safetyFParameterDiscovery = true; continue; }
#endif
#if TESTSUITE_QUALIFICATION
            if (option == "--testsuite-qualification") { testSuiteQualification = true; continue; }
#endif
            if (++i >= args.Length) throw new ArgumentException("Missing value for " + option);
            switch (option)
            {
                case "--worker": worker = Path.GetFullPath(args[i]); break;
                case "--tia-version":
                    if (tiaVersionGiven || args[i] is not ("19" or "21")) throw new ArgumentException("--tia-version takes 19 or 21, once.");
                    tiaVersion = int.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture); tiaVersionGiven = true; break;
                case "--git-executable":
                    if (!Path.IsPathFullyQualified(args[i])) throw new ArgumentException("--git-executable requires an absolute local executable path.");
                    gitExecutable = Path.GetFullPath(args[i]); break;
                case "--git-remote":
                    if (gitRemote is not null) throw new ArgumentException("--git-remote may be given once.");
                    gitRemote = args[i]; break;
                case "--sim-worker": simulator = Path.GetFullPath(args[i]); break;
                case "--sim-api-dir": simulatorApi = Path.GetFullPath(args[i]); break;
                case "--artifacts": artifacts = Path.GetFullPath(args[i]); break;
                case "--ua-client-identity":
                    if (uaClientIdentity is not null || args[i] != "default" && !TiaMcp.Core.Security.ProtectedCredentialGuard.IsId(args[i]))
                        throw new ArgumentException("--ua-client-identity requires one default or 32-lowercase-hex name.");
                    uaClientIdentity = args[i]; break;
                case "--watch-qualification": watchQualificationPoint = args[i]; break;
                case "--hmi-text-list-qualification":
                    if (hmiTextListQualification is not null || args[i] is not ("unified-comfort" or "unified-pc"))
                        throw new ArgumentException("--hmi-text-list-qualification takes one of unified-comfort or unified-pc, once.");
                    hmiTextListQualification = args[i]; break;
                case "--hmi-change-qualification":
                    if (hmiChangeQualification is not null || args[i] is not ("classic-comfort" or "classic-rt-advanced" or "classic-basic" or "unified-comfort" or "unified-pc"))
                        throw new ArgumentException("--hmi-change-qualification takes one of classic-comfort, classic-rt-advanced, classic-basic, unified-comfort or unified-pc, once.");
                    hmiChangeQualification = args[i]; break;
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
                    // Whole seconds; a decimal such as 600.0 (JSON clients often send numbers that way) is accepted and rounded up.
                    if (!double.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
                        || double.IsNaN(seconds) || seconds < 5 || seconds > 900)
                        throw new ArgumentException("--timeout-seconds must be a number of seconds between 5 and 900 (got '" + args[i] + "').");
                    timeout = (int)Math.Ceiling(seconds);
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
                "host.watch_authoring_source_negative" or "host.project_upgrade" or "host.project_upgrade.beforeDispatch"))
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
        if (hmiTextListQualification is not null && (componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || watchQualificationPoint is not null))
            throw new ArgumentException("HMI text-list qualification requires its own sequential Host.");
        if (hmiChangeQualification is not null && (componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || watchQualificationPoint is not null || hmiTextListQualification is not null))
            throw new ArgumentException("HMI change qualification requires its own sequential Host.");
        if (noNativeWorkers && (checkOpennessRegistration || diagnose || componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || safetyFParameterDiscovery || watchQualificationPoint is not null || hmiTextListQualification is not null || hmiChangeQualification is not null))
            throw new ArgumentException("Native diagnosis and qualification cannot run with native workers disabled.");
        if (diagnose && addInHandoff) throw new ArgumentException("Add-In handoff requires the long-running Host, not diagnosis mode.");
        if (checkOpennessRegistration && (diagnose || addInHandoff)) throw new ArgumentException("Registration inspection cannot be combined with native diagnosis or Add-In handoff.");
        if (addInEnrollment is not null && !addInHandoff) throw new ArgumentException("--addin-enrollment requires --addin-handoff.");
        if (enableLibraryIntegrateApplyGiven && !libraryIntegrateApply)
            throw new ArgumentException("--enable-library-integrate-apply and --disable-library-integrate-apply exclude each other.");
        if (tiaVersion == 19 && (componentQualification || testSuiteScopeProbe || testSuiteQualification || translationQualification || safetyFParameterDiscovery ||
            watchQualificationPoint is not null || runtimeQualificationPoint is not null || hmiTextListQualification is not null || hmiChangeQualification is not null ||
            addInHandoff || enableLibraryIntegrateApplyGiven || !libraryIntegrateApply || fingerprintReuse))
            throw new ArgumentException("--tia-version 19 runs the read-only V19 adapter; qualification modes, Add-In handoff, library apply and fingerprint reuse options are V21-only.");
        worker ??= TiaWorkerPath(root, packaged, tiaVersion);
        if (gitAllowNetworkRemote && gitRemote is null) throw new ArgumentException("--git-allow-network-remote requires --git-remote.");
        TiaMcp.Core.VersionControl.GitRemote? remote = null;
        if (gitRemote is not null)
        {
            if (gitExecutable is null) throw new ArgumentException("--git-remote requires --git-executable.");
            try { remote = TiaMcp.Core.VersionControl.GitRemote.Parse(gitRemote, gitAllowNetworkRemote); }
            catch (BridgeException error) { throw new ArgumentException("--git-remote: " + error.Message); }
        }
        return new HostOptions(worker, artifacts, api, timeout, diagnose, simulator, simulatorApi) { PhysicalPlcOnline = physicalPlcOnline, CheckOpennessRegistration = checkOpennessRegistration, UaClientIdentity = uaClientIdentity, AddInHandoff = addInHandoff, LibraryIntegrateApply = libraryIntegrateApply, FingerprintReuse = fingerprintReuse, AddInEnrollment = addInEnrollment, NoNativeWorkers = noNativeWorkers, Toolsets = toolsets, GitExecutable = gitExecutable, GitRemote = remote, LibraryLimits = libraryLimits, ComponentQualification = componentQualification, TestSuiteScopeProbe = testSuiteScopeProbe, TestSuiteQualification = testSuiteQualification, TranslationQualification = translationQualification, SafetyFParameterDiscovery = safetyFParameterDiscovery, WatchQualificationPoint = watchQualificationPoint, RuntimeQualificationPoint = runtimeQualificationPoint, HmiTextListQualificationProfile = hmiTextListQualification, HmiChangeQualificationProfile = hmiChangeQualification, TiaVersion = tiaVersion };
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

    // The bundled or source-built worker for one TIA version; --worker overrides it.
    internal static string TiaWorkerPath(string root, bool packaged, int tiaVersion) => (packaged, tiaVersion) switch
    {
        (true, 19) => Path.Combine(root, "workers", "openness-v19", "TiaMcp.Openness.V19.exe"),
        (true, _) => Path.Combine(root, "workers", "openness", "TiaMcp.Openness.V21.exe"),
        (false, 19) => Path.Combine(root, "src", "TiaMcp.Openness.V19", "bin", "Debug", "net48", "TiaMcp.Openness.V19.exe"),
        (false, _) => Path.Combine(root, "src", "TiaMcp.Openness.V21", "bin", "Debug", "net48", "TiaMcp.Openness.V21.exe"),
    };

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TiaMcp.slnx"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
}
