# Production Unified text-list import route — 31 October 2026

**Status:** passed on Unified Comfort and Unified PC. Evidence:
[`local.hmi.text-list-import.route.20261031`](../../evidence/local.hmi.text-list-import.route.20261031.json)
(route, frozen `artifacts/fw3g`, commit `a84d2a5`, run `artifacts/tx3`, 168 checks);
[`local.hmi.text-list-import.interruption.20261031`](../../evidence/local.hmi.text-list-import.interruption.20261031.json)
(Unified Comfort, `fw3f`/`1121bb8`, run `ti1`) and
[`local.hmi.text-list-import.interruption-pc.20261031`](../../evidence/local.hmi.text-list-import.interruption-pc.20261031.json)
(Unified PC, `fw3g`/`a84d2a5`, run `ti2`). The runs used a qualification Host
(`--hmi-text-list-qualification <profile>`) on owned copies of `HmiSeeder textlist-fixture` archives; the
evidence-curation commit then admitted both profiles in production (`HmiTextListImportRules.Profiles`).
[Route](../reference/unified-text-list-authoring.md#importing-a-candidate-into-a-project).

## Route runner (run `tx3`, both profiles passed)

`qualify_hmi_text_list_route.py --profiles unified-comfort unified-pc`, 84 checks per profile, no failures. Per
profile:

- The seeder created the fixture (`MTP700 Unified Comfort` or `WinCC Unified PC RT`, 21.0.1.0, en-US/de-DE
  enabled for runtime), and the Host retrieved an owned copy.
- Refusals before any write: four actual prior records with content V21 changes, each with its code; an author
  record with `outputId`; an unknown record; a client-supplied candidate; an unknown set output; and a candidate
  missing a project language (`HmiTextListLanguageMismatch`).
- Nine imports in order: boundary values, texts and names; the v1 base replacing the peer list; the retained
  v2 author and the v3 author that replaces it; both outputs of a retained v1 set; reordered languages. For each:
  - preview created/replaced lists correct;
  - apply with checkpoint, device compile and save;
  - the worker's readback exact, and the independent PyYAML oracle on `hmi_inspect`'s export exact, with every
    other list unchanged;
  - a same-key replay returned the recorded outcome, and another key was refused with `RecoveryRequired`.
- A preview prepared before another saved import was refused with `StaleRevision` and stayed prepared.
- No typed node outside the text lists changed: the first compile left the capture unchanged, and
  `compileChangedTypedNodes` was empty.
- `change_restore` of the last import passed with HMI content equality; the restored copy holds the previous
  lists.
- A fresh Host and owned TIA reopened the saved original with the last lists and unchanged typed nodes.
- Host and worker exited 0.

## Interruption on Unified PC (run `ti2`, 10 scenarios, all passed)

Same scenarios and outcomes as `ti1` below, on the `tx3/f2` Unified PC fixture. In addition, each Host-loss
scenario required the journal to end `applied` with every stage of a complete import.

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

## Earlier route runs (`tx1`, `tx2`; not passed)

`qualify_hmi_text_list_route.py` passed its refusal phase on both profiles in both runs. Four actual prior
records with content V21 changes, each refused with its code before any native call. Author record with
`outputId`, unknown record, client-supplied candidate and unknown set output, all refused at the Host. Both
seeded fixtures are created natively: `MTP700 Unified Comfort` and `WinCC Unified PC RT`, both 21.0.1.0.

- `tx1` (`eaf282c`): both profiles stopped at the first prepare with `HmiPreservationUnavailable`. In the
  fixture, de-DE is a project language but is not enabled for runtime. V21 then throws
  `EngineeringTargetInvocationException` from `HmiLanguageAndFont.Order` (`runtimeSettings/languageAndFonts/German (Germany)`).
  The capture correctly refuses this as a typed read error. Fixture change `c619140`: the route fixture
  enables every project language for runtime (Order 0 and 1 read back). Projects in that state stay refused;
  the code was not changed for it.
- `tx2` (`c619140`): both profiles imported the first candidate and read it back exactly. Apply then failed
  `CompileFailed`. The first compile of the retrieved fixture lists the two encrypted-transfer password errors
  flat, under a `Success` summary row. The compile after the import nests the same errors under
  `Runtime settings`, with container rows and an `Error` summary row. Compile rule fixed in `1121bb8`: only
  problem rows with text are compared, and the closing summary row is excluded. A later offline revision also
  requires both summary rows to match (digits ignored) and refuses an empty-text row that is no container. The Host-loss scenarios of `ti1` ran this build through
  the save.

## Not covered

- Other Unified devices, other languages, customer projects, consumers of imported lists, runtime and visual
  behavior. The fixtures keep their two encrypted-transfer password errors, so no clean compile is shown.
- A project language that is not enabled for runtime leaves the capture with a typed read error; such projects
  are refused, not imported.
- Host loss and a lost save hold the admission for review; the reconciler does not verify the applied text lists.
