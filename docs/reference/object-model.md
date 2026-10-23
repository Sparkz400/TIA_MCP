# Canonical object identities and software scopes

`object_resolve` resolves one exposed native object before a read, capability probe or change. Its schema version is 1. Each identity contains `projectPath` (the saved `.ap21` path), `plcPath`, `scopePath`, `objectPath`, `kind`, `name` and a `namespace` qualifier (empty for unnamespaced objects). Project/hardware identities have null PLC/scope fields when no PLC ownership is applicable. An `objectId` is the canonical `objectPath`, not a fabricated persistent GUID. Store `projectPath` with it to distinguish copied projects that share the same displayed names. A supplied `projectPath` must match the selected open project; a stale identity cannot silently select another project copy.

Paths use the same URI-escaped segments as project inventory. They are case-sensitive and must be copied exactly from returned data. Symbolic names use case-insensitive matching. All supplied selectors must agree. Rename, movement, retrieval into a new directory and project rename can invalidate identities; resolve again after those operations.

## Resolution

Supported kinds are `project`, `plc`, `block`, `type`, `tagTable`, `hardware`, `technology`, `softwareUnit` and `safetyUnit`. Select by `objectPath` or `name`; add `kind` and exact `scopePath` to disambiguate. Normal `projectName`/`plcPath` selectors remain available. A canonical object/scope path can infer its PLC. This release resolves one selected PLC at a time for software objects. A project with multiple PLCs requires an exact path or explicit PLC selector.

Software scope paths are the PLC path itself, `<plcPath>/units/<escaped-name>`, or `<plcPath>/safetyUnits/<escaped-name>`. User groups remain part of `objectPath`, for example `<scopePath>/blocks/groups/Helpers/Enable`. Namespaced block/type paths append `namespaces/<escaped-namespace>/<escaped-name>` to the native group path: `<scopePath>/blocks/groups/Helpers/namespaces/Company.Area/Enable`. Empty namespaces retain the original paths. This distinguishes identical short names even within the same unit and group. Path parsing follows structural positions, so a PLC, unit or user group named `plc`, `blocks` or `namespaces` remains unambiguous. Tag tables have no namespace-qualified identity in this API; individual tags/constants remain outside this resolver release.

Lookup visits at most 50,000 exposed objects and cannot claim uniqueness after traversal truncation. Duplicate names produce `AmbiguousTarget` with bounded candidate identities. Scope completeness describes the requested object family, not every possible TIA object: individual tag members, TO parameters and native folder objects are not separate resolver kinds yet. Object paths, names and scope selectors are conjunctive; an exact path never overrides a conflicting name or scope.

## Changes

`change_prepare` accepts `scopePath` at the top level or inside the payload (both must agree). Block payloads additionally accept `blockPath`; types use `typePath`, tag tables use `tagTablePath`. Exact paths select a single existing target. `block_rename`, `block_delete` and `graphical_operand_set` can use `blockPath` without a redundant block name. A payload `namespace` selector, when supplied, must agree with the source/XML namespace and exact path.

Before choosing a destination, the worker searches exposed scopes for every expected name. An unqualified duplicate is rejected; it is never silently redirected to the standard program. Existing targets must share one software scope and one native parent group. New objects default to the standard PLC scope only when no existing target determines a scope. Specify `scopePath` to create in a software unit. `groupPath` selects an existing user group; `group_create` creates supported user groups as described in [engineering workflows](../guides/engineering-workflows.md). Updates preserve their existing group. Mixed-group or mixed-scope imports must be split into separate changes.

Namespaced source uses the native `NAMESPACE Company.Area` / `END_NAMESPACE` envelope. The strict declaration scanner allows one namespace per prepared change; nested, mixed and malformed envelopes are rejected. A unit's namespace preset does not automatically qualify classic imported SCL source, so send the native namespace wrapper explicitly. XML uses the top-level object's `AttributeList/Namespace`. Namespace changes are not implicit updates: source/XML with a namespace conflicting with an exact selected path is rejected before saving a checkpoint. A name/namespace already owned by another scope produces `NamespaceCollision` rather than an attempted cross-unit overwrite. New namespaces require a software-unit destination. These behaviors follow the native [external-source unit API](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-software-units/adding-external-sources-in-units) and [block namespace API](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-software-units/accessing-namespaces-for-program-blocks), with namespace-preset behavior confirmed by the isolated fixture runs.

Case-only namespace aliases for an existing expected name are conservatively rejected across all exposed scopes, including the selected scope. Use the exact observed namespace spelling. This prevents an import preview from classifying a possible alias as an unrelated new object without assuming undocumented native case behavior.

Typed native routes now exist for standard scopes and software units:

- Source generation uses that scope's `ExternalSourceGroup`, with the documented typed user-group overload when needed.
- Block, type and tag-table XML imports use the selected native group's composition.
- Rename/delete and graphical operand edits use the same scoped block lookup as preparation and post-write verification.
- The revision fingerprint includes exposed standard/software-unit/safety-unit scopes, canonical block paths/native fingerprints and type/tag-table paths and timestamps.

Generated `systemGroups` objects cannot be mutated directly. Know-how protection is respected. Safety-unit mutations are limited to `safety_import_xml` and keep the existing safety login, language, consistency and generated-block checks. No generic reflective writes are used. The full checkpoint/archive/compile/save recovery workflow still applies.

## Qualification

The shared path and selector rules have pure tests for escaped segments, duplicate names, conflicting scopes, structural names and generated-group boundaries. The source scanner separately tests namespace envelopes and hidden declaration attempts.

`artifacts/object-model-live-fourth.json` completed 46 native MCP/helper checks on 2026-10-08 in an isolated CPU 1511-1 PN V4.1 project. The fixture contains four FCs with the same short name: one in the standard program, one in each of two software units, and another namespace within the first unit's same block group. Qualification includes:

- Ambiguous short names rejected; exact path and namespace-qualified identities resolve the intended object.
- Conflicting scope, stale project path and wrong source/XML namespace rejected during preparation/read resolution.
- Namespaced SCL update and native block XML round trip, nested user-group source creation/deletion, namespaced UDT source/XML import, and exact-path rename compiled and saved successfully.
- Standard-program, second-unit and same-unit alternate-namespace native payload hashes stayed unchanged (only export `DocumentInfo` is excluded from semantic comparison; original XML remains saved).
- A separate native change in unit B invalidated a previously prepared unit A edit; application returned `StaleRevision` and retained the original unit A block.

`artifacts/object-model-unit-recovery.json` then qualified archive restoration into a new directory and a fresh TIA reopen of that restored project. All three unit FC payloads matched their pre-change semantic hashes after recovery and again after reopen. Replaying the actual old project copy's identity returned `ProjectMismatch`, while the current copy's paths resolved correctly. The original fixture and recovered copy are both retained.

`artifacts/object-model-namespace-guards.json` records four additional negative-only checks against the final Release binaries: case-only namespace aliases were rejected for same-scope source, cross-scope source, scope-only XML and UDT source. No apply/save was invoked, and the selected block/type payload hashes stayed unchanged.

`artifacts/object-model-expanded.json` completed a focused software-unit run on 2026-10-08 using explicit Release host/worker binaries in a fresh owned CPU 1511-1 PN V4.1 scratch project. Its 64 recorded MCP/helper calls include 12 checkpointed changes that compiled and saved successfully, and five expected rejection calls. The focused run does not repeat the historical routing sequence above. It qualifies:

- Native LAD and FBD FC imports in the standard program and two software units, followed by an operand edit in the first unit. Duplicate short names and an incorrect namespace are rejected. The entire selected native payload matches the intended edit; root and second-unit payloads remain unchanged, excluding only export `DocumentInfo` from comparison.
- Distinct empty tag-table imports in all three scopes, an exact-path unit-table XML round trip, rejection of a conflicting scope, and unchanged native payloads for all three tables.

The earlier `artifacts/object-model-expanded-tag-name-collision.json` retains the native rejection when the same table name was reused across scopes and its successful archive recovery. This profile requires PLC-wide unique tag-table names; the passing run uses distinct table names instead of claiming same-name unit tables. Fixture adaptation preserves default XML namespace placement and omits the root FC's optional `MemoryLayout` field for unit imports, matching the native-seeded unit export.

These are profile-specific results. Empty table round trips do not qualify individual tag or constant edits. Cross-unit dependency creation, unit movement, safety-unit imports and other namespace/language/CPU combinations remain outside this fixture's qualification. Native unsupported operations retain their actual failure and recovery state rather than falling back into another scope. Fixture setup is a development-only helper under `scripts/native/UnitSeeder`, constrained to a runner-recorded worker-owned scratch project under repository artifacts; it is not an unrestricted MCP mutation tool.
