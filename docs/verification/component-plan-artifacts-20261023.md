# Component planning from pinned artifacts — 23 October 2026

The production library integration adapter now builds its plan through a pure
artifact entry point. Root and caller XML are loaded against the exact retained
path/hash descriptors with the bounded single-snapshot reader. The caller
artifact is captured immediately after export and retained unchanged in the plan.
Source/base and full-oracle policy construction also decode only verified bytes.
The duplicate independent hash read for each version row is removed: its top-level
content hash and artifact descriptor share the same captured hash.

When present, original descriptor byte counts must also match. Strings, nulls,
negative values, fractional values, overflow and counts above 4 MiB refuse.
Older descriptors containing only path/hash remain compatible. The pure XML
builder remains available for isolated model tests; the native adapter uses the
artifact-bound entry point. Combined installation remains disabled.

## Verification

Evidence root: `artifacts/overnight-20261022/component-plan-artifacts/` (the overnight
run continued across local midnight).

- `final-02.log`: all 245 managed component groups pass. Eighteen new plan/artifact
  groups exercise existing/standalone plan parity, changed root/caller bytes,
  missing/contradictory descriptors, duplicate/unmatched root selection, caller
  presence rules, exact size limits, malformed original byte counts, immutable
  returned caller metadata and retained original native FB export compatibility.
- A positive control uses the existing pure XML builder exactly as the former
  adapter could: it accepts a changed interface while carrying an earlier root
  hash. The new artifact entry refuses those changed bytes. This is an offline
  adapter-equivalent control, not an execution of the native adapter.
- `baseline-sdk-build.log`, `fixed-sdk-build.log` and `final-sdk-build.log`: the
  actual worker project compiles against the installed V21 SDK. The final build
  has zero warnings/errors. `verification.json` records exact production source,
  worker executable and referenced SDK hashes. The worker was never executed.
- `python-components-final.log` and `python-library-final.log`: 35 and eight related
  Python tests pass; `contracts.log` retains 261 tools, 56 mutation kinds and
  335 feature entries. Source binding collection/validation logs are retained;
  validation passes with no unavailable evidence and release acceptance false.
- `fixed.log` preserves the earlier 243-group pass before byte-count cases were
  added. `final.log` preserves a compiler failure caused by definite assignment
  of the short-circuited parsed count. Explicit initialization repairs the build;
  the final 245-group run and complete SDK build pass afterward.

The original source files are retained alongside these receipts. No prior failure,
original native export or previous package receipt was replaced. Compilation and
file-only checks do not establish SDK runtime behavior, actual export consistency,
project preservation, installation, compiler results, save/reopen, recovery or
PLC behavior. Those native gates remain deferred for the unattended run.
