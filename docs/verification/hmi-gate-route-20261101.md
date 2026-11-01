# HMI writes past the preservation gate — 1 November 2026

**Status:** passed. Build gh4 (`10763a35`), run on a qualification Host
(`--hmi-change-qualification <profile>`) in owned copies its own worker retrieved. The curation commit then
admitted the three profiles in production (`HmiEngineeringRules` admission table).

| Change kind | Profile and fixture | Route | Interruption |
| --- | --- | --- | --- |
| `hmi_classic_import` | TP700 Comfort, desk-configured pair | [`local.hmi.classic-import.route.20261101`](../../evidence/local.hmi.classic-import.route.20261101.json), 63/63 | [`local.hmi.classic-import.interruption.20261101`](../../evidence/local.hmi.classic-import.interruption.20261101.json), 8/8 |
| `hmi_external_tag_create` | MTP700 Unified Comfort, desk-configured pair; WinCC Unified PC RT, seeded baseline (`artifacts/q/hps`) | [`local.hmi.external-tag.route.20261101`](../../evidence/local.hmi.external-tag.route.20261101.json), 43/43 each | [`local.hmi.external-tag.interruption.20261101`](../../evidence/local.hmi.external-tag.interruption.20261101.json), 8/8 each |

## What the runs checked

- **Route** (`scripts/qualify_hmi_gate_route.py`): refusals before any write; for each apply (Classic: BackColor on
  both HMIs, no-op and GridColor imports, a no-op import of the start screen, a tag-table import adding one tag;
  Unified: a root tag and a global DB member on one HMI, a root tag on the other from its own PLC) every apply
  stage, compiler problem rows unchanged, an independent readback of the target, `hmi_inspect` of every HMI
  unchanged outside the intended delta, same-key replay and another key refused; checkpoint restore and a fresh
  reopen on a production Host.
- **Interruption** (`scripts/qualify_interruption.py` `ci-*`, `et-*`): worker loss at the checkpoint, during the
  baseline compile, the native write and the compile, and after the save; Host loss at the checkpoint, the write
  and the save. Each reconciled once without replay; worker kills released an unchanged project, the save and
  every Host loss held the admission, and after Host loss the journal ended applied with every stage.

## Defects the runs found

1. The prepared BackColor document lost TIA's XML declaration (`XDocument.ToString`), so the first native
   readback failed although only BackColor changed (run hr1; fixed in `9211e40f`).
2. The route runner compared compiler summary counts; the first compile after a retrieve lists the seeded
   Unified PC fixture's two password errors yet ends "errors: 0" (run hr4). It now compares problem rows
   (`1dd273b4`).
3. TIA's Unified export helper leaves `.ile/ItLikeEs.pid` and `.port` in the tag-table export folder while it
   runs; a capture that raced it moved an unchanged project's fingerprint, and apply refused `StaleRevision`
   (run hr5). Captures skip exactly these two files (`10763a35`).

## Limits

The qualified profiles and fixtures only: screens and tag tables on TP700 Comfort, root tags and scalar global DB
members on Unified Comfort and Unified PC. Classic import readback ignores `DocumentInfo`, export-local IDs and
formatting for the target (ruling R5); every other export is compared `Created`-only. Runtime, visual behavior,
PLC communication and customer projects are not covered. [HMI engineering](../guides/hmi-engineering.md).
