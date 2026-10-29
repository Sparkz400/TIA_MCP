# Typed supplied drive telegram review — 18 October 2026

This increment adds explicit v2 telegram observations to the existing drive
review and paging tools. It advances D10/C02 without completing either or the
immutable [44-workstream scope](../history/offline-scope-reconciliation-20261017.md).

## Implemented behavior

The [v2 contract](../../contracts/drive-profile-review.v2.schema.json) and
[reference](../reference/drive-profile-review.md) define exact local telegram IDs,
supported type names, nullable numbers and directional byte sizes. Addresses
retain their own IDs, PLC paths, direction, opaque context labels, start/length
values and independent bit/byte/unknown units. Missing information requires an
explicit reason. Unknown units never acquire a byte interpretation.

Explicit quantities normalize with exact Int64 arithmetic. A known address
length can be compared with a declared directional byte size; the result is
only arithmetic consistency. It does not prove native geometry, eligibility or
signal layout. Main telegram 999 is identified as freely configurable without
applying that meaning to other telegram types. Exact IDs correlate supplied
rows; equal numbers/addresses never merge them, and collection order does not
manufacture a change.

Original rows and array indices remain in the retained report. PLC resolution
checks exact saved identities and rejects ambiguity, virtual objects and wrong
kinds as captured PLC context. It does not establish ownership or live wiring.
Native, freshness, authenticity and complete-drive flags remain false.

The store versions its envelope and verifies that it matches the original
request. Every read still rebuilds the result from original retained input and
the saved review. V1 semantics and bytes remain unchanged, and v1 refuses the
new `telegrams`/`telegramChanges` page sections. Oversized rows remain explicitly
omitted from pages and downloadable in full.

Composite v1/v2 callers retain a v2 drive summary and continue resolving exact
parameter selectors. They still refuse unsupported telegram-purpose mappings:
this increment does not invent a composite telegram selector or compatibility
rule merely because typed observations are now present.

## Validation

Local evidence is under `artifacts/drive-telegrams-01/`. Final focused managed
checks pass 75 existing-drive/compatibility and 62 typed-telegram checks. Cases
include unknown fields and reasons, independent units, exact maximum Int32
arithmetic, all 4,096 allowed address rows, duplicate IDs, invalid saved owners,
Unicode ordinal matching, collection reordering, bounded pages, restart,
corrupted inputs and rehashed envelope-version mismatch.

Six Python contract tests pass. The independent verifier reads original rows,
checks schema versions and false authority flags, recomputes unit arithmetic,
PLC resolution and exact-ID differences, and uses ordinal UTF-16 ordering. The
final fixture includes IDs whose UTF-16 order differs from code-point order.

Actual Host tests pass 35 v1 and 44 v2 checks, including upload release, restart,
strict schemas, malformed units, exact original bytes, oversized row omission
and full artifact download. The existing actual prior-producer composite corpus
passes all 22 compatibility checks; reopening it also rebuilds its original v1
drive dependency from the original input rather than regenerating legacy data.
Each harness observes two clean Host lifetimes without native workers.

Focused composite checks pass 47 evaluator, 177 retention/recovery and
49 consumer-context checks. These include both existing composite versions
reading the new drive format without changing their supported selector rules.

Two snapshots contain the same 2,222 files, based on
`ac68879c8673bc3c15acd39fababe1432db26782` plus this increment:

| Source manifest | SHA-256 |
| --- | --- |
| `aggregate-source-manifest.json` | `b5d55a3cc40a1872996f9754db7c6252f84441bcaba91d78cad8fb0a8d84c081` |
| `package-source-manifest.json` | `f0c10d86c4859450e4f6e133053c24ae0a9e61c5baf81bb557f2040dba5fca1a` |

The complete aggregate passes all 44 managed suites (242 general checks),
1,498 Python tests with one skip, Explorer model/client checks and all configured
Host workflows. All 33 package/relocation stages pass. Both runs pass 75 drive,
62 telegram, 35 v1 drive MCP and 44 v2 drive MCP checks with independent source
verification. Composite checks pass 47 evaluator, 177 retention/recovery,
49 consumer, 50 v1 MCP, 59 v2 MCP and 22 actual prior-producer compatibility checks.

The package contains 1,566 files. Its manifest SHA-256 is
`3f69fadbdf9da0fe9a1b403043ace25e6d6430e4c62ece0a40951eb6a28c0ec1`. Runtime checks total
644 in the aggregate and 647
in the package. The evidence verifier checks fixed scenario counts, reconciles
idle status polls with the retained one-second session observations and checks
variable file-count assertions against retained files.

`verification.json` and `record-validation.py` record exact source manifests,
results, preserved historical metadata and post-freeze documentation changes.
Production code, schemas, tests and CI configuration are unchanged after the
freeze. `integration.json` records the pushed commit, exact-head hosted CI
observations and final Git/worktree state. Hosted execution is a separate gate
from these local results.

## Remaining work

Typed encoder observations, composite telegram/encoder selectors and exact
cross-domain constraints/associations remain next. Signal layouts, address
overlap, firmware/version eligibility, rebind/compile/reopen, real source
capture and physical communication/motion require additional implementation
and separate native qualification. The format is this project's supplied
observation format, not an importer for a Siemens export.

No actual browser, Siemens SDK/product, physical target, real disk-full or
power-loss test is claimed. All 112 historical evidence entries retain their
metadata and hashes, including 106 unavailable inputs. The immutable scope
reconciliation files remain unchanged; the full goal stays active.
