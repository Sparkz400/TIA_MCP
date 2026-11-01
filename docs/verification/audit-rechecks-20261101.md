# Native rechecks of three audit fixes: runs of 1 November 2026

The [second round](#second-round-the-open-items) at the end closes the items this round left open and the master-copy
tag finding.

Three fixes merged on 1 November were checked offline only: symbolic PLCSIM writes to output tags (PR 182), the
uncompiled first motion object, and F-I/O tag addresses from VCI packages and library master copies (PR 183). Each
run below used owned scratch projects that the runner created through the MCP Host, one run at a time, from frozen
Debug builds (`git archive`; Host, Openness worker and Simulator worker): `0f4ef125` (`artifacts/an1`) and, for the
F-I/O rerun after a runner fixture fix, `4a0dea87` (`artifacts/an2`).

| Evidence | Runner | Result |
| --- | --- | --- |
| `local.sim.output-refusal.20261101` | `scripts/qualify_plcsim_signals.py` | 85/85 steps |
| `local.technology.first-motion.20261101` | `scripts/qualify_first_motion.py`, `scripts/probe_technology_versions.py` | 12/12 and 50/50 steps |
| `local.safety.fio-routes.20261101` | `scripts/qualify_fio_tag_routes.py` | 48/48 steps (`fr_fed1eb9d`); the first run stopped on a fixture rename, below |

## Symbolic output writes

One owned PLCSIM Advanced V8 CPU 1511 instance on Softbus of an owned scratch CPU 1511-1 PN V4.1. PLCSIM's tag list
reports `SigOutWord` (`%QW22`) and `SigOutByte` (`%QB20`) in the area `Output`; the program writes both every scan.

- `sim_write` of `SigOutWord`, quoted (`"SigOutWord"`) and unquoted, refused with `InvalidRequest`, `details.symbol`
  as sent and `details.area` `Output`; single `sim_read` calls before and after read 4661 both times.
- `sim_write_signals` of `SigMarker`, `"SigDb".VInt` and `SigOutByte` refused whole with `InvalidRequest` naming
  `SigOutByte`; `SigMarker` and `VInt` stayed 1234, `SigOutByte` 164.
- `sim_test` with one step writing `SigOutByte` refused with `InvalidRequest` naming it, and no file appeared under the
  Host's `simulation-tests` directory.
- One `sim_write` of `SigMarker`, `"SigDb".VInt` and the input `SigInByte` then read back 4321, -1234 and 90.

The run also repeated every check of `local.plcsim_signals.20261030`. `diagnostic_plcsim_write` shares the check and
was not run natively; output elements and members of a structured output tag were not exercised.

## First motion object

`scripts/qualify_first_motion.py`, run `fm_8e53f7ee`, 12/12 steps, in an owned scratch CPU 1511-1 PN V4.1 that held no
technology object:

- `to_create` of `TO_PositioningAxis` 10.0 previewed `firstMotionObject: true` with its warning (the profile admitted
  the pair as accepted).
- `change_apply` with `compile=false` refused with `CompileRequired`. `change_status` still showed the change
  `prepared` with no stage, no `before.zap21` existed for it, no admission stayed open, and `technology_read` and the
  program inventory equalled the baseline.
- The same axis with parameters `{"VirtualAxis.Mode": 1}` applied with checkpoint, compile (0 errors) and save.
- `TO_ExternalEncoder` 10.0 then previewed without `firstMotionObject`, applied with `compile=false` and `save=false`
  (checkpoint and mutation stages only), and `change_restore` retrieved its checkpoint into a new directory; the
  recovered project holds the axis and not the encoder.

`scripts/probe_technology_versions.py` compiles its motion cells since PR 183. Its rerun, `tv_7becd8fa`, 50/50 steps on
the same three CPUs as `local.technology.version_profiles.20261031`, gave the same 45 cells: 21 accepted with the same
type and version, and the 24 recorded refusals refused at prepare by the embedded profile (`TechnologyVersionRefused`),
so V21's Create was not asked again. The 10 accepted motion creates applied with compile; 9 failed that compile as
unconnected (`CompileFailed` after the create; `TO_CommandTable` 8.0 compiled). All 21 accepted cells were restored in
place by `change_restore`; on 31 October 8 motion cells had needed the Portal closed unsaved instead. The contract
cells are unchanged.

## F-I/O tag addresses

One owned scratch with the standard CPU 1511-1 PN V4.1 and an added CPU 1511F-1 PN `6ES7 511-1FL03-0AB0/V4.1`
(`configureScratchSecurity`, `failsafeRuntimeAccess`; its generated scratch user holds Full access including fail-safe,
in the owned scratch only), built as in `scripts/probe_safety_kinds.py`: an ET 200SP station with two F-DI 8x24VDC HF
`6ES7 136-6BA01-0CA0/V2.0` on the F-CPU's IO system. The first F-DI's inputs start at byte 0, so the F-I/O address is
`%I0.0`. Run `fr_fed1eb9d` passed 48/48 steps on a build of `4a0dea87` (`artifacts/an2`).

Each refusal came from `change_prepare` as `SafetyBoundary` with `details.fIoPath` the first F-DI's F-I/O, and every
tag table and tag of both PLCs (`software_inspect`) was unchanged afterwards:

- `tag_create` of `FioIn` at `%I0.0` in the F-PLC's table `FioTags` (`details.logicalAddress` `%I0.0`).
- `tag_set` of `FioProbe`'s `LogicalAddress` from `%M40.0` to `%I0.0`.
- `tag_table_import_xml` of a new table `FioImport` with one tag at `%I0.0`.
- `vci_synchronize` WorkspaceToProject of `FioTags`, mapped by export to a workspace outside the project and the
  artifact store, after its package file `FioTags.xml` was edited from `%M40.0` to `%I0.0`. V21 offered only
  `SimaticML` as the tag table's VCI format.
- `library_instantiate` of a master copy of the standard PLC's `McTags` into the F-PLC (`details.masterCopy` `McTags`).

The bit-memory routes applied with checkpoint, compile and save: the package edited to `%M41.0` synchronized
project-ward and `FioProbe` read back at `%M41.0`; `McTags` with `McProbe` at `%M60.0` imported into the standard
PLC; after `McProbe` and `McTags` were deleted there, the master copy instantiated into the standard PLC and `McTags`
read back with `McProbe` at `%M60.0`.

Not exercised: the project-ward `vci_conflict_resolve` refusal, F-output ranges, other F-modules, and block or SCL
imports with absolute operands (outside the rule).

### Finding: a colliding master-copy tag is renamed

The first run (`fr_eeb8be6f`, build `0f4ef125`, 45/46 steps) renamed the standard PLC's `McTags` to `McTagsOld`
instead of deleting it, then instantiated the master copy into the standard PLC. `McProbe` was still in `McTagsOld`,
so TIA created the master copy's tag as `McProbe(1)`, and `library_instantiate` reported the change applied: its
readback names the created table only. The runner now deletes the tag and the table first (`4a0dea87`). The product
behavior was recorded in the [library guide](../guides/library-management.md) and the [TODO](../TODO.md); it is fixed
and rechecked in the [second round](#master-copy-tag-collisions).

## Second round: the open items

Two code fixes, offline tests first, then one runner at a time on frozen Debug builds (`git archive`; Host, Openness
worker and Simulator worker), each in owned scratch projects the runner created through the MCP Host: `5044285e`
(`artifacts/oi1`), and for the canonical-hash step `c204e4e4` (`artifacts/oi2`), which adds PR 186.

- `software_unit_create_from` brings a unit's tag tables from a library master copy unread, like `library_instantiate`
  of a tag-table master copy, and had no F-I/O rule. It now refuses at prepare and apply with `SafetyBoundary` while
  the PLC owns, or may own, any F-I/O range (`RejectFIoMasterCopy`, the rule of PR 183).
- A master-copy tag whose name the software scope already uses is created as `<name>(<n>)`. Openness V21 exposes only
  a master copy's content names, so its tag names cannot be read before the create; the readback of
  `library_instantiate` now compares the created tags and user constants with the scope's names before the create and
  fails the change with `MutationVerificationFailed` (`details.renamedTags`), so `change_restore` retrieves the
  checkpoint. `software_unit_create_from` lists such tags with the renamed blocks and types it refuses after the
  compile.

| Evidence | Runner | Result |
| --- | --- | --- |
| `local.safety.fio-routes-2.20261101` | `scripts/qualify_fio_tag_routes.py` | 75/75 steps (`fr_af86ff5b`) |
| `local.library.tag-collision.20261101` | `scripts/qualify_master_copy_tag_collision.py` | 41/41 steps (`tc_531cbab9`) |
| `local.sim.output-refusal-2.20261101` | `scripts/qualify_plcsim_signals.py`, `scripts/qualify_plcsim_diagnostics.py` | 95/95 (`ps_95b2f7f7`) and 60/60 steps (`ps_6b3362fe`) |
| `local.component.canonical-hash.20261101` | `scripts/qualify_plc_component.py --canonical-hash-probe` | passed (`artifacts/od`); the first attempt stopped in the runner, below |

### F-I/O routes: conflict resolution and unit copies

The fixture of the first round (CPU 1511F-1 PN with two ET 200SP F-DI, F-I/O address `%I0.0`, and a standard CPU
1511-1 PN). Every route of the first round refused and applied again. New:

- `vci_conflict_resolve` take=remote of `FioTags`, mapped in SimaticML. Its package was committed and pushed to the
  Host's `--git-remote` (a local bare repository); another engineer moved `FioProbe` from `%M41.0` to `%I0.0` in a
  plain Git clone and pushed; `tag_set` moved `FioProbe` to `%M42.0` in the project. After `vci_fetch`, three
  `vci_package_capture` records, a `vci_package_compare` `divergentChange` row and the checkout of the fetched package
  into the workspace, `change_prepare` refused with `SafetyBoundary`, `details.logicalAddress` `%I0.0` and the first
  F-DI.
- `software_unit_create_from` of a master copy of the standard PLC's empty unit `FioUnit` refused into the F-PLC with
  `SafetyBoundary` naming the master copy and an F-DI; after `FioUnit` was deleted there, it applied on the standard
  PLC and `FioUnit` read back as a software scope.

Every tag table and tag of both PLCs was unchanged after each refusal. Not exercised: take=merged, unit master copies
holding tags into an F-PLC, F-output ranges and block or SCL imports with absolute operands.

### Master-copy tag collisions

Two owned scratch CPU 1511-1 PN V4.1 projects.

- The table `McTags` with `McProbe` (`%M60.0`) and `McSpare` (`%M61.0`) became a master copy. With both tags and the
  table deleted first, `library_instantiate` applied with checkpoint, compile and save, and `McTags` read back with
  both names. With `McTags` renamed `McTagsOld` (both tags stay), TIA created `McProbe(1)` and `McSpare(1)`:
  `change_apply` failed with `MutationVerificationFailed` naming both, and `change_status` showed `recovery_required`.
  In a fresh empty owned Portal `change_restore` retrieved the checkpoint to a new location, whose tag tables equalled
  those before the apply.
- The unit `CollUnit`, whose default tag table held `UnitTag` (`%M70.0`), became a master copy;
  `software_unit_create_from` with `onNameConflict=rename` copied it as `CollUnit2` into the same PLC. TIA created the
  copy's tag as `UnitTag(1)` beside `CollUnit`'s `UnitTag`, so `software_unit_create_from` had the same path: the
  apply failed after the compile with `MutationVerificationFailed` naming `UnitTag(1)`, and `change_restore` in the
  applying Portal restored tag tables equal to those before. Whether `MasterCopyMode.ThrowIfExists` renames a tag of
  another unit or of the PLC root is not exercised.

### Output tags: structured members and diagnostic writes

Each run on a fresh owned PLCSIM Advanced V8 CPU 1511 instance on Softbus of an owned scratch CPU 1511-1 PN V4.1.

- `qualify_plcsim_signals.py` repeated the first round and added `SigOutStruct`, a PLC tag of the PLC data type
  `SigOutUdt` (members `A`, `B`: Int) at `%Q24.0`, imported with `tag_table_import_xml` and written by the program.
  PLCSIM's tag list reports `SigOutStruct` (Struct), `SigOutStruct.A` and `SigOutStruct.B`, all in the area `Output`.
  `sim_write` of `"SigOutStruct".A`, and a `sim_write_signals` batch of `SigMarker` and `"SigOutStruct".A`, refused with
  `InvalidRequest` (`details.symbol` `"SigOutStruct".A`, `details.area` `Output`); `QB24..27` read the same before and
  after and `SigMarker` kept 4321.
- `qualify_plcsim_diagnostics.py` added a second `diagnostic_plan` case over the PLC tags `DiagIn` (`%I30.0`),
  `DiagMarker` (`%M30.0`) and `DiagOut` (`%Q30.0`; the program copies `DiagIn` to it), all captured Bool declarations.
  `diagnostic_plcsim_write` of `DiagOut`, and of `DiagMarker` with `DiagOut`, refused with `InvalidRequest` (the Host
  reports these with its generic message), every value unchanged; a write of `DiagIn` and `DiagMarker` returned
  `written` with two dispatches and matching readbacks, `DiagOut` followed `DiagIn`, and a second write reset both.

Array elements of an output tag are not exercised.

### Canonical hash, live

On `c204e4e4` the runner stopped before the component window: its store scanner recomputed the Host's journal chain
in the earlier .NET form, and Hosts since PR 184 write it in RFC 8785 ("Journal hash chain differs"; no TIA change was
made). The runners' scanners (`scan_store`, the component reservation scan and the watch verifiers) now accept either
form, as the Host does (`d57defa5`). The rerun passed: a fresh FB/UDT component installed in six children with
preservation, fresh reopen, first-checkpoint recovery and window completion. Each advance request (`verifyImport`,
`prepareGraphical`, `verifyInstallation`) carried the key `prüfName名前`, text with umlauts, CJK and an astral emoji
(sent as `\u` escapes) and the number literals `1e3` and `2.2250738585072012e-308` written raw on the wire. The Host
refuses a child whose RFC 8785 request hash differs from the worker's, so every dispatch shows the two agreed; a
separate Python RFC 8785 recomputation of the `verifyImport` request after the run (`1000`, `2.2250738585072014e-308`)
reproduced the Host's recorded `requestSha256`. The earlier-form acceptance of PR 186 is not exercised by this fresh
store.
