# Saved-review intake and offline diagnostics — 16 October 2026

Scope: the L04/X11/D17/C04 offline entry path from a supplied saved review into
normal Host review/diagnostic tools. No Siemens product is installed or started.
This increment does not complete all C04 watch/Test Suite/alarm integration or
native signal acquisition. The full 44-workstream queue remains open.

The previous publication `95052ac6cbc37fc515f64fbf07c99578df4cf797` passes hosted
Python/managed/isolated OPC UA (`38908492475`) and Explorer (`38908492690`).

## Implementation and limitations

`project_snapshot_import` uses existing committed uploads and immutable artifact
storage. Exact raw bytes and IDs are preserved; relevant model identities and XML
hashes/signatures are checked. Embedded artifact paths are never resolved. Native
origin labels stay in the source, with explicit unauthenticated-source warnings
in Host responses and diagnostic plans/reports. This does not authenticate a
capture or validate the full native schema. Shared publication locks prevent
capture/import quota races; incomplete temporary files remain counted.

`trace_read` accepts exactly one existing artifact or committed upload reference.
Upload input bytes are retained independently in bounded diagnostic storage before
parsing; existing JSON/CSV and lineage/quality validation remains required.
The existing diagnostic MCP harness now uses actual review/measurement intake
rather than directly seeding the review store, and is part of Test-Offline.

## Development verification

- `artifacts/review-import-dev/core02.log`: 148 core checks pass, including exact
  review bytes/ID, upload release, duplicate import, corrupted destination refusal,
  forged signatures, duplicate JSON/identity/document entries, invalid metadata,
  unsafe/deep XML, node/document count bounds, competing publication and virtual endpoints.
- `artifacts/review-import-dev/diagnostics02.log`: 19 diagnostic groups pass,
  including exact retained measurement bytes, upload release, tamper and size
  refusal, existing signal mapping, measurement quality and comparison checks.
- `artifacts/review-import-dev/mcp01/result.json`: actual Host workflow passes
  across three Host lifetimes with absent native worker paths. It covers uploaded
  review search/compare/health after release/restart, exact download, manual trace
  planning, measurement upload/release/restart, independent event expectations,
  CSV and nanosecond comparisons, readback mismatch and explicit instance replan.
  Intake/signature/duplicate/incomplete-upload/tampered-source refusals pass.
  This development run precedes the final metadata/XML-depth guard additions.
- Two independent Python intake schema checks pass. Catalog/registry/source
  bindings now cover 174 declared tools, 55 mutation kinds and 247 features;
  missing historical evidence hashes are retained, not replaced with new proof.

A subsequent diagnostic audit reproduced a real failure with two virtual Explorer
groups: the source index keyed both by an empty object path. The failing regression
is retained in `artifacts/review-import-dev/virtual-groups-before.log` (19/20 pass).
The shared index now excludes nodes without native object paths; `diagnostics03.log`
passes all 20 groups, and exported MCP fixtures include multiple virtual groups.
`artifacts/review-import-dev/mcp02/result.json` also passes with the virtual groups and final application guards.
A harness cleanup regression verifies that failed shutdown cannot suppress
an earlier failure receipt; all three Python intake/harness checks pass.

The first frozen-source aggregate (`review-import-01`) was launched before these
last fixes and passed all 29 managed suites, 1,260 Python tests (one native-evidence
skip), Explorer unit checks and all MCP workflows. It remains distinct evidence.

## Aggregate failure and journal publication repair

The second snapshot (`review-import-02`, tree
`39d18b1630add704937edbf3317a217ac1656bb3`) passes 1,261 Python tests (one skip),
contracts/bindings and Explorer checks, then fails in Test Suite setup:
`EngineeringOperationStore.Prepare` receives an access-denied IOException while
renaming its completed `.preparing-*` journal directory. The failed result/log and
staging directory remain retained. There was ample disk space. The original
blocking process is unknown; no antivirus or permission cause is asserted.

All three engineering journal stores now use the same parent-directory publication
helper. It rechecks links and retries only Windows access/sharing/lock IO errors,
while the source directory still exists and no destination entry exists. It allows
at most 10 rename attempts with 25 ms waits (225 ms scheduled delay), under the
caller's existing store lock. Other failures, changed publication state and existing
destinations fail without overwrite or replay. Preparation and native commands
are never retried by this helper.

`artifacts/review-import-dev/publication02.log` passes 150 core checks. One test
holds an actual Windows read handle without delete sharing, observes one failed
rename, closes the handle, and verifies the second attempt preserves the complete
journal. Other tests cover persistent denial bounds, unrelated errors, competing
destination preservation, cross-parent refusal and an injected error after a
completed rename. The first handle experiment used metadata-only access, which
did not block rename; that test-only failure remains in `publication01.log`.

Documentation was checked using Context7's .NET library and the exact primary
[Directory.Move reference](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.move?view=net-10.0)
and [CreateFile sharing rules](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).
Those document in-use IO failures and rename/delete sharing respectively; they do
not identify the original blocking process or establish native engineering behavior.

The repaired shared stores also pass all 442 independent Test Suite checks in
`artifacts/review-import-dev/testsuite-repair.log`. A third frozen snapshot is
required for the final aggregate because the publication fix changes shared code.
## Final frozen-source validation

`artifacts/review-import-03/source/artifacts/checks/result.json` passes the full
aggregate: 29 managed suites, 1,261 Python tests (one unavailable-native-evidence
skip), 24 Explorer unit checks, contracts/source bindings and all actual MCP
workflows. This includes 150 core checks, 20 diagnostic groups, 442 Test Suite
checks, 72 VCI MCP checks and 43 GRAPH MCP checks. The diagnostic MCP workflow
passes 17 recorded steps across 3 normal Host lifetimes with absent native workers.
No application or test source changed after this tested tree; only documentation
records the completed result. Browser code did not change in this increment.

Tested Git tree: `bb2eb0b07b6fd462011d28354d45635071397c14`.
Source manifest SHA-256: `7f452b30cc87451adb7773a517e166b74569294d82825115f944b0ec4ccea17b`.
Aggregate receipt SHA-256: `4c40877cdf93b3f65d5617405e786d7de2c664deb04109f947bedef797eb54ec`.
Diagnostic MCP receipt SHA-256: `2517e1b45da4307f38f93e391a67b3f5f07d78352c7dc64b870a1e53e48cf541`.

The new publication's hosted CI remains a separate pending check. Historical
missing evidence remains six byte matches and 106 unavailable files; neither this
aggregate nor synthetic fixtures replace the missing native proof.

Native capture/import/compile/runtime, source authenticity, actual device timing,
licensed products, full document compatibility and release acceptance remain open.
