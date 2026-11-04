# Classic area workflow changes — 4 November 2026

The outfeed run exposed stale diagnostic progress, an unconstrained advertised
JSON argument shape, a zero-error baseline restriction, composer gaps and costly
repeated source discovery. This change addresses those six workflow areas in the
requested order. It does not modify or reopen the user's project.

Implemented changes:

1. Explicit object schemas for HMI requests/change payloads; final journal state
   is reflected in nonblocking progress without granting replay permission.
2. Completed Classic diagnostic baselines can contain existing errors; final
   diagnostics must be a non-regressing subset with matching owners. Rollback
   requires exact baseline equality. One recognized pre-import path failure can
   retry compilation only after unchanged screen scope is verified. Compile-only
   results separate compiler findings from unknown execution outcomes and avoid
   exporting inconsistent blocks for their identity/save-state fingerprint.
3. Native layer shells, selected formatted captions, typed interface/event
   constants and visibility prototypes extend the existing component composer.
4. Native-root-selected tag tables, bounded immutable parsed-document reuse and
   export/save/archive phase timers reduce preparation work and expose remaining
   cost. The five full screen-verification boundaries are retained.
5. Optional area preparation bundles source/type exports, declarations/calls,
   exact HMI members, missing-member findings and existing control prototypes.
   Array expansion, datatype conversion and signal semantics are not inferred.
6. Inert multilingual HTML previews and layout review findings accompany composed
   XML. A synthetic two-screen 66/56-control fixture exercises native-shaped
   manual/recipe permissions, structured members and multilingual captions.

Validation uses the HMI engineering suite, compile-evidence suite, actual-Host
schema/artifact/progress checks and the repository's broader offline CI. Native
V21 worker compilation is a separate build check, not a native execution test.
The synthetic fixture is described in
[`fixtures/hmi/area-workflow`](../../fixtures/hmi/area-workflow/README.md).

The historical native batch runner remains
[`scripts/qualify_hmi_classic_screen.py`](../../scripts/qualify_hmi_classic_screen.py):
later-member import refusal, reverse restoration, lost response and fresh reopen
still require an independently configured owned RT Advanced archive. That archive
from the earlier workstation run is unavailable in this checkout. No new native
or runtime qualification, skipped-boundary optimization, or measured end-to-end
speed improvement is claimed. Compile-only catalog qualification is marked
unverified for the changed policy; historical evidence remains retained.

The local broad managed run exposed an OPC UA fixture startup failure at its
loopback-only listener assertion. The HMI changes do not alter that fixture; the
failed run is retained at `artifacts/hmi-smoothing-managed/result.json` and is not
counted as a pass. The local Python wrapper also stops at the raw evidence-store
audit because this checkout lacks historical workstation evidence; its contracts
and source-binding checks passed, and Python tests are run separately. The direct
Python run found a stale release matrix and eleven fixture failures caused by the
workstation's short-form temporary path. The matrix was regenerated; all ten
matrix tests and nineteen fixture-route tests pass with the normal repository
temporary-directory setup. Neither broader local limitation is bypassed or counted
as a complete-suite pass. Final acceptance is tracked by the PR checks.
