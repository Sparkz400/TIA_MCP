## Do I need TIA Portal installed?

Only for native work: opening projects, fresh exports, compiling, applying changes and simulation. Document workflows (OPC UA NodeSets, AutomationML/CAEX, GRAPH XML, translation workbooks, saved project reviews, VCI Git) and the development checks run without Siemens software. See [[Getting Started]].

## Which versions are supported?

TIA Portal **V21** with Openness and PLCSIM Advanced **V8**. Older projects (V14–V20) can be upgraded to V21 on an owned copy with `project_upgrade`; the source is never opened, V13 and older are refused, and projects with device types V21 no longer supports fail the upgrade ([project upgrades](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/engineering-workflows.md#project-copies-upgrades-read-only-open-and-plc-compare)).

TIA Portal **V19** has a separate read-only adapter: a Host started with `--tia-version 19` opens `.ap19` projects or attaches to a running V19 Portal and reads inventory, blocks, data types and tag tables, but never changes them. It is built from source and not part of the release bundles ([V19 adapter](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/tia-v19-adapter.md)). Other versions have no adapter.

Installed SDKs alone do not extend these support claims. The
[workstation inventory](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/verification/release-readiness-20261103.md)
records the available CFC/DCC prerequisites and the native qualification still needed.

## Can I use it on a production PLC or plant?

No. Use it only with offline projects, copies, scratch projects and simulators you own. The simulation workflow operates on adapter-owned PLCSIM instances and does not expose physical PLC download, force or CPU control.

## Is it production-ready?

No, it is pre-1.0. Offline tooling is validated in CI; live TIA Portal and PLCSIM runtime qualification is incomplete. [Current status](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/STATUS.md) records what is verified and with which evidence. Only the latest `main` revision and the newest release are supported ([security policy](https://github.com/Sparkz400/TIA_MCP/blob/main/SECURITY.md#supported-versions)); changes are listed in the [changelog](https://github.com/Sparkz400/TIA_MCP/blob/main/CHANGELOG.md).

## How do I find out what a feature supports?

Ask the server: `feature_catalog` lists implementation scope, `target_capabilities` reports what the selected target supports. The [tool reference](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/tools.md) lists every tool with its support class. The [engineering coverage](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/engineering-coverage.md) reference covers native changes.

Typed LAD/FBD support is tied to exact CPU, firmware and route profiles. The second CPU and wave 9 route additions have engineering evidence, with their runtime limits recorded in the [builder guide](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/typed-lad-fbd-builders.md). HMI admission is similarly bounded by the [qualified kinds and profiles](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/hmi-engineering.md).

## What if a change fails halfway?

Changes are not transactions across Siemens calls. Each applied change has a checkpoint and journal for recovery; see the [engineering workflow](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/engineering-workflows.md).

For bounded Classic screen imports, a verified restoration can keep the same
project open. A completed edit with a lost response also has an evidence-based
lock reconciliation path. Unverified recovery still holds the lock. See
[screen recovery and long-running imports](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/hmi-engineering.md#screen-failure-recovery-without-closing-tia).

## Why does every change take minutes on a large project?

Each applied change fingerprints the project, saves it, writes a full recovery archive and compiles before and after the edit. The checks are never skipped. A Host started with `--enable-fingerprint-reuse` reuses the project-wide fingerprint read while TIA reports the project in the same saved state; that is off by default. Several other kinds (creating, renaming and deleting tags, constants, tag tables, blocks and data types) take `items` the same way. To rename or readdress many PLC tags, send them as one `tag_set` change with `items`, so the sequence runs once for all of them; see [many tag edits in one change](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/engineering-workflows.md#many-tag-edits-in-one-change).

## Why is Classic graphic-list export blocked?

A native reference-conversion failure was observed during export. Until that route
has a qualified remedy, the connector refuses source exports of Classic graphic
lists and changes whose preservation requires them. See the
[containment scope, offline checks and disposable-copy reproductions](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/hmi-engineering.md#temporary-classic-graphic-list-export-containment).
Changing export defaults or running without the graphical editor did not resolve
the graphic-list failure. The separate screen-only route has local copy evidence;
its exact limits are documented in the guide.

The separate `hmi_classic_screen_edit` workflow compares every regular screen of one Classic RT Advanced HMI without exporting graphic lists. It retains checkpoint, compile and exact readback checks, but explicitly excludes shared-resource, other-HMI, PLC and hardware content. This is bounded screen preservation, with profile-wide qualification still unverified. See the [screen editing contract](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/hmi-engineering.md#bounded-classic-screen-editing).

## The server cannot find TIA Portal or Openness

Run `.\scripts\Probe-Environment.ps1` and follow [environment diagnostics](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/environment-diagnostics.md) and [Openness registration](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/openness-application-registration.md). Your Windows user needs the `Siemens TIA Openness` group, and `PlcSimUsers` for simulation.

## Why does TIA repeatedly ask to allow access?

Siemens saves approval for an executable's path, modification time and hash. Rebuilding or moving it changes that identity; separate helpers also need their own registration. Engineering-bundle setup registers the final worker, and approved updates refresh the entry for that path. `Start-TiaMcp.ps1 -CheckOpennessRegistration` identifies missing or stale registration without starting TIA. See [registration diagnostics and setup](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/openness-application-registration.md#why-tia-still-asks). HMI support limits remain separate from authorization.

## Is this a Siemens product?

No. TIA MCP is independent and not affiliated with, endorsed or supported by Siemens AG. Siemens, TIA Portal, SIMATIC, STEP 7, WinCC and PLCSIM are trademarks of Siemens AG.

## What is the license?

[Apache License 2.0](https://github.com/Sparkz400/TIA_MCP/blob/main/LICENSE); keep the attribution in [NOTICE](https://github.com/Sparkz400/TIA_MCP/blob/main/NOTICE). Siemens products and SDK assemblies stay under their own terms.

Supplied vendor libraries keep their own notices and prerequisites. The
[PAL/DMC review](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/verification/vendor-intake-review-20261025.md)
records the reviewed packages and exclusions; they are not bundled with TIA MCP by that review.

## Has the Explorer been tested in a desktop MCP Apps host?

Yes. On 1 November 2026 VS Code 1.144.0 passed the full roundtrip with the production Host
and a fresh native capture: an exact selection answered by `project_model_details`, refusal
after `tia_detach`, and the artifact fallback for a client without MCP Apps. SSH/VM transport
is still open. See the
[desktop record and limits](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/verification/explorer-desktop-20261101.md).

## Where do I ask something that is not answered here?

[Discussions → Q&A](https://github.com/Sparkz400/TIA_MCP/discussions/categories/q-a).
