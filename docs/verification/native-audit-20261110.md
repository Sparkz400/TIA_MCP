# Native confirmation of the 9 November audit paths and the I/O analysis tools: runs of 10 November 2026

Two runners were extended and run on PCNico, one at a time, on owned scratch projects that each runner created through
the MCP Host, from frozen Debug builds (`git archive`; Host and Openness worker). No PLCSIM instance was used.

| Evidence | Runner | Frozen build | Result |
| --- | --- | --- | --- |
| `local.read_contracts.20261110` | `scripts/qualify_read_contracts.py` | `01b8c0f3` (`artifacts/nc3`) | 187/187 steps (`rc_14fd3c22`) |
| `local.plc_object_mutations.audit.20261110` | `scripts/qualify_plc_object_mutations.py` | `807fa628` (`artifacts/nc4`) | 390/390 steps (`x11_e0965210`) |

## I/O analysis tools

`block_export`, `plc_io_map` and `plc_tag_usage` had offline tests only. The read-contract scratch gained two
DI 16x24VDC HF (`hardware_plug`), a tag table `RcIo` with three Bool tags (`tag_table_import_xml`) and two SCL
functions (`source_upsert`).

- `block_export`: the FB/FC/OB selection and the DB selection each equal `software_inspect`'s set, with no skipped row;
  every native XML has its indexed size and holds its block's name; `namePrefix` narrows to one block; `maxBlocks: 1`
  returns `nextOffset` and is not complete; an unknown project is refused (`ProjectNotFound`).
- `plc_io_map`: ranges and their tags equal an oracle computed from `hardware_inspect` and `software_inspect`; the
  second module is a range without tags; `%Q200.0` and `%I200.0` are tags without listed hardware; `PlcNotFound`,
  `AmbiguousTarget` without a selector on two PLCs, and `complete: false` with two PLCs.
- `plc_tag_usage` over the export index: `RcIn` is read by `RcIoRead N1`, `RcOut` written by `RcIoWrite N1`, `RcSpare`
  unused; a one-block index finds no reader; `unusedOnly`; `PlcNotFound`.
- `isModified` was the same before and after the reads.

**Finding, not a defect:** V21 replaces an absolute SCL operand on import. The source read `%I0.1`; the project then
held a tag `Tag__...` at that address, which `plc_io_map` listed and `plc_tag_usage` reported with its reader. An
absolute access was therefore not produced natively, and that branch of `plc_tag_usage` keeps offline coverage only.

The three features are recorded as partial: the fixture has no output module, distributed I/O, drive telegram, shared
address, LAD/FBD/STL network or protected block, and the export index carries a size but no hash per file.

## Change-pipeline fixes of the audit

- **`not_applied` after a failed checkpoint:** a directory at the change's `before.zap21` made `Project.Archive` fail
  ("Archive Operation is not possible as the target file/folder ... is already exist"). The change ended `not_applied`
  with that failure, the stages `prechange_project_saved` and `change_finished_with_failure`, no archive hash and the
  PLC software unchanged. The next change on the project prepared and applied.
- **Intent decision log:** a decision of an applied change is written to `intent-decisions.jsonl`; with a half-written
  line appended, a prepare repeating the decision names it `previouslyConfirmed`, and a contradicting one reports a
  conflict with one warning. Applied while the runner held a lock on the log, the contradicting change still ended
  `applied` with one `intent-log-write-failure` readback and the log unchanged.
- **Hashed preview payload:** a `source_upsert` preview carries its source as `{bytes, sha256}`, equal to the persisted
  input and the journal. A wrong `previewHash` is refused (`PreviewMismatch`); with bytes appended to the input file the
  apply is refused (`ArtifactChanged`), the change stays `prepared`, and after restoring the file it applies.

### Finding: a `not_applied` apply could not be replayed

The first attempt (`x11_b95feca9`, build `01b8c0f3`) passed 213 steps and reached `not_applied`, then the replay of that
apply with the same idempotency key was refused with `RecoveryRequired`. `ChangeApply` answers every resolved outcome
on replay, but mutation admission in front of it accepted a replay only for state `applied`. Admission now uses the
same rule (`807fa628`); the rerun's replay returned `not_applied`.

## Runner corrections on the way

- `rc_b19aaea1` (build `a4dc11f3`) stopped at step 22: the shared native harness read a journal that the summary result
  of the change tools (default since 9 November) no longer carries. The harness now asks for full views.
- `rc_71068bac` (build `e1120de9`) stopped at `plc_io_map` after 125 steps: the runner expected the absolute operand to
  stay absolute (see the finding above).

All four stopped attempts closed their Portal and are kept beside the passing runs.

## Not covered

- A decision log that is read-only or occupied by a directory raises `UnauthorizedAccessException`, which the
  apply's `catch (IOException)` does not handle; not exercised and not changed.
- Whether an idle OPC UA session survives a `runtime_plcsim_bind` (600 s requested since 10 November) needs a run in
  PLCSIM's TCP/IP Online Access mode with the connect before the bind.
