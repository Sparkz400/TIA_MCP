# Unified text-list authoring — 18 October 2026

Implementation, focused and full frozen verification pass on base
`17e6476c7b9b4bb2c8ca10fb2b106ea360db76ae`. Git integration is recorded separately in the evidence directory.
No Siemens SDK, TIA, PLCSIM or native caller is used.

The new `hmi_text_list_author` tool reuses the HMI retention store to publish paired
plain configuration/translation YAML candidates and retain original structured
intent. Reopening by record ID rederives and checks both files and the entire
manifest. Existing component review versions and schemas remain unchanged.
The [reference](../reference/unified-text-list-authoring.md) documents formats,
bounds, restrictions and the separate native import/preservation gates.

The managed tests exercise input contradictions, identity and path limits, Unicode,
file/record tampering, repeat publication, upload release, reopening and retention
capacity. The actual MCP harness downloads candidates, compares their parsed YAML
against original intent and retained bilingual fixtures, feeds them into a selected
Unified component proposal, releases uploads and restarts the Host. An independent
PyYAML oracle checks complete trees and rejects content mutations with honest
replacement hashes. It is a test-only dependency, not a Host runtime dependency.

Two initial managed runs exposed and retained implementation failures: reopening
reused an already-parented JSON node, and programmatically constructed exact Int32
bounds were rejected by the Int64-only accessor. Original input is now cloned for
the recomputed manifest, and exact Int32/Int64 values are accepted without string
or fractional coercion. Their logs remain under
`artifacts/hmi-unified-text-output-01/`; final validation is recorded below.

Still open: complete C01 content/consumer/output mapping and regeneration; other
authored output formats; system/formatted text-list support; exact native numeric
ranges and installed languages; native import, compile, reopen, independent peer
preservation, recovery and runtime. The full 44-workstream goal remains active.

The initial focused managed run passes 49 authoring checks; the final frozen
aggregate/package runs include all 56 checks, including entry/language/text counts
and escaping amplification. The focused actual
MCP workflow passes 941 checks, including three authored packets and twelve
independent content mutations. Both Host lifetimes exit cleanly without workers.
Five Python oracle/schema tests pass. An initial MCP run failed because the test
expected a review-specific code for a traversal path; the existing shared guard
correctly returned `PackageInvalid`. The corrected check preserves that refusal.

## Final frozen validation

Two identical 2450-file source snapshots pass all 44 managed suites, 1525 Python
tests (one existing unavailable-evidence skip), 25 Explorer model/client checks
and all 38 package stages. Both runs pass 56 managed authoring checks and the full
941-check HMI MCP workflow, including all three authored packets, twelve
independent content mutations and the component-proposal handoff. Both also pass
1132 compatibility checks across 19 original v1-v5 records. Original files remain
unchanged, and both Host lifetimes exit cleanly without workers.

The portable package has 1765 files; its manifest is
`bd71b21e033ecfcab57542af18652d95f696a1229e5f55e4741e0a8ce391feb2`. The workflow passes against relocated packaged binaries. Five
Explorer assets are byte-identical to the previously tested package; no new browser
acceptance is claimed. PyYAML remains a test-only requirement.

All previous feature entries, HMI review schemas, original fixture bytes and 112
historical evidence references are preserved, including 106 unavailable inputs.
The new bilingual authoring request is an additional fixture. The original
44-workstream reconciliation is unchanged. Only final documentation differs from
the verified frozen sources. Receipts, original failed attempts, source manifests
and exact Git/CI state are retained in `artifacts/hmi-unified-text-output-01/`.
Hosted CI remains subject to the account billing/spending restriction. No native
gate or full-goal completion is promoted by this increment.
