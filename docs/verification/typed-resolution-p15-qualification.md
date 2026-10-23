# Typed resolution and direct instance dependency: p15 packet

This is an authored engineering qualification packet, not native acceptance.
It changes no production C# code, instruction profiles, schemas or support gates.
The previous reports and their hashes remain unchanged. Runtime remains unqualified.

## New direct-instance fixture

`scripts/qualify_typed_multi_dependency.py` uses the retained native LAD/FBD
`Tgp_FB_multi_*` caller and its existing `Child` instance of the scratch FB
`TiaMcp_EnableGate`. It copies only the independently captured `RetainedMarker`
declaration from `instance-effects/before-caller.xml` into the caller. The
declaration default is **17**. An independently authored SCL source then creates
a direct caller instance DB whose `RetainedMarker` start value is **42**.
The harness exports the complete baseline and requires both distinct values and
the exact native InstanceDB owner before typed preparation.

The actual change uses the existing `createMulti` request to append a fresh
instance and replace the selected network with a call to it. It does not add an
OB call. Before apply, the independently observed existing same-type `Child`
declaration specifies the expected new declaration, with only its name changed.
The oracle removes that single appended member and requires all previous DB XML
to match. For the caller it separately compares all content outside the selected
FlgNet and the independent reference topology within the network. IDs, comments,
unknown XML, all old declarations, both marker values and every peer block, UDT
and tag table remain strict.

Preparation must report exactly the existing direct DB as its dependent native
recompile effect, with a complete expected artifact. Both complete prepared
documents must match native readback after apply/compile/save. Recovery occurs
through the typed change's checkpoint; the saved changed project is then freshly
reopened, followed by a fresh reopen of the recovered baseline. All owned TIA
lifetimes, hosts and workers use the existing tracked cleanup receipts. The
report durably records exact change/hash/idempotency intent before apply; restore
dispatch is recorded before invocation and cannot replay. An uncertain operation
uses the existing status/recovery handling and never redispatches that mutation.

## Competing DB and stale cases

The existing `stale-db-ambiguity` fixture now requires that deliberate setup add
exactly one observed `TiaMcp_UnitB.Tgp_ResolutionOne` GlobalDB and delete nothing.
Its Bool `Flag=false` and Int `RetainedMarker=91` must match the authored source.
Every baseline object must remain unchanged. The fresh `AmbiguousOperand`
prepare rejection now has its own complete unchanged snapshot before stale
apply. This is separate from `tag-duplicate-setup-guard`, which proves only the
production setup collision guard and leaves native duplicate-tag ambiguity open.

The stale-address uncertainty/rejection offline matrix now runs for both LAD
and FBD. Exact `StaleRevision`/`StaleDeclaration` permits one deliberate-change
restore; timeout, unrelated failure, partial failure or unexpected success allows
only status inspection and cleanup. Existing address-only/full peer XML checks
remain unchanged.

## Serialized native commands

Run from a frozen source snapshot after independent review. Select the same
explicit frozen host/worker build for the intended acceptance lineage. Each
output directory must be new. The root coordinator serializes all invocations;
the fixture author ran no TIA or PLCSIM operation.

```powershell
# Assign these to the explicit frozen build chosen by the coordinator.
$typedHost = '<absolute frozen TiaMcp.Host.exe>'
$typedWorker = '<absolute frozen TiaMcp.Openness.V21.exe>'
$typedCommon = @('--host', $typedHost, '--worker', $typedWorker,
  '--artifact-policy-root', 'D:\03_Entwicklung\TIA_MCP\artifacts',
  '--artifacts', 'D:\03_Entwicklung\TIA_MCP\artifacts')
$typedSeeder = 'D:\03_Entwicklung\TIA_MCP\scripts\native\UnitSeeder\bin\Release\net48\UnitSeeder.exe'

# Missing FBD stale-address engineering evidence; one bounded case.
python scripts/qualify_typed_resolution.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p15\typed-stale-FBD-01 `
  --phase stale --language FBD --case stale-tag-address

# First namespace probe. Run the two negative namespace cases in a new output
# only after inspecting this native fixture establishment and cleanup result.
python scripts/qualify_typed_resolution.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p15\typed-namespace-LAD-01 `
  --phase namespace --language LAD --case nested-udt-namespace `
  --unit-seeder $typedSeeder

# Exact competing DB identities, fresh ambiguity rejection, stale rejection,
# restoration and recovered fresh reopen. No native duplicate-tag claim.
python scripts/qualify_typed_resolution.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p15\typed-ambiguity-LAD-01 `
  --phase stale --language LAD --case stale-db-ambiguity `
  --unit-seeder $typedSeeder

# Existing direct DB with a nondefault start value, then createMulti.
python scripts/qualify_typed_multi_dependency.py @typedCommon `
  --output D:\03_Entwicklung\TIA_MCP\artifacts\p15\typed-multi-LAD-01 `
  --language LAD
```

After reviewing each result, use a new output with `--language FBD` for the
namespace, competing DB and multi-instance fixtures. The namespace phase permits
`--case nested-udt-wrong-type` and `--case nested-udt-missing-member`, or all three
cases by omitting `--case`. Do not reuse an output or resume a failed native
operation. Keep the canonical-repository UnitSeeder path: its artifact guard is
bound to that repository location. These scripts have no runtime route.

## Offline validation

`python -m unittest discover -s scripts -p 'test_typed*.py'` passes **61/61**.
`scripts/Test.ps1` includes the new multi-dependency test module in its typed
suite list, so combined regression also exercises this coverage.
New tests cover both language fixtures, the independent append oracle against
marker resets, existing/new child drift, native IDs, comments, unknown XML and
wrong append order; the complete mocked apply/restore/reopen sequence; uncertain
apply status handling with no replay and recovery only for a resolved applied/
recovery-required status with a checkpoint; exact competing-DB identity/addition/
preservation; and stale outcomes in both languages.
All four native engineering gates and runtime remain pending for new cases until
the coordinator retains and independently audits actual native reports.

Namespace/UDT native acceptance, native duplicate-tag ambiguity, multi-instance
runtime state, numeric parameter behavior, timer/counter boundaries, advanced
unit routes and broader reference capture remain separate unfinished work.

## Namespace fixture numbering correction

The original `artifacts/p15/typed-namespace-LAD-01/result.json` stopped during
fixture setup with `BlockNumberCollision`. UnitSeeder had created FC2 and FC3;
the retained standard caller export still carried historical FC Number2. No
typed case executed. The exact owned TIA lifetime and its host/worker exited
cleanly. This report remains unchanged.

For the unit-seeded namespace and competing-DB phases, new caller fixtures now
use the existing supported `AutoNumber=true` creation form with the historical
`Number` omitted. The helper requires complete inventory, no existing same-name
block (case-insensitive), the exact expected FC/FB name and an observed
`AutoNumber=true` export.
It removes only the top-level block Number. It retains document IDs, all other
attributes and opaque XML; it never changes an existing block. Both authored
caller documents are retained in the run output. No production collision guard
changes. The ordinary non-unit standard/stale fixture route remains unchanged.

Offline coverage checks both retained standard FCs and generated nested FBs in
LAD/FBD, compares all XML outside the omitted Number, and rejects existing names,
wrong names, manual numbering and incomplete inventories. The corrected native
namespace attempt requires a fresh output, for example `typed-namespace-LAD-02`.

## Nested UDT source spelling correction

The separately retained `artifacts/p15/typed-namespace-LAD-02/result.json`
passed new caller numbering, standard fixtures and the parent-namespace Int
decoy UDT. Native generation then rejected the outer Alternate UDT's unqualified
`Child : "Tgp_ResolutionInner"` declaration as an unknown data type. No namespace
case executed. The exact failed change was inspected and restored once from
its checkpoint; owned cleanup completed. The original report remains unchanged.

The fixture source now spells that dependency
`Child : "TiaMcp_UnitA.Alternate.Tgp_ResolutionInner"`. After generation, and again
at case binding, the harness requires exact native inventory identities for
the Alternate Outer/Inner chain and parent-namespace Inner decoy, all in the
observed selected software-unit scope. Missing, duplicate, wrong-scope and
wrong-namespace identities block the fixture. The case still requires Bool at
`State.Child.Flag` and exactly the selected two UDT dependency paths.

The request packet and result scope explicitly limit this fixture to a fully
qualified nested chain. A pass will not qualify unqualified nested namespace
propagation; that remains supported by offline resolver tests only. New tests
bind the source's qualified declaration, selected/decoy member types and request
scope to those independent identity expectations. A native retry must use a new
output such as `typed-namespace-LAD-03` and the corrected frozen source snapshot.
