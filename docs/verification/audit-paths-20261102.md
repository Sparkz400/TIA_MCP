# Native rechecks of the 31 October audit paths: runs of 2 November 2026

The [31 October audit](../history/status-log.md#31-october-2026) changed five worker paths that no native run had reached
since: the unresolved-change guard of `change_prepare` and `change_apply` and the preview's `isModified`
(`f8f1bb94`, `272ca1c9`), generated-source reads under exclusive access with `projectModifiedBefore`/`After`
(`f8f1bb94`), the translation fixture and reopen stages that keep scratch status only for the project that held it
(`1eea6b0f`), and `compile=true` for `block_rename`, `block_delete`, `to_delete` and `hardware_delete` (`f8f1bb94`;
`hardware_attributes` exempt since `272ca1c9`). This round runs the runners that reach them, with steps added where a
changed branch was not reached, and the read evidence of `vci_workspace_inspect`'s `contentSha256`. Every run used owned
scratch projects that the runner created through the MCP Host, one run at a time under the shared native lock, from
frozen Debug builds (`git archive`; Host and Openness worker):

| Tree | Commit | Runs |
| --- | --- | --- |
| `artifacts/ap1` | `bc162ad2` | the runner steps of this round |
| `artifacts/ap2` | `1f9e4859` | `ap1` plus the RFC 8785 fix of the translation admission scripts (below) |
| `artifacts/ap3` | `22880f21` | `ap2` plus the X11 runner fix (below) |

| Evidence | Runner | Result |
| --- | --- | --- |
| `local.technology.to-delete-compile.20261102` | `scripts/qualify_first_motion.py` (`ap1`) | 14/14 steps (`fm_867b1e6d`) |
| `local.hardware_changes.delete-rule.20261102` | `scripts/qualify_hardware_changes.py --slice delete-rule` (`ap1`) | 14/14 steps (`hw_49bbccb6`) |
| `local.read_contracts.20261102` | `scripts/qualify_read_contracts.py` (`ap1`) | 164/164 steps (`rc_8b7611cf`) |

## Delete and rename need compile

Each kind was prepared, then `change_apply` with `compile=false` refused with `InvalidRequest` ("This engineering
operation requires compile=true."); `change_status` stayed `prepared` with no stage, no `before.zap21` was written and
the state the runner compares was unchanged:

- `to_delete` of the virtual `TO_PositioningAxis` in the project `change_restore` had just recovered (technology
  objects and program inventory unchanged).
- `hardware_delete` of a DI 16x24VDC HF at slot 2 of the scratch CPU's rack; the same prepared change then applied with
  checkpoint, device compile and save and removed exactly the module, its submodule, their inventory rows and its
  system constant.

## Generated-source reads and isModified

In the read-contract run `block_read` of `RcCaller` and `type_read` of `RcUdt` with `format: generatedSource` returned
one hashed file each, `removedExternalSources: []` and `projectModifiedBefore`/`projectModifiedAfter` `false` on the
saved scratch. Every preview of the run carries `isModified` (`false`). The offline-project refusal and the exclusive
access are not observable here: the scratch is offline and no other client contends.

## vci_workspace_inspect contentSha256

`local.read_contracts.20261028` saw no VCI workspace. The rerun creates `RcVci` (root outside the project and the
artifact store; V21 offered `SCL`, `SimaticML` and `SimaticSD` for an SCL block, the run maps in `SimaticML`) and maps
`RcCaller` and `RcFb` by export. `vci_workspace_inspect` named both mappings with a 64-hex `contentSha256` each, the
two differ, a second read returned the same hashes, `maxItems: 2` truncated the result, and after `block_attributes_set`
of `RcFb`'s `HeaderFamily` only `RcFb`'s hash changed. The hash is not recomputed independently: no other tool exports a
block without document info.

## Translation

### Finding: the admission scripts used the earlier hash form

Since PR 184 the Host hashes the admission key and its row states in RFC 8785. The first restricted same-instance
packet on `bc162ad2` (run `apsi`) stopped before its first case, `Admission key digest differs`: the runner compared
the Host's key hash with the earlier form. `build_translation_admission.py` wrote its proofs in that form too, which
the Host loader would have refused (`TranslationAdmissionIncomplete`), and `verify_translation_admission.py`
recomputed the row and content hashes in it. `1f9e4859` accepts either form in the runner, keeps the packet's own key
hash in the proofs and verifies in the form the forward key was hashed in. The restricted pipeline below then ran
end to end on `ap2`, so admission packets from current Hosts can be built again.

| Evidence | Run | Result |
| --- | --- | --- |
| `local.translation.restricted.qualification.20261102` | `bpsi`: `qualify_translation_import.py --same-instance --restricted-lifecycle` | passed; independent `verify_translation_admission.py` passed (8 imports, 10 restorations) |
| `local.translation.restricted.native.20261102` | `bppd`: `qualify_translation_native.py` with the three interruptions, on the provisional registry | passed; `--finalize` built the 25-proof entry |
| `local.translation.restricted.finalized.20261102` | `bpfc`: `qualify_translation_native.py --finalized-capture` on the final registry | passed |
| `local.translation.restricted.empty.20261102` | `bpem`: `qualify_translation_import.py --restricted-lifecycle --empty-restricted` | passed |

`bpsi` repeated `local.translation.restricted.qualification.20261031`: the Host kept exactly 5, 1 and 0 of 6 native
rows for the three categories, and on the block-comment scope the seven forward cases (the save, reopen and restore
replies `verifiedApplied`, the others `unchanged`), three reviewed reverse edits and the final unattempted plan all
carried the one key `8e2b8644…`, now hashed in RFC 8785. In `bppd` each interruption reconciled `unchanged` with the
replay refused (`RecoveryRequired`), and the clean lifecycle imported once, recovered and reconciled.

### Scratch status across the reopen

The 31 October fix lets the fixture and reopen stages keep scratch status only for the project that held it. A
`change_prepare` of `hardware_add` with `configureScratchSecurity` passes only for the worker's current scratch
(`ScratchIsolationRequired` otherwise) and writes nothing, so the runners use it as a probe:

- `bpem` (and `bpa1`): the owned fixture passed the probe after the fixture stage and after the no-op import's reopen;
  it kept its status.
- `bppd`: the admitted project, which the default-mode run opens with `project_open`, was refused with
  `ScratchIsolationRequired` right after the import's reopen; the reopen did not grant it. Before the fix the reopen
  made every reopened project the scratch.

### The finalized restricted entry

`bpfc` ran in default mode on the final registry, which has no deployment binding. Both loaders accepted it: an
uninstalled profile was refused (`TranslationImportProfileUnqualified`), and `translation_native_capture` of the
admitted project passed with the installed category scope, 5 block-comment rows. The capture shows the Main OB1
comment at the replacement that `bppd` imported, so a reviewed reverse edit was refused at `translation_native_prepare`
with `TranslationAdmissionMismatch`. This is inherent: `--finalize` needs the default-mode packet's clean lifecycle,
which imports the entry's one admitted edit, so a final entry's baseline is spent before it is installed. An import
through a final entry needs a scratch whose baseline a new packet admits.

### Empty restricted import

In `bpem` the Host-filtered `AlarmClassDisplayNameCategory` capture kept no row and listed all 6 native rows as
excluded. Its no-op plan has no admission key (`TranslationAdmissionScopeUnqualified`, only the block-comment scope
has a contract), and its header-only workbook imported natively with state `Info` (the block-comment imports return
`Warning` for Main's read-only rows), then compiled, saved, reopened, recovered to a new directory and reconciled.
The first attempt on `ap1` (run `apem`) read the prepare's omitted `admissionKey` and stopped before that import.

## Change prepare and apply after a failed change

`scripts/qualify_plc_object_mutations.py` now prepares `block_rename` and `block_delete`, refuses each with
`compile=false` before applying it, checks `isModified` in every preview, and after its closing partial failure (a tag
readdressed beyond the bit memory, `CompileFailed`, `recovery_required`) asks for another change and applies one it
prepared before the failure. The first run on `ap1` (`x11_38dc79d7`, 372 of 373 steps) is not evidence, but it
observed every new step: both `compile=false` refusals (`InvalidRequest`), and `change_prepare` refused with
`RecoveryRequired` naming the failed change. The change prepared before the failure was refused before its checkpoint
with `FingerprintUnavailable` (the failed compile leaves `X11Reader` inconsistent), which the runner did not accept, so
the run stopped there. The apply-side guard is therefore not reached sequentially: a failed apply changes the project,
so the fingerprint check refuses an earlier preview first; the guard covers a change still checkpointing, applying or
restoring. `22880f21` accepts that refusal; the rerun on `ap4` (`bba756d3`) is queued.

## Cross-worker adoption

No adoption route is built: a fresh worker would have to grant watch ownership of a fixture it did not create, which
the 31 October rule forbids, and no open item needs it. `qualify_translation_import.py --resume-root` now expects the
refusal. On `ap1` the first pair (`apa1` with `--stop-after-noop`, then `apa2`) showed it: the fresh worker reopened the
closed fixture through `qualification_translation_resume_closed_fixture`, and its next capture was refused by the
worker's ownership check (`ProjectIsolationRequired`, "Exact saved worker-owned translation fixture required."; the
qualification tool returns MCP's generic error text, the Host log names the refusal). That run stopped in the runner
itself; the repeat on `ap2` (`bpa2`) observed the same refusal and stopped on a variable the check overwrote, fixed in
`bba756d3`. The pair is queued on `ap4`.

## VCI remote bound

A local `--git-remote` is walked for links before Git's receive-pack runs on it, at most 100,000 entries
(`GitRemote.RequireSafeLocal`). A remote with more, typically many loose objects, is refused with `GitLimitExceeded`.
Decided on 2 November: this stays a documented limit, not a gap. The bound keeps the safety walk finite, and a remote
past it is repacked by its owner (`git gc` packs loose objects and refs), after which it holds a few pack files. The
[VCI Host workflow](../reference/vci-host-workflow.md) says so, and `TiaMcp.Engineering.Tests` pins the refusal of the
walk past its bound; no offline test proved it before.

## Project upgrade sources

V15.1 and V17–V20 sources and UMAC-protected and locked sources are blocked on this PC:

- Only TIA Portal V21 is installed (no older Portal, no older Openness).
- V21 Openness cannot make an older project: `ProjectComposition.Create` creates a V21 project, `Project.Archive` takes
  only an archivation mode, `Project.SaveAs` saves in V21, and `ProjectBase.ProtectProject` protects the open V21
  project. A V21 scratch is refused as a source (`project_upgrade` takes V14–V20 only), and a locked source needs a
  V14–V20 source as well.
- The public V14–V16 examples of 31 October are not part of the repository.

They need an older TIA installation or supplied fixtures, never a user project. The interrupted upgrade, which the
TODO still listed here, passed natively on 1 November (`local.project_upgrade.interruption.20261101`, 16/16 cases).

## Cleanup

Each runner closed its Host and owned TIA (the Harness and the exact translation clients require the owned Portal to
exit); afterwards no TIA, Host or worker process from `ap1`–`ap4` was left. The PLCSIM UI and Runtime Manager were
already running and were left alone.
