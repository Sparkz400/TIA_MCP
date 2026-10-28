# Composite rebind plan, natively — 28 October 2026

`composite_engineering_rebind_plan` (C02: `AC-identity`, `AC-completeness`, `AC-preview`,
`AC-evidence`) is verified with `local.composite_rebind.20261028`. Runner:
[`scripts/qualify_composite_rebind.py`](../../scripts/qualify_composite_rebind.py), frozen Debug
build of `a5fdadc` (`D:\wp\rb06`), 57/57 steps, host exit 0. Matrix: 523 of 2,031 cells open
(catalog `0.4.62`).

## Why it was open

The plan acts only on before-phase TO hardware relationships that are `declared`, connected to one
resolved module or channel. The verified `to_connect` route connected output cams to PLC tags only,
so the native phases of `local.composite_capture.20261028` had nothing to break
([record](scattered-rows-20261028.md)).

## Fixture

An unconnected `TO_ExternalEncoder` fails compile (`local.technology_changes.20261028`), so its
`to_disconnect` cannot pass. A virtual `TO_PositioningAxis` 10.0 (`VirtualAxis.Mode = 1`) compiles
both with and without a sensor connection. Every change below uses checkpoint, compile and save.
Each one has an exact state diff (hardware, device inventory, program, connections and every TO's
`technology_read` connections). Named TIA side effects are recorded under `sideEffects`.

1. `hardware_plug` a TM PosInput 2 V2.0 (`RbEncA`, slot 2). Then `hardware_channel_attributes`
   sets its channel 0 (`Technology`/`Complex`) to `OperatingMode` `MotionControl` (UInt64 269; the
   default was 277, `TechnologyObject`).
2. `to_create` the virtual axis `RbAxis`. `to_connect` its sensor 0 to that channel. The connection
   reads back on the channel and on the input, output and input/output module of the same submodule.
3. Capture the before phase (`composite_engineering_capture` from a saved review). It declares
   `axis/sensors/0` on `RbEncA/items/RbEncA`, resolved as `capturedObject`.
4. `hardware_plug` a second TM PosInput 2 (`RbEncB`, slot 3) in the same mode. `to_disconnect` the
   sensor. Capture the after phase: the relationship is `disconnected`.

## Plan

A `composite-engineering-request.v3` declares the model correspondence `RbEncA -> RbEncB`. The node
IDs come from the saved reviews in the Host store, checked against their reviewId hash, as the
composite oracles read them. The plan lists exactly one entry:

- the before technology record, `RbAxis`, `axis/sensors/0`, role `sensor`, before target `RbEncA`;
- `afterState` `disconnected`, replacement `RbEncB`, the correspondence ID, `selectorCarriedOver`;
- state `planned`, with `to_connect`
  `{technologyPath, role: sensor, sensorIndex: 0, hardwarePath: RbEncB, channelType: Technology, channelIoType: Complex, channelNumber: 0}`.

That payload is prepared unchanged, applied and read back on `RbEncB`'s channel. A third capture
resolves the sensor to `RbEncB`, and the plan from the before phase to it reports no broken
connection. The same phases without the correspondence give one `noDeclaredReplacement` entry
without a payload. A malformed handle is refused as `CompositeInvalidReference`, and an altered
receipt digest as `CompositeIntegrityFailure`.

## Native findings

- **`OperatingMode` is module-wide.** On a TM PosInput 2, TIA applies the mode written to one
  channel to both channels and shrinks the module's output range from 24 to 8 bytes. Run `rb01`
  failed closed on that (`MutationVerificationFailed`). For an `OperatingMode` write, channel
  preservation now accepts the same mode on every channel and resized address lengths of the
  selected item only; start addresses and everything else stay exact (three new
  `TiaMcp.HardwareEngineering.Tests` cases). `OperatingMode` is now in the channel write allowlist.
- **The hardware identifier can change.** A mode change sometimes renumbers the submodule's hardware
  identifier (`Local~RbEncA` 257 → 258 in `rb04` and `rb06`, not in `rb02`).
- **A sensor connection changes more than the TO.** TIA puts the item's addresses into the servo
  process image (`ProcessImage` 0 → 32768). It also adds the tags
  `RbAxis_Sensor1_Interface_AddressIn/Out` (`PD_TEL83_IN/OUT`) with their PD system types.
  `to_disconnect` keeps those tags, so the next connection adds copies suffixed `(1)`.

The catalog now lists this evidence for `change.to_connect` (sensor on a channel target),
`change.to_disconnect` and `change.hardware_channel_attributes` (`OperatingMode`).

## Limits

- One virtual axis with one sensor, two modules of one type and one model correspondence. AML
  correspondences through hardware mappings, actor, torque, encoder and measuring-input roles,
  conflicting replacements and truncation are tested offline only.
- The replacement is declared, not inferred. The carried-over channel selector fits here because
  both modules share their type.
- Engineering only; nothing is downloaded.

## Runs not cited

- `rb01`: the `OperatingMode` change failed preservation (finding above).
- `rb02`, `rb04`: the runner had not yet named the connection's and the mode change's side effects.
- `rb03`: the runner read report page rows without their `index`/`value` wrapper.
- `rb05`: passed 57/57, but both connections had one step label, so the second side-effect record
  replaced the first.
