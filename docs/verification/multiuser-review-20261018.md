# Supplied Project Server observations — 18 October 2026

Baseline: `542953aa32b69db39a5ad1eb9cad5998e049d662`. Implements the portable MU-02 slice of the [full X05 plan](../plans/expansion-plans/multiuser-project-server.md), through [two offline review tools](../reference/multiuser-observations.md). This is supplied-observation handling, not a Project Server adapter or native conflict/recovery proof.

## Delivered behavior

Strict before/after input retains exact server, group, project, session and marking declarations. Coverage states, public diagnostics, unknown revisions/locks/session freshness, missing parents and deleted-object marking omissions remain explicit. Comparison never converts a missing row into native deletion, a name into stable identity, or an empty marking list into conflict-free proof. Unchanged rows remain inspectable.

Original UTF-8 bytes and reports are content-addressed. Reopening rebuilds the report and rejects tampering or a newly hashed forged report. Committed upload release and Host restart preserve source bytes and bounded pages; no acquisition or operation is resumed. The catalog marks both tools partial/unverified. The generic native/external journal is not callable through these tools.

## Focused execution

- Managed: **61 checks**, including exact identities, coverage states, missing parent declarations, contradictory locks, malformed UTF-8/duplicate fields, row/depth/byte limits, the complete 2,048-marking boundary, persistence and tampering.
- Actual MCP: **65 checks**, including independent input/output schema validation, byte-preserving artifact download, complete paged observations/changes/issues, invalid identity/coverage/credential fields, upload release, two clean Host lifetimes, source/report tampering, rehashed report forgery and a large-row byte-budget cursor. Native workers are explicitly disabled and absent.
- Contract coverage: **220 tools, 56 mutation kinds, 294 features**. The 106 unavailable historical evidence records are preserved exactly, not reconstructed.

## Frozen regression scope

Both snapshots contain the same 2,134 source files, including uncommitted implementation, fixtures, schemas, tests and runners:

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/multiuser-review-01/aggregate-source-manifest.json` | `72ed37e1df5d8961333bd7616dfc5cf8004efc093783cef2513227f342af190a` |
| `artifacts/multiuser-review-01/package-source-manifest.json` | `d0bebfaa75dd7440891c65befb64bdf9842da34796b05b01d040383fe4badaed` |

The frozen aggregate passes all **43 managed suites**, **1,464 Python tests** with one unavailable-evidence skip, and all configured MCP workflows. All **28 relocated-package stages** pass, including the same 65 Multiuser checks and 644 runtime checks. The bundle contains 1,501 files with manifest SHA-256 `7248b6b1564aa83d1a896058ffdd78e05188564669ecddbf943fe8950631fb31`; this proves consistency, not a publisher signature.

`artifacts/multiuser-review-01/verification.json` binds both complete run results, compares the identical source manifests, confirms all historical evidence bindings unchanged, and records post-freeze documentation-only changes. No implementation, schema, fixture or runner changed after the snapshots. No regression run failed in this increment.

## Remaining work

TC-02 supplied Teamcenter metadata review and AI-03 authenticated Add-In handoff remain next offline work. Native Project Server identity, service inventory, locks, session lifecycle, markings, publication, independent outcome reconciliation, two-user conflicts and UI acceptance remain open under MU-03–10. No Siemens product, SDK, server, license, native account or network endpoint was used. The complete 44-workstream goal remains active. Exact-head integration/CI status is recorded separately; account-related CI refusal is not a passing test run.
