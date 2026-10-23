# Library compiler observations — 23 October 2026

The external `library-compilation.v1.schema.json` covers library compiler
aggregates, target receipts and flattened messages. Separate branches retain
current device-path coverage, older device-name scope and single-target HMI
compilation. `compiler` validates the standalone aggregate; `compiler-change`
adds operation/result validation and prepared-scope comparisons;
`compiler-lock` checks the compilation retained in an issued lock.

Standalone failed, unknown-state or truncated diagnostics remain observations.
Aggregate counts must equal target counts, but diagnostic message severities are
not counted to reconstruct native totals. Warning ancestors may repeat the same
warning, and a native zero summary can coexist with Error messages. Successful
diagnostics require zero reported errors, known non-error target/message states
and complete captured messages. Warnings remain permitted.

Messages retain description/path nulls, original states and depth. Traversal is
bounded to 500 messages and depth 64, including depth truncation below 500 rows.
An exact 500-row capture can be complete. Flat depth transitions must match a
preorder traversal. Nested diagnostic formats used by other evidence consumers
remain outside this library producer contract.

## Scope and lifecycle checks

Current prepared path/name pairs must match compiler targets. Duplicate display
names are allowed only with distinct device paths. Older name-only receipts keep
their weaker binding; they cannot impersonate a prepared device-path receipt.
Classic binds the selected HMI software path; Unified binds its owning device.
Lock receipts lack the original prepared device inventory and report only the
scope declared within the lock, not independent target authorization.

Applied project changes and issued project locks require successful complete
compiler evidence. Applied journals retain an explicit Boolean
`engineeringVerified` matching their project/global scope. Global operations may
have no compilation and cannot claim engineering verification. Failed/uncertain
journals can retain successful or unsuccessful compiler observations without
changing their transaction state or allowing automatic retries.

The validator adds `compilerReportsSuccess`, `compilerMessagesComplete` and
identity/scope-binding fields. No compilation is inferred when absent: its
success/completeness fields are null. Every receipt retains false native
execution, referenced-file-read, preview-hash-recomputation and qualification
flags. Existing envelope/operation modes remain compatible and do not silently
acquire the new compiler checks.

## Verification

Evidence is under `artifacts/todo-compiler-20261023/`, pinned in `verification.json`.

- `library-tests-final.log`: all 125 library tests pass, including 109 observation
  tests and 17 new compiler cases. Tests cover failed/unknown states despite zero
  summaries, warning preservation, exact count sums, depth/count boundaries,
  duplicate names versus paths, altered prepared targets, wrong HMI scopes,
  missing compiler results, malformed verification flags, recovery uncertainty,
  standalone/global lock limits and original CLI bytes.
- `admission-tests.log`: all 14 existing compiler-admission tests pass unchanged.
  Total focused Python scope is 139 tests; the new observation validator does
  not replace production compiler admission or its independent evidence gates.
- `native-replay-final.json`: all 32 original library change responses, ten
  compiler structures and 16 original lock receipt byte streams validate. The
  original lifecycle report remains unchanged at SHA-256
  `2b57f4e7be57321d0439068559b8dd53b47aa989c455d278a0aaaf5eb49b4880`.
  Extracted compiler/change structures are derived inputs; the lock receipts
  are validated in their original bytes with their retained hash pins.
- `contracts.log` and `bindings-validation.log`: contract coverage and current
  source bindings pass for 261 tools, 56 mutations and 335 feature entries.
  Only source inventory changes; no capability or qualification is promoted.

Initial test failures are retained: a legacy test inherited modern path metadata,
and an intermediate implementation placed a verification-flag check in the wrong
branch. Final tests and native compatibility runs use the corrected source.

This increment changes offline Python/schema/fixture/documentation files only.
No native engineering or runtime qualification is added. Detailed preview
analysis contracts remain open. Earlier aggregate/package results apply to their
recorded frozen baseline; this increment has focused verification only.
