# Library operation observation contracts — 23 October 2026

The external `library-operation.v1.schema.json` covers payloads and optional
returned results for all 18 native library change kinds. Explicit
`operation-plan` and `operation-change` modes layer this projection over existing
preview/change envelope validation; existing modes retain their compatibility.
PLC integration uses its existing recipe schema and cannot claim a combined
result. Classic placement and Unified tag/faceplate routes have separate shapes.

Cross-field checks bind source revisions, selected pins and destination resources,
reject duplicate selections/dependency pins/mappings, enforce applicable UTF-16
text bounds and Windows path rules, and compare result identity, release numbers,
policy/default flags and HMI link/binding observations. Applied journals require
a result, but a result in a failed/uncertain journal never changes its state.
Native release can rotate a version GUID; the original type and requested release
number remain mandatory. Exact JSON decimals and original byte/hash checking are
preserved. Paths embedded in observations are never opened.

## Verification

- `artifacts/todo-operations-20261023/library-tests-final-02.log`: 86 library tests
  pass, including 70 observation tests, all 18 kind branches, Classic/Unified
  routes, malformed flags, altered pins, path/text bounds, unknown recovery state,
  old-mode compatibility and a CLI consumer copied outside the repository.
- `artifacts/todo-operations-20261023/plc-library-tests.log`: eight existing PLC
  integration/dependency tests pass. Total focused scope: 94 tests.
- `artifacts/todo-operations-20261023/replay-03.json`: all 32 original change
  responses and 32 original previews from nine operation kinds pass. The retained
  `artifacts/l09/nt_69d941cf/qualification.json` bytes remain unchanged, SHA-256
  `2b57f4e7be57321d0439068559b8dd53b47aa989c455d278a0aaaf5eb49b4880`.
  Extracted structures are derived validation inputs, not original-file hashes.
- The API inventory adds the new schema to `sourceInputs` only, preserving every
  feature/evidence entry and all qualification states. Refresh and validation
  logs are retained in the same evidence directory.
- `verification.json` in that directory pins implementation inputs and evidence.
  Earlier failing tests and replay remain retained, including the incorrect
  initial release-GUID assumption and single-slash Windows path discrepancy.

This increment changes only offline Python/schema/inventory/documentation files.
The earlier [combined aggregate/package](todo-integration-20261023.md) validated
its own frozen snapshot and does **not** include this increment. The relocated
consumer check is narrower than a fresh full portable-package run.

## Limits and remaining work

Synthetic tests cover all 18 kinds; retained native compatibility covers nine.
Neither is new native execution. The contract preserves unused supplied fields
and opaque object selectors. Detailed preview analyses, full compiler evidence,
HMI library-query responses, complete recipe semantic/native resolution,
preservation, reopen and runtime acceptance remain separate work. Result metadata
does not establish artifact contents or a successful overall native transaction.
Every receipt retains `nativeExecuted: false`, `referencedFilesRead: false`,
`previewHashRecomputed: false` and `qualificationGranted: false`.
