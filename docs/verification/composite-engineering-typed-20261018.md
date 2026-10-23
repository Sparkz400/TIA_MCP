# Typed composite telegram and encoder review — 18 October 2026

Explicit composite v3 requests now bind supplied telegram, address and encoder
observations to their original records, phases and exact local IDs. This advances
C02 within the original 44-workstream scope. It does not finish native C02 or
the complete offline goal.

## Implemented behavior

The [workflow](../reference/composite-engineering-review.md) and strict
[request](../../contracts/composite-engineering-request.v3.schema.json)/[report](../../contracts/composite-engineering-review.v3.schema.json)
contracts add driveTelegram, driveTelegramAddress and technologyEncoder selectors.
AML associations remain explicit declarations. Startdrive encoder associations
require the original drive record, phase and telegram ID; equal numbers, names
or addresses cannot substitute. Model-backed routes require original node ID
and path. Different domains, known external-encoder number contradictions,
disconnected/unknown/contradictory states and unresolved PLC scope stay visible.
Documented numbers do not qualify native CPU/firmware/provider/layout eligibility.

Consumer context adds exact declared encoder target-to-owner links and mapped
owner/hardware/address-PLC roots. Indirect telegram links require context-listed
drive records. Contradictions cannot create eligible links. Saved graph evidence
remains separate, shared traversal/byte limits remain enforced and original
encoder coverage/policies are retained. Every reopen revalidates dependencies.

V1/v2 algorithms retain their original output. Before editing the evaluator,
two actual packaged-Host records corresponding to a7b9e89 were captured with
38 original dependency/store files (182,027 bytes), summaries and pages. The
[prior-producer corpus](../../fixtures/composite-engineering/retained-v1-v2/README.md)
preserves exact bytes. The older retained-v1 corpus remains separately tested.

## Focused and complete verification

Evidence is retained under `artifacts/composite-typed-01/`.

- 50 typed managed checks cover all six routes, exact selector fields/phases,
  absent context references, old versions, different records/PLCs, state/domain/
  number contradictions, original Int64 unit arithmetic and reordered rows.
- All 29 retained positive/negative reports pass their strict schemas and
  independent original-input checks. Six mutation controls reject altered
  record/phase/telegram references and altered address arithmetic.
- 70 actual MCP checks cover intake, released uploads, restart, page/download
  equality, malformed typed requests, original AML/drive/technology corruption,
  discovery, oversized rows and complete original-byte download.
- Actual prior-producer compatibility passes 50 v1/v2 checks and the existing
  22 original-v1 checks across clean current-Host lifetimes.
- Existing composite suites pass 51 evaluator, 177 retention/recovery and
  49 consumer checks, including four real publication-process interruptions.

The initial new test helper used an incorrect upload method and limits
constructor; the first test run also exposed an online/proposal fixture that
correctly failed the existing admission rule. A later fixture expansion used
a local before its declaration. These test-only failures were corrected;
their logs remain alongside the passing evidence. Final review also found a
missing explicit-drive-context guard for encoder dependency mappings. The new
negative control fails on the earlier evaluator and passes after correction.
Its initial isolated builds lacked embedded resources; only the later reproduced
assertion failure counts as negative evidence. Earlier full runs remain separate
evidence; both final full runs use the corrected source. No native attempt occurred.

Two identical frozen source snapshots contain 2,288 files. The complete
aggregate passes 44 managed suites (242 general checks), 1,505 Python tests
(one existing skip), Explorer model/client checks and all configured Host
workflows. All 35 package/relocation stages pass, including typed mappings and
both prior-producer corpora. Existing drive and technology/encoder workflows
also pass in both runs.

| Frozen source manifest | SHA-256 |
| --- | --- |
| final-aggregate-source-manifest.json | `d54a87b7e34bbac351e85f42d4b2f23016992e25092fa0d0c546d6cad53161c2` |
| final-package-source-manifest.json | `403da341be8eb9d028d1e716bc81b3f82f3c81002c7ef21051fcce91cf7076b6` |

The package contains 1,628 files; its manifest SHA-256 is
`6be96c07df0a4ecd21dc162502f928dd8e381599d518a422a858dd6aa9730b3c`. Runtime checks total
642 for the aggregate and 640 for the package; variable polling/file checks
are reconciled against retained observations and fixed scenarios.

`verification.json` binds the complete results to both source manifests and
records documentation-only changes after freezing. `integration.json` records
the exact pushed head and hosted CI outcome independently of local validation.

## Remaining work and native gates

Next is C03 security/runtime identity and permission handoff, then the remaining
operation/profile work across all 44 workstreams. Native C02 exchange, rebind,
complete affected-peer preservation, compile/save/reopen/recovery and physical
communication/motion remain unqualified. Native profile/telegram/channel/member
eligibility and complete impact coverage are not inferred from this review.

No actual browser, Siemens product/SDK, physical target, power-loss or real
disk-full result is claimed. All 112 historical evidence entries are unchanged,
including 106 unavailable inputs. Hosted CI billing remains a separate gate.
