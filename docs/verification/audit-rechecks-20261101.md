# Native rechecks of three audit fixes: runs of 1 November 2026

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
behavior is recorded in the [library guide](../guides/library-management.md) and the [TODO](../TODO.md); it is not
fixed here.
