# Scope decision: renumbered document-local IDs on insertion — 25 October 2026

**Decision (user, 25 October 2026):** when a network is inserted into an existing
block, the document-local `ID` values that TIA Portal V21 renumbers count as
preserved, provided everything else stays exact. This unblocks existing-caller
insertion for lane A (typed LAD/FBD) and lane B (PLC components).

## Why

Export object `ID`s are numbered in order within one SimaticML document. They are
not stable identities. V21 renumbers every object that follows an inserted network:
in the recorded run, block-title IDs 8/9 became D/E after an insertion that added
five object IDs, and the block compiled. Strict preservation of those values is
unattainable by construction ([typed TODO](typed-lad-fbd-todo.md)), so the strict
contract rejected every existing-caller insertion with
`NativeIdPreservationUnsupported`.

## What is accepted

Only the `ID` attribute values of existing objects that follow the insertion point
in document order. In the observed terminal-insertion case these are the block
title and comment objects. The change must be exactly that renumbering:

- every following existing `ID` increases by the number of object IDs the inserted
  unit adds, and their order is unchanged;
- no new `ID` equals, aliases or collides with an existing one;
- removing the inserted unit and reversing that one shift recovers the complete
  original caller payload.

## What stays exact

Everything else, as in the existing
[insertion preservation contract](../verification/typed-p16-qualification.md#existing-caller-insertion-preservation-contract):
`ID`s before the insertion point, per-network `UId`s, title and comment text in
every language, declarations, opaque extension data, ordering, attributes and all
unrelated project XML. Saved reopen and checkpoint recovery/reopen gates still
apply. Any other ID change, or an ID shift that does not follow this rule, still
fails closed.

## Not covered

- This does not qualify insertion. Each lane still needs native acceptance in both
  languages under the relaxed comparison.
- Storage/declaration creation with insertion still needs its own captured
  composite effect contract.
- Production combined `library_integrate` apply and OB insertion stay disabled
  until separately qualified.
