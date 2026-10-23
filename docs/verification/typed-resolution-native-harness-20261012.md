# Executable typed resolution qualification harness

This increment adds `scripts/qualify_typed_resolution.py` and the reusable
`qualification_owned_scratch.py` helper. It turns the previously authored request
packet into a native engineering runner with fixture setup, complete native XML
comparisons, saved-project reopen and checkpoint recovery checks.

Harness development used offline checks. The subsequent combined-build run passed
one native LAD case, `tag-exact-owner`, as recorded below. Mock tests, authored SCL,
imported reference envelopes and compilation of a helper do not qualify other cases.

The first root-executed native attempt, `artifacts/p13/typed-tag-LAD-01`, stopped
during fixture setup before any qualification case. Its exact standard table had
zero tags and 47 generated system constants in `software_inspect`; the original
metadata filter incorrectly required no constants of any kind. Owned cleanup
completed. The corrected selector permits only `kind=system` constants in that
metadata, still requires the actual exported XML to contain no tag, user constant
or system constant, and imports a separate `Tgp_ResolutionTags` fixture table.
Nothing is stripped from the observed native envelope or changed in the default
table. The source table metadata and exported XML are retained in each report.
Retained native default-table exports establish that generated hardware constants
can appear in inventory while being absent from exported table XML. Attempt 01
remains immutable and proves no typed case.

The corrected `artifacts/p13/typed-tag-LAD-02/result.json` passed `tag-exact-owner`
with native apply/compile, independent topology and preservation checks, changed
project reopen, checkpoint recovery and recovered project reopen. All three exact
owned TIA lifetimes and their hosts/workers exited cleanly. The actual scratch
table's inventory and empty native XML were verified before fixture import.
Runtime verification remains false. Other standard cases, FBD, namespace and stale
phases still require native runs. See [combined evidence](parallel-next-20261012.md).

## Safest first run

Run from the integrated isolated tree, for example
`D:\03_Entwicklung\TIA_MCP\artifacts\p13\merged`. Select a completed immutable
host/worker build. The explicit short artifact policy below avoids expanding TIA
project paths beneath the longer isolated source path.

```powershell
python scripts/qualify_typed_resolution.py `
  --host '<frozen bundle>\host\TiaMcp.Host.exe' `
  --worker '<same bundle>\workers\openness\TiaMcp.Openness.V21.exe' `
  --artifact-policy-root D:\03_Entwicklung\TIA_MCP\artifacts `
  --artifacts D:\03_Entwicklung\TIA_MCP\artifacts `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p13\typed-tag-LAD-next `
  --phase standard --language LAD --case tag-exact-owner
```

The output directory must not exist. The runner creates a fresh short
`rXXXXXX` native artifact directory within the selected policy root. It creates a
new owned headless TIA instance and scratch CPU; it never attaches to an existing
project and exposes no simulator or physical PLC operations. The TIA V21 worker
must be admitted by local Openness authorization; a successful startup on another
binary does not prove admission of the selected worker. A constructor timeout
without an owned process handle is retained as uncertain cleanup and prevents
further startup in that run.

After the single positive case passes, use a new output directory and omit
`--case` to run all cases of the selected phase. Repeat with `--language FBD` for
the second language. `--case` may be repeated and never silently skips an unknown
or wrong-phase ID.

| Phase | Cases | Additional prerequisite |
|---|---|---|
| `standard` | Exact tag table, wrong tag table, two exact DB owners with different start values, wrong DB kind/member/type | None |
| `namespace` | Correct nested UDT namespace, wrong requested scalar type, absent nested member | Guarded UnitSeeder |
| `stale` | Duplicate-tag setup guard, changed tag address, competing DB identity and stale apply | UnitSeeder for `stale-db-ambiguity` only |

## Namespace and stale DB setup

Build the existing `scripts/native/UnitSeeder/UnitSeeder.csproj` in the canonical
repository tree if necessary; compilation alone does not start TIA. Pass its
explicit executable with `--unit-seeder`. Its existing guard requires both its
authorization file and the fresh scratch project to remain under that seeder's
repository `artifacts` directory. For the short policy shown above, select the
seeder built in `D:\03_Entwicklung\TIA_MCP`, not one rooted in the isolated tree.
The runner records its hash, exact owned process/project authorization, output and
exit code. One bounded invocation establishes software units; timeout/unknown
outcomes are never replayed. The production MCP prepare/apply routes create every
tag, DB, UDT and graphical caller after unit setup.

For example, append the following arguments to the common host/worker/artifact
arguments, with a fresh output directory:

```powershell
--phase namespace --language LAD --case nested-udt-namespace `
--unit-seeder D:\03_Entwicklung\TIA_MCP\scripts\native\UnitSeeder\bin\Release\net48\UnitSeeder.exe
```

The namespace fixture puts the caller in `TiaMcp_UnitA` and an outer/inner UDT pair
in `TiaMcp_UnitA.Alternate` within the same software unit. A same-name decoy inner
UDT in `TiaMcp_UnitA` has an Int field, while the intended inner UDT has Bool. The
runner requires the exact intended type dependency paths. It uses the retained
unit-native caller envelope, adds the explicit UDT input and preserves its native
network envelope. This fixture has not yet passed native import or compile.

The DB ambiguity case first prepares a unique standard DB reference, then creates
an independently observed second DB with the same name in software unit B using
the supported source route. It requires a fresh typed prepare to reject
`AmbiguousOperand`, the original apply to reject `StaleRevision` or
`StaleDeclaration`, and complete preservation of the changed baseline. It then
restores and freshly reopens the deliberate fixture change's original checkpoint.
If native setup cannot create the competing identity, the run is blocked; no
production guard is bypassed.

Production tag imports reject same-scope duplicate names before mutation. The
`tag-duplicate-setup-guard` case verifies that `TagScopeMismatch` rejection and
unchanged full native XML. It explicitly reports `nativeAmbiguityQualified=false`.
The separate `stale-tag-address` case actually changes the selected tag address
through `tag_set`, verifies the native address, and exercises stale rejection and
checkpoint recovery. This is stale-declaration evidence, not duplicate-tag
ambiguity evidence.

## Evidence and stopping behavior

Each positive case verifies:

1. Exact operand identity, type and UDT dependency selection at prepare.
2. Independent Boolean access/wiring expectations and complete caller XML outside
   the selected `FlgNet`, before dispatching the typed apply.
3. Native apply/compile/save, complete prepared-versus-native caller XML, and all
   unrelated block/type/tag-table XML.
4. Checkpoint restoration, saved changed-project reopen, and recovered-project
   reopen in fresh owned TIA lifetimes.

Each native call is journaled before dispatch and after its response. Reports bind
the selected binary dependencies, harness files, source fixtures and retained XML
to hashes. Exports use exact path keys, preserving duplicate short names. Only
export provenance and insignificant serializer indentation use the existing
normalization; native IDs, comments, titles and declaration/start-value content
remain compared.

The helper captures a read-only process handle immediately from each successful
owned start. Detach, host exit, worker exit receipts and that exact native handle's
exit are all required. It never kills a Siemens process or reopens a PID. Failed
cleanup blocks subsequent sessions. A dispatched checkpoint restore is recorded
before the call; even a partial restore failure or lost response allows only
status/readback and cleanup, never a second restore dispatch. Physical runtime
verification remains false throughout.

## Component integration harness support

`OwnedScratch` can be reused by the component fixture harness with explicit
`extra_tools` for its library operations. `snapshot(call, directory)` and
`assert_snapshot(before, after, except_paths=...)` compare every block, UDT and tag
table using exact identities. Component coordinators must independently compare
each explicitly changed/created object's expected artifact, and explicitly adopt
any verified recovered project receipt before reopening it. The typed runner does
not qualify library imports, component orchestration, instance behavior or timers.

Offline checks:

```powershell
python -m unittest discover -s scripts -p 'test_typed*.py'
```

The p13 corrected development result is **48/48** passing typed Python tests, including 15
focused harness tests. They cover exact ownership, cleanup uncertainty, complete
peer preservation, namespace decoys, native Boolean wiring, untouched caller XML,
short artifact containment and partial/lost restore responses without replay.
The added regression covers the native inventory/export difference, rejects user
and unknown constants or foreign scopes, prevents fixture table replacement, and
keeps XML emptiness strict for every constant kind.
