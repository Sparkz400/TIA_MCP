# Typed LAD/FBD builders

The additive `graphical_typed_edit` change kind accepts a versioned graph and
prepares complete native XML through the existing
`change_prepare` / `change_apply` / `change_status` / `change_restore` lifecycle.
It does not add a separate write API. Native import, mandatory compilation,
complete readback and checkpoint recovery remain independent of runtime tests.
The implementation spans LAD-010 through LAD-070. Qualification is a matrix of
recorded targets and operations, not a claim that every planned cell is complete.

Work stopped at the user's requested checkpoint. See the consolidated
[remaining-work TODO](../plans/typed-lad-fbd-todo.md) and the per-case
[qualification ledger](../verification/typed-lad-fbd-qualification.md) for exact passed gates,
binary lineages and outstanding acceptance criteria.

## Contracts and scope

The shipped contracts are `typed-graphical-model.schema.json`,
`typed-graphical-change.schema.json`, `typed-graphical-read.schema.json` and
`typed-graphical-profiles.schema.json` in `contracts/`. Schema version 1 uses
explicit logical keys, operand references, typed ports and directed connections.
Raw JSON validation runs before coercion: unknown properties, wrong JSON types,
nulls, contradictory placement and mixed existing/new-target selectors fail.
Restricted logical keys, native IDs, endpoints and new declaration/instance names
must match their entire grammar: trailing line breaks are rejected, never trimmed.
This restriction does not alter the separate quoted symbolic-component grammar.
See [boundary verification](../verification/typed-text-boundaries-20261022.md).

LAD has an explicit power rail, contacts (including normally closed), ordinary
coils and series/parallel flow. FBD has separate AND, OR, NOT and assignment
semantics. Both support profile-bound comparisons, FC/FB calls, TON/TOF/TP and
CTU/CTD/CTUD. There is no automatic language conversion. The initial comparison
and counter profiles use `Int`; timer profiles use `Time`. A matching native
profile is required for every language, instruction, type, CPU/firmware and route.
Presence of a profile is native format evidence, not behavioral qualification.

The first exact target is V21 Update 2, CPU
`OrderNumber:6ES7 511-1AL03-0AB0/V4.1`, FlgNet/v5 and Interface/v5.
`standard`, `softwareUnit` (the unit's namespace preset), and an explicit alternate
`namespace` are separate profile predicates. Unit/namespace Boolean and FC/FB
creation resources come from separate native imports. Uncaptured combinations
return `GraphicalProfileUnavailable` or a more specific creation/dependency
diagnostic. They do not fall back to a standard-route profile.

Added 27 October 2026 and **unqualified**: the model also accepts `compare` with
`DInt` or `Real`, `move` (one exact scalar `dataType`), `math` (`ADD`/`SUB`/`MUL`/`DIV`
with one exact numeric `dataType`), `timer` `TONR` and `edge` (`R_TRIG`/`F_TRIG`,
explicit `instance` storage like timers and counters). Every data pin, operand and
native template overload must carry exactly that type; other combinations fail with
`GraphicalComparisonUnsupported`, `GraphicalArithmeticUnsupported`,
`GraphicalMoveUnsupported`, `GraphicalInstructionUnsupported` or `OperandTypeMismatch`.
No native reference for these families has been captured, so the shipped catalog has
no profile for them and they return `GraphicalProfileUnavailable` until one is
captured and qualified. Profiles exist only for the CPU 1511 V4.1 target below.

Safety, protected/generated blocks, set/reset coils, P_TRIG/N_TRIG and edge
contacts/coils, pointers, indirect access, array indexing, symbolic array bounds and
unprofiled compound call parameters are outside the current typed subset.
Unsupported native instructions remain readable as opaque content.

## Prepare a change

Pass a wrapper payload with `model` and either an exact existing `blockPath` or
new-block placement. The following existing-block example expresses one LAD
contact and coil; obtain the real network ID and block path from a fresh read.

```json
{
  "kind": "graphical_typed_edit",
  "payload": {
    "blockPath": "<exact canonical block path>",
    "model": {
      "schemaVersion": 1,
      "language": "LAD",
      "operation": "replaceNetwork",
      "networkId": "<native compile-unit ID>",
      "operands": [
        { "key": "enable", "scope": "local", "components": ["Enable"] },
        { "key": "active", "scope": "local", "components": ["Active"] }
      ],
      "nodes": [
        { "key": "rail", "kind": "powerrail" },
        { "key": "contact", "kind": "contact", "operand": "enable" },
        { "key": "coil", "kind": "coil", "operand": "active" }
      ],
      "connections": [
        { "from": { "node": "rail", "port": "out" }, "to": { "node": "contact", "port": "in" } },
        { "from": { "node": "contact", "port": "out" }, "to": { "node": "coil", "port": "in" } }
      ]
    }
  }
}
```

Operations are `replaceNetwork`, `insertNetwork`, `editNetwork` and `createBlock`.
Insertion uses observed placement IDs; creation requires explicit FC/FB name and
declarations. V21 insertion into the captured normal block envelope reallocates
the existing block-title object IDs. That cell cannot meet the requested strict
unrelated-ID preservation gate and is rejected by an explicit preparation
diagnostic; the native mismatch and verified checkpoint recovery remain evidence.
Existing edits are bounded operand substitutions on supported
native contact/coil/assignment nodes, identified by `nativeUid`. A whole-network
replacement requires complete supported boundaries. Unknown connected content
or unsupported native attributes prevent destructive typed replacement.

Operands resolve against the actual caller interface, an exact tag-table owner,
or an exact DB/UDT member path. Component boundaries are retained in identities;
names containing dots are not treated as paths. Inputs/constants are not writable.
Tag and type collisions, ambiguous callees, wrong directions, missing arguments,
conflicting outputs and stale declarations fail explicitly. All call parameters
come from a fresh exact FC/FB interface; scalar in-outs require writable storage.

`payload.instances` explicitly selects storage by key. Supported request kinds
are `existingGlobal`, `existingMulti`, `createGlobal` and `createMulti`. Global
instances require exact native ownership. Multi-instances require exact Static
declarations in an FB. New declarations and DB documents come from captured native
templates, not fabricated IEC layouts. Shared storage requires explicit consent
on every node and graph dependencies establishing execution order.

The preview includes source/result hashes, profile IDs, resolved identities,
declaration dependencies, native artifacts, ID mapping, effects and separate
qualification fields. Direct instance-DB effects retain the complete original
document and start values; the expected document contains only the explicit new
member. Unprofiled transitive multi-instance propagation is rejected before apply.
Checkpointing saves all current in-memory edits in the selected owned project.
Apply is recoverable, not a multi-operation atomic transaction.

## Preservation and native IDs

The native document is the preservation envelope. Untouched interfaces, comments,
attributes, extension nodes, networks and their IDs remain in the prepared tree.
The original XML bytes are retained separately. Only the existing comparison
rules may ignore top-level export provenance and serializer indentation.

Object IDs are hexadecimal and document-scoped. Instruction/access/wire UIds are
decimal and network-scoped; equal UIds in different networks are legal. Allocation
reserves opaque definitions and numeric aliases. Structurally unchanged selected
networks retain native IDs and order. A changed topology may require native
export-order reallocation within that selected network; the preview records the
previous mapping and `selectedNetworkIdReallocation` effect. Native re-export is
compared against the complete prepared document; ID mismatches are failures.
Software-unit block creation retains the captured `AutoNumber=true` attribute
while supplying the explicitly reserved number: native trials confirmed that
the requested number survives import/export, whereas forcing `false` is rejected.

Preparation captures dependencies and immutable profile resources. Apply checks
their hashes and fresh native declarations before checkpoint/mutation. It reads
back the entire caller, created instances and captured block dependencies after
compilation. A mismatch retains the failed change and recovery checkpoint; it is
never turned into a pass by discarding attributes, IDs or unknown XML.

## Read-only explorer integration

Call `engineering_read` with **all arguments inside `selectors`**:

```json
{
  "selectors": {
    "scope": "graphical",
    "blockPath": "<exact canonical block path>",
    "maxItems": 100,
    "includeNativeXml": false
  }
}
```

The response has `readOnly: true`, a versioned `graph`, per-network language,
native IDs/hashes, recognized and opaque nodes, unresolved connections and
coverage diagnostics. Full native XML is retained in an artifact. XML strings
are omitted by default to bound the response; request `includeNativeXml: true`
explicitly. `networkId` selects one network. `maxGraphBytes` is bounded to
4096–2097152 bytes, default 1048576; excessive output fails with the artifact link.
Recognition is syntax coverage, not a claim of exact type or runtime resolution.
The explorer and library lifecycle keep their own contracts and reference these
networks using existing canonical object identities.

## Independent evidence and reproduction

`fixtures/typed-graphical/sources/` contains independently authored native
SIMATIC SD documents. Native exports and their hashes are retained under
`fixtures/typed-graphical/native/`; `profiles.v1.json` binds templates to their
observed target and provenance. `source_upsert` accepts explicit new-block
`SIMATIC_SD` imports through the same change lifecycle, with mandatory compilation.
It cannot replace an existing graphical block.

`cases.v1.json`, `extended-cases.v1.json`, `route-cases.v1.json` and
`numeric-cases.v1.json` describe actual MCP qualifications. Independent Boolean
truth tables, comparison boundaries, timer/counter transitions and numeric
input/output/in-out/return/instance-independence assertions are separate from the
emitter. The qualifier checks an independent native topology oracle and then the
complete expected native document; those are distinct comparisons.

Run `scripts/qualify_typed_graphical.py` with explicit immutable host/worker
binaries and `--output` in an owned artifact directory. `--live --sim-worker ...
--provision-runtime-credential --trust-owned-empty-cpu-certificate` uses the
existing bounded simulator backend. `--engineering-only` explicitly skips runtime.
Every run records the binaries, targets, MCP calls, prepared artifacts, five gate
flags, cleanup and precise blocked evidence. Scope is owned scratch projects and
owned PLCSIM instances only; no physical-controller path is used.

Compilation does not establish behavior. Host timing and application call counts
do not establish exact PLC scan timing. Counter saturation, richer type variants,
alternate routes and unexecuted assertion cells must remain explicitly unverified.
Release bundles must retain/hash all schemas, fixtures, scripts and documentation;
local evidence does not qualify a remote VM.
