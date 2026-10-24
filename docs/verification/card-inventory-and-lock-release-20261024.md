# Post-RUN card inventory and project-lock release — 24 October 2026

Both are established from the frozen runtime corpus
(`artifacts/block-tests/frozen-20261024-04/`) and its 65 retained simulator
storages. No extra native run was needed.

## Post-RUN memory card

Each card was verified against its original manifest after download and again
after provisioning restored it, before normal power-on. After RUN, all 65 cards
differ from that manifest in **one identical pattern**:

- Added: `RData/retain_user`, `retain_system`, `retain_ext`, `retain_motion`,
  `retain_aslog`, plus new object-store generations `OMSSTORE/<id>/HIERARCHY_0`,
  `HIERARCHY_2` and three new object files.
- Removed: `OMSSTORE/<id>/HIERARCHY_1` and one superseded object file.
- Changed: `OMSSTORE/CONSISTENCY_1`.
- No `.info` file exists on any card.

This is PLCSIM writing retentive memory and rotating its object store during
RUN. It is deterministic, so it is runtime state: recorded with its inventory,
never compared, under the semantic preservation rule. Preservation itself holds:
- the card manifest is verified before power-on;
- the complete PLC exports are compared with the qualified revision before
  every case;
- project files change only through the receipted generated-state save and
  provisioning.

The `.info` addition seen earlier was observed on the VM; it is not reproduced
on this PC and stays open there.

## Project-lock release

Every one of the 65 cases opened the same saved scratch project with
`project_open` in a fresh owned TIA process. Each open came after the previous
case's TIA, worker and Host exits had been verified, and none hit a lock error.
On 8 October, failures of exactly this kind ("project already open") broke the
corpus. Normal TIA shutdown therefore releases the project lock, and a native
reopen works.

Evidence: `evidence/local.runtime.card-and-lock.20261024.json`.
