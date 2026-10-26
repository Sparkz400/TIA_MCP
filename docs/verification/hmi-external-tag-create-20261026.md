# Unified external PLC tag creation — 26 October 2026

**Status:** implemented and offline-tested. Not natively qualified. Production
apply stays unavailable behind the Unified preservation gate.

## What was added

`hmi_engineering` operation `external_tag_create` creates one Unified HMI tag on an
existing integrated connection, symbolically bound to one root PLC tag-table tag.
Payload: [hmi-engineering.v1.schema.json](../../contracts/hmi-engineering.v1.schema.json).

- **Request rules** (`HmiEngineeringRules`): Unified only. The target is an exact tag
  table. `connection` is an exact `hmiConnection` identity on the same
  project/session/HMI. `plcTag` is an exact PLC `tag` identity, as returned by
  `hmi_references`; software-unit scopes and extra (evidence) fields are refused.
  `dataType` is one of the primitive scalar types. Other operations refuse
  `connection`/`plcTag`.
- **Prepare** (worker): refuses an existing HMI tag name. Proves the connection's PLC
  owner the same way `hmi_references` does: exactly one valid native
  `CommunicationManagement` endpoint pair on this HMI owner whose partner is exactly
  one PLC in a completely traversed project. The PLC tag must be in that PLC's root
  tag tables, have an unquoted one-component symbol and the exact requested type.
  Same names never stand in for this proof.
- **Apply** (worker): `Tags.Create`, then `Connection`, then `PlcTag`. HmiSeeder
  observed this order working natively; the symbolic setter derives
  DataType/PlcName, and native refuses later DataType writes. Readback must match
  name, connection and PLC tag, the derived type, `SymbolicAccess`, and the PLC name
  when one is reported (`HmiRecipeRules.RequireExternalTagReadback`).
- **Preservation delta**: tag creation may change or add files only under the target
  table's export (`HmiPreservationRules.RequireUnrelatedPreservedOutside`); every
  other artifact must stay identical. This also fixes `tag_create`: its old
  comparison used the table path, which never matches an export key, so a passing
  apply could not verify preservation.

Not mapped: DB members, arrays, strings, UDTs, software-unit tags, absolute
addressing, creating connections, and Classic external tags. The installed V21
`Siemens.Engineering.WinCCUnified.xml` exposes no HMI-owned UDT type or
composition, so HMI-owned UDTs have no public API route.

## Offline results

| Check | Result |
| --- | --- |
| `dotnet run --project tests/TiaMcp.Hmi.Engineering.Tests/...` | 23 passed (4 new: request shape, field smuggling, readback, export delta) |
| `python -B -m unittest test_hmi_qualification` (in `scripts/`) | 56 passed (3 new probe tests with a fake worker) |
| V21 worker build against the installed SDK | succeeded, 0 warnings |
| `scripts/Test-Offline.ps1 -OutputDirectory artifacts/od165aa` | passed (exit 0): 2,021 Python tests (1 skipped), every managed suite (`TiaMcp.Tests` 249/249), final aggregate 2,096 checks |

The first full run used the default output directory and failed 2 of 249 in
`TiaMcp.Tests`. Under this long worktree path, simulation-checkpoint archive
staging went past TIA's 143-character limit. A shorter output directory passes.

The fake-worker tests check the probe's own logic only. They do not show what the
native body does.

## Native qualification step (not run)

The TIA machine was busy with the lane A corpus. Run this after the machine is
free, on a commit with no uncommitted changes:

```powershell
python -B scripts/qualify_hmi.py --native-report <fresh-unified-seeder-report.json> `
  --host <exact-packaged-host.exe> --worker <exact-packaged-worker.exe> `
  --report artifacts/<new>/hmi-bridge-external-tag.json --timeout 600 --probe-external-tag-create
```

Use a fresh positive Unified Comfort or Unified PC `HmiSeeder` fixture: two HMIs, two
PLCs, `LinkedSignal` → `SharedSignal` on integrated connections. For each proven link,
`--probe-external-tag-create` sends four `change_prepare` requests and never applies:

1. Valid request: must be refused with `HmiPreservationUnavailable`. Its
   `proposedEffect` must name the exact PLC owner, `SharedSignal`, the PLC type and a
   valid single-partner endpoint proof.
2. Existing name `LinkedSignal`: `HmiNameConflict`.
3. Different type: `TargetIncompatible`.
4. The other PLC's `SharedSignal`: `DependencyUnresolved`.

The owner's tag inventory must be unchanged afterwards. The result is
`profiles[].externalTagCreatePrepare`, outside the acceptance `GATES`. A pass
qualifies prepare-time ownership, declaration and type proof only. Apply, compile,
save/reopen, recovery, preservation and PLC communication remain unqualified until
Unified has a complete preservation capture.
