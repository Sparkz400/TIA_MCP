# Fifth parallel increment — 12 October 2026

The user requested parallel completion work on typed LAD/FBD, HMI prerequisites,
remote-I/O engineering, and review/explorer acceptance. Three agents own the
first three areas; the coordinator owns review/explorer and shared integration.
All native mutations use independently owned offline fixtures. At most two
qualifiers run concurrently; simulator qualification requires exclusive native
admission. Existing user projects, historical reports, and the installed MCP
package are outside this increment's mutation scope.

## Evidence and acceptance boundaries

- [Typed engineering](typed-p16-qualification.md) records the preservation
  contract, advanced software-unit cases, namespace/ambiguity diagnostics,
  captured routes, and their individual native outcomes. An authored fixture
  or passing offline oracle is not native or runtime acceptance.
  All twelve advanced-unit cases now pass on the first frozen combined build.
  The [strict independent audit](../../artifacts/p16t/advanced-final-01-audit.json)
  verifies 794 XML exports, 636 complete payload comparisons and 36 exact owned
  TIA exits. Runtime remains unqualified by this engineering run.
  A [supplemental compile audit](../../artifacts/p16t/advanced-final-01-compile-audit.json)
  checks 28 retained compile objects and 226 messages across all twelve cases;
  no contradictory Error diagnostics or truncated evidence were found.
- [HMI qualification](hmi-qualification.md) records real Unified bindings and
  bilingual resource lifecycle checks. A stricter reassessment found nested
  Error diagnostics inside nominally successful derived compile summaries;
  clean Unified prerequisites remain unproved. Original reports are retained. Classic needs
  a configured start screen; the user currently has no existing qualified
  project/archive. The public HmiTarget API has no supported setter. Native
  visual assessment remains at the prior stop, and RT Professional has no
  available qualified target.
- Remote-I/O adds reviewed module movement, channel parameters and controller
  association operations, with bounded configured-metadata preservation.
  Native attempt records remain separate from source implementation and
  catalog qualification. See [configured network scope](../guides/network-inspection.md).
- [Review/explorer](review-explorer-p16-qualification.md) records the successful
  native diamond dependency-impact fixture and its strict retained-export
  audits. The 1,000-function workload and six browser checks on its native data
  passed; complete global relationship coverage remains bounded. Desktop rendering
  remains unqualified because this session exposes no computer-use apps or
  browser surfaces.

## Shared preservation correction

The original shared qualification comparator parsed XML with ElementTree's
default parser, losing comments and processing instructions. It now uses the
existing preserving DOM comparator and labels new snapshots
`native-payload.v2-comments-pi-qnames`. The typed insertion, untouched-caller,
address-only and direct-instance oracles, and the review leaf-edit oracle also
preserve QName-only namespace bindings. Regression tests alter these fields to
prove the equality checks reject collateral changes.

Historical reports keep their original hashes and outcomes. Supplemental audits
recompute comparisons from retained raw exports and bind their source report
hashes explicitly. These corrections do not broaden a product's supported
mutation contract or retrospectively qualify another build.

## Compiler diagnostic evidence correction

The Unified HMI fixture exposed a native contradiction: zero error counts and a
successful summary coexisted with Error-state password diagnostics. The shared
production guard now requires complete nontruncated diagnostic evidence without
Error messages before apply/library success, save, typed engineering receipt,
scratch acceptance, simulated card generation, or component progression.
Raw native summaries/messages and recovery behavior remain intact. The installed
Siemens enum documentation explicitly permits `Information` as a successful
compile result, so zero-error `Success`, `Information` and `Warning` results are
accepted only with complete clean diagnostics.

The [guard evidence manifest](../../artifacts/p16h/compile-guard-final-assessment.json)
records exact sources and isolated builds, ten pure guard checks, 192 coordinator
checks and 35 HMI checks. These are offline regression results, not new native
qualification. Simulator ownership and backend guards were unchanged.

## Integration checkpoint

The first frozen combined Release snapshot is
`artifacts/deployment-build/fa0067bcbd32499485b72d8ea1cef38a`. Build and publication
completed with zero warnings/errors, and the compiled worker guard passed.
Its [full regression](../../artifacts/p16r/full-regression-01.log) ran 518 Python
tests in 37 suites (517 passed, one existing skip), plus all C# suites. The
simulator self-checks did not initialize Siemens APIs. The existing 21 explorer
client/model tests also passed independently.

Later source and harness changes require a separate frozen regression record.
Native reports retain the exact binaries they used; a newer combined build does
not silently replace their build identities. No deployment was performed.

## Legacy offline-report incident

An exploratory invocation of `verify_feature_catalog.py` used its legacy Debug
host and default `artifacts/feature-catalog-live.json` destination. Its 79-feature,
43-tool, 18-backlog and zero-exit results match the documented historical values,
but the previous file hash was not available. The
[incident receipt](../../artifacts/p16r/legacy-catalog-repeat.json) explicitly
excludes this rerun from current-source qualification. No native worker was
started and native reports were unaffected. The helper now rejects an existing
report path before host startup and uses exclusive report creation to prevent
future accidental replacement.
