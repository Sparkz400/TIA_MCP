# Unified screen authoring past the preservation gate — 2 November 2026

`hmi_engineering` operations of the Unified screen surface get their own change kinds, following
`hmi_external_tag_create` ([1 November](hmi-gate-route-20261101.md)). Each kind accepts exactly the two standing
Unified capture notes, compiles after the checkpoint, compares preservation with the compiled capture and
requires the compiler's errors and warnings unchanged. The runs used a qualification Host
(`--hmi-change-qualification <profile>`) in owned copies its own worker retrieved: the desk-configured
MTP700 Unified Comfort pair and the seeded WinCC Unified PC RT baseline (`artifacts/q/hps`, with its two
transfer-password errors). Every route run also carries the compiler-scope, normal-lifecycle and lock-release
gates of the [lifecycle record](hmi-lifecycle-20261101.md).

## Graphics: `hmi_screen_create`

**Status:** passed on build hbf2 (`201f846c`). The curation commit then admitted `unified-comfort` and
`unified-pc` in production.

| Profile and fixture | Route | Interruption |
| --- | --- | --- |
| MTP700 Unified Comfort, desk-configured pair | [`local.hmi.screen-create.route.20261102`](../../evidence/local.hmi.screen-create.route.20261102.json), 49/49 | [`local.hmi.screen-create.interruption.20261102`](../../evidence/local.hmi.screen-create.interruption.20261102.json), 8/8 |
| WinCC Unified PC RT, seeded baseline | the same evidence, 49/49 | the same evidence, 8/8 |

`scripts/qualify_hmi_gate_route.py --kind hmi_screen_create` first checked the refusals before any write:

- the same payload under `hmi_engineering` (`HmiPreservationUnavailable`), another operation, a screen property
  other than Width and Height, a rectangle outside the screen, an item type outside the allowlist and a Classic
  target (`InvalidRequest`);
- an existing screen or item name (`HmiNameConflict`);
- a size no screen of the HMI has (`DependencyUnresolved`).

Then it created a screen on HMI_RT_1 sized like Overview_A (800 × 480 on the panel), an `HmiIOField` on that
screen, and an `HmiButton` on HMI_RT_2's seeded Overview_B. Each apply checked:

- every apply stage, the compiler rows unchanged, and the compile scoped to the owning device;
- the created node read back: name, size or rectangle, and native type (`...UI.Widgets.HmiIOField`, `...HmiButton`);
- `hmi_inspect` of both HMIs and the typed model unchanged outside the created node, the owning screen's row included;
- a same-key replay, and another key refused.

The run ended with a checkpoint restore and a fresh reopen. The production Host of that reopen still refused the
kind, because it had no production profile then.

Interruption (`sc-*`, a new screen on HMI_RT_1) covered worker loss at the checkpoint, during the baseline
compile, the native write and the compile, and after the save, plus Host loss at the checkpoint, the write and
the save. Each case reconciled once without replay. The Host-loss cases ran the apply to completion, so the
preservation check of the created screen also passed in those runs.

Limits: the allowlisted item types, screens sized like an existing screen, and these two fixtures only. Texts,
styling, screen groups, faceplates and nested containers are not offered. How the screens look and behave in
runtime needs the desk.
