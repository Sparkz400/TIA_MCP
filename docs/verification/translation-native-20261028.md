# Project translations under V21 Update 3 — 28 October 2026

The 16 October translation packet ran on the previous TIA Portal V21 install. These runs close the D03
release rows under Update 3:

- the ten `qualification_translation_*` tools of the explicit `--translation-qualification` session;
- the six default-mode `translation_native_*` tools, against an installed exact admission;
- the TODO item for native interruption of `translation_native_*`.

Everything ran in owned Portals and owned scratch projects (CPU 1511-1 PN V4.1, English TIA UI, de-DE
reference and en-GB target). Nothing touched a user project, a PLC or PLCSIM.

## Builds

| Build | Commit | Use |
| --- | --- | --- |
| `D:\wp\tr07` | `bf12c29` | Debug Host and worker for every accepted run; `source-manifest.json` hash is the admission's `sourcePackageSha256` |
| `D:\wp\tr07\deploy` | `bf12c29` | Byte-identical copy of the `tr07` Host and worker trees (equal tree hashes), with the admission registry installed |
| `D:\wp\tr08` | `f8c5788` | Scripts only: the default-mode runner with one idempotency key per interruption scenario, run against `tr07\deploy` |

`tr01`–`tr06` are retained failed or superseded builds; their failures are the findings below.

## Runs

| Run | Runner | Result | What it covers |
| --- | --- | --- | --- |
| `tr07\si` | `qualify_translation_import.py --same-instance` | Passed; independent `verify_translation_admission.py` passed | One fixture, one forward key (`e4ef8daa…`). Seven forward cases: interruption before dispatch, source drift, lost import, compile, save, reopen and restore replies. Reviewed reverse edits after the three verified F outcomes. 8 imports and 10 distinct restorations. Every original/recovered pair reconciled by fresh reopen, existing destinations refused unchanged, zero pending resources, normal Host, worker and Portal exits. The final B→F plan stays unattempted with the same key. |
| `tr07\q1` | `--interrupt-before-checkpoint` | Passed | Fixture seed, category probes, capture, a no-op interrupted after acquisition and before checkpoint (`outcomeUnknown` at revision 2, no checkpoint, no import) |
| `tr07\q1\continuation-01` | resume of `q1`, `--stop-after-noop` | Passed | `qualification_translation_reconcile_before_checkpoint` on a new Host and worker after exact close of the first; a full no-op lifecycle with forged-plan, stale-revision and replay refusals, checkpoint recovery and reconciliation |
| `tr07\q1\continuation-02` | resume with `--prior-receipts continuation-01` | Passed | `qualification_translation_resume_closed_fixture`, then only the remaining Main edit: import, compile, save, reopen, then a lost restore reply reconciled from the reserved destination without retrieving again |
| `tr07\p2` | `qualify_translation_native.py` (default mode) | Passed | Three interruption scenarios and the clean lifecycle (below) |

Evidence: `local.translation.qualification.20261028` (the first four runs) and `local.translation.native.20261028` (`p2`).

### Default-mode lifecycle

The registry was built by [`build_translation_admission.py`](../../scripts/build_translation_admission.py) from
`tr07\si`. Its native cases cite the independent verifier receipt and its portable cases cite the
`TiaMcp.TranslationImport.Tests` receipt (165/165) from the same frozen source. It admits exactly the `si`
scratch project and its one Main OB1 comment edit. It was installed into `tr07\deploy` only; no package
contains it. The Host started without `--translation-qualification`. It listed the six production tools and
no qualification tool, then:

- refused an uninstalled profile (`TranslationImportProfileUnqualified`), a malformed profile ID
  (`TranslationAdmissionInvalid`) and an unknown operation (`OperationNotFound`), all before native dispatch;
- captured the project, then built a review, explicit approval and candidate through the offline tools
  (`translation_inspect`, `translation_prepare`, `translation_approve`, `translation_candidate`);
- prepared the native plan, which the Host admitted only because its exact key matched the registry;
- refused a forged plan (`ReviewApprovalMismatch`) and a stale revision (`RevisionConflict`) without changing
  the operation;
- imported once, with readback, peer preservation, compile, save and fresh reopen;
- refused the replay (`RecoveryRequired`) and an existing recovery destination
  (`TranslationRecoveryDestinationExists`; the marker file kept its hash);
- recovered the checkpoint into a new directory and reconciled read-only: `verifiedApplied` original and
  `verifiedRestored` copy;
- refused to prepare the changed project from the old candidate (`StaleContent`).

### Default-mode interruption

Each scenario captured, reviewed and prepared a fresh plan on a Host. The runner sent `translation_native_apply`,
polled the durable journal until the named state, and then ended the exact process: the worker together with
its owned Portal (image and parent verified), or the Host. A fresh Host then read the status, refused the
replay, recovered where the journal allows it, and reconciled. All three left the original unchanged, so each
following scenario started from the admitted baseline.

| Loss | Journal state reached | Outcome |
| --- | --- | --- |
| Worker and Portal | `checkpointIntent` (archive in progress) | `outcomeUnknown` with no retained checkpoint; replay refused; reconciled `unchanged` |
| Worker and Portal | `dispatchIntent`, inside the native import call (import directory created, no return) | `outcomeUnknown`; replay refused; checkpoint restored into a new directory; reconciled `unchanged` / `verifiedRestored` |
| Host | `dispatchIntent` | The worker finished the import, could not reply and exited without saving. The journal stayed at `dispatchIntent`, which only read-only reconciliation accepts (recovery needs `outcomeUnknown`, `recoveryRequired`, `ready` or `succeeded`); reconciled `unchanged` |

No import or restoration was repeated: the native import directories match the attempts one to one.

## Findings

- **Category leakage still holds.** On the seeded fixture, the block-comment, hardware and alarm-class
  category requests on the PLC each returned all six PLC rows, against 5, 1 and 0 expected. Restricted
  category/object profiles stay disabled. The populated-Main control is now inconclusive: its sentinels are
  English default OB interface comments, which an English-UI scratch stores under en-US rather than the de-DE
  source.
- **A new scratch takes the TIA UI language as reference.** Seeding failed with "German reference language
  required". `qualification_translation_fixture` now activates de-DE, makes it reference and editing language,
  adds en-GB and deactivates the rest.
- **The Host assumed the Siemens API under `C:\Program Files`.** TIA is installed on `D:` here, so every
  qualification prepare failed while hashing the API. The Host now reads the registered
  `Siemens.Engineering.Base` location, as the worker already did.
- **View paths follow the UI language.** The export writes `Program blocks\Main\Block comment`. The peer
  comparer, the admission descriptor and the verifiers accepted only the German labels, so every Main edit
  would have failed its preservation check. Both label sets are now accepted for exactly that field.
- **Read-only interface comments make imports return Warning.** The whole-project export lists Main's
  `Initial_Call` and `Remanence` comments, and importing them back unchanged logs one read-only warning each.
  The adapter accepted only `Info`, so every import was refused after dispatch. `Warning` is now accepted only
  when the log is exactly those read-only warnings on rows the candidate leaves unchanged.
- **The export sorts rows by source text.** The source-drift case moved the Main row, so the profile check
  refused it (`TranslationScopeMismatch`) before the content check (`StaleWorkbook`). Both are pre-dispatch
  refusals; the runner and verifier accept either.
- **Runner fixes.** The old runners required the machine-wide Portal set to be unchanged. Other lanes start and
  end their own Portals, and these runners never terminate a process. They now require only that the owned
  Portal exits, and record foreign Portals that ended (`si` and the continuations each saw one). The first
  default-mode attempt (`tr07\p1`) reused one idempotency key for both `dispatchIntent` scenarios; the third
  prepare was correctly refused as `IdempotencyConflict`. That attempt is retained and `p2` reran all three.
- The global native-dispatch guard admits one coordinator at a time. A pre-checkpoint run started while the
  same-instance run held it, and it refused before starting a Host (`tr07\q1-refused-guard-busy`).

## Checks

- `TiaMcp.TranslationImport.Tests`: 165/165, including the new UI-label and read-only-warning checks.
- `test_translation_failure_packet.py`, `test_translation_native_packet.py`: passed.
- `test_translation_host_admission.py` gained the pre-checkpoint injection conflict case.

## Limits

- One scratch per packet, one PLC, de-DE/en-GB, target-only text. Generalized culture pairs, several PLC
  owners and source-text mode were not run natively. Rich text, clears, empty targets, HMI and customer
  projects are not covered.
- The read-only warning is matched in its English wording; a German UI fails closed until observed.
- The installed admission is instance-bound: it cannot admit any other project. Production import stays
  unavailable until an operator reviews evidence and installs a registry for their own project.
- A Host lost at `dispatchIntent` can only be reconciled read-only. A partial original would stay unresolved
  rather than be restored from the checkpoint; none was observed.
