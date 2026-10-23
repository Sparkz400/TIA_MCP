# Library preview and change observations — 23 October 2026

External observation contracts now cover prepared library preview envelopes and
live/recovered change responses. The preview contract retains the complete kind
catalog, session generation, source revision pins, selected versions/masters,
dependency observations, payload, effects, warnings and fingerprint. Optional
document snapshots, compilation scope and plan-only integration flags retain
their producer shapes. This is envelope coverage: operation-specific payloads,
effects, detailed analysis and result/compiler objects remain opaque evidence.

The change-response schema distinguishes the live response from disk-recovered
status. It references the existing journal schema while tightening identity/hash
boundaries, timestamps, recovery attempts and current producer qualification
limits. The original journal contract is unchanged.

The companion `scripts/validate_library_observations.py` accepts `--kind plan` and
`--kind change`, in addition to identity and lock. After schema validation it
checks repeated change IDs, kinds, session generations, preview pins, entire
preview structures and live states. Preview comparisons ignore object-property
order, retain array order and distinguish boolean from numeric values. Document
snapshot hashes must agree, and paired compilation names/paths must have equal
counts. Historical previews without `compileTargetPaths` remain supported.

Recovered status follows `LibraryJournalStore.ReconciledState`: without recovery
attempts only prepared/applied remain certain; other recorded states report
`outcome_unknown`. The latest recovery attempt reports restored only when marked
completed; otherwise it reports `recovery_outcome_unknown`. Mixed live/recovered
response shapes and automatic retry promotion refuse validation.

All original inputs remain read-only under the existing 4 MiB observation limit.
The validator now also rejects finite-looking JSON exponents that overflow into
non-finite Python floats. Embedded paths are never opened. SHA-256 verifies the
selected original bytes when a caller supplies a pin. Repeated preview hashes
are compared literally: they are **not recomputed** using Python serialization,
which differs from the producer's Newtonsoft serializer. Output explicitly says
`previewHashRecomputed: false`. Structural consistency cannot establish signature
authenticity, native effects, preservation, successful compilation or qualification.
The native journal store separately validates its own serialized preview hash.

## Verification

Evidence is retained in `artifacts/overnight-20261022/library-contracts/`:

- `change-tests-01.log`: all 16 new tests pass. Controls cover mismatched repeated
  identity/payload/revision fields, boolean/numeric confusion, ordered arrays,
  interrupted/recovered state reconciliation, malformed recovery attempts,
  complete-string pins, timestamps, paired document/compile data, integration
  apply refusal, exponent overflow, opaque-result limits and actual CLI byte pins.
- `change-compatibility-01.log`: all 14 existing identity/lock tests pass against
  the expanded validator.
- `change-native-01.json`: 32 saved native change views and their 32 preview
  structures pass. They are extracted from the original 9 October producer report
  pinned to SHA-256 `2b57f4e7be57321d0439068559b8dd53b47aa989c455d278a0aaaf5eb49b4880`;
  original bytes remain unchanged. These are derived structures, not new native
  exports. The saved views cover prepared/applied across nine library kinds;
  interrupted and recovery states have synthetic offline controls only.
- `change-contracts.log`: cross-contract coverage validation.
- `change-verification.json`: exact source/fixture/evidence hashes and scope.

The new fixtures are authored synthetic observations, with illustrative hashes
and paths. No native process is started. The remaining library schema TODO stays
open for operation-specific payload/results, detailed inspection and integration
coverage. These additions, like the preceding identity/lock increment, postdate
frozen runtime snapshot `ce4067493d784ce4b779532e2eba5071`; that snapshot's aggregate
and package results do not include them.
