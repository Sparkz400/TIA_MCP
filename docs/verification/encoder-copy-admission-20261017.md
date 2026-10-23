# Closed encoder source admission and inspection copies — 17 October 2026

## Repair and workflow

The previous read-only companion validated compiler, endpoint and process evidence
but opened retained project paths without proving their saved bytes were unchanged.
The [previous synthetic reproduction](../../artifacts/encoder-endpoints-01/next-admission-reproduction.json)
changed project bytes and still reached a project-open dispatch.

The producer now publishes `closedCopies` only after all original native/worker/Host
handles prove successful exit. Snapshots cover every regular file and empty
directory in each distinct generated project root. They include the original
pre-edit archive digest, checked against both the apply and restore receipts.
The completion flag remains false until snapshot publication succeeds; file-proof
failure is recorded separately from successful process cleanup.

The companion requires a completed producer report and its previously recorded
SHA-256, not a digest first invented after opening. It validates original calls,
compiler/endpoint proof, archive provenance, source exports and both closed trees
before new Host startup. The report, prerequisites and original trees are pinned
and rechecked before copying, startup, every open attempt and final cleanup.
Links/reparse points, overlapping roots, altered/missing/added files, missing empty
directories, changed archives and incomplete source runs refuse.

Each project is copied into a fresh inspection directory using the existing
file-copy/verification code shared with HMI. Only that verified copy is registered
for opening. Every original open attempt has a corresponding admission record;
known lock retries must pass the file checks again. The returned process must
match the owned session. Native metadata writes to an inspection copy do not
modify the retained original. Final cleanup verifies original source preservation
before publishing success. Nothing deletes native lock files or replays mutations.

The independent auditor validates original closed snapshots and separately checks
all copied-path mappings, file receipts and pre-open call indices. Its
`closedSourceSnapshotsVerified` and `closedCopyAdmissionVerified` fields distinguish
these proofs from endpoint/compile proof. Legacy reports remain diagnostic evidence;
they cannot authorize this current companion without original closed snapshots.

On a Siemens-equipped qualification machine, retain the `Report SHA-256` printed
by the completed producer, then run the companion with that exact digest:

```powershell
python scripts/verify_encoder_preservation.py --evidence <producer-report.json> --evidence-sha256 <recorded-sha256> --host <Host.exe> --worker <Worker.exe> --report <new-companion-report.json>
```

The source report and companion output must remain outside original project trees.
A changed source requires a new legitimate producer run; replacing an expected
hash does not establish native authenticity or repair missing provenance.

## Verification

Twelve new file/admission orchestration methods exercise production validators
with real temporary project trees and fake native processes. Cases cover byte and
empty-directory drift, missing/added files, a real Windows junction, expected report
hashes, source completion/exit proof, original archive identity, changes during
copy/startup/retries/cleanup, foreign returned processes, post-cleanup snapshot
failure, metadata changes confined to inspection copies, and independent-audit
tampering. Existing encoder, HMI, runtime guard and safety tests are also exercised.

Final frozen Git tree: `3fdb593b367823909abd24484038bc81d2bebf59` (2033 files).
[Source manifest](../../artifacts/encoder-copy-01/source-manifest.json) SHA-256:
`302723951825c4d8bd8e47ae5311cb515deccaba36bce0bcfe607c4cffe47db9`.
Source ZIP SHA-256: `d9f2bd341f4ce1d3d1518eaa4b8f6a394e6b5f92bd7787bd9f355624ed6ea133`.

The [aggregate](../../artifacts/encoder-copy-01/source/artifacts/checks/result.json)
passes 41 managed suites, 1,432 Python tests (one unavailable-evidence skip),
24 Explorer model/client checks, contracts/source bindings and all offline MCP
workflows. The general executable passes 224 checks. All 70 encoder Python
methods also pass under -OO, including 33 lifecycle, 5 endpoint and 12 closed-copy admission methods.
Another 96 shared HMI, safety and runtime-guard tests pass.
Run: 2026-10-17T14:25:29.3424324Z to 2026-10-17T14:36:52.7245497Z.
Aggregate receipt SHA-256: `0824aca66506510d145725cd7755b25258c339bda73d1ff480c729441a2517fe`.
[Console evidence](../../artifacts/encoder-copy-01/aggregate-console.log).

All 24 [package stages](../../artifacts/encoder-copy-01/source/artifacts/package-checks/result.json)
pass, including exact ZIP/manifest verification and relocated MCP workflows.
Bundle `tia-mcp-offline_20261017_142552_d06da2e5` contains 1437 manifest files.
Package receipt SHA-256: `416b8367dbd77aaead8a85854d59dedcdfb8c98eb31bd808996e2803d2eacb0d`.
Manifest SHA-256: `78221bede26682ba88f38c07f48cd178593c3477ece46589effc602ed75db6a6`.
ZIP SHA-256: `c9855b6a6e00dd9e822047100bf50c48bcbba42305201abd7ad2a34339e05dfa`.
The portable package disables native workers. Browser tests were not repeated
for this file-admission and qualification-harness repair.

The [source comparison](../../artifacts/encoder-copy-01/final-source-comparison.json)
verifies unchanged frozen source and permits only five completion documents to
differ in the final commit. All 106 unavailable historical evidence binding rows
remain unchanged. The [integration receipt](../../artifacts/encoder-copy-01/integration.json)
records the pushed commit, exact hosted CI outcome and cleanup.

The retained [synthetic scope demonstration](../../artifacts/encoder-copy-01/next-preservation-scope.json)
changes unobserved data before terminal snapshots while selected-encoder and later
copy checks pass. This illustrates missing pre-edit peer-content evidence, not a
native corruption result. Raw binary project-file equality across compilation
would not establish the required engineering semantics.

## Remaining limits

File hashes prove consistency of recorded bytes, not authentication of a native
capture. Tree scans and copy operations are not atomic snapshots or filesystem
leases; changed observations refuse. The selected encoder XML/parameters/endpoint
checks still do not prove preservation of all unrelated engineering content during
the earlier mutation/recovery. Producer immediate reopens retain their original
owned-process workflow; their native timing and copied-project behavior remain
unqualified. Siemens V21 SDK/product/real archive and runtime tests are unexecuted.
No tool, operation-kind or feature counts change; all 106 unavailable historical
bindings remain untouched. The full D13/D14 and 44-workstream goal remains active.
