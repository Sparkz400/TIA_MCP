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

## Actual binding: `hmi_dynamization_create`

**Status:** passed: the route on build hbfc (`ebe8ba13`), the interruption on build hbfd (`754068ce`); the curation commit then admitted `unified-comfort` and `unified-pc` in production.

| Profile and fixture | Route | Interruption |
| --- | --- | --- |
| MTP700 Unified Comfort, desk-configured pair | [`local.hmi.dynamization.route.20261102`](../../evidence/local.hmi.dynamization.route.20261102.json), 48/48 | [`local.hmi.dynamization.interruption.20261102`](../../evidence/local.hmi.dynamization.interruption.20261102.json), 8/8 |
| WinCC Unified PC RT, seeded baseline | the same evidence, 48/48 | the same evidence, 8/8 |

`scripts/qualify_hmi_gate_route.py --kind hmi_dynamization_create` first created its items through the already
admitted `hmi_screen_create`: two IOFields, a bar and a gauge. Those setup applies come before the reference
capture. The refusals before any write:

- the same payload under `hmi_engineering` (`HmiPreservationUnavailable`);
- an operation outside the kind, `Visible` by a tag that is not Bool, `Left` (outside ProcessValue, Visible and
  Enabled), a tag of the other HMI, and a `dataType` other than the tag's (`InvalidRequest`);
- `ProcessValue` of a rectangle, which has no such typed setter (`HmiAttributeUnavailable`).

It then bound three `ProcessValue` properties:

- an IOField to `LinkedSignal`, a root PLC tag;
- a bar to `LinkedDbValue`, a global DB member;
- an IOField on HMI_RT_2 to `SharedValue`, an internal tag.

Each binding was read back: the dynamization with tag, type `Tag` and no indirect addressing, and the
`hmi_references` chain item → HMI tag resolved. For the two PLC-backed tags, their PLC declaration resolved too.
`hmi_inspect` and the typed model stayed unchanged outside the new dynamization node. In the item's row only its
`dynamizations` and their `behaviorCoverage` count changed. Interruption (`dy-*`) bound a new IOField to
`LinkedSignal`.

Found by these runs:

1. V21 gives an internal tag the localized pseudo-connection `<Internal tag>`. The body treated any nonempty
   connection as PLC-backed and refused `SharedValue` with `DependencyUnresolved` (run hb1). A tag without a PLC
   tag, without an address, and with no connection of its HMI is now internal. The label text is never compared.
   Fixed in `6c9db0d2`.
2. The runner read only the first page of `hmi_references` (200 edges), where the new edges are missing. It also
   compared the item row's behavior count, which the new dynamization changes (runs hb3, hb4). Fixed in
   `3a8563bf`.
3. Two interruption-runner defects on the setup apply. The presence check raised while the setup item did not
   exist yet (run hd1, fixed in `89645805`). The no-replay check counted the setup's completed admission as a
   second apply (run hd3, fixed in `754068ce`).
4. `hmi_references` captures screen items only when the request names their screen (`screenPath`), so the chain
   was still empty (run hb5). The readback now asks with the item's screen. Fixed in `ebe8ba13`.

Limits: script, resource-list, flashing and expression dynamizations, replacing a dynamization, and faceplate
interface bindings are not offered. Whether the bound values show in runtime needs the desk.

## Scripts: `hmi_event_handler_create`

**Status:** passed: the route on build hbfe (`f5c4efe1`), the interruption on build hbfb (`5f715cdc`); the curation commit then admitted `unified-comfort` and `unified-pc` in production.

| Profile and fixture | Route | Interruption |
| --- | --- | --- |
| MTP700 Unified Comfort, desk-configured pair | [`local.hmi.event-handler.route.20261102`](../../evidence/local.hmi.event-handler.route.20261102.json), 47/47 | [`local.hmi.event-handler.interruption.20261102`](../../evidence/local.hmi.event-handler.interruption.20261102.json), 8/8 |
| WinCC Unified PC RT, seeded baseline | the same evidence, 47/47 | the same evidence, 8/8 |

`scripts/qualify_hmi_gate_route.py --kind hmi_event_handler_create` first created a button through
`hmi_screen_create` (setup). For each owner it learns the object's own event types from the worker's refusal of an
undocumented one, and picks one without a handler. The refusals before any write:

- the existing `Loaded` handler of the seeded Overview_A (`HmiNameConflict`; the kind never replaces a handler or
  script);
- the same payload under `hmi_engineering` (`HmiPreservationUnavailable`);
- `InvalidRequest` for: an operation outside the kind, an undocumented event, event `None`, a blank script, a
  script over 16384 characters, a script with a control character, and a Classic target.

It then added a script handler to the screen Overview_A, to a seeded rectangle and to the setup button. Each was
read back with its exact script text in CRLF form and its SHA-256; the script is never executed. `hmi_inspect` and the typed model
stayed unchanged outside the new handler node. In an item's row only its `eventHandlers` and their
`behaviorCoverage` count changed. Interruption (`eh-*`) added a handler to the screen Overview_A on HMI_RT_1.

Found by these runs:

1. The route's first choice, `Loaded`, already had a handler on the seeded screen, and the kind correctly refused
   it (run he1). The runner now picks an event without a handler.
2. V21 keeps `Script.ScriptCode` with CRLF line ends. An LF script read back with CRLF (its SHA-256 is that of the
   CRLF form), so the exact readback failed and every apply ended in `recovery_required` (run hn1). The worker now
   writes the CRLF form and compares the readback with it, and a lone CR refuses. Fixed in `5f715cdc`.
3. Once an item had a handler, `hmi_inspect` reported the items section of its screen with a changed coverage
   status: the behavior reader also passed the script part through the generic property reader, whose result
   the script snapshot replaced anyway (run he7). The script part is now read through its snapshot only. Fixed
   in `f5c4efe1`.

Limits: property-event handlers, custom web/widget container events and global script modules are not offered.
What a script does in runtime needs the desk.
