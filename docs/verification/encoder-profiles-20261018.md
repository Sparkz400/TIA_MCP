# Typed supplied encoder profiles — 18 October 2026

This increment extends the existing technology-profile review/report tools with
explicit v4 encoder observations. It advances D13/C02 within the original
[44-workstream scope](../history/offline-scope-reconciliation-20261017.md), without
completing either workstream or native qualification.

## Implemented behavior

The [reference](../reference/technology-profile-review.md#typed-supplied-encoder-observations-v4)
and [contract](../../contracts/technology-profile-review.v1.schema.json) define
module, separate-module, channel, explicit-address, DB-member and Startdrive
telegram routes, with separate unknown and disconnected states. Exact saved
selectors retain ambiguity, identity mismatch, kind and PLC-scope outcomes.
Unknown readback units never become byte addresses. Sensor-index contradictions
remain visible without silently changing the supplied observation.

Telegram routes name an exact retained drive record, phase and row ID in the
same saved review. Every intake/reopen rebuilds those dependencies from original
inputs. Reports retain original telegram evidence, its drive comparison,
identity, domain and hardware resolution. A documented external-encoder number
is not proof of native eligibility, CPU/firmware support or a live connection.

V4 composes existing source retention and consumer context without changing old
record semantics. It adds bounded encoder pages, exact full artifact download,
incremental output limits, an eight-record/64 MiB drive-input bound and a combined
500-change proposal limit. Existing composite versions retain the v4 summary
and relationship selectors, revalidate indirect drive dependencies and charge
their bytes. Explicit composite encoder/telegram selectors and mapping rules
remain open.

## Focused verification

Evidence is retained under `artifacts/encoder-profiles-01/`.

- 205 existing technology-profile checks and 67 new encoder checks pass. These
  cover every route, explicit units and maximum Int32 values, unknowns,
  contradictory states, exact IDs, foreign PLCs, ambiguity, virtual objects,
  unknown scope, retained versions, sources, dependency corruption, rehashed
  report/authority forgery, both full 256-row phases and proposal limits.
- Genuine original drive inputs exercise the eight-record limit and separately
  cross 64 MiB within eight records. Large repeated telegram evidence exercises
  incremental report refusal, explicit page omission and full retention.
- Five Python schema tests pass. An independent original-input verifier
  recomputes selectors, unit arithmetic, exact-ID changes and drive references;
  it also rejects altered unit, identity, telegram and change results.
- The actual Host workflow passes 193 checks across two clean lifetimes,
  including v1–v4, source/consumer composition, upload release, restart,
  malformed requests, original dependencies, oversized pages and full downloads.
- The actual prior-producer harness passes 115 checks over four retained
  v1/v2/v3 records, including both source-free and source-bearing v3 records.
  The [fixture](../../fixtures/technology-profiles/retained-v1-v3/README.md) retains
  12 original files (129,916 bytes) and exact prior summaries/pages, captured
  from the packaged Host corresponding to `28d4ac9`. No legacy bytes are regenerated.
- Composite callers pass 51 evaluator, 177 retention/recovery and 49 consumer
  checks, including indirect drive-input corruption through v4 technology data.

The first test compile exposed a new test helper using the wrong upload-store
members; it was corrected to the existing API. The first execution exposed an
error-code mismatch in the new text-validation test; the new validator now uses
the technology-profile refusal code. The first Host attempt rejected a synthetic
foreign-PLC fixture outside the captured project root. The fixture now uses a
second PLC inside that root, and the rerun passes. Build/execution failure logs
and the incomplete first Host verification receipt remain with the local
evidence. None was a native execution attempt.

## Full frozen validation

Two snapshots contain the same 2,241 source files, based on
`28d4ac9b17436ae9ae5e7f0fe08b30446b9db7c2` plus this increment:

| Source manifest | SHA-256 |
| --- | --- |
| `aggregate-source-manifest.json` | `8b129ce9de8b1b86f8a0f6c903045e8c4c696832d6a6b49abdd7e018eb89e4cd` |
| `package-source-manifest.json` | `433a5f6edbbf3a1bfced19847e419e195ba0a83e599e68dce10a669b178767a2` |

The complete aggregate passes all 44 managed suites (242 general checks),
1,503 Python tests with one existing skip, Explorer model/client checks and all
configured Host workflows. All 34 package/relocation stages pass. Both runs pass
205 existing technology checks, 67 encoder checks, 193 technology MCP checks and
115 actual prior-version checks. Composite checks pass 51 evaluator,
177 retention/recovery, 49 consumer, 50 v1 MCP, 59 v2 MCP and 22 prior-producer
checks. Drive v1/v2 workflows and their independent original-input checks pass.

The package contains 1,583 files; its manifest SHA-256 is
`85b8a8caf9b638862b14564d43742346f83300c5c9ab7007289ce1f1c0b11b97`. Runtime checks total
643 for the aggregate and 656 for the package.
The verifier reconciles variable polling/file assertions against retained
observations while comparing the fixed scenarios with prior evidence.

`verification.json` records both exact source manifests, full results and
documentation-only changes after freezing. Production source, schemas, tests and
CI configuration are unchanged after freezing. `integration.json` records the
exact pushed head, hosted CI observations and final Git/worktree state. Hosted
execution is a separate gate from these local results.

## Remaining qualification

Native encoder capture, actual SDK signatures/provider availability, installed
CPU/firmware/TO profiles, channel/member existence, complete signal geometry,
same-instance wiring, native connect/disconnect, compile/save/reopen/recovery and
physical motion remain unqualified. The V21 generic and dedicated Startdrive
routes are preserved as distinct declared routes; no proprietary adapter was
invented from documentation alone.

No actual browser, Siemens SDK/product, physical target, power-loss or real
disk-full result is claimed. All 112 historical evidence entries retain their
metadata, including 106 unavailable inputs. The original scope reconciliation
remains unchanged. C02 explicit selectors/constraints, C03 security/runtime
handoff and the remaining operation/profile work stay active.
