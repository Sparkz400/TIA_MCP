# Integrated library changes and portable package — 23 October 2026

The fresh portable package captures the accumulated operation, HMI-query,
compiler, update-analysis and synchronization contracts and helpers. Its frozen
source also includes the earlier Unified internal-tag repair; native workers are
excluded from this offline package. Their native acceptance remains separate.

## Completed package checks

`artifacts/todo-integrated-20261023/package-01/result.json` reports all 53 stages
passed. These include strict ZIP/manifest checks, original and relocated Host
startup, scoped Git operations, graph/AutomationML/UA engineering, diagnostics,
Test Suite, HMI/component review, retained-producer compatibility, credentials,
secure loopback runtime, polling/subscriptions and prebound diagnostic capture.
The final relocated integrity check also passes. Transcript:
`artifacts/todo-integrated-20261023/package-run.log`.

Separate packaged-library checks pass 21 cases in each location, covering 17
validator modes. Each invocation runs outside the repository working directory
with its Python import path removed, using the packaged schema/helper files and
hash-pinned inputs. Original fixture/native input bytes remain unchanged. These
42 cases add validator deployment evidence; they do not qualify native behavior.
Receipts: `observations-original/result.json` and
`observations-relocated/result.json` under the same evidence directory.

Source snapshot:
`artifacts/deployment-build/073ce04528ed4fb28fca7551b714dd29` (2,871 input files).
Package: `artifacts/releases/tia-mcp-offline_20261023_073152_7e668775` (2,125
packaged files), plus its adjacent ZIP and checksum. Manifest SHA-256:
`b94641510c52e1b19094c0a8cc405068e1a2c4a3fcaff87315b1b7d05666332a`.

## Completed full aggregate

`artifacts/todo-integrated-20261023/offline-01/result.json` reports the full
`All` aggregate passed: 1,813 Python tests, 45 managed suites, 25 Explorer
model/client checks and every configured Host workflow. The original transcript
is `offline-run.log`; the final diagnostic UA binding run passes 924 checks.
The runner reports no native qualification.

`snapshot-comparison.json` verifies all 2,871 captured source hashes and compares
the complete source-file set. Implementation, schemas, tests and fixtures match
the frozen package; subsequent documentation corrections are listed separately.
`verification.json` pins both complete runs, strict package integrity reports,
packaged observation checks, source comparison, catalog counts and prior batch
receipts. The earlier pending checkpoint and draft evidence remain historical.

## Scope and draft work

The current catalog has 335 features: 132 implemented, 193 partial and ten not
implemented, recorded in `catalog-counts.json`. These labels are not a release
completion percentage. The TODO acceptance text now restates the existing release
ledger: every feasible capability remains a 1.0 obligation; a partial package
does not reduce that scope. The frozen package retains its captured documentation
bytes; these subsequent status and scope-text corrections remain in the workspace.

While verification runs, `hmi-preview-draft/` contains an unintegrated schema,
helper and harness for four HMI preview routes. Fifteen authored cases pass. A
scan of ten original native probe reports found zero library target previews,
so this is not native preview compatibility evidence. The draft records omitted
interface-type, connection, collision and source-descriptor evidence explicitly.
It is outside the source snapshot, aggregate and package; it requires review,
integration, expanded tests and its own verification before the TODO is closed.

No installed MCP replacement, native engineering dispatch, PLC connection or
release publication was performed by this batch. Hosted CI, native preservation,
runtime/visual acceptance and the remaining release obligations stay open.
