# Safety rows (D14) — 28 October 2026

Evidence [`local.safety_changes.20261028`](../../evidence/local.safety_changes.20261028.json).
Runner [`scripts/qualify_safety_changes.py`](../../scripts/qualify_safety_changes.py), run `sf3`, on a
frozen Debug build in `D:\wp\b63` (sources extracted with `git archive`, with a `source-manifest.json`).
The run used one fresh owned scratch under TIA Portal V21 Update 3. No safety login, password,
acceptance, commissioning or download.

## Fail-safe runtime access

An F-CPU compiles only when a local user holds the runtime right "Full access including
fail-safe". Scratch provisioning never granted it, so the safety rows were blocked
([earlier record](wave10-native-slot-20261027.md)). On 28 October the user approved granting it
in owned safety-test scratch projects only.

`hardware_add` now takes `failsafeRuntimeAccess: true` together with `configureScratchSecurity: true`.
It is refused without `configureScratchSecurity` and, like that option, applies only to new devices
in the worker's own scratch. The generated runtime role then holds `ProtectionLevelFailsafeAccess`
("Full access including fail-safe") instead of "Full access". The credential metadata and the
report record the granted right. The password is never returned.

## What ran

The scratch gets a CPU 1511F-1 PN V4.1 (`6ES7 511-1FL03-0AB0`) with that option. It also gets an
ET 200SP station with two F-DI 8x24VDC HF on the F-CPU's IO system, set up through the
remote-I/O route of [`probe_safety_kinds.py`](../../scripts/probe_safety_kinds.py).

| Row | Result |
| --- | --- |
| `safety_inspect` | Exact F-PLC, runtime group `Main_Safety_RTG1`, program signatures; explicit gaps; truncation reported at `maxItems: 1`; the standard scratch PLC refused (`UnsupportedCapability`) |
| `safety_validation_inspect` | Both F-DI listed with their F-parameters; explicit gaps; the standard PLC refused (`UnsupportedCapability`) |
| `safety_printout` | A Microsoft Print to PDF file whose bytes match the reported size and SHA-256; the absent XPS printer refused (`PrinterUnavailable`); a layout name with a space refused (`InvalidRequest`) |
| `change.safety_fparameter_set` | One F-DI's `Failsafe_FDestinationAddress` moved to a free address with checkpoint, device compile, configured-hardware preservation and save; `change_status` equal to the apply result; an exact state diff limited to that F-DI; fresh-reopen persistence; checkpoint restore into a new directory equal to the reopened state |

`safety_fparameter_set` refusals: a non-allowlisted attribute (`UnsupportedAttribute`), a wrong
expected value (`ExpectedValueMismatch`), a missing acknowledgement (`InvalidRequest`), the other
F-DI's address (`AddressConflict`), this F-DI's `Failsafe_FMonitoringtime` (`AttributeNotWritable`)
and a wrong `previewHash` (`PreviewMismatch`).

Verified: `safety_inspect`, `safety_validation_inspect`, `safety_printout`,
`change.safety_fparameter_set` (15 cells; matrix 512 of 2,031 open, catalog `0.4.62`).

## Findings

- **The F-block import needs a license.** The first run (`sf1`, build `b61`) passed every read,
  refusal and the F-parameter change. It then failed `safety_import_xml`'s apply: TIA refused
  the import with "No safety license available". The F-compile and the F-parameter change work
  without a license. After the user activated a STEP 7 Safety license, the import passed
  ([below](#f-block-import-with-a-safety-license)).
- **F-monitoring time is not writable on this F-DI.** The ET 200SP F-DI does not declare
  `Failsafe_FMonitoringtime` writable, so only the F-destination address is qualified.
- **Cyclic IO bandwidth is compile-derived.** The F-CPU interface's
  `CalculatedBandwidthForCyclicIoData` reads 7040 after a compile and 0 after a fresh reopen.
  The second run (`sf2`, build `b62`) failed its reopen comparison on it. The runner now records
  that attribute across the reopen instead of comparing it, as the GSD runner does. Its
  recomputation at the next compile is recorded as a side effect. `sf1` and `sf2` are retained
  and not cited.

## Limits

- One F-CPU, one ET 200SP station and two F-DI; other F-CPUs, F-I/O and firmware are not covered.
- A printout or signature read does not validate, accept or approve a safety program.
- `safety.logic_changes` and `safety.hardware` stay deferred backlog features.

## F-block import with a safety license

Evidence [`local.safety_import.20261028`](../../evidence/local.safety_import.20261028.json). Run `sf4`
of the same runner, on a frozen Debug build in `D:\wp\b64`, with an activated STEP 7 Safety license.
The full `sf3` scope passed again.

`change.safety_import_xml` re-imported the generated F-FBD block `Main_Safety_RTG1` with one filled
comment text:

- The import ran with checkpoint, PLC compile and save, and `change_status` equal to the apply
  result. The comment is in the native export.
- An exact state diff is limited to that block, apart from one recorded side effect (next
  paragraph).
- A wrong `previewHash` was refused (`PreviewMismatch`), as were the prepare refusals for a
  missing acknowledgement, a scope mismatch and a standard block (`SafetyBoundary`).
- The import persisted across a fresh Portal reopen. A second import was restored from its
  checkpoint into a new directory equal to the reopened state.

Finding: the import's PLC software compile leaves the F-compiler block `DB1_C` (System
blocks/STEP 7 Safety/Compiler blocks, know-how protected) changed and inconsistent. The next
device compile, here the following F-parameter change, regenerates it as consistent. The kind does
not run a device compile, so after a safety import the F-program needs a full compile before it
is consistent everywhere.

Verified: `change.safety_import_xml` (6 cells). Every non-deferred D14 cell is now closed (matrix
502 of 2,031 open, catalog `0.4.64`).
