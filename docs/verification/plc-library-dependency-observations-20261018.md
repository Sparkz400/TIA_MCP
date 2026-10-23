# Saved PLC library dependency observations

Status: focused checks and full frozen aggregate/package validation pass on the
integrated HMI repair base. No native qualification is claimed.
No Siemens SDK, native caller or TIA operation was executed.

Saved FB/FC/PLC-type observations now retain a nested `plc-library-dependencies.v1`
observation from the same owner-checked library version. An observed ordered list
of version GUIDs is distinct from unavailable data. Empty lists require a successful
enumeration; null entries, empty identities, excessive lists and native failures
cannot become empty successful observations. Duplicate and self references remain
original data for a later closure evaluator. No closure or native qualification is
claimed. Unexpected non-native failures propagate.

The existing shared dependency reader keeps its 1,000-entry limit. Its optional
budget callback charges the collection getter and each iterator-read attempt,
including the final probe that establishes completion. Both project-model block
and type capture use one separate 20,000-operation budget for the whole capture.
Exhaustion prevents further dependency getters, disposes active enumeration and
publishes no partial GUID list. The known root identity remains available with an
explicit unavailable dependency observation. Capture limits, usage and partial
provider coverage expose this boundary; no caller-provided option is added.

The focused project-model suite passes 13 groups, including 38 linked helper cases
and nonempty dependency retention through snapshot, original saved-review import,
C01 review and reopen. Older saved snapshots retain their original absent fields.
The existing PLC integration suite passes all 207 checks. The same 38 helper cases
pass on actual x64 .NET Framework with synthetic Siemens shapes. Two controls that
bypass the budget or turn native failure into an empty list each fail four cases.
Two source-only caller-wiring checks pass; these do not compile the native caller.

Six actual prior-producer v5 HMI reviews and their 24 original files are now retained
under `fixtures/hmi-components/retained-v5`, with the original producer binary and
source-manifest hashes. No record was regenerated. The existing compatibility
workflow reopens all 19 v1-v5 records through the packaged Host across two clean
lifetimes, compares receipts and every paged report section, and rechecks every
original file hash: 1,018 checks pass. This focused run uses the completed HMI repair
package; final combined dependency-source validation also passes as recorded below.

Evidence is retained in the primary checkout under
`artifacts/plc-library-dependency-observations-01/`. The implementation is in the
commit `c655fa9e4b1801cbf24fc61a4d9c154c5b77719a` on main. The
completed worktree was archived and its branch removed after preserving local
evidence. Final validation is recorded below; exact Git/CI and cleanup receipts
are retained in that evidence directory.

The capture improvement is integrated. The subsequent [v6 association increment](hmi-dependency-associations-20261018.md)
compares explicit dependency/object/export selections; broader C01 work remains.
The HMI/runtime-oracle base is `e0693a97ffb1259301252db92187f3f559b4b1e2`.
C01 still needs complete consumer/content mapping and broader authored output
expectations; explicit dependency-to-original-export/object association is now
covered by the subsequent verification linked above. Authentic missing
formats, Siemens compilation, true native instance association, generation and
runtime acceptance remain external gates. The original full goal remains open.

## Final frozen verification

Two identical 2,437-file snapshots pass all 44 managed suites, 1,520 Python tests
(one existing unavailable-evidence skip), 25 Explorer model/client tests and all
38 package stages. Both runs retain the 578 current HMI MCP checks and 1,018
compatibility checks across 19 original v1-v5 records. The 38 linked capture cases,
13 model groups and 207 existing PLC integration checks pass in the aggregate.
Both runtime workflows pass independent verification of seven capture chains,
three migrations and seven mutation controls.

The final package has 1757 files, manifest
`f46f0baab4120b55054a3a7285efcd07a4e645cb981e32ada50156a54df5ee1f`. All five Explorer assets are unchanged
from the prior tested package; no new browser execution is claimed. All 112
historical evidence entries and the original scope reconciliation are unchanged;
106 historical inputs remain unavailable. The six retained v5 records and 24
files match the original producer evidence byte for byte. Post-snapshot changes
are final documentation only. See
`artifacts/plc-library-dependency-observations-01/verification.json`.

This validates portable behavior, retention and bounded synthetic native shapes.
Siemens SDK compilation, full native caller behavior and the remaining C01 and
44-workstream requirements remain open.
