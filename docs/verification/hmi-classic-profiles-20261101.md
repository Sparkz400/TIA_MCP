# Classic import on WinCC RT Advanced and KTP700 Basic PN — 1–2 November 2026

**Status:** passed. Build hbf5 (`750121ea`) ran `hmi_classic_import` on a qualification Host
(`--hmi-change-qualification <profile>`) in owned copies its own worker retrieved. The curation commit then
admitted both profiles in production (`HmiEngineeringRules` admission table).

| Profile and fixture | Route | Interruption |
| --- | --- | --- |
| WinCC RT Advanced, seeded HmiSeeder baseline (`artifacts/q/hps`) | [`local.hmi.classic-import.profiles.route.20261101`](../../evidence/local.hmi.classic-import.profiles.route.20261101.json), 72/72 | [`local.hmi.classic-import.profiles.interruption.20261102`](../../evidence/local.hmi.classic-import.profiles.interruption.20261102.json), 8/8 |
| KTP700 Basic PN, seeded HmiSeeder baseline | the same evidence, 71/71 | the same evidence, 8/8 |

Neither profile has a desk-configured fixture. As for WinCC Unified PC RT (ruling R4), the runs retrieved the
seeded baseline of the 25 October bridge run, which a fresh `HmiSeeder qualify` created on this PC. Its HMIs
have no start screen, so the baseline compile reports one error ("A start screen has not been configured" on
RT Advanced, "No start screen defined or the start screen has been deleted" on Basic). The kind's compile rule
requires that error, and every other compiler row, unchanged by the write; both runs found it unchanged after
every apply. The KTP700 Basic PN fixture has no popup (its import terminated the Portal in run h2/s1), so the
popup refusal is not run there.

## What the runs checked

The route of [1 November](hmi-gate-route-20261101.md) with the gates of the
[lifecycle record](hmi-lifecycle-20261101.md): refusals before any write; BackColor on both HMIs, no-op and
GridColor imports, a no-op import of the other screen and a tag-table import adding one tag, each with every
apply stage, compiler rows unchanged, an independent readback of the target, `hmi_inspect` of every HMI
unchanged outside the intended delta, same-key replay and another key refused; compiler scope (the target
`HmiTarget` only); checkpoint restore; normal lifecycle of both Hosts and an immediate read-write reopen by a
new TIA process. Interruption (`scripts/qualify_interruption.py` `ci-*`): worker loss at the checkpoint, during
the baseline compile, the native write and the compile, and after the save; Host loss at the checkpoint, the
write and the save; each reconciled once without replay.

## Limits

Screens and tag tables only, on these two seeded fixtures. The start screen stays unset, so neither fixture
compiles cleanly; a desk-configured fixture (start screen set in the editor) would replace them. Runtime,
visual behavior and PLC communication are not covered.
