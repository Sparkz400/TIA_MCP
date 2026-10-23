# Library observation contracts — 23 October 2026

New external schemas cover serialized library identities and successful-save
lock receipts. They retain session handles, metadata revisions, original source
pins, selected types/masters, bounded dependency versions, unavailable dependency
scope and saved identity views. The lock schema requires the producer's explicit
`freshReopenVerified: false` and `runtimeVerified: false`; a saved receipt cannot
be promoted into those qualifications by adding or changing flags.

`scripts/validate_library_observations.py` validates an explicitly selected local
file of at most 4 MiB, optionally against its original SHA-256. It rejects duplicate
JSON keys, non-finite numbers, invalid UTF-8, malformed identity/hash/GUID text and
schema violations. BOM bytes remain part of the hash. All schema references are
local. Embedded project, journal, source and artifact paths are never opened.

Timestamp checking uses an explicit native-DateTime parser with a timezone and
valid calendar/time fields. It does not rely on jsonschema's optional RFC3339
dependency, which is absent in the current environment. Leap-second timestamps
are outside the native DateTime profile. Validation does not normalize or rewrite
the supplied file.

This closes the identity and lock-envelope portion of the library schema TODO.
Plans, operation-specific results, detailed compiler payloads and the broader
library inspection surface remain separate work. `target`, `actualResult` and
`compilation` in the receipt retain opaque native evidence. Additional supplied
selection fields remain observations; they do not authorize native resolution.
Schema validity cannot prove authenticity, preservation, current availability,
successful complete compilation, fresh reopen or runtime behavior.

## Evidence

`artifacts/overnight-20261022/library-contracts/` retains:

- `tests-02.log`: 14 tests pass, including exact-text boundaries, date/calendar
  failures, missing fields, byte/hash limits, unavailable/empty dependency
  distinction, qualification flags, duplicate keys and actual CLI behavior.
- `native-final.json`: all 16 original 9 October native lock receipts validate
  against their retained hashes, plus 15 extracted native identity views. Identity
  structures are derived from their hash-verified parent bytes; they are not new
  standalone native exports. The actual CLI validates an original receipt and
  its bytes remain unchanged.
- `native-locks-01.json`: the first structural pass of all 16 original receipts.
- `tests-01.log`: the initial negative date test failed because FormatChecker had
  no registered date-time validator. This original failure is preserved; the
  explicit parser and additional date controls pass in the final run.
- `contracts.log`: feature coverage remains 261 tools, 56 mutation kinds and
  335 features. No new MCP tool or native capability is advertised.
- `verification.json`: final source/fixture/evidence hashes and scope.

The checked-in fixtures under `fixtures/libraries/observation-contracts/` are
authored synthetic shapes with no native authority. Native receipt evidence
remains bound to its original 9 October source/build and selected fixtures.
No Siemens or native worker process was started for this increment.

These schema/validator additions postdate the frozen combined runtime snapshot
`ce4067493d784ce4b779532e2eba5071`; its aggregate and package results cannot be
claimed to include these additions. Their focused evidence is recorded separately.
