# Supplied type XML and alarm impacts — 17 October 2026

Saved reviews now accept exact nonvirtual block/type XML documents and compare
UDT interfaces and opaque supervision content through the existing review tools.
Separate type coverage counters retain missing content as unknown. Explicit
HMI→alarm→supervision→member→UDT paths are evaluated independently in before and
after snapshots. No new tool, dependency or native operation was added.

This is supplied structural evidence, not a genuine Siemens format recognizer,
native type capture, compatibility verdict or runtime qualification. A declared
consumer is a review candidate; a Bool-to-Int XML difference does not itself prove
a compiler error or operating fault. Native capture still selects standard blocks.

## Exact source

- Base commit: `c5ddc9a076b3b26da3c854a46309ba23719e2224`.
- Frozen staged Git tree: `2e36d0afbac4cfb1e5436223b44663005d6c15bd`.
- Source: `artifacts/alarm-types-01/source`, 1,878 files; original `source.zip` retained.
- Source manifest: `artifacts/alarm-types-01/source-manifest.json`, SHA-256
  `e8500e1054bbb2f2743229956ce96bbbc884eb73a34cfd6f0ccd8e29d0c64d1b`.
- Final documentation updates are limited to this record, STATUS and the execution
  queue. Source, tests, catalog, bindings and workflow guide remain frozen.

## Executed validation

Targeted Core validation passes 154/154 groups, including three new type-review
groups. They cover type/interface and opaque supervision changes, full retained
four-edge before/after paths, paging independence, default-depth truncation,
missing/unavailable documents, scope removal, block/type kind changes, restart
search and invalid/duplicate/virtual/foreign selectors, forged hashes/signatures,
wrong endpoint identities and unresolved links.

The first targeted run passed 153/154: an older traversal fixture attached XML to
a virtual node. It now changes captured metadata without attaching source to a
virtual identity, retaining both source/target edge-exclusion checks. The final
targeted run passes. No existing valid block-document requirement was relaxed.

Actual MCP intake uses independently authored XML/signatures and graph evidence;
it downloads exact original bytes after upload release and Host restart, compares
the supplied type, searches its Unicode text, checks health, pages changes without
losing dependency paths, and rejects malformed identities/content. The existing
block-only diagnostic fixture still imports and completes its workflow unchanged.
The extended diagnostic harness passes 51 steps across four clean Host lifetimes,
including the same checks in the relocated portable package.

Portable package validation passes all 14 stages; original and Unicode-relocated
bundles have 1,346 manifest files with no unexpected files. The packaged Host
keeps native workers disabled. Full frozen aggregate validation exited zero: all
34 managed suites (including 154 Core groups), 1,280 Python tests with one
unavailable-native-evidence skip, 24 Explorer model/client checks and all MCP
workflows pass. Catalog/source binding checks retain 191 static tools, 55 mutation
kinds and 264 features; normal Host discovery exposes 171 tools.

A separate probe against the same frozen portable package confirms the diagnostic
boundary: type-only evidence creates an unresolved planning case, refuses the UDT
as an executable block, refuses signals without a selected instance, and refuses
watch planning without resolved storage. Its Host exited cleanly with no workers.
Receipt: `artifacts/alarm-types-dev/diagnostic-boundary-probe/result.json`.
This informs the next increment; it does not deliver an alarm-to-diagnostic selector.

## Retained receipts

Paths below are relative to the frozen source directory.

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/checks/result.json` | `b9aaec67296a790b2a670927e60cc91a2907d3cd438b2c7cc10ac2d7e31e1eee` |
| `artifacts/checks/diagnostic-mcp/result.json` | `6a77e37b020ca1f0f5a5a740eb1c8dee12676024047f0b6791a21965e0fc78a2` |
| `artifacts/package-checks/result.json` | `705a6ecd1cdd1ba36b4ee6047179799ae7be0d4be9cde2f087cdd8b44e28d1c0` |
| `artifacts/package-checks/diagnostic-mcp/result.json` | `c8fd3526985ec3f221ca75308495cc874decab43d44a2f1e34c49dd62ab0e3c7` |

Portable ZIP: `artifacts/releases/tia-mcp-offline_20261016_224710_645f30f7.zip`.
SHA-256: `664adcc0fc39a2d79ab1e2ebbe9717f13317dffa7b0f87e4cc269617e2c4561a`.
Bundle manifest: `cb9a01de7829ed660717f53f3083b6ceb34c8e0e1bb6896bcdaddc11a3896888`.

Executed from the frozen source with the local Python test dependencies:

```powershell
./scripts/Test-Offline.ps1 -OutputDirectory artifacts/checks
./scripts/Test-OfflinePackage.ps1 -OutputDirectory artifacts/package-checks
```

Console logs remain beside `source` as `aggregate-console.log` and
`package-console.log`; detailed logs and exact binary hashes are retained below
that source. Both commands exited zero. Expected negative/refusal output is not
native execution or qualification.

## Qualification and continuation

The base commit passes hosted [Explorer](https://github.com/Sparkz400/TIA_MCP/actions/runs/38976973727)
and [Python/managed/package/OPC UA](https://github.com/Sparkz400/TIA_MCP/actions/runs/38976973487).
These base results do not establish a later commit's hosted result. GitHub had no
open issues or PRs at this audit; only the primary main branch/worktree was active.

Continue AL-03/08 with selected alarm evidence in diagnostic/watch planning,
retaining exact snapshot/owner/storage identities, ambiguous or missing links and
replan lineage. `DiagnosticEvidence` recognizes exported DB/FB forms for executable
storage; a UDT definition must not bypass that requirement. The next code audit
is retained at `artifacts/alarm-types-dev/next-alarm-handoff.md`.

Full D11 remains open: native formats and metadata, complete alarm inventory/type
capture, lifecycle/recovery, generated dependencies, genuine language/placeholder
rules, HMI integration and runtime behavior. No Siemens installation, license,
device, native compile/import, browser/visual acceptance or runtime test was used.
The evidence inventory still contains 106 unavailable historical files; their
recorded hashes are preserved, never reclassified as passed.

[Workflow](../guides/project-reviews.md), [full D11 plan](../plans/expansion-plans/plc-alarms-prodiag.md),
and [complete execution queue](../plans/offline-execution-20261016.md).
