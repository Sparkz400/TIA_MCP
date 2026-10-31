# Production Unified text-list import route — 31 October 2026

**Status:** interruption passed for Unified Comfort; the route runner has not passed yet. Evidence:
[`local.hmi.text-list-import.interruption.20261031`](../../evidence/local.hmi.text-list-import.interruption.20261031.json)
(frozen `artifacts/fw3f`, commit `1121bb8`, run `artifacts/ti1`). No profile is admitted in production
(`HmiTextListImportRules.Profiles` is empty); every run used a qualification Host
(`--hmi-text-list-qualification <profile>`) on owned copies of `HmiSeeder textlist-fixture` archives.
[Route](../reference/unified-text-list-authoring.md#importing-a-candidate-into-a-project).

## Interruption (run `ti1`, 10 scenarios, all passed)

`qualify_interruption.py --hmi-fixture artifacts/tx2/f1/checkpoint.zap21 --hmi-fixture-profile unified-comfort`
interrupted one `hmi_text_list_import` apply of the boundary-values candidate. Each scenario then reconciled it
once from a fresh Host and owned TIA (`engineering_admission_reconcile`) and checked that nothing was replayed
(journal bytes unchanged, one admission). Released cases were recovered from their checkpoint, and every case
was checked by an independent fresh read of the original project.

| Boundary | Interruption | Outcome | Effect persisted |
| --- | --- | --- | --- |
| checkpoint (`tl-ckpt`) | worker | held | no |
| baseline compile (`tl-mut`) | worker | released, project unchanged | no |
| native import (`tl-imp`) | worker | released, project unchanged | no |
| export readback (`tl-read`) | worker | released, project unchanged | no |
| compile and preservation (`tl-comp`) | worker | released, project unchanged | no |
| after the save (`tl-save`) | worker | held | yes |
| checkpoint, import, readback, save (`tl-hckp`, `tl-himp`, `tl-hred`, `tl-hsav`) | Host | held | yes |

In the four Host-loss cases the worker finished the whole apply on its own: exact import readback, unchanged
HMI capture outside the candidate lists, unchanged compiler problems and save. This is the first native
evidence of a complete apply. The admission is held because a fresh session cannot recompute the HMI outcome
(target kind).

## Route runner (runs `tx1`, `tx2`; not passed)

`qualify_hmi_text_list_route.py` passed its refusal phase on both profiles in both runs. Four actual prior
records with content V21 changes, each refused with its code before any native call. Author record with
`outputId`, unknown record, client-supplied candidate and unknown set output, all refused at the Host. Both
seeded fixtures are created natively: `MTP700 Unified Comfort` and `WinCC Unified PC RT`, both 21.0.1.0.

- `tx1` (`eaf282c`): both profiles stopped at the first prepare with `HmiPreservationUnavailable`. In the
  fixture, de-DE is a project language but is not enabled for runtime. V21 then throws
  `EngineeringTargetInvocationException` from `HmiLanguageAndFont.Order` (`runtimeSettings/languageAndFonts/German (Germany)`).
  The capture correctly refuses this as a typed read error. Fix `c619140`: the route fixture enables every
  project language for runtime (Order 0 and 1 read back).
- `tx2` (`c619140`): both profiles imported the first candidate and read it back exactly. Apply then failed
  `CompileFailed`. The first compile of the retrieved fixture lists the two encrypted-transfer password errors
  flat, under a `Success` summary row. The compile after the import nests the same errors under
  `Runtime settings`, with container rows and an `Error` summary row. Fix `1121bb8`: only problem rows with text
  are compared, and the closing summary row is excluded. The Host-loss scenarios of `ti1` ran this build through
  the save.

## Still open

- A passing route run per profile: the apply sequence, the stale-preview and replay checks, `change_restore`
  and the fresh reopen. Unified PC has not been interrupted yet.
- A project language that is not enabled for runtime leaves the capture with a typed read error. Such projects
  are refused, not imported.
