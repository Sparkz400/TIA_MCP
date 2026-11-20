# Openness gap register

Things the TIA Portal Openness public API does not offer but this project needed.
The register is collected for Siemens: about once a month the open entries are sent
as feature requests, so every entry must stand on its own for a reader who has never
seen this repository.

Agents add entries as they hit a gap ([rule in AGENTS.md](../../AGENTS.md#record-openness-gaps)).
Maintainers decide what is sent and record it under [Submissions](#submissions).

## What belongs here

An entry is a capability that exists in the TIA Portal editor or data model and is
missing, read-only or incomplete in the public Openness API of an installed version.

It does not belong here when:

- the API exists and this project has not implemented or qualified it yet
  (that is [TODO.md](../TODO.md)),
- the behaviour is a defect in an existing API call (file it as a Siemens support
  case and link it from the entry that it blocks, if any),
- it is a licence, protection or safety restriction that Siemens documents as
  intentional, unless the entry argues for a narrower, safe variant.

## Entry rules

- **Prove the absence.** State how it was established: public metadata of the named
  assembly and version, a native call and its exact exception or result, or the
  Siemens documentation page. "I could not find it" is not evidence.
- **Name the version.** TIA Portal version and update, and the Openness assembly.
- **Say what the editor can do** that the API cannot, with the menu path.
- **Say what it would enable**, in terms Siemens can weigh: who needs it and what
  they have to do today.
- **Record the workaround**, including when it relies on non-public API, and what
  it costs.
- **No customer data.** No project names, customer names, paths or screenshots from
  customer projects. Reproduce on an owned fixture and describe that.
- Never delete an entry. Change its status and say why.

Status values: `open` (not yet sent), `sent` (in a submission), `acknowledged`
(Siemens replied), `resolved` (available in a named version), `withdrawn` (it was
our mistake; keep the reason).

## Template

```markdown
### G-NNN: <one-line capability>

- **Status:** open
- **Area:** <HMI Classic | HMI Unified | PLC | Hardware | Safety | Drives | Libraries | Project | Other>
- **Found:** <YYYY-MM-DD>, TIA Portal <version and update>, `<assembly>`
- **Missing:** <what cannot be read, set, created, exported or imported>
- **Editor equivalent:** <menu path in the TIA Portal UI>
- **Evidence:** <metadata check, native call and result, or documentation link; link the record in this repo>
- **Impact here:** <what is blocked or made manual>
- **Benefit if added:** <what it enables for automation users in general>
- **Workaround:** <what we do instead, and its cost or risk>
- **Suggested API:** <optional: the shape that would fit the existing object model>
```

## Entries

The first four entries were seeded on 2026-11-11 and G-005 to G-010 on 2026-11-20, from
findings this repository had already documented. Their evidence links point at those
records; they were not re-run on those days.

### G-001: Set the start screen of a Classic HMI

- **Status:** open
- **Area:** HMI Classic
- **Found:** 2026-10 (recorded in this register 2026-11-11), TIA Portal V21, `Siemens.Engineering.WinCC`
- **Missing:** A setter for the runtime setting "Start screen" of a Classic HMI
  (Basic, Comfort, RT Advanced). The native `HmiTarget` self-description exposes only
  read-only `Author` and `Name`; the screen export XML carries no start-screen
  setting; public metadata exposes no runtime-settings object or export for
  `HmiTarget`.
- **Editor equivalent:** HMI device > Runtime settings > General > Start screen.
- **Evidence:** [HMI remaining work](../plans/hmi-remaining-work.md) ("Fresh positive
  native run 01": every Classic HMI created through Openness reports one
  missing-start-screen compiler error); [HMI fixture preparation](hmi-fixture-preparation.md).
- **Impact here:** A Classic HMI created through Openness cannot be compiled until a
  person sets the start screen once in the editor. Every compilable Classic test
  fixture needs that manual step, and a headless workflow cannot create one.
- **Benefit if added:** Fully automated creation of a compilable Classic HMI station:
  generated machine variants, CI builds and test fixtures without an operator.
- **Workaround:** Configure the start screen by hand once and reuse that project or a
  device master copy. A workaround through non-public API is being evaluated for
  owned test fixtures only; it is unsupported and may break with any update.
- **Suggested API:** A `RuntimeSettings` object on the Classic `HmiTarget` with a
  `StartScreen` reference, as `HmiUnified.RuntimeSettings.HmiRuntimeSetting.StartScreen`
  already offers for Unified.

### G-002: Export and import a Unified screen

- **Status:** open
- **Area:** HMI Unified
- **Found:** 2026-10 (recorded in this register 2026-11-11), TIA Portal V21, `Siemens.Engineering.WinCC` (Unified)
- **Missing:** `HmiScreen.Export` / an import counterpart. Unified tag tables have a
  documented composition export; screens do not.
- **Editor equivalent:** None as XML; the editor can copy screens through libraries.
- **Evidence:** [HMI engineering, Preservation](../guides/hmi-engineering.md#preservation)
  ("Installed V21 metadata exposes no `HmiScreen.Export`").
- **Impact here:** A Unified screen cannot be captured as one document, compared
  before and after a change, or restored. The project walks the typed object model
  node by node instead, bounded to 45000 nodes.
- **Benefit if added:** Version control, review and exact restore of Unified screens,
  on a par with Classic screens and PLC blocks.
- **Workaround:** Typed object-model walk with a hash per node; slower and without a
  restore path.
- **Suggested API:** `HmiScreen.Export(FileInfo, ExportOptions)` and
  `HmiScreenComposition.Import(FileInfo, ImportOptions)`, matching the Classic
  `Screen` API.

### G-003: Read the alarms of a Classic HMI

- **Status:** open
- **Area:** HMI Classic
- **Found:** 2026-10 (recorded in this register 2026-11-11), TIA Portal V21, `Siemens.Engineering.WinCC`
- **Missing:** A public alarm composition on the Classic `HmiTarget` (discrete and
  analog alarms, alarm classes and groups).
- **Editor equivalent:** HMI device > HMI alarms.
- **Evidence:** [HMI engineering](../guides/hmi-engineering.md) ("The Classic
  HmiTarget surface has no public alarm composition"); maintainer decision of
  2026-11-07 in [TODO.md](../TODO.md).
- **Impact here:** Alarm configuration cannot be inventoried, compared across a
  change or included in a whole-HMI preservation check.
- **Benefit if added:** Alarm lists could be reviewed, documented and checked against
  the PLC program automatically.
- **Workaround:** None through the API; alarms stay outside every comparison.
- **Suggested API:** Alarm compositions on `HmiTarget` with `Export`/`Import`, as tag
  tables and screens have.

### G-004: Provision the transfer password of a Unified panel created through Openness

- **Status:** open
- **Area:** HMI Unified
- **Found:** 2026-10 (recorded in this register 2026-11-11), TIA Portal V21
- **Missing:** A supported way to complete the encrypted-transfer setup of a Unified
  panel that was created through Openness, so that it compiles.
- **Editor equivalent:** Unified device > Runtime settings > security / transfer
  password dialog.
- **Evidence:** [TODO.md](../TODO.md) ("panels created through Openness have no start
  screen or Unified transfer password"); [HMI fixture preparation](hmi-fixture-preparation.md).
- **Impact here:** A Unified panel created headless does not compile until an
  operator enters the password in the editor.
- **Benefit if added:** Automated creation of compilable Unified stations.
- **Workaround:** An operator configures the fixture once in the editor.
- **Note:** This is a security setting and the restriction may be intentional. The
  request is for a supported provisioning route (for example a credential reference
  or a secure prompt callback), not for reading or bypassing the password.

### G-005: Create a drive object in a drive control unit

- **Status:** open
- **Area:** Drives
- **Found:** 2026-11 (recorded in this register 2026-11-20), TIA Portal V21, `Siemens.Engineering.Startdrive` (`Siemens.Engineering.MC.Drives`)
- **Missing:** A member that adds or removes a drive object (DO) of a control unit.
  `DriveObjectContainer` exposes only `DriveObjects` and `Parent`; `DriveObjectComposition`
  is a read-only collection (`Count`, `Item`, `IndexOf`, `Contains`, `IsReadOnly`) with no
  `Create`, `Add` or `Insert`.
- **Editor equivalent:** Startdrive: add a drive object to a SINAMICS control unit
  (for example a further servo or vector axis).
- **Evidence:** Public member inventory of the V21 Openness assemblies, taken 2026-10-14
  ([api-coverage-inventory-20261014.json](../plans/expansion-plans/api-coverage-inventory-20261014.json),
  members of `Siemens.Engineering.MC.Drives.DriveObjectComposition` and `DriveObjectContainer`);
  the same finding is stated in [TODO.md](../TODO.md) (D10 `drives.engineering`: "Startdrive V21 has no
  member that creates drive objects"). Siemens' documented "creating a drive" example
  creates the drive device (`Devices.CreateWithItem`), not a drive object inside it.
- **Impact here:** A drive can be created as a device, but its drive objects cannot be
  added, so a multi-axis drive layout cannot be generated; only the drive objects the
  catalog type already contains can be configured.
- **Benefit if added:** Generating complete multi-axis drive layouts for machine variants.
- **Workaround:** Choose a catalog type that already contains the needed drive objects,
  or add the drive object by hand in the editor.
- **Suggested API:** `DriveObjectComposition.Create(DriveObjectType)` and `Delete()`,
  with a `CanCreate` check as `Telegram.CanChangeSize` has.

### G-006: Set the PKW parameter channel of a drive telegram

- **Status:** open
- **Area:** Drives
- **Found:** 2026-11-07, TIA Portal V21, `Siemens.Engineering.Startdrive` (`Siemens.Engineering.MC.Drives.Telegram`)
- **Missing:** A setter or method for the PKW (parameter channel) part of a telegram.
  `Telegram.PKW` is a read-only property; `ChangeSize` resizes the process-data
  channels, and the PKW follows the telegram number.
- **Editor equivalent:** Startdrive: drive > Communication > telegram configuration, PKW
  channel (acyclic parameter channel).
- **Evidence:** [Drive telegram resize record](../verification/drives-telegram-resize-20261107.md)
  ("PKW has no Openness setter (`Telegram.PKW` is read-only)"); the member inventory
  lists `P:Siemens.Engineering.MC.Drives.Telegram.PKW` and no PKW method
  ([inventory](../plans/expansion-plans/api-coverage-inventory-20261014.json)).
- **Impact here:** A telegram layout that needs a PKW channel the telegram number does
  not give cannot be produced; the telegram change tool can only select another number.
- **Benefit if added:** Complete telegram layouts (cyclic data plus parameter channel)
  from automation, so a drive's I/O range matches the PLC program without an editor step.
- **Workaround:** Pick the telegram number that carries the wanted PKW; otherwise a
  person sets it in the editor.
- **Suggested API:** `Telegram.PKW` with a setter, or `Telegram.ChangePkw(bool)` guarded
  by a `CanChangePkw` check.

### G-007: Project motor and encoder data on a SINAMICS G120C

- **Status:** open
- **Area:** Drives
- **Found:** 2026-11-08, TIA Portal V21, `Siemens.Engineering.Startdrive` (`Siemens.Engineering.MC.Drives.DFI.HardwareProjection`)
- **Missing:** Motor and encoder projection for a SINAMICS G120C PN (6SL3210-1KE21-3AF1,
  firmware 4.7). Every motor and encoder read of its `HardwareProjection`
  (`GetCurrentMotorConfiguration`, `GetCurrentEncoderConfiguration`) answers
  `EngineeringTargetInvocationException`, so no entry can be read or projected.
- **Editor equivalent:** Startdrive: drive > Commissioning > motor data.
- **Evidence:** [Drive motor projection record](../verification/drive-motor-configure-20261108.md)
  (read-only probe: the G120C PN answers every motor and encoder read with that
  exception, while a G220 reads motor data sets 0 and 1); the same family also reports
  "Drive object number could not be retrieved" ([drives index record](../verification/drives-index-20261101.md)).
- **Impact here:** The motor nameplate of a G120C cannot be set or verified through
  Openness; the motor projection tool works on a G220 only.
- **Benefit if added:** The same offline motor configuration for the common compact
  G120 family as for a G220.
- **Workaround:** Enter the motor data in the editor.
- **Note:** The cause is not established. The projection may not support a G120C by
  design, or the call may be a defect. The request is for a support statement per
  family, or a typed "not supported" answer instead of a generic engineering exception.
  On a G220 `SetMotorType` and `SetEncoder` answer "use PlugNew to create the motor
  first"; that route is not yet tried and is tracked in [TODO.md](../TODO.md), not here.

### G-008: Load a safety program into PLCSIM Advanced through the generated memory card

- **Status:** open
- **Area:** Safety
- **Found:** 2026-11-01, TIA Portal V21, `Siemens.Engineering` (`Siemens.Engineering.Download.DownloadProvider`), PLCSIM Advanced V8
- **Missing:** A route that loads an F-CPU's safety program into a simulated CPU.
  `DownloadProvider.Download(DirectoryInfo, callback)`, which writes the PLCSIM virtual
  memory card, refuses a CPU 1511F-1 PN (`6ES7 511-1FL03-0AB0/V4.1`) with
  "An error has occured during download: 'Load safety program to device'"
  (`EngineeringTargetInvocationException`). No card is written. The online route to a
  PLCSIM instance needs a card from the same PLC, so it cannot start either.
- **Editor equivalent:** Download to device with PLCSIM, including the safety program,
  from the TIA Portal editor.
- **Evidence:** [Changed F-logic in PLCSIM Advanced V8](../verification/safety-logic-plcsim-20261101.md#boundary),
  native runs `local.safety.logic_plcsim_boundary.20261101` and
  `local.safety.logic_online_route.20261101` on an owned scratch with an owned
  CPU1511F simulation instance.
- **Impact here:** Changed F-logic compiles but cannot be run in simulation, so F-logic
  behaviour, F-I/O injection and passivation cannot be tested without hardware.
- **Benefit if added:** Test of changed safety logic in simulation before it reaches a
  machine, in CI.
- **Workaround:** None through Openness; an operator downloads in the editor.
- **Note:** The online route exposes `Siemens.Engineering.Safety.Download.Configurations.SafetyProgram`
  (`ConsistentDownload`); this project deliberately does not answer safety
  confirmations. The restriction on the offline card may be intentional. The request is
  for a documented, safe variant for simulated CPUs (for example the same confirmation
  answered by the caller), not for bypassing safety checks.

### G-009: Export or cross-reference the generated F-runtime-group blocks of an F-CPU

- **Status:** open
- **Area:** Safety
- **Found:** 2026-10-08 (first seen), confirmed 2026-11-12, TIA Portal V21 Update 2, `Siemens.Engineering` (`PlcBlock.Export`, cross-references)
- **Missing:** A supported way to read the generated F-runtime-group organization block
  `FOB_RTG1` (and the blocks in its system block groups). Its export is refused (TIA
  reports a know-how protected SCL OB), and its cross-references name no references:
  one source, none of its calls.
- **Editor equivalent:** The editor shows the generated blocks and their cross-references.
- **Evidence:** [Offline safety verification](../verification/safety-verification.md)
  ("Siemens' prohibition on exporting generated `FOB_RTG1`"); [Engineering workflows,
  dependency scan](../guides/engineering-workflows.md); native record
  `local.safety.drive-tags-bool.20261112` and the decision in [TODO.md](../TODO.md)
  (batch follow-ups of 11 November).
- **Impact here:** The dependency scan that guards deleting a tag, constant, type or
  block cannot prove the generated blocks free of the name. So on a PLC with a safety
  program every such delete is refused with `DependencyCoverageIncomplete`.
- **Benefit if added:** Safe automated deletes and renames on F-CPUs, and complete
  impact analysis for safety projects.
- **Workaround:** The person deletes in the editor, which shows the references itself.
- **Note:** The protection of generated safety blocks is intentional. The request is a
  read-only way to query which symbols those blocks use (a reliable cross-reference or
  a name-usage query), not for exporting protected code.
- **Suggested API:** A cross-reference or name-usage query that includes generated
  F-system blocks without exposing their code.

### G-010: Force values on a PLC or a simulated PLC

- **Status:** open
- **Area:** PLC
- **Found:** 2026-10-30, TIA Portal V21 Openness, PLCSIM Advanced V8 Runtime API, S7-1500 Web API, S7-1500 OPC UA server
- **Missing:** Any member that starts or stops forcing of a PLC address. Writes are
  assignments that the program scan may overwrite; a force holds the value.
  - TIA Portal Openness V21: the 15 XML files of `PublicAPI/V21/net48` hold no member
    named `Force*`, `Unforce*` or `Monitor*`. `Siemens.Engineering.SW.WatchAndForceTables`
    exposes force-table export/import and row properties (`ForceValue`, `ForceIntention`)
    as stored engineering data only; `OnlineProvider` offers `GoOnline`/`GoOffline`.
  - PLCSIM Advanced V8: `SimulationRuntimeApi.h` has no force member; the only match is
    the LED type `SRLT_FORCE`. `Write*` assigns once.
  - S7-1500 Web API (methods as used here: `PlcProgram.Read/Write/Browse` and others): no
    force method or permission.
  - OPC UA server: no force service; `Write` is an assignment.
- **Editor equivalent:** Online > Force table (start and stop forcing).
- **Evidence:** [Live monitoring and forcing plan](../plans/live-monitoring-forcing-plan.md)
  (search record of 2026-10-30 and conclusion). The Web API row rests on the method
  set this repository uses, not on a Siemens manual (none is installed); check it
  against the Siemens documentation before the entry is sent.
- **Impact here:** Forcing stays unimplemented ("an assignment is not forcing"). Tests
  and commissioning scripts cannot hold an input or output value against the program.
- **Benefit if added:** Automated test benches, commissioning checks and simulation
  scenarios that hold signals, including audit of forced values.
- **Workaround:** An operator uses the force table in the editor. Repeated writes or
  table imports are not forcing and are not offered as such.
- **Note:** Forcing changes machine behaviour; the request is for a route with the
  protection and access checks the editor has (for example a dedicated access right),
  not for an unguarded one.
- **Suggested API:** A force job on the online PLC (activate and deactivate a
  `PlcForceTable` online) and `Force`/`Unforce` in the PLCSIM Runtime API.

## Submissions

| Date | Sent to | Entries | Reference / reply |
|---|---|---|---|
| | | | |
