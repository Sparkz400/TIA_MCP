# Independent TO source bundles - 17 October 2026

The existing TO review tools now accept versioned v2 packets with independently
committed source uploads. Each exact label/purpose/phase/filename/size/digest
association is retained, while identical bytes may share one owned content file.
The [workflow](../reference/technology-profile-review.md#independently-retained-source-files-v2)
documents the input and retained manifest. The broader TO-02 consumer/impact handoff
and all native format, identity, compatibility and execution gates remain open.

## Implementation boundary

The existing v1 Core comparer is unchanged. The store validates the v2 extension,
normalizes only that known extension for the existing comparer, then appends a
versioned source manifest. All declared uploads must match before source publication
begins. Sources retain exact bytes under their digests, independently of upload lifetime.
The request and final record publish after the sources have been retained and verified.
Every record reopen rehashes all its declared owned source files, validates byte counts,
and recomputes the expected source associations from the original request. Report-only
substitutions cannot change purpose, filename, phase, source reference or authority.

Source input: 1–16 files, 1 byte–16 MiB each, at most 64 MiB declared total including
repeated roles. Request JSON remains limited to 4 MiB. The existing 16 MiB report,
512 MiB retention and 128 KiB page bounds remain. The source section is paged through
the same tool; original bytes are downloadable with artifact_read. Source extensions
and declared purposes do not establish trusted native formats or source authenticity.

An interrupted publication can leave owned files or temporary files, all counted
against quota, but cannot yield a completed record before every source is present.
An explicit retry revalidates inputs and existing partial files. Corrupted owned files
refuse rather than being silently repaired from uploads. No mutation or native recovery
is dispatched. The tests exercise a deterministic mid-bundle capacity failure, not
an actual power loss or process kill during source publication.

## Focused checks and compatibility

The final managed run (`artifacts/to-sources-tests-03/verification.json`) passes
134 checks. New cases include exact UTF-16 BOM/CRLF and binary data; distinct composed/
decomposed labels; shared bytes under different purposes; count/individual/aggregate
bounds; duplicate labels and filename/hash/length/authority substitution; validation of
all sources before the first copy; corruption and no silent repair; forged report purpose,
reference and version; released uploads; partial-publication quota accounting and explicit
retry. Real 16 MiB files, 64 MiB declared total and 16 source declarations are exercised.
Earlier 127- and 130-check development runs remain retained, without replacing the final run.

The actual MCP check (`artifacts/to-sources-mcp-01/verification.json`) passes
76 checks across two clean Host lifetimes. It validates both schema versions, supplies
independent Unicode-named UTF-16 and binary files, downloads exact original bytes, pages
the source manifest, verifies separate same-digest roles, refuses bad declarations without
changing retained files, releases every input/source upload, restarts, and checks corruption
and explicit restoration. Native worker paths deliberately point to absent executables.

The previous/current Host compatibility run (`artifacts/to-sources-compatibility.json`)
opens both historical v1 records from artifacts/technology-mcp-04/host-artifacts with the
previous packaged Host and the new Host. Both return identical receipts, preserve all seven
saved/workflow files byte-for-byte, and exit cleanly without native workers. Its binary
hashes are retained in the receipt. Production source has not changed since that new Host
build. v1 records retain their original serialized representation and content IDs; their
new source page is empty, with no invented source evidence.

## Frozen validation source

Source: artifacts/to-sources-01/source, tree
8a6ecc31aef8e51f4ff23ca32010fa5a2c784a1d; 1,947 tracked files.
Manifest (`artifacts/to-sources-01/source-manifest.json`) SHA-256:
e8895d2dc7992b7d14096333eb8657ecb84dec266ed42d22e8792fa5d7a690a1.
Source archive SHA-256:
055f3b0ef0fcbbb448ad1e93734170104d99f955645fda5ba4dee3148d50ad35.

Base 7eb1e1ffa9a6bbafb6f271c026c8fbf6ff88006a passes all hosted jobs: Explorer
38999703394 and Python/.NET/OPC UA/package 38999703384. New-source acceptance remains
separate.

The frozen aggregate (`artifacts/to-sources-01/source/artifacts/checks/result.json`)
passes 39 managed suites, 1,295 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all MCP workflows. The TO checks
pass 134 managed assertions and 76 actual MCP checks. Run:
2026-10-17T03:51:53.4722600Z to 2026-10-17T04:01:57.8250385Z; receipt SHA-256:
8f444322351379e016129c574040ddd2da3bb4ea76f51c4b0e826e11ce269a17.

All 22 package stages (`artifacts/to-sources-01/source/artifacts/package-checks/result.json`)
pass, including 134 TO managed checks and 76 actual MCP checks after Unicode relocation.
Receipt SHA-256: b192d90d4f513b158fad06bdfaf431563a2e69eb124feb8148d280cab5acb1c7.
Bundle tia-mcp-offline_20261017_035215_d329716f has 1,383 manifest files;
manifest SHA-256: b6f8ddf0b9f7e882ada5163b0cb731be47782f33b434ca8b808564e4ae2eea66.
ZIP SHA-256: d23a9e6065feb3f731f42c4b8637fa180e240e89eee69006f88a0284351052ee.
Hashes establish integrity, not publisher authentication or native qualification.

The final source comparison (`artifacts/to-sources-01/final-source-comparison.json`)
permits only completion-documentation changes in status, TODO, queue, remaining D13
acceptance and this record. Production code, tests, schemas, bindings and scripts remain
identical to the tested source. The integration receipt (`artifacts/to-sources-01/integration.json`)
records the pushed commit, its separate hosted CI state and cleanup.

## Remaining scope

Broader declared parameter/relationship consumers and bounded saved graph impact handoff
are still open under TO-02. Files are retained, not parsed as genuine cam/MCL/native XML,
linked to fresh native endpoints, or certified as complete originals. TO-03–12 and the
full 44-workstream queue remain open. No Siemens SDK/product/PLCSIM/PLC was installed
or used. All 106 unavailable historical evidence bindings/hashes remain preserved; the
catalog remains 199 tools, 55 mutation kinds and 272 features, with no qualification promotion.
