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

The first four entries were seeded on 2026-11-11 from findings this repository had
already documented. Their evidence links point at those records; they were not
re-run on that day.

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

## Submissions

| Date | Sent to | Entries | Reference / reply |
|---|---|---|---|
| | | | |
