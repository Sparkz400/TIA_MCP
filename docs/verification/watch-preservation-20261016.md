# Watch document scalar preservation — 16 October 2026

This D02/L05/F02 audit changes the shared preserving XML document reader. It adds
no vendor grammar, tool, dependency or native mutation admission.

## Reproduced defects and repair

`Field` flattened nested elements into ordinary row values through the XElement
string conversion. Comments and processing instructions were also hidden from
the typed review. A captured display format could therefore appear to be an
ordinary `Bool` despite containing unsupported markup.

Comment language identifiers had the same problem. Reading or selecting a
comment treated opaque culture contents as an ordinary culture key. Adding a
language cloned that item and assigned `Culture.Value`, silently discarding its
opaque contents in the clone.

Scalar reads now use the existing plain-text guard. Comment culture validation
uses that same guard, and direct comment edits validate culture identities before selection while
leaving unselected opaque text untouched. Upsert and culture filtering already call that shared validation.
Absent optional fields still return null; attributes and metadata outside scalar
contents remain preserved. No-op capture returns the exact original bytes even
when a later typed read or edit refuses unsupported contents.

## Executed development checks

- `artifacts/watch-preservation-dev/before02.log`: the original implementation
  fails three new groups, including direct language cloning (31/34 pass).
- `artifacts/watch-preservation-dev/after.log`: all 34 planner/document groups
  pass, including unchanged genuine capture hashes and independent XML oracles.
- Four dispatcher adversaries start with a retained genuine seed and inject
  nested markup or a comment into DisplayFormat/Culture. Artifact and observation
  hashes are updated while the synthetic typed response stays scalar. Each
  preparation refuses before candidate publication, checkpoint or apply.
- A positive clone oracle checks every XML node, opaque culture/text attributes,
  item comments/instructions, untouched peers and remapped native IDs.

The malformed variations are synthetic adversaries, not newly qualified Siemens
exports. Genuine fixture byte provenance remains unchanged.

A compatibility check caught an overbroad intermediate guard: it also validated
unselected text. `compatibility-before.log` records that refusal (34/35 pass).
The final guard checks cultures and only the selected text; `final-library.log`
passes all 35 groups, including independent preservation of opaque peer text.

## Aggregate validation

Validation is deliberately split across two frozen Git-tree snapshots. Both have
1,788 tracked files and base commit `7142f5e822eb9afcae60510cfb16ab28b8966d0a`.

- `artifacts/watch-preservation-01`: tree
  `b1eece142528d75dd4766926cdd9eef5b8ab73bf`, source manifest SHA-256
  `826a513c06037a36ec083f0d34b8a591e00671ac9c006797e9dadd78a4c882ec`.
  Full aggregate passes: 29 managed suites, 1,266 Python tests with one missing
  native-evidence skip, 24 Explorer model/client checks and all actual MCP flows.
  `source/artifacts/checks/result.json` SHA-256:
  `0b7d0bc589202983a7cac78c6d84e72eea0157b3d526010b2c9006866376e03b`.
  This snapshot predates the final unselected-language compatibility refinement
  and is not final binary evidence.
- `artifacts/watch-preservation-02`: final tree
  `9b92c1a2ecbeb8b5a9051cf2f7a8a67f6b389754`, source manifest SHA-256
  `1f093c246cc0be9afdd846f80ef6b36082ca7ae69cc40f8b5dc0ab04f83e676d`.
  All 1,266 Python tests pass with one unavailable-native-evidence skip; contracts,
  source bindings and evidence inventory pass. All 35 watch planner/document groups
  and the complete engineering admission/authoring/recovery suite pass, including
  20 expanded watch checks. Host builds with zero warnings/errors, and five actual
  Host qualification-boundary checks pass without launching a native worker.

Final snapshot receipts:

- `source/artifacts/python-checks/result.json` SHA-256
  `b1dabeb9da93340a560373eb2415adc827f91d820be392960eb618f22967387d`.
- `source/artifacts/final-checks/result.json` SHA-256
  `a55a9d2d4144ea3e12772f3013da690ff5c348bcd270f547b4b27537c979f3db`.
- Final Core DLL SHA-256
  `6e229c677cdc2060df241df874f4ea117e16b36861f050e6b92aa6d96c1cec5f`;
  Host DLL SHA-256
  `d5c20ba98c76d015cdfa0f03f804ad004b768c76675de793f2fcaf863c920938`.

The snapshots differ only in the document implementation, document regression
checks, generated source bindings and this verification document. Final commit
source/test bytes must match snapshot 02; subsequent edits are documentation only.
Metadata remains 175 declared tools, 55 mutation kinds and 248 features. Six
retained evidence files match and 106 remain unavailable; no qualification is
promoted. Watch scalar commit `71154ca` passes all hosted jobs (`38928381550` and
Explorer `38928381546`). No new portable ZIP was built locally for this increment.

## Limits

The source is shared with the net48 native adapter, but that adapter cannot be
built here without Siemens dependencies. Native import, compile, reopen, recovery,
hardware and vendor/version compatibility remain untested on this build. The
generic watch mutation gates and the full offline execution queue remain open.
