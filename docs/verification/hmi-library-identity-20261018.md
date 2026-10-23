# HMI library relationship identity — 18 October 2026

Status: full offline verification passed. Exact integration/hosted-CI state is
recorded in `artifacts/hmi-library-identity-01/integration.json` after push.
Base: `bc970a6b7e2d03fb7e91364411594c097835d02e`. No native execution or
qualification is claimed. C01 and the full 44-workstream goal remain open.

While tracing C01 library/consumer associations, a failure was reproduced in the
shared HMI project-model relationship key. Faceplate library observations have no
captured target path. The previous key used the source, relation, property, spelling
and target path but omitted the library type/version GUIDs. Two supplied observations
with different versions therefore acquired the same key, and `RetainUncaptured`
discarded one as a duplicate. The failing regression is retained in
`artifacts/hmi-library-identity-01/before.log`.

The key now includes the separately observed type and version GUID text for this
relationship kind. It excludes export hashes, timestamps and interface payloads;
those remain evidence, not new relationship identities. Other relationship kinds
and library observations with no GUID text keep their existing keys. New captures
with a GUID-bearing faceplate reference receive the stronger identity. Previously
saved snapshots are not rewritten; their original IDs remain historical evidence.

The linked production helper passes 25 checks, including the reproduced loss,
different types, exact retained original observations, repeated-observation
deduplication, unchanged unresolved endpoints, edge limits, no-identity historical
keys and unrelated relationship compatibility. The test inputs are synthetic;
using the real helper does not prove the Siemens caller executes correctly.

The change affects `OpennessSession.HmiProjectModel.cs` through its existing shared
key call. The native worker has not been built against Siemens assemblies here.
Actual faceplate/version collection, native GUID authenticity, library export
association, complete dependency and consumer inventories, generation, compilation,
saved reopen and runtime/visual acceptance remain open. A distinct retained GUID
pair is neither a resolved captured endpoint nor proof of complete library closure.

Exact local evidence is under `artifacts/hmi-library-identity-01/`. Historical
qualification evidence and the original scope reconciliation remain unchanged.

## Full validation

Both frozen snapshots contain 2373 identical source files. The
aggregate passes 44 managed suites, including the changed 25-check HMI model
suite, 242 general checks, 19 HMI preservation checks, 1,510 Python tests (one
existing skip), 25 Explorer model/client checks and all Host workflows. All 38
package/relocation stages pass. The 1704-file package manifest is
`598fe44c468fedbaa5b1b9590acfdacd57d1cce2c470fd01892f2e3a21cc1085`.

The same production helper and test source also compile and pass all 25 checks
as an actual x64 executable on the installed .NET Framework runtime, using the
repository's existing Roslyn/Framework fixture approach and Newtonsoft.Json
13.0.4. Source, executable and dependency hashes are retained separately. This
checks the pure helper's Framework compatibility; it does not compile or run the
Siemens caller.

Both runs also preserve the preceding C01 workflow: 384 HMI MCP checks, four
independently verified original expected-effect records and 416 compatibility
checks across nine prior records. Five packaged Explorer assets match the prior
browser-tested assets; no new browser run is claimed. The native worker and its
Siemens caller remain uncompiled and unexecuted here. Package success validates
the portable product, not native relationship acquisition.

All 112 historical evidence entries remain unchanged, including 106 unavailable
inputs. The original scope reconciliation is byte-identical. Final documentation
is the only change after the frozen sources. Broader C01 and every remaining
44-workstream obligation remain open.
