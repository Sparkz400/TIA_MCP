# Encoder prerequisite checkpoint provenance — 17 October 2026

The failed-seeder recovery path computed its expected archive hash at recovery
time. A synthetic reproduction replaced the checkpoint after creation; the old
parent dispatched `project_retrieve` using the replacement bytes' hash. The
before reproduction (`artifacts/encoder-checkpoint-before.log`) records that
dispatch. The repaired reproduction (`artifacts/encoder-checkpoint-after.log`)
refuses retrieval. Neither probe invokes Siemens.

EncoderSeeder now publishes an immutable receipt immediately after archiving the
saved prerequisites, before its hardware-attribute writes and technology-object
creation. The receipt binds the exact authorization bytes, owned process/project,
archive path, byte count and SHA-256. Publication uses create-new and durable
flush; a failed publication prevents the subsequent seeder mutation. The returned
receipt digest describes the exact serialized bytes written, rather than a later
read of a replaceable file. This is the seeder prerequisite checkpoint, after the
Host has already plugged the fixture module; it is not the pre-task baseline.

The parent captures the authorization digest before launching the helper and
verifies the receipt when a known terminal result arrives. Bounded metadata reads
and the existing no-link/reparse path checks prevent substituted locations; the
archive is hashed as a stream. JSON parsing uses the same bounded bytes whose
digest was verified. Recovery rechecks the original receipt before closing the
first session and again before retrieval in a fresh owned session. It sends the
original recorded archive digest and verifies the returned archive/process
receipt. It never replaces that expected digest with the current file hash.

Unknown helper/session outcomes cannot dispatch recovery. Lost retrieval replies
are not replayed. A successful checkpoint retrieval leaves qualification failed
and explicitly reports `fullNativePreservationVerified: false`; retrieval and
inventory alone do not prove whole-project restoration. The read-only companion
and independent auditor also require the retained prerequisite checkpoint to
remain valid before accepting the current source evidence. Legacy sources without
this receipt cannot establish current checkpoint provenance.

## Verification

Seven Python methods cover valid receipts, archive replacement, source/phase/auth
binding, malformed descriptors, unknown helpers, actual directory aliases and a
receipt replacement between hash and parse. Four additional orchestration methods
cover known failed-seeder retrieval, missing/changed receipts, replacement during
session close, lost/mismatched retrieval replies and stale source checkpoints.
These join the existing full producer/companion/auditor fake-process workflows.

The new `TiaMcp.EncoderEvidence.Tests` suite compiles the actual SDK-independent
receipt publisher and exercises seven groups with real file I/O and synthetic
archive bytes, including an actual Windows junction. Its emitted receipt is read
by the independent Python verifier in the offline runner. The suite is included
in the solution, native-environment test entry point and offline managed queue.
No fabricated archive is presented as a genuine Siemens project.

Both optimized Python modes pass 74 focused cases. Native SDK compilation and
native Archive/Retrieve execution have not run.

Frozen Git tree: `6914b63e79fe421f95f642213fc0c88077a08a79` (1990 files).
Source manifest (`artifacts/encoder-checkpoint-01/source-manifest.json`) SHA-256:
`71344e47e47b05e46f6a61892e65d08320846618e655b2139bf2f232198ae987`.
Source ZIP SHA-256: `bc307e6b68dc040f868235315b69fe43486be37a7a068aa02ca983d03813a574`.

The aggregate (`artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json`)
passes 40 managed suites, 1,353 Python tests (one unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows.
The unchanged general executable passes all 202 checks. The Python aggregate
includes 74 focused regression cases under each of `-O` and `-OO` through its
optimization subprocess test; child cases are not added to the parent test count.
The new managed suite runs seven actual file-I/O publisher groups and verifies its
synthetic receipt through the independent Python reader.
Run: 2026-10-17T08:51:41.0692233Z to 2026-10-17T09:01:45.9760170Z.
Receipt SHA-256: `172c91ce0d2659543adf42540b68c3b4f6e13893d6db7a9db2da3808868a0b12`.
Console evidence (`artifacts/encoder-checkpoint-01/aggregate-console.log`).

All 22 package stages (`artifacts/encoder-checkpoint-01/source/artifacts/package-checks/result.json`)
pass, including relocation. Bundle `tia-mcp-offline_20261017_085202_8614680d` contains 1406
manifest files. Receipt SHA-256: `200b6bd890498e8cad60e7fd0698183c3ba9ba5e2efe9a853be742e4413c003b`.
Manifest SHA-256: `1a34d4835d961d0e10fac7d5ffdbe5940f0138dcd2c80c3baec51d55ef50fe8f`.
ZIP SHA-256: `d2c75e18c02823405fde4e9a5cd9c97b42fbd130eaafccfdeea0844df1df8420`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The source comparison (`artifacts/encoder-checkpoint-01/final-source-comparison.json`)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
integration receipt (`artifacts/encoder-checkpoint-01/integration.json`) records the
pushed commit, hosted CI results and cleanup.

## Remaining gates

Hashes establish byte consistency, not authenticity of a Siemens project or proof
that the archived native state is complete. Native SDK build, actual archive
publication/locking, retrieval, project preservation, compilation and recovery
acceptance remain open. The SDK-independent publisher tests do not replace them.
Independent recovery-copy endpoint correspondence is still required; provider
identifiers are current-project observations and their project context must not
be stripped to force equality. Exact target-set coverage, TO-03–12 and the full
offline queue remain open. All historical native evidence hashes are preserved.
