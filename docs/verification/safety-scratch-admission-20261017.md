# Closed safety scratch admission — 17 October 2026

The direct safety harness previously admitted an existing project by its workspace
folder and TiaMcpScratch_ filename alone. A [before/after reproduction](../../artifacts/safety-scratch-admission-reproduction/comparison.json)
shows an unproven synthetic project reaching project_open before the fix and
refusing before Host construction afterward. No Siemens process was started.

## Implemented boundary

Existing --project reuse now requires --source-report and --source-report-sha256.
The report must be a completed safety run with exact terminal process evidence,
matching retained call/step records, an original successful F-CPU scratch creation
and a closed project snapshot. Reused reports retain their parent report binding;
admission follows at most 16 reports to the original creation. Every source report
is byte-bound and outside the mutable project tree. Duplicate JSON keys, incorrect
hashes, mismatched project/process identities, incomplete lifetimes and unknown
creation contexts refuse. Original creation diagnostics must also pass the shared
compiler-evidence guard.

The existing HMI fixture project_tree and safe_path helpers capture every regular
project file plus empty directories and reject links/reparse points. Two consecutive
snapshots must agree. Admission compares the saved snapshot with current bytes and
rechecks it after Host startup immediately before project_open. The returned open
and inventory must identify the selected project and current process. The new-run
inventory must likewise match its scratch creation response.

After verified terminal cleanup, a successful run publishes closedScratch for
future reuse. Failure to capture it marks the run incomplete. Source-report inputs
are checked again after cleanup; a changed report also forces completed false.
The project itself may change during the explicitly requested run, and receives a
new snapshot only after closure. The prior source report is never rewritten.

This is recorded provenance and byte preservation, not independent authentication:
sourceAuthenticityVerified remains false. No file lock or atomic filesystem/native
open transaction is claimed. A concurrent replacement after the last pre-open check
remains a native integration/concurrency qualification limit.

## Usage

For a new scratch, run the direct harness as before with a new --report path outside
the project directory. A successful run emits closedScratch after cleanup. To reuse
that exact closed project, pass its --project path, the completed --source-report
and its independently selected --source-report-sha256, together with the matching
--artifacts root. Use a fresh output report each time. Historical reports lacking
closedScratch cannot authorize reuse; create a new scratch to establish the receipt.
The archive-retrieval recovery entrypoint remains a separate workflow.

## Verification

Seven new tests cover complete file/directory changes, eight invalid original
sources before Host construction, required CLI arguments, startup replacement,
four returned-owner mismatches, close-time report substitution, snapshot failure,
and fresh creation followed by two successive reuses. All process evidence and
project contents are synthetic. The [focused run](../../artifacts/safety-scratch-admission-targeted.log)
passes 42 parent tests, including the optimized -O/-OO child suites.

Frozen Git tree: `ecfe16d9765eed07365257ca3e9c648af85863da` (2004 files).
[Source manifest](../../artifacts/safety-scratch-admission-01/source-manifest.json) SHA-256:
`7b7358efebe44142a87ab89ff215997f98755648927cbc09d9661f767759a0d1`.
Source ZIP SHA-256: `70848b0988fd4790daaf8115444f44483b0f61bcf44c65382599de621ed10c6c`.

The [full Python aggregate](../../artifacts/safety-scratch-admission-01/source/artifacts/checks/result.json)
passes all 1,391 tests with one unavailable-historical-evidence skip, plus contracts,
source bindings and retained-evidence inventory. The optimized child suites pass
within that aggregate.
Run: 2026-10-17T10:18:35.9329146Z to 2026-10-17T10:19:56.1213645Z.
Receipt SHA-256: `63b317d9d01bdc459d83309c9b164f1ecf594634ddf3e9f3dca9d2654ee3ce34`.
[Console evidence](../../artifacts/safety-scratch-admission-01/python-console.log).

The [source comparison](../../artifacts/safety-scratch-admission-01/final-source-comparison.json)
confirms only completion-documentation changes after the tested snapshot and
preservation of all 106 unavailable historical evidence binding rows. The
[integration receipt](../../artifacts/safety-scratch-admission-01/integration.json) records the
pushed commit, hosted CI outcome and cleanup.

This Python-only increment does not repeat managed, package or browser checks.
The prior [package result](../../artifacts/xml-document-01/source/artifacts/package-checks/result.json)
and [full managed aggregate](../../artifacts/encoder-checkpoint-01/source/artifacts/checks/result.json)
remain evidence for their own snapshots. Historical missing native evidence stays
missing; no receipt or source hash is replaced to claim a pass.

## Remaining work

Native project creation/open/closure, directory layout, permissions, concurrency,
SDK compilation and safety runtime behavior remain unqualified. Full compiler
target coverage and baseline compilation, original archive content correspondence,
D14 reports/F-I/O/activation, TO-03–12 and the remaining 44-workstream queue remain
open. This does not establish functional-safety or release acceptance.
