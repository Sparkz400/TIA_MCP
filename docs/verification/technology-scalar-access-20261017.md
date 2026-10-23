# Technology scalar access and batch preflight - 17 October 2026

The previous scalar path permitted missing Value-access metadata and substituted
string when a current value was null. It also dispatched earlier setters before
checking later entries, and readback compared JSON values without preserving the
native scalar type. Both edit and initial creation now use one shared preflight and
application routine. [Behavior and limits](../guides/engineering-workflows.md#technology-scalar-write-boundary).

## Source and test boundary

`OpennessSession.TechnologyParameters.cs` contains the actual production routines.
The existing scalar conversion, protected-name and scalar-presentation methods moved
unchanged to `OpennessSession.ScalarValues.cs`; all hardware/network callers still use
the same partial-class methods. The [byte comparison](../../artifacts/to-write-scalar-move.json)
compares both original method spans to the moved file. No shared conversion behavior
was changed by the move. Native dispatch sites in Changes.cs still call the shared
apply path for both create and parameter edit; edit preview now calls its shared
preflight. Existing checkpoint/compile/save/recovery routing remains in place.

The test project links these exact production files with explicitly synthetic
Siemens-shaped classes. It exercises getters/setters, permissions, lookup identity,
type changes, conversion failures and injected failures. This proves the linked
control flow on .NET 10; it does not prove the real SDK ABI, whole worker build,
actual native metadata availability, CPU eligibility, preservation or recovery.
No Siemens product, SDK, PLCSIM or PLC was installed or executed.

[Final focused run](../../artifacts/to-write-tests-02.log): 159/159 general executable
checks pass, including five new scenario groups: access/type boundaries; whole-batch
preflight and 0/500/501 bounds; revalidation/partial setter failure/typed readback;
supported scalar round trips and invalid conversions; explicit types for null values.
Cases cover missing/null/duplicate metadata, read-only/write-only/no access, exact
name mismatch and missing parameters, protected names, null/unsupported/ambiguous
types, invalid later entries with zero setters, getter/metadata failures, access/name/
type changes before dispatch, failures after the first setter without replay, readback
value/type/access drift, and read-only-after-write success. Scalars include exact
large signed/unsigned integers, Boolean, Unicode/CRLF strings, enum and numeric types.
The first development run had one fixture failure: a C# conditional promoted both
fixture values to Int64. Explicit boxing now models a real Int32-to-Int64 change;
the original failed log is retained as development evidence.

## Qualification and combined checks

Both affected catalog entries are now unverified for the changed source. Historical
axis/encoder evidence references and all 106 unavailable evidence rows/hashes remain
preserved. Static totals remain 199 tools, 55 mutation kinds and 272 features.
Final source: artifacts/to-write-01/source, Git tree
c38ef123047e877f4ac1a02500e9395696560be8; 1954 tracked files.
[Source manifest](../../artifacts/to-write-01/source-manifest.json) SHA-256:
fd1d165f7ece3e8e85209ecb710ae66cf8aabca6ac519355db2348ce472a5a82.
Source ZIP SHA-256: 6317ccf7a6ff63b0ed6c11988b903c56697cc6ab96b499fec9a20b437dddc620.

The [aggregate](../../artifacts/to-write-01/source/artifacts/checks/result.json)
passes 39 managed suites, 1,295 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all actual offline MCP workflows.
The general executable repeats all 159 checks, including the five new linked-routine
groups. Run: 2026-10-17T04:45:57.1661141Z to 2026-10-17T04:56:08.0288801Z.
Receipt SHA-256: 6fa9157d75f93e3bef96a5f93eb00b879d48280a5aed35d5e948664399670a84.
[Console output](../../artifacts/to-write-01/aggregate-console.log) retains test results.

All 22 [package stages](../../artifacts/to-write-01/source/artifacts/package-checks/result.json)
pass, including relocation. This offline package intentionally excludes native workers;
its success is not an SDK build or native execution of the changed scalar routines.
Receipt SHA-256: 5dc0996d2c39b9284d9cfbaeb8326e975026140b63e2ccabc2b36913c04fb9ec.
Bundle tia-mcp-offline_20261017_044619_82c6239e: 1386 manifest files;
manifest SHA-256: c09d716602376e9e43ef7ba6e4b9966489ee380464177eb05a57879ea754c7c4.
ZIP SHA-256: 5d649f23503f7d2a5afe6013ffaa182d8deb05241459121fa96bba581a21be5e.
Hashes establish integrity, not publisher authenticity or native qualification.

The [final comparison](../../artifacts/to-write-01/final-source-comparison.json)
permits only completion-documentation edits after this source snapshot. The
[integration receipt](../../artifacts/to-write-01/integration.json) records the pushed
commit, hosted CI state and cleanup.

Outer evidence capture and the full native worker were inspected in source, not executed.
Getter/setter refusal assertions apply to the linked scalar routines; they do not
establish that every native inspection or export operation performs no internal reads.

## Remaining work

The follow-up generic attribute source repair is now implemented and recorded
[separately](attribute-scalar-access-20261017.md), including its independent verification
and remaining SDK/native gates.

TO-03 exact native metadata/capture and TO-04–12 lifecycle, content, connection,
preservation and qualification remain open, together with the full 44-workstream
queue. This guard repair does not provide full native captures, exact path-based TO
mutation selection, supported CPU/version enumeration, units/range semantics or a
new parameter family. Rebuild against genuine SDK metadata, test current known/unknown
access/type paths, compile/save/reopen and independently verify full preserved native
content and checkpoint recovery before qualifying the changed routes. GitHub CI on
base 9909608 was refused before steps because of account payment/spending limits.
