# Classic screen recovery — local verification, 3 November 2026

This record covers recovery for bounded `hmi_classic_screen_edit` operations on
Classic RT Advanced. It does not qualify other mutation kinds, all Classic
profiles, whole-HMI preservation or runtime behavior. The workflow and limits are
documented in the [HMI engineering guide](../guides/hmi-engineering.md#screen-failure-recovery-without-closing-tia).

## Failure mechanisms and changes

A returned import or compiler failure previously left the change in
`recovery_required`, even when its original screen could be restored in the open
project. A separate failure occurred when a five-minute MCP call timed out but
the native worker subsequently finished, compiled and saved successfully. The
durable admission remained `outcomeUnknown`; the generic reconciliation route
required the original TIA process to exit and could not verify this screen scope.

The bounded screen workflow now attempts one restoration of the retained original
screen under the original native exclusive-access lease. It requires unchanged
peer screens, valid checkpoint and backup hashes, exact restored readback,
unchanged successful compiler diagnostics and exact screen-scope equality before
and after save. Only verified recovery reports `rolled_back`; the original error
is retained. Verified pre-import failures report `not_applied`. A failed or
unverifiable restoration remains held.

For a terminal `applied` or verified `rolled_back` journal, admission reconciliation
can attach to the still-open project. It checks the exact admitted resources,
prepared preview, retained files, terminal compiler evidence, every regular screen
and saved project fingerprint under native exclusive access. The verification
does not import, save or replay. Generic recovery rules remain unchanged for other
operations. Busy errors now identify the blocking admission IDs.

The final fingerprint reuses the saved screen capture already verified under the
same exclusive lease, eliminating a redundant full export. Long screen operations
use a 900-second worker timeout and a longer client timeout (960 seconds).

## Managed and build checks

- HMI engineering checks: 79 passed, zero failed. Recovery cases reject missing
  terminal stages, unfinished changes, wrong operation kinds, changed warnings,
  compiler errors, truncated diagnostics, changed peers and inconsistent scope
  hashes.
- The complete engineering test executable passed, including 48 admission
  reconciliation checks, cross-process resource conflicts, interrupted-operation
  recovery and unchanged generic reconciliation restrictions.
- The terminal-policy checker accepted the actual previously timed-out applied
  journal with all 73 regular screen records. This check alone does not release
  ownership; fresh native verification is still required.
- The isolated Release build succeeded with zero errors. Existing nullable
  warnings remain. The compiled-worker guard passed: user TIA discovery and
  attachment do not dispose the user process, and Classic graphic-list export
  remains excluded. Strict bundle hash verification passed.

## Native open-project reconciliation

Using the public stdio MCP tools from the updated release, the worker attached to
the original, already authenticated TIA V21 project. The original TIA process ID
was unchanged throughout the test. Reconciliation verified all 73 regular screens
against the retained saved manifest, plus checkpoint/input/export hashes, terminal
stages, complete compiler evidence and the exact saved-state fingerprint.

The public result was `released: true`, `held: false`, `automaticReplay: false`.
The admission moved from `outcomeUnknown` to `reconciled` through the normal
durable store. No journal was manually edited, and no TIA restart, import or save
was used to release the lock. The temporary MCP Host and worker then exited
normally, leaving the user project open.

## Subsequent screen apply in the same TIA process

After reconciliation, a fresh MCP attachment prepared and applied the second
corrected infeed screen in that same TIA process. Import, exact target readback,
baseline/final HMI compilation, peer-screen preservation, save and saved-content
verification all passed. No `ResourceBusy` or recovery lock remained.

The final native exports of both infeed screens match their candidates. All 94
status references resolve against the native HMI tag structure; the WinCC array
indices correctly use 0–11 for the PLC's belts 1–12. Compilation reports zero
errors and the same 18 baseline warnings. The other 71 regular screens are
unchanged. Final TIA editor visual inspection and runtime behavior were not
tested; computer use remained stopped.

## Native in-place restoration during the screen extension

After a third regular screen was added, two actual extension attempts exercised
automatic restoration in the same open TIA process, now with 74 regular screens:

- The first import changed the intended screen but failed exact readback because
  WinCC serialized visibility animations before the font and caption compositions.
  The worker restored the original screen, compiled, saved and verified the entire
  declared regular-screen scope. The terminal state was `rolled_back`.
- The corrected composition order passed exact readback. Compilation had zero
  errors but three additional warnings because the candidates resized existing
  signal-lamp graphics. The unchanged-diagnostics gate rejected the change and
  the worker again completed verified in-place restoration with `rolled_back`.

Both restorations returned to zero errors and the same 18 baseline warnings.
Fresh preparations were accepted after each failure without restarting TIA,
closing the project, clearing the admission store or editing a journal. With
native composition order and the existing 20-pixel lamp size, the first extended
screen then applied, compiled and saved successfully in that same process; the
other 73 regular screens were preserved.

The remaining manual screen and the new overview/diagnostics screen subsequently
passed the same import, exact readback, compile, preservation, save and saved-state
checks. All three saved native definitions match their prepared candidates. Their
446 infeed status bindings resolve against the native HMI tag structure, all 48
manual motion selectors retain their original function IDs and permissions, and
the other 71 regular screens remain unchanged. Each final compile has zero errors
and the same 18 baseline warnings. Empty-line initialization was explicitly left
inhibited at the user's request: its SETUP-only HMI permission conflicts with the
PLC's AUTO requirement, so the final three screens contain no initialization
selector. PLC code and runtime state were not changed.

## Evidence limits

These observed failures exercise native readback rejection and compiler-warning
rejection followed by automatic restoration. The wider interruption and recovery
failure matrix remains covered by managed policy tests; these observations do not
qualify every native failure mode. No equipment download or runtime test was
performed. Private project paths, XML, archives and compiler records remain in
local artifacts rather than the repository.
