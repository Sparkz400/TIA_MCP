# Typed reference resolution: 12 October 2026

The native capture now shares its exact ownership, symbol uniqueness and UDT
selection decisions with an offline-testable resolver. The resolver keeps literal
member components distinct, selects one exact owner, and resolves nested UDT names
in the declaring UDT's namespace. Inline native member expansions cannot bypass
the independently selected type declaration or its dependency capture.

Apply-time checks now repeat tag symbol uniqueness over all tables in the selected
scope. DB operands record a `dbSymbol` dependency, and apply repeats the existing
complete-PLC DB-name uniqueness rule as well as exact owner, scope, kind and
namespace checks. A competing declaration introduced after preparation returns
`StaleDeclaration`, even if the originally selected declaration is unchanged.
Tag revalidation also rejects a declaration that has become a safety tag. Existing
project revision, artifact hash, declaration XML and native preservation checks
remain in place.

This adds no instruction, storage, software-unit tag, namespaced DB or cross-unit
route. In particular, native import/compile, persistence, recovery and simulator
qualification remain separate from the tests below.

## Offline verification

- The full C# suite passed **144/144** tests, including six new resolver tests.
  This includes **30/30** typed tests: 79 retained native network reads and 35
  captured instruction profile emissions remain covered by the existing corpus.
- The typed Python tests passed **33/33**, including five new packet binding tests.
- The native worker compiled with zero errors and warnings. Building does not
  attach to TIA or load a project.
- Retained LAD/FBD caller and InstanceDB declarations are compared in full before
  and after successful and rejected resolution. Authored namespace/duplicate-name
  cases test decisions and are not independent native fixture evidence.

Commands from the repository root:

```powershell
dotnet run --project tests/TiaMcp.Tests/TiaMcp.Tests.csproj -c Release
python -m unittest discover -s scripts -p 'test_typed_graphical*.py'
dotnet build src/TiaMcp.Openness.V21/TiaMcp.Openness.V21.csproj -c Release
```

## Prepared native case packet

`fixtures/typed-graphical/resolution-packet.v1.json` contains 12 authored cases:
exact tag ownership, wrong table, duplicate tag names, two exact DB owners with
the same member name, wrong DB kind, absent member, wrong type, duplicate DB
names, cross-scope DB rejection, nested UDT namespace selection, and the retained
unsupported software-unit tag guard.

The packet binder is offline. It emits actual `change_prepare` argument objects
using exact identities and network IDs from observations, and never starts a
native process or issues a mutation. Its output has `executed=false` and all five
qualification gates false. Fixture prerequisites are retained in each output
case; they must be independently established in the owned scratch before running
the request. In particular, duplicate-tag setup must not bypass existing import
collision guards. If the native object model cannot represent that prerequisite,
record the case as blocked rather than claiming a native rejection pass.

Provide an observations JSON file with:

- `inventory`: the complete `software_inspect` structured result.
- `bindings`: explicit observed paths keyed by roles used in the selected cases
  (`caller`, `tags`, `otherTags`, `dbOne`, `dbTwo`, `ambiguousDb`, `otherScopeDb`,
  `nestedCaller`, `outerType`, `innerType`, `unitTags`). Unused roles may be absent.
- `exports`: a mapping from the exact caller paths to their `block_read.xml`
  strings. Caller names must agree with the observed inventory.

The ordinary caller must expose a writable local `OutputBool`. Positive tag/DB
fixtures use `Ready` and `Flag`, respectively. The nested caller declares
`State : B.Outer` from namespace `A`; `B.Outer.Child` uses unqualified `Inner`,
`B.Inner.Flag` is Bool, and decoy `A.Inner.Flag` is Int. Use a Boolean route already
covered by the profile catalog. Refer to each case's explicit prerequisites.

```powershell
python scripts/typed_graphical_resolution_cases.py `
  --observations artifacts/resolution-observations.json `
  --case tag-exact-owner --case db-first-owner --case db-second-owner `
  --output artifacts/resolution-requests.json
```

For each native prepare run, verify the exact diagnostic or the resolved operand
identity/data type and recorded type dependency paths. Compare complete caller,
peer block, tag-table and UDT XML before and after preparation, including negative
cases. For each positive mutation, separately retain native compile, complete
prepared-vs-readback XML, save/reopen, checkpoint restore and recovered reopen
evidence using the established typed qualifier. Preparation alone cannot pass
those gates.

Two additional native scenarios remain to be authored against an independently
established archive: prepare a unique tag/DB operand, introduce a competing native
declaration through an owned supported fixture operation, and attempt the original
apply. The original prepared change must reject with `StaleDeclaration` or the
earlier project-level `StaleRevision`, preserve the new baseline in full, and retain
the deliberate fixture change's recovery evidence. Never weaken project freshness
checks to reach the narrower dependency diagnostic.

No native resolution packet or runtime assertion was executed in this workstream.
The existing native qualification ledger has deliberately not been changed.
