# Exact operation text before serialization - 17 October 2026

The shared operation journal now rejects malformed UTF-16 before JSON can replace
it with U+FFFD. This closes a managed intake defect across operation names,
idempotency keys, resource effects, required probe/gate names, artifact references
and recovery destinations. It adds no public tool, schema or native implementation.

## Reproduced failure

The baseline binary receipt (`artifacts/optext-01/baseline-reproduction.json`)
runs the final test executable with the previous Core binary from the frozen
`5181f8ad2654f2ceca8c4bb8cdac8b1e829ca487` production source. It fails as expected.
Its retained case 31 changes `dispatch.` plus an unpaired surrogate into the distinct
required gate `dispatch.` plus literal U+FFFD and publishes `succeeded`. Case 34
publishes success after an artifact path is replaced with an existing different
file. Cases 39 and 42 publish `reconciled` after artifact or recovery-path replacement.
The exact caller text, not the substituted text, must control these decisions.

The earlier development receipts `artifacts/optext-red-01` and `optext-red-02`
reproduce the defect before the production edit. The first used record equality
for a reopened collection-bearing record, an unsuitable preservation assertion;
the second corrected that to serialized record equality. Final tests additionally
compare all retained JSON file names and bytes across every refusal. The final
baseline run has 95 failure entries and 9 passing assertions; failure entries include
additional unexpected-error diagnostics, so those counts are not distinct scenarios.
No failing run is counted as acceptance.

## Implementation and scope

`EngineeringOperationStore` reuses `EngineeringIdentityGuard.Text`, preserving the
existing `InvalidOperationPlan` refusal code. Artifact reference validation is shared
between plan intake and evidence verification. Incoming checkpoints, gate evidence,
interruption references, destinations and reconciliation are validated before freezing;
the frozen values and durable transitions retain their existing checks. Refusals cannot
publish a revision or release existing reservations. Translation, watch, Test Suite,
Git and protected-credential callers continue to use the same API.

Valid Unicode is not normalized by this text guard. Existing path canonicalization,
NFC package-path restrictions, artifact hashes, external-state observations and owner
checks still apply. This change cannot reconstruct original malformed input from an
old record after its text was already replaced. Literal U+FFFD is valid Unicode and
remains supported; existing records are not rewritten or retrospectively qualified.

## Source and focused verification

Frozen source: `artifacts/optext-01/source`, tree
`e3d77dc13dd500a2ff69bf346fd98f047b65d539`, 1,934 files.
Source manifest (`artifacts/optext-01/source-manifest.json`) SHA-256:
`a178782d3db9cc7fb898767f7ac8e479d18eb368a23e22d6198abfdcfd25f818`.
Source archive SHA-256:
`440b38b6f504f13a1303bfc5f3b0c2e724e7b384d56bc877fe651a4c0d790ca6`.

The focused receipt (`artifacts/optext-green-02/verification.json`) passes 100
assertions: five malformed UTF-16 shapes across six plan/input fields, twelve
checkpoint/evidence/recovery entry cases, unchanged durable records/files, and
exact successful Unicode reopen/idempotency. Literal U+FFFD, a supplementary
character and composed/decomposed text remain distinct valid input. The same checks
run in the normal engineering suite. The initial fixed run passed 50 assertions;
final coverage adds file preservation and separate artifact/path cases.

The actual Host compatibility receipt (`artifacts/optext-01/compatibility.json`),
SHA-256 `579db4bd85bc3ca2d0ad4cf01ead8c83efc3ecc459568912411affc1a121100b`,
compares the previous and current packaged Host on all four valid Unicode records.
Both return the identical retained records, preserve every retained file, and exit
normally across eight Host lifetimes without launching workers.

## Full verification and integration

The frozen aggregate (`artifacts/optext-01/source/artifacts/checks/result.json`)
passes all 38 managed suites, 1,280 Python tests (one existing unavailable-evidence
skip), 24 Explorer checks, contracts/source bindings and all MCP workflows.
It ran from `2026-10-17T02:39:42.3140563Z` to
`2026-10-17T02:49:32.1685212Z`; receipt SHA-256:
`0090fda074463c1231d047564f78afdfd425a679799c46f41c7dc90c23ee8f1e`.
The 100 operation-text assertions also pass in that exact aggregate; their retained
receipt copy (`artifacts/optext-01/aggregate-operation-text.json`) has SHA-256
`4313dd30fde4da9aaa5037c41f14de25ee7a667bf42c54720c071e293611f905`.

All 21 package stages (`artifacts/optext-01/source/artifacts/package-checks/result.json`)
pass, including original/Unicode-relocated verification and all existing packaged
workflows. Receipt SHA-256: `900a1a7ac14854a0bc60be10d4c27e32695f77e783d956f884a25b142e9fa6a8`.
The bundle `tia-mcp-offline_20261017_024003_824f1348` contains 1,375 manifest files;
manifest SHA-256: `32c7734b6ea5147b6ee1597181bfd466d5d5508d990ad4e2defd31dd4f41026b`.
ZIP SHA-256: `b1cb7d4a46b9b2babd00e47e685fcc7830ef921854f4c6835fbeebddaf1fe6b7`.
Hashes establish integrity, not publisher authentication or native qualification.

The final source comparison (`artifacts/optext-01/final-source-comparison.json`)
allows only completion-documentation changes to status, TODO, queue and this record;
production source, tests, scripts and bindings remain identical to the verified tree.
The integration receipt (`artifacts/optext-01/integration.json`) records the
pushed commit, hosted CI state and cleanup. Base `5181f8a` now passes Explorer
`38995419507` and all four Python/.NET jobs `38995419529`; its final managed job
completed at `2026-10-17T02:47:54Z`. New-commit hosted acceptance is separate.

Follow-up: hosted `0110331` passed Explorer, Python, package and OPC UA, but its
managed job failed when the Git timeout fixture published an incomplete PID file.
The [D13 follow-up record](technology-oracle-20261017.md#hosted-ci-regression-and-repair)
retains the failure and documents the fixture repair and its separate verification.
The local passing receipts above remain unchanged and do not imply hosted acceptance.

## Remaining work

The catalog remains 197 static tools, 55 mutation kinds and 270 features; the 106
unavailable historical evidence rows/hashes remain preserved. This is a bounded F02
provenance repair, not closure of the entire provenance audit or 44-workstream queue.
Native SDK compilation, actual TIA/PLCSIM/PLC behavior, authenticated providers,
owner/profile migration and release acceptance remain unverified.
