using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--fake-worker")) return await FakeWorker.RunAsync(args);
        if (args.Length == 2 && args[0] is "--handoff-server" or "--handoff-client") return await AddInReceiverTests.Child(args[0], args[1]);
        if (args.Length == 1 && args[0] == "--identity-child") { Console.WriteLine("ready"); Console.ReadLine(); return 0; }
        if (args.Length == 2 && args[0] == "--verify-worker")
        {
            try
            {
                WorkerSafetyGuard.Verify(args[1]);
                Console.WriteLine("PASS Compiled worker keeps discovery/attachment descriptors disposal-free and isolates closure of its own qualification Portal. No Siemens assembly was loaded.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL Worker process-lifetime safety guard" + Environment.NewLine + exception);
                return 1;
            }
        }
        var typedGraphicalOnly = args.Length == 1 && args[0] == "--typed-graphical";
        var bridgeOnly = args.Length == 1 && args[0] == "--bridge-only";
        var ownerOnly = args.Length == 1 && args[0] == "--addin-owner-only";
        var handoffOnly = args.Length == 1 && args[0] == "--addin-handoff-only";
        var receiverOnly = args.Length == 1 && args[0] == "--addin-receiver-only";
        if (args.Length != 0 && !typedGraphicalOnly && !bridgeOnly && !ownerOnly && !handoffOnly && !receiverOnly)
        {
            Console.Error.WriteLine("Usage: TiaMcp.Tests [--verify-worker PATH | --typed-graphical | --bridge-only | --addin-owner-only | --addin-handoff-only | --addin-receiver-only]");
            return 2;
        }

        (string Name, Func<Task> Run)[] tests =
        [
            ("Add-In receiver survives actual process crash and unread completion without replay", AddInReceiverTests.ExchangeAndCrash),
            ("Add-In receiver refuses actual same-user impostors and malformed bounded frames", AddInReceiverTests.ImpostorAndMalformed),
            ("Add-In receiver bounds connections deadlines and disconnect consumption", AddInReceiverTests.CapacityDeadlineAndDisconnect),
            ("Add-In receiver retains interrupted consumption after actual peer exit", AddInReceiverTests.PeerExit),
            ("Add-In handoff completion survives lost replies and restart without replay", AddInHandoffTests.CompletionAndRestart),
            ("Add-In handoff binds owners and consumes cancellation or changed context", AddInHandoffTests.OwnerAndCancellation),
            ("Add-In handoff uses monotonic expiry and bounds pending requests and retention", AddInHandoffTests.ExpiryAndLimits),
            ("Add-In handoff retains failed publication and refuses changed artifacts", AddInHandoffTests.PublicationAndTamper),
            ("Add-In handoff rejects malformed inputs unavailable authority and competing owners", AddInHandoffTests.InvalidInputsAndAuthority),
            ("Add-In handoff enrollment binds the owner's Add-In loader and keeps stale-context and one-use refusals", AddInEnrollmentTests.BindsOwnerLoader),
            ("Add-In handoff enrollment pins the published assembly and configuration identities", AddInEnrollmentTests.PinnedHashes),
            ("Add-In handoff enrollment file is exact", AddInEnrollmentTests.FileIsExact),
            ("Add-In handoff enrollment reads the actual Windows parent process", AddInEnrollmentTests.ActualParentLookup),
            ("Add-In owner retains exact OS identity and disposal never kills", AddInOwnerTests.ProcessIdentity),
            ("Add-In owner refuses exited changed and unavailable identities", AddInOwnerTests.Refusals),
            ("Add-In owner context cannot accept caller identity or imply producer authentication", AddInOwnerTests.Context),
            ("Manifest publication creates and replaces exact saved bytes", ManifestPublicationTests.SuccessfulPublication),
            ("Manifest publication retries actual Windows source and destination locks", ManifestPublicationTests.ActualWindowsLocks),
            ("Manifest publication bounds permanent denial and preserves originals", ManifestPublicationTests.BoundedErrors),
            ("Manifest publication refuses state drift unknown outcomes and interruption", ManifestPublicationTests.DriftAndUnknownOutcomes),
            ("Manifest publication rejects unsafe paths and new junctions", ManifestPublicationTests.UnsafePathsAndLinks),
            ("Failed upload commit preserves receiving state and exact recovery bytes", ManifestPublicationTests.UploadFailureAndRecovery),
            ("Manifest serialization failure is never replayed", ManifestPublicationTests.SerializationIsNotRetried),
            ("PLC compile-only admission refuses mutation payload and ambiguous target", PlcCompilationTests.Admission),
            ("PLC compiler preserves exact target and original error diagnostics", PlcCompilationTests.Diagnostics),
            ("PLC compiler journal precedes single dispatch and lost outcomes never replay", PlcCompilationTests.DispatchFailure),
            ("Shared compiler scope matches independently authorized devices before dispatch", DeviceCompilationTests.AuthorizedScope),
            ("Shared device hardware identities cover remote groups and refuse ambiguity", DeviceCompilationTests.HardwarePaths),
            ("Shared device hardware bounds stop before any compiler dispatch", DeviceCompilationTests.HardwareBounds),
            ("Shared compiler diagnostics retain errors and explicit count depth bounds", DeviceCompilationTests.DiagnosticBounds),
            ("Device compilers retain exact paths across duplicate display names", DeviceCompilationTests.ExactPaths),
            ("Device compiler services preflight before any dispatch", DeviceCompilationTests.ServicesBeforeDispatch),
            ("Device compiler ambiguity and enumeration fail before dispatch", DeviceCompilationTests.AmbiguityAndEnumeration),
            ("Device compiler inventory bounds fail before dispatch", DeviceCompilationTests.Bounds),
            ("Device compiler scope drift cannot establish coverage", DeviceCompilationTests.ScopeDrift),
            ("Device compiler failures retain single dispatch without replay", DeviceCompilationTests.CompilerFailureNeverReplays),
            ("Hardware references bind bounded canonical project membership", TechnologyReferenceTests.HardwareMembership),
            ("Technology/block correspondence uses native equality rather than names", TechnologyBlockMembershipTests.ExactNativeIdentity),
            ("Technology/block correspondence rejects aliases and bounds", TechnologyBlockMembershipTests.AmbiguityAndBounds),
            ("Ambiguous hardware membership cannot produce a resolved reference", TechnologyReferenceTests.AmbiguousHardwareMembership),
            ("Reference IDs require exact provider roundtrips", TechnologyReferenceTests.ExactIdentifiers),
            ("Reference provider failures and invalid IDs remain unresolved", TechnologyReferenceTests.FailedProvidersAndIdentityText),
            ("Reference channels require unique native owner compositions", TechnologyReferenceTests.ChannelOwnership),
            ("Reference native read bounds dispose incomplete scans", TechnologyReferenceTests.BoundsAndDisposal),
            ("Reference identifier collisions cannot qualify", TechnologyReferenceTests.IdentifierCollisions),
            ("Reference resolution reaches actual connection attributes", TechnologyReferenceTests.ConnectionIntegration),
            ("Connection services distinguish observed absence failure and unvisited states", TechnologyConnectionTests.ServiceStates),
            ("Connection field errors and projections remain incomplete", TechnologyConnectionTests.FieldsAndProjections),
            ("Connection sensor failures preserve torque observations", TechnologyConnectionTests.SensorFailuresKeepPeers),
            ("Connection budgets precede service and field getters", TechnologyConnectionTests.BudgetBeforeGetters),
            ("Connection complete observations retain distinct slots and paths", TechnologyConnectionTests.CompleteAndAbsentSlots),
            ("Connection attribute failures retain independent field evidence", TechnologyConnectionTests.AttributeFailuresKeepFieldEvidence),
            ("Connection null sensors consume the shared budget", TechnologyConnectionTests.NullSensorsConsumeBudget),
            ("Connection service failures retain independent peers", TechnologyConnectionTests.IndependentFailures),
            ("Inspection preserves finite scalar and version values", InspectionValueTests.Scalars),
            ("Inspection bounds and validates metadata before getters", InspectionValueTests.MetadataFailures),
            ("Inspection marks incomplete attributes explicitly", InspectionValueTests.IncompleteAttributes),
            ("Inspection bounds cyclic and broad collections", InspectionValueTests.CollectionBounds),
            ("Inspection bounds text without splitting surrogate pairs", InspectionValueTests.TextBounds),
            ("Inspection labels bounded native projections as incomplete identities", InspectionValueTests.NativeProjections),
            ("Inspection excludes sensitive getters", InspectionValueTests.SensitiveGetters),
            ("Inspection does not invoke opaque display methods", InspectionValueTests.OpaqueValues),
            ("Inspection rejects duplicate attribute identities before paging", InspectionValueTests.DuplicateMetadata),
            ("Explorer retains distinct technology child selections and owner ancestry", ProjectModelTests.TechnologyChildSelections),
            ("Technology child inventory preserves exact owner and composition paths", TechnologyChildrenTests.OwnershipAndPaths),
            ("Technology child mutations cannot retarget a same-named peer", TechnologyChildrenTests.OwnerBoundMutation),
            ("Technology child lookup failures cannot certify selection or absence", TechnologyChildrenTests.IncompleteDiscovery),
            ("Technology child traversal shares item and depth bounds", TechnologyChildrenTests.BoundsAndCycles),
            ("Technology child moves change owner-aware fingerprints", TechnologyChildrenTests.FingerprintIncludesChildren),
            ("Technology selection uses exact escaped paths or unique legacy names", TechnologySelectionTests.ExactSelection),
            ("Technology selectors reject malformed and foreign targets before scanning", TechnologySelectionTests.InvalidSelectors),
            ("Technology previews bind legacy names to fixed paths", TechnologySelectionTests.BoundIntent),
            ("Technology selection and fingerprints refuse incomplete scans", TechnologySelectionTests.TraversalBounds),
            ("Technology deletion verifies the selected path without replay", TechnologySelectionTests.DeleteVerification),
            ("Technology fingerprints retain group paths and type identity", TechnologySelectionTests.FingerprintIdentity),
            ("Alarm workbook rows keep exported cell text within row and cell bounds", AlarmWorkbookTests.RowsAndBounds),
            ("Alarm workbook parts reject DTDs", AlarmWorkbookTests.RejectsDtd),
            ("Technology reads exclude sensitive and unknown-access values before getters", TechnologyReadTests.AccessAndExclusions),
            ("Technology reads preserve scalar types and explicit capture errors", TechnologyReadTests.ValuesAndErrors),
            ("Technology parameter capture bounds filtered traversal", TechnologyReadTests.BoundedCapture),
            ("Technology parameter completeness cannot hide exclusions or errors", TechnologyReadTests.Completeness),
            ("Attribute writes require exact access and known types", AttributeWriteTests.AccessAndTypes),
            ("Attribute batches preflight before setters", AttributeWriteTests.BatchPreflight),
            ("Attribute dispatch and typed readback reject drift", AttributeWriteTests.DispatchAndReadback),
            ("Attribute scalars and explicit null declarations roundtrip", AttributeWriteTests.ScalarValues),
            ("Technology writes require exact known read/write access and a current type", TechnologyParameterTests.AccessAndType),
            ("Technology batches preflight every scalar before the first setter", TechnologyParameterTests.BatchPreflight),
            ("Technology dispatch and readback recheck access, identity and type", TechnologyParameterTests.DispatchAndReadback),
            ("Technology supported scalar values round-trip and invalid inputs refuse", TechnologyParameterTests.ScalarRoundtrip),
            ("Null technology values require a unique explicit supported type", TechnologyParameterTests.ExplicitNullType),
            ("Journal directory publication tolerates an actual Windows handle", JournalDirectoryPublicationTests.ActualWindowsHandle),
            ("Journal publication bounds retries and never overwrites or replays", JournalDirectoryPublicationTests.BoundsAndNoOverwrite),
            ("Card moves tolerate transient locks and reconcile without replay", SimulationCardMoveRetryTests.TransientLockAndReconciliation),
            ("Owned PLCSIM routes answer only the offered project-user login, once", SimulationOnlineLoginTests.OnlyTheOfferedProjectUserLoginOnce),
            ("Owned PLCSIM first-use TLS trust pins one authorized certificate per attempt", SimulationTlsPinTests.FirstUsePinsOnlyTheAuthorizedOwnedCertificate),
            ("Provisioning checkpoint refuses unsaved state and existing destinations", SimulationCheckpointTests.SavedStateAndCollisions),
            ("Provisioning checkpoint and durable receipt precede runtime continuation", SimulationCheckpointTests.ArchiveAndDurableReceiptPrecedeContinuation),
            ("Card move retries retain deadlines and never replay other operations", SimulationCardMoveRetryTests.BoundsAndNoNativeReplay),
            ("Discovery retains nonempty snapshots and requires repeated absence", SimulationDiscoveryWaitTests.TransientEntriesAndStableAbsence),
            ("Discovery never accepts persistent devices or late empty snapshots", SimulationDiscoveryWaitTests.PersistentDevicesAndLateSnapshots),
            ("Discovery guard, read and evidence errors are not retried", SimulationDiscoveryWaitTests.GuardAndReadFailuresNeverRetry),
            ("Runtime Manager placeholder exclusion is exact and narrow", SimulationDiscoveryWaitTests.RuntimePlaceholderIsExactAndNarrow),
            ("Runtime Manager placeholder baseline is short-lived and session-bound", SimulationDiscoveryWaitTests.RuntimePlaceholderBaselineLifetime),
            ("Simulator admits Softbus and existing TCP/IP single-adapter instances only", SimulationNetworkAdmissionTests.SoftbusAndSingleAdapterAdmission),
            ("Simulator refuses mode switches and unknown network values", SimulationNetworkAdmissionTests.ModeSwitchesAndUnknownValuesRefuse),
            ("Simulator worker never assigns network mode, adapters or IPs", SimulationNetworkAdmissionTests.WorkerNeverAssignsNetworkMode),
            ("OPC UA server settings map pinned native attributes and payload forms", OpcUaServerSettingsTests.PinnedFieldsAndPayloadForms),
            ("OPC UA server settings refuse bad values and stale expectations", OpcUaServerSettingsTests.RefusesBadValuesAndStaleExpectations),
            ("Project reviews survive restart and reject tampering", ProjectReviewStoreTests.PersistenceAndTamper),
            ("Project reviews preserve native content and direct impacts", ProjectReviewStoreTests.ContentAndImpact),
            ("Project reviews retain incomplete coverage and exact copy identity", ProjectReviewStoreTests.CoverageAndCopyIdentity),
            ("Project reviews search native tokens and report bounded health findings", ProjectReviewStoreTests.SearchHealthAndBounds),
            ("Imported reviews retain exact bytes and survive upload release", ProjectReviewStoreTests.ImportedReviewPersistence),
            ("Imported reviews reject forged signatures and malformed identity", ProjectReviewStoreTests.ImportedReviewValidation),
            ("Imported reviews serialize publication and preserve virtual endpoints", ProjectReviewStoreTests.ImportedReviewPublication),
            ("Supplied type reviews compare XML and retain exact alarm dependencies", ProjectReviewTypeTests.ContentAndImpacts),
            ("Supplied type reviews retain unknown content and scope boundaries", ProjectReviewTypeTests.CoverageAndScope),
            ("Supplied type reviews reject ambiguous selectors and forged content", ProjectReviewTypeTests.IntakeValidation),
            ("Project reviews reject unsafe XML and mismatched identities", ProjectReviewStoreTests.UntrustedInputs),
            ("Library journals atomically preserve records and reject tampering", LibraryJournalStoreTests.AtomicRecordsAndTamper),
            ("Library type creation takes one or 1..100 distinct document names", LibraryJournalStoreTests.CreateTypeDocumentNamesAreBounded),
            ("Library durable claims forbid mutation replay", LibraryJournalStoreTests.DurableClaimNoReplay),
            ("Library recovery intent and outcome survive restart", LibraryJournalStoreTests.RecoveryOutcomeSurvivesRestart),
            ("Library journals keep native timestamps and their preview hash", LibraryJournalStoreTests.NativeTimestampKeepsPreviewHash),
            ("Project model preserves independent native/virtual hierarchy and exact selection", ProjectModelTests.HierarchyAndExactSelection),
            ("Project model cursors bind filters, lifetime and session", ProjectModelTests.CursorLifetimeAndSession),
            ("Project model rejects malformed ancestry and budgets", ProjectModelTests.InvalidAncestryAndBudgets),
            ("Project model bounds cyclic graphs and separates metadata/content", ProjectModelTests.GraphsAndSeparateFreshness),
            ("Project model bounds complete serialized graphs and unresolved evidence", ProjectModelTests.GraphSerializedByteLimit),
            ("Project model provider integration preserves exact evidence", ProjectModelTests.ProviderContractIntegration),
            ("Project model typed preview preserves exact native source and partial bounds", ProjectModelTypedPreviewTests.AdaptationAndBounds),
            ("Project model measures 1k/10k/50k cached performance", ProjectModelTests.CachedPerformance),
            ("Feature catalog preserves target/evidence separation and bounded pagination", FeatureCatalogPagination),
            ("Feature catalog rejects invented evidence and unimplemented support claims", FeatureCatalogValidation),
            ("Target capabilities separate API, implementation and qualification", CapabilityRulesTests.AvailabilitySeparatesEvidence),
            ("Target capabilities preserve native blockers and unavailable services", CapabilityRulesTests.AvailabilityPreservesTargetBlockers),
            ("Target capabilities reject unknown status values", CapabilityRulesTests.AvailabilityRejectsUnknownStates),
            ("Safety capability blockers preserve native import eligibility", CapabilityRulesTests.SafetyImportEligibilityPreservesNativeBoundary),
            ("Object identities distinguish exact paths and scoped names", ObjectIdentityTests.ExactPathsAndScopedNames),
            ("Object identities resolve canonical scope boundaries", ObjectIdentityTests.CanonicalPathScopes),
            ("Duplicate native names require exact scope or path", ObjectIdentityTests.DuplicateNamesRequireScope),
            ("Mutations reject mixed or contradictory software scopes", ObjectIdentityTests.MutationScopeSelection),
            ("Generated native groups cannot be confused with user folder names", ObjectIdentityTests.GeneratedGroupBoundary),
            ("Case-only namespace aliases cannot create unpreviewed targets", ObjectIdentityTests.NamespaceCaseAliasesCannotCreateTargets),
            ("Namespaced sources preserve exact declaration scope", NamespaceSourceTests.NamespaceSourcesPreserveExactScope),
            ("Namespace envelopes cannot hide imported object scope", NamespaceSourceTests.NamespaceEnvelopeCannotHideObjectScope),
            ("Malformed namespace and qualified declarations are rejected", NamespaceSourceTests.NamespaceMalformedAndQualifiedDeclarationsRejected),
            ("Namespace words in source comments and literals remain content", NamespaceSourceTests.NamespaceKeywordsInCommentsAndLiteralsRemainContent),
            ("Artifact transfer is bounded and hashes the complete immutable read", ArtifactTransfer),
            ("Artifact transfer rejects traversal, siblings and invalid ranges", ArtifactBoundaries),
            ("Artifact journal replacement preserves complete reads and truthful missing-path errors", ArtifactJournalReplacement),
            ("Journal replacement retries unchanged-name, sharing and lock failures within a finite bound", JournalReplacementRetry),
            ("Framing preserves UTF-8 across individual-byte reads", FramingUnicode),
            ("Framing preserves unread LF and CRLF frames", FramingMultiple),
            ("Framing spans the internal 8 KiB buffer", FramingLarge),
            ("Framing rejects EOF with an incomplete frame", FramingTruncated),
            ("Framing rejects EOF between frames", FramingEnd),
            ("Framing enforces the exact byte limit", FramingLimit),
            ("Framing rejects invalid UTF-8", FramingInvalidUnicode),
            ("Framing observes cancellation", FramingCancellation),
            ("Request envelope round-trips arguments and defaults", RequestEnvelope),
            ("Request size is capped before transport", RequestLimit),
            ("Response result survives JSON document disposal", ResponseResult),
            ("Response rejects mismatched IDs and versions", ResponseMismatch),
            ("Response rejects malformed envelopes", ResponseMalformed),
            ("Response preserves worker error code and details", ResponseError),
            ("Bridge serializes overlapping callers and pairs responses", BridgeConcurrentCalls),
            ("Bridge remains usable after an operation error", BridgeOperationError),
            ("Bridge rejects invalid requests without faulting", BridgeInvalidRequest),
            ("Bridge timeout is terminal and never replays", BridgeTimeout),
            ("Bridge active cancellation is terminal and never replays", BridgeActiveCancellation),
            ("Bridge cancellation while queued leaves session usable", BridgeQueuedCancellation),
            ("Bridge pre-cancelled call never starts a worker", BridgePreCancelled),
            ("Bridge mismatched response ID is terminal", () => BridgeTerminalFault("wrongId", "ProtocolMismatch")),
            ("Bridge mismatched protocol version is terminal", () => BridgeTerminalFault("wrongVersion", "ProtocolMismatch")),
            ("Bridge malformed response is terminal", () => BridgeTerminalFault("malformed", "InvalidResponse")),
            ("Bridge disconnect is terminal", () => BridgeTerminalFault("disconnect", "WorkerDisconnected")),
            ("Bridge truncated response is terminal", () => BridgeTerminalFault("truncated", "WorkerDisconnected")),
            ("Bridge reports worker exit before connection", BridgeEarlyExit),
            ("Bridge rejects a same-user impostor before sending any request", BridgeImpostor),
            ("Bridge rechecks the launched worker before another request", BridgePeerExit),
            ("Bridge owner observations never launch a worker and preserve explicit startup", BridgeOwnerObservation),
            ("Bridge owner observations preserve disabled cancelled disposed and faulted states", BridgeOwnerRefusals),
            ("Bridge reports missing executable", BridgeMissingWorker),
            ("Disabled native bridge refuses existing executables without becoming faulted", BridgeDisabled),
            ("Bridge disposal rejects further calls", BridgeDisposed),
            ("Bridge shutdown observes clean worker exit and unused workers", BridgeShutdownSuccess),
            ("Bridge shutdown retains a nonzero exit after a successful response", BridgeShutdownFailure),
            ("Native API lifetime spans operations and shuts down once at worker disposal", NativeRuntimeLifetimeTests.DefersShutdownUntilWorkerDisposal),
            ("Native API shutdown failures propagate and are never retried", NativeRuntimeLifetimeTests.DoesNotRetryShutdownFailures),
            ("Unused workers do not initialize or shut down optional native runtimes", NativeRuntimeLifetimeTests.UnusedWorkerHasNoNativeShutdown),
            ("Clean worker EOF ignores only redundant closed-pipe flush", WorkerPipeWriterTests.ClosedPeer),
            ("Worker write and unrelated disposal failures remain visible", WorkerPipeWriterTests.FailuresRemainVisible),
            ("IL safety guard accepts descriptor-only inspection", WorkerSafetyGuard.TestsAcceptSafe),
            ("IL safety guard rejects explicit, using, nested disposal and descriptor escape", WorkerSafetyGuard.TestsRejectUnsafe),
            ("Offline analysis respects native call access and reference direction", OfflineEvidenceTests.NativeCallsAndDirection),
            ("Offline analysis separates reads, writes and unknown access", OfflineEvidenceTests.NativeReadWriteClassification),
            ("I/O address analysis preserves bit/byte widths and rejects unsupported areas", OfflineEvidenceTests.NativeIoAddressWidths),
            ("I/O conflicts preserve range boundaries and unknown ownership", OfflineEvidenceTests.IoConflictsRespectBoundsAndUnknownOwners),
            ("Instance targets respect canonical software scope boundaries", OfflineEvidenceTests.InstanceTargetsRespectCanonicalScopes),
            ("Incomplete indexes cannot prove unique instance targets", OfflineEvidenceTests.InstanceTargetsRejectIncompleteNameUniqueness),
            ("Instance resolution never guesses a namespace", OfflineEvidenceTests.InstanceTargetsNeverGuessNamespaces),
            ("Instance targets use exact native type relations", OfflineEvidenceTests.InstanceTargetsUseExactNativeTypeRelations),
            ("Native member identities preserve owner and component boundaries", OfflineEvidenceTests.NativeMemberKeysPreserveOwnerAndComponentBoundaries),
            ("Native interface normalization preserves sparse array values and nested UDT members", OfflineEvidenceTests.InterfaceSparseArraysAndNestedTypes),
            ("Native instance declarations preserve member ownership and sections", OfflineEvidenceTests.InterfaceInstanceDeclarationsAndSectionOwnership),
            ("Unsupported array paths and bounds remain explicit", OfflineEvidenceTests.InterfaceArrayPathsStayExplicitWhenUnresolved),
            ("Interface normalization obeys traversal bounds without expanding arrays", OfflineEvidenceTests.InterfaceNormalizationIsBounded),
            ("Native symbolic matching preserves nested and instance boundaries", OfflineEvidenceTests.NativeSymbolsKeepNestedAndInstanceBoundaries),
            ("Deletion policies and empty group checks fail closed", DependencyRulesTests.DeletionPolicyAndEmptyGroupsFailClosed),
            ("Dependency token scans preserve unresolved evidence", DependencyRulesTests.NativeDependencyTokensAreConservative),
            ("Dependency exclusion preserves same-name declarations in other owners", DependencyRulesTests.SelectedDeclarationExclusionPreservesOtherOwners),
            ("Graphical patches preserve untouched native content", EngineeringSourcesTests.GraphicalPatchesPreserveUntouchedNativeContent),
            ("Typed exact owners never fall back to names", TypedGraphicalResolutionTests.ExactOwnersNeverFallBackToNames),
            ("Typed tag and DB symbol bindings reject new ambiguity", TypedGraphicalResolutionTests.SymbolBindingsRejectNewAmbiguity),
            ("Typed UDT identities require exact namespace and scope", TypedGraphicalResolutionTests.TypesRequireExactNamespaceAndScope),
            ("Typed nested UDT resolution follows the declaring namespace", TypedGraphicalResolutionTests.NestedUdtUsesDeclaringNamespaceEvenWithInlineMembers),
            ("Typed member paths reject ambiguity and unsupported arrays", TypedGraphicalResolutionTests.MemberPathsRejectAmbiguityArraysAndLiteralBoundaryConfusion),
            ("Typed resolution preserves complete retained native declarations", TypedGraphicalResolutionTests.RetainedNativeDeclarationsRemainCompletelyUnchanged),
            ("Typed raw payload rejects unknown fields, nulls and JSON coercions", TypedGraphicalTests.RawPayloadRejectsUnknownNullAndCoercedValues),
            ("Typed payload identities reject line breaks without trimming or rewriting", TypedGraphicalTextBoundaryTests.CompleteIdentityTextIsRequired),
            ("Typed reader retains opaque native content and reports incomplete coverage", TypedGraphicalTests.ReaderRetainsOpaqueContentAndReportsCoverage),
            ("Typed template cultures follow the target project without discarding text", TypedGraphicalTests.TemplateCulturesFollowTheTargetProject),
            ("Typed network insertion preserves prior XML with deterministic collision-free IDs", TypedGraphicalTests.InsertPreservesEntirePriorDocumentAndAllocatesDeterministically),
            ("Typed versions, languages and invalid topology fail closed", TypedGraphicalTests.VersionsLanguagesAndTopologyFailClosed),
            ("Typed operand resolution never guesses types or writable outputs", TypedGraphicalTests.TypesWritesAndUnresolvedOperandsNeverGuess),
            ("Typed replacement refuses opaque network boundaries", TypedGraphicalTests.OpaqueNetworksCannotBeReplacedAsCompleteGraphs),
            ("Typed advanced profiles require exact target and provenance", TypedGraphicalTests.AdvancedProfilesCannotBeEnabledByNameOrWrongTarget),
            ("Typed comparisons retain independently specified pins and overload", TypedGraphicalTests.ComparisonUsesIndependentPinAndOverloadExpectations),
            ("Typed constants enforce numeric and time bounds without implicit conversions", TypedGraphicalTests.ConstantsRespectExactSignedUnsignedAndTimeBounds),
            ("Typed calls bind exact interfaces and reject wrong parameter direction", TypedGraphicalTests.CallsBindExactInterfacesAndRejectInvalidDirections),
            ("Typed cross-unit calls require a relation, publication and their own profile", TypedGraphicalTests.CrossUnitCallsRequireRelationPublicationAndTheirOwnProfile),
            ("Typed FB calls require exact instance ownership and explicit sharing", TypedGraphicalTests.FbInstancesRequireExactOwnershipAndIntentionalSharing),
            ("Typed bounded edits preserve IDs and reject shared opaque access", TypedGraphicalTests.BoundedEditsPreserveIdsAndRejectSharedUnknownAccess),
            ("Typed IDs and separately qualified target routes fail closed", TypedGraphicalTests.OpaqueDuplicateIdsAndUnsupportedRoutesFailClosed),
            ("Typed creation retains complete native defaults and rejects missing declaration prototypes", TypedGraphicalTests.CreationRetainsCompleteNativeDefaultsAndRejectsMissingPrototypes),
            ("Typed branch fanout and stateful profile storage retain explicit semantics", TypedGraphicalTests.BranchFanoutAndStatefulStorageFollowIndependentExpectations),
            ("Typed raw payload accepts only exact MOVE, arithmetic, edge and TONR families", TypedGraphicalTests.RawPayloadAcceptsOnlyExactNewFamilies),
            ("Typed DInt and Real comparisons use exact overloads without conversion", TypedGraphicalTests.NumericComparisonsUseExactDIntAndRealOverloads),
            ("Typed MOVE and arithmetic keep one exact operand type", TypedGraphicalTests.MoveAndArithmeticKeepOneExactType),
            ("Typed TONR and edge detectors require exact IEC storage", TypedGraphicalTests.TonrAndEdgeDetectorsRequireExactIecStorage),
            ("Typed reader covers the independently captured native instruction corpus", TypedGraphicalCorpusTests.ReadCapturedNetworks),
            ("Typed emitter preserves every independently captured native instruction profile", TypedGraphicalCorpusTests.EmitCapturedInstructionProfiles),
            ("Typed replacement retains known native instruction and wire IDs", TypedGraphicalCorpusTests.ReplacementRetainsKnownNativeIds),
            ("Typed SUB/DIV read and replace TIA's Card-less import form", TypedGraphicalCorpusTests.NonExpandableMathReadsWithoutExportedCard),
            ("Typed global FB instances retain native storage defaults without guessing layouts", TypedGraphicalTests.GlobalInstanceTemplatesRetainObservedDefaults),
            ("Typed factories preserve every captured scalar creation and instance default", TypedGraphicalTests.CapturedCreationAndInstanceDefaultsRemainComplete),
            ("Typed captured branches preserve the complete native XML after replacement", TypedGraphicalCorpusTests.CapturedBranchRoundtripPreservesCompleteNativeXml),
            ("Typed inserted networks allocate native UIds within their own network", TypedGraphicalCorpusTests.NewNetworkIdsAreNativeScoped),
            ("Typed FBD structural replacement matches independently observed native ordering", TypedGraphicalCorpusTests.StructuralFbdReplacementMatchesObservedNativeOrder),
            ("Typed multi-instance mapping preserves observed embedded defaults and direct DB starts", TypedGraphicalTests.MultiInstanceTemplatesPreserveObservedShapes),
            ("Typed dependent-instance effect manifest is selected by route", TypedGraphicalTests.DependentEffectManifestFollowsRoute),
            ("Typed IEC dependent effect appends only the captured member", TypedGraphicalTests.IecDependentEffectAppendsOnlyTheCapturedMember),
            ("Typed transitive effect reproduces the captured parents", TypedGraphicalTests.TransitiveEffectReproducesTheCapturedParents),
            ("Typed first network goes before the block title of a network-free caller", TypedGraphicalTests.FirstNetworkGoesBeforeTheBlockTitle),
            ("Graphical patch failures are atomic", EngineeringSourcesTests.GraphicalPatchFailuresAreAtomic),
            ("Native readback detects collateral XML loss", EngineeringSourcesTests.NativeReadbackDetectsCollateralLoss),
            ("Protected native generated blocks do not request unavailable checksums", EngineeringFingerprintTests.ProtectedNativeGeneratedBlocksDoNotRequestChecksums),
            ("User checksum failures cannot become generated exclusions", EngineeringFingerprintTests.UserFingerprintFailuresCannotBecomeGeneratedExclusions),
            ("Native fingerprint values and missing providers remain distinct", EngineeringFingerprintTests.NativeFingerprintValuesAndMissingProvidersStayDistinct),
            ("Protected in-work test instances use native fingerprints only", EngineeringFingerprintTests.ProtectedInWorkTestInstancesUseNativeFingerprintsOnly),
            ("Library changes refuse inherited inconsistent PLC data types", EngineeringFingerprintTests.LibraryChangesRefuseInheritedInconsistentTypes),
            ("UMAC role changes plan exact effects and preserve administrators", UmacRoleRulesTests.PlansExactEffectsAndPreservesAdministrators),
            ("Graphical fragments use the native default namespace", EngineeringSourcesTests.GraphicalFragmentsUseNativeDefaultNamespace),
            ("Native network insertion preserves existing units and checks IDs", EngineeringSourcesTests.NativeNetworkInsertionPreservesExistingUnitsAndChecksIds),
            ("Native calls preserve instances and expose unresolved arguments", EngineeringSourcesTests.NativeCallsPreserveInstancesAndExposeUnresolvedArguments),
            ("Graphical call rewiring does not mutate shared accesses", EngineeringSourcesTests.GraphicalCallRewiringDoesNotMutateSharedAccesses),
            ("Source preview includes every same-line declaration", EngineeringSourcesTests.AllDeclarationsArePreviewed),
            ("Source lexer ignores declarations inside comments and literals", EngineeringSourcesTests.CommentsAndLiteralsDoNotCreateDeclarations),
            ("Source preview rejects unbalanced and out-of-scope declarations", EngineeringSourcesTests.ScopeAndBalanceAreEnforced),
            ("Native XML import envelope rejects hidden objects, duplicate IDs and DTDs", EngineeringSourcesTests.XmlEnvelopeRejectsHiddenObjectsAndDuplicateIds),
            ("Recovery preflight enforces TIA project-directory boundary without creating files", EngineeringPathsTests.LengthBoundaryIsPure),
            ("Recovery preflight rejects invalid project components and relative roots", EngineeringPathsTests.InvalidPathsRejected),
            ("Archive staging preflight enforces the native 143-character boundary", EngineeringPathsTests.ArchiveStagingBoundaryIsPure),
            ("Archive staging preflight rejects invalid paths", EngineeringPathsTests.ArchivePathsRejectInvalidComponents),
            ("Recovery archives retain unique short names within the native path limit", EngineeringPathsTests.RecoveryArchiveUsesShortName)
        ];
        if (typedGraphicalOnly) tests = tests.Where(test => test.Name.StartsWith("Typed ", StringComparison.Ordinal)).ToArray();
        if (bridgeOnly) tests = tests.Where(test => test.Name.StartsWith("Bridge ", StringComparison.Ordinal)).ToArray();
        if (ownerOnly) tests = tests.Where(test => test.Name.StartsWith("Add-In owner ", StringComparison.Ordinal)).ToArray();
        if (handoffOnly) tests = tests.Where(test => test.Name.StartsWith("Add-In handoff ", StringComparison.Ordinal)).ToArray();
        if (receiverOnly) tests = tests.Where(test => test.Name.StartsWith("Add-In receiver ", StringComparison.Ordinal)).ToArray();
        var failures = 0;
        var timer = Stopwatch.StartNew();
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + Environment.NewLine + exception);
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed in {timer.Elapsed.TotalSeconds:F1}s. No TIA API calls were made.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task FramingUnicode()
    {
        const string source = "Grüße → 温度 🙂";
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(source + "\r\n"), 1);
        var reader = new BoundedLineReader(stream, 100);
        Check.Equal(source, await reader.ReadAsync(default));
    }

    private static async Task FramingMultiple()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\r\n\nthird\nlast\r\n"));
        var reader = new BoundedLineReader(stream, 20);
        foreach (var expected in new[] { "first", "", "third", "last" })
            Check.Equal(expected, await reader.ReadAsync(default));
    }

    private static async Task FramingLarge()
    {
        var source = new string('x', 8191) + "🙂" + new string('y', 8192);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source + "\nnext\n"));
        var reader = new BoundedLineReader(stream, 20000);
        Check.Equal(source, await reader.ReadAsync(default));
        Check.Equal("next", await reader.ReadAsync(default));
    }

    private static async Task FramingTruncated()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"incomplete\":true}"));
        await Check.ThrowsAsync<EndOfStreamException>(() => new BoundedLineReader(stream, 100).ReadAsync(default));
    }

    private static async Task FramingEnd()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("done\n"));
        var reader = new BoundedLineReader(stream, 100);
        Check.Equal("done", await reader.ReadAsync(default));
        await Check.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(default));
    }

    private static async Task FramingLimit()
    {
        using (var exact = new MemoryStream(Encoding.UTF8.GetBytes("ää\n")))
            Check.Equal("ää", await new BoundedLineReader(exact, 4).ReadAsync(default));
        using (var excessive = new FragmentedStream(Encoding.UTF8.GetBytes("ääa\n"), 1))
            await Check.BridgeError("ResponseTooLarge", () => new BoundedLineReader(excessive, 4).ReadAsync(default));
        using (var noNewline = new MemoryStream(new byte[20000]))
            await Check.BridgeError("ResponseTooLarge", () => new BoundedLineReader(noNewline, 100).ReadAsync(default));
    }

    private static async Task FramingInvalidUnicode()
    {
        using var stream = new MemoryStream([0xc3, 0x28, 0x0a]);
        await Check.ThrowsAsync<DecoderFallbackException>(() => new BoundedLineReader(stream, 100).ReadAsync(default));
    }

    private static async Task FramingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("line\n"));
        await Check.ThrowsAsync<OperationCanceledException>(() => new BoundedLineReader(stream, 100).ReadAsync(cancellation.Token));
    }

    private static Task RequestEnvelope()
    {
        using var document = JsonDocument.Parse(BridgeProtocol.EncodeRequest("request-1", "inspect", new { name = "Überlauf", nested = new { count = 3 } }));
        var root = document.RootElement;
        Check.Equal(BridgeProtocol.Version, root.GetProperty("protocolVersion").GetInt32());
        Check.Equal("request-1", root.GetProperty("id").GetString());
        Check.Equal("inspect", root.GetProperty("operation").GetString());
        Check.Equal("Überlauf", root.GetProperty("arguments").GetProperty("name").GetString());
        Check.Equal(3, root.GetProperty("arguments").GetProperty("nested").GetProperty("count").GetInt32());
        using var defaultArgs = JsonDocument.Parse(BridgeProtocol.EncodeRequest("2", "list", null));
        Check.Equal(JsonValueKind.Object, defaultArgs.RootElement.GetProperty("arguments").ValueKind);
        Check.Equal(0, defaultArgs.RootElement.GetProperty("arguments").EnumerateObject().Count());
        return Task.CompletedTask;
    }

    private static Task RequestLimit() => Check.BridgeError("RequestTooLarge", () =>
    {
        BridgeProtocol.EncodeRequest("large", "write", new { text = new string('x', BridgeProtocol.MaxRequestBytes) });
        return Task.CompletedTask;
    });

    private static Task ResponseResult()
    {
        var result = BridgeProtocol.DecodeResponse("{\"protocolVersion\":1,\"id\":\"a\",\"success\":true,\"result\":{\"name\":\"轴\",\"values\":[1,2]}}", "a");
        Check.Equal("轴", result.GetProperty("name").GetString());
        Check.Equal(2, result.GetProperty("values")[1].GetInt32());
        Check.Equal(JsonValueKind.Null, BridgeProtocol.DecodeResponse("{\"protocolVersion\":1,\"id\":\"a\",\"success\":true,\"result\":null}", "a").ValueKind);
        return Task.CompletedTask;
    }

    private static async Task ResponseMismatch()
    {
        foreach (var json in new[]
        {
            "{\"protocolVersion\":2,\"id\":\"a\",\"success\":true,\"result\":{}}",
            "{\"protocolVersion\":1,\"id\":\"other\",\"success\":true,\"result\":{}}"
        })
            await Check.BridgeError("ProtocolMismatch", () => Task.FromResult(BridgeProtocol.DecodeResponse(json, "a")));
    }

    private static async Task ResponseMalformed()
    {
        foreach (var json in new[]
        {
            "not json", "null", "[]", "{}", "{\"protocolVersion\":1}",
            "{\"protocolVersion\":\"1\",\"id\":\"a\",\"success\":true,\"result\":{}}",
            "{\"protocolVersion\":2147483648,\"id\":\"a\",\"success\":true,\"result\":{}}",
            "{\"protocolVersion\":1,\"id\":5,\"success\":true,\"result\":{}}",
            "{\"protocolVersion\":1,\"id\":\"a\",\"success\":\"true\",\"result\":{}}",
            "{\"protocolVersion\":1,\"id\":\"a\",\"success\":true}",
            "{\"protocolVersion\":1,\"id\":\"a\",\"success\":false,\"error\":null}",
            "{\"protocolVersion\":1,\"id\":\"a\",\"success\":false,\"error\":{\"code\":4,\"message\":\"bad\"}}"
        })
            await Check.BridgeError("InvalidResponse", () => Task.FromResult(BridgeProtocol.DecodeResponse(json, "a")));
    }

    private static async Task ResponseError()
    {
        var error = await Check.ThrowsAsync<BridgeOperationException>(() => Task.FromResult(BridgeProtocol.DecodeResponse(
            "{\"protocolVersion\":1,\"id\":\"a\",\"success\":false,\"error\":{\"code\":\"ProtectedBlock\",\"message\":\"Cannot access block.\",\"details\":{\"block\":\"FB10\"}}}", "a")));
        Check.Equal("ProtectedBlock", error.Code);
        Check.Equal("Cannot access block.", error.Message);
        Check.Equal("FB10", error.Details!.Value.GetProperty("block").GetString());
    }

    private static async Task BridgeConcurrentCalls()
    {
        await using var fixture = new BridgeFixture();
        var calls = Enumerable.Range(0, 12).Select(index => fixture.Client.CallAsync("echo", new { index, delayMs = 25, name = "Temperatur_ü", values = new[] { index, index + 1 } })).ToArray();
        var results = await Task.WhenAll(calls);
        for (var i = 0; i < results.Length; i++)
        {
            Check.Equal(i, results[i].GetProperty("arguments").GetProperty("index").GetInt32());
            Check.Equal("Temperatur_ü", results[i].GetProperty("arguments").GetProperty("name").GetString());
            Check.Equal(i + 1, results[i].GetProperty("arguments").GetProperty("values")[1].GetInt32());
            Check.Equal(1, results[i].GetProperty("maximumInFlight").GetInt32());
        }
        var events = fixture.Events();
        Check.Equal(1, events.Count(e => e.Kind == "started"));
        var requests = events.Where(e => e.Kind == "request").ToArray();
        Check.Equal(12, requests.Length);
        Check.Equal(12, requests.Select(e => e.Id).Distinct().Count());
    }

    private static async Task BridgeOperationError()
    {
        await using var fixture = new BridgeFixture();
        var error = await Check.ThrowsAsync<BridgeOperationException>(() => fixture.Client.CallAsync("operationError"));
        Check.Equal("ProtectedBlock", error.Code);
        Check.Equal("FB10", error.Details!.Value.GetProperty("block").GetString());
        Check.Equal("still usable", (await fixture.Client.CallAsync("echo", new { value = "still usable" })).GetProperty("arguments").GetProperty("value").GetString());
        Check.Equal(1, fixture.Events().Count(e => e.Kind == "started"));
    }

    private static async Task BridgeInvalidRequest()
    {
        await using var fixture = new BridgeFixture();
        await Check.ThrowsAsync<ArgumentException>(() => fixture.Client.CallAsync(" "));
        await Check.BridgeError("RequestTooLarge", () => fixture.Client.CallAsync("echo", new { text = new string('x', BridgeProtocol.MaxRequestBytes) }));
        Check.Equal(0, fixture.Events().Count);
        await fixture.Client.CallAsync("echo");
    }

    private static async Task BridgeTimeout()
    {
        // Startup shares this deadline. Allow normal Windows process startup
        // under load; the deliberately stalled request still must time out.
        await using var fixture = new BridgeFixture(timeoutSeconds: 5);
        await fixture.Client.CallAsync("echo");
        await Check.BridgeError("TimeoutOutcomeUnknown", () => fixture.Client.CallAsync("stall"));
        await fixture.AssertTerminalAsync("stall");
    }

    private static async Task BridgeActiveCancellation()
    {
        await using var fixture = new BridgeFixture();
        await fixture.Client.CallAsync("echo");
        using var cancellation = new CancellationTokenSource();
        var stalled = fixture.Client.CallAsync("stall", cancellationToken: cancellation.Token);
        await fixture.WaitForRequestAsync("stall");
        cancellation.Cancel();
        await Check.BridgeError("CancelledOutcomeUnknown", () => stalled);
        await fixture.AssertTerminalAsync("stall");
    }

    private static async Task BridgeQueuedCancellation()
    {
        await using var fixture = new BridgeFixture();
        var first = fixture.Client.CallAsync("echo", new { delayMs = 300 });
        await fixture.WaitForRequestAsync("echo");
        using var cancellation = new CancellationTokenSource();
        var queued = fixture.Client.CallAsync("mustNotRun", cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => queued);
        await first;
        await fixture.Client.CallAsync("echo");
        Check.Equal(0, fixture.Events().Count(e => e.Operation == "mustNotRun"));
        Check.Equal(1, fixture.Events().Count(e => e.Kind == "started"));
    }

    private static async Task BridgePreCancelled()
    {
        await using var fixture = new BridgeFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => fixture.Client.CallAsync("echo", cancellationToken: cancellation.Token));
        Check.Equal(0, fixture.Events().Count);
        await fixture.Client.CallAsync("echo");
    }

    private static async Task BridgeTerminalFault(string operation, string code)
    {
        await using var fixture = new BridgeFixture();
        await Check.BridgeError(code, () => fixture.Client.CallAsync(operation));
        await fixture.AssertTerminalAsync(operation);
    }

    private static async Task BridgeImpostor()
    {
        await using var fixture = new BridgeFixture(delegateConnection: true);
        await Check.BridgeError("WorkerPeerMismatch", () => fixture.Client.CallAsync("mustNotBeSent", new { secret = "synthetic-private-payload" }));
        await Check.BridgeError("BridgeFaulted", () => fixture.Client.CallAsync("mustNotBeReplayed"));
        await fixture.Client.DisposeAsync();
        Check.Equal<bool?>(true, fixture.Client.WorkerShutdownSucceeded);
        var events = fixture.Events();
        Check.Equal(1, events.Count(e => e.Kind == "launcherStarted"));
        Check.Equal(1, events.Count(e => e.Kind == "launcherStopped"));
        Check.Equal(1, events.Count(e => e.Kind == "started"));
        Check.Equal(1, events.Count(e => e.Kind == "stopped"));
        Check.Equal(false, events.Single(e => e.Kind == "launcherStarted").Id == events.Single(e => e.Kind == "started").Id);
        Check.Equal(0, events.Count(e => e.Kind == "request"));
    }

    private static async Task BridgePeerExit()
    {
        await using var fixture = new BridgeFixture();
        await fixture.Client.CallAsync("echo");
        using var worker = Process.GetProcessById(int.Parse(fixture.Events().Single(e => e.Kind == "started").Id!));
        _ = worker.SafeHandle;
        await fixture.Client.CallAsync("exitAfterReply");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.WaitForExitAsync(deadline.Token);
        await Check.BridgeError("WorkerExited", () => fixture.Client.CallAsync("mustNotBeSent"));
        await Check.BridgeError("BridgeFaulted", () => fixture.Client.CallAsync("mustNotBeReplayed"));
        Check.Equal(2, fixture.Events().Count(e => e.Kind == "request"));
    }

    private static async Task BridgeOwnerObservation()
    {
        await using var fixture = new BridgeFixture();
        string artifacts = Path.Combine(Path.GetTempPath(), "TiaMcpOwnerAbsent-" + Guid.NewGuid().ToString("N"));
        try
        {
            IBridgeClient dispatcher = new TiaMcp.Core.WatchTables.WatchTableDispatcher(fixture.Client, artifacts);
            await Check.BridgeError("AddInOwnerUnavailable", () => fixture.Client.CallAsync("addin_owner_context"));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Check.BridgeError("AddInOwnerUnavailable", () => dispatcher.CallAsync("addin_owner_context"))));
            Check.Equal(0, fixture.Events().Count);
            Check.Equal(0, Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Length);
            // Explicit work may establish the owner while an observation waits behind it.
            var explicitWork = dispatcher.CallAsync("echo", new { delayMs = 200 });
            await fixture.WaitForRequestAsync("echo");
            var observed = dispatcher.CallAsync("addin_owner_context");
            await Task.WhenAll(explicitWork, observed);
            await dispatcher.CallAsync("addin_owner_context");
            Check.Equal(1, fixture.Events().Count(e => e.Kind == "started"));
            Check.Equal(2, fixture.Events().Count(e => e.Operation == "addin_owner_context"));
            Check.Equal(0, Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            foreach (string name in new[] { "engineering-admissions", "engineering-operations" })
                if (Directory.Exists(Path.Combine(artifacts, name))) Directory.Delete(Path.Combine(artifacts, name));
            if (Directory.Exists(artifacts)) Directory.Delete(artifacts);
        }
    }

    private static async Task BridgeOwnerRefusals()
    {
        await using var disabled = new BridgeClient(new BridgeOptions("absent.exe", Disabled: true));
        await Check.BridgeError("NativeWorkersDisabled", () => disabled.CallAsync("addin_owner_context"));
        await using var fixture = new BridgeFixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => fixture.Client.CallAsync("addin_owner_context", cancellationToken: cancelled.Token));
        Check.Equal(0, fixture.Events().Count);
        await fixture.Client.CallAsync("echo");
        using var worker = Process.GetProcessById(int.Parse(fixture.Events().Single(e => e.Kind == "started").Id!));
        _ = worker.SafeHandle;
        await fixture.Client.CallAsync("exitAfterReply");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.WaitForExitAsync(deadline.Token);
        await Check.BridgeError("WorkerExited", () => fixture.Client.CallAsync("addin_owner_context"));
        await Check.BridgeError("BridgeFaulted", () => fixture.Client.CallAsync("addin_owner_context"));
        Check.Equal(0, fixture.Events().Count(e => e.Operation == "addin_owner_context"));
        Check.Equal(1, fixture.Events().Count(e => e.Kind == "started"));
        await fixture.Client.DisposeAsync();
        await Check.ThrowsAsync<ObjectDisposedException>(() => fixture.Client.CallAsync("addin_owner_context"));
    }

    private static async Task BridgeEarlyExit()
    {
        await using var client = new BridgeClient(new BridgeOptions(BridgeFixture.DotnetPath(), TimeoutSeconds: 5,
            WorkerPrefixArguments: [Assembly.GetExecutingAssembly().Location, "--fake-worker", "--exit-before-connect"]));
        await Check.BridgeError("WorkerExited", () => client.CallAsync("echo"));
        await Check.BridgeError("BridgeFaulted", () => client.CallAsync("echo"));
    }

    private static async Task BridgeMissingWorker()
    {
        await using var client = new BridgeClient(new BridgeOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe")));
        await Check.BridgeError("WorkerNotFound", () => client.CallAsync("echo"));
        await Check.BridgeError("BridgeFaulted", () => client.CallAsync("echo"));
    }

    private static async Task BridgeDisabled()
    {
        await using var client = new BridgeClient(new BridgeOptions(BridgeFixture.DotnetPath(), Disabled: true));
        await Check.BridgeError("NativeWorkersDisabled", () => client.CallAsync("discover"));
        await Check.BridgeError("NativeWorkersDisabled", () => client.CallAsync("sim_status"));
        await client.DisposeAsync();
        Check.Equal<bool?>(true, client.WorkerShutdownSucceeded);
        await Check.ThrowsAsync<ObjectDisposedException>(() => client.CallAsync("echo"));
    }

    private static async Task BridgeDisposed()
    {
        await using var fixture = new BridgeFixture();
        await fixture.Client.CallAsync("echo");
        await fixture.Client.DisposeAsync();
        await fixture.Client.DisposeAsync();
        await Check.ThrowsAsync<ObjectDisposedException>(() => fixture.Client.CallAsync("echo"));
    }

    private static async Task BridgeShutdownSuccess()
    {
        await using var unused = new BridgeClient(new BridgeOptions("nonexistent-unused-worker.exe"));
        Check.Equal<bool?>(null, unused.WorkerShutdownSucceeded);
        await unused.DisposeAsync();
        Check.Equal<bool?>(true, unused.WorkerShutdownSucceeded);
        await using var fixture = new BridgeFixture();
        await fixture.Client.CallAsync("echo");
        await fixture.Client.DisposeAsync();
        Check.Equal<bool?>(true, fixture.Client.WorkerShutdownSucceeded);
    }

    private static async Task BridgeShutdownFailure()
    {
        await using var fixture = new BridgeFixture();
        await fixture.Client.CallAsync("exitOnShutdown");
        await fixture.Client.DisposeAsync();
        Check.Equal<bool?>(false, fixture.Client.WorkerShutdownSucceeded);
        await fixture.Client.DisposeAsync();
        Check.Equal<bool?>(false, fixture.Client.WorkerShutdownSucceeded);
    }
}

internal sealed class FragmentedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken);
}

internal static class Check
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}>, actual <{actual}>.");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("Expected exception " + typeof(T).Name + ".");
    }

    public static async Task BridgeError(string code, Func<Task> action)
    {
        var exception = await ThrowsAsync<BridgeException>(action);
        Equal(code, exception.Code);
    }
}
