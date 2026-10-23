# Configured Unified fixture reopen and archive recovery — 21 October 2026

The configured Unified Comfort fixture now passes a fresh-process saved-project
reopen, independent archive retrieval, and a second fresh-process reopen of the
recovered copy. Both exact owners, `HMI_RT_1` and `HMI_RT_2`, compile with zero
errors, zero warnings and complete diagnostic trees at every stage. Every
captured inventory/scalar equals the independent configured baseline with its
JSON type preserved. There are no native export files in these Unified captures.
This closes the captured-state fixture persistence/recovery prerequisite; it
does not qualify complete HMI content, a production bridge operation or a write
profile. Runtime, visual behavior and interrupted-write recovery remain open.

The previous [settings attempt](native-isolated-dispatch-20261021.md) and its
original source/binary snapshot remain unchanged. The configured archive is
`artifacts/w6uni01/native/configured.zap21`, SHA-256
`90849fb7a9dadb286564b6e8d48fa42214d01e5ead78f03aeca67f9398117170`.
The wrapper result remains
`92ce7ad1385780d7d4c1f0512e06e7220de19ccfba576721c808287d2e511c14`.
The originally configured project was opened, inspected, compiled and closed
without a save. TIA may update project metadata on opening; unchanged project
file bytes are not claimed. The fresh recovered copy was saved and closed.

## Original native evidence

| Attempt | Exact result SHA-256 | Outcome |
| --- | --- | --- |
| `artifacts/w6univer01/result.json` | `8e4b02cfffb38700005d7dfc0c6b5c3328921fd4a9f3bf420dd7a494fc010d39` | Saved-project reopen and independent archive retrieval, both captured comparisons and strict compiles passed |
| `artifacts/w6univer02/result.json` | `8470abe5cbf389bad4503240adec8f0091cc35929e5c083e6a281c4ad8a58c4f` | Recovered project reopened in a second fresh helper, captured comparison and strict compiles passed |

The first original helper/main Portal were 47176/42808; the second were
47984/32496. Both helpers exited zero. Their original atomic non-breakaway jobs
drained with respectively 31 and 19 total process accounting entries, zero active
processes, zero termination limits and zero limit-terminated processes. Exact
retained handles signaled, and the registry terminals bind their own admission,
intent, job name and original job evidence. No outcome relies on a later PID
lookup. `artifacts/native-isolated-dispatch/configured-native-summary.json`
collects the exact references and a separate file-only validation of both chains.

Cleanup records `explicitNativeTermination=true` for explicit SDK process
disposal and `coordinatorForcedTermination=false`. Normal lifecycle and
independent project-lock release remain false. The job covers ordinary inherited
process creation; no guarantee is inferred for brokered processes or an
individual handle for every ephemeral observation. No input or operation targeted
the pre-existing user's Portal/project, whose bytes were not independently
audited. The old `hmic20c` failure and unresolved registry ticket are unchanged.

Both helpers captured the same 16 loaded `Siemens.Engineering*` assembly
identities, paths and hashes in `native/loaded-helper-sdk.json`, SHA-256
`ad036bc1a7f4422533f8b2653ec7a401b40ad9e0983dbf314e83be46123c2e7b`.
The final file-only audit checked those installed files still match. This binds
loaded helper assemblies; it does not enumerate every private Portal module or
retroactively supply loaded-SDK evidence for the original settings attempt.

## Admission, comparison and independent review

The new `verify-configured` helper entry is qualification-only. The coordinator
requires a completed original fixture, exact raw reports, unchanged archive,
original terminal/job bindings, current exclusive registry/store ownership,
exact owned project/owners and a fresh atomic output directory. Phase two also
requires the complete original phase-one result and recovered project identity.
Sources are revalidated inside the held window before dispatch and at completion.
The same reviewed post-boot isolation route leaves historical cleanup unknown;
no generalized old-ticket exemption or automatic replay is introduced.

The independent Python comparison keeps nested array order, unknown fields,
getter errors and scalar types. Available export hashes and contents are checked;
only the established XML `DocumentInfo/Created` timestamp can be normalized.
Nonfinite values, truncation, missing exports, duplicate owners, warnings,
incomplete diagnostics and error messages hidden by zero summary counts refuse.
Unavailable getters remain unavailable rather than becoming coverage claims.

The helper build passed with zero warnings/errors. Its SHA-256 is
`d56cde9874267eefac9f71e01078fdc6dfcfef8285c59411ad0a53be75b0e6e3`.
`artifacts/native-isolated-dispatch/configured-source-binary-manifest.json`,
SHA-256 `31ef52680da87afb2041620d2b3abdc74d27e76a53f75e5500c9a258318f6dce`,
binds 17 source/test files and two managed binaries to preserved snapshots.
All those hashes remained unchanged after both native attempts.

All 62 linked managed admission checks and 22 Python comparison/compile/lifetime
checks pass. The Explorer reviewer independently ran the managed 62, 21 Python
checks and 18 actual-source/type/lifetime controls, then reviewed the final
additional baseline-drift test. Its final receipt, SHA-256
`30c8a8920643a0fda931e9a79994066c71b1f4a5c24ead7cc17864c9367c0d67`,
binds the final source/binary manifest. Coordinator and Explorer review preceded
native dispatch.

Independent review found Python's ordinary equality accepted a native Boolean
changed to integer `1`; strict JSON comparison repairs that defect, with both
Boolean/integer directions and integer/float controls. Failed test-only attempts
remain retained: one drift fixture overlapped its own project, and a later
completion-drift fixture omitted its mocked window intent. Both were repaired
without native dispatch or weakened production assertions. The reviewer's
intermediate harness signature mismatch is retained separately. Original native
failures in older reports are not rewritten by these new successful attempts.

The next useful acceptance work is an exact current production bridge's
inspection/export/compile against a fresh copy of this configured archive,
with independent comparisons and the same complete ownership/cleanup boundary.
Production HMI mutations remain disabled pending operation-specific preservation,
dependencies, interrupted-write recovery and the remaining native acceptance.

## Coordinator integration

The lane commit `7e6ca4f` is integrated as `adde6ed`; its documentation conflict
was resolved by preserving the previous isolation qualification and linking this
later result. Independent post-run review passes 529 checks across the two native
lifetimes, exact terminal/admission chains, 16 loaded helper assemblies and all
three captured states. The first job accounts for 31 processes, while 29 individual
process entries have retained handles; no inference fills that difference. The
second job accounts for 19 processes with 19 retained individual entries.

The integration checkout passes 22 new comparison/compile/lifetime checks plus
95 existing guard/isolation/coordinator/store checks. The changed native helper
passes the full 62 linked managed admission checks and actual two-lifetime workflow.
Portable production Host, worker and bundle sources are unchanged by this fixture
milestone; their prior qualification remains separate. No broad package rerun is
needed for this fixture-only change. The running combined UA validation uses its
own earlier frozen source and does not claim these new helper checks.

All 136 independent audit/input/log references are hash-verified and retained under
`D:/03_Entwicklung/TIA_MCP/artifacts/goal-wave6-20261021/native-recovery/`.
`integration.json` binds the original reviewer receipt
`70059d26a596f9598d33b83a9dc9022ce6f2f80b53b87c3b6138a453217f5616`,
source snapshots and coordinator checks. Live project directories and protected
credentials are not committed. The exact production bridge is the next milestone.
