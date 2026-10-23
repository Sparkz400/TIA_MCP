# Library HMI query contracts — 23 October 2026

`library-hmi-query.v1.schema.json` covers the inner HMI `library_query` request and
capability, target and Classic/Unified inventory response shapes. The local
validator exposes explicit `hmi-request`, `hmi-capabilities`, `hmi-targets` and
`hmi-inventory` modes. Existing operation and envelope modes remain separate.

Request checks preserve native defaults, project-selection strings and nested or
root-level HMI selectors. Inventory paths retain the native UTF-16 text bound and
control-character refusal. Capabilities bind family/assembly names, version,
identity rejection reason, diagnostic, availability and false qualification
flags. Target checks bind Classic software compilation and the broader Unified
owning-device scope. Inventory checks bind unique identities and each distinct
truncation boundary, without guessing request limits or total counts.

## Verification

Evidence is retained under `artifacts/todo-hmi-query-20261023/` and pinned in
`verification.json`.

- `library-tests-final-02.log`: all 108 library tests pass, including 92 observation
  tests and 20 new HMI query cases. Coverage includes absent/available/rejected/
  unreadable modules, native rejection precedence, wrong mode/family/scope,
  duplicate identities, fixed versus variable truncation bounds, unavailable
  links, unqualified observed links, nested interfaces, null native metadata,
  localized labels, malformed requests, original-byte pins and CLI behavior.
  The existing relocated consumer test includes the new helper and schemas.
- `native-replay.json`: all 32 previously retained operation response/preview
  pairs still validate after the HMI text-pattern correction. This does not add
  native inventory qualification.
- `installed-metadata.log` and `installed-metadata/library_query.json`: the
  actual compiled worker returns both installed V21 modules as available from
  assembly metadata. The reused optional-isolation executable blocks every
  WinCC assembly resolution, checks the Portal field before/after each metadata
  call and checks that no optional module was loaded. `bridge_info`,
  `library_capabilities` and `library_query` metadata calls pass. No TIA project
  is opened and no native engineering operation is executed.
- The original absent-module capture is
  `artifacts/optional-hmi-isolation-bb10a5f10de84c9aa5b3f07ff54e669f/library_query.json`.
  Both original capability responses pass exact-byte validation. Their checked-in
  copies in `fixtures/libraries/observation-contracts` are byte-identical.
  Target and Classic/Unified inventory fixtures are explicitly synthetic.
- `property-types.json` independently captures the current installed Unified
  assembly's 15 relevant screen, faceplate, tag and connection property types
  through `MetadataLoadContext`, with the assembly hash checked before and after.
  Unsigned dimensions, signed coordinates and native string properties match
  the contract. An older metadata inventory had a different assembly hash and
  was not used as evidence for the installed module.
- Two failing regressions in `control-boundary-before.log` exposed a pattern
  that could accept a non-whitespace control character through its `\S` term.
  Request paths, operation HMI paths and HMI text patterns now check the forbidden
  character range separately from the nonblank requirement. Final tests reject
  NUL, C0, DEL and C1 controls while preserving ordinary Unicode.
- Contract coverage and refreshed source bindings remain at 261 tools,
  56 mutations and 335 feature entries, with no qualification promotion.

## Scope and limits

Classic nested tag/connection and Unified interface truncation use fixed
200-entry bounds. Top-level limits are absent from responses and may be below
200. Unified `screenItemsTruncated` counts all screen items, so zero captured
faceplates can coexist with that flag; nested truncation does not imply top-level
truncation. Inventory still omits the detailed native content/references declared
by its coverage text. No total count or complete graph is inferred.

Availability is an assembly-metadata observation, not licensing or target
compatibility. Response validation does not bind an absent original request,
resolve native selectors or authenticate the producer. All validator receipts
retain false native-execution, referenced-file-read, preview-hash-recomputation
and qualification flags. Detailed preview/compiler contracts and native
qualification remain open. This Python/schema increment has focused validation;
earlier aggregate/package evidence covers its own frozen baseline only.
