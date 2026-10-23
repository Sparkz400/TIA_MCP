# Internal security/runtime evaluator — 18 October 2026

This is a fully validated internal evaluator increment, not completion of C03 or
the full 44-workstream goal. The [reference](../reference/security-runtime-handoff.md)
defines the delivered internal behavior and remaining workflow integration.

Implemented: a bounded strict request/report and three original-observation
schemas; verified access-reader reuse; exact owner/context/identity checks;
separate permission outcomes and invalidation; immutable old access behavior.
All authority and assertion-admission flags stay false.

Focused verification under `artifacts/security-runtime-01/`:

- `managed-02`: 147 checks, comprising 73 existing access checks and 74 new
  handoff checks. Fifty-five positive/negative reports cover identity changes,
  expired/reconnected contexts, credential/client changes, permissions, unknown
  fields and supplied/synthetic provenance. Malformed/duplicate/bounded inputs
  and corrupt dependencies are refused.
- Independent original-input/schema validation covers all 55 reports. Eight
  deliberately altered reports are rejected. The oracle checks original hashes,
  all reported constraints, scoped outcomes and false admission flags.
- Release Core/Host compilation succeeds without warnings.
- `access-mcp`: 42 existing actual Host workflow checks, including restart,
  released uploads, paging, retained bytes and tamper refusal.
- `access-prior-mcp`: 36 compatibility checks across two clean Host lifetimes
  reopen two actual prior-producer records without changing their five original
  files (13,659 bytes). The corpus includes proposal and observation reports.
  [Retained corpus](../../fixtures/access-review/retained-v1/README.md).

## Complete frozen validation

Two identical frozen source snapshots contain 2,303 files. The full aggregate
passes 44 managed suites (242 general checks), 1,507 Python tests (one existing
skip), Explorer model/client tests and all configured Host workflows. All 36
package/relocation stages pass. Both runs include the 147 access/handoff checks,
55 original-input/schema cases, eight report mutation controls, 42 existing
access MCP checks and 36 actual prior-record compatibility checks. Existing
composite, drive/encoder and other domain workflows also pass.

An additional isolated probe runs the complete 147-check access/handoff harness
against the exact Core and Graph DLLs from the finished package. Their hashes
match the bundle, and its 55 reports pass the independent original-input oracle
and eight mutation controls. This directly exercises the packaged evaluator
without inventing a public MCP route.

| Frozen source manifest | SHA-256 |
| --- | --- |
| aggregate-source-manifest.json | `75bd7173e025fda20a5e4d6973de071f9f5907ef9f8c81f9763c7c8761a1e213` |
| package-source-manifest.json | `ad2eee16018b037428013ee293cbc335d0b483eed27ef0ea83b54afa40477f88` |

The package contains 1,641 files; manifest SHA-256
`9961c2b64fa63359006e988318ee512ebc62ae092d5080b7a4d912bc45ce2bc8`. Runtime MCP checks total 643
in the aggregate and 644 in the package. Variable polling/file counts
are reconciled against retained observations and fixed scenarios.

`artifacts/security-runtime-01/verification.json` binds results to both frozen
source manifests and identifies documentation-only changes afterward. The
separate integration receipt records the pushed head and hosted CI outcome.
All 112 historical evidence entries are unchanged; 106 inputs remain unavailable.

## Remaining work

Current handoff evaluation has no public MCP route and no capture/session
lifecycle integration. Next: retain original observations independently of
released uploads, publish/reopen/page/download handoff reviews, then integrate
runtime/capture lifecycle, independent quality/gap checks and migration evidence.
The full C03 contract and all remaining 44-workstream obligations stay open.
No actual browser, Siemens SDK/product, physical target, native mutation,
power-loss or real disk-full test is claimed. Hosted CI billing remains a
separate gate. Matching supplied observations never grant live authority.
