# Typed resolution: p14 qualification continuation

This fixture increment retains the existing exact identity, complete native XML,
fresh reopen, checkpoint recovery and owned-process cleanup checks. It adds a
complete address-only XML oracle for the deliberate `stale-tag-address` fixture
change and makes every unverified stale apply terminal for further mutations.
It changes no production C# source, native instruction profile or storage route.

## Native execution commands

Run these commands from the isolated tree containing the corrected p14 harness,
for example `D:\03_Entwicklung\TIA_MCP\artifacts\p14\fixtures`, or from the
root-integrated merged tree. Keep the p13 source/evidence immutable. The selected
p13 binaries below are the exact bundle already used by the passed p13
`tag-exact-owner` LAD report. The runner hashes their dependencies on every owned
lifetime. Native executions are serial and each output directory must be new.

```powershell
$typedHost = 'D:\03_Entwicklung\TIA_MCP\artifacts\p13\merged\src\TiaMcp.Host\bin\Release\net10.0-windows\TiaMcp.Host.exe'
$typedWorker = 'D:\03_Entwicklung\TIA_MCP\artifacts\p13\merged\src\TiaMcp.Openness.V21\bin\Release\net48\TiaMcp.Openness.V21.exe'
$typedCommon = @('--host', $typedHost, '--worker', $typedWorker,
  '--artifact-policy-root', 'D:\03_Entwicklung\TIA_MCP\artifacts',
  '--artifacts', 'D:\03_Entwicklung\TIA_MCP\artifacts')

# Remaining standard LAD cases; p13 already qualified tag-exact-owner in LAD.
python scripts/qualify_typed_resolution.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p14\typed-standard-LAD-next `
  --phase standard --language LAD `
  --case tag-wrong-table --case db-first-owner --case db-second-owner `
  --case db-wrong-kind --case db-missing-member --case db-wrong-type

# All seven standard FBD cases.
python scripts/qualify_typed_resolution.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p14\typed-standard-FBD-next `
  --phase standard --language FBD

# Tag collision guard and stale address; these do not need software-unit setup.
python scripts/qualify_typed_resolution.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p14\typed-stale-tag-LAD-next `
  --phase stale --language LAD `
  --case tag-duplicate-setup-guard --case stale-tag-address
```

Repeat the last command with `--language FBD` and another new output directory
when FBD stale-address evidence is needed. Do not omit both stale `--case`
arguments unless the guarded unit seeder is supplied: the full stale phase also
includes the separate competing DB fixture.

## Independent expectations

| Case | Required native result |
|---|---|
| `tag-exact-owner` | Exact table-owned Bool `Ready`; global symbol `Ready`; full positive gates |
| `tag-wrong-table` | `OperandNotFound` from the observed other table; every object unchanged |
| `db-first-owner` | Exact `Tgp_ResolutionOne.Flag`; emitted global components `Tgp_ResolutionOne`, `Flag`; full positive gates |
| `db-second-owner` | Exact `Tgp_ResolutionTwo.Flag`; emitted global components `Tgp_ResolutionTwo`, `Flag`; full positive gates |
| `db-wrong-kind` | `OperandOwnerMismatch` for the exact FC used as a DB owner; every object unchanged |
| `db-missing-member` | `OperandNotFound` for `MissingFlag`; every object unchanged |
| `db-wrong-type` | `OperandTypeMismatch` for requested Int versus native Bool; every object unchanged |
| `tag-duplicate-setup-guard` | `TagScopeMismatch` before mutation; native tag ambiguity remains unqualified |
| `stale-tag-address` | Deliberate native `%M100.0` to `%M100.1` change only, stale apply rejected, unchanged post-rejection XML, baseline checkpoint recovered and freshly reopened |

Both DBs have a Bool `Flag` and distinct start values; full DB exports remain part
of every preservation comparison. LAD requires the retained Contact/Coil topology
and power rail. FBD requires the retained input-to-Coil assignment topology. The
offline oracle is checked against independently retained native FC envelopes in
both languages, including wrong scope and wrong DB-owner counterexamples.

The stale address oracle constructs its expected XML from the complete before
export, changes only the exact `Ready` tag's `LogicalAddress`, and compares the
entire resulting table XML. It independently compares every other block, UDT and
table. Comments, IDs, unknown XML and every other declaration field remain strict.

Before stale `change_apply`, the report durably records the exact change ID and
idempotency key. Only an explicit `StaleRevision` or `StaleDeclaration` rejection
allows restoration of the deliberate fixture change. Lost response, unrelated
error, partial failure, or unexpected success records status for the attempted
typed change and suppresses all checkpoint restoration in that run. A restore
already dispatched remains non-replayable under the existing receipt guard.

## Namespace and competing DB follow-up

The existing authored sources and guarded seeder contract were reviewed offline;
this does not establish native import or compilation success. After standard
qualification, a bounded first namespace command is:

```powershell
python scripts/qualify_typed_resolution.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p14\typed-namespace-LAD-next `
  --phase namespace --language LAD --case nested-udt-namespace `
  --unit-seeder D:\03_Entwicklung\TIA_MCP\scripts\native\UnitSeeder\bin\Release\net48\UnitSeeder.exe
```

Use the canonical-repository seeder so its artifact-root guard matches the short
artifact policy. The seeder is invoked once against the exact owned scratch
receipt; the production prepare/apply routes create the typed fixtures. Nested
UDT resolution must select the Bool inner type in `TiaMcp_UnitA.Alternate`, not the
same-name Int decoy in `TiaMcp_UnitA`. Failure to establish those native identities
blocks the case. No profile or ownership guard is relaxed.

## Development evidence and scope

The first root-executed stale LAD attempt,
`artifacts/p14/typed-stale-LAD-01/result.json`, passed the duplicate setup guard
and observed the native `StaleRevision` response, but report replacement failed
with Windows error 5 before the runner accepted that response. The run remained
blocked: status showed the typed change still prepared with no recovery archive,
no restore was dispatched, and exact owned cleanup completed. It does not prove
stale-address recovery. The holder causing the file replacement failure is not
attributed. Its six reported source/fixture inputs were archived and individually
hash-verified in `source-snapshot/manifest.json` before this correction; the
original report remains unchanged.

Report persistence now calls the existing `run_block_tests.save_json` helper. It
retries only replacement of the same report's same-directory `.pending` file,
only for Windows errors 5, 32 or 33, within the existing two-second budget. The
helper is included in the report's harness hash manifest. No native dispatch is
inside that retry. Other errors or an exhausted budget remain terminal, retaining
the prior report and pending evidence. A new stale run must use a fresh scratch
and output, for example `typed-stale-LAD-02`; attempt 01 is never resumed.

`python -m unittest discover -s scripts -p 'test_typed*.py'` passed **53/53** tests,
including **20** focused harness tests. Added regressions cover exact address-only
XML, unrelated-object drift, retained LAD/FBD topology, durable stale-dispatch
intent and status-only handling of uncertain or unrelated outcomes. Both exact
stale rejection codes restore the deliberate checkpoint once in mocked tests.
Persistence integration tests inject transient Windows replacement failures after
the native response and verify the exact stale rejection survives with a single
dispatch. A nonretryable replacement failure retains the last report and pending
native response. The existing persistence-helper tests separately check bounded
retry exhaustion and same-file scope.

The fixture agent ran no native process or MCP operation. The p13 LAD exact-tag
case is backed by `artifacts/p13/typed-tag-LAD-02/result.json`; subsequent root-run
p14 results must be assessed from their own retained reports. Runtime/physical
PLC verification remains false, and neither offline tests nor these commands
qualify namespace, duplicate-tag ambiguity, DB ambiguity or component behavior.
