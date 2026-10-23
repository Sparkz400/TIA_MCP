# Explicit integer text-list conversion — 21 October 2026

The existing offline set-generation tool now supports explicit v3 integer
domain conversions for unchanged text-list states. Exact captured source and
target declarations, original predecessor/current files and preserved member
and output identities are mandatory. The six admitted signed/unsigned
8/16/32-bit types use checked interval representability; no cast, clamping,
wrapping, remapping or native operation occurs. Full source-domain
representability and listed-value representability remain separate facts.
The [workflow](../reference/unified-text-list-integer-conversion.md) defines
the contract and its remaining native/runtime boundaries.

Focused lane evidence is retained under `artifacts/hmi-conversion`:

| Evidence | Result |
| --- | --- |
| `managed-05/text-conversion/verification.json` | 461 checks, 83 retained cases and 23 explicit refusals. |
| `managed-05/text-coordinated/verification.json` | 147 existing coordinated-set checks. |
| `mcp-02/verification.json` | 446 checks through two actual offline Host lifetimes, including independent original XML, integer-domain, complete parsed YAML and peer-file oracles. |
| `prior-02/verification.json` | 142 checks over two actual prior-producer v1 sets, original child authors and mixed v1/v2 evolution. |
| `prior-v2-all-01/verification.json` | 362 checks against the complete original wave4 v2 producer fixture, including all 45 retained successful/refused cases, original bytes, restart and tamper controls. |
| `final-source-manifest.json` | Exact lane source/schema/test/harness/document/fixture hashes. |

The Host and focused managed builds complete with zero warnings/errors.
The three final Host qualification receipts bind the same Host SHA-256
`fe07c5edd303c8b86f2339b37a2e72f547b05ce6ad610d036d5ebc321b6644ef`
and Core SHA-256
`5b92d693232c223837786ff1a1f1dd4d92baf4f8553127d807379efe1b2d5b88`.
Every Host exits zero without forced termination or native workers.

Checks include signed/unsigned widening and narrowing; zero-crossing ranges;
exact Int32 minimum and UInt32 maximum refusal boundaries; concealed removal
of overflowing predecessor values; unchanged translations and interval IDs;
missing companions, stale declarations, manual drift and unrelated conflicts;
mixed v1/v3 lineage, regeneration and removal; seven altered-evidence oracle
controls; five honestly rehashed conversion-record controls; and original
input/predecessor/candidate tamper refusal. Schema validation covers every
successful and conflict request/record in the MCP workflow.

The portable [original v2 fixture](../../fixtures/hmi-conversion/retained-v2/README.md)
retains a successful mixed-lineage response and a manual-edit refusal with
51 original dependency files (347,021 bytes), producer provenance and exact
digests. Both responses and every copied original byte are checked in both
Host lifetimes by the new harness. Those files were copied from the previous
frozen aggregate, not regenerated using the new implementation.

Earlier development failures remain separate incomplete artifacts under
`managed-01`, `managed-02` and `managed-03`. The first exposed required-field
validation incorrectly treating the optional conversion selector as mandatory.
The later two exposed test assertions comparing JSON property order and
expecting the wrong existing error code. These were repaired before final
qualification; no earlier attempt was relabeled. `managed-04` and `mcp-01`
passed the initial matrix, before adding the final Int32-minimum and
nonobject-selector controls.

This lane did not run the combined aggregate or relocated-package suite;
the integration owner must qualify the combined frozen source and package.
No TIA dispatch, native import, PLC value conversion, physical device access,
full C01 completion or native quarantine clearance is claimed.
