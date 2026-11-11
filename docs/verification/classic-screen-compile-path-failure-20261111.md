# Classic screen edit: HMI compiler path failure after the import — 11 November 2026

This record covers one failed `hmi_classic_screen_edit` apply on Classic WinCC RT
Advanced and the reporting change made for it. **The cause of the compiler failure is
not identified, and it was not reproduced.** The workflow still cannot detect the
condition before the import. Private project paths, screen names, XML, archives and
compiler records remain in local artifacts and are not part of the repository.

## What happened

A four-screen batch was applied to a project open in a user-owned TIA V21 instance with
user interface. The edits changed integer constants only.

| Step | Result |
|---|---|
| Checkpoint save | 22 ms; the project was unmodified |
| Recovery archive | 39 s |
| Baseline `compile-hmi` | 8 s, 0 errors, 18 warnings; answered with `New compilation is not necessary` |
| Four imports | each read back and verified |
| `compile-hmi` after the import | 19 s, aborted: 1 error, 58 warnings |
| Rollback imports | each read back and verified; screen-scope hash equal to the baseline |
| Rollback `compile-hmi` | 13 s, aborted with the same rows |
| Outcome | `recovery_required`, "Rollback compiler diagnostics do not match the successful baseline" |

Both failed compiles got past the screen checks and the tag count, then reported
`Internal WARNING (detail: EvaluateStringRelations: stream to patch is null).` followed by
`Cannot compile. Cannot access the project path.` The 40 additional warnings were 39
`No graphics list assigned to '…'` rows on instances of two project-library faceplate
types in screens the change did not edit, and the internal warning.

The user closed the project without saving and reopened it. A full HMI software
rebuild in the TIA user interface then finished without errors.

## What the retained journals show

- **The sequence alone is not sufficient.** Two applies to the same project on
  10 November ran the same sequence: an immediate checkpoint save, the archive, a
  baseline answered with `New compilation is not necessary`, four imports under the
  exclusive lease, and a compile of the unsaved project. Both compiles after the import
  generated (new time stamp, tag counts) and finished. So the archive, the exclusive
  lease, compiling an unsaved project and a skipped-generation baseline do not, alone
  or together, produce the failure.
- **The edit content is not the cause.** The rollback compile ran on the re-imported
  originals and failed with identical rows.
- **It was a condition of that TIA session, not a momentary one.** The two failed
  compiles were two minutes apart. A retry inside the apply would not have helped.
- **The failing component is the Siemens generator.** The internal warning text is in
  `Siemens.Simatic.Hmi.Es2rt.Target.CeRt.dll`. V21 runs the HMI compile in a child
  process of the Portal (`-Name=HMICompiler(cert,smart)`).
- **A skipped-generation baseline is a valid diagnostics reference.** In one of the
  10 November applies it equalled the later real rollback compile row for row. It
  proves nothing about whether the compiler can run.

"Not sufficient" does not clear the archive or the other factors: they may be necessary
together with something else. The difference between the two days that fits the
compiler's own messages best is the prior compile result. On 11 November an error-free
result from the user interface (time stamp about four hours old) existed before the
checkpoint archive, and `stream to patch is null` suggests the generator was trying to
update an existing result. On 10 November the baseline was an aborted compile with 110
errors. This is weakened by one observation: the earlier 10 November apply generated
error-free 26 minutes before the later apply's archive, although its rollback compile
then aborted with the original errors. The worker build was also newer on 11 November;
its changes since the 10th touch fingerprint reuse and batching, not the compile or the
project folder. None of this is shown to matter.

The same path failure was seen in a baseline compile around 4 November, which is why
one baseline retry exists. That journal is no longer retained on the workstation, so it
cannot be compared.

The clean full rebuild after reopening reported 18 warnings, as the user confirmed. The
39 `No graphics list assigned` warnings are therefore not findings of a full build.
They are a symptom of the failure: the generator could not read data of faceplate
instances it had read before. Not known: whether the HMI runtime or simulation was
running from that TIA session.

## Reproduction attempt

A compilable owned RT Advanced fixture can be built without the editor since the same
day: a screen created from a master copy in an HMI without screens becomes its start
screen (pull request 298 of this repository). On a retrieved copy of that fixture, in an
owned headless Portal, two runs replayed the order of native calls of the apply under
one exclusive access: save, archive, compile, re-import of a changed screen, compile,
re-import, compile; and the same without the archive. In both, the first compile was
answered from the last result and the two compiles after a re-import generated and
finished with 0 errors. **The failure did not occur.**

The fixture has one screen with one rectangle and one tag, no faceplate instances,
graphic lists or connections, and the Portal ran without user interface for under a
minute. The runs rule out the bare sequence on a minimal HMI, as the 10 November
journals already did on the real one, and nothing else. The customer project and its
checkpoint archive were not opened.

## Change

- The compile after the import and the rollback compile now recognize the path failure
  as a compiler that could not run. The apply failure message names it, and
  `compile.compilerUnavailable` is `true`. The error code stays `CompileFailed`.
- After such a rollback compile the worker still captures and compares the full
  regular-screen scope before it gives its verdict. On equality the journal carries
  `hmiEvidence.rollbackCompilerUnavailable`, the stage
  `hmi_rollback_scope_verified_unsaved` and a `recoveryFailure` stating that the open
  project holds the original screens unsaved. It says the saved project is the
  checkpoint only when the apply failed before its own save; after
  `changed_project_saved` it says the saved project holds the edit and names the
  recovery archive instead.
  The state remains `recovery_required`; nothing is saved without verified diagnostics.
- `hmiEvidence.baselineCompileSkippedGeneration` records whether the baseline was
  answered from the last compile. It does not refuse: both successful 10 November
  applies had such a baseline.

## Checks

- HMI engineering suite: 109 passed, 0 failed. The new case uses synthetic rows in the
  shape of the three compiles above and covers the skipped-generation flag, the final
  compile message, the two rollback messages, and that a real diagnostics difference and
  an exact match keep their previous verdicts.
- Release build of the V21 worker: 0 errors, 0 warnings.

No native run exercised the changed recovery path; there is no fixture on which the
compiler fails this way.

## Open

- Identify the cause. Still to vary on the owned fixture: faceplate instances with
  graphic lists from a project library, a Portal with user interface that has run for
  hours with an earlier error-free compile from the editor, a running HMI runtime or
  simulation, and a full rather than a delta generation.
- Until then the failure is detected only after the import.
- A change left `recovery_required` this way has no in-place reconciliation after the
  project is closed without saving and reopened.
