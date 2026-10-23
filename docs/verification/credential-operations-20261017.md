# Credential operation ownership and exact native context — 17 October 2026

This offline increment extends the existing operation journal and local protected
credential store. It does not add a native provider, a public credential mutation
MCP tool, or a second mutation-state ledger. WA-09, D12/X03 and the full release
queue remain open.

## Behavior

- Immutable plans optionally bind bounded named dispatch/recovery requirements to
  exact public references and expected external account/resource context. Null
  omission preserves old plan/record hashes. One vault per operation avoids
  multiple-vault lock ordering.
- Host admission verifies readability and pins every current/recovery generation
  before journal acquisition. Required private lifecycle version 2 retains exact
  artifact root, operation ID and plan hash; missing pins refuse.
- Checkpoint/dispatch revalidate references under the vault lock. The existing
  dispatch intent precedes one synchronous trusted consumer. Only dispatch slots
  reach that consumer, and their buffers are cleared on return or failure. Success
  remains dispatch intent until independent required outcome gates pass.
- Pending, unknown, missing and corrupt journal obligations refuse retirement.
  Stopped/succeeded/reconciled journals release their pins only for retirement;
  history and ciphertext are retained. A terminal credential-bearing journal
  cannot reopen using potentially retired references.
- Raw credential use cannot bypass operation admission. Core credential verification
  callbacks are a trusted adapter boundary, like native reacquisition, and must run
  with the vault lock. They are not authorization supplied by an MCP caller.
- The shared native-context guard now reuses exact Unicode validation before JSON
  serialization, closing a reproduced session/owner identity substitution gap.

See the [workflow](../reference/protected-credentials.md#operation-ownership-and-single-dispatch)
for ordering, required retained artifacts and recovery limits.

## Frozen sources and failed attempts

| Snapshot | Tree | Result |
| --- | --- | --- |
| `artifacts/pins-01/source` | `c909a576950b12196aa1c10e40ae41ec3adcecc4` | Aggregate and package failed on a test-only substring assertion; not acceptance. |
| `artifacts/pins-02/source` | `b0dcca244d1e366b6b008bd800865444a5073de9` | Corrected JSON-property assertion and explicit normal Host-exit checks. All 38 managed suites, Python/Explorer/MCP workflows and 20 package stages pass. Does not include the later native-context fix. |
| `artifacts/pins-03/source` | `15e2112aa8a1ec570ec81dc30266ce76b8787823` | Final production source, including the Unicode fix; full aggregate and all 20 package stages pass. |

Each snapshot contains 1,930 files. Final source manifest:
`artifacts/pins-03/source-manifest.json`,
SHA-256 `2e44f75811a44019a088a5b9a66946261d605b974aea502969bab61baf64e2f3`.
Its archive SHA-256 is
`9eaafc8f5061aae72f40a1c5d2c42ac4237d34b1c668434dff4603792e27d5dc`.

The first test searched the complete serialized JSON for `credentials`, which
matched the aggregate/package fixture directory name. Checking the actual property
fixes that false failure. The failed aggregate receipt hash is
`cca0e0247d932511069ad021f2ef1da66dd29cc95c863a6922ec374028dc82f3`;
failed package receipt hash is
`7ccebeb5d65dbf9224a1dc74ef1f50589c1c023f91d6e81394a67fc97bed88b0`.
Both remain in `artifacts/pins-01/source/artifacts/`; the package completed 18 stages
before reaching that assertion. Neither running job was silently replaced.
Earlier development `pins-dev-01` used an incorrect expected refusal code and was
corrected to the existing `RecoveryRequired` contract. An initial standalone Python
invocation lacked `jsonschema` and failed before starting a Host; subsequent checks
use the existing repository test environment.

A separate real defect was reproduced in
`pins-dev-05-unicode-reproduction.json` (`artifacts/pins-dev-05-unicode-reproduction.json`):
raw session UTF-16 units `[98,97,100,55296]` became persisted
`[98,97,100,65533]` in a prepared journal. No native call occurred. The existing
exact-text validator now rejects malformed session, hardware, native identity and
probe names before freezing the plan. Valid Unicode round-trips unchanged.

## Targeted evidence and compatibility

Final targeted source passes 55 storage checks, 81 operation checks and 29 actual
MCP checks across two clean Host lifetimes. These are checks, including retained
file comparisons, rather than 81 distinct native scenarios. Evidence is retained
in `artifacts/pins-dev-06/credentials/` and `artifacts/pins-host-06/`.

The operation suite covers exact requirement validation, unreserved owners, mixed
vaults, interrupted acquisition and idempotent pin publication, stale session
refusal, missing/corrupt journals, raw-use refusal, buffer clearing, sanitized
consumer errors and independent terminal release. An actual managed child exits
17 after persisted dispatch intent and one synthetic provider invocation. A fresh
process observes the pending state, refuses a second invocation and refuses
retirement of both generations until separate reconciliation evidence is retained.
No real credential, PLC, endpoint or provider is used.

The final-source compatibility receipt
`legacy-compatibility.json` (`artifacts/pins-03/legacy-compatibility.json`),
SHA-256 `733bb0cbce35e7ae04a51c479a27d205db51e1bc3bd8587236271baeb7893be6`,
compares old/new Host results for four original journals (prepared, reconciled,
succeeded and unknown) and preserves all 74 evidence files. It also verifies actual
DPAPI readability of an active version-1 credential while preserving all eight
old vault files. Both Hosts exit normally without worker startup.

The separate downgrade check (`artifacts/pins-02/downgrade-refusal.json`)
shows that the previous Host refuses a credential-bearing plan with `JournalInvalid`
and the previous vault rejects pinned lifecycle version 2. All 41 test files stay
unchanged. Unknown new ownership cannot silently become an old unreserved record.

## Final full verification

The baseline `pins-02` aggregate passed at `2026-10-17T01:52:37.4052334Z`:
receipt SHA-256 `9797c775d3de6f0e6379863f5a60cb3113f5cd6a05ae3cf39dfb6e25fc8fea01`.
Its 20-stage package receipt hash is
`8e9237157dcaf270df4c596c84554a5ae57c3dea12ec0d2c753cfa1188f88087`.

Final `pins-03` package and Unicode relocation pass all 20 stages. Receipt:
`result.json` (`artifacts/pins-03/source/artifacts/package-checks/result.json`),
SHA-256 `0806f1d8610c2b9a25bb0af831e931f80c957c69504751b6bb06d3af5c529267`.
Both original and relocated bundles pass strict verification of 1,373 files with
manifest SHA-256 `123c34599f9f3e39e161103bd5be8a8b155e1cf901d5457b6d9036897b780b3b`.
ZIP SHA-256: `32024c939989625e2fd0270f50abb6fe7d216e7facac31396fbc6ca704d659c2`.

| Final package receipt | Checks | SHA-256 |
| --- | --- | --- |
| `credentials/verification.json` | 55 | `e61d0353f0cd081e64d159f636da510f8d83daf330f09da85c648deda7038858` |
| `credentials/operations/verification.json` | 81 | `46c6e0062354e99a56f797572f772b26d72bccd90ae5df16cdaff5585297bd3b` |
| `credential-cli/verification.json` | 82 across 21 processes | `ad680f0973df99c909e4cfb14dd033786283f14d18ef1c2a610b3f9d2d21b310` |
| `credential-operation-mcp/verification.json` | 29 across two clean Host lifetimes | `e0aec01de80ec9f09d442ea9c97e612caf2b167caa00669e788d95393e434d90` |

Final `pins-03` aggregate passes all 38 managed suites, 1,280 Python tests
(one existing unavailable-evidence skip), 24 Explorer model/client checks, schemas,
source bindings and every MCP workflow. It ran from
`2026-10-17T01:48:04.8305401Z` through `2026-10-17T01:58:08.0919262Z` and exited zero.
Receipt: `result.json` (`artifacts/pins-03/source/artifacts/checks/result.json`),
SHA-256 `a8d7e248be5fd0b856cd2a4670999e489a654012bea990ece402a5179ee94c03`.
The aggregate operation receipt contains 81 checks and has SHA-256
`3f8d01664d293008e1e8f0dd997ff6ab50185deb29395e7867bf360dec002b66`.
Its 29-check MCP receipt has the same digest as the package receipt above.

Only completion documentation changes after the final frozen source. The
final source comparison (`artifacts/pins-03/final-source-comparison.json`)
records those exact paths; no executable source, tests, schema, CI command or
binding changes after this passing full run. Frozen archives, failed receipts,
compatibility evidence and exact built packages are retained for review.

## Remaining gates

Recovery slots are pinned and checked for readability, but recovery consumption
needs its own fresh admitted session contract that permits changed post-mutation
content. Independent external recovery, authenticated expected context, native
exclusive access, provider handoff/transport, profile loss/context migration and
hardware/endpoint acceptance remain open. The callback seam and synthetic provider
tests do not establish native authorization or safe recovery of an actual device.

The previous base `9b249f219252c36d1dbc32f91f5ee67b7d3a8106` passed Explorer
`38990648168` and all four Python/.NET workflow jobs `38990648146`; the last managed
job completed at `2026-10-17T01:39:09Z`. New-commit hosted acceptance is separate.
The catalog remains 197 static tools, 55 mutation kinds and 270 features; all 106
unavailable historical evidence bindings retain their prior rows/hashes. Absence
of Siemens software or historical receipts is not a passing native/release gate.

See the [complete 44-workstream queue](../plans/offline-execution-20261016.md).
