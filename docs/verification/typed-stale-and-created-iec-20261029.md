# Typed resolution stale phase and created wave 9 IEC storage — 29 October 2026

Two typed LAD/FBD (L01) items, each run natively from a Debug build frozen with `git archive`
at the short project path `D:/Entwicklung/TIA_MCP-public/y` and curated from a worktree at that
build's commit. Reports are in the main checkout under `artifacts/q/yz` (stale phase),
`artifacts/q/c9` (capture) and `artifacts/q/yw` (engineering cells). Owned scratch projects only;
no PLCSIM.

## Stale phase: V21 refuses a same-qualified-name DB

`stale-db-ambiguity` used to add `TiaMcp_UnitB.Tgp_ResolutionOne` and expect `AmbiguousOperand`
for the root `Tgp_ResolutionOne`. Since `a808569` the resolver compares qualified names, so that
DB is no competitor. Production imports refuse a same-qualified-name DB in another scope
(`NamespaceCollision`), so the case now asks TIA itself: `ResolutionSeeder duplicate-db-diagnostic`
tries every route to a second `Tgp_ResolutionOne` without a namespace in unit `TiaMcp_UnitB`.
V21 refuses each one:

| Route | Native refusal |
| --- | --- |
| Namespace-free source in the unit (preset `TiaMcp_UnitB`) | `GenerateBlocksFromSource`: "The block cannot be overwritten because it is already contained in another software unit." |
| The same source under an empty unit preset | the same |
| Generate `TiaMcp_UnitB.Tgp_ResolutionOne`, then clear its namespace | `set_Namespace`: "A program element with this fully qualified name already exists in this CPU." |
| Generate namespace-free `Tgp_ResolutionOne_Renamed`, then rename it | `set_Name`: the same |

The seeder deletes each partial block, so the complete original project XML stays unchanged and
compiles clean; the case then ends blocked as a recorded native boundary, like duplicate PLC tag
names. Fully qualified names are unique per CPU, so a native `AmbiguousOperand` for a DB operand,
or a `StaleDeclaration` from a new same-name DB at apply, cannot occur on V21. The `dbSymbol`
uniqueness checks stay as a defensive guard.

`tag-duplicate-setup-guard` and `stale-tag-address` pass in LAD and FBD on the same build.
Record: [`local.typed.resolution-stale.20261029`](../../evidence/local.typed.resolution-stale.20261029.json)
(build `dff2201`; runs `stl-lad`, `stl-fbd`, `db-lad`, `db-fbd`). A first probe on `84114ff`
(`probe-db-lad`) tried only the source routes and is not curated.

## Created TONR, R_TRIG and F_TRIG storage

The builder already accepted `createMulti` for TONR, R_TRIG and F_TRIG and `createGlobal` for TONR
(R_TRIG/F_TRIG have no captured global storage document), but no cell had run, and `createMulti`
on an FB with instance DBs had no captured IEC dependent-instance effect for these types.

- **Effect capture.** The standard IEC effect packet now appends `Timer_TONR : TONR_TIME`,
  `Edge_RTRIG : R_TRIG` and `Edge_FTRIG : F_TRIG` too. One owned capture passed both inputs,
  changed only the declared FB and its observed DB, and matched a fresh-process reopen. The
  extractor rewrote `instance-effects-iec.v1.json` with nine members and refuses a recapture whose
  earlier members differ: the six timer/counter members are structurally identical to the replaced
  capture. Record: [`local.typed.iec-effect-standard-wave9.20261029`](../../evidence/local.typed.iec-effect-standard-wave9.20261029.json)
  (build `b4abe48`).
- **Engineering cells.** `wave9-cases.v1.json` gains 14 engineering-only cells:
  `typed-createmulti-{tonr,rtrig,ftrig}-{lad,fbd}`, the same with the caller embedded two parent
  levels deep (`typed-transitive-createmulti-…`) and `typed-createglobal-tonr-{lad,fbd}`.
  All 14 pass the four engineering gates (engineering, complete XML preservation, fresh reopen,
  checkpoint recovery) in one batch, each in a fresh owned scratch. The transitive cells check the
  recaptured member shapes natively, nested two levels deep in the parent FBs and DBs. Record:
  [`local.typed.wave9-created-storage.engineering.20261029`](../../evidence/local.typed.wave9-created-storage.engineering.20261029.json)
  (build `297c404`). The engineering summarizer accepts reports only under its own tree's
  `artifacts/`, so the batch was summarized with the `297c404` script rooted at the main checkout.

Runtime (PLCSIM) is not run here. The cells that need it, each with
`qualify_typed_graphical.py --cases fixtures/typed-graphical/wave9-cases.v1.json --live --runtime
--provision-runtime-credential --trust-owned-empty-cpu-certificate --sim-worker <V8 worker>`:
`typed-createmulti-{tonr,rtrig,ftrig}-{lad,fbd}` and `typed-createglobal-tonr-{lad,fbd}`. The runtime
harness gives the DUT FB an instance DB, so `createMulti` there follows the new effect profile.
