# Fresh checkpointed PLC baseline compilation — 17 October 2026

## Gap and implementation

Encoder recovery previously wrote unchanged TO parameters to trigger compilation;
safety recovery reimported unchanged F-block XML. These were real engineering
operations, not independent compile-only evidence. A scratch/seeder compile also
did not establish a fresh baseline immediately before the edited fixture exports.

The existing prepare/apply/status/recovery route now accepts `plc_compile` with
one exact PLC selector and an empty payload. It preflights the compiler before
checkpointing, saves and archives the project, records dispatch before calling
ICompilable once, retains exact diagnostics, and checks the selected native PLC
identity after compilation. It bypasses the mutation dispatcher and records
`compile_only_requested` instead of `mutation_applied`. Native generated state
may still change. Errors and unknown outcomes keep the existing recovery and
ownership rules; no automatic compiler retry is introduced.

The linked `PlcCompilation` helper is shared with the existing software compilation
path. Target capability reporting and the feature/obligation catalog expose the
new operation as partial and unverified. The public tool count stays 201; there
are now 56 prepared operation kinds and 275 feature entries. The static source
binder correctly retains a partial branch binding for compile-only because its
apply path deliberately bypasses `ApplyEngineeringMutation`.

Encoder qualification now compiles after seeding and before baseline exports,
then recompiles recovery without parameter writes. The v2 compiler policy binds
four original applications: hardware, baseline, actual edit and recovery compile.
Independent auditing checks original preparation/outcome pairs, exact selection,
project/process identity, no mutation stage, dispatch order and the baseline
phase. Missing baseline or legacy policies cannot authorize the current companion.

Safety roundtrips likewise compile before baseline signatures/exports. Recovery
and independent archive retrieval use compile-only. New recovery-source admission
requires the original fresh baseline before exports and import preparation, with
exact request flags, owner, summary and no intervening apply or project change.
Current retained auditing checks recovery compile-only calls and reports
`freshBaselineCompilationVerified` separately. Historical diagnostic audits remain
readable with that flag false; historical reports do not acquire new qualification.

## Verification

The linked production C# helper passes three new groups: exact request/service
admission, unmodified error diagnostics, and journal-before-single-dispatch failures.
The general executable passes 222 checks without Siemens API execution. Encoder
orchestration tests cover all compiler phases, same-value substitution, baseline
ordering and lost baseline replies before any edit. Safety tests run the real
Python roundtrip/recovery/reopen orchestration against synthetic clients and audit
the original retained calls. Compiler proof and source-admission tests reject
wrong owners/targets, mutation payloads, missing/late stages and stale exports.

The first new lost-reply assertion expected a raw TimeoutError. The established
controller wraps that outcome as NativeToolError; the assertion was corrected to
check the retained timeout code. The original failed log is retained at
`artifacts/plc-compile-encoder-optimized-02.log`. An earlier safety test used a
numeric call index that shifted after baseline insertion; it now selects the
original safety-inspection call by tool identity. Its failed log is retained at
`artifacts/plc-compile-safety-01.log`.

Final frozen Git tree: `d44bdc53596ba927bde5f9c3ed8e235794b0946e` (2027 files).
Source manifest (`artifacts/plc-baseline-01/source-manifest.json`) SHA-256:
`66a646c9500ca0d3d2b0158c8ba078ea5a8c323b0377b6c9d8de3eb20a79c089`.
Source ZIP SHA-256: `146dc7102905319348ac4270bf54166f50ba5b131f231bb086e8f495dbc76d01`.

The aggregate (`artifacts/plc-baseline-01/source/artifacts/checks/result.json`)
passes 41 managed suites, 1,411 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 222 checks. Targeted optimization runs
pass 29 encoder lifecycle, 14 compiler-proof, 26 safety qualification and 7 safety
source-admission methods under -OO.
Run: 2026-10-17T13:34:21.7041951Z to 2026-10-17T13:45:46.2982170Z.
Aggregate receipt SHA-256: `30287f2f29a659714645e16da7a9d6d64988d1116f4683ba79c4fd620a04fa26`.
Console evidence (`artifacts/plc-baseline-01/aggregate-console.log`).

All 24 package stages (`artifacts/plc-baseline-01/source/artifacts/package-checks/result.json`)
pass. Bundle `tia-mcp-offline_20261017_133444_d2b5a5c8` contains 1431 manifest files.
Package receipt SHA-256: `4860ef874a25fe74fcccc5632da4c6c13a50a71390be9836ddd5468a4601819e`.
Manifest SHA-256: `37872f695c89028cc898150c813d0fbd90ecee8e437841b567d3788e433dd667`.
ZIP SHA-256: `912af4ef20e677b086d80b5ce56ec537c6e6e2121e820a1c95e6c416c4bb287a`.
The portable package disables native workers. Browser tests were not repeated
for this compiler/harness increment; SDK/native compilation remains unexecuted.

The source comparison (`artifacts/plc-baseline-01/final-source-comparison.json`)
verifies unchanged frozen source and permits only six completion documents to
differ in the final commit. All 106 unavailable historical evidence binding rows
remain unchanged. The integration receipt (`artifacts/plc-baseline-01/integration.json`)
records the pushed commit, exact hosted CI outcome and cleanup.

## Next reproduced recovery-copy defect

The retained synthetic reproduction (`artifacts/plc-baseline-01/next-endpoint-reproduction.json`)
contains two individually valid channel-reference observations with the same
module/channel profile and different owning project paths. Their raw attribute
maps differ, so the existing recovery equality rejects a correctly re-owned copy.
The compiler/lifetime orchestration tests isolate connection validation through
a mock; they do not establish endpoint correspondence. The next repair must bind
each endpoint to independent hardware/channel inventory within its own project,
then compare logical identity without discarding owner evidence.

## Remaining limits

No Siemens SDK build, native worker, TIA Portal, PLCSIM, PLC or safety/motion runtime
was executed. Linked synthetic API shapes do not prove V21 ABI compatibility.
The established ICompilable route is documented in Siemens' V20
[compilation reference](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/compiling-a-project);
that reference is not V21 acceptance evidence.

Fresh native baseline generation, compiler side effects, F-signature behavior,
complete unrelated-content preservation, independent recovery-copy endpoint
correspondence and closed-project admission remain unqualified. All 106 missing
historical evidence bindings are preserved. This increment does not complete the
full SA-01–12, TO-03–12 or 44-workstream goal.
