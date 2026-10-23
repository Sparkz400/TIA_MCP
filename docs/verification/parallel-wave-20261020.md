# Parallel C01, C04 and runtime pressure increment — 20 October 2026

This increment delivers three bounded offline slices: component-bound Unified
Bool text-list generation/regeneration, retained diagnostic experiment
composition, and runtime capture pressure qualification. It does not complete
C01, C03, C04, D16 or the full 44-workstream scope.

## Implemented scope

- [C01 generation](hmi-bound-generation-20261020.md): the existing author tool
  accepts an exact saved v7 component/output/Bool-binding selection. Generation
  and regeneration retain paired YAML, explicit translations, predecessor
  lineage and exact manual-peer bytes. Conflicts suppress candidates.
- [C04 experiments](diagnostic-experiments-20261020.md): two offline tools join
  original bug/alarm/Test Suite evidence to watch/trace plans and imported
  before/after measurements. Original sources, trace membership and explicit
  direct replan mappings are revalidated. This does not establish acquisition,
  causality or a verified fix.
- [C03/D16 pressure](runtime-pressure-20261020.md): production poller/store tests
  exercise 32,768 mixed-signal attempts, skipped scheduling slots, worker
  saturation, cancellation and reservation recovery. An independent retained
  byte/chain/value/schedule oracle rejects seven mutations.

The Host exposes 236 tools and 56 mutation kinds; the catalog contains 310
features. New feature entries remain partial and unverified for native use.
The aggregate and relocated package runners include both new MCP workflows;
the aggregate/workstation runners include the independent pressure oracle.

## Frozen validation

The final implementation snapshot is `artifacts/pw20src3`, containing 2,495
files. Its adjacent manifest SHA-256 is
`453a6f714030ece30b67aae970e73e30f27f758d8c93ad7cbaa859b64b26a7ef`.
Earlier `pw20src` and `pw20src2` snapshots precede coordinator repairs and are
not the final aggregate source. This verification note and final status updates
are written after the implementation freeze.

All validation passes from that same implementation snapshot:

| Validation | Result | Evidence under `artifacts/pw20src3/artifacts/` |
| --- | --- | --- |
| Full aggregate | 44 managed suites; 1,569 Python tests with one existing skip; 25 Explorer model/client checks; contracts, source bindings and every Host workflow | `all-01/result.json` |
| Actual browser fixtures | 38 checks; no page errors or network requests | `browser-01/browser-results.json` |
| Offline package and relocation | All 40 stages; strict verification of 1,797 files before and after relocation | `package-01/result.json`, `package-01/independent-summary.json` |

Both aggregate and package runs pass the new generation MCP workflow (34
checks), new diagnostic experiment MCP workflow (57 checks), existing HMI MCP
workflow (1,102 checks) and original v1–v5 HMI compatibility (1,208 checks).
Runtime MCP counts include repeated concurrent observations: this aggregate
records 1,472 checks and the package records 1,485; each also passes 62 actual
prior-capture compatibility checks. These counts are execution observations,
not a fixed test-count contract.

The verified bundle is
`artifacts/pw20src3/artifacts/releases/tia-mcp-offline_20261020_074047_74b96e1d`.
Its manifest SHA-256 is
`4f273409272da9dd3e487dceba1a4e4254ac68693727779800338231840c2cd4`;
its ZIP SHA-256 is
`35a17313438d92ccaca7e41ece5047bd2d8d44550b7c95a970889756ea5b2846`.
`artifacts/parallel-wave-20261020/final-verification.json` binds the final
receipts, exact Host/Core/helper binaries, archive and reporting files, and
rechecks all frozen files against the working implementation. Only the three
status/queue documents and this verification note differ after the freeze.
`git diff --check` passes. The package is a validation artifact; native release
acceptance remains false.

Focused results include 71 generation checks, 34 generation MCP checks,
41 diagnostic groups with 102 new experiment checks, 57 experiment MCP checks,
71 pressure checks, 37 portable HMI admission checks and 31 coordinator tests.
The native HmiSeeder helper builds against the installed SDK with zero warnings
and errors at `artifacts/parallel-wave-20261020/hmi-seeder-build02/`.

The actual historical v1 author compatibility run at
`artifacts/c01-generation-20261020/prior-author-01/verification.json` passes 40
checks across two fresh Hosts. It opens three original valid records and rejects
three forged variants; all 102 original and 102 copied files remain unchanged.
The requested October 18 corpus was unavailable, so the retained October 19
producer corpus is explicitly identified instead. Diagnostic compatibility
covers six original cases, five imports and 44 unchanged historical files, as
described in its lane verification. The root retained-evidence audit at
`artifacts/parallel-wave-20261020/retained-evidence.json` matches all 112 available
references; omitted historical artifacts in a source snapshot remain unavailable.

## Native fixture coordination

The user's earlier HMI editor pause was explicitly lifted for prerequisite
settings on new owned copies. The initial uncommitted preparation/admission
work was preserved under `artifacts/parallel-wave-20261020/initial/` before
integration. Preparation now checks positive exact fixture provenance,
unchanged archives, recovery evidence and current store ownership.

Two attempts remain failed:

- `artifacts/hmic20`: the old coordinator incorrectly expected one native
  process, although the owned editor had compiler/background children.
  The helper refused the abort command before capture, save or archive.
- `artifacts/hmic20b`: the repaired tree collector incorrectly treated an
  already-retained startup helper exiting during a repeated identity read as
  missing capture. The new bound `abort-close` command closed the owned editor
  before capture, save or archive.

Both attempts retain original process-capture, native result, reconciliation and
holder-release receipts. Reconciliation duplicated the coordinator's original
kernel process handles and verified the recorded birth identities and terminal
states before releasing only the stalled Python holder. All four original
targets in the first attempt and eleven in the second had already exited.
No target was terminated; unrelated TIA sessions were left untouched. Neither
attempt is converted into a pass.

The final collector distinguishes the exact main editor from a bounded owned
tree, retains late descendants, and uses the birth-matched original handle to
resolve a repeated-enumeration exit race. PID reuse, missing birth identity and
uncaptured lifetimes still refuse. Explicit `abort-close` validates the same
review/process/project binding as capture. Simulated checks do not establish
successful native configuration.

In `artifacts/hmic20c`, the user set and saved `Overview_A` as the start screen
for both `HMI_RT_1` and `HMI_RT_2`. The native helper then completed, capturing
complete diagnostics with zero errors and zero warnings for both owners and
`native/configured.zap21` with SHA-256
`e6f1ae79c105d5fd473399d121f80d75e86ce494a8370dab486c6e5b6b3f3498`.
The source archive/report remain unchanged. `export-preservation.json` verifies
all ten paired collector-returned screen/tag XML exports with the existing
Classic policy: only `Document/DocumentInfo/Created` is normalized, with the XML
declaration and remaining nodes compared exactly. This is not whole-project
preservation or fresh-reopen evidence.

The coordinator still failed its complete-tree gate: a short-lived
`Siemens.Automation.Diagnostics.Report.exe` (PID 38560) exited before its original
handle was retained. All eleven captured original process objects were verified
terminal, and only the stalled Python holder was released. Current reporter PID
absence is supporting quiescence evidence, not proof of that original lifetime.
`coordinator-reconciliation-final.json` hash-binds the receipts and explicitly
limits the earlier generic release wording to those eleven captured objects.
Complete owned-tree exit and native qualification remain false. The configured
archive is retained as helper-produced prerequisite evidence; the coordinator
attempt is not a pass.

The root native queue is stopped at this unresolved capture boundary; Unified
settings were not dispatched. `coordinator-quarantine.json` records the gap but
is documentary, not an admission guard enforced by `StoreExclusiveWindow`.
Reacquiring a lock alone cannot resolve it. The user authorization to resume
prerequisite editor work remains valid.

The refreshed file-only Unified packet at
`artifacts/parallel-wave-20261020/hmi-unified-preparation-final/preparation.json`
binds the final helper and original Unified resource fixture. It grants no
admission and records no native dispatch. The packet's authorization step is
already satisfied by the user's explicit resume; the unresolved tracking gate
still prevents this coordinator from dispatching the editor step.

## Remaining gates

Actual disk-full testing could not execute: both bounded 64 MiB VHD attempts
failed at `Mount-VHD` with Windows `0x80070522` (required privilege unavailable).
Both VHDs remain detached. No existing volume was filled and no elevation was
attempted. Application capacity refusal and an accelerated manual-clock test
do not establish real disk, memory, handle or long-duration OS pressure.

Successful editor prerequisite capture still needs independent fresh-process
reopen/compile and archive recovery evidence. The existing repair helper marks
those gates open; the production HMI engineering preservation gate remains
closed. Wider formats/templates, complete consumer coverage, native installation
and regeneration, exact online program/instance association, real acquisition
and wider device profiles remain separate work.
