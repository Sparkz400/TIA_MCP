# Safety recovery source admission — 17 October 2026

The retrieval harness selected the first block export and trusted the archive hash
in a mutable journal. It did not bind either to the selected original safety
change. Nine [before/after reproductions](../../artifacts/safety-source-reproduction/comparison.json)
show mismatched journal identities/previews/projects, archive-plus-journal-hash
replacement, failed baseline inspection, wrong export ownership, unknown apply
outcome, missing terminal proof and missing preservation coverage reaching Host
construction in the previous implementation. All now refuse before construction.

Admission reads bounded source/journal/export metadata, rejects duplicate JSON
keys, and reuses the existing evidence-path checks against links, junctions and
escape from the fixture artifact root. It requires exact original session terminal
evidence and equality between the report's steps and its retained session calls.
Successful signatures and byte-verified XML exports must precede the selected
original preparation and identify its native process, project, PLC and block.
Every declared preservation block needs both original semantic digests and a
matching successful pre-change export; unrelated first exports cannot substitute.

The original preparation, preview, apply request/response and journal must agree
on the change ID, preview hash, payload and project. The archive must be the
selected change's nonempty before.zap21 and match the original returned digest,
not just a later journal value. A known recovery_required error with its recorded
checkpoint details is supported after exact terminal closure. A timeout/lost apply
response, missing details or an uncertain journal cannot authorize retrieval.

Source report, journal, exports and archive receive retained path/size/hash records
at admission. These are checked again immediately before retrieval, before final
qualification and after process cleanup. A mismatch never replaces an expected
digest with the new file's digest. Changes during cleanup force completed false and
retain preservationFailure. Original inputs are read only by this workflow.

Retrieval uses the original response-bound archive digest and checks the returned
path, byte count, digest and owned process ID before further reads. Inspection and
recompilation now use the exact recorded block/PLC paths. Returned native metadata
must identify the recovered project copy and current owned process. Inline XML and
its retained artifact must agree for the selected and supporting blocks.

The result distinguishes recordedSourceBindingVerified from
sourceAuthenticityVerified, which remains false. Agreement among supplied records
is not independent proof of their native origin or truth. This change does not
cryptographically authenticate a source report or invent a new pre-mutation receipt.

## Verification

Six new regression methods cover known success/failure admission, 18 mismatched or
uncertain sources before Host construction, every retained input changing after
admission, startup-time archive substitution, six retrieval/cleanup defects and
duplicate JSON keys. Existing recovery entrypoint checks now use the explicit
original-source fixture, including compilation refusals and terminal cleanup.
The [focused run](../../artifacts/safety-source-targeted.log) passes 35 parent
tests, including the -O/-OO subprocess runner. All processes and archives are
synthetic; no Siemens process starts.

Frozen Git tree: `5d4f1669f1235911182bb75cfe97ff730186eaa7` (2001 files).
[Source manifest](../../artifacts/safety-source-01/source-manifest.json) SHA-256:
`4ea031244272061f113405af26405b311a8635a00a6332ee34a89b889679d6c3`.
Source ZIP SHA-256: `1ed000229e1788cefc1d995208385e992ae4bd66e712c7da1a54c4d692c53108`.

The [full Python aggregate](../../artifacts/safety-source-01/source/artifacts/checks/result.json)
passes all 1,384 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. The optimized child suites pass
within that aggregate.
Run: 2026-10-17T10:11:09.7405062Z to 2026-10-17T10:12:30.1955387Z.
Receipt SHA-256: `30cdbaf23d7572727180f007d3bd69323719c7a06c4d11dcbb90cdb337e4a27c`.
[Console evidence](../../artifacts/safety-source-01/python-console.log).

The [source comparison](../../artifacts/safety-source-01/final-source-comparison.json)
confirms only completion-documentation changes after the tested snapshot and
preservation of all 106 unavailable historical evidence binding rows. The
[integration receipt](../../artifacts/safety-source-01/integration.json) records the
pushed commit, hosted CI outcome and cleanup.

This Python-only increment does not repeat managed, package or browser checks.
The prior [package result](../../artifacts/xml-document-01/source/artifacts/package-checks/result.json)
and [full managed aggregate](../../artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json)
remain evidence for their own source snapshots.

## Remaining gates

The explicit existing-scratch --project route in the direct harness still needs
stronger original-source admission. Independent source authenticity, original
archive content correspondence, complete required compiler-target coverage and
baseline compilation remain open. The native recovery copy must preserve usable
recorded PLC/block paths; a changed namespace currently refuses rather than being
matched by name. Native mapping, SDK, archive/retrieval and interruption behavior
need Siemens qualification. Full D14 reporting/F-I/O/activation scope, TO-03–12 and
the full offline queue remain open; functional-safety acceptance is not established.
