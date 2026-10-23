# Library update analyses — 23 October 2026

The new `library-update-analysis.v1.schema.json` describes the project-update
preview's impact, baseline and cleanup guard. Explicit `update-plan` and
`update-change` modes validate this projection plus the existing operation
contract. Other modes retain their previous scope. These are offline observation
checks; production native code is unchanged by this increment.

Requested type/version metadata must match complete selected dependency closures.
The validator rejects missing dependencies, conflicting type versions, extraneous
closure members, duplicate identities, a newer committed source release and a
selected root without uses. Dependency types without uses remain valid library
synchronization observations and do not imply newly created instances.

Affected paths must exactly cover all baseline uses of the requested types, with
matching original version links and requested releases. Counts, scope labels,
no-op status and changed/unchanged actions are independently checked. One native
comparison covers each distinct changed prior version, even when it has multiple
instances. Failed comparisons remain valid incomplete evidence and cannot claim
`nativeComparisonsComplete`. Comparison property captures retain their 200-row
bound; their native meaning is not interpreted as compatibility or preservation.

The cleanup proof checks canonical exact paths and their 4,096 UTF-16-unit bound,
selected-PLC membership, complete required baseline-use coverage, unique resolved
edges and nonempty reported OB roots. It excludes self-edges and outgoing edges
from changed callers, then recomputes reachability. Disconnected cycles provide
no proof. The reported reachable set must exactly match the computed set and
include every baseline library use, including unrelated types. Unavailable
cross-reference services do not erase independently recorded positive paths.

The result reports `updateAnalysisValidated`, `updateComparisonsComplete` and
`recordedCleanupReachabilityValidated`. Native graph verification and arbitrary
engineering-content preservation remain explicitly false. Root OB identity,
native edge truth and object-description attributes are retained observations;
the local checker cannot independently establish them. It does not validate
post-update readbacks, source/target selection against TIA, compiler diagnostics,
save/reopen results, runtime reachability or native qualification. Compiler
evidence has its own explicit validator modes.

## Verification

Evidence is in `artifacts/todo-update-analysis-20261023/`, pinned by
`verification.json`:

- All 144 library tests pass, including 128 observation tests and 19 new update
  cases. Cases include unchanged versions, dependency-only types, multiple uses,
  missing comparisons, altered identities/counts, missing unrelated proof,
  disconnected cycles, mutable callers, canonical paths and qualification flags.
  The relocated CLI validates the new fixture using only copied local resources.
- The retained lifecycle replay passes all 32 operation changes, both update
  changes and four embedded update previews. Structures are extracted and
  reserialized, not claimed as original byte streams. The original report's
  SHA-256 remains
  `2b57f4e7be57321d0439068559b8dd53b47aa989c455d278a0aaaf5eb49b4880`.
- `fixture-source.json` pins the source JSON pointer and derived fixture bytes.
  No original native evidence was modified or replayed against TIA.
- Contract coverage and refreshed source bindings pass for 261 tools, 56
  mutations and 335 feature entries. No qualification is promoted.

The initial new-test failure is retained: the synthetic change wrapper used a
different session generation from the native preview. The fixture wrapper was
corrected; production schema/session checks were not weakened.

Synchronization, HMI/component previews and other detailed effects remain open,
as do post-update preservation and native qualification. Earlier aggregate and
package results apply to their frozen baseline; this batch has focused checks
and does not claim a rebuilt deployment package.
