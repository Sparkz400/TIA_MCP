# Replan template retention and manifest-test diagnostics — 17 October 2026

## Replan defect and repair

`diagnostic_replan` retained hypotheses but silently discarded the selected role
template when creating a case for another machine instance. The new regression
reproduced this loss before the repair ([before log](../../artifacts/replan-template-before.log)).
The shared replanner now passes the prior template ID back through the existing
planner. The current catalog supplies the role question, signal roles and trigger
guidance; an original case without a template remains without one. This is role
context, not reused target evidence or a new template override API.

All signal mappings and instance/declaration/type checks still run. Old equipment
observations and timing do not carry over. Alarm and Test Suite selectors still
need fresh explicit selection. Prior case lineage retains the old evidence.
Host help, the feature catalog and the guide describe this behavior.

Eighteen focused assertions cover two selected templates and a template-free
case, the separately resolved Conv1 instance, original hypotheses/lineage,
excluded old observations and no native execution claim. The public MCP workflow
covers the same three cases, request/report schemas and identical replanned IDs
across actual Host restart. All 35 diagnostic groups and 66 MCP steps pass in
targeted development checks.

## Manifest fixture investigation

The preceding increment's [initial failure](../../artifacts/diagnostic-export-01/source/artifacts/checks/result.json)
observed a retry delay where a manifest drift fixture expected none. The unchanged
general executable and 100 focused repetitions passed afterward; its original
cause remains unconfirmed.

A [controlled probe](../../artifacts/manifest-lock-probe/result.log) holds the
fixture source open without delete sharing. The intended completed-move mutation
then fails before establishing the test state. Production correctly enters its
bounded retry path; the fixture's interruption callback hides the original I/O
error behind a generic wait-count failure. This reproduces the failure mechanism,
not proof that a file lock caused the original event.

The test now retains an exception raised while arranging the intended state and
reports its defect name, attempt/wait counts and original exception. Assertions,
retry limits and production publication behavior are unchanged. The [same
controlled probe after the diagnostic change](../../artifacts/manifest-lock-probe-after/result.log)
now names the completed-move fixture and preserves the sharing-violation exception.
Its nonzero exit is expected and checked by the [assessment](../../artifacts/manifest-lock-probe-after/assessment.json).
No failure has been relabeled as a pass or production guard weakened.

## Verification scope

Frozen tree: `2843518386c66cb7f051bcc103680e35134afef7` (2049 files).
[Source manifest](../../artifacts/replan-template-01/source-manifest.json) SHA-256:
`2fca0ba821d1a9d566f33b61f0f6fb7b20d7ac621071734cbe9a4998b87c2510`.
Source ZIP SHA-256: `2fc16df36a17d6a86b4881b307c00cb77b4fc6f59ee410bf94254cb88119125b`.

The [targeted run](../../artifacts/replan-template-01/source/artifacts/targeted/result.json)
passes contracts/source bindings, all 35 diagnostic groups, all 226 general
checks, the Host build and all 66 diagnostic MCP steps across five clean Host
lifetimes. The latter includes three exact replan receipts preserved across
restart. Run: 2026-10-17T17:09:04.5751528Z to 2026-10-17T17:10:08.3180326Z.
Receipt SHA-256: `0e3e7857ba9120154bb8823beeb2b6b8093f83751f5d47a09f506acf931c7bb7`.
[Console](../../artifacts/replan-template-01/targeted-console.log).

All 24 [package stages](../../artifacts/replan-template-01/source/artifacts/package-checks/result.json)
pass, including exact ZIP/manifest checks, relocation and the expanded diagnostic
workflow. Bundle `tia-mcp-offline_20261017_170928_982f82d5` contains 1448 manifest files.
Package receipt SHA-256: `50329bf2559b02685115b589db89f65ac01d7aa15ef1a85da85ba5afacdebbc6`.
Manifest SHA-256: `226054c13080d32dac4e2c4fcba42352aa06471fea4a925ecaac1556f5fa62b5`.
ZIP SHA-256: `431672067257b888a5afd0b6616e45752dcc76d08fe83237692fa260ff10c09f`.

The [final source comparison](../../artifacts/replan-template-01/final-source-comparison.json)
permits only five completion-document changes after the tested frozen tree and
preserves all 106 unavailable historical evidence binding rows. The
[integration receipt](../../artifacts/replan-template-01/integration.json) records
exact Git/CI and cleanup state. No native qualification is claimed.

The change reaches one shared replanner field, Host help, catalog bindings,
managed diagnostics, the actual MCP workflow and manifest-test error reporting.
No report-renderer, acquisition, native worker or publication implementation changed.
The preceding commit's 41-suite managed result and 1,455-test Python result remain
historical evidence; this increment does not claim to have rerun them all.

## Remaining work

Native acquisition, target/program/clock identity and the full D17/C04 workflow
remain open. Investigate the original intermittent manifest failure further if
it recurs; its new diagnostic should distinguish fixture setup from state-drift
handling. Continue bounded repeat-capture planning and the full 44-workstream
queue. This increment does not close the overall goal.
