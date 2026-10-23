# C01 dependency impacts and workstation validation

The validation repair checkpoint is `808451e`. It fixes the installed-SDK HMI
method-group build, Windows ACL comparison bookkeeping, isolated test report
destinations and the profiled optimization-test timeout. Before that commit the
full workstation build/test, 1,532 Python tests, 25 Explorer unit tests and 38
browser checks passed. All 112 retained historical evidence entries matched;
the original project-index report was restored byte-for-byte before committing.

V7 connects explicit library dependency associations with exact saved consumer
paths, selected component bindings, outputs, supplied-file effects and derived
expectations. Missing mappings, contradictory/unavailable identities and original
traversal exclusions remain visible. A separate 1 MiB prefix budget records every
omitted association. No production HMI mutation or qualification is enabled.
The [reference](../reference/hmi-component-review.md#dependency-impacts-and-expected-outputs-v7)
describes the contract.

Evidence is retained under `artifacts/c01-665366d3/`. The 25 focused impact checks cover
scope/review isolation, duplicate consumer paths, shared outputs, missing
bindings, contradictions, retained publication, upload release, exact reopen,
rehashed forgery rejection and byte-bound omission accounting. The actual MCP
workflow checks strict schemas and both new paged sections, separately supplied
matching/changed/missing output files, fresh Host reopen and forged-report refusal.
The final `impacts-04` producer and `mcp-02` workflow pass all 1,102 MCP checks.
Its independent Python oracle reads original exports and saved objects, verifies
consumer edges against the saved model and recomputes file effects from original
supplied bytes.

All 19 actual prior-producer v1-v5 records pass 1,208 compatibility checks across
two clean Host lifetimes. Before rebuilding the Host, the checkpoint binaries
captured the responses of 12 prior v6 records, including expected forgery
refusals. The new Host returns identical responses in two lifetimes and all
retained original files remain byte-identical. `prior-v6-baseline.json` and
`prior-v6-reopen.json` retain this separate producer comparison.

The narrow native runner now retains exact Host/worker/TIA process handles using
`ComponentOwnedScratch` and requires complete successful PLC compiler diagnostics.
It authors four PLC functions with a diamond dependency graph, edits one leaf,
checks all peer block/type/tag-table XML, reopens the changed project, recovers
the checkpoint into a separate project and reopens the recovered project. This
scope does not establish HMI/library generation, complete project preservation
or PLC runtime behavior.

The first native attempt, `artifacts/c01-native-665366d3/result.json`, failed
before scratch creation: Siemens reported that the PortalPackage installer was
blocking startup. Its original exact Host/worker handles exited zero and the
native handle exited 6; all were closed without forced termination. The complete
native workflow and its cleanup verdict therefore failed for that attempt. After
the installer dialogs exited and Windows no longer reported an installation in
progress, a separate attempt was started in `artifacts/c01-native-665366d3b/`.
The user subsequently confirmed that an installation/update was running; the
first failed attempt is retained without changing its verdict.

The second native attempt passes. Both authored source operations compile with
complete successful diagnostics. All eight peer exports preserve their native
payloads; the leaf differs only in the independently specified return type,
literal and marker. The captured graph has the expected 12 typed paths on each
side, and depth/path/edge bounds expose truncation. All nine exported software
objects match after changed-project reopen, checkpoint retrieval and recovered-
project reopen. Comparison uses the documented native payload policy, excluding
top-level `DocumentInfo` and serializer indentation, while retaining substantive
XML content. All 45 original export byte hashes were independently rechecked.

All three owned Host/worker/TIA lifetimes close cleanly with exact retained
process handles, worker birth-bound exit receipts and no forced termination.
An additional offline Host restart returns the same saved comparison and source
search results without launching a native worker. The frozen 23-file binary copy
is unchanged after the run. This is a successful narrow PLC engineering workflow
on this workstation, not native C01 HMI/library generation or runtime acceptance.

Final regression: the Host build passes with zero warnings/errors; the complete
Python suite passes all 1,532 tests in 512.091 seconds. Contract validation and
current source bindings pass, and all 112 historical evidence inputs match.
The 15 review/preservation acceptance tests and five exact-lifetime tests also
passed before native dispatch. Results are retained in `python-01/result.json`,
`python-01/validation.log` and `verified-checks.json`. The full workstation and
browser results above belong to the separate validation checkpoint; they are not
represented as a new full-suite/package/browser run for v7.

Full C01 remains open: broader output formats, native library/HMI content
association, authored generation and target-specific preservation/runtime gates
still require their own evidence.
