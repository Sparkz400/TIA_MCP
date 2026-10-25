# HMI production-bridge retrieval and read-only inspection — 25 October 2026

Fresh independent `HmiSeeder` fixtures were created on this PC for every installed
HMI profile: two HMI owners each, two CPU 1511-1 PN owners with protected
confidential configuration and project users, and integrated HMI connections. The
production Host and V21 worker then retrieved each fixture archive with
`project_retrieve` and inspected it read-only. Curated evidence:
[`local.hmi.bridge.20261025`](../../evidence/local.hmi.bridge.20261025.json).

| Profile | Bridge retrieval | Inspection accuracy | Explorer | Save/reopen and archive recovery | Compile |
| --- | --- | --- | --- | --- | --- |
| Classic TP700 Comfort | passed | passed | passed | passed | failed: no start screen |
| Classic WinCC RT Advanced | passed | passed | passed | passed | failed: no start screen |
| Classic KTP700 Basic PN | passed | passed | passed | passed | failed: no start screen |
| Unified MTP700 Comfort | passed | passed | passed | passed | failed: encrypted-transfer password |
| Unified PC RT | passed | passed | passed | passed | failed: encrypted-transfer password |
| Classic WinCC RT Professional | blocked | blocked | blocked | blocked | blocked |

Duplicate names, wrong-family and stale-target rejection and bounded inspection also
pass for all five profiles. Both Unified profiles pass the integrated connection, PLC
and DB declaration, screen-tag-PLC chain, script and alarm-reference gates. The owned
Portal, Host and worker exits were verified for every run.

## Archive-retrieval access denial

The earlier bridge denial (`bridge-positive-03/04`, 12 October) did not recur. The
fixtures contain the protected CPUs and project users introduced at that time, so
those settings do not cause it. The original reports and fixtures no longer exist on
this PC; the failure cannot be re-examined, and only the current pass is claimed.

## Fixes found by these runs

- Classic slide-ins have no `Name` and their system folder rejects `Folders`. The
  adapter names them by `SlideinType` and no longer asks that folder for `Folders`.
- Every Classic screen role (screen, template, popup, slide-in, global elements,
  overview) exports the same SimaticML. Item inspection now exports and parses all
  six, skipping each role's own root element.
- The KTP700 Basic PN popup import terminated the native Portal (run `h2/s1`). The
  seeder no longer authors that popup and records the skip.
- Classic regular screens reappear in a different enumeration order after save/reopen
  and retrieval (A, B becomes B, A). Screen names are unique within each Classic
  screen collection, so the qualifier compares those collections by name. It also
  compares every export by content, removing only `DocumentInfo/Created`. IDs,
  unknown fields and every other ordering stay significant.

## Remaining blockers

- Native compile needs a configured Classic start screen and Unified
  encrypted-transfer passwords. Neither is settable through the public API, and both
  need native editor work.
- Classic PLC declaration mapping fails. The integrated connections exist in hardware,
  but `HmiTarget.Connections` is empty, and positive Classic fixtures have no PLC-bound
  tag without a native-accepted Classic binding grammar.
- WinCC RT Professional is in the catalog (`6AV2 105`, V21), but the PC rack's
  `CanPlugNew` refuses its application item in every slot. No RT Professional fixture
  can be created without a usable WinCC Professional installation or license.
