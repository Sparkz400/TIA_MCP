# Typed LAD/FBD remaining qualification

Historical record. See [current status](../STATUS.md) and [remaining TODO](../TODO.md) for the latest checkpoint and work order.

Update, 9 October 2026: all 28 standard engineering cells that remained at the
checkpoint below passed in continuation attempt 03 (`artifacts/continuation-20261009/typed-standard-engineering-03/batch.json`).
That covers the 12 remaining storage-creation cells, four numeric FC/FB cells and
12 existing-global IEC cells. The independent audit passed 489 XML comparisons,
with all 84 exact owned TIA exits verified. These are engineering-only results;
runtime and broader scoped-route work remain separate. See the updated
[qualification ledger](../verification/typed-lad-fbd-qualification.md). The table and commands
below preserve the earlier checkpoint and its fixture definitions.

Checkpoint: 8 October 2026. Work stopped at the user's requested checkpoint.
This is a continuation list, not a claim that LAD-010–070 is fully qualified.
Native import/compilation, complete XML preservation, fresh-session persistence,
checkpoint recovery and runtime behavior remain separate gates.

## Standard instance creation: 16 of 28 engineering cells passed

Each passed cell below executed actual MCP prepare/apply/status/restore,
native import and compilation, complete expected readback and peer preservation,
save/reopen, and recovered-project reopen/comparison. These were
`--engineering-only` runs: **none is runtime evidence**.

| Case | Result artifact |
| --- | --- |
| typed-createmulti-fb-multi-lad | `artifacts/tqi04/typed-createmulti-fb-multi-lad/result.json` |
| typed-createglobal-fb-multi-lad | `artifacts/tqi03/typed-createglobal-fb-multi-lad/result.json` |
| typed-createmulti-ton-lad | `artifacts/tqi04/typed-createmulti-ton-lad/result.json` |
| typed-createglobal-ton-lad | `artifacts/tqi03/typed-createglobal-ton-lad/result.json` |
| typed-createmulti-tof-lad | `artifacts/tqi05/typed-createmulti-tof-lad/result.json` |
| typed-createglobal-tof-lad | `artifacts/tqi05/typed-createglobal-tof-lad/result.json` |
| typed-createmulti-tp-lad | `artifacts/tqi05/typed-createmulti-tp-lad/result.json` |
| typed-createglobal-tp-lad | `artifacts/tqi05/typed-createglobal-tp-lad/result.json` |
| typed-createmulti-ctu-lad | `artifacts/tqi05/typed-createmulti-ctu-lad/result.json` |
| typed-createglobal-ctu-lad | `artifacts/tqi05/typed-createglobal-ctu-lad/result.json` |
| typed-createmulti-ctd-lad | `artifacts/tqi05/typed-createmulti-ctd-lad/result.json` |
| typed-createglobal-ctd-lad | `artifacts/tqi05/typed-createglobal-ctd-lad/result.json` |
| typed-createmulti-ctud-lad | `artifacts/tqi05/typed-createmulti-ctud-lad/result.json` |
| typed-createglobal-ctud-lad | `artifacts/tqi05/typed-createglobal-ctud-lad/result.json` |
| typed-createmulti-ton-fbd | `artifacts/tqi05/typed-createmulti-ton-fbd/result.json` |
| typed-createglobal-ton-fbd | `artifacts/tqi05/typed-createglobal-ton-fbd/result.json` |

The last cell completed recovery and `tia_detach` at
`2026-10-08T13:51:23.978331+00:00`. The eager batch had entered only baseline
`project_retrieve` for `typed-createmulti-tof-fbd` when stopped. That cell made
no `change_prepare` or `change_apply` call. Its raw report retains the unfinished
retrieval; `artifacts/tqi05/checkpoint-stop.json` records the interruption and
verified exit of its exact owned TIA process (PID 43916). No typed mutation was
interrupted. No further cells were started.

The 12 remaining storage IDs in `extended-cases.v1.json` are:

```text
typed-createmulti-fb-multi-fbd
typed-createglobal-fb-multi-fbd
typed-createmulti-tof-fbd
typed-createglobal-tof-fbd
typed-createmulti-tp-fbd
typed-createglobal-tp-fbd
typed-createmulti-ctu-fbd
typed-createglobal-ctu-fbd
typed-createmulti-ctd-fbd
typed-createglobal-ctd-fbd
typed-createmulti-ctud-fbd
typed-createglobal-ctud-fbd
```

The first two require a retry of an independently diagnosed fixture error:
the earlier in-memory request used uppercase `TRUE`, while the typed constant
contract requires lowercase `true`. Both were rejected during prepare with
`InvalidTypedConstant`; native reference XML correctly retains `TRUE`.
The authoring scripts and cases now use the correct typed lexeme, and a pure
case preflight test prevents recurrence. Failed evidence remains in
`artifacts/tqi04/typed-createmulti-fb-multi-fbd/result.json` and
`artifacts/tqi05/typed-createglobal-fb-multi-fbd/result.json`.

Earlier prerequisite failures remain in `artifacts/tqi01` (sandbox worker startup)
and `artifacts/tqi02` (internal inventory limit, fixed in worker 05 onward).
Do not count these as unsupported instruction results or successful qualification.

## Numeric calls and existing global IEC instances

All four `numeric-cases.v1.json` cells still require actual typed qualification:

```text
typed-numeric-fc-lad
typed-numeric-fb-lad
typed-numeric-fc-fbd
typed-numeric-fb-fbd
```

Independent native helpers and wrappers already imported/compiled and are
hash-bound in the corpus. The typed FC case covers mandatory Input, Output,
InOut and scalar Return pins in native order. The FB behavioral oracle covers
state history, enable/reset, InOut mutation, and two distinct caller instances.
Pure interface/harness preflight is not execution of either oracle.

All 12 `existing-global-cases.v1.json` cells still require actual typed qualification:

```text
typed-existingglobal-ton-lad
typed-existingglobal-tof-lad
typed-existingglobal-tp-lad
typed-existingglobal-ctu-lad
typed-existingglobal-ctd-lad
typed-existingglobal-ctud-lad
typed-existingglobal-ton-fbd
typed-existingglobal-tof-fbd
typed-existingglobal-tp-fbd
typed-existingglobal-ctu-fbd
typed-existingglobal-ctd-fbd
typed-existingglobal-ctud-fbd
```

## Negative actual-MCP checks

`artifacts/tqn01/result.json` contains five passed rejection cells, each followed
by complete unchanged native snapshot verification: `wrong-type`,
`read-only-output`, `missing-argument`, `wrong-fb-instance`, and
`ambiguous-db-owner`.

Rerun the complete negative script after its fixture/expected-code corrections.
The remaining cells are `language-mismatch` (a valid FBD graph targeting LAD;
expected coordinator diagnostic `LanguageConversionNotSupported`),
`implicit-shared-instance`, and `stale-declaration`. The stale test deliberately
changes the numeric callee through the normal prepared workflow, verifies that
the earlier typed preparation rejects application, and restores/completely
compares the original checkpoint. It has not executed yet.

The optional ninth cell, `planned-caller-instance-collision`, prepares a new FB
caller and global DB with the same name and expects `DeclarationCollision`.
Use `--include-planned-collision` only with a fresh binary containing that
coordinator guard; the immutable worker 07 predates it.

## Exact continuation commands

Run from the repository root, using an ordinary authorized Windows token
(the default restricted sandbox cannot launch the installed Openness worker).
Keep at most one portal per runner and retain every case report. These commands
do not run automatically and must use new output directories.

```powershell
$typedHost = 'artifacts/typed-host-build-01/TiaMcp.Host.exe'
$typedWorker = 'artifacts/typed-native-build-07/TiaMcp.Openness.V21.exe'
$typedBaseline = 'artifacts/qeae5ce/changes/change-22d6e159b46e46178758c81c6dcd5f2d/before.zap21'
$typedBaselineSha = 'f80e2b11e20842bb2d495139f7d6fbd9b51c16ca877fff8ef37dd323ea923329'
$typedCommon = @('--host', $typedHost, '--worker', $typedWorker,
  '--engineering-only', '--baseline-archive', $typedBaseline,
  '--baseline-sha256', $typedBaselineSha)
$typedRemaining = @(
  'typed-createmulti-fb-multi-fbd', 'typed-createglobal-fb-multi-fbd',
  'typed-createmulti-tof-fbd', 'typed-createglobal-tof-fbd',
  'typed-createmulti-tp-fbd', 'typed-createglobal-tp-fbd',
  'typed-createmulti-ctu-fbd', 'typed-createglobal-ctu-fbd',
  'typed-createmulti-ctd-fbd', 'typed-createglobal-ctd-fbd',
  'typed-createmulti-ctud-fbd', 'typed-createglobal-ctud-fbd')
$typedSelectors = @()
foreach ($typedCase in $typedRemaining) { $typedSelectors += @('--case', $typedCase) }
python scripts/qualify_typed_graphical.py @typedCommon --cases fixtures/typed-graphical/extended-cases.v1.json --output artifacts/tqi06 @typedSelectors
python scripts/qualify_typed_graphical.py @typedCommon --cases fixtures/typed-graphical/numeric-cases.v1.json --output artifacts/tqnum01
python scripts/qualify_typed_graphical.py @typedCommon --cases fixtures/typed-graphical/existing-global-cases.v1.json --output artifacts/tqeg01
python scripts/qualify_negative_typed_graphical.py --host $typedHost --worker $typedWorker --baseline-archive $typedBaseline --baseline-sha256 $typedBaselineSha --output artifacts/tqn02
# For all nine negatives, replace $typedWorker with the fresh guarded binary,
# choose a new output directory, and add --include-planned-collision.
```

The baseline is a SHA-bound archive from the first independent native import
checkpoint in `artifacts/tq05/typed-boolean-lad/result.json`. Each case retrieves
a fresh owned project. It contains the standard scratch CPU and four helper
blocks. It cannot be used as runtime evidence or substituted for route fixtures.

## Further acceptance gates

- Exercise an actual typed `createMulti` against a caller that already has a
  direct dependent InstanceDB with retained start values. The independent
  before/after pair in `native/instance-effects` proves native shape and retained
  value 42; pure mapping tests pass. The current 28 storage cases do not create
  such an existing caller DB before the typed change. Add an explicit native
  dependent-effect case and compare the prepared expected dependency artifact
  and all original starts/attributes after apply, reopen, and restore.
- Complete the parent runtime matrix and the independently authored CTU upper
  limit driver cases. No storage engineering result above upgrades runtime
  qualification. Retry prerequisite failures only after retaining their exact
  evidence and confirming owned-instance cleanup.
- Finish separately recorded standard new-block/edit/insertion and software-unit/
  namespace-route gates. Native insertion at common block-title boundaries is
  currently rejected explicitly because TIA renumbers unrelated object IDs;
  do not weaken the preservation comparer to claim success.
- Run final package/source regression and deployed-profile hash validation after
  the final shared changes. Remote transport, software-unit and alternate namespace
  routes require their own result artifacts; standard PLC-folder results do not
  qualify those targets.

The separate [native route continuation record](typed-lad-fbd-remaining-native.md)
lists four passed scoped replacements (`artifacts/tqr02`) and eight passed scoped
FC/FB creations (`artifacts/tqr03`), each with all four engineering gates and no
runtime claim. It contains the exact pending advanced unit/namespace/reference
case IDs and capture commands; do not substitute standard-scope results for them.

Related implementation and evidence are described in
[typed-lad-fbd-builders.md](../guides/typed-lad-fbd-builders.md) and the
[workstream plan](../plans/typed-lad-fbd-builders-plan.md).
