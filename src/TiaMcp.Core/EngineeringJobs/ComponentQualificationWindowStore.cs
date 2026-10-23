using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.EngineeringJobs;

public sealed record ComponentWindowManifest(int SchemaVersion, string Id, string ComponentId, string Workspace,
    string ProjectPath, string PlcPath, string PlanId, string PlanPreviewHash, string PlanDirectory,
    string SourceDirectory, string SourceSnapshotPath, string SourceSnapshotSha256, bool SourceSnapshotIsDirectory, string OriginalSourcePath, bool OriginalSourceIsDirectory,
    string SourceLibraryId, string AdapterSha256, int InitialNativeProcessId, string InitialNativeSessionEpoch,
    IReadOnlyList<EngineeringMutationResource> Resources);
public sealed record ComponentWindowChild(string Step, string RequestSha256, string State,
    EngineeringArtifactReference? Response, string? IdempotencyKeyHash);
public sealed record ComponentWindowReservation(string Id, int Revision, string State, ComponentWindowManifest Manifest,
    string ManifestSha256, IReadOnlyList<string> ResourceKeys, string OwnerCapabilitySha256, int OwnerHostProcessId,
    DateTime OwnerHostBirthUtc, bool InstallationAllowed, IReadOnlyList<ComponentWindowChild> Children,
    string? PreviousSha256, DateTimeOffset RecordedAt, EngineeringArtifactReference? Completion, string Transition);

/// <summary>
/// Qualification-only lifetime ownership. This store never dispatches native work, resumes installation,
/// interprets elapsed time as consent, or releases resources on an individual child response.
/// All callers use the configured canonical Host artifact store; independently configured stores are not federated.
/// </summary>
public sealed class ComponentQualificationWindowStore
{
    private readonly string root;
    private readonly string directory;
    private readonly string lockPath;
    private static readonly string[] Sequence = ["start", "applyLibrary", "verifyImport", "prepareGraphical", "applyGraphical", "verifyInstallation", "observeOriginal", "recover", "observeRestored"];

    public ComponentQualificationWindowStore(string artifactRoot)
    {
        root = Path.GetFullPath(artifactRoot); PackageStorage.EnsureNoLinks(root);
        directory = PackageStorage.Under(root, "engineering-component-windows");
        Directory.CreateDirectory(directory);
        string operations = PackageStorage.Under(root, "engineering-operations"); Directory.CreateDirectory(operations);
        lockPath = Path.Combine(operations, "store.lock");
    }

    public (ComponentWindowReservation Reservation, string Capability) Acquire(ComponentWindowManifest manifest)
    {
        manifest = Freeze(manifest); ValidateManifest(root, manifest);
        using var held = PackageStorage.Lock(lockPath);
        if (Directory.Exists(manifest.Workspace) || File.Exists(manifest.Workspace)) Fail("ComponentWorkspaceExists", "The complete descendant workspace must be absent before admission.");
        var keys = Keys(manifest.Resources);
        var occupied = new EngineeringOperationStore(root).PendingResourceKeysUnderLock()
            .Concat(EngineeringMutationAdmissionStore.PendingResourceKeysUnderLock(root));
        if (occupied.Any(a => keys.Any(b => EngineeringResourceKey.Overlaps(a, b)))) Fail("ResourceBusy", "A durable owner already reserves a component resource.");
        if (Directory.Exists(PackageStorage.Under(directory, manifest.Id))) Fail("ComponentReplayRefused", "A component window identity is never reused.");
        string capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        using var process = Process.GetCurrentProcess();
        var record = new ComponentWindowReservation(manifest.Id, 0, "active", manifest, ManifestHash(manifest), keys,
            TokenHash(capability), process.Id, process.StartTime.ToUniversalTime(), true, [], null, DateTimeOffset.UtcNow, null, "acquire");
        string staging = PackageStorage.Under(directory, ".preparing-" + manifest.Id); Directory.CreateDirectory(staging);
        Write(record, staging); PackageStorage.PublishDirectory(staging, PackageStorage.Under(directory, manifest.Id));
        return (record, capability);
    }

    public ComponentWindowReservation Read(string id)
    { using var held = PackageStorage.Lock(lockPath); return ReadUnderLock(root, id); }

    public ComponentWindowReservation Resume(string id, int revision, string capability) => Change(id, revision, "ownerResume", r =>
    {
        Capability(r, capability); if (Terminal(r)) Fail("ComponentReplayRefused", "A terminal window cannot resume.");
        using var current = Process.GetCurrentProcess();
        if (r.OwnerHostProcessId == current.Id && r.OwnerHostBirthUtc == current.StartTime.ToUniversalTime())
            Fail("ComponentFreshHostRequired", "Owner resume requires an actual fresh Host process, not a repeated call in the live owner.");
        if (r.OwnerHostProcessId != current.Id || r.OwnerHostBirthUtc != current.StartTime.ToUniversalTime())
        {
            try
            {
                using var previous = Process.GetProcessById(r.OwnerHostProcessId);
                if (!previous.HasExited && previous.StartTime.ToUniversalTime() == r.OwnerHostBirthUtc)
                    Fail("ResourceBusy", "The exact prior Host is still alive; ownership cannot be stolen.");
            }
            catch (ArgumentException) { /* Exact previous PID no longer exists. */ }
        }
        return r with { OwnerHostProcessId = current.Id, OwnerHostBirthUtc = current.StartTime.ToUniversalTime(), InstallationAllowed = false };
    });

    public ComponentWindowReservation BeginChild(string id, int revision, string capability, string step,
        string requestSha256, IReadOnlyList<EngineeringMutationResource> nativeResources) => Change(id, revision, "childIntent", r =>
    {
        Owner(r, capability); EngineeringIdentityGuard.Sha256(requestSha256, "componentChildRequestSha256");
        int index = r.Children.Count;
        bool recovery = step is "recover" or "observeRestored" or "observeRecoveryOriginal";
        if (!recovery) Active(r);
        if (r.Children.LastOrDefault()?.State == "dispatchIntent") Fail("ComponentOutcomeUnknown", "The retained dispatch intent must first be recorded as unknown; it is never replayed.");
        if (!r.InstallationAllowed && Sequence.Take(6).Contains(step)) Fail("ComponentFreshSessionRequired", "Owner resume permits observation and recovery, never installation continuation.");
        if (r.InstallationAllowed && index >= 6) Fail("ComponentFreshSessionRequired", "Acceptance observations require a fresh owned Host lifecycle.");
        if (NextStep(r) != step) Fail("ComponentReplayRefused", "Only the next exact component child or recovery-only suffix may dispatch once.");
        RequireContained(r.Manifest, nativeResources);
        string? key = step is "applyLibrary" or "applyGraphical" ? TokenHash(r.Manifest.ComponentId + ":" + (step == "applyLibrary" ? "library" : "graphical")) : null;
        return r with { Children = r.Children.Append(new ComponentWindowChild(step, requestSha256, "dispatchIntent", null, key)).ToArray() };
    });

    public ComponentWindowReservation Returned(string id, int revision, string capability, EngineeringArtifactReference response) => Change(id, revision, "childReturned", r =>
    {
        Owner(r, capability); if (r.Children.LastOrDefault()?.State != "dispatchIntent") Fail("ComponentReplayRefused", "No exact pending child can accept a return.");
        VerifyArtifact(root, response); ComponentWindowEvidence.ValidateChild(root, r, response);
        return r with { Children = ReplaceLast(r.Children, r.Children.Last() with { State = "returned", Response = response }) };
    });

    public ComponentWindowReservation Unknown(string id, int revision, string capability, EngineeringArtifactReference? failure = null) => Change(id, revision, "childUnknown", r =>
    {
        Owner(r, capability); if (r.Children.LastOrDefault()?.State != "dispatchIntent") Fail("ComponentReplayRefused", "Only the dispatched child can become unknown.");
        if (failure is not null) VerifyArtifact(root, failure);
        return r with { State = "outcomeUnknown", Children = ReplaceLast(r.Children, r.Children.Last() with { State = "outcomeUnknown", Response = failure }) };
    });

    // Only the trusted adapter may submit an exact retained native child receipt. Public completion never accepts a boolean.
    public ComponentWindowReservation ReconcileChild(string id, int revision, string capability, EngineeringArtifactReference response) => Change(id, revision, "childReconciled", r =>
    {
        Owner(r, capability); if (r.Children.LastOrDefault()?.State is not ("dispatchIntent" or "outcomeUnknown")) Fail("ComponentReplayRefused", "Only the latest exact unresolved child can be reconciled.");
        VerifyArtifact(root, response); ComponentWindowEvidence.ValidateChild(root, r, response);
        return r with { State = r.Children.Take(r.Children.Count - 1).Any(c => c.State == "outcomeUnknown") ? "outcomeUnknown" : "active", Children = ReplaceLast(r.Children, r.Children.Last() with { State = "reconciled", Response = response }) };
    });

    public ComponentWindowReservation Complete(string id, int revision, string capability, EngineeringArtifactReference completion) => Change(id, revision, "complete", r =>
    {
        Owner(r, capability); Active(r);
        if (r.InstallationAllowed || !r.Children.Select(c => c.Step).SequenceEqual(Sequence)) Fail("ComponentAcceptanceIncomplete", "Both reviewed children and all fresh original/recovery observations are required; recovery-only is not installation acceptance.");
        VerifyArtifact(root, completion);
        ComponentWindowEvidence.Validate(root, r, completion);
        return r with { State = "released", Completion = completion };
    });

    public ComponentWindowReservation ReconcileRecovery(string id, int revision, string capability, EngineeringArtifactReference completion) => Change(id, revision, "recoveryReconciled", r =>
    {
        Owner(r, capability);
        if (Terminal(r) || r.InstallationAllowed || r.Children.LastOrDefault()?.Step != "observeRecoveryOriginal" || r.Children.Last().State is not ("returned" or "reconciled"))
            Fail("ComponentAcceptanceIncomplete", "Recovery-only reconciliation requires fresh restored and current-original observations.");
        VerifyArtifact(root, completion); ComponentWindowEvidence.ValidateRecovery(root, r, completion);
        return r with { State = "recoveryReconciled", Completion = completion };
    });

    public static string ManifestHash(ComponentWindowManifest manifest) => NativeHash(JsonSerializer.SerializeToNode(manifest, BridgeProtocol.Json)!);
    public static string? NextStep(ComponentWindowReservation r)
    {
        if (Terminal(r) || r.Children.LastOrDefault()?.State == "dispatchIntent") return null;
        if (r.InstallationAllowed) return r.State == "active" && r.Children.Count < 6 ? Sequence[r.Children.Count] : null;
        if (r.Children.LastOrDefault()?.Step == "recover") return r.Children.Last().State is "returned" or "reconciled" ? "observeRestored" : null;
        if (r.Children.LastOrDefault()?.Step == "observeRestored" && !r.Children.Select(c => c.Step).SequenceEqual(Sequence))
            return r.Children.Last().State is "returned" or "reconciled" ? "observeRecoveryOriginal" : null;
        if (r.Children.Any(c => c.Step is "recover" or "observeRestored")) return null;
        if (r.Children.Count == 6 && r.Children.All(c => c.State is "returned" or "reconciled")) return "observeOriginal";
        return r.Children.Count >= 2 ? "recover" : null;
    }
    public static string NativeHash(JsonNode value)
    {
        JsonNode? Sort(JsonNode? node) => node switch {
            JsonObject obj => new JsonObject(obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new KeyValuePair<string, JsonNode?>(x.Key, Sort(x.Value)))),
            JsonArray array => new JsonArray(array.Select(Sort).ToArray()), _ => node?.DeepClone() };
        return TokenHash(Sort(value)!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    public ComponentWindowReservation RequireOwner(string id, string capability)
    { var record = Read(id); Owner(record, capability); if (Terminal(record)) Fail("ComponentReplayRefused", "The component lifetime is terminal."); return record; }
    public static void RequireContained(ComponentWindowManifest manifest, IReadOnlyList<EngineeringMutationResource> resources)
    {
        if (resources is null || resources.Count is < 1 or > 64) Fail("MutationScopeIncomplete", "The child requires a bounded exact native resource scope.");
        var parent = Keys(manifest.Resources);
        foreach (var key in Keys(resources))
            if (!parent.Any(p => Contains(p, key))) Fail("ComponentResourceEscape", "A child resource is outside the complete admitted component manifest.");
    }

    public static void ValidateManifest(string artifactRoot, ComponentWindowManifest m)
    {
        if (m is null || m.SchemaVersion != 1 || m.Resources is null || m.Resources.Count != 6 || m.InitialNativeProcessId <= 0 ||
            string.IsNullOrWhiteSpace(m.InitialNativeSessionEpoch) || string.IsNullOrWhiteSpace(m.PlcPath) || string.IsNullOrWhiteSpace(m.SourceLibraryId))
            Fail("MutationScopeIncomplete", "A complete native-resolved qualification manifest is required.");
        PackageStorage.Id(m.Id);
        if (m.ComponentId != "component-" + m.Id || m.Workspace != PackageStorage.Under(artifactRoot, "q/c" + m.Id[..12]))
            Fail("ComponentResourceEscape", "The qualification workspace must be the exact reserved short descendant.");
        if (!m.PlanId.StartsWith("lib-", StringComparison.Ordinal) || !m.ProjectPath.EndsWith(".ap21", StringComparison.OrdinalIgnoreCase))
            Fail("MutationScopeIncomplete", "Exact original plan and target project are required.");
        foreach (string digest in new[] { m.PlanPreviewHash, m.SourceSnapshotSha256, m.AdapterSha256 }) EngineeringIdentityGuard.Sha256(digest, "componentManifestHash");
        var expected = new EngineeringMutationResource[] {
            new("project", Path.GetDirectoryName(m.ProjectPath)!, "targetProjectAndCompanions"),
            new("workspace", m.PlanDirectory, "reviewedPlanAndSourceChecks"),
            new("library", m.SourceDirectory, "pinnedWorkingSource"),
            new("library", m.SourceSnapshotPath, "immutableSourceSnapshot"),
            new("library", m.OriginalSourcePath, "immutableOriginalSource"),
            new("workspace", m.Workspace, "allPreparationJournalsClaimsCheckpointsReadbackAndRecovery") };
        if (Hash(expected.OrderBy(x => x.Effect).ToArray()) != Hash(m.Resources.OrderBy(x => x.Effect).ToArray()))
            Fail("MutationScopeIncomplete", "The manifest must include exactly target companions, plan artifacts, working source, original snapshot, and the complete future workspace.");
        _ = Keys(m.Resources); _ = EngineeringResourceKey.ForPath("project", m.ProjectPath);
        if (m.Resources.Where(r => r.Path != m.Workspace).Any(r => Contains(EngineeringResourceKey.ForPath(r.Kind, r.Path), EngineeringResourceKey.ForPath("workspace", m.Workspace)) ||
            Contains(EngineeringResourceKey.ForPath("workspace", m.Workspace), EngineeringResourceKey.ForPath(r.Kind, r.Path))))
            Fail("ComponentResourceEscape", "The new workspace must be disjoint from all original resources.");
        if (!Directory.Exists(m.PlanDirectory) || !Directory.Exists(m.SourceDirectory) ||
            (m.SourceSnapshotIsDirectory ? !Directory.Exists(m.SourceSnapshotPath) : !File.Exists(m.SourceSnapshotPath)) ||
            (m.OriginalSourceIsDirectory ? !Directory.Exists(m.OriginalSourcePath) : !File.Exists(m.OriginalSourcePath)))
            Fail("MutationScopeIncomplete", "Original source and prepared-plan resources must already exist.");
    }

    internal static IReadOnlyList<string> PendingResourceKeysUnderLock(string artifactRoot) => Records(artifactRoot)
        .Where(r => !Terminal(r)).SelectMany(r => r.ResourceKeys).ToArray();

    private ComponentWindowReservation Change(string id, int revision, string transition, Func<ComponentWindowReservation, ComponentWindowReservation> update)
    {
        using var held = PackageStorage.Lock(lockPath); var prior = ReadUnderLock(root, id);
        if (prior.Revision != revision) Fail("RevisionConflict", "The component reservation changed; reread its exact revision.");
        var next = update(prior) with { Revision = revision + 1, PreviousSha256 = Hash(prior), RecordedAt = DateTimeOffset.UtcNow, Transition = transition };
        ValidateRecord(root, prior, next); Write(next, PackageStorage.Under(directory, id)); return next;
    }
    private static IEnumerable<ComponentWindowReservation> Records(string root)
    {
        string path = PackageStorage.Under(root, "engineering-component-windows"); if (!Directory.Exists(path)) yield break;
        var folders = Directory.GetDirectories(path); if (folders.Length > 10000) Fail("OperationStoreLimit", "Too many component reservations.");
        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            if (name.StartsWith(".preparing-", StringComparison.Ordinal) && name.Length == 43 && name[11..].All(char.IsAsciiHexDigit)) continue;
            yield return ReadUnderLock(root, name);
        }
    }
    private static ComponentWindowReservation ReadUnderLock(string root, string id)
    {
        PackageStorage.Id(id); string path = PackageStorage.Under(root, "engineering-component-windows/" + id);
        if (!Directory.Exists(path)) Fail("OperationNotFound", "Component window was not found.");
        var files = Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length is < 1 or > 96) Fail("JournalInvalid", "Component reservation revisions must be bounded.");
        ComponentWindowReservation? previous = null;
        for (int i = 0; i < files.Length; i++)
        {
            if (Path.GetFileName(files[i]) != i.ToString("D8") + ".json" || new FileInfo(files[i]).Length > 2 * 1024 * 1024) Fail("JournalInvalid", "Component revisions must be contiguous and bounded.");
            var record = PackageStorage.Load<ComponentWindowReservation>(files[i]);
            if (record.Id != id || record.Revision != i) Fail("JournalInvalid", "Component reservation identity changed.");
            ValidateRecord(root, previous, record); previous = record;
        }
        return previous!;
    }
    private static void ValidateRecord(string root, ComponentWindowReservation? prior, ComponentWindowReservation r)
    {
        ValidateManifest(root, r.Manifest);
        if (r.Id != r.Manifest.Id || r.ManifestSha256 != ManifestHash(r.Manifest) || !Keys(r.Manifest.Resources).SequenceEqual(r.ResourceKeys) ||
            r.OwnerHostProcessId <= 0 || r.OwnerHostBirthUtc.Kind != DateTimeKind.Utc || r.Children is null || r.Children.Count > Sequence.Length ||
            r.State is not ("active" or "outcomeUnknown" or "released" or "recoveryReconciled")) Fail("JournalInvalid", "Component reservation scope or state is invalid.");
        EngineeringIdentityGuard.Sha256(r.OwnerCapabilitySha256, "ownerCapabilitySha256");
        for (int i = 0; i < r.Children.Count; i++)
        {
            var c = r.Children[i];
            bool recoverySuffix = r.Children.Take(i + 1).Any(x => x.Step == "recover");
            if (c.State is not ("dispatchIntent" or "outcomeUnknown" or "returned" or "reconciled") ||
                i < r.Children.Count - 1 && c.State == "dispatchIntent" ||
                !recoverySuffix && c.Step != Sequence[i] ||
                recoverySuffix && (c.Step == "recover" ? i < 2 || r.Children.Take(i).Any(x => x.Step == "recover") : c.Step == "observeRestored" ? r.Children[i - 1].Step != "recover" : c.Step != "observeRecoveryOriginal" || r.Children[i - 1].Step != "observeRestored"))
                Fail("JournalInvalid", "Component child order/outcome is invalid.");
            EngineeringIdentityGuard.Sha256(c.RequestSha256, "childRequestSha256");
            string? key = c.Step is "applyLibrary" or "applyGraphical" ? TokenHash(r.Manifest.ComponentId + ":" + (c.Step == "applyLibrary" ? "library" : "graphical")) : null;
            if (c.IdempotencyKeyHash != key || c.State is "returned" or "reconciled" && c.Response is null) Fail("JournalInvalid", "Exact inner idempotency or returned evidence is missing.");
            if (c.Response is not null) VerifyArtifact(root, c.Response);
        }
        if (r.State != "recoveryReconciled" && (r.State == "outcomeUnknown") != r.Children.Any(c => c.State == "outcomeUnknown") || Terminal(r) && r.Completion is null)
            Fail("JournalInvalid", "Component lifetime cannot discard an unresolved child or final evidence.");
        if (r.Completion is not null) VerifyArtifact(root, r.Completion);
        if (prior is null)
        {
            if (r.Revision != 0 || r.State != "active" || r.Children.Count != 0 || r.PreviousSha256 is not null || !r.InstallationAllowed || r.Completion is not null || r.Transition != "acquire")
                Fail("JournalInvalid", "Invalid initial component reservation.");
            return;
        }
        if (r.PreviousSha256 != Hash(prior) || r.ManifestSha256 != prior.ManifestSha256 || r.OwnerCapabilitySha256 != prior.OwnerCapabilitySha256 ||
            r.RecordedAt < prior.RecordedAt || Terminal(prior) || !prior.InstallationAllowed && r.InstallationAllowed ||
            r.Children.Count < prior.Children.Count || r.Children.Count > prior.Children.Count + 1) Fail("JournalInvalid", "Component immutable identity or ownership transition changed.");
        for (int i = 0; i < Math.Max(0, prior.Children.Count - 1); i++)
            if (Hash(prior.Children[i]) != Hash(r.Children[i])) Fail("JournalInvalid", "An earlier child receipt was replaced.");
        if (prior.Children.Count > 0)
        {
            var old = prior.Children.Last(); var now = r.Children[prior.Children.Count - 1];
            if (old.RequestSha256 != now.RequestSha256 || old.IdempotencyKeyHash != now.IdempotencyKeyHash ||
                old.State is "returned" or "reconciled" && Hash(old) != Hash(now) ||
                old.State == "outcomeUnknown" && now.State is not ("outcomeUnknown" or "reconciled"))
                Fail("JournalInvalid", "A child intent or completed receipt cannot be replayed/replaced.");
        }
        var expected = prior with { Revision = r.Revision, PreviousSha256 = r.PreviousSha256, RecordedAt = r.RecordedAt, Transition = r.Transition };
        switch (r.Transition)
        {
            case "ownerResume": expected = expected with { OwnerHostProcessId = r.OwnerHostProcessId, OwnerHostBirthUtc = r.OwnerHostBirthUtc, InstallationAllowed = false }; break;
            case "childIntent":
                if (NextStep(prior) != r.Children.Last().Step || r.Children.Count != prior.Children.Count + 1 ||
                    r.Children.Last().State != "dispatchIntent" || r.Children.Last().Response is not null)
                    Fail("JournalInvalid", "Invalid new component child intent.");
                expected = expected with { Children = r.Children }; break;
            case "childReturned":
            case "childUnknown":
            case "childReconciled":
                if (r.Children.Count != prior.Children.Count || prior.Children.Count == 0 ||
                    (r.Transition == "childReconciled" ? prior.Children.Last().State is not ("dispatchIntent" or "outcomeUnknown") : prior.Children.Last().State != "dispatchIntent"))
                    Fail("JournalInvalid", "Invalid component child outcome transition.");
                string state = r.Transition == "childReturned" ? "returned" : r.Transition == "childUnknown" ? "outcomeUnknown" : "reconciled";
                if (r.Children.Last().State != state) Fail("JournalInvalid", "Component outcome transition disagrees with child state.");
                expected = expected with { State = state == "outcomeUnknown" || prior.Children.Take(prior.Children.Count - 1).Any(c => c.State == "outcomeUnknown") ? "outcomeUnknown" : "active", Children = ReplaceLast(prior.Children, prior.Children.Last() with { State = state, Response = r.Children.Last().Response }) }; break;
            case "complete":
                if (prior.State != "active" || prior.InstallationAllowed || prior.Children.Count != Sequence.Length || prior.Children.Any(c => c.State is not ("returned" or "reconciled")) || r.Completion is null)
                    Fail("JournalInvalid", "Incomplete component acceptance cannot release ownership.");
                expected = expected with { State = "released", Completion = r.Completion }; break;
            case "recoveryReconciled":
                if (prior.InstallationAllowed || prior.Children.LastOrDefault()?.Step != "observeRecoveryOriginal" || prior.Children.Last().State is not ("returned" or "reconciled") || r.Completion is null)
                    Fail("JournalInvalid", "Incomplete recovery-only reconciliation cannot release ownership.");
                expected = expected with { State = "recoveryReconciled", Completion = r.Completion }; break;
            default: Fail("JournalInvalid", "Unknown component reservation transition."); break;
        }
        if (Hash(expected) != Hash(r)) Fail("JournalInvalid", "Component transition modified unrelated immutable state.");
    }
    private static void Owner(ComponentWindowReservation r, string capability)
    {
        Capability(r, capability); using var process = Process.GetCurrentProcess();
        if (r.OwnerHostProcessId != process.Id || r.OwnerHostBirthUtc != process.StartTime.ToUniversalTime()) Fail("ComponentOwnerResumeRequired", "The retained capability must explicitly resume in this fresh Host.");
    }
    private static void Capability(ComponentWindowReservation r, string capability)
    {
        if (capability is null || capability.Length != 64 || !capability.All(char.IsAsciiHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TokenHash(capability)), Encoding.ASCII.GetBytes(r.OwnerCapabilitySha256)))
            Fail("ComponentOwnerMismatch", "The exact retained owner capability is required.");
    }
    private static void Active(ComponentWindowReservation r)
    { if (r.State != "active" || r.Children.LastOrDefault()?.State is "dispatchIntent" or "outcomeUnknown") Fail("ComponentOutcomeUnknown", "An unresolved component child retains every resource and is never replayed."); }
    private static bool Terminal(ComponentWindowReservation r) => r.State is "released" or "recoveryReconciled";
    internal static void VerifyArtifact(string root, EngineeringArtifactReference a)
    {
        if (a is null) Fail("ArtifactInvalid", "Retained evidence is required.");
        EngineeringIdentityGuard.Sha256(a.Sha256, "componentEvidenceSha256"); string path = PackageStorage.Under(root, PackageStorage.Relative(a.RelativePath));
        if (!File.Exists(path) || PackageStorage.FileHash(path) != a.Sha256) Fail("ArtifactChanged", "Component evidence is missing or changed.");
    }
    internal static string EvidencePath(string root, EngineeringArtifactReference a) => PackageStorage.Under(root, PackageStorage.Relative(a.RelativePath));
    internal static string TokenHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool Contains(string parent, string child)
    { string p = parent[(parent.IndexOf(':') + 1)..], c = child[(child.IndexOf(':') + 1)..]; return c == p || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal); }
    private static string[] Keys(IReadOnlyList<EngineeringMutationResource> resources) => resources.Select(r => EngineeringResourceKey.ForPath(r.Kind, r.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static ComponentWindowChild[] ReplaceLast(IReadOnlyList<ComponentWindowChild> values, ComponentWindowChild next) => values.Take(values.Count - 1).Append(next).ToArray();
    private static T Freeze<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, BridgeProtocol.Json), BridgeProtocol.Json)!;
    private static string Hash<T>(T value) => CanonicalJson.Sha256(JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!);
    private static void Write(ComponentWindowReservation r, string directory) => PackageStorage.Save(Path.Combine(directory, r.Revision.ToString("D8") + ".json"), r);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new BridgeException(code, message);
}
