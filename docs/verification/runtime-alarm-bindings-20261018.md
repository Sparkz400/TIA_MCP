# Runtime capture alarm bindings — 18 October 2026

Base: `00330f4c3eb5be7db8d7450dae25c326d4261415`. This increment carries
explicit normalized supplied alarm bindings through capture, analysis, export and
Explorer. It does not define a Siemens alarm format or native producer.

## Implemented boundary

- Start independently reconstructs every explicit binding from the exact original
  review and refuses unresolved or altered source before capture intent/polling.
- Optional retained association metadata includes all original bindings even when
  capture selects only a subset. The existing 128 KiB combined plan limit refuses
  oversized evidence without truncation or partial capture.
- New analysis revalidates the original case/review and exact retained report.
  Old captures without the field keep their prior analysis behavior. Raw capture
  reopen/export and already-saved report exports survive source removal.
- Report exports and Explorer reject promoted nested qualification claims. Both
  show selected versus unselected signals without claiming values were observed
  or an alarm was active. Original supplied text is escaped.

## Current evidence

Evidence is retained under `artifacts/runtime-alarm-bindings-01/`.
The initial managed run passes 40 diagnostic groups and 57 new checks; an
additional exact retained-report mismatch check is included for final validation.
The real loopback TLS/DPAPI/Host workflow passes 1,492 checks, including new
alarm-bound capture, source-removal and export scenarios. That total includes
variable polling assertions. All four altered qualification claims refuse before
polling or intent. The workflow includes two intentional Host kills and normal
shutdowns; no native worker executes.

Eleven capture-contract tests pass. The browser run passes with no page errors
or network requests, including thirteen alarm refusal mutations, unchanged view
on refusal, exact download and supplied-HTML escaping. Light/dark 1280x800
screenshots are retained; both views were inspected. Final frozen results are recorded below;
integration evidence is retained separately.

## Remaining scope

Independent online address/program association, native alarm status, actual call
invocation, complete consumer mapping and native AL lifecycle operations remain
open. Broader runtime storage pressure/profile work and the original 44-workstream
goal remain open. Missing Siemens products and historical evidence are not passes.

## Final frozen verification — 19 October 2026

The identical 2,467-file source snapshots pass all 44 managed
suites, 1,531 Python tests (one missing-native-evidence skip), 25 Explorer
model/client checks, 38 actual browser checks and all 38 package stages. The
diagnostic suite passes 40 groups, including 58 alarm-capture checks. Both actual
TLS/DPAPI/Host workflows pass (1,499 aggregate and 1,493 packaged
checks; polling totals vary). Each includes two intentional process kills and
two normal shutdowns. No native worker executes.

Both workflows retain three exact alarm bindings for one selected signal,
preserve unselected signals as unknown and keep all verdicts unresolved. Reports
and raw exports survive source removal. Each build also passes 62 checks against
actual prior-producer capture bytes across two clean Host lifetimes; no old record
is regenerated or rewritten.

The relocated Host also re-exported the retained alarm analysis after source
removal in one additional clean lifetime. Four report browser checks pass at
1440 and 390 pixels, including exact JSON, inert supplied text, keyboard access
and no page overflow. Separate captures verify the selected/unselected alarm
table; this is offline presentation evidence, not native HMI acceptance.

Source manifest SHA-256: `849ab241b465ca3999acb51315a7cd1e0c5837d52950b1c18148002e48420762`.
Package strict manifest SHA-256: `b17b1442d76e17e7c461d8c1f100ea9316fd0d8df04a62a235fd724b3534c9fe`.
All 112 historical evidence binding rows are unchanged; 106 remain unavailable.
Only final status/verification prose differs after the freeze. The original
44-workstream audit and all native qualification gates remain unchanged.
