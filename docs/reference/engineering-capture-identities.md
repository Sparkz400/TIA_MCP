# Shared captured engineering identities

This is the minimum **AC-02 identity contract** used by the offline watch-table, VCI, translation and Test Suite modules. It does not close AC-02 native identity/probe qualification.

`CapturedObjectIdentity` retains the existing exact `projectPath`, `plcPath`, `scopePath`, `objectPath` and kind vocabulary. Selectors are opaque, bounded and case-sensitive; no trimming, Unicode normalization or invented vendor GUID occurs. Null PLC/scope means not supplied, not an implicit default scope. An unrecognized kind does not establish a native resolver for that kind.

`EngineeringSnapshotIdentity` binds an observation ID to a lowercase SHA-256 digest of the captured source manifest and an explicit TIA version. An observation ID is a bridge receipt identity, not a persistent Siemens revision. `EngineeringObjectReference` combines these and an optional content digest. The producer must hash the actual retained bytes/manifest; string validation alone does not verify that provenance.

The [Core guards](../../src/TiaMcp.Core/Engineering/EngineeringIdentity.cs) check exact object equality, same-project capture consistency, and expected-versus-observed content identity. `EnsureSameSnapshot` permits different objects in one project capture. `EnsureFresh` additionally requires the same object and identical content state, including known versus unknown content. This only establishes consistency between supplied captures. A later native apply must reacquire and validate the actual project/object/program state through the coordinator's admitted worker.

Use [the reference schema](../../contracts/engineering-object-reference.v1.schema.json) with `BridgeProtocol.Json` camel-case serialization. `CanonicalJson` sorts object keys ordinally and preserves array order, nulls and numeric JSON representation. It does not claim a universal external canonicalization standard. Output consumers must preserve those semantics when recomputing hashes. Observation timestamps belong outside a semantic digest unless time itself is part of the evidence being bound.

Domains add their own identity dimensions: watch rows bind exact instance storage/member components and case hash; VCI packages bind all companion files and observed profile; translations bind workbook/layout/key/culture/source text; Test Suite definitions bind original bytes, required scope and result provenance. None can derive an online address, active license, current program identity or native qualification from these shared records.

The next additive [native observation and durable operation substrate](engineering-operation-journal.md) now binds physical project paths, worker epochs, adapter/profile identities, target probes and resource admission. Its adapter callbacks and recovery tests do not close native AC-02/AC-03 qualification.
