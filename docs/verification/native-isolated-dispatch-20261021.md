# Native isolation qualification — 21 October 2026

The installed Portal V21 SDK, WinCC and Unified assemblies are available.
The current HmiSeeder builds against that SDK with zero warnings and errors.
The 20 October `hmic20c` reporter gap remains unknown; the original failure,
configured Classic archive and durable unresolved registry ticket remain intact.

The new [explicit isolation policy](../reference/native-dispatch-isolation.md)
admits a fresh owned local copy only after corroborated full OS boot evidence.
It is not historical cleanup reconciliation. The existing default path still
refuses the old ticket. No production project or physical PLC is included.

Reviewed original fingerprints:

- Legacy intent: `5c18e026d371bc6d3034693abbc345a504ccbee6d6fa2d33adedbe899454b990`.
- Original reconciliation: `1c8c8c7be74e012f622e8b5b0201ae9efb15441fd1cdbd080ee3567f5667c4c0`.

Current local boot observations agree on `2026-10-21T03:15:46.500Z`.
Kernel-General event 12 is record 328302; Kernel-Boot event 27 is record 328310
with `BootType=0`. Current CIM and kernel uptime independently agree. Original
reconciliation creation/write metadata is 20 October at approximately 07:31Z.
These observations separate current local execution; they do not recover the
missing original reporter handle or authenticate the historical host.

Validation in the isolated native worktree:

- 23 new isolation tests, 12 existing registry tests, 33 fixture coordinator tests
  and 27 existing store/recovery tests pass.
- Independent Explorer-lane review reran 68 isolation/registry/fixture tests with
  no failures or skips, reviewed atomic output creation and the no-fallback job
  path, and found no remaining blocking issue within the documented scope.
- Actual file-only admission `artifacts/native-isolated-dispatch/actual-admission-02/result.json`
  passes: default quarantine refusal, matching full boot, current exclusive
  registry/store locks, competing-owner refusal, unchanged original registry and
  absent disposable target. No native process was dispatched by this check.

Negative controls include missing event fields, hybrid/resume boot, mismatched
machines, inconsistent clocks, rehashed original replacement, missing source
files, changed archive, postboot evidence, a different/new unresolved ticket,
reparse ancestry, existing/overlapping output, network volumes and boot changes
inside the held window. The existing actual Windows job qualification covers
live descendants, breakaway refusal, failed atomic launch and no retry.

The first actual file-only admission refused because the reviewed intent digest
was transcribed with two adjacent characters swapped. Its original log and
refusal receipts remain in `actual-admission-01`; the corrected digest is the
unchanged file's SHA-256. An earlier test-only collector-source assertion tried
to inspect its mocked function; it was corrected to inspect the original
function. Neither failed attempt launched TIA or altered historical evidence.

## Actual Unified prerequisite and contained native lifetime

`artifacts/w6uni01/result.json` completes successfully with helper exit zero.
The positive `native-positive-02` Unified Comfort archive was retrieved into a
new owned project. Only each owner's encrypted-transfer password and matching
confirmation were configured in the native editor; encrypted transfer remained
enabled. The exact saved state was captured through the bound helper command.
Both `HMI_RT_1` and `HMI_RT_2` compile successfully with zero errors, zero warnings
and complete diagnostic trees. The configured archive SHA-256 is
`90849fb7a9dadb286564b6e8d48fa42214d01e5ead78f03aeca67f9398117170`.

The original helper and exact main editor handles exited, explicit SDK disposal
was observed, and the original non-breakaway job drained: nineteen total process
accounting entries, zero active and no termination limits. The receipt records
`explicitNativeTermination=true` for SDK disposal and
`coordinatorForcedTermination=false`; it does not qualify normal lifecycle or
independent project-lock release (`normalLifecyclePassed=false` and
`projectLockReleaseVerified=false`).
No individual ephemeral handle or brokered-process guarantee is inferred. The
new attempt has its own exact terminal ticket; the old unresolved ticket and
report remain unchanged. No operation or UI input was directed at the pre-existing
user's Portal/project; its file bytes were not independently audited.
This establishes Siemens compatibility with the contained fixture route for this
attempt, not universal descendant or production-adapter qualification.

The executing HmiSeeder SHA-256 is
`09c90b515006dcbf751d628e7a1a22cc2c24c0380962cda49ead55566044fb05`.
`artifacts/native-isolated-dispatch/source-binary-manifest.json` binds its sources
and copied managed dependency. Loaded Siemens assembly identities were not
captured by this helper, so exact loaded-SDK runtime provenance remains unqualified.
`atomic-output-02/result.json` separately invokes its
exact compiled Unicode `CreateDirectoryW` entry on Framework 4: first creation
succeeds and a collision refuses with the original sentinel bytes unchanged.
The first reflection harness attempt failed on PowerShell argument boxing before
calling the method; a typed delegate repaired only that test invocation.

Fresh-process reopen, independent captured-state preservation and archive
retrieval/recovered reopen subsequently passed in the
[separate configured-fixture verification](hmi-configured-recovery-20261021.md).
Those captures contain no native export files and do not qualify whole-HMI
preservation, production bridge operations, runtime or visual behavior.

## Coordinator integration

The native lane commit `a3c6895` and precision correction `871ea8c` are integrated
as `082a12f` and `fa919e1`. The full Python suite passes 1,614 tests with one
existing unavailable-evidence skip. The retained original wrapper handle records
exit zero; the interrupted console observation did not cause a second test run.
All source changes during that run were documentation-only. The exact native
helper build and focused qualifications above cover the changed SDK fixture path;
portable production Host/bundle code is unchanged by this native milestone.

Independent review passes 382 evidence-ledger checks, including exact source
snapshots, helper/dependency bytes, all nineteen retained process entries, the
lock window, terminal job proof and unchanged historical quarantine. This audit
rechecks recorded evidence; it does not recreate historical process handles.
The before/after captured HMI observations agree within their recorded getter
coverage; this does not establish whole-project preservation.

Original lane artifacts, both failed file-only qualification attempts, the
configured archive, all independent audit references and the full Python log
are hash-verified and retained under
`D:/03_Entwicklung/TIA_MCP/artifacts/goal-wave6-20261021/native-isolation/`.
`integration.json` binds the independent audit and completed regression. No
disposable project or protected credentials are committed. Fresh-process
reopen/retrieval qualification is recorded as the separate milestone linked above.
